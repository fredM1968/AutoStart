using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace NINA.Plugin.AutoStart {

    /// <summary>
    /// Compte à rebours annulable, utilisé avant le lancement de la séquence et avant la coupure de l'alimentation.
    /// "Annuler" arrête tout, le bouton d'action écourte l'attente.
    /// </summary>
    public partial class CountdownWindow : Window {
        private readonly TaskCompletionSource<bool> result = new();
        private readonly DispatcherTimer timer;
        private readonly int total;
        private int remaining;

        private CountdownWindow(string title, string message, string detail, string actionLabel, int seconds) {
            InitializeComponent();
            WindowTheme.Apply(this);
            total = Math.Max(1, seconds);
            remaining = total;
            TitleText.Text = title;
            SequenceText.Text = message;
            ConnectText.Text = detail;
            NowButton.Content = actionLabel;
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

        /// <summary>À appeler sur le fil de l'interface. Renvoie true pour continuer, false si annulé.</summary>
        public static Task<bool> ShowAsync(string title, string message, string detail, string actionLabel, int seconds) {
            var w = new CountdownWindow(title, message, detail, actionLabel, seconds);
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
