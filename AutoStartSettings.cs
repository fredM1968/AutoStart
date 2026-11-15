using System;
using System.IO;
using System.Text.Json;

namespace NINA.Plugin.AutoStart {

    public enum SequenceKind {
        None,
        Simple,
        Advanced
    }

    /// <summary>
    /// Réglages du plugin, stockés dans %LOCALAPPDATA%\NINA\AutoStart\autostart.json.
    /// Volontairement indépendants du profil NINA : ils doivent être lisibles dès l'ouverture de NINA.
    /// </summary>
    public class AutoStartSettings {
        public bool Enabled { get; set; }
        public string SequencePath { get; set; } = string.Empty;
        public string ProfileId { get; set; } = string.Empty;
        public string ProfileName { get; set; } = string.Empty;
        public bool ConnectEquipment { get; set; } = true;
        public int DelaySeconds { get; set; } = 30;

        public static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "AutoStart");

        private static string FilePath => Path.Combine(Folder, "autostart.json");

        public SequenceKind Kind => KindOf(SequencePath);

        /// <summary>Type de séquence déduit de l'extension : .json = avancée, .xml / .ninaTargetSet = simple.</summary>
        public static SequenceKind KindOf(string path) {
            if (string.IsNullOrWhiteSpace(path)) return SequenceKind.None;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch {
                ".json" => SequenceKind.Advanced,
                ".xml" => SequenceKind.Simple,
                ".ninatargetset" => SequenceKind.Simple,
                _ => SequenceKind.None
            };
        }

        public static AutoStartSettings Load() {
            try {
                if (File.Exists(FilePath)) {
                    var s = JsonSerializer.Deserialize<AutoStartSettings>(File.ReadAllText(FilePath));
                    if (s != null) {
                        if (s.DelaySeconds < 0) s.DelaySeconds = 0;
                        return s;
                    }
                }
            } catch (Exception ex) {
                NINA.Core.Utility.Logger.Error("AutoStart : lecture des réglages impossible", ex);
            }
            return new AutoStartSettings();
        }

        public void Save() {
            try {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            } catch (Exception ex) {
                NINA.Core.Utility.Logger.Error("AutoStart : enregistrement des réglages impossible", ex);
            }
        }
    }
}
