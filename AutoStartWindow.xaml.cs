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
        private readonly NinaServices nina;
        private readonly AutoStartRunner runner;

        public AutoStartWindow(AutoStartSettings settings, NinaServices nina, AutoStartRunner runner) {
            InitializeComponent();
            WindowTheme.Apply(this);
            this.settings = settings;
            this.nina = nina;
            this.profileService = nina.Profile;
            this.runner = runner;
            if (string.IsNullOrEmpty(settings.ProfileId)) CaptureActiveProfile();
            ExpirePastSchedule();
            DataContext = this;
            Loaded += async (s, e) => await VerifyScheduleAsync();
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

        public string EquipmentText {
            get {
                try {
                    return EquipmentCatalog.Summary(settings.EffectiveDevices(profileService.ActiveProfile))
                           + (settings.DevicesToConnect == null ? " (selon le profil)" : "");
                } catch {
                    return string.Empty;
                }
            }
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

        // --- Programmation de l'allumage ---

        private bool scheduleDirty;
        private string scheduleInfo = string.Empty;

        private static DateTime DefaultScheduleTime {
            get {
                var t = DateTime.Today.AddHours(21);
                return t > DateTime.Now ? t : t.AddDays(1);
            }
        }

        private DateTime WantedScheduleTime => settings.ScheduleOnTime ?? DefaultScheduleTime;

        /// <summary>ON si une programmation a été enregistrée dans la prise (et, pour un allumage unique, pas encore passée).</summary>
        public bool ScheduleOn => settings.ScheduleOnEnabled
                                  && (settings.ScheduleRepeat != ScheduleRepeat.Once || settings.ScheduleOnTime > DateTime.Now);

        public IReadOnlyList<KeyValuePair<ScheduleRepeat, string>> RepeatOptions { get; } = new[] {
            new KeyValuePair<ScheduleRepeat, string>(ScheduleRepeat.Once, "Une seule fois"),
            new KeyValuePair<ScheduleRepeat, string>(ScheduleRepeat.Daily, "Tous les jours"),
            new KeyValuePair<ScheduleRepeat, string>(ScheduleRepeat.Days, "Certains jours"),
        };

        public ScheduleRepeat ScheduleRepeat {
            get => settings.ScheduleRepeat;
            set {
                if (settings.ScheduleRepeat == value) return;
                settings.ScheduleRepeat = value;
                MarkScheduleChanged();
            }
        }

        public bool IsOnce => settings.ScheduleRepeat == ScheduleRepeat.Once;
        public bool IsSomeDays => settings.ScheduleRepeat == ScheduleRepeat.Days;

        private List<DayChoice> scheduleDayItems;

        /// <summary>Boutons L M M J V S D (indice DayOfWeek : 0 = dimanche).</summary>
        public List<DayChoice> ScheduleDayItems => scheduleDayItems ??= Enumerable.Range(0, 7)
            .Select(i => (DayOfWeek)((i + 1) % 7))
            .Select(d => new DayChoice(
                System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(d),
                () => settings.ScheduleDays != null && settings.ScheduleDays.Length == 7 && settings.ScheduleDays[(int)d],
                v => {
                    if (settings.ScheduleDays == null || settings.ScheduleDays.Length != 7) settings.ScheduleDays = new bool[7];
                    settings.ScheduleDays[(int)d] = v;
                    MarkScheduleChanged();
                }))
            .ToList();

        /// <summary>Prochaine date (aujourd'hui ou demain) à cette heure-là.</summary>
        private static DateTime NextAt(DateTime time) {
            var t = DateTime.Today + new TimeSpan(time.Hour, time.Minute, 0);
            return t > DateTime.Now.AddMinutes(1) ? t : t.AddDays(1);
        }

        /// <summary>
        /// Un allumage unique déjà passé : la prise l'a exécuté (ou l'heure est dépassée).
        /// On repasse sur OFF et on propose la même heure au prochain jour possible.
        /// </summary>
        private void ExpirePastSchedule() {
            if (settings.ScheduleRepeat != ScheduleRepeat.Once || settings.ScheduleOnTime == null) return;
            var t = settings.ScheduleOnTime.Value;
            if (t > DateTime.Now) return;
            if (settings.ScheduleOnEnabled) {
                scheduleInfo = $"Le dernier allumage programmé ({t:dd/MM/yyyy} à {t:HH\\:mm}) est passé.";
                settings.ScheduleOnEnabled = false;
            }
            settings.ScheduleOnTime = NextAt(t);
            settings.Save();
        }

        private void MarkScheduleChanged() {
            if (settings.ScheduleOnEnabled) scheduleDirty = true;
            Changed();
        }

        public DateTime? ScheduleDate {
            get => WantedScheduleTime.Date;
            set {
                if (value == null) return;
                SetWantedScheduleTime(value.Value.Date + WantedScheduleTime.TimeOfDay);
            }
        }

        public string ScheduleTimeText {
            get => WantedScheduleTime.ToString("HH:mm");
            set {
                var text = (value ?? string.Empty).Trim().Replace('h', ':').Replace('H', ':').Replace('.', ':');
                if (text.EndsWith(":")) text += "00";
                if (TimeSpan.TryParse(text, out var time) && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1)) {
                    SetWantedScheduleTime(WantedScheduleTime.Date + new TimeSpan(time.Hours, time.Minutes, 0));
                } else {
                    OnPropertyChanged(nameof(ScheduleTimeText));   // valeur invalide : on réaffiche l'ancienne
                }
            }
        }

        private void SetWantedScheduleTime(DateTime time) {
            if (settings.ScheduleOnTime == time) return;
            settings.ScheduleOnTime = time;
            MarkScheduleChanged();
        }

        public string ScheduleText {
            get {
                if (!string.IsNullOrEmpty(busyText)) return busyText;
                var t = WantedScheduleTime;
                if (settings.ScheduleOnEnabled && scheduleDirty) {
                    return "Date ou heure modifiée : clique sur « Programmer » pour l'enregistrer dans la prise.";
                }
                if (ScheduleOn) {
                    if (IsOnce) return $"Allumage programmé le {t:dddd dd/MM/yyyy} à {t:HH\\:mm} {scheduleInfo}".Trim();
                    var next = NextOccurrence(settings.WantedSchedule(t));
                    var nextText = next == null ? "" : $" — prochain : {next:dddd dd/MM} à {next:HH\\:mm}";
                    return $"Allumage programmé {KasaPlug.Describe(settings.WantedSchedule(t))}{nextText} {scheduleInfo}".Trim();
                }
                if (settings.ScheduleOnEnabled) {
                    return $"La programmation du {t:dd/MM/yyyy} à {t:HH\\:mm} est passée : choisis une nouvelle date ou heure.";
                }
                return string.IsNullOrEmpty(scheduleInfo) ? "Aucun allumage programmé." : scheduleInfo;
            }
        }

        private string busyText;

        private void SetBusy(string text) {
            busyText = text;
            ScheduleButton.IsEnabled = text == null;
            OnPropertyChanged(nameof(ScheduleText));
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

        private void Equipment_Click(object sender, RoutedEventArgs e) {
            var dialog = new EquipmentWindow(settings.EffectiveDevices(profileService.ActiveProfile), nina) { Owner = this };
            if (dialog.ShowDialog() == true) {
                settings.DevicesToConnect = dialog.Selected;
                Changed();
            }
        }

        // ------------------------------------------------------------ alimentation

        private async void ScheduleToggle_Click(object sender, RoutedEventArgs e) {
            if (ScheduleOn) {
                await CancelScheduleAsync();
            } else {
                await ApplyScheduleAsync();
            }
        }

        private async void ScheduleApply_Click(object sender, RoutedEventArgs e) => await ApplyScheduleAsync();

        private async Task ApplyScheduleAsync() {
            if (!IsValidAddress(settings.PlugAddress)) {
                scheduleInfo = "Renseigne d'abord l'adresse IP de la prise (ou utilise SCAN).";
                Changed();
                return;
            }
            var wanted = WantedScheduleTime;
            string moved = null;
            if (settings.ScheduleRepeat == ScheduleRepeat.Once && wanted <= DateTime.Now.AddMinutes(1)) {
                // Heure déjà passée : on prend la même heure au prochain jour possible plutôt que de refuser.
                wanted = NextAt(wanted);
                settings.ScheduleOnTime = wanted;
                moved = $"(l'heure demandée était passée : reportée au {wanted:dddd dd/MM})";
            }
            var schedule = settings.WantedSchedule(wanted);
            if (schedule.IsRepeating && !schedule.Days.Any(d => d)) {
                scheduleInfo = "Choisis au moins un jour.";
                Changed();
                return;
            }
            SetBusy("Enregistrement de la programmation dans la prise…");
            try {
                var result = await Task.Run(() => PowerManager.ScheduleOnAsync(settings, schedule));
                settings.ScheduleOnEnabled = true;
                settings.ScheduleOnTime = result.Schedule.Time;
                scheduleDirty = false;
                scheduleInfo = result.ClockOffset == TimeSpan.Zero
                    ? "(enregistré dans la prise)"
                    : $"(enregistré dans la prise ; son horloge a {FormatOffset(result.ClockOffset)} d'écart avec le PC, écart compensé)";
                if (moved != null) scheduleInfo = moved + " " + scheduleInfo;
            } catch (Exception ex) {
                settings.ScheduleOnEnabled = false;
                scheduleDirty = false;
                scheduleInfo = $"Programmation impossible : {ex.Message}";
            } finally {
                SetBusy(null);
                Changed();
            }
        }

        private async Task CancelScheduleAsync() {
            SetBusy("Suppression de la programmation dans la prise…");
            try {
                if (IsValidAddress(settings.PlugAddress)) {
                    await Task.Run(() => PowerManager.CancelScheduleAsync(settings));
                }
                scheduleInfo = "Programmation supprimée.";
            } catch (Exception ex) {
                scheduleInfo = $"⚠ La prise n'a pas pu être jointe ({ex.Message}) : la programmation y est peut-être encore enregistrée.";
            } finally {
                settings.ScheduleOnEnabled = false;
                scheduleDirty = false;
                SetBusy(null);
                Changed();
            }
        }

        /// <summary>À l'ouverture : vérifie que la prise a toujours la programmation (elle peut avoir été réinitialisée).</summary>
        private async Task VerifyScheduleAsync() {
            if (!ScheduleOn || !IsValidAddress(settings.PlugAddress)) return;
            try {
                var inPlug = await Task.Run(() => PowerManager.GetScheduleAsync(settings));
                if (inPlug == null) {
                    scheduleInfo = "⚠ introuvable dans la prise : clique sur « Programmer » pour l'enregistrer à nouveau.";
                } else if (!SameSchedule(inPlug, settings.WantedSchedule(WantedScheduleTime))) {
                    scheduleInfo = $"⚠ la prise indique {KasaPlug.Describe(inPlug)} : clique sur « Programmer » pour corriger.";
                } else {
                    scheduleInfo = "(confirmé par la prise)";
                }
            } catch {
                scheduleInfo = "(prise injoignable, programmation non vérifiée)";
            }
            OnPropertyChanged(nameof(ScheduleText));
        }

        private static bool SameSchedule(PlugSchedule a, PlugSchedule b) {
            if (a.IsRepeating != b.IsRepeating) return false;
            if (!a.IsRepeating) return Math.Abs((a.Time - b.Time).TotalMinutes) <= 1;
            return Math.Abs((a.Time.TimeOfDay - b.Time.TimeOfDay).TotalMinutes) <= 1 && a.Days.SequenceEqual(b.Days);
        }

        private static DateTime? NextOccurrence(PlugSchedule s) {
            if (!s.IsRepeating) return s.Time;
            for (int i = 0; i < 8; i++) {
                var day = DateTime.Today.AddDays(i);
                var when = day + new TimeSpan(s.Time.Hour, s.Time.Minute, 0);
                if (s.Days[(int)day.DayOfWeek] && when > DateTime.Now) return when;
            }
            return null;
        }

        private static string FormatOffset(TimeSpan offset) {
            var abs = offset.Duration();
            return abs.TotalHours >= 1 ? $"{(int)abs.TotalHours} h {abs.Minutes:00}" : $"{abs.Minutes} min";
        }

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

    /// <summary>Un jour de la semaine dans la programmation répétée.</summary>
    public class DayChoice : INotifyPropertyChanged {
        private readonly Func<bool> read;
        private readonly Action<bool> write;

        public DayChoice(string name, Func<bool> get, Action<bool> set) {
            Name = name;
            Label = name.Substring(0, 1).ToUpperInvariant();
            read = get;
            write = set;
        }

        public string Name { get; }
        public string Label { get; }

        public bool IsSelected {
            get => read();
            set { write(value); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
