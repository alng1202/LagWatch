using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;

namespace LagWatch
{
    internal sealed class PingSample
    {
        public DateTime Timestamp { get; set; }
        public bool Success { get; set; }
        public long LatencyMs { get; set; }
    }

    internal sealed class NetworkSnapshot
    {
        public int TotalSamples { get; set; }
        public int SuccessfulSamples { get; set; }
        public long CurrentMs { get; set; }
        public double AverageMs { get; set; }
        public double JitterMs { get; set; }
        public double LossPercent { get; set; }
        public long MinimumMs { get; set; }
        public long MaximumMs { get; set; }
        public string Quality { get; set; }
    }

    internal static class StatisticsCalculator
    {
        public static NetworkSnapshot Calculate(IEnumerable<PingSample> source)
        {
            List<PingSample> samples = source == null
                ? new List<PingSample>()
                : source.ToList();
            List<long> successful = samples.Where(s => s.Success).Select(s => s.LatencyMs).ToList();

            NetworkSnapshot result = new NetworkSnapshot();
            result.TotalSamples = samples.Count;
            result.SuccessfulSamples = successful.Count;
            result.CurrentMs = samples.Count > 0 && samples[samples.Count - 1].Success
                ? samples[samples.Count - 1].LatencyMs
                : -1;
            result.AverageMs = successful.Count == 0 ? 0 : successful.Average(v => (double)v);
            result.MinimumMs = successful.Count == 0 ? 0 : successful.Min();
            result.MaximumMs = successful.Count == 0 ? 0 : successful.Max();
            result.LossPercent = samples.Count == 0
                ? 0
                : 100.0 * (samples.Count - successful.Count) / samples.Count;

            if (successful.Count > 1)
            {
                double change = 0;
                for (int i = 1; i < successful.Count; i++)
                    change += Math.Abs(successful[i] - successful[i - 1]);
                result.JitterMs = change / (successful.Count - 1);
            }

            result.Quality = Classify(result);
            return result;
        }

        private static string Classify(NetworkSnapshot snapshot)
        {
            if (snapshot.TotalSamples == 0 || snapshot.SuccessfulSamples == 0)
                return "WAITING";
            if (snapshot.LossPercent >= 20 || snapshot.AverageMs >= 180 || snapshot.JitterMs >= 60)
                return "CRITICAL";
            if (snapshot.LossPercent >= 5 || snapshot.AverageMs >= 120 || snapshot.JitterMs >= 35)
                return "POOR";
            if (snapshot.LossPercent >= 2 || snapshot.AverageMs >= 80 || snapshot.JitterMs >= 20)
                return "FAIR";
            if (snapshot.LossPercent > 0 || snapshot.AverageMs >= 45 || snapshot.JitterMs >= 10)
                return "GOOD";
            return "EXCELLENT";
        }
    }

    internal static class TargetValidator
    {
        public static bool IsValid(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return false;

            string target = value.Trim();
            if (target.Length > 253 || target.Contains("/") || target.Contains("\\") ||
                target.Contains("://") || target.Any(Char.IsWhiteSpace))
                return false;

            IPAddress address;
            if (IPAddress.TryParse(target, out address))
                return true;

            return Uri.CheckHostName(target) == UriHostNameType.Dns;
        }
    }

    internal sealed class GameProcessOption
    {
        public string DisplayName { get; set; }
        public int ProcessId { get; set; }
        public string ProcessName { get; set; }
        public string ExecutablePath { get; set; }
        public bool IsLikelyGame { get; set; }
        public int DetectionScore { get; set; }
        public string DetectionReason { get; set; }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    internal static class GameProcessService
    {
        private static readonly HashSet<string> CommonGameNames = new HashSet<string>(
            new[]
            {
                "cs2", "csgo", "valorant", "valorant-win64-shipping",
                "fortniteclient-win64-shipping", "rocketleague", "r5apex",
                "overwatch", "overwatch launcher", "league of legends",
                "rainbowsix", "rainbowsix_vulkan", "modernwarfare", "cod",
                "destiny2", "eldenring", "helldivers2", "pubg", "tslgame",
                "dota2", "cyberpunk2077", "bg3", "bg3_dx11", "wow",
                "wowclassic", "diablo iv", "gta5", "rdr2", "robloxplayerbeta",
                "warframe.x64", "pathofexile", "pathofexile_x64",
                "deadbydaylight-win64-shipping", "palworld-win64-shipping",
                "nomanssky", "civilizationvi", "skyrimse", "fallout4",
                "terraria", "stardew valley", "osu!", "forzahorizon5",
                "haloinfinite", "marvel-win64-shipping", "minecraft.windows"
            },
            StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> ExcludedAppNames = new HashSet<string>(
            new[]
            {
                "lagwatch", "explorer", "applicationframehost", "searchhost",
                "startmenuexperiencehost", "shellexperiencehost", "textinputhost",
                "chrome", "msedge", "firefox", "opera", "brave", "discord",
                "slack", "teams", "msteams", "spotify", "obs64", "obs32",
                "code", "devenv", "notepad", "notepad++", "windowsterminal",
                "powershell", "pwsh", "cmd", "taskmgr", "steam", "steamwebhelper",
                "epicgameslauncher", "battle.net", "agent", "eadesktop",
                "ealauncher", "ubisoftconnect", "upc", "riotclientservices",
                "riotclientux", "riotclientuxrender"
            },
            StringComparer.OrdinalIgnoreCase);

        public static List<GameProcessOption> GetVisibleProcesses()
        {
            List<GameProcessOption> result = new List<GameProcessOption>();
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    string title = process.MainWindowTitle;
                    if (String.IsNullOrWhiteSpace(title))
                        continue;

                    string executablePath = null;
                    try { executablePath = process.MainModule.FileName; }
                    catch { }
                    int score = ScoreCandidate(process.ProcessName, executablePath);

                    result.Add(new GameProcessOption
                    {
                        DisplayName = title + "  (" + process.ProcessName + ")",
                        ProcessId = process.Id,
                        ProcessName = process.ProcessName,
                        ExecutablePath = executablePath,
                        DetectionScore = score,
                        IsLikelyGame = score >= 70,
                        DetectionReason = DetectionReason(process.ProcessName, executablePath, score)
                    });
                }
                catch
                {
                    // Protected and short-lived processes are simply omitted.
                }
                finally
                {
                    process.Dispose();
                }
            }

            return result
                .OrderByDescending(p => p.DetectionScore)
                .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public static bool LooksLikeGame(GameProcessOption option)
        {
            return option != null && option.IsLikelyGame;
        }

        public static GameProcessOption DetectRunningGame()
        {
            return GetVisibleProcesses()
                .Where(p => p.IsLikelyGame)
                .OrderByDescending(p => p.DetectionScore)
                .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault();
        }

        internal static int ScoreCandidate(string processName, string executablePath)
        {
            if (String.IsNullOrWhiteSpace(processName) || ExcludedAppNames.Contains(processName))
                return -100;

            if (CommonGameNames.Contains(processName))
                return 120;

            string path = (executablePath ?? "").Replace('/', '\\').ToLowerInvariant();
            string[] strongGamePaths =
            {
                "\\steamapps\\common\\",
                "\\xboxgames\\",
                "\\gog galaxy\\games\\",
                "\\riot games\\",
                "\\ea games\\",
                "\\ubisoft game launcher\\games\\"
            };
            if (strongGamePaths.Any(path.Contains))
                return 90;

            if (path.Contains("\\epic games\\") && !path.Contains("\\launcher\\"))
                return 85;

            string lowerName = processName.ToLowerInvariant();
            if (lowerName.EndsWith("-win64-shipping") || lowerName.EndsWith("-win32-shipping"))
                return 80;

            return 0;
        }

        private static string DetectionReason(string processName, string executablePath, int score)
        {
            if (score >= 120) return "known game executable";
            string path = (executablePath ?? "").ToLowerInvariant();
            if (path.Contains("steamapps")) return "Steam game folder";
            if (path.Contains("xboxgames")) return "Xbox game folder";
            if (path.Contains("epic games")) return "Epic Games folder";
            if (path.Contains("riot games")) return "Riot Games folder";
            if (path.Contains("gog galaxy")) return "GOG game folder";
            if (path.Contains("ea games")) return "EA game folder";
            if (path.Contains("ubisoft")) return "Ubisoft game folder";
            if (score >= 80) return "game engine executable";
            return "visible application";
        }
    }

    internal sealed class CommandResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public bool TimedOut { get; set; }
    }

    internal static class CommandRunner
    {
        public static async Task<CommandResult> RunAsync(string fileName, string arguments, int timeoutMs)
        {
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = fileName;
            start.Arguments = arguments;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;

            using (Process process = new Process())
            {
                process.StartInfo = start;
                process.Start();
                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();
                bool exited = await Task.Run(() => process.WaitForExit(timeoutMs));
                if (!exited)
                {
                    try { process.Kill(); }
                    catch { }
                }

                return new CommandResult
                {
                    ExitCode = exited ? process.ExitCode : -1,
                    Output = await outputTask,
                    Error = await errorTask,
                    TimedOut = !exited
                };
            }
        }
    }

    internal sealed class DiagnosticReport
    {
        public string AdapterName { get; set; }
        public string AdapterDescription { get; set; }
        public string AdapterType { get; set; }
        public long LinkSpeedMbps { get; set; }
        public string Gateway { get; set; }
        public string DnsServers { get; set; }
        public string LocalAddress { get; set; }
        public long GatewayPingMs { get; set; }
        public long TargetPingMs { get; set; }
        public List<string> Recommendations { get; private set; }

        public DiagnosticReport()
        {
            Recommendations = new List<string>();
            GatewayPingMs = -1;
            TargetPingMs = -1;
        }

        public string ToDisplayText(string target)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("ACTIVE CONNECTION");
            text.AppendLine(AdapterName + " — " + AdapterType);
            text.AppendLine(AdapterDescription);
            text.AppendLine("Link speed: " + LinkSpeedMbps.ToString("N0", CultureInfo.InvariantCulture) + " Mbps");
            text.AppendLine("Local address: " + ValueOrDash(LocalAddress));
            text.AppendLine("Gateway: " + ValueOrDash(Gateway) + FormatPing(GatewayPingMs));
            text.AppendLine("DNS: " + ValueOrDash(DnsServers));
            text.AppendLine("Target: " + target + FormatPing(TargetPingMs));
            text.AppendLine();
            text.AppendLine("RECOMMENDATIONS");
            if (Recommendations.Count == 0)
                text.AppendLine("No obvious local connection problems were found.");
            else
                foreach (string recommendation in Recommendations)
                    text.AppendLine("• " + recommendation);
            return text.ToString().Trim();
        }

        private static string ValueOrDash(string value)
        {
            return String.IsNullOrWhiteSpace(value) ? "—" : value;
        }

        private static string FormatPing(long value)
        {
            return value < 0 ? "" : "  ·  " + value + " ms";
        }
    }

    internal static class NetworkDiagnostics
    {
        public static async Task<DiagnosticReport> AnalyzeAsync(string target)
        {
            NetworkInterface adapter = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .OrderByDescending(HasGateway)
                .ThenByDescending(n => n.Speed)
                .FirstOrDefault();

            DiagnosticReport report = new DiagnosticReport();
            if (adapter == null)
            {
                report.AdapterName = "No active adapter found";
                report.AdapterDescription = "Windows did not report an active network connection.";
                report.AdapterType = "Disconnected";
                report.Recommendations.Add("Connect to a network, then run diagnostics again.");
                return report;
            }

            IPInterfaceProperties properties = adapter.GetIPProperties();
            report.AdapterName = adapter.Name;
            report.AdapterDescription = adapter.Description;
            report.AdapterType = FriendlyType(adapter.NetworkInterfaceType);
            report.LinkSpeedMbps = Math.Max(0, adapter.Speed / 1000000);
            report.Gateway = properties.GatewayAddresses
                .Select(g => g.Address)
                .Where(a => a != null && !IPAddress.Any.Equals(a) && !IPAddress.IPv6Any.Equals(a))
                .Select(a => a.ToString())
                .FirstOrDefault();
            report.DnsServers = String.Join(", ", properties.DnsAddresses.Select(a => a.ToString()).Take(3));
            report.LocalAddress = properties.UnicastAddresses
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .FirstOrDefault();

            if (!String.IsNullOrEmpty(report.Gateway))
                report.GatewayPingMs = await PingOnceAsync(report.Gateway, 1200);
            report.TargetPingMs = await PingOnceAsync(target, 1500);

            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                report.Recommendations.Add("For competitive play, Ethernet usually has steadier latency than Wi‑Fi.");
            if (report.LinkSpeedMbps > 0 && report.LinkSpeedMbps < 100)
                report.Recommendations.Add("The adapter negotiated below 100 Mbps; check the cable, signal, or adapter settings.");
            if (report.GatewayPingMs >= 10)
                report.Recommendations.Add("Latency is already elevated between this PC and the router; inspect Wi‑Fi signal or local congestion.");
            if (report.GatewayPingMs < 0 && !String.IsNullOrEmpty(report.Gateway))
                report.Recommendations.Add("The router did not answer ICMP. This can be normal, but it limits local latency diagnosis.");
            if (report.TargetPingMs < 0)
                report.Recommendations.Add("The target did not answer ICMP. Try another endpoint or confirm that ping is allowed.");
            else if (report.TargetPingMs >= 120)
                report.Recommendations.Add("Target latency is high; use a closer game region when the game offers one.");

            return report;
        }

        public static string GetActiveAdapterSummary()
        {
            try
            {
                NetworkInterface adapter = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up)
                    .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .OrderByDescending(HasGateway)
                    .ThenByDescending(n => n.Speed)
                    .FirstOrDefault();
                return adapter == null ? "No active adapter" : adapter.Name + " · " + FriendlyType(adapter.NetworkInterfaceType);
            }
            catch
            {
                return "Adapter unavailable";
            }
        }

        private static bool HasGateway(NetworkInterface adapter)
        {
            try { return adapter.GetIPProperties().GatewayAddresses.Count > 0; }
            catch { return false; }
        }

        private static async Task<long> PingOnceAsync(string target, int timeoutMs)
        {
            try
            {
                using (Ping ping = new Ping())
                {
                    PingReply reply = await ping.SendPingAsync(target, timeoutMs);
                    return reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;
                }
            }
            catch
            {
                return -1;
            }
        }

        private static string FriendlyType(NetworkInterfaceType type)
        {
            switch (type)
            {
                case NetworkInterfaceType.Wireless80211: return "Wi‑Fi";
                case NetworkInterfaceType.Ethernet:
                case NetworkInterfaceType.GigabitEthernet:
                case NetworkInterfaceType.FastEthernetFx:
                case NetworkInterfaceType.FastEthernetT: return "Ethernet";
                case NetworkInterfaceType.Ppp: return "VPN / PPP";
                default: return type.ToString();
            }
        }
    }
}
