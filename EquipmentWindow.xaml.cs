using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

namespace NINA.Plugin.AutoStart {

    public class EquipmentChoice : INotifyPropertyChanged {
        private bool isSelected;
        public string Key { get; init; }
        public string Label { get; init; }
        public string Detail { get; init; }

        public bool IsSelected {
            get => isSelected;
            set { isSelected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Choix des équipements que AutoStart connecte avant la séquence.</summary>
    public partial class EquipmentWindow : Window {
        private readonly NinaServices nina;

        public List<EquipmentChoice> Items { get; }

        /// <summary>Résultat après OK.</summary>
        public List<string> Selected => Items.Where(i => i.IsSelected).Select(i => i.Key).ToList();

        public EquipmentWindow(IEnumerable<string> current, NinaServices nina) {
            InitializeComponent();
            WindowTheme.Apply(this);
            this.nina = nina;
            var profile = nina.Profile.ActiveProfile;
            var selected = current.ToHashSet();
            Items = EquipmentCatalog.All.Select(e => {
                var device = EquipmentCatalog.DeviceInProfile(e, profile);
                return new EquipmentChoice {
                    Key = e.Key,
                    Label = e.Label,
                    Detail = device == null ? "(aucun appareil dans le profil)" : $"({device})",
                    IsSelected = selected.Contains(e.Key)
                };
            }).ToList();
            DataContext = this;
        }

        private void Apply(ICollection<string> keys) {
            foreach (var item in Items) item.IsSelected = keys.Contains(item.Key);
        }

        private void Profile_Click(object sender, RoutedEventArgs e) =>
            Apply(EquipmentCatalog.FromProfile(nina.Profile.ActiveProfile));

        private void Connected_Click(object sender, RoutedEventArgs e) {
            var connected = EquipmentCatalog.CurrentlyConnected(nina);
            if (connected.Count == 0) {
                MessageBox.Show(this, "Aucun équipement n'est connecté dans NINA pour le moment.", "AutoStart",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Apply(connected);
        }

        private void Ok_Click(object sender, RoutedEventArgs e) {
            DialogResult = true;
        }
    }
}
