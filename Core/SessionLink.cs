using System.IO;
using System.Security.Cryptography;

namespace ErrorChecker.Core
{
    // Lien envoyé par mail : errorchecker:join?p=<dossier de la session>&k=<clé>
    // La clé n'est jamais écrite sur le partage : sans le mail, impossible de voir l'écran
    // ou d'envoyer des touches, même avec un accès au dossier partagé.
    public sealed record SessionLink(string Folder, byte[] Key)
    {
        public const string Scheme = "errorchecker";
        private const int KeySize = 32;

        public static SessionLink CreateNew(string sharedFolder)
        {
            var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.UserName}-{RandomNumberGenerator.GetInt32(1_000_000):D6}";
            var folder = Path.Combine(sharedFolder, "sessions", id);
            Directory.CreateDirectory(folder);
            return new SessionLink(folder, RandomNumberGenerator.GetBytes(KeySize));
        }

        // Supprime les sessions abandonnées (application fermée brutalement, etc.).
        public static void DeleteStale(string sharedFolder, TimeSpan maxAge)
        {
            var sessions = Path.Combine(sharedFolder, "sessions");
            if (!Directory.Exists(sessions)) return;
            foreach (var dir in Directory.GetDirectories(sessions))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > maxAge) Directory.Delete(dir, true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public override string ToString() =>
            $"{Scheme}:join?p={Uri.EscapeDataString(Folder)}&k={Convert.ToBase64String(Key).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";

        public static SessionLink Parse(string link)
        {
            link = link.Trim().Trim('"');
            int query = link.IndexOf('?');
            if (!link.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase) || query < 0)
                throw new FormatException("Ce n'est pas un lien d'assistance (il doit commencer par « errorchecker: »).");

            string? folder = null, key = null;
            foreach (var part in link[(query + 1)..].Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) continue;
                var value = Uri.UnescapeDataString(part[(eq + 1)..]).TrimEnd('/');  // certains clients ajoutent un « / » final
                if (part[..eq] == "p") folder = value;
                else if (part[..eq] == "k") key = value;
            }
            if (folder == null || key == null) throw new FormatException("Lien d'assistance incomplet.");

            var base64 = key.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
            if (bytes.Length != KeySize) throw new FormatException("Lien d'assistance invalide (clé).");
            return new SessionLink(folder, bytes);
        }
    }
}
