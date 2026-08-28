using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LagWatch
{
    internal sealed class OutageMapForm : Form
    {
        private readonly Label ispValue;
        private readonly Label localValue;
        private readonly Label providerValue;
        private readonly Label globalValue;
        private readonly Label plannedValue;
        private readonly Label updatedValue;
        private readonly Label offlineRiskValue;
        private readonly AccentButton refreshButton;
        private readonly AccentButton providerStatusButton;
        private string providerStatusUrl;

        public OutageMapForm()
        {
            Text = "LagWatch Pro — Outage Map";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(760, 555);
            MinimumSize = new Size(680, 470);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);

            Label title = MakeLabel("OUTAGE MAP", 26, 22, 16, Theme.Text);
            Label subtitle = MakeLabel("LOCAL DEVICE  →  ISP EDGE  →  GLOBAL INTERNET", 26, 52, 9, Theme.TextMuted);
            Controls.Add(title); Controls.Add(subtitle);

            CardPanel card = new CardPanel { Location = new Point(26, 88), Size = new Size(690, 310), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            Controls.Add(card);
            AddCaption(card, "DETECTED ISP / ASN", 20); ispValue = AddValue(card, "Detecting…", 20);
            AddCaption(card, "LOCAL CONNECTION", 72); localValue = AddValue(card, "Checking gateway and DNS…", 72);
            AddCaption(card, "ISP NETWORK / INTERNET EDGE", 124); providerValue = AddValue(card, "Checking beyond your gateway…", 124);
            AddCaption(card, "GLOBAL OUTAGES", 176); globalValue = AddValue(card, "Checking configured provider…", 176);
            AddCaption(card, "PLANNED OUTAGES", 228); plannedValue = AddValue(card, "Checking provider capability…", 228);
            providerStatusButton = new AccentButton { Text = "OPEN ISP STATUS CENTER", Width = 190, Height = 30,
                Location = new Point(210, 263), Visible = false, AccentColor = Color.FromArgb(251, 191, 36) };
            providerStatusButton.Click += delegate
            {
                if (!String.IsNullOrWhiteSpace(providerStatusUrl))
                    Process.Start(new ProcessStartInfo(providerStatusUrl) { UseShellExecute = true });
            };
            card.Controls.Add(providerStatusButton);

            offlineRiskValue = MakeLabel("OFFLINE PREDICTION · learning from local outage events", 26, 410, 8.5f, Theme.TextMuted);
            Controls.Add(offlineRiskValue);
            updatedValue = MakeLabel("Not checked", 26, 446, 8.5f, Theme.TextFaint);
            Controls.Add(updatedValue);
            refreshButton = new AccentButton { Text = "REFRESH OUTAGE DATA", Width = 180, Height = 36, Primary = true,
                AccentColor = Color.FromArgb(251, 191, 36), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            refreshButton.Location = new Point(ClientSize.Width - 206, 437);
            refreshButton.Click += async delegate { await RefreshAsync(); };
            Controls.Add(refreshButton);
            Resize += delegate { refreshButton.Left = ClientSize.Width - 206; card.Width = ClientSize.Width - 52; };
            Shown += async delegate { await RefreshAsync(); };
        }

        private async Task RefreshAsync()
        {
            refreshButton.Enabled = false;
            ispValue.Text = localValue.Text = providerValue.Text = globalValue.Text = "Checking…";
            plannedValue.Text = "Provider-specific adapter required";
            try
            {
                string isp = await DetectIspWithCacheAsync();
                ispValue.Text = String.IsNullOrWhiteSpace(isp) ? "Unavailable offline · no cached ISP yet" : isp;
                ConfigureProviderStatus(isp);
                bool adapter = NetworkInterface.GetIsNetworkAvailable();
                bool gateway = await PingAsync(GetGateway(), 1200);
                bool cloudflare = await PingAsync("1.1.1.1", 1400);
                bool google = await PingAsync("8.8.8.8", 1400);
                bool dns = await ResolveAsync("example.com");
                string diagnosis = ClassifyOffline(adapter, gateway, cloudflare, google, dns);
                localValue.Text = gateway ? "HEALTHY · gateway reachable" :
                    (!adapter ? "LOCAL OUTAGE · network adapter disconnected" :
                    (cloudflare || google ? "INDETERMINATE · gateway may block ICMP" : "POSSIBLE LOCAL OR ROUTER OUTAGE"));
                localValue.ForeColor = gateway || cloudflare || google ? Theme.Good : Theme.Bad;
                providerValue.Text = cloudflare || google
                    ? "HEALTHY · independent public endpoints reachable"
                    : (dns ? "DEGRADED · DNS works but probes fail" : "POSSIBLE ISP / UPSTREAM OUTAGE");
                providerValue.ForeColor = cloudflare || google ? Theme.Good : Theme.Warning;
                globalValue.Text = await QueryIodaWithCacheAsync(isp);
                OutageHistory history = new OutageHistory();
                history.Record(DateTime.Now, diagnosis);
                offlineRiskValue.Text = "OFFLINE PREDICTION · " + history.GetRecurringWarning(DateTime.Now);
                offlineRiskValue.ForeColor = diagnosis == "HEALTHY" ? Theme.TextMuted : Theme.Warning;
                plannedValue.Text = providerStatusUrl == null
                    ? "No free provider feed found · use your ISP account/app"
                    : "Available through the official ISP status center (service address/sign-in required)";
                updatedValue.Text = "Updated " + DateTime.Now.ToString("g") +
                    " · results are evidence, not a guaranteed ISP incident declaration";
            }
            catch (Exception ex)
            {
                globalValue.Text = "Outage check failed · " + ex.Message;
                globalValue.ForeColor = Theme.Warning;
            }
            finally { refreshButton.Enabled = true; }
        }

        private static async Task<string> DetectIspWithCacheAsync()
        {
            string cache = CachePath("isp.txt");
            try
            {
                using (WebClient client = NewClient())
                {
                    string json = await client.DownloadStringTaskAsync("https://asn.ipinfo.app/api/json/myasn");
                    Match asn = Regex.Match(json, "\\\"asn\\\"\\s*:\\s*(\\d+)");
                    Match name = Regex.Match(json, "\\\"name\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"");
                    string result = NormalizeProvider(name.Success ? name.Groups[1].Value : "Unknown ISP") +
                        (asn.Success ? " · AS" + asn.Groups[1].Value : "");
                    File.WriteAllText(cache, result); return result;
                }
            }
            catch { return File.Exists(cache) ? File.ReadAllText(cache) + " · cached" : null; }
        }

        private void ConfigureProviderStatus(string isp)
        {
            providerStatusUrl = null;
            string value = (isp ?? "").ToUpperInvariant();
            if (value.Contains("COMCAST") || value.Contains("XFINITY"))
                providerStatusUrl = "https://www.xfinity.com/support/statusmap";
            else if (value.Contains("SPECTRUM") || value.Contains("CHARTER"))
                providerStatusUrl = "https://www.spectrum.net/support/tv/troubleshooting-your-spectrum-services-after-outage";
            else if (value.Contains("AT&T"))
                providerStatusUrl = "https://www.att.com/outages/";
            else if (value.Contains("VERIZON"))
                providerStatusUrl = "https://www.verizon.com/support/check-network-status/";
            providerStatusButton.Visible = providerStatusUrl != null;
        }

        internal static string NormalizeProvider(string raw)
        {
            string value = (raw ?? "").ToUpperInvariant();
            if (value.Contains("COMCAST") || value.Contains("XFINITY")) return "Comcast";
            if (value.Contains("CHARTER") || value.Contains("SPECTRUM")) return "Spectrum";
            if (value.Contains("BELLSOUTH") || value.Contains("AT&T") || value.Contains("ATT-INTERNET")) return "AT&T";
            if (value.Contains("VERIZON")) return "Verizon";
            if (value.Contains("COX")) return "Cox";
            if (value.Contains("CENTURYLINK") || value.Contains("QWEST") || value.Contains("LUMEN")) return "CenturyLink / Lumen";
            if (value.Contains("FRONTIER")) return "Frontier";
            if (value.Contains("T-MOBILE") || value.Contains("TMOBILE")) return "T-Mobile";
            return String.IsNullOrWhiteSpace(raw) ? "Unknown ISP" : raw.Split('-')[0].Trim();
        }

        private static async Task<string> QueryIodaWithCacheAsync(string isp)
        {
            Match asn = Regex.Match(isp ?? "", "AS(\\d+)", RegexOptions.IgnoreCase);
            if (!asn.Success) return "UNAVAILABLE · ISP ASN could not be detected";
            string cache = CachePath("ioda.txt");
            try
            {
                using (WebClient client = NewClient())
                {
                    long until = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); long from = until - 86400;
                    string json = await client.DownloadStringTaskAsync("https://api.ioda.inetintel.cc.gatech.edu/v2/outages/alerts/asn/" + asn.Groups[1].Value + "?from=" + from + "&until=" + until);
                    bool empty = Regex.IsMatch(json, "\\\"data\\\"\\s*:\\s*\\[\\s*\\]");
                    int count = Regex.Matches(json, "\\\"score\\\"\\s*:").Count;
                    string result = empty || count == 0 ? "NO IODA ASN OUTAGE ALERTS IN LAST 24 HOURS" : count + " FREE IODA OUTAGE ALERT SIGNAL(S) RETURNED";
                    File.WriteAllText(cache, DateTime.Now.ToString("o") + Environment.NewLine + result); return result;
                }
            }
            catch
            {
                if (!File.Exists(cache)) return "OFFLINE · no cached global-outage result";
                string[] lines = File.ReadAllLines(cache); DateTime saved;
                string age = lines.Length > 0 && DateTime.TryParse(lines[0], out saved) ? " · cached " + (int)(DateTime.Now - saved).TotalHours + "h ago" : " · cached";
                return (lines.Length > 1 ? lines[1] : "Last global result unavailable") + age;
            }
        }

        internal static string ClassifyOffline(bool adapter, bool gateway, bool firstPublic, bool secondPublic, bool dns)
        {
            if (!adapter) return "LOCAL_ADAPTER_OUTAGE";
            if (firstPublic || secondPublic) return dns ? "HEALTHY" : "DNS_OUTAGE_SUSPECTED";
            if (gateway) return "ISP_OR_UPSTREAM_OUTAGE_SUSPECTED";
            return "LOCAL_OR_ROUTER_OUTAGE_SUSPECTED";
        }

        private static string CachePath(string name)
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LagWatch");
            Directory.CreateDirectory(directory); return Path.Combine(directory, name);
        }

        private static WebClient NewClient()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            WebClient client = new WebClient(); client.Headers[HttpRequestHeader.UserAgent] = "LagWatch-Dev/1.0"; return client;
        }

        private static string GetGateway()
        {
            try { return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses).Select(g => g.Address.ToString()).FirstOrDefault(); }
            catch { return null; }
        }

        private static async Task<bool> PingAsync(string target, int timeout)
        {
            if (String.IsNullOrWhiteSpace(target)) return false;
            try { using (Ping ping = new Ping()) { return (await ping.SendPingAsync(target, timeout)).Status == IPStatus.Success; } }
            catch { return false; }
        }

        private static async Task<bool> ResolveAsync(string host)
        {
            try { return (await Dns.GetHostAddressesAsync(host)).Length > 0; }
            catch { return false; }
        }

        private static Label MakeLabel(string text, int left, int top, float size, Color color)
        { return new Label { Text = text, AutoSize = true, Location = new Point(left, top), Font = new Font("Segoe UI Semibold", size), ForeColor = color }; }
        private static void AddCaption(Control card, string text, int top) { card.Controls.Add(MakeLabel(text, 20, top, 8, Theme.TextFaint)); }
        private static Label AddValue(Control card, string text, int top)
        {
            Label label = MakeLabel(text, 210, top - 2, 9.5f, Theme.Text); label.AutoSize = false; label.Width = 450; label.Height = 25;
            label.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; card.Controls.Add(label); return label;
        }
    }

    internal sealed class OutageHistory
    {
        private readonly string path;
        public OutageHistory()
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LagWatch");
            Directory.CreateDirectory(directory); path = Path.Combine(directory, "outage-events.csv");
        }

        public void Record(DateTime timestamp, string diagnosis)
        {
            Prune(timestamp.AddDays(-30));
            if (diagnosis == "HEALTHY") return;
            string last = File.Exists(path) ? File.ReadLines(path).LastOrDefault() : null;
            DateTime previous;
            if (last != null && DateTime.TryParse(last.Split(',')[0], out previous) && timestamp - previous < TimeSpan.FromMinutes(15)) return;
            if (!File.Exists(path)) File.WriteAllText(path, "timestamp,classification" + Environment.NewLine);
            File.AppendAllText(path, timestamp.ToString("o") + "," + diagnosis + Environment.NewLine);
        }

        public string GetRecurringWarning(DateTime now)
        {
            if (!File.Exists(path)) return "no recurring local outage pattern learned yet";
            int matches = 0; var days = new System.Collections.Generic.HashSet<DateTime>();
            foreach (string line in File.ReadLines(path).Skip(1))
            {
                DateTime timestamp; if (!DateTime.TryParse(line.Split(',')[0], out timestamp)) continue;
                double distance = Math.Abs((timestamp.TimeOfDay - now.TimeOfDay).TotalMinutes); distance = Math.Min(distance, 1440 - distance);
                if (timestamp.Date != now.Date && distance <= 30) { matches++; days.Add(timestamp.Date); }
            }
            return matches >= 3 && days.Count >= 3
                ? "elevated recurring-outage risk near this time (" + matches + " events across " + days.Count + " days)"
                : "learning · " + matches + " matching prior event(s), requires 3 days";
        }

        private void Prune(DateTime cutoff)
        {
            if (!File.Exists(path)) return;
            string[] retained = File.ReadLines(path).Skip(1).Where(line =>
            { DateTime timestamp; return DateTime.TryParse(line.Split(',')[0], out timestamp) && timestamp >= cutoff; }).ToArray();
            File.WriteAllLines(path, new[] { "timestamp,classification" }.Concat(retained).ToArray());
        }
    }
}
