using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Forms;
using Screen = System.Windows.Forms.Screen;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Linq;
using ErrorChecker.FileAccess;
using ErrorChecker.Logging;
using ErrorChecker.Security;
using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using System.Runtime.InteropServices;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using LogLevel = ErrorChecker.Logging.LogLevel;

namespace ErrorChecker
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private bool isRunning = false;
        private CancellationTokenSource? cancellationTokenSource;
        private string currentMode = "";
        private string sharedFolderPath = "";
        private const string SCREENSHOT_FILENAME = "screen.enc";
        private const string COMMAND_PREFIX = "cmd_";
        private const int CAPTURE_INTERVAL = 50; // ms

        private bool isMouseOverImage;
        private ConcurrentQueue<RemoteCommand> commandQueue = new ConcurrentQueue<RemoteCommand>();
        
        private Encryption encryption;
        private ImageCompressor imageCompressor;
        private ScreenManager screenManager;
        private Logger logger;
        private TextBlock latencyDisplay;
        private WindowManager windowManager;
        private WindowInfo selectedWindow;
        private bool isScreenMode = true;
        private string latency = "Latence : -- ms";
        public string Latency
        {
            get { return latency; }
            set
            {
                if (latency != value)
                {
                    latency = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Latency)));
                }
            }
        }

        private DateTime lastClickTime;
        private Point lastClickPosition;
        private const int DOUBLE_CLICK_TIME = 500; // ms
        private const int DOUBLE_CLICK_DISTANCE = 5; // pixels
        private int commandCounter = 0;

        private bool isCapturingKeys = false;

        // Référence au Border autour de ScreenDisplay
        private Border screenDisplayBorder;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            screenManager = new ScreenManager();
            imageCompressor = new ImageCompressor();
            windowManager = new WindowManager();
            
            // Initialiser la sélection d'écran
            InitializeScreenSelection();

            // Initialiser la liste des applications
            RefreshWindowsList();

            // Mode par défaut
            ModeSelection.SelectedIndex = 0;
            SharingType.SelectedIndex = 0; // Sélectionner le mode écran par défaut
            isScreenMode = true; // Synchroniser l'état initial

            // Récupérer la référence au Border
            screenDisplayBorder = (Border)this.FindName("ScreenDisplayBorder");

            logger = new Logger(Path.Combine(Environment.CurrentDirectory, "logs"), LogLevel.Error);

            // Ajouter l'affichage de la latence
            latencyDisplay = new TextBlock
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 10, 10, 0),
                Foreground = System.Windows.Media.Brushes.White,
                Background = System.Windows.Media.Brushes.Black,
                Padding = new Thickness(5)
            };
            Grid mainGrid = (Grid)this.FindName("MainGrid");
            if (mainGrid != null)
            {
                mainGrid.Children.Add(latencyDisplay);
            }

            LatencyText = new TextBlock
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 10, 10, 0),
                Foreground = System.Windows.Media.Brushes.White,
                Background = System.Windows.Media.Brushes.Black,
                Padding = new Thickness(5)
            };
            if (mainGrid != null)
            {
                mainGrid.Children.Add(LatencyText);
            }

            // Gérer le focus de la souris sur l'image
            ScreenDisplay.MouseEnter += ScreenDisplay_MouseEnter;
            ScreenDisplay.MouseLeave += ScreenDisplay_MouseLeave;
            ScreenDisplay.MouseDown += ScreenDisplay_MouseDown;
            ScreenDisplay.LostFocus += ScreenDisplay_LostFocus;
        }

        private async Task ProcessCommandQueue(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (commandQueue.TryDequeue(out var command))
                    {
                        // Créer un nom unique pour le fichier de commande
                        var cmdId = Interlocked.Increment(ref commandCounter);
                        string commandFilePath = Path.Combine(sharedFolderPath, $"{COMMAND_PREFIX}{cmdId}.enc");

                        // Sérialiser et chiffrer la commande
                        var commandJson = JsonSerializer.Serialize(command);
                        var encryptedCommand = encryption.Encrypt(Encoding.UTF8.GetBytes(commandJson));

                        // Écrire dans le fichier
                        await FileManager.SafeWriteAllBytesAsync(commandFilePath, encryptedCommand, token);
                        logger?.LogDebug($"Envoi commande {command.Type} - Fichier: {commandFilePath}");
                    }

                    await Task.Delay(1, token);
                }
                catch (Exception ex)
                {
                    logger?.LogError($"Erreur lors du traitement des commandes : {ex.Message}");
                    await Task.Delay(100, token);
                }
            }
        }

        private async Task ProcessReceivedCommands(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Rechercher tous les fichiers de commande
                    var commandFiles = Directory.GetFiles(sharedFolderPath, $"{COMMAND_PREFIX}*.enc")
                                              .OrderBy(f => f)
                                              .ToList();

                    foreach (var filePath in commandFiles)
                    {
                        try
                        {
                            // Lire et déchiffrer la commande
                            var encryptedData = await File.ReadAllBytesAsync(filePath, token);
                            var decryptedData = encryption.Decrypt(encryptedData);
                            var commandJson = Encoding.UTF8.GetString(decryptedData);

                            // Désérialiser et exécuter la commande
                            var command = JsonSerializer.Deserialize<RemoteCommand>(commandJson);
                            if (command != null)
                            {
                                ExecuteCommand(command);
                                logger?.LogDebug($"Exécution commande {command.Type} - Fichier: {filePath}");
                            }

                            // Supprimer le fichier après traitement
                            File.Delete(filePath);
                        }
                        catch (Exception ex)
                        {
                            logger?.LogError($"Erreur lors du traitement du fichier {filePath}: {ex.Message}");
                            try { File.Delete(filePath); } catch { }
                        }
                    }

                    await Task.Delay(1, token);
                }
                catch (Exception ex)
                {
                    logger?.LogError($"Erreur lors du traitement des commandes reçues : {ex.Message}");
                    await Task.Delay(100, token);
                }
            }
        }

        private async Task CaptureScreen(CancellationToken token)
        {
            Stopwatch latencyTimer = new Stopwatch();
            int consecutiveErrors = 0;
            const int MAX_ERRORS = 5;

            while (!token.IsCancellationRequested && isRunning)
            {
                try
                {
                    latencyTimer.Restart();
                    
                    Bitmap screenshot = null;
                    try
                    {
                        if (isScreenMode)
                        {
                            screenshot = screenManager.CaptureScreen();
                        }
                        else
                        {
                            if (selectedWindow == null) 
                            {
                                await Task.Delay(CAPTURE_INTERVAL, token);
                                continue;
                            }
                            screenshot = windowManager.CaptureWindow(selectedWindow.Handle);
                        }

                        using var ms = new MemoryStream();
                        await imageCompressor.CompressAndSaveAsync(screenshot, ms);
                        byte[] imageData = ms.ToArray();

                        if (encryption == null)
                        {
                            throw new InvalidOperationException("Le système de cryptage n'est pas initialisé");
                        }

                        byte[] encryptedData = encryption.Encrypt(imageData);
                        var timestamp = DateTime.Now;
                        string screenshotPath = Path.Combine(sharedFolderPath, SCREENSHOT_FILENAME);
                        await FileManager.SafeWriteAllBytesAsync(screenshotPath, encryptedData, token);

                        var latency = DateTime.Now - timestamp;
                        UpdateLatencyDisplay(latency);
                        //logger?.LogInfo($"Latence mesurée (capture) : {latency.TotalMilliseconds:F0} ms");
                        consecutiveErrors = 0;
                    }
                    finally
                    {
                        screenshot?.Dispose();
                    }

                    latencyTimer.Stop();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        LatencyText.Text = $"Latence : {latencyTimer.ElapsedMilliseconds} ms";
                    });

                    await Task.Delay(CAPTURE_INTERVAL, token);
                }
                catch (Exception ex)
                {
                    logger.LogError($"Erreur lors de la capture : {ex.Message}");
                    //consecutiveErrors++;

                    if (consecutiveErrors >= MAX_ERRORS)
                    {
                        logger.LogError("Trop d'erreurs consécutives, arrêt de la capture");
                        await Dispatcher.InvokeAsync(() =>
                        {
                            MessageBox.Show("Trop d'erreurs consécutives, l'application va s'arrêter.");
                            StopApplication();
                        });
                        break;
                    }

                    await Task.Delay(CAPTURE_INTERVAL * 2, token);
                }
            }
        }

        private async Task WatchScreen(CancellationToken token)
        {
            Stopwatch latencyTimer = new Stopwatch();
            int consecutiveErrors = 0;
            const int MAX_ERRORS = 5;
            string lastImageHash = string.Empty;

            while (!token.IsCancellationRequested && isRunning)
            {
                try
                {
                    latencyTimer.Restart();
                    
                    string screenshotPath = Path.Combine(sharedFolderPath, SCREENSHOT_FILENAME);
                    if (!File.Exists(screenshotPath))
                    {
                        await Task.Delay(CAPTURE_INTERVAL / 2, token);
                        continue;
                    }

                    var timestamp = DateTime.Now;
                    byte[] encryptedData = await FileManager.SafeReadAllBytesAsync(screenshotPath, token);
                    byte[] imageData = encryption.Decrypt(encryptedData);
                    
                    // Vérifier si l'image a changé
                    string currentHash = Convert.ToBase64String(
                        System.Security.Cryptography.SHA256.Create()
                        .ComputeHash(imageData)
                    );

                    if (currentHash != lastImageHash)
                    {
                        using var ms = new MemoryStream(imageData);
                        var bitmap = await imageCompressor.LoadCompressedImageAsync(ms);
                        
                        await Dispatcher.InvokeAsync(() =>
                        {
                            ScreenDisplay.Source = bitmap;
                            var latency = DateTime.Now - timestamp;
                            UpdateLatencyDisplay(latency);
                            logger?.LogInfo($"Latence mesurée (affichage) : {latency.TotalMilliseconds:F0} ms");
                        });

                        lastImageHash = currentHash;
                    }

                    consecutiveErrors = 0;
                    latencyTimer.Stop();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        LatencyText.Text = $"Latence : {latencyTimer.ElapsedMilliseconds} ms";
                    });

                    await Task.Delay(CAPTURE_INTERVAL / 2, token);
                }
                catch (Exception ex)
                {
                    logger.LogError($"Erreur lors de la surveillance : {ex.Message}");
                    //consecutiveErrors++;

                    if (consecutiveErrors >= MAX_ERRORS)
                    {
                        logger.LogError("Trop d'erreurs consécutives, arrêt de la surveillance");
                        await Dispatcher.InvokeAsync(() =>
                        {
                            MessageBox.Show("Trop d'erreurs consécutives, l'application va s'arrêter.");
                            StopApplication();
                        });
                        break;
                    }

                    await Task.Delay(CAPTURE_INTERVAL, token);
                }
            }
        }

        private Point GetScaledMousePosition(Point mousePosition)
        {
            if (ScreenDisplay.Source == null) return mousePosition;

            // Dimensions de l'image source
            double sourceWidth = ScreenDisplay.Source.Width;
            double sourceHeight = ScreenDisplay.Source.Height;

            // Si mode taille réelle, pas besoin de calculs complexes
            if (RealSizeMode.IsChecked == true)
            {
                return mousePosition;
            }

            // Dimensions de la zone d'affichage
            double displayWidth = ScreenDisplay.ActualWidth;
            double displayHeight = ScreenDisplay.ActualHeight;

            // Calcul simple du ratio de mise à l'échelle
            double scaleX = sourceWidth / displayWidth;
            double scaleY = sourceHeight / displayHeight;

            // Conversion des coordonnées
            return new Point(
                mousePosition.X * scaleX,
                mousePosition.Y * scaleY
            );
        }

        private void ScreenDisplay_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!isMouseOverImage || currentMode != "Dépanneur") return;

            if (!isCapturingKeys)
            {
                isCapturingKeys = true;
                ScreenDisplay.Focusable = true;
                ScreenDisplay.Focus();
            }

            var mousePosition = e.GetPosition(ScreenDisplay);
            var scaledPosition = GetScaledMousePosition(mousePosition);

            // Vérifier si c'est un double-clic
            var currentTime = DateTime.Now;
            var timeSinceLastClick = (currentTime - lastClickTime).TotalMilliseconds;
            var distance = Math.Sqrt(Math.Pow(scaledPosition.X - lastClickPosition.X, 2) + 
                                   Math.Pow(scaledPosition.Y - lastClickPosition.Y, 2));

            bool isDoubleClick = timeSinceLastClick <= DOUBLE_CLICK_TIME && 
                               distance <= DOUBLE_CLICK_DISTANCE && 
                               e.ChangedButton == MouseButton.Left;

            int buttonValue = e.ChangedButton switch
            {
                MouseButton.Left => 0,
                MouseButton.Right => 1,
                MouseButton.Middle => 2,
                _ => 0
            };

            var command = new RemoteCommand
            {
                Type = RemoteCommand.CommandType.MouseClick,
                Button = buttonValue,
                X = (int)scaledPosition.X,
                Y = (int)scaledPosition.Y,
                IsDoubleClick = isDoubleClick
            };
            EnqueueCommand(command);

            lastClickTime = currentTime;
            lastClickPosition = scaledPosition;
        }

        private void ScreenDisplay_LostFocus(object sender, RoutedEventArgs e)
        {
            isCapturingKeys = false;
            logger?.LogDebug("Capture des touches désactivée");
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (currentMode != "Dépanneur" || !isCapturingKeys) return;

            // Ignorer les touches modificatrices seules
            switch (e.Key)
            {
                case Key.LeftShift:
                case Key.RightShift:
                case Key.LeftCtrl:
                case Key.RightCtrl:
                case Key.LeftAlt:
                case Key.RightAlt:
                case Key.System: // Alt Gr
                    return;
            }

            // Gérer les touches spéciales
            string? specialKey = null;
            switch (e.Key)
            {
                case Key.Enter: specialKey = "\r"; break;
                case Key.Tab: specialKey = "\t"; break;
                case Key.Space: specialKey = " "; break;
                case Key.Back: specialKey = "\b"; break;
                case Key.Delete: specialKey = "\u007F"; break;
                case Key.Escape: specialKey = "\u001B"; break;
                case Key.Left: specialKey = "{LEFT}"; break;
                case Key.Right: specialKey = "{RIGHT}"; break;
                case Key.Up: specialKey = "{UP}"; break;
                case Key.Down: specialKey = "{DOWN}"; break;
                case Key.Home: specialKey = "{HOME}"; break;
                case Key.End: specialKey = "{END}"; break;
                case Key.PageUp: specialKey = "{PGUP}"; break;
                case Key.PageDown: specialKey = "{PGDN}"; break;
                case Key.Insert: specialKey = "{INS}"; break;
                case Key.F1: specialKey = "{F1}"; break;
                case Key.F2: specialKey = "{F2}"; break;
                case Key.F3: specialKey = "{F3}"; break;
                case Key.F4: specialKey = "{F4}"; break;
                case Key.F5: specialKey = "{F5}"; break;
                case Key.F6: specialKey = "{F6}"; break;
                case Key.F7: specialKey = "{F7}"; break;
                case Key.F8: specialKey = "{F8}"; break;
                case Key.F9: specialKey = "{F9}"; break;
                case Key.F10: specialKey = "{F10}"; break;
                case Key.F11: specialKey = "{F11}"; break;
                case Key.F12: specialKey = "{F12}"; break;
            }

            if (specialKey != null)
            {
                var command = new RemoteCommand
                {
                    Type = RemoteCommand.CommandType.KeyPress,
                    KeyChar = specialKey
                };
                EnqueueCommand(command);
                e.Handled = true;
                return;
            }

            // Pour les touches normales, obtenir le vrai caractère
            var keyStates = new byte[256];
            GetKeyboardState(keyStates);

            var virtualKey = KeyInterop.VirtualKeyFromKey(e.Key);
            var scanCode = MapVirtualKey((uint)virtualKey, 0);
            var chars = new StringBuilder(5);

            if (ToUnicode((uint)virtualKey, scanCode, keyStates, chars, chars.Capacity, 0) > 0)
            {
                var command = new RemoteCommand
                {
                    Type = RemoteCommand.CommandType.KeyPress,
                    KeyChar = chars.ToString()
                };
                EnqueueCommand(command);
                e.Handled = true;
            }
        }

        [DllImport("user32.dll")]
        private static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
            [Out] StringBuilder pwszBuff, int cchBuff, uint wFlags);

        [DllImport("user32.dll")]
        private static extern bool GetKeyboardState(byte[] lpKeyState);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        private void MainWindow_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (currentMode != "Dépanneur" || !isCapturingKeys) return;

            var command = new RemoteCommand
            {
                Type = RemoteCommand.CommandType.KeyPress,
                KeyChar = e.Text
            };
            EnqueueCommand(command);
            logger?.LogDebug($"Caractère : {e.Text}");
            e.Handled = true;
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartApplication();
        }

        private void StartApplication()
        {
            if (string.IsNullOrWhiteSpace(SharedFolderPath.Text))
            {
                MessageBox.Show("Veuillez sélectionner un dossier partagé.");
                return;
            }

            try
            {
                sharedFolderPath = SharedFolderPath.Text;
                if (!Directory.Exists(sharedFolderPath))
                {
                    Directory.CreateDirectory(sharedFolderPath);
                }

                currentMode = ((ComboBoxItem)ModeSelection.SelectedItem).Content.ToString();
                encryption = new Encryption(sharedFolderPath);

                isRunning = true;
                cancellationTokenSource = new CancellationTokenSource();
                var token = cancellationTokenSource.Token;

                if (currentMode == "Dépanneur")
                {
                    // Mode dépanneur - lit les images et envoie les commandes
                    Task.Run(() => WatchScreen(token), token);
                    Task.Run(() => ProcessCommandQueue(token), token);
                }
                else
                {
                    // Mode utilisateur - capture les images et lit les commandes
                    if (!isScreenMode && selectedWindow == null)
                    {
                        MessageBox.Show("Veuillez sélectionner une fenêtre à partager.", "Erreur",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        StopApplication();
                        return;
                    }

                    if (string.IsNullOrEmpty(sharedFolderPath))
                    {
                        MessageBox.Show("Veuillez sélectionner un dossier partagé.", "Erreur",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        StopApplication();
                        return;
                    }

                    Task.Run(() => CaptureScreen(token), token);
                    if (AllowRemoteControl.IsChecked == true)
                    {
                        Task.Run(() => ProcessReceivedCommands(token), token);
                    }
                }

                StartButton.IsEnabled = false;
                StopButton.IsEnabled = true;
                StatusText.Text = "En cours d'exécution...";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erreur lors du démarrage : {ex.Message}");
                StopApplication();
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopApplication();
        }

        private void StopApplication()
        {
            isRunning = false;
            cancellationTokenSource?.Cancel();
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }

        private void ModeSelection_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ModeSelection.SelectedItem == null) return;

            var isUserMode = ((ComboBoxItem)ModeSelection.SelectedItem).Content.ToString() == "Utilisateur";
            //UserControls.Visibility = isUserMode ? Visibility.Visible : Visibility.Collapsed;

            // Initialiser les contrôles appropriés
            if (isUserMode)
            {
                if (SharingType.SelectedItem == null)
                    SharingType.SelectedIndex = 0;
                
                SharingType_SelectionChanged(SharingType, null);
            }
        }

        private void UpdateLatencyDisplay(TimeSpan latency)
        {
            Latency = $"Latence : {latency.TotalMilliseconds:F0} ms";
        }

        private void InitializeScreenSelection()
        {
            var screens = screenManager.GetScreens();
            ScreenSelection.ItemsSource = screens;
            if (screens.Count > 0)
                ScreenSelection.SelectedIndex = 0;
        }

        private void ScreenSelection_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ScreenSelection.SelectedIndex >= 0)
            {
                var screens = screenManager.GetScreens();
                if (ScreenSelection.SelectedIndex < screens.Count)
                {
                    screenManager.SelectScreen((ScreenInfo)ScreenSelection.SelectedItem);
                    logger?.LogInfo($"Écran sélectionné : {ScreenSelection.SelectedIndex + 1}");
                }
            }
        }

        private void RefreshWindowsList()
        {
            try
            {
                var windows = windowManager.GetWindows();
                
                // Filtrer les fenêtres vides ou système
                windows = windows.Where(w => !string.IsNullOrWhiteSpace(w.Title) && 
                                           !w.Title.Equals("Program Manager", StringComparison.OrdinalIgnoreCase))
                               .OrderBy(w => w.Title)
                               .ToList();

                WindowSelection.ItemsSource = windows;
                
                if (windows.Count > 0)
                {
                    WindowSelection.SelectedIndex = 0;
                    logger?.LogInfo($"Liste des applications rafraîchie : {windows.Count} fenêtres trouvées");
                }
                else
                {
                    logger?.LogWarning("Aucune fenêtre trouvée lors du rafraîchissement");
                    WindowSelection.ItemsSource = new List<WindowInfo> { new WindowInfo { Title = "Aucune application disponible" } };
                    WindowSelection.SelectedIndex = 0;
                    selectedWindow = null;
                }
            }
            catch (Exception ex)
            {
                logger?.LogError($"Erreur lors du rafraîchissement de la liste des fenêtres : {ex.Message}");
                WindowSelection.ItemsSource = new List<WindowInfo> { new WindowInfo { Title = "Erreur lors du chargement" } };
                WindowSelection.SelectedIndex = 0;
                selectedWindow = null;
            }
        }

        private void RefreshWindows_Click(object sender, RoutedEventArgs e)
        {
            RefreshWindowsList();
        }

        private void WindowSelection_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (WindowSelection.SelectedItem != null)
            {
                selectedWindow = (WindowInfo)WindowSelection.SelectedItem;
            }
        }

        private void ExecuteCommand(RemoteCommand command)
        {
            try
            {
                // En mode application, vérifier si la fenêtre existe toujours
                if (!isScreenMode)
                {
                    if (!windowManager.IsWindowValid(selectedWindow.Handle))
                    {
                        logger?.LogInfo("L'application partagée a été fermée");
                        Dispatcher.InvokeAsync(() => 
                        {
                            MessageBox.Show("L'application partagée a été fermée.", "Partage terminé", MessageBoxButton.OK, MessageBoxImage.Information);
                            StopApplication();
                        });
                        return;
                    }
                }

                switch (command.Type)
                {
                    case RemoteCommand.CommandType.MouseClick:
                        if (isScreenMode)
                        {
                            // En mode écran, ajouter l'offset de l'écran
                            var bounds = screenManager.GetCurrentScreenBounds();
                            command.X += bounds.X;
                            command.Y += bounds.Y;
                        }
                        else
                        {
                            // En mode application, ajouter la position réelle de l'application
                            var clientPoint = windowManager.GetClientPoint(selectedWindow.Handle);
                            command.X += clientPoint.X;
                            command.Y += clientPoint.Y;

                            // Focus sur la fenêtre avant le clic
                            windowManager.FocusWindow(selectedWindow.Handle);
                            Thread.Sleep(50);
                        }
                        
                        MouseOperations.MouseClick(command.X, command.Y, command.Button, command.IsDoubleClick);
                        break;

                    case RemoteCommand.CommandType.KeyPress:
                        if (string.IsNullOrEmpty(command.KeyChar)) break;

                        if (!isScreenMode)
                        {
                            windowManager.FocusWindow(selectedWindow.Handle);
                            Thread.Sleep(50);
                        }

                        // Gérer les touches spéciales avec des codes virtuels
                        if (command.KeyChar.StartsWith("{") && command.KeyChar.EndsWith("}"))
                        {
                            SendKeys.SendWait(command.KeyChar);
                        }
                        else
                        {
                            // Envoyer directement le caractère
                            foreach (char c in command.KeyChar)
                            {
                                KeyOperations.SendCharacter(c);
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                logger?.LogError($"Erreur lors de l'exécution de la commande : {ex.Message}");
            }
        }

        private void EnqueueCommand(RemoteCommand command)
        {
            if (!isRunning || currentMode != "Dépanneur") return;

            commandQueue.Enqueue(command);
            logger?.LogDebug($"Commande ajoutée : {command.Type}");
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog();
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                SharedFolderPath.Text = dialog.SelectedPath;
            }
        }

        private void SharingType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SharingType.SelectedItem == null) return;

            var content = ((ComboBoxItem)SharingType.SelectedItem).Content.ToString();
            isScreenMode = content == "Écran";

            // Mise à jour de la visibilité des panneaux
            if (isScreenMode)
            {
                ScreenSelectionPanel.Visibility = Visibility.Visible;
                WindowSelectionPanel.Visibility = Visibility.Collapsed;
                if (ScreenSelection.Items.Count > 0)
                    ScreenSelection.SelectedIndex = 0;
            }
            else
            {
                ScreenSelectionPanel.Visibility = Visibility.Collapsed;
                WindowSelectionPanel.Visibility = Visibility.Visible;
                if (WindowSelection.Items.Count > 0)
                    WindowSelection.SelectedIndex = 0;
            }

            logger?.LogDebug($"Mode de partage changé : {(isScreenMode ? "Écran" : "Application")}");
        }

        private void RealSizeMode_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (!isRunning || currentMode != "Dépanneur") return;

            bool isRealSize = RealSizeMode.IsChecked ?? false;
            
            // Adapter la taille de la fenêtre
            if (ScreenDisplay.Source != null)
            {
                var workingArea = SystemParameters.WorkArea;
                double newWidth, newHeight;

                if (isRealSize)
                {
                    // Taille réelle + panneau de config
                    newWidth = Math.Min(ScreenDisplay.Source.Width + 300, workingArea.Width);
                    newHeight = Math.Min(ScreenDisplay.Source.Height + 50, workingArea.Height);
                }
                else
                {
                    // Taille adaptée à l'écran
                    newWidth = Math.Min(workingArea.Width, 800);
                    newHeight = Math.Min(workingArea.Height, 600);
                }

                Width = newWidth;
                Height = newHeight;

                // S'assurer que la fenêtre reste visible
                if (Left + Width > workingArea.Width)
                    Left = Math.Max(0, workingArea.Width - Width);
                if (Top + Height > workingArea.Height)
                    Top = Math.Max(0, workingArea.Height - Height);
            }
        }

        private void ScreenDisplay_MouseEnter(object sender, MouseEventArgs e)
        {
            isMouseOverImage = true;
            if (currentMode == "Dépanneur")
            {
                Mouse.OverrideCursor = System.Windows.Input.Cursors.Cross;
                ScreenDisplay.Focus();
                isCapturingKeys = true;
                screenDisplayBorder.BorderThickness = new Thickness(2);
                screenDisplayBorder.BorderBrush = System.Windows.Media.Brushes.Red;
            }
        }

        private void ScreenDisplay_MouseLeave(object sender, MouseEventArgs e)
        {
            isMouseOverImage = false;
            if (currentMode == "Dépanneur")
            {
                Mouse.OverrideCursor = null;
                isCapturingKeys = false;
                screenDisplayBorder.BorderThickness = new Thickness(1);
                screenDisplayBorder.BorderBrush = System.Windows.Media.Brushes.Black;
            }
        }
    }

    public static class MouseOperations
    {
        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

        public static void MouseClick(int x, int y, int button, bool isDoubleClick)
        {
            SetCursorPos(x, y);
            Thread.Sleep(50); // Attendre que le curseur soit positionné

            uint downFlag = button switch
            {
                0 => MOUSEEVENTF_LEFTDOWN,
                1 => MOUSEEVENTF_RIGHTDOWN,
                2 => MOUSEEVENTF_MIDDLEDOWN,
                _ => MOUSEEVENTF_LEFTDOWN
            };

            uint upFlag = button switch
            {
                0 => MOUSEEVENTF_LEFTUP,
                1 => MOUSEEVENTF_RIGHTUP,
                2 => MOUSEEVENTF_MIDDLEUP,
                _ => MOUSEEVENTF_LEFTUP
            };

            // Premier clic
            mouse_event(downFlag, 0, 0, 0, 0);
            Thread.Sleep(10);
            mouse_event(upFlag, 0, 0, 0, 0);

            if (isDoubleClick)
            {
                Thread.Sleep(10);
                mouse_event(downFlag, 0, 0, 0, 0);
                Thread.Sleep(10);
                mouse_event(upFlag, 0, 0, 0, 0);
            }
        }
    }

    public static class KeyOperations
    {
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern short VkKeyScan(char ch);

        private const int KEYEVENTF_KEYUP = 0x0002;

        public static void SendCharacter(char c)
        {
            short vk = VkKeyScan(c);
            byte virtualKey = (byte)(vk & 0xff);
            bool shift = (vk & 0x100) != 0;
            bool ctrl = (vk & 0x200) != 0;
            bool alt = (vk & 0x400) != 0;

            // Appuyer sur les modificateurs si nécessaire
            if (shift) keybd_event(0x10, 0, 0, 0);  // SHIFT
            if (ctrl)  keybd_event(0x11, 0, 0, 0);  // CTRL
            if (alt)   keybd_event(0x12, 0, 0, 0);  // ALT

            // Appuyer et relâcher la touche principale
            keybd_event(virtualKey, 0, 0, 0);
            Thread.Sleep(5);
            keybd_event(virtualKey, 0, KEYEVENTF_KEYUP, 0);

            // Relâcher les modificateurs dans l'ordre inverse
            if (alt)   keybd_event(0x12, 0, KEYEVENTF_KEYUP, 0);
            if (ctrl)  keybd_event(0x11, 0, KEYEVENTF_KEYUP, 0);
            if (shift) keybd_event(0x10, 0, KEYEVENTF_KEYUP, 0);
        }
    }
}
