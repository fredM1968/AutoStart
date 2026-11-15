using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugin.AutoStart {

    /// <summary>
    /// Prise TP-Link Kasa, protocole local historique (port 9999, JSON chiffré par XOR « autokey » de clé initiale 171).
    /// Même protocole que les scripts PowerShell switchON / switchOFF : HS100, HS103, HS105, HS110, KP105, KP115, HS300…
    /// Les prises Tapo et certaines Kasa récentes (protocole KLAP, pas de port 9999) ne sont pas prises en charge.
    /// </summary>
    public sealed class KasaPlug : ISmartPlug {
        public const int Port = 9999;
        private const int TimeoutMs = 3000;
        private const string SysInfo = "{\"system\":{\"get_sysinfo\":{}}}";

        public string Address { get; }

        public KasaPlug(string address) {
            if (string.IsNullOrWhiteSpace(address) || !IPAddress.TryParse(address.Trim(), out _)) {
                throw new ArgumentException($"adresse IP invalide : « {address} »");
            }
            Address = address.Trim();
        }

        public async Task<bool> GetStateAsync(CancellationToken ct = default) {
            using var doc = await SendAsync(SysInfo, ct);
            return doc.RootElement.GetProperty("system").GetProperty("get_sysinfo").GetProperty("relay_state").GetInt32() == 1;
        }

        public async Task SetStateAsync(bool on, CancellationToken ct = default) {
            using var doc = await SendAsync($"{{\"system\":{{\"set_relay_state\":{{\"state\":{(on ? 1 : 0)}}}}}}}", ct);
            var err = doc.RootElement.GetProperty("system").GetProperty("set_relay_state").GetProperty("err_code").GetInt32();
            if (err != 0) throw new InvalidOperationException($"la prise a renvoyé l'erreur {err}");
        }

        public async Task<FoundPlug> GetInfoAsync(CancellationToken ct = default) {
            using var doc = await SendAsync(SysInfo, ct);
            return ParseSysInfo(Address, doc.RootElement);
        }

        // ------------------------------------------------------------------ échange TCP

        private async Task<JsonDocument> SendAsync(string json, CancellationToken ct) {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeoutMs);
            using var client = new TcpClient();
            try {
                await client.ConnectAsync(IPAddress.Parse(Address), Port, timeout.Token);
                var stream = client.GetStream();
                var payload = Encrypt(json, withHeader: true);
                await stream.WriteAsync(payload, timeout.Token);

                var header = new byte[4];
                await stream.ReadExactlyAsync(header, timeout.Token);
                int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                if (length <= 0 || length > 1_000_000) throw new InvalidOperationException("réponse de la prise invalide");
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, timeout.Token);
                return JsonDocument.Parse(Decrypt(body, 0, body.Length));
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                throw new TimeoutException($"la prise ne répond pas à l'adresse {Address}");
            } catch (SocketException ex) {
                throw new InvalidOperationException($"impossible de joindre la prise à l'adresse {Address} ({ex.SocketErrorCode})", ex);
            }
        }

        // ------------------------------------------------------------------ chiffrement Kasa

        internal static byte[] Encrypt(string json, bool withHeader) {
            var bytes = Encoding.UTF8.GetBytes(json);
            int offset = withHeader ? 4 : 0;
            var output = new byte[offset + bytes.Length];
            if (withHeader) {
                output[0] = (byte)(bytes.Length >> 24);
                output[1] = (byte)(bytes.Length >> 16);
                output[2] = (byte)(bytes.Length >> 8);
                output[3] = (byte)bytes.Length;
            }
            byte key = 171;
            for (int i = 0; i < bytes.Length; i++) {
                key = (byte)(key ^ bytes[i]);
                output[offset + i] = key;
            }
            return output;
        }

        internal static string Decrypt(byte[] data, int offset, int count) {
            var result = new byte[count];
            byte key = 171;
            for (int i = 0; i < count; i++) {
                byte c = data[offset + i];
                result[i] = (byte)(c ^ key);
                key = c;
            }
            return Encoding.UTF8.GetString(result);
        }

        private static FoundPlug ParseSysInfo(string address, JsonElement root) {
            var info = root.GetProperty("system").GetProperty("get_sysinfo");
            string alias = info.TryGetProperty("alias", out var a) ? a.GetString() : "";
            string model = info.TryGetProperty("model", out var m) ? m.GetString() : "Kasa";
            bool? on = info.TryGetProperty("relay_state", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() == 1 : null;
            return new FoundPlug(address, alias, model, on);
        }

        // ------------------------------------------------------------------ recherche sur le réseau

        /// <summary>
        /// 1) Diffusion UDP sur le port 9999 (les prises Kasa répondent toutes en moins d'une seconde) ;
        /// 2) si rien n'a répondu : essai du port TCP 9999 sur chaque adresse des réseaux locaux (/22 au plus).
        /// </summary>
        public static async Task<List<FoundPlug>> DiscoverAsync(IProgress<string> progress, CancellationToken ct) {
            progress?.Report("Recherche par diffusion sur le réseau…");
            var found = await BroadcastAsync(TimeSpan.FromSeconds(3), ct);
            if (found.Count > 0) return found;

            progress?.Report("Aucune réponse à la diffusion : balayage des adresses du réseau…");
            return await ScanSubnetsAsync(progress, ct);
        }

        private static async Task<List<FoundPlug>> BroadcastAsync(TimeSpan duration, CancellationToken ct) {
            var results = new ConcurrentDictionary<string, FoundPlug>();
            var request = Encrypt(SysInfo, withHeader: false);
            var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
            foreach (var (address, mask) in LocalNetworks()) targets.Add(BroadcastAddress(address, mask));

            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stop.CancelAfter(duration);

            var receive = Task.Run(async () => {
                while (!stop.IsCancellationRequested) {
                    try {
                        var r = await udp.ReceiveAsync(stop.Token);
                        using var doc = JsonDocument.Parse(Decrypt(r.Buffer, 0, r.Buffer.Length));
                        var ip = r.RemoteEndPoint.Address.ToString();
                        results[ip] = ParseSysInfo(ip, doc.RootElement);
                    } catch (OperationCanceledException) {
                        break;
                    } catch {
                        // réponse d'un autre appareil, ignorée
                    }
                }
            });

            // Trois envois espacés : l'UDP peut perdre un paquet.
            for (int i = 0; i < 3 && !stop.IsCancellationRequested; i++) {
                foreach (var target in targets) {
                    try { await udp.SendAsync(request, new IPEndPoint(target, Port), stop.Token); } catch { }
                }
                try { await Task.Delay(500, stop.Token); } catch { }
            }
            await receive;
            ct.ThrowIfCancellationRequested();
            return results.Values.OrderBy(p => p.Address, StringComparer.Ordinal).ToList();
        }

        private static async Task<List<FoundPlug>> ScanSubnetsAsync(IProgress<string> progress, CancellationToken ct) {
            var hosts = new List<IPAddress>();
            foreach (var (address, mask) in LocalNetworks()) {
                uint ip = ToUInt(address), m = ToUInt(mask);
                uint network = ip & m, broadcast = network | ~m;
                if (broadcast - network > 1024) {           // réseau trop grand : on se limite au /24 du PC
                    network = ip & 0xFFFFFF00;
                    broadcast = network | 0xFF;
                }
                for (uint h = network + 1; h < broadcast; h++) {
                    if (h != ip) hosts.Add(FromUInt(h));
                }
            }

            var results = new ConcurrentBag<FoundPlug>();
            using var gate = new SemaphoreSlim(64);
            int done = 0;
            var tasks = hosts.Distinct().Select(async host => {
                await gate.WaitAsync(ct);
                try {
                    if (await IsPortOpenAsync(host, ct)) {
                        try { results.Add(await new KasaPlug(host.ToString()).GetInfoAsync(ct)); } catch { }
                    }
                } finally {
                    gate.Release();
                    var n = Interlocked.Increment(ref done);
                    if (n % 32 == 0) progress?.Report($"Balayage du réseau… {n}/{hosts.Count}");
                }
            });
            await Task.WhenAll(tasks);
            return results.OrderBy(p => p.Address, StringComparer.Ordinal).ToList();
        }

        private static async Task<bool> IsPortOpenAsync(IPAddress host, CancellationToken ct) {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(400);
            using var client = new TcpClient();
            try {
                await client.ConnectAsync(host, Port, timeout.Token);
                return true;
            } catch {
                ct.ThrowIfCancellationRequested();
                return false;
            }
        }

        /// <summary>Adresses IPv4 du PC (cartes réseau actives, Wi-Fi compris) avec leur masque.</summary>
        private static IEnumerable<(IPAddress Address, IPAddress Mask)> LocalNetworks() {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()) {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses) {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                    var bytes = ua.Address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue;   // adresse automatique sans réseau
                    yield return (ua.Address, ua.IPv4Mask);
                }
            }
        }

        private static IPAddress BroadcastAddress(IPAddress address, IPAddress mask) =>
            FromUInt(ToUInt(address) | ~ToUInt(mask));

        private static uint ToUInt(IPAddress a) {
            var b = a.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static IPAddress FromUInt(uint v) =>
            new IPAddress(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
    }
}
