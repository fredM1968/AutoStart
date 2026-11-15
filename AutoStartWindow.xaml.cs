using NINA.Profile.Interfaces;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

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

        public string StatusText {
            get {
                if (!IsOn) return "Désactivé : NINA ne se lancera pas tout seul au démarrage du PC.";
                var seq = settings.Kind == SequenceKind.None
                    ? "sans lancer de séquence"
                    : $"puis lancera « {Path.GetFileName(settings.SequencePath)} »"
                      + (settings.DelaySeconds > 0 ? $" après {settings.DelaySeconds} s de compte à rebours" : " immédiatement");
                var warn = settings.Kind != SequenceKind.None && !File.Exists(settings.SequencePath)
                    ? "\n⚠ Le fichier de séquence est introuvable."
                    : string.Empty;
                return $"Activé : NINA s'ouvrira à l'ouverture de session Windows {seq}.{warn}\n" +
                       "Sur un PC sans écran, active l'ouverture de session automatique de Windows.";
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
