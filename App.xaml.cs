using System.IO;
using System.Windows;
using ErrorChecker.Core;
using ErrorChecker.Logging;
using Microsoft.Win32;

namespace ErrorChecker
{
    public partial class App : Application
    {
        public static Logger Log { get; } = new(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ErrorChecker"), LogLevel.Info);

        // Lancé par un lien errorchecker:... (mail) => dépanneur ; sinon => utilisateur.
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (_, args) =>
            {
                Log.LogError($"Erreur non gérée : {args.Exception}");
                MessageBox.Show(args.Exception.Message, "Assistance à distance", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            try
            {
                RegisterUrlProtocol();
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Liens {SessionLink.Scheme}: non enregistrés : {ex.Message}");
            }

            var link = e.Args.FirstOrDefault(a => a.StartsWith(SessionLink.Scheme + ":", StringComparison.OrdinalIgnoreCase));
            Window window = link != null ? new HelperWindow(link) : new UserWindow();
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log.Dispose();
            base.OnExit(e);
        }

        // Les liens errorchecker: des mails ouvrent cet exécutable. Dans HKCU : pas besoin d'être administrateur.
        private static void RegisterUrlProtocol()
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + SessionLink.Scheme);
            key.SetValue("", "URL:Assistance à distance");
            key.SetValue("URL Protocol", "");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exe}\" \"%1\"");
        }
    }
}
