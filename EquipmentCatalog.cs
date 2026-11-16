using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Plugin.AutoStart {

    /// <summary>Un type d'équipement NINA : comment savoir s'il est connecté, le connecter, et quel appareil le profil a choisi.</summary>
    public record EquipmentItem(
        string Key,
        string Label,
        Func<NinaServices, bool> IsConnected,
        Func<NinaServices, Task<bool>> Connect,
        Func<IProfile, string> ProfileDevice);

    /// <summary>Liste des équipements, dans l'ordre de connexion du bouton « Tout connecter » de NINA.</summary>
    public static class EquipmentCatalog {

        public static IReadOnlyList<EquipmentItem> All { get; } = new[] {
            new EquipmentItem("Camera", "Caméra", n => n.Camera.GetInfo().Connected, n => n.Camera.Connect(), p => p.CameraSettings.Id),
            new EquipmentItem("FilterWheel", "Roue à filtres", n => n.FilterWheel.GetInfo().Connected, n => n.FilterWheel.Connect(), p => p.FilterWheelSettings.Id),
            new EquipmentItem("Telescope", "Monture", n => n.Telescope.GetInfo().Connected, n => n.Telescope.Connect(), p => p.TelescopeSettings.Id),
            new EquipmentItem("Focuser", "Focuser", n => n.Focuser.GetInfo().Connected, n => n.Focuser.Connect(), p => p.FocuserSettings.Id),
            new EquipmentItem("Rotator", "Rotateur", n => n.Rotator.GetInfo().Connected, n => n.Rotator.Connect(), p => p.RotatorSettings.Id),
            new EquipmentItem("Guider", "Guidage", n => n.Guider.GetInfo().Connected, n => n.Guider.Connect(), p => p.GuiderSettings.GuiderName),
            new EquipmentItem("FlatDevice", "Panneau de flats", n => n.FlatDevice.GetInfo().Connected, n => n.FlatDevice.Connect(), p => p.FlatDeviceSettings.Id),
            new EquipmentItem("Weather", "Météo", n => n.Weather.GetInfo().Connected, n => n.Weather.Connect(), p => p.WeatherDataSettings.Id),
            new EquipmentItem("Switch", "Switch", n => n.Switch.GetInfo().Connected, n => n.Switch.Connect(), p => p.SwitchSettings.Id),
            new EquipmentItem("Dome", "Dôme", n => n.Dome.GetInfo().Connected, n => n.Dome.Connect(), p => p.DomeSettings.Id),
            new EquipmentItem("SafetyMonitor", "Moniteur de sécurité", n => n.SafetyMonitor.GetInfo().Connected, n => n.SafetyMonitor.Connect(), p => p.SafetyMonitorSettings.Id),
        };

        /// <summary>Appareil choisi dans le profil pour ce type, ou null s'il n'y en a pas.</summary>
        public static string DeviceInProfile(EquipmentItem item, IProfile profile) {
            try {
                var id = item.ProfileDevice(profile);
                if (string.IsNullOrWhiteSpace(id) || id == "No_Device" || id == "No_Guider") return null;
                return id;
            } catch {
                return null;
            }
        }

        /// <summary>Choix par défaut : les types pour lesquels le profil a un appareil.</summary>
        public static List<string> FromProfile(IProfile profile) =>
            All.Where(e => DeviceInProfile(e, profile) != null).Select(e => e.Key).ToList();

        /// <summary>Les équipements connectés en ce moment dans NINA.</summary>
        public static List<string> CurrentlyConnected(NinaServices nina) =>
            All.Where(e => { try { return e.IsConnected(nina); } catch { return false; } }).Select(e => e.Key).ToList();

        public static string Summary(IEnumerable<string> keys) {
            var labels = All.Where(e => keys.Contains(e.Key)).Select(e => e.Label).ToList();
            return labels.Count == 0 ? "aucun équipement" : string.Join(", ", labels);
        }
    }
}
