using System.IO;
using System.Text.Json;

namespace ErrorChecker.Core
{
    // ErrorChecker.json, à côté de l'exécutable (voir le fichier d'exemple du dépôt).
    public sealed class AppConfig
    {
        public const string FileName = "ErrorChecker.json";

        public string SharedFolder { get; set; } = "";
        // Destinataires possibles des demandes d'aide ; le premier est proposé par défaut.
        public List<HelperContact> Helpers { get; set; } = new();

        public static AppConfig Load(string directory)
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) throw new FileNotFoundException($"Configuration introuvable : {path}");
            var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), options) ?? new AppConfig();
            if (string.IsNullOrWhiteSpace(config.SharedFolder) || config.Helpers.Count == 0 || config.Helpers.Any(h => string.IsNullOrWhiteSpace(h.Email)))
                throw new InvalidDataException($"{path} : SharedFolder et au moins un dépanneur (Helpers, avec Email) sont obligatoires.");
            return config;
        }
    }

    public sealed class HelperContact
    {
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Email : Name;
    }
}
