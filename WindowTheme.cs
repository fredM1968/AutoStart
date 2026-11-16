using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace NINA.Plugin.AutoStart {
    /// <summary>
    /// Barre de titre sombre pour les fenêtres du plugin (comme NINA). Windows 10 (1809+) et 11 : mode sombre de la barre de titre ;
    /// Windows 11 : en plus, couleur de barre, de texte et de bordure assorties. Sans effet (et sans erreur) sur les autres versions.
    /// </summary>
    public static class WindowTheme {
        private const int DwmUseImmersiveDarkModeOld = 19;   // Windows 10 1809 à 1903
        private const int DwmUseImmersiveDarkMode = 20;
        private const int DwmBorderColor = 34;               // Windows 11
        private const int DwmCaptionColor = 35;
        private const int DwmTextColor = 36;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private static int ColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

        /// <summary>
        /// Le modèle de bouton de NINA ignore Padding et ContentTemplate : le texte touche les bords.
        /// On remplace donc le texte de chaque bouton par un TextBlock avec une marge intérieure.
        /// </summary>
        private static void PadButtons(DependencyObject parent) {
            foreach (var child in LogicalTreeHelper.GetChildren(parent)) {
                if (child is not DependencyObject d) continue;
                if (d is Button b) {
                    if (b.Content is string text) {
                        b.Content = new TextBlock {
                            Text = text,
                            Margin = new Thickness(14, 4, 14, 4),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                    }
                    if (b.VerticalAlignment == VerticalAlignment.Stretch) b.VerticalAlignment = VerticalAlignment.Center;
                    continue;
                }
                PadButtons(d);
            }
        }

        public static void Apply(Window window) {
            window.Loaded += (s, e) => PadButtons(window);
            window.SourceInitialized += (s, e) => {
                try {
                    IntPtr hwnd = new WindowInteropHelper(window).Handle;
                    if (hwnd == IntPtr.Zero) return;
                    int on = 1;
                    if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref on, sizeof(int)) != 0) {
                        DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeOld, ref on, sizeof(int));
                    }
                    int caption = ColorRef(38, 48, 51);
                    int border = ColorRef(60, 74, 78);
                    int text = ColorRef(232, 232, 232);
                    DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref caption, sizeof(int));
                    DwmSetWindowAttribute(hwnd, DwmBorderColor, ref border, sizeof(int));
                    DwmSetWindowAttribute(hwnd, DwmTextColor, ref text, sizeof(int));
                } catch (Exception) {
                    // fonction indisponible : la barre de titre reste celle de Windows
                }
            };
        }
    }
}
