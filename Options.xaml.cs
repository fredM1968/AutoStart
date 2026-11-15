using System.ComponentModel.Composition;
using System.Windows;

namespace NINA.Plugin.AutoStart {

    [Export(typeof(ResourceDictionary))]
    public partial class Options : ResourceDictionary {

        public Options() {
            InitializeComponent();
        }

        private void OpenWindow_Click(object sender, RoutedEventArgs e) {
            if ((sender as FrameworkElement)?.DataContext is AutoStartPlugin plugin) {
                plugin.OpenWindow();
            }
        }
    }
}
