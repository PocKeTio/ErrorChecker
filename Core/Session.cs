using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ErrorChecker.Core
{
    // Une demande d'aide = un code court envoyé par mail, ex. « K7QM-2XPA-9TRD » (60 bits aléatoires).
    // Du code on dérive (PBKDF2) le nom du dossier de session ET la clé de chiffrement : rien de secret
    // n'est écrit sur le partage, et le nom du dossier ne permet pas de retrouver le code.
    public sealed record Session(string Code, string Folder, byte[] Key)
    {
        public const string Scheme = "errorchecker";
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // base32 de Crockford : ni I, L, O, U
        private const int CodeLength = 12;
        // 60 bits de hasard + 10 000 itérations : retrouver un code hors ligne depuis le partage coûterait
        // ~2^73 opérations (des milliers d'années-GPU) pour une session qui dure quelques minutes.
        // Plus d'itérations ne protège pas mieux et gèle l'interface (PBKDF2 est lent sous .NET Framework).
        private const int Iterations = 10_000;
        private static readonly byte[] Salt = Encoding.ASCII.GetBytes("ErrorChecker/session/v1");

        public string Display => $"{Code[..4]}-{Code[4..8]}-{Code[8..]}";
        public string Link => $"{Scheme}:{Code}";

        public static Session CreateNew(string sharedFolder)
        {
            // 32 symboles = 5 bits : un octet aléatoire par caractère, sans biais (256 est un multiple de 32).
            var random = new byte[CodeLength];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
            var code = random.Select(b => Alphabet[b % Alphabet.Length]).ToArray();
            var session = Open(sharedFolder, new string(code));
            Directory.CreateDirectory(session.Folder);
            return session;
        }

        // Accepte le code tapé (minuscules, espaces, tirets, O/0 et I/L/1 confondus) ou le lien errorchecker:...
        public static Session Open(string sharedFolder, string codeOrLink)
        {
            var code = Normalize(codeOrLink);
            byte[] derived;
            using (var pbkdf2 = new Rfc2898DeriveBytes(code, Salt, Iterations, HashAlgorithmName.SHA256)) derived = pbkdf2.GetBytes(32 + 10);
            var folder = Path.Combine(sharedFolder, "sessions", "s-" + BitConverter.ToString(derived, 32, 10).Replace("-", "").ToLowerInvariant());
            return new Session(code, folder, derived.Take(32).ToArray());
        }

        public static string Normalize(string input)
        {
            var text = input.Trim().Trim('"');
            if (text.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)) text = text[(Scheme.Length + 1)..];
            var code = new StringBuilder();
            foreach (char c in text.ToUpperInvariant())
            {
                char ch = c switch { 'O' => '0', 'I' or 'L' => '1', _ => c };
                if (Alphabet.Contains(ch)) code.Append(ch);
                else if (c is not ('-' or ' ' or '/')) throw new FormatException($"Caractère inattendu dans le code : « {c} ».");
            }
            if (code.Length != CodeLength) throw new FormatException("Le code doit faire 12 caractères, par exemple K7QM-2XPA-9TRD.");
            return code.ToString();
        }

        // Supprime les sessions abandonnées (application fermée brutalement, etc.) : sans écriture depuis maxAge.
        // Une demande en attente écrit un signe de vie toutes les 2 s : elle n'est jamais concernée.
        public static void DeleteStale(string sharedFolder, TimeSpan maxAge)
        {
            var sessions = Path.Combine(sharedFolder, "sessions");
            if (!Directory.Exists(sessions)) return;
            foreach (var dir in Directory.GetDirectories(sessions))
            {
                try
                {
                    var lastActivity = Directory.GetFiles(dir).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(Directory.GetCreationTimeUtc(dir)).Max();
                    if (DateTime.UtcNow - lastActivity > maxAge) Directory.Delete(dir, true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
