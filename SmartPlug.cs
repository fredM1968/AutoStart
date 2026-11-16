using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugin.AutoStart {

    /// <summary>Marques / protocoles de prises connectées pris en charge. Pour en ajouter une : une valeur ici + une classe ISmartPlug.</summary>
    public enum PlugBrand {
        TpLinkKasa
    }

    /// <summary>Prise connectée pilotée en local (sans passer par le cloud du fabricant).</summary>
    public interface ISmartPlug {
        string Address { get; }
        Task<bool> GetStateAsync(CancellationToken ct = default);
        Task SetStateAsync(bool on, CancellationToken ct = default);
    }

    public enum ScheduleRepeat {
        Once,
        Daily,
        Days
    }

    /// <summary>
    /// Allumage programmé, en heure du PC.
    /// Days == null : une seule fois, à la date et l'heure de Time.
    /// Sinon : chaque jour coché (indice 0 = dimanche, comme DayOfWeek), à l'heure de Time (la date est ignorée).
    /// </summary>
    public record PlugSchedule(DateTime Time, bool[] Days) {
        public bool IsRepeating => Days != null;
    }

    /// <param name="ClockOffset">Écart entre l'horloge de la prise et celle du PC.</param>
    public record ScheduleResult(PlugSchedule Schedule, TimeSpan ClockOffset);

    /// <summary>
    /// Prise capable d'enregistrer elle-même une heure d'allumage (programmateur interne) :
    /// l'allumage a lieu même si le PC ou NINA sont éteints.
    /// </summary>
    public interface ISchedulablePlug {
        /// <summary>Programme l'allumage. Remplace la programmation précédente du plugin.</summary>
        Task<ScheduleResult> ScheduleAsync(PlugSchedule schedule, CancellationToken ct = default);
        /// <summary>Supprime la programmation créée par le plugin.</summary>
        Task CancelScheduleAsync(CancellationToken ct = default);
        /// <summary>Programmation du plugin actuellement enregistrée dans la prise (heure du PC), ou null.</summary>
        Task<PlugSchedule> GetScheduleAsync(CancellationToken ct = default);
    }

    /// <summary>Prise trouvée par la recherche sur le réseau.</summary>
    public record FoundPlug(string Address, string Alias, string Model, bool? IsOn) {
        public override string ToString() {
            var name = string.IsNullOrWhiteSpace(Alias) ? Model : $"{Alias} ({Model})";
            var state = IsOn switch { true => " – allumée", false => " – éteinte", _ => "" };
            return $"{Address}  —  {name}{state}";
        }
    }

    public record PlugBrandItem(PlugBrand Brand, string DisplayName);

    public static class SmartPlugFactory {

        public static IReadOnlyList<PlugBrandItem> Brands { get; } = new[] {
            new PlugBrandItem(PlugBrand.TpLinkKasa, "TP-Link Kasa (HS100, HS103, HS105, HS110, KP105, KP115…)")
        };

        public static ISmartPlug Create(PlugBrand brand, string address) => brand switch {
            PlugBrand.TpLinkKasa => new KasaPlug(address),
            _ => throw new NotSupportedException($"Prise non prise en charge : {brand}")
        };

        public static Task<List<FoundPlug>> DiscoverAsync(PlugBrand brand, IProgress<string> progress, CancellationToken ct) => brand switch {
            PlugBrand.TpLinkKasa => KasaPlug.DiscoverAsync(progress, ct),
            _ => Task.FromResult(new List<FoundPlug>())
        };

        public static string DisplayName(PlugBrand brand) =>
            Brands.FirstOrDefault(b => b.Brand == brand)?.DisplayName ?? brand.ToString();
    }
}
