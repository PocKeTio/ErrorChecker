using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Input;
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

        private readonly ScreenManager screenManager = new();
        private readonly bool autoSend;
        private readonly bool helperMode;
        private AppConfig? config;
        private Session? session;
        private ChannelWriter? writer;
        private ChannelReader? reader;
        private CancellationTokenSource? cts;
        private volatile Settings settings = new(Fps: 5, Quality: 60, Screen: -1); // = réglages par défaut du dépanneur
        private volatile bool joined;
        private volatile bool accepted;
        private int keyframeRequested;
        private int sentSeq;
        private int ackedSeq;
        private readonly long[] sentAt = new long[16];
        private volatile int lagMs;
        private long lastInput;

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
            else if (autoSend) SendRequest();   // lancé par l'appli VBA : la demande part sans clic
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e) => SendRequest();

        private void SendRequest()
        {
            var helper = (HelperContact)HelperBox.SelectedItem;
            try
            {
                Session.DeleteStale(config!.SharedFolder, TimeSpan.FromDays(1));
                session = Session.CreateNew(config.SharedFolder);
                writer = new ChannelWriter(session.Folder, Channel.UserToHelper, session.Key, create: true);
                Channel.Create(session.Folder, Channel.HelperToUser);
                reader = new ChannelReader(session.Folder, Channel.HelperToUser, session.Key);
                OutlookMailer.Send(helper.Email, $"Demande d'assistance – {Environment.UserName} ({Environment.MachineName})", MailBody(session));
            }
            catch (Exception ex)
            {
                App.Log.LogError($"Demande d'aide impossible : {ex}");
                CloseSession(sendBye: false);
                MessageBox.Show(this, $"La demande d'aide n'a pas pu être envoyée :\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            App.Log.LogInfo($"Demande d'aide envoyée à {helper.Email} : {session.Folder}");
            ShowSession($"Demande envoyée à {helper}.\nGardez cette fenêtre ouverte : on vous demandera d'accepter la prise en main.\n\nCode : {session.Display}", "Annuler la demande");
            cts = new CancellationTokenSource();
            var token = cts.Token;
            Task.Run(() => ReceiveLoop(token));
            Task.Run(() => PingLoop(token));
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

        private async Task ReceiveLoop(CancellationToken token)
        {
            DateTime? lastReceived = null; // le délai ne court qu'une fois le dépanneur arrivé
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var messages = reader!.Poll();
                    foreach (var msg in messages) await Handle(msg, token);
                    if (messages.Count > 0) lastReceived = DateTime.UtcNow; // après : la boîte d'accord peut rester ouverte longtemps
                    if (DateTime.UtcNow - lastReceived > PeerTimeout)
                    {
                        End("La connexion avec le dépanneur a été perdue.", sendBye: true);
                        return;
                    }
                    // En attente du dépanneur (parfois longtemps) : sondage lent. En session : réactif.
                    await Task.Delay(joined ? 15 : 250, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail(ex, token); }
        }

        private async Task Handle(Msg msg, CancellationToken token)
        {
            switch (msg)
            {
                case Join join when !joined:
                    joined = true;
                    App.Log.LogInfo($"Demande de prise en main : {join.Helper}");
                    if (!await Dispatcher.InvokeAsync(() => AskConsent(join.Helper)))
                    {
                        writer!.Write(new Refuse());
                        End("Vous avez refusé la prise en main.", sendBye: false);
                        return;
                    }
                    var screens = screenManager.GetScreens();
                    int primary = screens.FindIndex(s => s.Screen.Primary);
                    settings = settings with { Screen = primary };
                    writer!.Write(new Accept(primary, screens.Select(s => s.Name).ToArray()));
                    accepted = true;
                    Interlocked.Exchange(ref keyframeRequested, 1);
                    _ = Task.Run(() => CaptureLoop(token));
                    await Dispatcher.InvokeAsync(() => ShowActive(join.Helper));
                    break;
                case Settings s when s != settings:
                    var list = screenManager.GetScreens();
                    screenManager.SelectScreen(s.Screen >= 0 && s.Screen < list.Count ? list[s.Screen] : list.First(i => i.Screen.Primary));
                    settings = s;
                    Interlocked.Exchange(ref keyframeRequested, 1);
                    break;
                case Ack ack:
                    OnAck(ack.Seq);
                    break;
                case Ping ping:
                    writer!.Write(new Pong(ping.Ticks));
                    break;
                case Bye:
                    End("Le dépanneur a terminé l'assistance.", sendBye: false);
                    break;
                case KeyStroke key when accepted:
                    InputInjector.Key(key.Vk, (ModifierKeys)key.Modifiers);
                    Volatile.Write(ref lastInput, Stopwatch.GetTimestamp());
                    break;
                case TextInput text when accepted:
                    InputInjector.Text(text.Text);
                    Volatile.Write(ref lastInput, Stopwatch.GetTimestamp());
                    break;
                case MouseInput mouse when accepted:
                    var bounds = screenManager.GetCurrentScreenBounds();
                    InputInjector.Mouse(mouse.Action, bounds.X + mouse.X, bounds.Y + mouse.Y, mouse.Value);
                    if (mouse.Action != MouseKind.Move) Volatile.Write(ref lastInput, Stopwatch.GetTimestamp());
                    break;
            }
        }

        // Retard capture -> affichage, lissé, renvoyé au dépanneur dans les images suivantes.
        private void OnAck(int seq)
        {
            if (seq <= Volatile.Read(ref ackedSeq)) return;
            int sample = (int)ElapsedMs(sentAt[seq % sentAt.Length]);
            lagMs = lagMs == 0 ? sample : (lagMs * 3 + sample) / 4;
            Volatile.Write(ref ackedSeq, seq);
        }

        private static double ElapsedMs(long since) => (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;

        private bool AskConsent(string helper)
        {
            Topmost = true;
            Activate();
            var answer = MessageBox.Show(this,
                $"{helper} demande à voir votre écran et à utiliser votre souris et votre clavier.\n\n" +
                "Vous pourrez arrêter à tout moment avec le bouton « Terminer l'assistance ».\n\nAccepter ?",
                "Demande de prise en main", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            return answer == MessageBoxResult.Yes;
        }

        // Cadence : au plus Fps images/s, jamais plus de MaxFramesInFlight images non acquittées
        // (si le réseau ou le dépanneur ne suit pas, la cadence baisse d'elle-même), et une capture
        // rapprochée juste après une action du dépanneur pour qu'il voie l'effet de son clic sans attendre.
        private async Task CaptureLoop(CancellationToken token)
        {
            var encoder = new ScreenEncoder();
            long lastCapture = 0;
            bool blocked = false;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(5, token);
                    var s = settings;
                    if (Volatile.Read(ref sentSeq) - Volatile.Read(ref ackedSeq) >= MaxFramesInFlight) continue;
                    double interval = 1000.0 / Math.Clamp(s.Fps, 1, 30);
                    if (ElapsedMs(Volatile.Read(ref lastInput)) < InputBoostMs) interval = Math.Min(interval, InputBoostIntervalMs);
                    if (ElapsedMs(lastCapture) < interval) continue;
                    lastCapture = Stopwatch.GetTimestamp();

                    if (Interlocked.Exchange(ref keyframeRequested, 0) == 1) encoder.Reset();
                    ScreenFrame? frame;
                    try
                    {
                        var bounds = screenManager.GetCurrentScreenBounds();
                        var cursor = System.Windows.Forms.Cursor.Position;
                        using var screenshot = screenManager.CaptureScreen();
                        frame = encoder.Encode(screenshot, new System.Drawing.Point(cursor.X - bounds.X, cursor.Y - bounds.Y), s.Quality, sentSeq + 1, lagMs);
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
                    sentAt[frame.Seq % sentAt.Length] = Stopwatch.GetTimestamp();
                    Volatile.Write(ref sentSeq, frame.Seq);
                    writer!.Write(frame);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail(ex, token); }
        }

        // Signe de vie, y compris avant l'arrivée du dépanneur (il sait ainsi que l'application tourne).
        private async Task PingLoop(CancellationToken token)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(2000, token);
                    writer!.Write(new Ping(0));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail(ex, token); }
        }

        private void Fail(Exception ex, CancellationToken token)
        {
            if (token.IsCancellationRequested) return; // session déjà fermée : erreur attendue
            App.Log.LogError($"Erreur de session : {ex}");
            End($"L'assistance s'est arrêtée sur une erreur :\n{ex.Message}", sendBye: true);
        }

        private void End(string reason, bool sendBye) => Dispatcher.InvokeAsync(() => EndSession(reason, sendBye));

        private void EndSession(string? reason, bool sendBye)
        {
            if (session == null) return; // déjà terminée
            CloseSession(sendBye);
            Topmost = false;
            IdlePanel.Visibility = Visibility.Visible;
            SessionPanel.Visibility = Visibility.Collapsed;
            if (reason != null) MessageBox.Show(this, reason, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void CloseSession(bool sendBye)
        {
            joined = false;
            accepted = false;
            cts?.Cancel();
            InputInjector.ReleaseMouseButtons();
            try
            {
                if (sendBye) writer?.Write(new Bye("L'utilisateur a terminé l'assistance."));
            }
            catch (Exception ex) { App.Log.LogWarning($"Fin de session non transmise : {ex.Message}"); }
            writer?.Dispose();
            reader?.Dispose();
            if (session != null)
            {
                var folder = session.Folder;
                Task.Run(() => { try { Directory.Delete(folder, true); } catch (Exception) { } }); // au mieux ; sinon nettoyé plus tard
            }
            session = null;
            writer = null;
            reader = null;
            cts = null;
            sentSeq = ackedSeq = 0;
            lagMs = 0;
        }

        private void ShowSession(string status, string button)
        {
            IdlePanel.Visibility = Visibility.Collapsed;
            SessionPanel.Visibility = Visibility.Visible;
            SessionStatus.Text = status;
            StopButton.Content = button;
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

        private void StopButton_Click(object sender, RoutedEventArgs e) => EndSession(null, sendBye: true);

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

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (session != null) CloseSession(sendBye: true);
        }
    }
}
