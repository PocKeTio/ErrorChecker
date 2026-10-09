using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ErrorChecker.Core;

namespace ErrorChecker
{
    // Côté dépanneur : ouvert par le lien du mail (ou le code tapé). Affiche l'écran de l'utilisateur
    // et lui envoie souris et clavier une fois qu'il a accepté.
    public partial class HelperWindow : Window
    {
        private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(20);

        private readonly string code;
        private readonly CancellationTokenSource cts = new();
        private readonly object pendingLock = new();
        private readonly SemaphoreSlim pendingSignal = new(0);
        private readonly DispatcherTimer statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly HashSet<MouseButton> pressed = new();
        private Session? session;
        private ChannelWriter? writer;
        private ChannelReader? reader;
        private List<Msg> pending = new();
        private WriteableBitmap? bitmap;
        private readonly FrameDecoder decoder = new(DecodeImage); // copie locale de l'écran de l'utilisateur
        private Point remoteCursor = new(-1, -1);
        private (int X, int Y) lastPoint = (-1, -1);
        private bool accepted;
        private bool ended;

        public bool Joined { get; private set; }
        private bool loadingScreens;
        private int framesReceived;
        private long bytesReceived;
        private double roundTripMs = -1;
        private int lagMs = -1;

        public HelperWindow(string code)
        {
            InitializeComponent();
            this.code = code;
            Loaded += (_, _) => Start();
            statsTimer.Tick += (_, _) => ShowStats();
        }

        private void Start()
        {
            try
            {
                session = Session.Open(AppConfig.Load(AppContext.BaseDirectory).SharedFolder, code);
                reader = new ChannelReader(session.Folder, Channel.UserToHelper, session.Key);
                writer = new ChannelWriter(session.Folder, Channel.HelperToUser, session.Key, create: false);
                writer.Write(new Join($"{Environment.UserName} ({Environment.MachineName})"));
            }
            catch (Exception ex)
            {
                App.Log.LogError($"Connexion impossible : {ex}");
                reader?.Dispose();
                writer?.Dispose();
                var message = ex switch
                {
                    FileNotFoundException or DirectoryNotFoundException => "Session introuvable : l'utilisateur a annulé sa demande ou l'assistance est terminée.",
                    IOException when ex.HResult == unchecked((int)0x80070020) => "Un autre dépanneur a déjà rejoint cette session.", // violation de partage
                    _ => ex.Message
                };
                MessageBox.Show(this, $"Impossible de rejoindre la session :\n{message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                ended = true;
                Close();
                return;
            }

            Joined = true;
            App.Log.LogInfo($"Session rejointe : {session.Folder}");
            Title += " – " + session.Display;
            StatusText.Text = "En attente de l'accord de l'utilisateur…";
            Task.Run(() => ReceiveLoop(cts.Token));
            Task.Run(() => SendLoop(cts.Token));
            statsTimer.Start();
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            var lastReceived = DateTime.UtcNow;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var messages = reader!.Poll();
                    if (messages.Count > 0) lastReceived = DateTime.UtcNow;
                    foreach (var msg in messages) Handle(msg);
                    if (DateTime.UtcNow - lastReceived > PeerTimeout)
                    {
                        End("L'application de l'utilisateur ne répond plus.", sendBye: true);
                        return;
                    }
                    await Task.Delay(30, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail(ex, token); }
        }

        private void Handle(Msg msg)
        {
            switch (msg)
            {
                case Accept accept:
                    Dispatcher.InvokeAsync(() => OnAccepted(accept));
                    break;
                case Refuse:
                    End("L'utilisateur a refusé la prise en main.", sendBye: false);
                    break;
                case Bye bye:
                    End(bye.Reason, sendBye: false);
                    break;
                case Pong pong:
                    roundTripMs = (Stopwatch.GetTimestamp() - pong.Ticks) * 1000.0 / Stopwatch.Frequency;
                    break;
                case ScreenFrame frame:
                    ApplyFrame(frame);
                    break;
            }
        }

        private void OnAccepted(Accept accept)
        {
            loadingScreens = true;
            ScreenBox.ItemsSource = accept.Screens;
            ScreenBox.SelectedIndex = Math.Max(accept.Primary, 0);
            loadingScreens = false;
            accepted = true;
            SendSettings();
            StatusText.Text = "Connecté. Cliquez dans l'image pour prendre la main (clavier transmis quand le cadre est rouge).";
            Viewer.Focus();
        }

        // Défilements puis zones décodées (en parallèle) dans la copie locale, hors du thread d'affichage ;
        // l'affichage ne fait que recopier les rectangles modifiés. Acquittement une fois l'image affichée.
        private void ApplyFrame(ScreenFrame frame)
        {
            bool resized = decoder.Apply(frame);
            var target = decoder.Screen!;
            int stride = frame.Width * 4;
            Interlocked.Increment(ref framesReceived);
            Interlocked.Add(ref bytesReceived, frame.Patches.Sum(p => (long)p.Data.Length));

            Dispatcher.Invoke(() =>
            {
                if (resized || bitmap == null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
                {
                    // DPI de cet écran : en « taille réelle », un pixel de l'utilisateur = un pixel ici.
                    var dpi = VisualTreeHelper.GetDpi(this);
                    bitmap = new WriteableBitmap(frame.Width, frame.Height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Bgr32, null);
                    ScreenImage.Source = bitmap;
                    bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), target, stride, 0, 0);
                }
                else
                {
                    foreach (var m in frame.Moves) bitmap.WritePixels(new Int32Rect(m.X, m.Y, m.W, m.H), target, stride, m.X, m.Y);
                    foreach (var p in frame.Patches) bitmap.WritePixels(new Int32Rect(p.X, p.Y, p.W, p.H), target, stride, p.X, p.Y);
                }
                remoteCursor = new Point(frame.CursorX, frame.CursorY);
                UpdateCursor();
            });
            lagMs = frame.LagMs;
            Enqueue(new Ack(frame.Seq));
        }

        private static void DecodeImage(Patch patch, byte[] screen, int stride)
        {
            using var ms = new MemoryStream(patch.Data);
            var image = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var pixels = new byte[patch.W * patch.H * 4];
            new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0).CopyPixels(pixels, patch.W * 4, 0);
            for (int y = 0; y < patch.H; y++)
                Buffer.BlockCopy(pixels, y * patch.W * 4, screen, (patch.Y + y) * stride + patch.X * 4, patch.W * 4);
        }

        private void UpdateCursor()
        {
            bool visible = bitmap != null && ScreenImage.ActualWidth > 0
                && remoteCursor.X >= 0 && remoteCursor.Y >= 0 && remoteCursor.X < bitmap.PixelWidth && remoteCursor.Y < bitmap.PixelHeight;
            RemoteCursor.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) return;
            Canvas.SetLeft(RemoteCursor, remoteCursor.X * ScreenImage.ActualWidth / bitmap!.PixelWidth - RemoteCursor.Width / 2);
            Canvas.SetTop(RemoteCursor, remoteCursor.Y * ScreenImage.ActualHeight / bitmap.PixelHeight - RemoteCursor.Height / 2);
        }

        private void ScreenImage_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCursor();

        private void ShowStats()
        {
            int frames = Interlocked.Exchange(ref framesReceived, 0);
            long bytes = Interlocked.Exchange(ref bytesReceived, 0);
            StatsText.Text = $"{frames} img/s · {bytes / 1024} Ko/s"
                + (lagMs >= 0 ? $" · retard image {lagMs} ms" : "")
                + (roundTripMs >= 0 ? $" · aller-retour {roundTripMs:F0} ms" : "");
        }

        // Envoi dès qu'il y a quelque chose ; ce qui arrive pendant une écriture part groupé dans la suivante.
        private async Task SendLoop(CancellationToken token)
        {
            var lastPing = DateTime.MinValue;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (DateTime.UtcNow - lastPing > TimeSpan.FromSeconds(2))
                    {
                        Enqueue(new Ping(Stopwatch.GetTimestamp()));
                        lastPing = DateTime.UtcNow;
                    }
                    List<Msg> batch;
                    lock (pendingLock)
                    {
                        batch = pending;
                        pending = new List<Msg>();
                    }
                    writer!.Write(batch);
                    await pendingSignal.WaitAsync(TimeSpan.FromSeconds(1), token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail(ex, token); }
        }

        private void Enqueue(Msg msg)
        {
            lock (pendingLock)
            {
                // Mouvements de souris : seule la dernière position en attente compte.
                if (msg is MouseInput { Action: MouseKind.Move } && pending.Count > 0 && pending[^1] is MouseInput { Action: MouseKind.Move })
                    pending[^1] = msg;
                else
                    pending.Add(msg);
            }
            if (pendingSignal.CurrentCount == 0) pendingSignal.Release();
        }

        private void Settings_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (accepted && !loadingScreens) SendSettings();
        }

        private void SendSettings()
        {
            int fps = int.Parse((string)((ComboBoxItem)FpsBox.SelectedItem).Content);
            int quality = int.Parse((string)((ComboBoxItem)QualityBox.SelectedItem).Tag);
            Enqueue(new Settings(fps, quality, ScreenBox.SelectedIndex));
        }

        private void RealSize_Changed(object sender, RoutedEventArgs e)
        {
            bool realSize = RealSizeBox.IsChecked == true;
            ScreenImage.Stretch = realSize ? Stretch.None : Stretch.Uniform;
            Viewer.HorizontalScrollBarVisibility = Viewer.VerticalScrollBarVisibility =
                realSize ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        }

        // --- Souris ---

        private bool TryGetRemotePoint(MouseEventArgs e, out int x, out int y)
        {
            x = y = 0;
            if (!accepted || bitmap == null || ScreenImage.ActualWidth <= 0) return false;
            var p = e.GetPosition(ScreenImage);
            x = (int)Math.Clamp(p.X * bitmap.PixelWidth / ScreenImage.ActualWidth, 0, bitmap.PixelWidth - 1);
            y = (int)Math.Clamp(p.Y * bitmap.PixelHeight / ScreenImage.ActualHeight, 0, bitmap.PixelHeight - 1);
            lastPoint = (x, y);
            return true;
        }

        private static int? ButtonIndex(MouseButton button) => button switch
        {
            MouseButton.Left => 0,
            MouseButton.Right => 1,
            MouseButton.Middle => 2,
            _ => null
        };

        private void ScreenImage_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Viewer.Focus();
            if (ButtonIndex(e.ChangedButton) is not int button || !TryGetRemotePoint(e, out int x, out int y)) return;
            ScreenImage.CaptureMouse(); // le relâchement arrivera même hors de l'image
            pressed.Add(e.ChangedButton);
            Enqueue(new MouseInput(MouseKind.Down, x, y, button));
            e.Handled = true;
        }

        private void ScreenImage_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!pressed.Remove(e.ChangedButton) || ButtonIndex(e.ChangedButton) is not int button) return;
            TryGetRemotePoint(e, out _, out _);
            Enqueue(new MouseInput(MouseKind.Up, lastPoint.X, lastPoint.Y, button));
            if (pressed.Count == 0) ScreenImage.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void ScreenImage_MouseMove(object sender, MouseEventArgs e)
        {
            var previous = lastPoint;
            if (TryGetRemotePoint(e, out int x, out int y) && (x, y) != previous)
                Enqueue(new MouseInput(MouseKind.Move, x, y, 0));
        }

        private void ScreenImage_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!TryGetRemotePoint(e, out int x, out int y)) return;
            Enqueue(new MouseInput(MouseKind.Wheel, x, y, e.Delta));
            e.Handled = true;
        }

        // Capture perdue (Alt+Tab, fenêtre système...) : relâcher les boutons chez l'utilisateur.
        private void ScreenImage_LostMouseCapture(object sender, MouseEventArgs e)
        {
            foreach (var button in pressed)
                Enqueue(new MouseInput(MouseKind.Up, lastPoint.X, lastPoint.Y, ButtonIndex(button)!.Value));
            pressed.Clear();
        }

        // --- Clavier ---

        private bool KeyboardForwarded => accepted && Viewer.IsKeyboardFocusWithin;

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!KeyboardForwarded) return;
            e.Handled = true; // rien ne doit agir sur cette fenêtre (Alt+F4, Tab, flèches...)
            var key = e.Key switch
            {
                Key.System => e.SystemKey,
                Key.ImeProcessed => e.ImeProcessedKey,
                Key.DeadCharProcessed => e.DeadCharProcessedKey,
                _ => e.Key
            };
            // Les modificateurs seuls ne sont pas envoyés : ils accompagnent la touche suivante.
            if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
            SendKey(key, Keyboard.Modifiers);
        }

        // Sinon, relâcher Alt active le menu système de cette fenêtre et avale la touche suivante.
        private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (KeyboardForwarded) e.Handled = true;
        }

        private void SendKey(Key key, ModifierKeys modifiers) =>
            Enqueue(new KeyStroke(KeyInterop.VirtualKeyFromKey(key), (int)modifiers));

        private void Shortcut_Click(object sender, RoutedEventArgs e)
        {
            if (!accepted) return;
            var gesture = (string)((FrameworkElement)sender).Tag; // ex. "Shift+F8"
            int plus = gesture.LastIndexOf('+');
            var key = (Key)new KeyConverter().ConvertFromInvariantString(gesture[(plus + 1)..])!;
            var modifiers = plus < 0 ? ModifierKeys.None : (ModifierKeys)new ModifierKeysConverter().ConvertFromInvariantString(gesture[..plus])!;
            SendKey(key, modifiers);
            Viewer.Focus();
        }

        private void SendText_Click(object sender, RoutedEventArgs e)
        {
            if (!accepted || TextToSend.Text.Length == 0) return;
            Enqueue(new TextInput(TextToSend.Text));
            TextToSend.Clear();
            Viewer.Focus();
        }

        // --- Fin de session ---

        private void Fail(Exception ex, CancellationToken token)
        {
            if (token.IsCancellationRequested) return; // session déjà fermée : erreur attendue
            App.Log.LogError($"Erreur de session : {ex}");
            End($"L'assistance s'est arrêtée sur une erreur :\n{ex.Message}", sendBye: true);
        }

        private void End(string reason, bool sendBye) => Dispatcher.InvokeAsync(() => EndSession(reason, sendBye));

        private void EndSession(string? reason, bool sendBye)
        {
            if (ended) return;
            ended = true;
            accepted = false;
            cts.Cancel();
            statsTimer.Stop();
            try
            {
                if (sendBye) writer?.Write(new Bye("Le dépanneur a terminé l'assistance."));
            }
            catch (Exception ex) { App.Log.LogWarning($"Fin de session non transmise : {ex.Message}"); }
            writer?.Dispose();
            reader?.Dispose();
            if (session != null)
            {
                var folder = session.Folder;
                Task.Run(() => { try { Directory.Delete(folder, true); } catch (Exception) { } }); // au mieux
            }
            StatusText.Text = reason ?? "Assistance terminée.";
            if (reason != null) MessageBox.Show(this, reason, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void End_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_Closing(object? sender, CancelEventArgs e) => EndSession(null, sendBye: true);
    }
}
