using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using System;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Plugin.AutoStart {

    /// <summary>
    /// Point d'entrée du plugin. NINA le crée au chargement des plugins, avant que le séquenceur soit prêt :
    /// si NINA a été ouvert par le démarrage de Windows, on lance la procédure en tâche de fond.
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class AutoStartPlugin : PluginBase {
        private readonly NinaServices nina;
        private readonly AutoStartRunner runner;
        private readonly PowerManager power;
        private AutoStartWindow window;

        [ImportingConstructor]
        public AutoStartPlugin(IProfileService profileService, ISequenceMediator sequenceMediator, IApplicationMediator applicationMediator,
                               ICameraMediator cameraMediator, IFilterWheelMediator filterWheelMediator, ITelescopeMediator telescopeMediator,
                               IFocuserMediator focuserMediator, IRotatorMediator rotatorMediator, IGuiderMediator guiderMediator,
                               IFlatDeviceMediator flatDeviceMediator, IWeatherDataMediator weatherDataMediator, ISwitchMediator switchMediator,
                               IDomeMediator domeMediator, ISafetyMonitorMediator safetyMonitorMediator) {
            nina = new NinaServices {
                Profile = profileService,
                Sequence = sequenceMediator,
                Application = applicationMediator,
                Camera = cameraMediator,
                FilterWheel = filterWheelMediator,
                Telescope = telescopeMediator,
                Focuser = focuserMediator,
                Rotator = rotatorMediator,
                Guider = guiderMediator,
                FlatDevice = flatDeviceMediator,
                Weather = weatherDataMediator,
                Switch = switchMediator,
                Dome = domeMediator,
                SafetyMonitor = safetyMonitorMediator
            };
            runner = new AutoStartRunner(nina);
            power = new PowerManager(nina, runner.IsAnySequenceRunning);
            runner.Power = power;
            power.AttachToSequencer();

            var settings = AutoStartSettings.Load();
            bool startedByWindows = WindowsStartup.ConsumeStartupFlag();

            try {
                // Rafraîchit l'entrée de démarrage : le chemin de NINA.exe peut changer après une mise à jour de NINA.
                if (settings.Enabled && WindowsStartup.IsRegistered()) {
                    WindowsStartup.Register(settings.ProfileId);
                }
            } catch (Exception ex) {
                Logger.Error("AutoStart : mise à jour de l'entrée de démarrage impossible", ex);
            }

            if (startedByWindows) {
                if (settings.Enabled) {
                    Logger.Info("AutoStart : NINA a été ouvert par le démarrage de Windows");
                    runner.Start(settings, isTest: false);
                } else {
                    Logger.Info("AutoStart : NINA ouvert par le démarrage de Windows mais AutoStart est sur OFF");
                }
            }
        }

        /// <summary>Ouvre (ou ramène au premier plan) la fenêtre AutoStart.</summary>
        public void OpenWindow() {
            Application.Current.Dispatcher.Invoke(() => {
                if (window != null) {
                    window.Activate();
                    return;
                }
                window = new AutoStartWindow(AutoStartSettings.Load(), nina.Profile, runner);
                if (Application.Current.MainWindow is Window main && main.IsLoaded) window.Owner = main;
                window.Closed += (s, e) => window = null;
                window.Show();
            });
        }

        public override Task Teardown() {
            Application.Current?.Dispatcher.Invoke(() => window?.Close());
            return base.Teardown();
        }
    }
}
