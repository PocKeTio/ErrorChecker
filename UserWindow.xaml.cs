using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ErrorChecker.Capture;
using ErrorChecker.Core;
using ErrorChecker.Input;
using ErrorChecker.Mail;

namespace ErrorChecker
{
    // Côté utilisateur : choisir à qui demander de l'aide, un accord explicite, un bouton pour arrêter.
    // Tous les réglages (écran, cadence, qualité) sont pilotés par le dépanneur.
    public partial class UserWindow : Window
    {
        private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(20);
        private const int MaxFramesInFlight = 2;    // au-delà, on attend l'acquittement : le retard ne s'accumule jamais
        private const int InputBoostMs = 500;       // après une action du dépanneur, réponse visible au plus vite
        private const int InputBoostIntervalMs = 100;

        // Tout ce qui ne vaut que pour une session. Une boucle encore en cours d'une session terminée
        // ne touche que son propre objet : elle ne peut pas perturber la demande suivante.
        private sealed class Live
        {
            public Live(Session session, ChannelWriter writer, ChannelReader reader) => (Session, Writer, Reader) = (session, writer, reader);
            public readonly Session Session;
            public readonly ChannelWriter Writer;
            public readonly ChannelReader Reader;
            public readonly CancellationTokenSource Cts = new();
            public readonly Outbox Outbox = new();
            public readonly long[] SentAt = new long[16];
            public volatile Settings Settings = new(Fps: 5, Quality: 60, Screen: -1, ReduceColors: false); // = réglages par défaut du dépanneur
            public volatile bool Joined;
            public volatile bool Accepted;
            public volatile int LagMs;
            public int KeyframeRequested;
            public int SentSeq;
            public int AckedSeq;
            public long LastInput;
        }

        private readonly ScreenManager screenManager = new();
        private readonly bool autoSend;
        private readonly bool helperMode;
        private AppConfig? config;
        private Live? live;

        public UserWindow(bool autoSend = false, string? description = null, bool helperMode = false)
        {
            InitializeComponent();
            this.autoSend = autoSend;
            this.helperMode = helperMode;
            ProblemText.Text = description ?? "";
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                config = AppConfig.Load(AppContext.BaseDirectory);
                HelperBox.ItemsSource = config.Helpers;
                HelperBox.SelectedIndex = 0;
                HelperPanel.Visibility = config.Helpers.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                App.Log.LogError($"Configuration : {ex}");
                HelperPanel.Visibility = Visibility.Collapsed;
                HelpButton.IsEnabled = false;
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (helperMode)
            {
                HelperExpander.IsExpanded = true;
                CodeText.Focus();
            }
            // Lancé par l'appli VBA : la demande part sans clic, sauf s'il faut choisir le destinataire.
            else if (autoSend && config.Helpers.Count == 1) SendRequest();
            else if (autoSend) HelperBox.Focus();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e) => SendRequest();

        private async void SendRequest()
        {
            var helper = (HelperContact)HelperBox.SelectedItem;
            Live s;
            try
            {
                Session.DeleteStale(config!.SharedFolder, TimeSpan.FromDays(1));
                var session = Session.CreateNew(config.SharedFolder);
                var writer = new ChannelWriter(session.Folder, Channel.UserToHelper, session.Key, create: true);
                Channel.Create(session.Folder, Channel.HelperToUser);
                s = new Live(session, writer, new ChannelReader(session.Folder, Channel.HelperToUser, session.Key));
            }
            catch (Exception ex)
            {
                App.Log.LogError($"Session impossible à créer : {ex}");
                MessageBox.Show(this, $"La demande d'aide n'a pas pu être créée (dossier partagé inaccessible ?) :\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            live = s;
            ShowSession("Envoi de la demande…", "Annuler la demande", s.Session.Display);
            var token = s.Cts.Token;
            _ = Task.Run(() => ReceiveLoop(s, token));
            _ = Task.Run(() => SendLoop(s, token));

            // Si le mail ne part pas, la session reste ouverte : le code peut être donné par téléphone ou Teams.
            string status;
            try
            {
                var subject = $"Demande d'assistance – {Environment.UserName} ({Environment.MachineName})";
                var body = MailBody(s.Session);
                var result = await Task.Run(() => OutlookMailer.Send(helper.Email, subject, body));
                App.Log.LogInfo($"Demande d'aide à {helper.Email} ({result}) : {s.Session.Folder}");
                status = result switch
                {
                    MailResult.Sent => $"Demande envoyée à {helper}.",
                    MailResult.ShownToUser => "Vérifiez le mail ouvert dans Outlook et cliquez sur « Envoyer ».",
                    _ => "Le mail attend dans la boîte d'envoi d'Outlook : ouvrez Outlook pour qu'il parte, ou donnez ce code au support.",
                };
            }
            catch (Exception ex)
            {
                App.Log.LogError($"Mail non envoyé : {ex}");
                status = $"Le mail n'a pas pu partir ({ex.Message}).\nDonnez ce code au support (téléphone, Teams) :";
            }
            if (live == s && !s.Joined)
                SessionStatus.Text = status + "\nGardez cette fenêtre ouverte : on vous demandera d'accepter la prise en main.";
        }

        private string MailBody(Session s)
        {
            static string Html(string text) => WebUtility.HtmlEncode(text);
            var problem = string.IsNullOrWhiteSpace(ProblemText.Text) ? "(non précisé)" : ProblemText.Text.Trim();
            return $"<p><b>{Html(Environment.UserName)}</b> (poste {Html(Environment.MachineName)}) demande de l'aide.</p>"
                 + $"<p>Problème : {Html(problem).Replace("\n", "<br>")}</p>"
                 + $"<p style=\"font-size:20pt;font-family:Consolas,monospace\"><a href=\"{Html(s.Link)}\">{Html(s.Display)}</a></p>"
                 + "<p>Cliquer sur le code ouvre ErrorChecker. Sinon : lancer ErrorChecker, « Je suis le dépanneur », taper le code. "
                 + "L'utilisateur devra accepter la prise en main.</p>";
        }

        private async Task ReceiveLoop(Live s, CancellationToken token)
        {
            DateTime? lastReceived = null; // le délai ne court qu'une fois le dépanneur arrivé
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var messages = s.Reader.Poll();
                    foreach (var msg in messages) await Handle(s, msg, token);
                    if (messages.Count > 0) lastReceived = DateTime.UtcNow; // après : la boîte d'accord peut rester ouverte longtemps
                    if (DateTime.UtcNow - lastReceived > PeerTimeout)
                    {
                        End(s, "La connexion avec le dépanneur a été perdue.", sendBye: true);
                        return;
                    }
                    // En attente du dépanneur (parfois longtemps) : sondage lent. En session : réactif.
                    await Task.Delay(s.Joined ? 15 : 250, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail(s, ex, token); }
        }

        // Réponses et signes de vie partent par la file d'envoi : la réception (donc les clics et touches
        // du dépanneur) n'attend jamais derrière l'écriture d'une grosse image.
        private async Task SendLoop(Live s, CancellationToken token)
        {
            try
            {
                await s.Outbox.RunAsync(s.Writer, () => new Ping(0), token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail(s, ex, token); }
        }

        private async Task Handle(Live s, Msg msg, CancellationToken token)
        {
            switch (msg)
            {
                case Join join when !s.Joined:
                    s.Joined = true;
                    App.Log.LogInfo($"Demande de prise en main : {join.Helper}");
                    if (!await Dispatcher.InvokeAsync(() => AskConsent(join.Helper)))
                    {
                        s.Writer.Write(new Refuse());
                        End(s, "Vous avez refusé la prise en main.", sendBye: false);
                        return;
                    }
                    var screens = screenManager.GetScreens();
                    int primary = screens.FindIndex(i => i.Screen.Primary);
                    s.Settings = s.Settings with { Screen = primary };
                    s.Writer.Write(new Accept(primary, screens.Select(i => i.Name).ToArray()));  // avant la première image
                    s.Accepted = true;
                    _ = Task.Run(() => CaptureLoop(s, token));
                    await Dispatcher.InvokeAsync(() => ShowActive(join.Helper));
                    break;
                case Settings settings when settings != s.Settings:
                    // Seul un changement d'écran demande une image complète ; cadence et qualité s'appliquent
                    // aux images suivantes (les zones floues seront de toute façon affinées).
                    if (settings.Screen != s.Settings.Screen) Interlocked.Exchange(ref s.KeyframeRequested, 1);
                    s.Settings = settings;
                    break;
                case Ack ack:
                    OnAck(s, ack.Seq);
                    break;
                case Ping ping:
                    s.Outbox.Post(new Pong(ping.Ticks));
                    break;
                case Bye:
                    End(s, "Le dépanneur a terminé l'assistance.", sendBye: false);
                    break;
                case KeyStroke key when s.Accepted:
                    InputInjector.Key(key.Vk, (ModifierKeys)key.Modifiers);
                    Volatile.Write(ref s.LastInput, Stopwatch.GetTimestamp());
                    break;
                case TextInput text when s.Accepted:
                    InputInjector.Text(text.Text);
                    Volatile.Write(ref s.LastInput, Stopwatch.GetTimestamp());
                    break;
                case MouseInput mouse when s.Accepted:
                    var bounds = screenManager.GetCurrentScreenBounds();
                    InputInjector.Mouse(mouse.Action, bounds.X + mouse.X, bounds.Y + mouse.Y, mouse.Value, (ModifierKeys)mouse.Modifiers);
                    if (mouse.Action != MouseKind.Move) Volatile.Write(ref s.LastInput, Stopwatch.GetTimestamp());
                    break;
            }
        }

        // Retard capture -> affichage, lissé, renvoyé au dépanneur dans les images suivantes.
        private static void OnAck(Live s, int seq)
        {
            if (seq <= Volatile.Read(ref s.AckedSeq)) return;
            int sample = (int)ElapsedMs(s.SentAt[seq % s.SentAt.Length]);
            s.LagMs = s.LagMs == 0 ? sample : (s.LagMs * 3 + sample) / 4;
            Volatile.Write(ref s.AckedSeq, seq);
        }

        private static double ElapsedMs(long since) => (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;

        private bool AskConsent(string helper)
        {
            var previous = GetForegroundWindow();   // l'appli où travaillait l'utilisateur (Excel...)
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;  // sinon la question resterait invisible
            Topmost = true;
            Activate();
            var answer = MessageBox.Show(this,
                $"{helper} demande à voir votre écran et à utiliser votre souris et votre clavier.\n\n" +
                "Vous pourrez arrêter à tout moment avec le bouton « Terminer l'assistance ».\n\nAccepter ?",
                "Demande de prise en main", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return false;

            // Les touches du dépanneur vont à la fenêtre active : on rend la main à l'appli de l'utilisateur,
            // et cette fenêtre ne pourra plus devenir active (sinon F5 ou Alt+F4 tomberaient sur elle).
            SetNoActivate(true);
            var own = new WindowInteropHelper(this).Handle;
            if (previous != IntPtr.Zero && previous != own) SetForegroundWindow(previous);
            return true;
        }

        // Cadence : au plus Fps images/s, jamais plus de MaxFramesInFlight images non acquittées
        // (si le réseau ou le dépanneur ne suit pas, la cadence baisse d'elle-même), et une capture
        // rapprochée juste après une action du dépanneur pour qu'il voie l'effet de son clic sans attendre.
        private async Task CaptureLoop(Live s, CancellationToken token)
        {
            var encoder = new ScreenEncoder();
            long lastCapture = 0;
            bool blocked = false;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(5, token);
                    var settings = s.Settings;
                    if (Volatile.Read(ref s.SentSeq) - Volatile.Read(ref s.AckedSeq) >= MaxFramesInFlight) continue;
                    double interval = 1000.0 / Math.Clamp(settings.Fps, 1, 30);
                    if (ElapsedMs(Volatile.Read(ref s.LastInput)) < InputBoostMs) interval = Math.Min(interval, InputBoostIntervalMs);
                    if (ElapsedMs(lastCapture) < interval) continue;
                    lastCapture = Stopwatch.GetTimestamp();

                    if (Interlocked.Exchange(ref s.KeyframeRequested, 0) == 1) encoder.Reset();
                    ScreenFrame? frame;
                    try
                    {
                        // Écrans relus à chaque image : résolution ou écran débranché pris en compte.
                        var screens = screenManager.GetScreens();
                        screenManager.SelectScreen(settings.Screen >= 0 && settings.Screen < screens.Count ? screens[settings.Screen] : screens.First(i => i.Screen.Primary));
                        var bounds = screenManager.GetCurrentScreenBounds();
                        var cursor = System.Windows.Forms.Cursor.Position;
                        using var screenshot = screenManager.CaptureScreen();
                        frame = encoder.Encode(screenshot, new System.Drawing.Point(cursor.X - bounds.X, cursor.Y - bounds.Y), settings.Quality, settings.ReduceColors, s.SentSeq + 1, s.LagMs);
                        blocked = false;
                    }
                    catch (Win32Exception ex)
                    {
                        // Session verrouillée ou écran UAC : capture impossible pour l'instant, on réessaie.
                        if (!blocked) App.Log.LogWarning($"Capture impossible : {ex.Message}");
                        blocked = true;
                        await Task.Delay(1000, token);
                        continue;
                    }
                    if (frame == null) continue;
                    s.SentAt[frame.Seq % s.SentAt.Length] = Stopwatch.GetTimestamp();
                    Volatile.Write(ref s.SentSeq, frame.Seq);
                    s.Writer.Write(frame);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail(s, ex, token); }
        }

        private void Fail(Live s, Exception ex, CancellationToken token)
        {
            if (token.IsCancellationRequested) return; // session déjà fermée : erreur attendue
            App.Log.LogError($"Erreur de session : {ex}");
            End(s, $"L'assistance s'est arrêtée sur une erreur :\n{ex.Message}", sendBye: true);
        }

        private void End(Live s, string reason, bool sendBye) => Dispatcher.InvokeAsync(() => EndSession(s, reason, sendBye));

        private void EndSession(Live s, string? reason, bool sendBye)
        {
            if (live != s) return; // déjà terminée (ou c'est une ancienne session)
            CloseSession(sendBye);
            SetNoActivate(false);
            Topmost = false;
            IdlePanel.Visibility = Visibility.Visible;
            SessionPanel.Visibility = Visibility.Collapsed;
            if (reason != null) MessageBox.Show(this, reason, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void CloseSession(bool sendBye)
        {
            var s = live;
            if (s == null) return;
            live = null;
            s.Accepted = false;
            s.Cts.Cancel();
            InputInjector.ReleaseMouseButtons();
            try
            {
                if (sendBye) s.Writer.Write(new Bye("L'utilisateur a terminé l'assistance."));
            }
            catch (Exception ex) { App.Log.LogWarning($"Fin de session non transmise : {ex.Message}"); }
            s.Writer.Dispose();
            s.Reader.Dispose();
            var folder = s.Session.Folder;
            Task.Run(() => { try { Directory.Delete(folder, true); } catch (Exception) { } }); // au mieux ; sinon nettoyé plus tard
        }

        private void ShowSession(string status, string button, string? code = null)
        {
            IdlePanel.Visibility = Visibility.Collapsed;
            SessionPanel.Visibility = Visibility.Visible;
            SessionStatus.Text = status;
            StopButton.Content = button;
            CodeDisplay.Text = code ?? "";
            CodeDisplay.Visibility = code == null ? Visibility.Collapsed : Visibility.Visible;
        }

        // Pendant l'assistance : fenêtre réduite, toujours visible, dans le coin de l'écran.
        private void ShowActive(string helper)
        {
            ShowSession($"Assistance en cours avec {helper}.\nIl voit votre écran et peut utiliser la souris et le clavier.", "Terminer l'assistance");
            Topmost = true;
            UpdateLayout();
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 12;
            Top = area.Bottom - ActualHeight - 12;
        }

        private void SetNoActivate(bool enabled)
        {
            var handle = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, enabled ? style | WS_EX_NOACTIVATE : style & ~WS_EX_NOACTIVATE);
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (live != null) EndSession(live, null, sendBye: true);
        }

        // Code mal tapé ou session introuvable : on revient ici pour corriger, sans quitter l'appli.
        private void JoinButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Session.Normalize(CodeText.Text);
            }
            catch (FormatException ex)
            {
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var helperWindow = new HelperWindow(CodeText.Text);
            helperWindow.Closed += (_, _) => { if (helperWindow.Joined) Close(); else Show(); };
            Hide();
            helperWindow.Show();
        }

        private void Window_Closing(object? sender, CancelEventArgs e) => CloseSession(sendBye: true);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
