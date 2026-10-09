using System.Runtime.InteropServices;

namespace ErrorChecker.Mail
{
    // Envoi par Outlook (version classique, automation COM) : aucun serveur SMTP à configurer.
    public static class OutlookMailer
    {
        public static void Send(string to, string subject, string htmlBody)
        {
            var type = Type.GetTypeFromProgID("Outlook.Application")
                ?? throw new InvalidOperationException("Outlook n'est pas installé sur ce poste.");
            dynamic outlook = Activator.CreateInstance(type)!;
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
            }
        }
    }
}
