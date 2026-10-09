using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ErrorChecker.Mail
{
    public enum MailResult { Sent, WaitingInOutbox, ShownToUser }

    // Envoi par Outlook (version classique, automation COM) : aucun serveur SMTP à configurer.
    public static class OutlookMailer
    {
        private const int OutboxFolder = 4; // olFolderOutbox

        public static MailResult Send(string to, string subject, string htmlBody)
        {
            var type = Type.GetTypeFromProgID("Outlook.Application")
                ?? throw new InvalidOperationException("Outlook n'est pas installé sur ce poste.");
            bool wasRunning = Process.GetProcessesByName("OUTLOOK").Length > 0;
            dynamic outlook = Activator.CreateInstance(type)!;
            dynamic outbox = outlook.Session.GetDefaultFolder(OutboxFolder);
            int queuedBefore = outbox.Items.Count;
            dynamic mail = outlook.CreateItem(0); // olMailItem
            mail.To = to;
            mail.Subject = subject;
            mail.HTMLBody = htmlBody;
            try
            {
                mail.Send();
            }
            catch (COMException)
            {
                // Envoi automatique bloqué par la sécurité d'Outlook : on affiche le mail, il reste à cliquer sur Envoyer.
                mail.Display(false);
                return MailResult.ShownToUser;
            }
            if (wasRunning) return MailResult.Sent;

            // Outlook démarré juste pour l'occasion : sans lui le mail peut rester dans la boîte d'envoi.
            try
            {
                outlook.Session.SendAndReceive(false);
                for (int i = 0; i < 30 && outbox.Items.Count > queuedBefore; i++) Thread.Sleep(500);
                return outbox.Items.Count > queuedBefore ? MailResult.WaitingInOutbox : MailResult.Sent;
            }
            catch (COMException)
            {
                return MailResult.WaitingInOutbox;
            }
        }
    }
}
