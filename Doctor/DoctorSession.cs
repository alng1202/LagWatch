using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Netfix.Diagnostics;
using Netfix.Model;

namespace LagWatch.Doctor
{
    internal enum FixPhase { Running, Done, Failed }

    internal sealed record FixState(FixPhase Phase, string Message);

    /// <summary>
    /// Network Doctor's state: NetFix's checkup (PC health → OSI layers 1–4 → 5–7 → speed test) and its one-click fixes,
    /// ported from Netfix's WPF AppState to WinForms. Events are raised on the UI thread that created the session.
    /// The OSI offline rule and bottom-up advice come unchanged from the NetFix engine.
    /// </summary>
    internal sealed class DoctorSession : IDisposable
    {
        public event Action Changed;

        private readonly SynchronizationContext ui;
        private readonly DiagnosticEngine engine = DiagnosticEngine.Live();
        public ConnectivityMonitor Connectivity { get; } = new ConnectivityMonitor();

        public bool IsRunning { get; private set; }
        public string Step { get; private set; } = "";
        public double Progress { get; private set; }
        public CheckupResult Result { get; private set; }
        public List<Issue> ResolvedIssues { get; } = new List<Issue>();
        public Dictionary<(string, Fix), FixState> FixStates { get; } = new Dictionary<(string, Fix), FixState>();
        public bool IsFixingAll { get; private set; }

        /// <summary>The latest layer reports used by Network details and network diagnosis.</summary>
        public List<LayerReport> Reports { get; private set; } = new List<LayerReport>();
        public bool LayersRunning { get; private set; }
        public DateTime? LayersChecked { get; private set; }

        private DateTime lastRecheck = DateTime.MinValue;
        private bool rechecking;

        public DoctorSession()
        {
            ui = SynchronizationContext.Current ?? new SynchronizationContext();
            Connectivity.Changed += () => Post(() =>
            {
                Notify();
                ScheduleRecheck(TimeSpan.FromSeconds(4));
            });
        }

        private void Post(Action action) { ui.Post(_ => action(), null); }

        public void Notify() { Changed?.Invoke(); }

        public void Clear()
        {
            if (IsRunning) return;
            Result = null;
            ResolvedIssues.Clear();
            FixStates.Clear();
            Notify();
        }

        public async Task RunCheckup(CheckScope scope)
        {
            if (IsRunning) return;
            IsRunning = true;
            Result = null;
            ResolvedIssues.Clear();
            FixStates.Clear();
            Notify();
            CheckupResult result = await Diagnose(scope, true, null);
            Result = result;
            if (result.Reports.Count > 0) { Reports = result.Reports; LayersChecked = DateTime.Now; }
            IsRunning = false;
            lastRecheck = DateTime.Now;
            Notify();
        }

        /// <summary>A quiet network-only pass used by the background watch. Returns the problems it found.</summary>
        public async Task<CheckupResult> QuietNetworkScan()
        {
            CheckupResult result = await Diagnose(CheckScope.Network, false, null);
            if (result.Reports.Count > 0) { Reports = result.Reports; LayersChecked = DateTime.Now; Notify(); }
            return result;
        }

        private void SetStep(string step, double progress)
        {
            Step = step;
            Progress = progress;
            Notify();
        }

        private async Task<CheckupResult> Diagnose(CheckScope scope, bool interactive, HealthItem keepSpeed)
        {
            Action<string, double> step = (s, p) => { if (interactive) SetStep(s, p); };
            var issues = new List<Issue>();
            var healthy = new List<HealthItem>();
            var blocked = new List<Topic>();
            var reports = new List<LayerReport>();

            if (scope.ChecksPerformance() || scope.ChecksHardware())
            {
                step("Checking your PC…", 0.05);
                var pc = await Task.Run(() => PcHealth.Check(scope.ChecksPerformance(), scope.ChecksHardware()));
                issues.AddRange(pc.Issues);
                healthy.AddRange(pc.Healthy);
            }

            if (scope.ChecksSound())
            {
                step("Checking sound, camera and Bluetooth…", 0.12);
                var calls = await Task.Run(Calls.Check);
                issues.AddRange(calls.Issues);
                healthy.AddRange(calls.Healthy);
            }

            if (scope.ChecksNetwork())
            {
                var titles = new Dictionary<OSILayer, string>
                {
                    [OSILayer.Physical] = "Checking your Wi‑Fi or cable…",
                    [OSILayer.DataLink] = "Looking for your router…",
                    [OSILayer.Network] = "Checking your network settings…",
                    [OSILayer.Transport] = "Checking connections to your router…",
                    [OSILayer.Session] = "Checking the internet…",
                    [OSILayer.Presentation] = "Checking secure websites…",
                    [OSILayer.Application] = "Checking websites…",
                };
                foreach (var layer in OSILayers.All)
                {
                    step(titles[layer], 0.2 + 0.1 * ((int)layer - 1));
                    reports.Add(await Task.Run(() => engine.Run(layer)));
                }
                string adapter = NetworkInfo.Primary()?.Name ?? "Wi-Fi";
                var eval = NetworkAdvisor.Evaluate(reports, adapter);
                issues.InsertRange(0, eval.Issues);
                healthy.InsertRange(0, eval.Healthy);
                blocked.AddRange(eval.Blocked);
            }

            var session = reports.FirstOrDefault(r => r.Layer == OSILayer.Session);
            if (scope.RunsSpeedTest() && session != null &&
                (session.Status == CheckStatus.Pass || session.Status == CheckStatus.Warning) &&
                !issues.Any(i => i.Severity == Severity.Problem && !i.Topic.IsPC()))
            {
                step("Measuring your internet speed…", 0.92);
                var context = await Task.Run(SpeedContext);
                var mbps = await SpeedTest.DownloadMbps();
                var usual = context.Network is null ? null : SpeedHistory.Usual(context.Network);
                var verdict = SpeedTest.Evaluate(mbps, context.Wifi, usual);
                if (mbps is { } m && context.Network is not null) SpeedHistory.Record(context.Network, m);
                if (verdict.Issue is not null) issues.Add(verdict.Issue);
                if (verdict.Healthy is not null) healthy.Add(verdict.Healthy);
            }
            else if (keepSpeed is not null) healthy.Add(keepSpeed);

            step("Done", 1);
            return new CheckupResult
            {
                Issues = issues.OrderByDescending(i => i.Severity).ThenBy(i => i.Topic.IsPC()).ToList(),
                Healthy = healthy, Blocked = blocked, Scope = scope, Reports = reports,
            };
        }

        private static async Task<(string Network, SpeedTest.WiFiLink Wifi)> SpeedContext()
        {
            var adapters = NetworkInfo.Adapters();
            var primary = NetworkInfo.Primary(adapters);
            var router = NetworkInfo.Router(adapters, primary);
            var mac = router is null ? null : Native.ArpLookup(router);
            var vpn = adapters.FirstOrDefault(a => a.Up && a.Kind == NetworkInfo.AdapterKind.Vpn)?.Description;
            SpeedTest.WiFiLink link = null;
            if (primary?.IsWiFi == true)
            {
                var wlan = await NetworkInfo.Wlan();
                if (wlan.Interfaces.FirstOrDefault(w => w.Connected) is { Dbm: { } dbm } w)
                    link = new SpeedTest.WiFiLink(dbm, w.TransmitMbps ?? 0);
            }
            return (mac is null ? null : mac + (vpn is null ? "" : "+" + vpn), link);
        }

        /// <summary>Quietly reruns the scan on screen (no speed test) after a fix or a connection change. At most every 15 s.</summary>
        public void ScheduleRecheck(TimeSpan delay)
        {
            if (Result is null || IsRunning) return;
            var timer = new System.Windows.Forms.Timer { Interval = (int)Math.Max(1, delay.TotalMilliseconds) };
            timer.Tick += async (s, e) =>
            {
                timer.Stop();
                timer.Dispose();
                await Recheck(false);
            };
            timer.Start();
        }

        public async Task Recheck(bool force)
        {
            CheckupResult current = Result;
            if (current is null || IsRunning || rechecking) return;
            if (!force && DateTime.Now - lastRecheck < TimeSpan.FromSeconds(15)) return;
            rechecking = true;
            lastRecheck = DateTime.Now;
            var fresh = await Diagnose(current.Scope, false, current.Healthy.FirstOrDefault(h => h.Topic == Topic.Speed));
            rechecking = false;
            if (IsRunning || Result is null) return;
            var speed = current.Issues.FirstOrDefault(i => i.Topic == Topic.Speed);
            if (speed != null) fresh.Issues.Add(speed);
            foreach (var gone in current.Issues.Where(i => fresh.Issues.All(f => f.Id != i.Id)))
                if (ResolvedIssues.All(r => r.Id != gone.Id)) ResolvedIssues.Add(gone);
            ResolvedIssues.RemoveAll(r => fresh.Issues.Any(f => f.Id == r.Id));
            Result = fresh;
            if (fresh.Reports.Count > 0) { Reports = fresh.Reports; LayersChecked = DateTime.Now; }
            Notify();
        }

        /// <summary>Runs a fix the user clicked (and already confirmed, where it asks). Tier checks happen in the UI.</summary>
        public async Task RunFix(Issue issue, Fix fix)
        {
            var key = (issue.Id, fix);
            FixState state;
            if (FixStates.TryGetValue(key, out state) && state.Phase == FixPhase.Running) return;
            FixStates[key] = new FixState(FixPhase.Running, "");
            Notify();
            var outcome = await FixRunner.Run(fix);
            FixStates[key] = new FixState(outcome.Success ? FixPhase.Done : FixPhase.Failed, outcome.Message);
            Notify();
            if (outcome.Success && fix.Kind != FixKind.Open) ScheduleRecheck(TimeSpan.FromSeconds(4));
            lastRecheck = DateTime.MinValue;
        }

        /// <summary>Fix all: each problem's first fix that changes something and that this plan allows, once each.</summary>
        public static List<(Issue Issue, Fix Fix)> FixAllPlan(IEnumerable<Issue> issues, SubscriptionTier tier)
        {
            var plan = new List<(Issue, Fix)>();
            foreach (var issue in issues)
            {
                var fix = issue.Fixes.FirstOrDefault(f => f.RunsInFixAll && SubscriptionPolicy.CanRunFix(tier, f));
                if (fix != null && plan.All(p => p.Item2 != fix)) plan.Add((issue, fix));
            }
            return plan;
        }

        public static string FixAllSummary(IReadOnlyList<(Issue Issue, Fix Fix)> plan)
        {
            var lines = plan.Select(p => "• " + p.Fix.Label + " — " + p.Issue.Title).ToList();
            var notes = plan.Select(p => p.Fix.Confirmation).Where(c => c is not null).Distinct().ToList();
            if (notes.Count > 0) lines.Add("\n" + string.Join("\n", notes));
            if (plan.Any(p => p.Fix.NeedsAdmin)) lines.Add("\nWindows may ask you to allow changes (click Yes).");
            return string.Join("\n", lines);
        }

        public async Task FixAll(IReadOnlyList<(Issue Issue, Fix Fix)> plan)
        {
            IsFixingAll = true;
            Notify();
            foreach (var item in plan) await RunFix(item.Issue, item.Fix);
            IsFixingAll = false;
            Notify();
        }

        /// <summary>Network details: runs every layer again without the device checks.</summary>
        public async Task RunAllLayers()
        {
            if (LayersRunning) return;
            LayersRunning = true;
            Reports = new List<LayerReport>();
            Notify();
            foreach (var layer in OSILayers.All)
            {
                var report = await Task.Run(() => engine.Run(layer));
                Reports = Reports.Append(report).ToList();
                Notify();
            }
            LayersRunning = false;
            LayersChecked = DateTime.Now;
            Notify();
        }

        /// <summary>Preview rendering only (--render-doctor): shows sample results without scanning.</summary>
        internal void ShowDemo(CheckupResult demo)
        {
            Result = demo;
            Reports = demo.Reports;
            LayersChecked = DateTime.Now;
            Notify();
        }

        internal static CheckupResult DemoResult()
        {
            var reports = new List<LayerReport>
            {
                new LayerReport(OSILayer.Physical, new[] { new Finding("Wi‑Fi signal", CheckStatus.Warning, "-74 dBm (fair)", "Move closer to the router.", IssueCode.FairSignal) }),
                new LayerReport(OSILayer.Network, new[] { new Finding("DNS settings", CheckStatus.Warning, "Typed in by hand: 203.0.113.9", "Use automatic DNS.") }),
                new LayerReport(OSILayer.Presentation, new[] { new Finding("Encryption", CheckStatus.Pass, "Certificates are trusted") }),
                new LayerReport(OSILayer.Application, new[]
                {
                    new Finding("Firewall", CheckStatus.Pass, "On for all profiles"),
                    new Finding("Hosts file", CheckStatus.Warning, "Blocks login.microsoftonline.com", "Remove the block.", IssueCode.HostsFileBlocks),
                    new Finding("Proxy", CheckStatus.Pass, "No proxy"),
                }),
            };
            return new CheckupResult
            {
                Scope = CheckScope.Full,
                Reports = reports,
                Issues = new List<Issue>
                {
                    new Issue
                    {
                        Id = "dnsServersDown", Topic = Topic.Websites, Title = "Websites won't open: your lookup settings don't answer",
                        Explanation = "This PC was set by hand to use website lookup servers that aren't answering.",
                        Fixes = { Fix.UseAutomaticDNS("Wi-Fi"), Fix.FlushDNS }, Steps = { "Click Fix website lookups." },
                        Technical = "DNS 203.0.113.9 timed out; 1.1.1.1 answered in 14 ms",
                    },
                    new Issue
                    {
                        Id = "hostsBlocks", Severity = Severity.Warning, Topic = Topic.Security, Title = "A setting on this PC blocks Microsoft sign-in",
                        Explanation = "Old software left lines in the hosts file that send login.microsoftonline.com nowhere.",
                        Fixes = { Fix.UnblockSites(new[] { "login.microsoftonline.com" }) },
                    },
                    new Issue
                    {
                        Id = "weakSignal", Severity = Severity.Warning, Topic = Topic.Connection, Title = "Your Wi‑Fi signal is only fair",
                        Explanation = "Games and calls can stutter. Moving closer to the router helps.",
                        Fixes = { Fix.RestartWiFi, Fix.Open(Destination.WifiSettings) },
                        Steps = { "Move closer to your router, or remove what's between you and it." },
                    },
                },
                Healthy = new List<HealthItem>
                {
                    new HealthItem(Topic.Processor, "12% busy"), new HealthItem(Topic.Memory, "41% free"),
                    new HealthItem(Topic.Router, "Answers in 2 ms"), new HealthItem(Topic.Internet, "Reachable"),
                },
            };
        }

        public void Dispose() { Connectivity.Dispose(); }
    }
}
