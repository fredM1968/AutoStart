using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;

namespace NINA.Plugin.AutoStart {

    /// <summary>
    /// Inscription de NINA au démarrage de Windows (clé Run de l'utilisateur, aucun droit administrateur requis).
    ///
    /// La commande inscrite passe par cmd.exe pour poser la variable d'environnement NINA_AUTOSTART=1 :
    /// c'est elle qui permet au plugin de savoir que NINA a été ouvert par le démarrage du PC
    /// (une ouverture manuelle de NINA ne lance donc jamais la séquence).
    /// On ne peut pas passer un argument inventé à NINA : il refuse de démarrer s'il ne reconnaît pas un argument.
    /// </summary>
    public static class WindowsStartup {
        public const string EnvironmentFlag = "NINA_AUTOSTART";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "NINA AutoStart";

        /// <summary>Chemin de NINA.exe en cours d'exécution.</summary>
        public static string NinaExecutable {
            get {
                try {
                    var path = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path)) return path;
                } catch { }
                return Path.Combine(AppContext.BaseDirectory, "NINA.exe");
            }
        }

        public static string BuildCommand(string profileId) {
            var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var args = string.IsNullOrWhiteSpace(profileId) ? string.Empty : $" --profileid {profileId}";
            // Pas d'espace avant "&&" sinon la variable vaudrait "1 ".
            return $"\"{cmd}\" /c set {EnvironmentFlag}=1&& start \"\" \"{NinaExecutable}\"{args}";
        }

        public static bool IsRegistered() {
            try {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                return key?.GetValue(ValueName) is string;
            } catch {
                return false;
            }
        }

        public static void Register(string profileId) {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            key.SetValue(ValueName, BuildCommand(profileId), RegistryValueKind.String);
            NINA.Core.Utility.Logger.Info($"AutoStart : inscription au démarrage de Windows ({BuildCommand(profileId)})");
        }

        public static void Unregister() {
            try {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (key?.GetValue(ValueName) != null) {
                    key.DeleteValue(ValueName, false);
                    NINA.Core.Utility.Logger.Info("AutoStart : désinscription du démarrage de Windows");
                }
            } catch (Exception ex) {
                NINA.Core.Utility.Logger.Error("AutoStart : désinscription impossible", ex);
            }
        }

        /// <summary>
        /// Lit puis efface le drapeau : ainsi un redémarrage de NINA (installation de plugin, etc.)
        /// lancé depuis cette instance n'en hérite pas et ne relance pas la séquence.
        /// </summary>
        public static bool ConsumeStartupFlag() {
            var value = Environment.GetEnvironmentVariable(EnvironmentFlag);
            Environment.SetEnvironmentVariable(EnvironmentFlag, null);
            return value == "1";
        }
    }
}
