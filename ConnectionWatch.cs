using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

// Connection heads-ups: LagWatch's always-on view of the user's internet connection.
//   Collect  — every 30 s, ping the router and two public servers (tray, whenever LagWatch runs) and keep a 5-minute
//              summary tagged with the provider/ASN, so a shared provider service can be added later without rework.
//   Predict  — times that were spotty on most earlier same weekdays (or weekdays/weekends) are flagged ahead of time.
//   Warn     — before a likely spotty time, while it's happening (with the likely cause and the fixes), when it's
//              back, and when outage monitors report problems on the provider's network. Nothing changes by itself.

namespace LagWatch
{
    /// <summary>How the connection is doing; higher is worse.</summary>
    internal enum LinkState { Healthy = 0, Spotty = 1, LocalOutage = 2, ProviderOutage = 3 }

    /// <summary>One 30-second check: the router and the wider internet.</summary>
    internal sealed class HealthProbe
    {
        public DateTime Time { get; set; }
        public bool AdapterUp { get; set; } = true;
        public bool GatewayKnown { get; set; }
        public bool GatewayOk { get; set; }
        public long GatewayMs { get; set; }
        public bool PublicOk { get; set; }
        public long PublicMs { get; set; }
    }

    /// <summary>The connection over the last few minutes.</summary>
    internal sealed class LinkStatus
    {
        public LinkState State { get; set; }
        /// <summary>The problem is on this side of the router (Wi‑Fi, cable, router).</summary>
        public bool LocalCause { get; set; }
        /// <summary>False when the router never answers pings, so the side of the problem can't be told.</summary>
        public bool CauseKnown { get; set; } = true;
        public double LossPercent { get; set; }
        public double GatewayLossPercent { get; set; }
        public double MedianMs { get; set; }
        public int Probes { get; set; }
    }

    internal static class LinkEvaluator
    {
        /// <summary>Probes considered: 10 × 30 s = the last 5 minutes.</summary>
        public const int Window = 10;

        public static bool IsOutage(LinkState state) { return state == LinkState.LocalOutage || state == LinkState.ProviderOutage; }

        /// <param name="baselineMs">Usual internet latency on this connection (0 when not learned yet).</param>
        public static LinkStatus Evaluate(IReadOnlyList<HealthProbe> probes, double baselineMs)
        {
            List<HealthProbe> recent = probes.Skip(Math.Max(0, probes.Count - Window)).ToList();
            LinkStatus status = new LinkStatus { Probes = recent.Count };
            if (recent.Count == 0) return status;

            List<HealthProbe> lastTwo = recent.Skip(Math.Max(0, recent.Count - 2)).ToList();
            if (lastTwo.Count == 2 && lastTwo.All(p => !p.AdapterUp))
            {
                status.State = LinkState.LocalOutage; status.LocalCause = true; status.LossPercent = 100;
                return status;
            }

            status.LossPercent = 100.0 * recent.Count(p => !p.PublicOk) / recent.Count;
            List<HealthProbe> withGateway = recent.Where(p => p.GatewayKnown).ToList();
            bool gatewayAnswers = withGateway.Any(p => p.GatewayOk); // some routers never answer pings
            status.GatewayLossPercent = withGateway.Count == 0 || !gatewayAnswers ? 0
                : 100.0 * withGateway.Count(p => !p.GatewayOk) / withGateway.Count;
            List<double> ok = recent.Where(p => p.PublicOk).Select(p => (double)p.PublicMs).OrderBy(v => v).ToList();
            status.MedianMs = ok.Count == 0 ? 0 : ok[ok.Count / 2];

            // Down: the last two checks reached nothing on the internet.
            if (lastTwo.Count == 2 && lastTwo.All(p => !p.PublicOk))
            {
                bool routerNow = lastTwo.Any(p => p.GatewayOk);
                status.State = routerNow ? LinkState.ProviderOutage : LinkState.LocalOutage;
                status.CauseKnown = routerNow || gatewayAnswers;
                status.LocalCause = !routerNow && gatewayAnswers;
                return status;
            }

            if (recent.Count < 4) return status;
            List<double> gatewayMs = withGateway.Where(p => p.GatewayOk).Select(p => (double)p.GatewayMs).OrderBy(v => v).ToList();
            bool localLossy = gatewayAnswers && status.GatewayLossPercent >= 20;
            bool localSlow = gatewayMs.Count >= 3 && gatewayMs[gatewayMs.Count / 2] >= 60;
            bool lossy = status.LossPercent >= 10;
            bool slow = ok.Count >= 3 && status.MedianMs >= Math.Max(150, baselineMs * 3);
            if (lossy || slow || localLossy || localSlow)
            {
                status.State = LinkState.Spotty;
                status.LocalCause = localLossy || localSlow;
            }
            return status;
        }
    }

    /// <summary>A 5-minute summary of the connection, as stored on disk.</summary>
    internal sealed class HealthBucket
    {
        public DateTime Start { get; set; }
        public string Provider { get; set; } = "";
        public string Asn { get; set; } = "";
        public int Probes { get; set; }
        public double LossPercent { get; set; }
        public double MedianMs { get; set; }
        public double GatewayLossPercent { get; set; }
        public LinkState State { get; set; }
    }

    /// <summary>network-health.csv: the connection's history, one row per 5 minutes, kept 60 days.</summary>
    internal sealed class HealthHistory
    {
        public const int KeepDays = 60;
        public const int BucketMinutes = 5;
        private const string Header = "bucket_start,provider,asn,probes,public_loss_pct,public_median_ms,gateway_loss_pct,state";
        private readonly string path;
        private DateTime lastPrune = DateTime.MinValue;

        public HealthHistory(string path) { this.path = path; }

        public static string DefaultPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LagWatch", "network-health.csv");
            }
        }

        public void Append(HealthBucket bucket)
        {
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            if (lastPrune.Date != bucket.Start.Date) { Prune(bucket.Start.AddDays(-KeepDays)); lastPrune = bucket.Start; }
            bool header = !File.Exists(path);
            using (StreamWriter writer = new StreamWriter(path, true, new UTF8Encoding(false)))
            {
                if (header) writer.WriteLine(Header);
                writer.WriteLine(String.Join(",", new[]
                {
                    bucket.Start.ToString("o", CultureInfo.InvariantCulture), Csv(bucket.Provider), Csv(bucket.Asn),
                    bucket.Probes.ToString(CultureInfo.InvariantCulture),
                    bucket.LossPercent.ToString("0.#", CultureInfo.InvariantCulture),
                    bucket.MedianMs.ToString("0", CultureInfo.InvariantCulture),
                    bucket.GatewayLossPercent.ToString("0.#", CultureInfo.InvariantCulture),
                    bucket.State.ToString()
                }));
            }
        }

        public List<HealthBucket> Load(DateTime since)
        {
            List<HealthBucket> result = new List<HealthBucket>();
            if (!File.Exists(path)) return result;
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(stream))
                {
                    reader.ReadLine();
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        HealthBucket bucket = ParseLine(line);
                        if (bucket != null && bucket.Start >= since) result.Add(bucket);
                    }
                }
            }
            catch (IOException) { }
            return result;
        }

        internal static HealthBucket ParseLine(string line)
        {
            string[] p = SplitCsv(line);
            DateTime start; int probes; double loss, median, gatewayLoss; LinkState state;
            if (p.Length != 8 || !DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out start)
                || !Int32.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out probes)
                || !Double.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out loss)
                || !Double.TryParse(p[5], NumberStyles.Float, CultureInfo.InvariantCulture, out median)
                || !Double.TryParse(p[6], NumberStyles.Float, CultureInfo.InvariantCulture, out gatewayLoss)
                || !Enum.TryParse(p[7], out state)) return null;
            return new HealthBucket { Start = start, Provider = p[1], Asn = p[2], Probes = probes, LossPercent = loss,
                MedianMs = median, GatewayLossPercent = gatewayLoss, State = state };
        }

        private void Prune(DateTime cutoff)
        {
            if (!File.Exists(path)) return;
            try
            {
                List<string> keep = File.ReadLines(path).Skip(1).Where(l => { HealthBucket b = ParseLine(l); return b != null && b.Start >= cutoff; }).ToList();
                string temporary = path + ".pruning";
                File.WriteAllLines(temporary, new[] { Header }.Concat(keep), new UTF8Encoding(false));
                File.Replace(temporary, path, null);
            }
            catch (IOException) { }
        }

        private static string Csv(string value)
        {
            value = value ?? "";
            return value.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        private static string[] SplitCsv(string line)
        {
            List<string> fields = new List<string>(); StringBuilder current = new StringBuilder(); bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else current.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
            fields.Add(current.ToString());
            return fields.ToArray();
        }
    }

    /// <summary>A coming stretch of time when the connection was spotty on most comparable earlier days.</summary>
    internal sealed class RiskWindow
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public bool High { get; set; }
        public int SpottyDays { get; set; }
        public int ObservedDays { get; set; }
        /// <summary>"Tuesdays", "weekdays" or "weekends".</summary>
        public string Basis { get; set; } = "";
        /// <summary>What usually happened: spotty, or down.</summary>
        public LinkState Usual { get; set; } = LinkState.Spotty;
        public string Key { get { return Start.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture); } }
    }

    internal sealed class RiskOutlook
    {
        public List<RiskWindow> Windows { get; set; } = new List<RiskWindow>();
        /// <summary>Earlier days with enough history (2+ hours) to learn from.</summary>
        public int LearnedDays { get; set; }
        public bool Ready { get { return LearnedDays >= RiskForecaster.RequiredDays; } }
        public static readonly RiskOutlook Empty = new RiskOutlook();
    }

    /// <summary>
    /// Flags coming half hours that were spotty or down on most comparable earlier days: the same weekday when there are
    /// 3+ of them, otherwise weekdays or weekends. A time-of-week pattern, not machine learning, and it says so.
    /// </summary>
    internal static class RiskForecaster
    {
        public const int RequiredDays = 3;
        private const int SlotMinutes = 30;

        private sealed class SlotScore
        {
            public DateTime Start; public int Observed; public int Spotty; public int Outages; public string Basis = "";
            public double Ratio { get { return Observed == 0 ? 0 : (double)Spotty / Observed; } }
            public bool Flagged { get { return Observed >= RequiredDays && Spotty >= 2 && Ratio >= 0.4; } }
            public bool High { get { return Spotty >= 3 && Ratio >= 0.6; } }
        }

        public static RiskOutlook Forecast(IEnumerable<HealthBucket> history, DateTime now, int hours)
        {
            Dictionary<DateTime, List<HealthBucket>> byDay = history.Where(b => b.Start.Date < now.Date)
                .GroupBy(b => b.Start.Date).ToDictionary(g => g.Key, g => g.ToList());
            RiskOutlook outlook = new RiskOutlook { LearnedDays = byDay.Count(d => d.Value.Count >= 24) };
            if (byDay.Count == 0) return outlook;

            DateTime first = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute < 30 ? 0 : 30, 0);
            RiskWindow open = null; SlotScore openBest = null;
            for (DateTime t = first; t < now.AddHours(hours); t = t.AddMinutes(SlotMinutes))
            {
                SlotScore score = Score(byDay, t);
                if (!score.Flagged)
                {
                    if (open != null) { outlook.Windows.Add(open); open = null; }
                    continue;
                }
                if (open == null) { open = new RiskWindow { Start = t }; openBest = null; }
                open.End = t.AddMinutes(SlotMinutes);
                open.High |= score.High;
                if (openBest == null || score.Ratio > openBest.Ratio || (score.Ratio == openBest.Ratio && score.Observed > openBest.Observed))
                {
                    openBest = score;
                    open.SpottyDays = score.Spotty; open.ObservedDays = score.Observed; open.Basis = score.Basis;
                    open.Usual = score.Outages * 2 >= score.Spotty ? LinkState.ProviderOutage : LinkState.Spotty;
                }
            }
            if (open != null) outlook.Windows.Add(open);
            return outlook;
        }

        private static SlotScore Score(Dictionary<DateTime, List<HealthBucket>> byDay, DateTime t)
        {
            List<DateTime> sameWeekday = byDay.Keys.Where(d => d.DayOfWeek == t.DayOfWeek && d >= t.Date.AddDays(-42)).ToList();
            SlotScore score = Score(byDay, sameWeekday, t);
            score.Basis = t.DayOfWeek + "s";
            if (score.Observed >= RequiredDays) return score;
            bool weekend = IsWeekend(t);
            List<DateTime> sameKind = byDay.Keys.Where(d => IsWeekend(d) == weekend && d >= t.Date.AddDays(-21)).ToList();
            SlotScore broader = Score(byDay, sameKind, t);
            broader.Basis = weekend ? "weekends" : "weekdays";
            return broader;
        }

        private static SlotScore Score(Dictionary<DateTime, List<HealthBucket>> byDay, List<DateTime> days, DateTime t)
        {
            SlotScore score = new SlotScore { Start = t };
            foreach (DateTime day in days)
            {
                DateTime from = day + t.TimeOfDay, to = from.AddMinutes(SlotMinutes);
                List<HealthBucket> inSlot = byDay[day].Where(b => b.Start >= from && b.Start < to).ToList();
                if (inSlot.Count < 3) continue; // LagWatch wasn't running long enough then to know
                score.Observed++;
                if (inSlot.Any(b => b.State != LinkState.Healthy)) score.Spotty++;
                if (inSlot.Any(b => LinkEvaluator.IsOutage(b.State))) score.Outages++;
            }
            return score;
        }

        private static bool IsWeekend(DateTime d) { return d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday; }
    }

    internal enum HeadsUpKind { Before, During, After, ProviderOutage }

    internal sealed class HeadsUp
    {
        public HeadsUpKind Kind { get; set; }
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        /// <summary>Clicking it should open Check &amp; Fix (the problem is on the user's side).</summary>
        public bool OffersFixes { get; set; }
        public bool Serious { get; set; }
    }

    /// <summary>The words of every heads-up, kept apart so they can be tested.</summary>
    internal static class HeadsUpText
    {
        private static string Time(DateTime t) { return t.ToString("h:mm tt", CultureInfo.CurrentCulture); }

        public static string ProviderOrDefault(string provider)
        {
            return String.IsNullOrWhiteSpace(provider) || provider.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)
                ? "your internet provider" : provider;
        }

        public static HeadsUp Before(RiskWindow w)
        {
            string what = LinkEvaluator.IsOutage(w.Usual) ? "down" : "spotty";
            return new HeadsUp
            {
                Kind = HeadsUpKind.Before, Serious = w.High,
                Title = "Heads-up: " + what + " internet likely at " + Time(w.Start),
                Body = "On " + w.SpottyDays + " of the last " + w.ObservedDays + " " + w.Basis + ", your internet was " + what +
                       " between " + Time(w.Start) + " and " + Time(w.End) + ". Save your work and finish big uploads or downloads before then.",
            };
        }

        public static HeadsUp During(LinkStatus s, string provider)
        {
            string who = ProviderOrDefault(provider);
            switch (s.State)
            {
                case LinkState.ProviderOutage:
                    return new HeadsUp { Kind = HeadsUpKind.During, Serious = true, Title = "Your internet is down",
                        Body = "Your router is working, but nothing beyond it answers, so the problem is on " + who +
                               "'s side. Save your work to this PC now." };
                case LinkState.LocalOutage:
                    return new HeadsUp { Kind = HeadsUpKind.During, Serious = true, OffersFixes = true, Title = "You're offline",
                        Body = s.CauseKnown
                            ? "Your PC can't reach your router. Click to troubleshoot your connection."
                            : "Your PC can't reach the internet. Click to troubleshoot your connection." };
                default:
                    if (s.LocalCause)
                        return new HeadsUp { Kind = HeadsUpKind.During, OffersFixes = true, Title = "Your Wi‑Fi is spotty right now",
                            Body = "Checks to your own router are failing or slow, so the problem is between this PC and the router. " +
                                   "Move closer to the router, or click to troubleshoot." };
                    return new HeadsUp { Kind = HeadsUpKind.During, Title = "Your internet is spotty right now",
                        Body = (s.LossPercent >= 1 ? s.LossPercent.ToString("0") + "% of checks to the internet are failing"
                                                   : "The internet is responding slowly (≈" + s.MedianMs.ToString("0") + " ms)") +
                               ", but your router answers fine, so it's on " + who + "'s side. Save your work and pause big uploads." };
            }
        }

        public static HeadsUp After(DateTime start, DateTime end, LinkState worst)
        {
            int minutes = Math.Max(1, (int)Math.Round((end - start).TotalMinutes));
            string what = LinkEvaluator.IsOutage(worst) ? "down" : "spotty";
            return new HeadsUp
            {
                Kind = HeadsUpKind.After, Title = "Your internet is back to normal",
                Body = "It was " + what + " for " + minutes + " min (" + Time(start) + "–" + Time(end) + "). " +
                       "If this keeps happening around this time, LagWatch will warn you beforehand.",
            };
        }

        public static HeadsUp ProviderAlert(string provider)
        {
            return new HeadsUp
            {
                Kind = HeadsUpKind.ProviderOutage, Serious = true,
                Title = "Possible outage at " + ProviderOrDefault(provider),
                Body = "Internet outage monitors report problems on your provider's network in the last 2 hours. " +
                       "Save your work in case your connection drops.",
            };
        }

        /// <summary>One line for the Outage Map and the forecast strip.</summary>
        public static string OutlookSummary(RiskOutlook outlook)
        {
            if (!outlook.Ready)
                return "learning your connection's patterns (" + Math.Min(outlook.LearnedDays, RiskForecaster.RequiredDays) + " of " +
                       RiskForecaster.RequiredDays + " days)";
            if (outlook.Windows.Count == 0) return "no spotty times expected";
            RiskWindow w = outlook.Windows[0];
            return (LinkEvaluator.IsOutage(w.Usual) ? "outage" : "spotty") + " likely " + Time(w.Start) + "–" + Time(w.End) +
                   " (" + w.SpottyDays + " of last " + w.ObservedDays + " " + w.Basis + ")" +
                   (outlook.Windows.Count > 1 ? " · " + (outlook.Windows.Count - 1) + " more later" : "");
        }
    }

    /// <summary>Asks IODA (Georgia Tech's free internet-outage monitor) about the provider's network; sends only the ASN.</summary>
    internal static class ProviderOutageFeed
    {
        /// <summary>IODA v2 alerts for one ASN between two Unix times.</summary>
        public static string AlertsUrl(string asn, long from, long until)
        {
            return "https://api.ioda.inetintel.cc.gatech.edu/v2/outages/alerts?entityType=asn&entityCode=" +
                Uri.EscapeDataString(asn) + "&from=" + from + "&until=" + until;
        }

        /// <summary>Alerts that signal a problem: level "critical" or "warning" ("normal" means it recovered).</summary>
        public static int CountProblems(string json)
        {
            return Regex.Matches(json ?? "", "\\\"level\\\"\\s*:\\s*\\\"(critical|warning)\\\"", RegexOptions.IgnoreCase).Count;
        }

        /// <summary>Problem alerts in the last <paramref name="hours"/> hours, or -1 when the feed can't be reached.</summary>
        public static async Task<int> RecentAlertsAsync(string asn, int hours)
        {
            try
            {
                using (WebClient client = OutageMapForm.NewClient())
                {
                    long until = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), from = until - hours * 3600L;
                    return CountProblems(await client.DownloadStringTaskAsync(AlertsUrl(asn, from, until)));
                }
            }
            catch { return -1; }
        }
    }

    /// <summary>
    /// Runs while LagWatch runs (including from the tray): checks the connection every 30 s, stores 5-minute summaries,
    /// keeps the outlook for the next 12 hours, and raises <see cref="HeadsUpRaised"/>. It never changes any settings.
    /// </summary>
    internal sealed class ConnectionWatch : IDisposable
    {
        public const int ProbeSeconds = 30;
        private static readonly string[] PublicTargets = { "1.1.1.1", "8.8.8.8" };

        private readonly HealthHistory history;
        private readonly Timer probeTimer;
        private readonly Timer outlookTimer;
        private readonly List<HealthProbe> probes = new List<HealthProbe>();
        private readonly List<HealthProbe> bucketProbes = new List<HealthProbe>();
        private readonly HashSet<string> warned = new HashSet<string>();
        private DateTime bucketStart = DateTime.MinValue;
        private LinkState bucketWorst;
        private bool probing, refreshingOutlook;
        private double baselineMs;
        private string provider = "", asn = "";
        private DateTime ispCheckedAt = DateTime.MinValue;
        private DateTime providerCheckedAt = DateTime.MinValue;
        private DateTime lastProviderAlert = DateTime.MinValue;
        private LinkState shownState = LinkState.Healthy;
        private int badStreak, goodStreak;
        private DateTime incidentStart;

        public ConnectionWatch(HealthHistory history)
        {
            this.history = history;
            probeTimer = new Timer { Interval = ProbeSeconds * 1000 };
            probeTimer.Tick += async delegate { await ProbeAsync(); };
            outlookTimer = new Timer { Interval = 10 * 60 * 1000 };
            outlookTimer.Tick += async delegate { await RefreshOutlookAsync(); };
        }

        public event Action<HeadsUp> HeadsUpRaised;
        public event Action OutlookChanged;
        /// <summary>Whether provider outage checks may run (they reach IODA over the internet).</summary>
        public Func<bool> CanCheckProvider { get; set; } = () => false;
        public RiskOutlook Outlook { get; private set; } = RiskOutlook.Empty;
        public LinkStatus Current { get; private set; } = new LinkStatus();
        public string Provider { get { return provider; } }
        /// <summary>A spotty/down notice is showing and its "back to normal" hasn't come yet.</summary>
        public bool IncidentActive { get { return shownState != LinkState.Healthy; } }

        public void Start()
        {
            probeTimer.Start();
            outlookTimer.Start();
            var _ = RefreshOutlookAsync();
            var __ = ProbeAsync();
        }

        public void Dispose()
        {
            probeTimer.Stop(); outlookTimer.Stop();
            try { FlushBucket(); } catch (IOException) { }
            probeTimer.Dispose(); outlookTimer.Dispose();
        }

        private async Task ProbeAsync()
        {
            if (probing) return;
            probing = true;
            try
            {
                HealthProbe probe = await TakeProbeAsync();
                probes.Add(probe);
                if (probes.Count > 40) probes.RemoveAt(0);
                LinkStatus status = LinkEvaluator.Evaluate(probes, baselineMs);
                Current = status;
                AddToBucket(probe, status.State);
                UpdateIncident(probe.Time, status);
                WarnAhead(probe.Time);
                await RefreshProviderAsync(probe.Time);
            }
            catch (Exception) { }
            finally { probing = false; }
        }

        private static async Task<HealthProbe> TakeProbeAsync()
        {
            HealthProbe probe = new HealthProbe { Time = DateTime.Now, AdapterUp = NetworkInterface.GetIsNetworkAvailable() };
            string gateway = NetworkDiagnostics.GetDefaultGateway();
            probe.GatewayKnown = !String.IsNullOrWhiteSpace(gateway);
            Task<long> gatewayPing = probe.GatewayKnown ? PingAsync(gateway, 900) : Task.FromResult(-1L);
            long[] publicMs = await Task.WhenAll(PublicTargets.Select(t => PingAsync(t, 1400)));
            long gatewayMs = await gatewayPing;
            probe.GatewayOk = gatewayMs >= 0; probe.GatewayMs = Math.Max(0, gatewayMs);
            long[] answered = publicMs.Where(ms => ms >= 0).ToArray();
            probe.PublicOk = answered.Length > 0; probe.PublicMs = answered.Length > 0 ? answered.Min() : 0;
            return probe;
        }

        private static async Task<long> PingAsync(string target, int timeout)
        {
            try
            {
                using (Ping ping = new Ping())
                {
                    PingReply reply = await ping.SendPingAsync(target, timeout);
                    return reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;
                }
            }
            catch { return -1; }
        }

        private void AddToBucket(HealthProbe probe, LinkState state)
        {
            DateTime start = probe.Time.Date.AddMinutes(Math.Floor(probe.Time.TimeOfDay.TotalMinutes / HealthHistory.BucketMinutes) * HealthHistory.BucketMinutes);
            if (start != bucketStart)
            {
                try { FlushBucket(); } catch (IOException) { }
                bucketStart = start; bucketWorst = LinkState.Healthy;
            }
            bucketProbes.Add(probe);
            if (state > bucketWorst) bucketWorst = state;
        }

        private void FlushBucket()
        {
            if (bucketProbes.Count == 0) return;
            List<HealthProbe> p = bucketProbes.ToList();
            bucketProbes.Clear();
            List<double> ok = p.Where(x => x.PublicOk).Select(x => (double)x.PublicMs).OrderBy(v => v).ToList();
            List<HealthProbe> gw = p.Where(x => x.GatewayKnown).ToList();
            history.Append(new HealthBucket
            {
                Start = bucketStart, Provider = provider, Asn = asn, Probes = p.Count, State = bucketWorst,
                LossPercent = 100.0 * p.Count(x => !x.PublicOk) / p.Count,
                MedianMs = ok.Count == 0 ? 0 : ok[ok.Count / 2],
                GatewayLossPercent = gw.Count == 0 || !gw.Any(x => x.GatewayOk) ? 0 : 100.0 * gw.Count(x => !x.GatewayOk) / gw.Count,
            });
        }

        /// <summary>During: two bad checks in a row. Escalates once from spotty to down. After: two good minutes.</summary>
        private void UpdateIncident(DateTime now, LinkStatus status)
        {
            if (status.State != LinkState.Healthy) { badStreak++; goodStreak = 0; }
            else { goodStreak++; badStreak = 0; }

            if (shownState == LinkState.Healthy)
            {
                if (badStreak < 2) return;
                shownState = status.State;
                incidentStart = now.AddSeconds(-ProbeSeconds * (badStreak - 1));
                Raise(HeadsUpText.During(status, provider));
                return;
            }
            if (LinkEvaluator.IsOutage(status.State) && !LinkEvaluator.IsOutage(shownState))
            {
                shownState = status.State;
                Raise(HeadsUpText.During(status, provider));
                return;
            }
            if (status.State != LinkState.Healthy && status.State > shownState) shownState = status.State;
            if (goodStreak >= 4)
            {
                DateTime end = now.AddSeconds(-ProbeSeconds * (goodStreak - 1));
                Raise(HeadsUpText.After(incidentStart, end, shownState));
                shownState = LinkState.Healthy;
            }
        }

        /// <summary>Before: 30 minutes ahead of a likely spotty time, once per window.</summary>
        private void WarnAhead(DateTime now)
        {
            foreach (RiskWindow w in Outlook.Windows)
            {
                TimeSpan lead = w.Start - now;
                if (lead > TimeSpan.Zero && lead <= TimeSpan.FromMinutes(30) && warned.Add(w.Key))
                    Raise(HeadsUpText.Before(w));
            }
        }

        private async Task RefreshProviderAsync(DateTime now)
        {
            // The provider name tags the stored history (every plan); the outage feed is checked only when allowed.
            if (now - ispCheckedAt >= TimeSpan.FromHours(6))
            {
                ispCheckedAt = now;
                string isp = await OutageMapForm.DetectIspWithCacheAsync();
                if (!String.IsNullOrWhiteSpace(isp))
                {
                    Match m = Regex.Match(isp, "AS(\\d+)", RegexOptions.IgnoreCase);
                    asn = m.Success ? m.Groups[1].Value : asn;
                    provider = isp.Split(new[] { " · " }, StringSplitOptions.None)[0].Trim();
                }
            }
            if (String.IsNullOrEmpty(asn) || !CanCheckProvider() || now - providerCheckedAt < TimeSpan.FromMinutes(15)) return;
            providerCheckedAt = now;
            int alerts = await ProviderOutageFeed.RecentAlertsAsync(asn, 2);
            if (alerts > 0 && now - lastProviderAlert >= TimeSpan.FromHours(3))
            {
                lastProviderAlert = now;
                Raise(HeadsUpText.ProviderAlert(provider));
            }
        }

        public async Task RefreshOutlookAsync()
        {
            if (refreshingOutlook) return;
            refreshingOutlook = true;
            try
            {
                DateTime now = DateTime.Now;
                var result = await Task.Run(() =>
                {
                    List<HealthBucket> buckets = history.Load(now.AddDays(-HealthHistory.KeepDays));
                    List<double> usual = buckets.Where(b => b.Start >= now.AddDays(-7) && b.State == LinkState.Healthy && b.MedianMs > 0)
                        .Select(b => b.MedianMs).OrderBy(v => v).ToList();
                    return Tuple.Create(RiskForecaster.Forecast(buckets, now, 12), usual.Count >= 12 ? usual[usual.Count / 2] : 0);
                });
                Outlook = result.Item1;
                baselineMs = result.Item2;
                OutlookChanged?.Invoke();
                WarnAhead(DateTime.Now);
            }
            catch (Exception) { }
            finally { refreshingOutlook = false; }
        }

        private void Raise(HeadsUp headsUp) { HeadsUpRaised?.Invoke(headsUp); }
    }
}
