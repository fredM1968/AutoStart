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

        // --- Alimentation du matériel par prise connectée ---
        public bool PowerEnabled { get; set; }
        public PlugBrand PlugBrand { get; set; } = PlugBrand.TpLinkKasa;
        public string PlugAddress { get; set; } = string.Empty;
        /// <summary>Allume la prise au lancement automatique, avant la connexion du matériel.</summary>
        public bool PowerOnAtStartup { get; set; } = true;
        /// <summary>Attente après l'allumage pour que les appareils USB apparaissent dans Windows.</summary>
        public int PowerOnWaitSeconds { get; set; } = 20;
        /// <summary>Compte à rebours (annulable) entre la fin de la séquence et la coupure.</summary>
        public int PowerOffDelaySeconds { get; set; } = 60;
        public bool DisconnectBeforePowerOff { get; set; } = true;
        /// <summary>Ne coupe pas si la séquence a été arrêtée à la main.</summary>
        public bool PowerOffOnlyIfCompleted { get; set; } = true;

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
                        if (s.PowerOffDelaySeconds < 0) s.PowerOffDelaySeconds = 0;
                        if (s.PowerOnWaitSeconds < 0) s.PowerOnWaitSeconds = 0;
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
