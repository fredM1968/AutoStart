using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugin.AutoStart {

    /// <summary>
    /// Alimentation du matériel par prise connectée :
    /// - allumage au lancement automatique (avant la connexion du matériel) ;
    /// - coupure à la fin de toute séquence, simple ou avancée (événement SequenceFinished de NINA).
    /// </summary>
    public class PowerManager {
        private readonly NinaServices nina;
        private readonly Func<bool> isSequenceRunning;
        private int busy;

        public PowerManager(NinaServices nina, Func<bool> isSequenceRunning) {
            this.nina = nina;
            this.isSequenceRunning = isSequenceRunning;
        }

        // ---------------------------------------------------------------- commandes de base

        public static async Task SetPowerAsync(AutoStartSettings settings, bool on, CancellationToken ct = default) {
            var plug = SmartPlugFactory.Create(settings.PlugBrand, settings.PlugAddress);
            Exception last = null;
            for (int attempt = 1; attempt <= 3; attempt++) {           // le Wi-Fi peut rater un échange
                try {
                    await plug.SetStateAsync(on, ct);
                    Logger.Info($"AutoStart : prise {plug.Address} {(on ? "allumée" : "éteinte")}");
                    return;
                } catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested) {
                    last = ex;
                    Logger.Warning($"AutoStart : essai {attempt} sur la prise {plug.Address} raté : {ex.Message}");
                    await Task.Delay(3000, ct);
                }
            }
            throw last ?? new InvalidOperationException("prise injoignable");
        }

        public static Task<bool> GetPowerAsync(AutoStartSettings settings, CancellationToken ct = default) =>
            SmartPlugFactory.Create(settings.PlugBrand, settings.PlugAddress).GetStateAsync(ct);

        // ---------------------------------------------------------------- allumage au démarrage

        public async Task PowerOnForStartupAsync(AutoStartSettings settings) {
            if (!settings.PowerEnabled || !settings.PowerOnAtStartup) return;
            try {
                if (await GetPowerAsync(settings)) {
                    Logger.Info("AutoStart : la prise est déjà allumée");
                    return;
                }
                Notification.ShowInformation("AutoStart : mise sous tension du matériel…");
                await SetPowerAsync(settings, true);
                if (settings.PowerOnWaitSeconds > 0) {
                    Logger.Info($"AutoStart : attente de {settings.PowerOnWaitSeconds} s après la mise sous tension");
                    await Task.Delay(TimeSpan.FromSeconds(settings.PowerOnWaitSeconds));
                }
            } catch (Exception ex) {
                Logger.Error("AutoStart : mise sous tension impossible", ex);
                Notification.ShowWarning($"AutoStart : impossible d'allumer la prise ({ex.Message}). Le lancement continue.");
            }
        }

        // ---------------------------------------------------------------- coupure en fin de séquence

        /// <summary>S'abonne à la fin des séquences dès que le séquenceur de NINA est prêt.</summary>
        public void AttachToSequencer() {
            _ = Task.Run(async () => {
                var end = DateTime.Now.AddMinutes(10);
                while (DateTime.Now < end) {
                    try {
                        if (nina.Sequence.Initialized) {
                            nina.Sequence.SequenceFinished += OnSequenceFinished;
                            Logger.Info("AutoStart : surveillance de la fin des séquences active");
                            return;
                        }
                    } catch {
                        // séquenceur pas encore enregistré
                    }
                    await Task.Delay(1000);
                }
                Logger.Error("AutoStart : séquenceur indisponible, la coupure de l'alimentation en fin de séquence ne fonctionnera pas");
            });
        }

        private Task OnSequenceFinished(object sender, EventArgs e) {
            // NINA attend la fin de ce gestionnaire : on rend la main tout de suite.
            bool cancelled = WasCancelled(sender);
            _ = Task.Run(() => PowerOffAfterSequenceAsync(cancelled));
            return Task.CompletedTask;
        }

        /// <summary>NINA signale la fin aussi bien après un arrêt manuel qu'à la fin normale : on lit son jeton d'annulation.</summary>
        private static bool WasCancelled(object sequencerVm) {
            try {
                var field = sequencerVm?.GetType().GetField("cts", BindingFlags.Instance | BindingFlags.NonPublic);
                return field?.GetValue(sequencerVm) is CancellationTokenSource cts && cts.IsCancellationRequested;
            } catch {
                return false;
            }
        }

        private async Task PowerOffAfterSequenceAsync(bool cancelled) {
            var settings = AutoStartSettings.Load();
            if (!settings.PowerEnabled) return;
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try {
                if (cancelled && settings.PowerOffOnlyIfCompleted) {
                    Logger.Info("AutoStart : séquence arrêtée à la main, l'alimentation reste allumée");
                    Notification.ShowInformation("AutoStart : séquence arrêtée à la main, l'alimentation du matériel reste allumée.");
                    return;
                }

                Logger.Info("AutoStart : fin de séquence, coupure de l'alimentation prévue");
                if (settings.PowerOffDelaySeconds > 0) {
                    var go = await OnUi(() => CountdownWindow.ShowAsync(
                        "Coupure de l'alimentation",
                        "La séquence est terminée : l'alimentation du matériel va être coupée.",
                        settings.DisconnectBeforePowerOff ? "Le matériel sera d'abord déconnecté dans NINA." : "Le matériel n'est pas déconnecté avant la coupure.",
                        "Couper maintenant",
                        settings.PowerOffDelaySeconds));
                    if (!await go) {
                        Logger.Info("AutoStart : coupure de l'alimentation annulée par l'utilisateur");
                        Notification.ShowInformation("AutoStart : coupure de l'alimentation annulée.");
                        return;
                    }
                }

                if (isSequenceRunning()) {
                    Logger.Info("AutoStart : une nouvelle séquence a démarré, coupure annulée");
                    Notification.ShowWarning("AutoStart : une séquence a redémarré, l'alimentation n'est pas coupée.");
                    return;
                }

                if (settings.DisconnectBeforePowerOff) {
                    Notification.ShowInformation("AutoStart : déconnexion du matériel…");
                    await DisconnectAllAsync();
                }

                await SetPowerAsync(settings, false);
                Notification.ShowSuccess("AutoStart : alimentation du matériel coupée.");
            } catch (Exception ex) {
                Logger.Error("AutoStart : coupure de l'alimentation impossible", ex);
                Notification.ShowError($"AutoStart : impossible de couper l'alimentation.\n{ex.Message}");
            } finally {
                Interlocked.Exchange(ref busy, 0);
            }
        }

        private async Task DisconnectAllAsync() {
            // Guidage d'abord (arrêt de PHD2 / du guideur), monture et dôme en dernier.
            await Disconnect("guidage", () => nina.Guider.GetInfo().Connected, nina.Guider.Disconnect);
            await Disconnect("caméra", () => nina.Camera.GetInfo().Connected, nina.Camera.Disconnect);
            await Disconnect("roue à filtres", () => nina.FilterWheel.GetInfo().Connected, nina.FilterWheel.Disconnect);
            await Disconnect("focuser", () => nina.Focuser.GetInfo().Connected, nina.Focuser.Disconnect);
            await Disconnect("rotateur", () => nina.Rotator.GetInfo().Connected, nina.Rotator.Disconnect);
            await Disconnect("panneau de flats", () => nina.FlatDevice.GetInfo().Connected, nina.FlatDevice.Disconnect);
            await Disconnect("monture", () => nina.Telescope.GetInfo().Connected, nina.Telescope.Disconnect);
            await Disconnect("dôme", () => nina.Dome.GetInfo().Connected, nina.Dome.Disconnect);
            await Disconnect("switch", () => nina.Switch.GetInfo().Connected, nina.Switch.Disconnect);
            await Disconnect("météo", () => nina.Weather.GetInfo().Connected, nina.Weather.Disconnect);
            await Disconnect("moniteur de sécurité", () => nina.SafetyMonitor.GetInfo().Connected, nina.SafetyMonitor.Disconnect);
            await Task.Delay(3000);
        }

        private static async Task Disconnect(string name, Func<bool> isConnected, Func<Task> disconnect) {
            try {
                if (!isConnected()) return;
                Logger.Info($"AutoStart : déconnexion {name}");
                await Task.Run(disconnect);
            } catch (Exception ex) {
                Logger.Error($"AutoStart : déconnexion {name} impossible", ex);
            }
        }

        private static Task<T> OnUi<T>(Func<T> action) {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) return Task.FromResult(action());
            return dispatcher.InvokeAsync(action).Task;
        }
    }
}
