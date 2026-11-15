using NINA.Profile.Interfaces;
using System;
using System.ComponentModel;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace NINA.Plugin.AutoStart {

    public partial class AutoStartWindow : Window, INotifyPropertyChanged {
        private readonly AutoStartSettings settings;
        private readonly IProfileService profileService;
        private readonly AutoStartRunner runner;

        public AutoStartWindow(AutoStartSettings settings, IProfileService profileService, AutoStartRunner runner) {
            InitializeComponent();
            WindowTheme.Apply(this);
            this.settings = settings;
            this.profileService = profileService;
            this.runner = runner;
            if (string.IsNullOrEmpty(settings.ProfileId)) CaptureActiveProfile();
            DataContext = this;
        }

        // ------------------------------------------------------------ propriétés affichées

        /// <summary>ON seulement si le réglage est actif ET l'entrée de démarrage Windows existe encore.</summary>
        public bool IsOn => settings.Enabled && WindowsStartup.IsRegistered();

        public string SequencePath => string.IsNullOrEmpty(settings.SequencePath) ? "(aucune séquence)" : settings.SequencePath;

        public string SequenceKindText => settings.Kind switch {
            SequenceKind.Advanced => "Type : séquence avancée",
            SequenceKind.Simple when settings.SequencePath.EndsWith(".ninaTargetSet", StringComparison.OrdinalIgnoreCase)
                => "Type : séquence simple (ensemble de cibles)",
            SequenceKind.Simple => "Type : séquence simple (une cible)",
            _ => "Sans séquence, NINA s'ouvrira simplement au démarrage."
        };

        public string ProfileText => $"Profil NINA : {(string.IsNullOrEmpty(settings.ProfileName) ? "(profil par défaut)" : settings.ProfileName)}";

        public bool ConnectEquipment {
            get => settings.ConnectEquipment;
            set { settings.ConnectEquipment = value; Changed(); }
        }

        public string DelayText {
            get => settings.DelaySeconds.ToString();
            set {
                if (int.TryParse(value, out var s) && s >= 0 && s <= 3600) {
                    settings.DelaySeconds = s;
                    Changed();
                }
            }
        }

        // --- Alimentation ---

        public bool PowerEnabled => settings.PowerEnabled;

        public IReadOnlyList<PlugBrandItem> Brands => SmartPlugFactory.Brands;

        public PlugBrand PlugBrand {
            get => settings.PlugBrand;
            set { settings.PlugBrand = value; Changed(); }
        }

        public string PlugAddress {
            get => settings.PlugAddress;
            set { settings.PlugAddress = (value ?? string.Empty).Trim(); Changed(); }
        }

        public bool PowerOnAtStartup {
            get => settings.PowerOnAtStartup;
            set { settings.PowerOnAtStartup = value; Changed(); }
        }

        public bool DisconnectBeforePowerOff {
            get => settings.DisconnectBeforePowerOff;
            set { settings.DisconnectBeforePowerOff = value; Changed(); }
        }

        public bool PowerOffOnlyIfCompleted {
            get => settings.PowerOffOnlyIfCompleted;
            set { settings.PowerOffOnlyIfCompleted = value; Changed(); }
        }

        public string PowerOffDelayText {
            get => settings.PowerOffDelaySeconds.ToString();
            set {
                if (int.TryParse(value, out var s) && s >= 0 && s <= 3600) {
                    settings.PowerOffDelaySeconds = s;
                    Changed();
                }
            }
        }

        private string plugStateText = "État de la prise : inconnu";
        public string PlugStateText {
            get => plugStateText;
            private set { plugStateText = value; OnPropertyChanged(); }
        }

        public string StatusText {
            get {
                var power = !settings.PowerEnabled
                    ? string.Empty
                    : string.IsNullOrWhiteSpace(settings.PlugAddress)
                        ? "\n⚠ Alimentation : aucune adresse IP de prise renseignée."
                        : $"\nAlimentation : coupure de la prise {settings.PlugAddress} à la fin de chaque séquence"
                          + (settings.PowerOffDelaySeconds > 0 ? $" (après {settings.PowerOffDelaySeconds} s)." : ".");
                if (!IsOn) return "Désactivé : NINA ne se lancera pas tout seul au démarrage du PC." + power;
                var seq = settings.Kind == SequenceKind.None
                    ? "sans lancer de séquence"
                    : $"puis lancera « {Path.GetFileName(settings.SequencePath)} »"
                      + (settings.DelaySeconds > 0 ? $" après {settings.DelaySeconds} s de compte à rebours" : " immédiatement");
                var warn = settings.Kind != SequenceKind.None && !File.Exists(settings.SequencePath)
                    ? "\n⚠ Le fichier de séquence est introuvable."
                    : string.Empty;
                return $"Activé : NINA s'ouvrira à l'ouverture de session Windows {seq}.{warn}\n" +
                       "Sur un PC sans écran, active l'ouverture de session automatique de Windows." + power;
            }
        }

        // ------------------------------------------------------------ actions

        private void Toggle_Click(object sender, RoutedEventArgs e) {
            try {
                if (IsOn) {
                    WindowsStartup.Unregister();
                    settings.Enabled = false;
                } else {
                    if (string.IsNullOrEmpty(settings.ProfileId)) CaptureActiveProfile();
                    WindowsStartup.Register(settings.ProfileId);
                    settings.Enabled = true;
                }
            } catch (Exception ex) {
                settings.Enabled = false;
                MessageBox.Show(this, $"Impossible de modifier le démarrage de Windows :\n{ex.Message}", "AutoStart",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Changed();
        }

        private void Choose_Click(object sender, RoutedEventArgs e) {
            var dialog = new Microsoft.Win32.OpenFileDialog {
                Title = "Choisir la séquence à lancer au démarrage",
                Filter = "Toutes les séquences NINA|*.json;*.xml;*.ninaTargetSet" +
                         "|Séquence avancée (*.json)|*.json" +
                         "|Séquence simple – une cible (*.xml)|*.xml" +
                         "|Séquence simple – ensemble de cibles (*.ninaTargetSet)|*.ninaTargetSet",
                Multiselect = false
            };
            if (!string.IsNullOrEmpty(settings.SequencePath) && File.Exists(settings.SequencePath)) {
                dialog.InitialDirectory = Path.GetDirectoryName(settings.SequencePath);
                dialog.FileName = Path.GetFileName(settings.SequencePath);
            } else {
                try {
                    var folder = profileService.ActiveProfile.SequenceSettings.DefaultSequenceFolder;
                    if (Directory.Exists(folder)) dialog.InitialDirectory = Path.GetFullPath(folder);
                } catch { }
            }

            if (dialog.ShowDialog(this) == true) {
                if (AutoStartSettings.KindOf(dialog.FileName) == SequenceKind.None) {
                    MessageBox.Show(this, "Ce type de fichier n'est pas une séquence NINA.", "AutoStart", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                settings.SequencePath = dialog.FileName;
                Changed();
            }
        }

        private void Clear_Click(object sender, RoutedEventArgs e) {
            settings.SequencePath = string.Empty;
            Changed();
        }

        private void Profile_Click(object sender, RoutedEventArgs e) {
            CaptureActiveProfile();
            if (IsOn) {
                try { WindowsStartup.Register(settings.ProfileId); } catch { }
            }
            Changed();
        }

        private void Test_Click(object sender, RoutedEventArgs e) {
            if (runner.IsRunning) {
                MessageBox.Show(this, "Une procédure AutoStart est déjà en cours.", "AutoStart", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (settings.Kind == SequenceKind.None) {
                MessageBox.Show(this, "Choisis d'abord une séquence.", "AutoStart", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            settings.Save();
            Close();
            runner.Start(settings, isTest: true);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        // ------------------------------------------------------------ alimentation

        private void PowerToggle_Click(object sender, RoutedEventArgs e) {
            if (!settings.PowerEnabled && !IsValidAddress(settings.PlugAddress)) {
                MessageBox.Show(this, "Renseigne d'abord l'adresse IP de la prise (ou utilise SCAN).", "AutoStart",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                OnPropertyChanged(nameof(PowerEnabled));
                return;
            }
            settings.PowerEnabled = !settings.PowerEnabled;
            Changed();
        }

        private async void Scan_Click(object sender, RoutedEventArgs e) {
            ScanButton.IsEnabled = false;
            FoundList.Visibility = Visibility.Collapsed;
            try {
                var progress = new Progress<string>(m => PlugStateText = m);
                var found = await Task.Run(() => SmartPlugFactory.DiscoverAsync(settings.PlugBrand, progress, CancellationToken.None));
                if (found.Count == 0) {
                    PlugStateText = "Aucune prise trouvée. Vérifie qu'elle est sur le même réseau que ce PC.";
                } else if (found.Count == 1) {
                    PlugAddress = found[0].Address;
                    PlugStateText = $"Trouvée : {found[0]}";
                } else {
                    FoundList.ItemsSource = found;
                    FoundList.Visibility = Visibility.Visible;
                    var current = found.FirstOrDefault(f => f.Address == settings.PlugAddress);
                    if (current != null) FoundList.SelectedItem = current;
                    PlugStateText = $"{found.Count} prises trouvées : choisis la bonne dans la liste.";
                }
            } catch (Exception ex) {
                PlugStateText = $"Recherche impossible : {ex.Message}";
            } finally {
                ScanButton.IsEnabled = true;
            }
        }

        private void FoundList_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            if (FoundList.SelectedItem is FoundPlug plug) {
                PlugAddress = plug.Address;
                PlugStateText = $"Choisie : {plug}";
            }
        }

        private void On_Click(object sender, RoutedEventArgs e) => _ = TestPowerAsync(true);

        private void Off_Click(object sender, RoutedEventArgs e) => _ = TestPowerAsync(false);

        private async Task TestPowerAsync(bool on) {
            if (!IsValidAddress(settings.PlugAddress)) {
                PlugStateText = "Adresse IP invalide.";
                return;
            }
            if (!on && MessageBox.Show(this, "Couper maintenant l'alimentation du matériel ?\nDéconnecte-le d'abord dans NINA s'il est connecté.",
                                       "AutoStart", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) {
                return;
            }
            OnButton.IsEnabled = OffButton.IsEnabled = false;
            try {
                PlugStateText = on ? "Allumage…" : "Extinction…";
                await Task.Run(() => PowerManager.SetPowerAsync(settings, on));
                var state = await Task.Run(() => PowerManager.GetPowerAsync(settings));
                PlugStateText = $"État de la prise : {(state ? "allumée" : "éteinte")}";
            } catch (Exception ex) {
                PlugStateText = $"Erreur : {ex.Message}";
            } finally {
                OnButton.IsEnabled = OffButton.IsEnabled = true;
            }
        }

        private static bool IsValidAddress(string address) =>
            !string.IsNullOrWhiteSpace(address) && IPAddress.TryParse(address.Trim(), out var ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

        // ------------------------------------------------------------ utilitaires

        private void CaptureActiveProfile() {
            try {
                var p = profileService.ActiveProfile;
                settings.ProfileId = p.Id.ToString();
                settings.ProfileName = p.Name;
            } catch { }
        }

        /// <summary>Enregistre et rafraîchit tout l'affichage.</summary>
        private void Changed() {
            settings.Save();
            OnPropertyChanged(string.Empty);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
