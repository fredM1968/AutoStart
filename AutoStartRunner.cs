using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Serialization;
using NINA.ViewModel.Interfaces;
using NINA.ViewModel.Sequencer;
using NINA.WPF.Base.Interfaces.Mediator;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace NINA.Plugin.AutoStart {

    /// <summary>Services NINA dont le lancement automatique a besoin (injectés dans le plugin).</summary>
    public class NinaServices {
        public IProfileService Profile { get; init; }
        public ISequenceMediator Sequence { get; init; }
        public IApplicationMediator Application { get; init; }
        public ICameraMediator Camera { get; init; }
        public IFilterWheelMediator FilterWheel { get; init; }
        public ITelescopeMediator Telescope { get; init; }
        public IFocuserMediator Focuser { get; init; }
        public IRotatorMediator Rotator { get; init; }
        public IGuiderMediator Guider { get; init; }
        public IFlatDeviceMediator FlatDevice { get; init; }
        public IWeatherDataMediator Weather { get; init; }
        public ISwitchMediator Switch { get; init; }
        public IDomeMediator Dome { get; init; }
        public ISafetyMonitorMediator SafetyMonitor { get; init; }
    }

    /// <summary>
    /// Déroulé du lancement automatique :
    /// 1. attendre que le séquenceur de NINA soit prêt ;
    /// 2. compte à rebours annulable ;
    /// 3. connexion de tout le matériel (optionnelle, même ordre que le bouton « Tout connecter » de NINA) ;
    /// 4. chargement de la séquence (simple ou avancée) puis démarrage dans l'onglet Imagerie.
    /// </summary>
    public class AutoStartRunner {
        private readonly NinaServices nina;
        private int running;

        public AutoStartRunner(NinaServices nina) {
            this.nina = nina;
        }

        public bool IsRunning => running == 1;

        public void Start(AutoStartSettings settings, bool isTest) {
            if (Interlocked.Exchange(ref running, 1) == 1) return;
            _ = Task.Run(async () => {
                try {
                    await RunAsync(settings, isTest);
                } catch (Exception ex) {
                    Logger.Error("AutoStart : échec du lancement automatique", ex);
                    Notification.ShowError($"AutoStart : échec du lancement automatique.\n{ex.Message}");
                } finally {
                    Interlocked.Exchange(ref running, 0);
                }
            });
        }

        private async Task RunAsync(AutoStartSettings settings, bool isTest) {
            Logger.Info($"AutoStart : démarrage de la procédure ({(isTest ? "test" : "démarrage du PC")}), séquence = '{settings.SequencePath}'");

            if (!await WaitForSequencerAsync(TimeSpan.FromMinutes(5))) {
                throw new InvalidOperationException("le séquenceur de NINA n'est pas prêt après 5 minutes.");
            }

            var kind = settings.Kind;
            if (kind == SequenceKind.None) {
                Logger.Info("AutoStart : aucune séquence choisie, NINA reste simplement ouvert.");
                if (isTest) Notification.ShowWarning("AutoStart : aucune séquence n'est choisie.");
                return;
            }
            if (!File.Exists(settings.SequencePath)) {
                throw new FileNotFoundException($"séquence introuvable : {settings.SequencePath}");
            }
            if (IsAnySequenceRunning()) {
                Notification.ShowWarning("AutoStart : une séquence est déjà en cours, lancement automatique ignoré.");
                return;
            }

            // Compte à rebours : laisse le temps de reprendre la main à distance (AnyDesk...) et d'annuler.
            if (settings.DelaySeconds > 0) {
                var go = await OnUi(() => CountdownWindow.ShowAsync(Path.GetFileName(settings.SequencePath), settings.DelaySeconds, settings.ConnectEquipment));
                if (!await go) {
                    Logger.Info("AutoStart : lancement annulé par l'utilisateur.");
                    Notification.ShowInformation("AutoStart : lancement de la séquence annulé.");
                    return;
                }
            }

            if (settings.ConnectEquipment) {
                Notification.ShowInformation("AutoStart : connexion du matériel…");
                await ConnectAllAsync();
            }

            if (kind == SequenceKind.Advanced) {
                await StartAdvancedAsync(settings.SequencePath);
            } else {
                await StartSimpleAsync(settings.SequencePath);
            }
        }

        // ---------------------------------------------------------------- attente de NINA

        private async Task<bool> WaitForSequencerAsync(TimeSpan timeout) {
            var end = DateTime.Now + timeout;
            while (DateTime.Now < end) {
                try {
                    if (nina.Sequence.Initialized && GetNavigation() != null) {
                        await Task.Delay(2000); // laisse NINA finir d'afficher sa fenêtre
                        return true;
                    }
                } catch {
                    // la navigation du séquenceur n'est pas encore enregistrée
                }
                await Task.Delay(1000);
            }
            return false;
        }

        /// <summary>La navigation du séquenceur (vues simple et avancée) n'est pas exposée aux plugins : on la lit dans le médiateur.</summary>
        private ISequenceNavigationVM GetNavigation() {
            var field = nina.Sequence.GetType().GetField("sequenceNavigation", BindingFlags.Instance | BindingFlags.NonPublic);
            return field?.GetValue(nina.Sequence) as ISequenceNavigationVM;
        }

        private bool IsAnySequenceRunning() {
            try {
                if (nina.Sequence.IsAdvancedSequenceRunning()) return true;
                var simple = GetNavigation()?.SimpleSequenceVM;
                var prop = simple?.GetType().GetProperty("IsRunning");
                return prop?.GetValue(simple) is true;
            } catch {
                return false;
            }
        }

        // ---------------------------------------------------------------- matériel

        private async Task ConnectAllAsync() {
            await Connect("caméra", () => nina.Camera.GetInfo().Connected, nina.Camera.Connect);
            await Connect("roue à filtres", () => nina.FilterWheel.GetInfo().Connected, nina.FilterWheel.Connect);
            await Connect("monture", () => nina.Telescope.GetInfo().Connected, nina.Telescope.Connect);
            await Connect("focuser", () => nina.Focuser.GetInfo().Connected, nina.Focuser.Connect);
            await Connect("rotateur", () => nina.Rotator.GetInfo().Connected, nina.Rotator.Connect);
            await Connect("guidage", () => nina.Guider.GetInfo().Connected, nina.Guider.Connect);
            await Connect("panneau de flats", () => nina.FlatDevice.GetInfo().Connected, nina.FlatDevice.Connect);
            await Connect("météo", () => nina.Weather.GetInfo().Connected, nina.Weather.Connect);
            await Connect("switch", () => nina.Switch.GetInfo().Connected, nina.Switch.Connect);
            await Connect("dôme", () => nina.Dome.GetInfo().Connected, nina.Dome.Connect);
            await Connect("moniteur de sécurité", () => nina.SafetyMonitor.GetInfo().Connected, nina.SafetyMonitor.Connect);
            await Task.Delay(3000);
        }

        private static async Task Connect(string name, Func<bool> isConnected, Func<Task<bool>> connect) {
            try {
                if (isConnected()) return;
                Logger.Info($"AutoStart : connexion {name}");
                var ok = await Task.Run(connect);
                if (!ok) Logger.Info($"AutoStart : {name} non connecté(e) (aucun appareil choisi dans le profil ?)");
            } catch (Exception ex) {
                Logger.Error($"AutoStart : connexion {name} impossible", ex);
            }
        }

        // ---------------------------------------------------------------- séquence avancée

        private async Task StartAdvancedAsync(string path) {
            var nav = GetNavigation() ?? throw new InvalidOperationException("séquenceur introuvable.");
            var seq2 = nav.Sequence2VM;

            await OnUi(() => {
                // Même chargement que le menu « Ouvrir » du séquenceur avancé (méthode interne de NINA).
                var load = seq2.GetType().GetMethod("LoadSequenceFromFile", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(string) }, null);
                if (load != null) {
                    load.Invoke(seq2, new object[] { path });
                } else {
                    // Repli sur l'API publique si la méthode change dans une future version de NINA.
                    var container = new SequenceJsonConverter(seq2.SequencerFactory).Deserialize(File.ReadAllText(path));
                    if (container is ISequenceRootContainer root) {
                        nina.Sequence.SetAdvancedSequence(root);
                    } else {
                        throw new InvalidOperationException("le fichier n'est pas une séquence avancée complète.");
                    }
                }
                nav.SwitchToAdvancedView();
                return true;
            });

            // NINA affiche lui-même l'erreur si le fichier est illisible : on vérifie que c'est bien notre fichier qui est chargé.
            var loaded = nina.Sequence.GetAdvancedSequencerSavePath();
            if (!string.IsNullOrEmpty(loaded) &&
                !string.Equals(Path.GetFullPath(loaded), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException($"la séquence avancée n'a pas pu être chargée : {path}");
            }

            Logger.Info($"AutoStart : démarrage de la séquence avancée {path}");
            Notification.ShowInformation($"AutoStart : démarrage de la séquence « {Path.GetFileName(path)} »");
            await OnUi(() => { nina.Application.ChangeTab(ApplicationTab.IMAGING); return true; });

            // skipValidation = true : comme --runsequence, pas de boîte de dialogue de validation bloquante.
            // La séquence dure toute la nuit : on ne l'attend pas.
            _ = OnUi<Task>(async () => {
                try {
                    await nina.Sequence.StartAdvancedSequence(true);
                } catch (Exception ex) {
                    Logger.Error("AutoStart : erreur pendant la séquence avancée", ex);
                }
            });
        }

        // ---------------------------------------------------------------- séquence simple

        /// <summary>
        /// Le séquenceur simple ne propose que des boîtes de dialogue pour charger une cible :
        /// on reproduit ici son propre code de chargement (CaptureSequenceList.Load / LoadSequenceSet + MigrateFromCaptureSequenceList).
        /// </summary>
        private async Task StartSimpleAsync(string path) {
            var nav = GetNavigation() ?? throw new InvalidOperationException("séquenceur introuvable.");
            var simple = nav.SimpleSequenceVM;
            var type = simple.GetType();
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            await OnUi(() => {
                var migrate = type.GetMethod("MigrateFromCaptureSequenceList", inst)
                              ?? throw new InvalidOperationException("version de NINA non prise en charge (MigrateFromCaptureSequenceList).");
                var cslType = migrate.GetParameters()[0].ParameterType;

                var profile = nina.Profile.ActiveProfile;
                object filters = profile.FilterWheelSettings.FilterWheelFilters;
                double lat = profile.AstrometrySettings.Latitude;
                double lon = profile.AstrometrySettings.Longitude;

                var targets = new List<ISequenceItem>();
                bool isSet = Path.GetExtension(path).Equals(".ninaTargetSet", StringComparison.OrdinalIgnoreCase);

                if (isSet) {
                    var loadSet = cslType.GetMethods(BindingFlags.Static | BindingFlags.Public)
                        .FirstOrDefault(m => m.Name == "LoadSequenceSet" && m.GetParameters().Length == 4)
                        ?? throw new InvalidOperationException("version de NINA non prise en charge (LoadSequenceSet).");
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                    var set = loadSet.Invoke(null, new object[] { fs, filters, lat, lon }) as IEnumerable;
                    foreach (var csl in set ?? Array.Empty<object>()) {
                        targets.Add((ISequenceItem)migrate.Invoke(simple, new[] { csl }));
                    }
                } else {
                    var load = cslType.GetMethods(BindingFlags.Static | BindingFlags.Public)
                        .FirstOrDefault(m => m.Name == "Load" && m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType == typeof(string))
                        ?? throw new InvalidOperationException("version de NINA non prise en charge (CaptureSequenceList.Load).");
                    var csl = load.Invoke(null, new object[] { path, filters, lat, lon });
                    if (csl != null) {
                        var target = (ISequenceItem)migrate.Invoke(simple, new[] { csl });
                        target.GetType().GetProperty("FileName")?.SetValue(target, path);
                        targets.Add(target);
                    }
                }

                if (targets.Count == 0) throw new InvalidOperationException($"aucune cible lue dans {path}");

                // Remplace les cibles actuelles du séquenceur simple.
                var area = (ISequenceContainer)type.GetProperty("Targets").GetValue(simple);
                foreach (var old in area.Items.ToList()) area.Remove(old);
                foreach (var t in targets) area.Add(t);

                typeof(ISimpleSequenceVM).GetProperty(nameof(ISimpleSequenceVM.SelectedTarget)).SetValue(simple, targets[0]);
                targets[0].GetType().GetMethod("ResetProgressCascaded", inst, null, Type.EmptyTypes, null)?.Invoke(targets[0], null);
                nav.ActiveSequencerVM = simple;
                return true;
            });

            var start = type.GetProperty("StartSequenceCommand")?.GetValue(simple) as ICommand
                        ?? throw new InvalidOperationException("version de NINA non prise en charge (StartSequenceCommand).");

            // La commande n'est disponible que si la caméra est libre : on attend un peu après la connexion.
            var end = DateTime.Now.AddSeconds(60);
            while (!await OnUi(() => start.CanExecute(null))) {
                if (DateTime.Now > end) throw new InvalidOperationException("la caméra n'est pas disponible pour lancer la séquence simple.");
                await Task.Delay(1000);
            }

            Logger.Info($"AutoStart : démarrage de la séquence simple {path}");
            Notification.ShowInformation($"AutoStart : démarrage de la séquence « {Path.GetFileName(path)} »");
            await OnUi(() => {
                nina.Application.ChangeTab(ApplicationTab.IMAGING);
                start.Execute(null);
                return true;
            });
        }

        // ---------------------------------------------------------------- utilitaires

        private static Task<T> OnUi<T>(Func<T> action) {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) return Task.FromResult(action());
            return dispatcher.InvokeAsync(action).Task;
        }
    }
}
