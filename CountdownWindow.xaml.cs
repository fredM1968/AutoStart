using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace NINA.Plugin.AutoStart {

    /// <summary>Compte à rebours avant le lancement : "Annuler" arrête tout, "Lancer maintenant" écourte l'attente.</summary>
    public partial class CountdownWindow : Window {
        private readonly TaskCompletionSource<bool> result = new();
        private readonly DispatcherTimer timer;
        private readonly int total;
        private int remaining;

        private CountdownWindow(string sequenceName, int seconds, bool connect) {
            InitializeComponent();
            WindowTheme.Apply(this);
            total = Math.Max(1, seconds);
            remaining = total;
            SequenceText.Text = $"La séquence « {sequenceName} » va démarrer.";
            ConnectText.Text = connect ? "Le matériel sera connecté juste avant." : "Le matériel doit déjà être connecté.";
            Progress.Maximum = total;
            Refresh();

            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => {
                remaining--;
                Refresh();
                if (remaining <= 0) Finish(true);
            };
            Closed += (s, e) => Finish(false);
            timer.Start();
        }

        /// <summary>À appeler sur le fil de l'interface. Renvoie true pour lancer, false si annulé.</summary>
        public static Task<bool> ShowAsync(string sequenceName, int seconds, bool connect) {
            var w = new CountdownWindow(sequenceName, seconds, connect);
            if (Application.Current?.MainWindow is Window main && main.IsLoaded && main != w) {
                w.Owner = main;
                w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            w.Show();
            w.Activate();
            return w.result.Task;
        }

        private void Refresh() {
            CountText.Text = $"{remaining} s";
            Progress.Value = total - remaining;
        }

        private void Finish(bool go) {
            timer.Stop();
            if (result.TrySetResult(go) && IsLoaded) Close();
        }

        private void Now_Click(object sender, RoutedEventArgs e) => Finish(true);

        private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(false);
    }
}
