using System.IO;
using System.Text.Json;

namespace ErrorChecker.Core
{
    // ErrorChecker.json, à côté de l'exécutable (voir le fichier d'exemple du dépôt).
    public sealed class AppConfig
    {
        public const string FileName = "ErrorChecker.json";

        public string SharedFolder { get; set; } = "";
        public string SupportEmail { get; set; } = "";

        public static AppConfig Load(string directory)
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) throw new FileNotFoundException($"Configuration introuvable : {path}");
            var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), options) ?? new AppConfig();
            if (string.IsNullOrWhiteSpace(config.SharedFolder) || string.IsNullOrWhiteSpace(config.SupportEmail))
                throw new InvalidDataException($"{path} : SharedFolder et SupportEmail sont obligatoires.");
            return config;
        }
    }
}
