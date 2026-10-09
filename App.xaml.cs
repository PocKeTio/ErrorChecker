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

        // errorchecker:CODE (lien du mail)  => dépanneur, session ouverte directement
        // /depanneur                        => saisie du code
        // /aide [description]               => la demande part sans clic (bouton « Aide » d'une appli VBA)
        // sans argument                     => fenêtre de demande d'aide
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
                Log.LogWarning($"Liens {Session.Scheme}: non enregistrés : {ex.Message}");
            }

            var args = e.Args;
            var link = args.FirstOrDefault(a => a.StartsWith(Session.Scheme + ":", StringComparison.OrdinalIgnoreCase));
            bool Flag(string name) => args.Length > 0 && args[0].TrimStart('/', '-').Equals(name, StringComparison.OrdinalIgnoreCase);
            Window window = link != null ? new HelperWindow(link)
                : Flag("aide") ? new UserWindow(autoSend: true, description: string.Join(" ", args.Skip(1)))
                : new UserWindow(helperMode: Flag("depanneur"));
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
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exe == null) return;
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Session.Scheme);
            key.SetValue("", "URL:Assistance à distance");
            key.SetValue("URL Protocol", "");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exe}\" \"%1\"");
        }
    }
}
