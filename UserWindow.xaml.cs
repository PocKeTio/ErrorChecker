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
    // Côté utilisateur : un bouton pour demander de l'aide, un accord explicite, un bouton pour arrêter.
    // Tous les réglages (écran, cadence, qualité) sont pilotés par le dépanneur.
    public partial class UserWindow : Window
    {
        private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(20);

        private readonly ScreenManager screenManager = new();
        private SessionLink? session;
        private ChannelWriter? writer;
        private ChannelReader? reader;
        private CancellationTokenSource? cts;
        private volatile Settings settings = new(Fps: 5, Quality: 70, Screen: -1); // = réglages par défaut du dépanneur
        private volatile bool accepted;
        private int keyframeRequested;

        public UserWindow()
        {
            InitializeComponent();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var config = AppConfig.Load(AppContext.BaseDirectory);
                SessionLink.DeleteStale(config.SharedFolder, TimeSpan.FromDays(1));
                session = SessionLink.CreateNew(config.SharedFolder);
                writer = new ChannelWriter(session.Folder, Channel.UserToHelper, session.Key, create: true);
                Channel.Create(session.Folder, Channel.HelperToUser);
                reader = new ChannelReader(session.Folder, Channel.HelperToUser, session.Key);
                OutlookMailer.Send(config.SupportEmail, $"Demande d'assistance – {Environment.UserName} ({Environment.MachineName})", MailBody(session));
            }
            catch (Exception ex)
            {
                App.Log.LogError($"Demande d'aide impossible : {ex}");
                CloseSession(sendBye: false);
                MessageBox.Show(this, $"La demande d'aide n'a pas pu être envoyée :\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            App.Log.LogInfo($"Demande d'aide envoyée : {session.Folder}");
            ShowSession("Demande envoyée au support.\nGardez cette fenêtre ouverte : on vous demandera d'accepter la prise en main.", "Annuler la demande");
            cts = new CancellationTokenSource();
            var token = cts.Token;
            Task.Run(() => ReceiveLoop(token));
            Task.Run(() => PingLoop(token));
        }

        private string MailBody(SessionLink link)
        {
            static string Html(string s) => WebUtility.HtmlEncode(s);
            var problem = string.IsNullOrWhiteSpace(ProblemText.Text) ? "(non précisé)" : ProblemText.Text.Trim();
            var url = link.ToString();
            return $"<p><b>{Html(Environment.UserName)}</b> (poste {Html(Environment.MachineName)}) demande de l'aide.</p>"
                 + $"<p>Problème : {Html(problem).Replace("\n", "<br>")}</p>"
                 + $"<p><a href=\"{Html(url)}\">Prendre la main</a> (l'utilisateur devra accepter).</p>"
                 + $"<p style=\"color:gray\">Si le lien ne s'ouvre pas : lancer ErrorChecker, « Je suis le dépanneur », coller :<br>{Html(url)}</p>";
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
                    await Task.Delay(30, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail(ex, token); }
        }

        private async Task Handle(Msg msg, CancellationToken token)
        {
            switch (msg)
            {
                case Join join when !accepted:
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
                case Ping ping:
                    writer!.Write(new Pong(ping.Ticks));
                    break;
                case Bye:
                    End("Le dépanneur a terminé l'assistance.", sendBye: false);
                    break;
                case KeyStroke key when accepted:
                    InputInjector.Key(key.Vk, (ModifierKeys)key.Modifiers);
                    break;
                case TextInput text when accepted:
                    InputInjector.Text(text.Text);
                    break;
                case MouseInput mouse when accepted:
                    var bounds = screenManager.GetCurrentScreenBounds();
                    InputInjector.Mouse(mouse.Action, bounds.X + mouse.X, bounds.Y + mouse.Y, mouse.Value);
                    break;
            }
        }

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

        private async Task CaptureLoop(CancellationToken token)
        {
            var encoder = new ScreenEncoder();
            bool blocked = false;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var started = Stopwatch.StartNew();
                    var s = settings;
                    if (Interlocked.Exchange(ref keyframeRequested, 0) == 1) encoder.Reset();
                    ScreenFrame? frame;
                    try
                    {
                        var bounds = screenManager.GetCurrentScreenBounds();
                        var cursor = System.Windows.Forms.Cursor.Position;
                        using var screenshot = screenManager.CaptureScreen();
                        frame = encoder.Encode(screenshot, new System.Drawing.Point(cursor.X - bounds.X, cursor.Y - bounds.Y), s.Quality);
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
                    // Écriture bloquante : si le réseau est lent, la cadence baisse d'elle-même.
                    if (frame != null) writer!.Write(frame);
                    await Task.Delay(Math.Max(10, 1000 / Math.Clamp(s.Fps, 1, 30) - (int)started.ElapsedMilliseconds), token);
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

        private void JoinButton_Click(object sender, RoutedEventArgs e)
        {
            new HelperWindow(LinkText.Text).Show();
            Close();
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (session != null) CloseSession(sendBye: true);
        }
    }
}
