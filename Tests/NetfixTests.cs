using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Netfix.App;
using Netfix.Diagnostics;
using Netfix.Model;
using Xunit;

namespace Netfix.Tests;

// MARK: - The core rule: layers 1–4 offline, 5–7 need the internet

public class OSIRuleTests
{
    [Fact]
    public void OnlyLayersFiveToSevenNeedTheInternet()
    {
        Assert.Equal(new[] { 1, 2, 3, 4 }, OSILayers.OfflineLayers.Select(l => (int)l));
        Assert.Equal(new[] { 5, 6, 7 }, OSILayers.InternetLayers.Select(l => (int)l));
    }

    private sealed class FakeCheck : ILayerCheck
    {
        public OSILayer Layer { get; init; }
        public bool Ran { get; private set; }
        public Task<List<Finding>> Run()
        {
            Ran = true;
            return Task.FromResult(new List<Finding> { new("Fake", CheckStatus.Pass, "ok") });
        }
    }

    [Fact]
    public async Task OfflineSkipsInternetLayersButRunsLocalOnes()
    {
        var checks = OSILayers.All.Select(l => new FakeCheck { Layer = l }).ToList();
        var engine = new DiagnosticEngine(checks, () => false);
        foreach (var layer in OSILayers.All)
        {
            var report = await engine.Run(layer);
            Assert.Equal(layer.RequiresInternet() ? CheckStatus.Skipped : CheckStatus.Pass, report.Status);
        }
        Assert.All(checks, c => Assert.Equal(!c.Layer.RequiresInternet(), c.Ran));
    }

    [Fact]
    public void SkippedInternetLayersAreNotFailures()
    {
        var reports = OSILayers.All.Select(l => l.RequiresInternet()
            ? LayerReport.Skipped(l, DiagnosticEngine.OfflineReason)
            : new LayerReport(l, new[] { new Finding("x", CheckStatus.Pass, "ok") })).ToList();
        var eval = NetworkAdvisor.Evaluate(reports, "Wi-Fi");
        Assert.Empty(eval.Issues);
        Assert.Contains(Topic.Internet, eval.Blocked);
        Assert.Contains("skipped", Diagnosis.Summarize(reports));
    }
}

// MARK: - Advisor: bottom-up, one root cause

public class AdvisorTests
{
    private static LayerReport R(OSILayer layer, params Finding[] f) => new(layer, f);

    [Fact]
    public void ShowsOnlyTheLowestFailure()
    {
        var reports = new List<LayerReport>
        {
            R(OSILayer.Physical, new Finding("Network link", CheckStatus.Fail, "none", code: IssueCode.NoLink)),
            R(OSILayer.Application, new Finding("Web request", CheckStatus.Fail, "x", code: IssueCode.WebFailed)),
        };
        var eval = NetworkAdvisor.Evaluate(reports, "Wi-Fi");
        Assert.Single(eval.Issues);
        Assert.Equal(nameof(IssueCode.NoLink), eval.Issues[0].Id);
        Assert.Contains(Topic.Websites, eval.Blocked);
    }

    [Fact]
    public void WifiOffAddsTurnOnOnlyWhenNothingConnects()
    {
        var reports = new List<LayerReport>
        {
            R(OSILayer.Physical, new Finding("Wi‑Fi radio", CheckStatus.Info, "off", code: IssueCode.WifiOff),
                                 new Finding("Network link", CheckStatus.Fail, "none", code: IssueCode.NoLink)),
        };
        var issue = NetworkAdvisor.Evaluate(reports, "Wi-Fi").Issues.Single();
        Assert.Equal(Fix.TurnWiFiOn, issue.Fixes[0]);

        var onCable = new List<LayerReport> { R(OSILayer.Physical, new Finding("Wi‑Fi radio", CheckStatus.Info, "off", code: IssueCode.WifiOff)) };
        Assert.Empty(NetworkAdvisor.Evaluate(onCable, "Ethernet").Issues);
    }

    [Fact]
    public void RouterIgnoringPingIsFineWhenItAnswersArp()
    {
        var reports = new List<LayerReport>
        {
            R(OSILayer.DataLink, new Finding("Router ARP", CheckStatus.Pass, "ok")),
            R(OSILayer.Network, new Finding("Ping router", CheckStatus.Fail, "no replies", code: IssueCode.RouterNoPing)),
        };
        Assert.Empty(NetworkAdvisor.Evaluate(reports, "Wi-Fi").Issues);
    }

    [Fact]
    public void VpnIsBlamedOnlyWhenSomethingAboveTheRouterFails()
    {
        var vpn = new Finding("VPN", CheckStatus.Info, "Work VPN is connected", code: IssueCode.VpnOn, fixes: new[] { Fix.DisconnectVPN("Work VPN") });
        var quiet = NetworkAdvisor.Evaluate(new List<LayerReport> { R(OSILayer.Network, vpn) }, "Wi-Fi");
        Assert.Empty(quiet.Issues);

        var failing = NetworkAdvisor.Evaluate(new List<LayerReport>
        {
            R(OSILayer.Network, vpn),
            R(OSILayer.Session, new Finding("Internet sessions", CheckStatus.Fail, "none", code: IssueCode.InternetUnreachable)),
        }, "Wi-Fi");
        Assert.Equal(nameof(IssueCode.VpnOn), failing.Issues[0].Id);
        Assert.Contains(Fix.DisconnectVPN("Work VPN"), failing.Issues[0].Fixes);
    }

    [Fact]
    public void ManualSettingsGetFixNetworkSettingsInsteadOfRejoin()
    {
        var reports = new List<LayerReport>
        {
            R(OSILayer.Network, new Finding("IP address", CheckStatus.Fail, "bad", code: IssueCode.InvalidIP),
                                new Finding("Address source", CheckStatus.Info, "Set manually", code: IssueCode.ManualAddress)),
        };
        var issue = NetworkAdvisor.Evaluate(reports, "Ethernet").Issues.Single();
        Assert.Equal(Fix.UseAutomaticAddress("Ethernet"), issue.Fixes[0]);
    }

    [Fact]
    public void VpnStoppingFleetNamesTheVpn()
    {
        var reports = new List<LayerReport>
        {
            R(OSILayer.Network, new Finding("VPN", CheckStatus.Info, "Work VPN is connected", code: IssueCode.VpnOn, fixes: new[] { Fix.DisconnectVPN("Work VPN") })),
            R(OSILayer.Application, new Finding("Fleet", CheckStatus.Fail, "no answer", code: IssueCode.FleetUnreachable)),
        };
        var issue = NetworkAdvisor.Evaluate(reports, "Wi-Fi").Issues.Single();
        Assert.Equal("Your VPN is stopping Fleet from loading", issue.Title);
        Assert.Equal(FixKind.DisconnectVPN, issue.Fixes[0].Kind);
    }

    [Fact]
    public void HelpStepsAreForHomeUsersNeverAHelpDeskOrIT()
    {
        foreach (var code in Enum.GetValues<IssueCode>())
        {
            var issue = NetworkAdvisor.PlainIssue(code, "Wi-Fi");
            var text = issue.Title + " " + issue.Explanation + " " + string.Join(" ", issue.Steps);
            Assert.DoesNotContain(" IT ", text);
            foreach (var word in new[] { "help desk", "microsoft support", "office", "organization" })
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Mac", issue.Title + issue.Explanation + string.Join(" ", issue.Steps));
        }
    }
}

// MARK: - Layer checks' pure parts

public class CheckTests
{
    [Theory]
    [InlineData(81, -60)]
    [InlineData(56, -72)]
    [InlineData(40, -80)]
    public void SignalPercentToDbm(int percent, int dbm) => Assert.Equal(dbm, NetworkInfo.SignalToDbm(percent));

    [Fact]
    public void SignalLimitsMatchTheMac()
    {
        NetworkInfo.WlanInfo Wifi(int rssi) => new("Wi-Fi", "connected", "Home", "Home", 70, rssi, "802.11ax", "5 GHz", 36, 400, 400);
        Assert.Equal(CheckStatus.Pass, PhysicalCheck.WifiFindings(Wifi(-60), false).First(f => f.Title == "Wi‑Fi signal").Status);
        Assert.Equal(IssueCode.FairSignal, PhysicalCheck.WifiFindings(Wifi(-70), false).First(f => f.Title == "Wi‑Fi signal").Code);
        Assert.Equal(IssueCode.WeakSignal, PhysicalCheck.WifiFindings(Wifi(-80), false).First(f => f.Title == "Wi‑Fi signal").Code);
        Assert.Equal(IssueCode.WifiOff, PhysicalCheck.WifiFindings(null, true).Single().Code);
    }

    [Fact]
    public void WifiBackupOnlyForLaptops()
    {
        Assert.Null(PhysicalCheck.WifiBackupFinding(laptop: false, wifiOn: false, wifiJoined: false));
        Assert.Equal(IssueCode.WifiOffOnCable, PhysicalCheck.WifiBackupFinding(true, false, false)!.Code);
        Assert.Equal(IssueCode.WifiNotJoinedOnCable, PhysicalCheck.WifiBackupFinding(true, true, false)!.Code);
        Assert.Null(PhysicalCheck.WifiBackupFinding(true, true, true));
    }

    private static NetworkInfo.Adapter Cable(long speed, long errors, long packets) =>
        new("Ethernet", "Realtek USB GbE", NetworkInfo.AdapterKind.Ethernet, true, speed, "00-11-22-33-44-55", 5,
            IPAddress.Parse("192.168.1.20"), 24, IPAddress.Parse("192.168.1.1"), new List<IPAddress>(), true, packets, 0, errors, 0);

    [Fact]
    public void CableSpeedAndErrors()
    {
        Assert.Equal(IssueCode.CableSlow, PhysicalCheck.EthernetFindings(Cable(100, 0, 1000), 1000).First().Code);
        Assert.Equal(CheckStatus.Pass, PhysicalCheck.EthernetFindings(Cable(1000, 0, 1000), 1000).First().Status);
        Assert.Equal(IssueCode.CableErrors, PhysicalCheck.EthernetFindings(Cable(1000, 50, 10_000), 1000).Last().Code);
        // A few errors from plugging in are normal.
        Assert.Equal(CheckStatus.Pass, PhysicalCheck.EthernetFindings(Cable(1000, 5, 1000), 1000).Last().Status);
        Assert.Equal(2500, PhysicalCheck.MaxSpeedFromChoices("10 Mbps Half Duplex\n1.0 Gbps Full Duplex\n2.5 Gbps Full Duplex"));
    }

    [Fact]
    public void AddressValidation()
    {
        Assert.Equal(IssueCode.SelfAssignedIP, NetworkCheck.IpAddressFindings(IPAddress.Parse("169.254.3.4"), 16, null, "Wi-Fi")[0].Code);
        Assert.Equal(CheckStatus.Pass, NetworkCheck.IpAddressFindings(IPAddress.Parse("10.0.0.5"), 24, IPAddress.Parse("10.0.0.1"), "Wi-Fi")[0].Status);
        Assert.Equal(IssueCode.GatewayMismatch, NetworkCheck.IpAddressFindings(IPAddress.Parse("10.0.0.5"), 24, IPAddress.Parse("10.0.1.1"), "Wi-Fi")[0].Code);
        Assert.Equal(IssueCode.InvalidIP, NetworkCheck.IpAddressFindings(IPAddress.Parse("10.0.0.255"), 24, null, "Wi-Fi")[0].Code);
    }

    [Fact]
    public void DnsSettings()
    {
        Assert.Equal(IssueCode.NoDNS, NetworkCheck.DnsSettingsFinding(new string[0]).Code);
        Assert.Equal(CheckStatus.Warning, NetworkCheck.DnsSettingsFinding(new[] { "1.1.1.1", "0.0.0.0" }).Status);
        Assert.Equal(CheckStatus.Pass, NetworkCheck.DnsSettingsFinding(new[] { "1.1.1.1" }).Status);
    }

    [Fact]
    public void LocalDnsDownBlamesRouterOnlyWhenItIsTheServer()
    {
        Assert.Equal(IssueCode.LocalDNSDown, TransportCheck.LocalDnsDownFinding("x", new[] { "192.168.1.1" }, "192.168.1.1").Code);
        Assert.Equal(IssueCode.NetworkDNSDown, TransportCheck.LocalDnsDownFinding("x", new[] { "10.1.1.10" }, "10.1.1.1").Code);
    }

    [Fact]
    public void SecureConnectionsBlockedVersusInternetDown()
    {
        Assert.Equal(IssueCode.HttpsBlocked, SessionCheck.UnreachableFinding(true).Code);
        Assert.Equal(IssueCode.InternetUnreachable, SessionCheck.UnreachableFinding(false).Code);
    }

    [Fact]
    public void LookupCause()
    {
        Assert.Equal(IssueCode.DnsServersDown, ApplicationCheck.LookupCause(true, new[] { "10.9.9.9" }, true, "Wi-Fi")!.Code);
        Assert.Null(ApplicationCheck.LookupCause(true, new string[0], true, "Wi-Fi"));
        Assert.Equal(IssueCode.DnsBlocked, ApplicationCheck.LookupCause(false, new string[0], true, "Wi-Fi")!.Code);
        Assert.Null(ApplicationCheck.LookupCause(false, new string[0], false, "Wi-Fi"));
    }
}

// MARK: - Parsers of Windows tools' output (samples from a real Windows 11 PC)

public class ParserTests
{
    private const string WlanSample = @"
There is 1 interface on the system:

    Name                   : Wi-Fi
    Description            : Intel(R) Wi-Fi 7 BE211 320MHz
    State                  : connected
    SSID                   : Cathy-wifi
    Band                   : 6 GHz
    Channel                : 165
    Radio type             : 802.11ax
    Receive rate (Mbps)    : 2042
    Transmit rate (Mbps)   : 576
    Signal                 : 81%
    Rssi                   : -63
    Profile                : Cathy-wifi
";

    [Fact]
    public void ParsesNetshWlan()
    {
        var w = NetworkInfo.ParseWlanInterfaces(WlanSample).Single();
        Assert.True(w.Connected);
        Assert.Equal("Cathy-wifi", w.Ssid);
        Assert.Equal(81, w.SignalPercent);
        Assert.Equal(-63, w.Dbm);
        Assert.Equal(165, w.Channel);
        Assert.Equal(576, w.TransmitMbps);
    }

    [Fact]
    public void ParsesDisabledAdapters()
    {
        var list = NetworkInfo.ParseInterfaceList(@"
Admin State    State          Type             Interface Name
-------------------------------------------------------------------------
Enabled        Connected      Dedicated        Wi-Fi
Disabled       Disconnected   Dedicated        Ethernet 2
");
        Assert.Equal(2, list.Count);
        Assert.True(list.Single(i => i.Name == "Ethernet 2").Disabled);
    }

    [Fact]
    public void FindsLeftoverVpnRoutes()
    {
        var routes = NetworkInfo.ParseRoutes(@"
Active Routes:
Network Destination        Netmask          Gateway       Interface  Metric
          0.0.0.0          0.0.0.0         10.0.0.1       10.0.0.191     35
          0.0.0.0        128.0.0.0       10.8.0.1       10.8.0.6      5
        128.0.0.0        128.0.0.0       10.8.0.1       10.8.0.6      5
===========================================================================
Persistent Routes:
  None");
        Assert.Equal(3, routes.Count);
        Assert.Equal(2, NetworkInfo.LeftoverVpnRoutes(routes, new[] { IPAddress.Parse("10.0.0.191") }).Count);
        Assert.Empty(NetworkInfo.LeftoverVpnRoutes(routes, new[] { IPAddress.Parse("10.8.0.6") }));
    }

    [Fact]
    public void ParsesRasdial()
    {
        Assert.Empty(NetworkInfo.ParseRasdial("No connections\r\nCommand completed successfully.\r\n"));
        Assert.Equal(new[] { "Work VPN" }, NetworkInfo.ParseRasdial("Connected to\r\nWork VPN\r\nCommand completed successfully.\r\n"));
    }

    [Fact]
    public void ParsesFirewallBlockAll()
    {
        var text = "Domain Profile Settings:\n----\nFirewall Policy                       BlockInbound,AllowOutbound\n\nPublic Profile Settings:\n----\nFirewall Policy                       BlockInboundAlways,AllowOutbound\n";
        Assert.Equal(new[] { "Public" }, NetworkInfo.ParseFirewallBlockAll(text));
    }

    [Fact]
    public void HostsFileBlocks()
    {
        var hosts = "# 0.0.0.0 google.com\n127.0.0.1 localhost\n0.0.0.0 login.microsoftonline.com\n10.0.0.5 intranet.example.com\n0.0.0.0 ads.tracker.net\n";
        Assert.Equal(new[] { "login.microsoftonline.com" }, NetworkInfo.BlockedSites(hosts));
    }

    [Fact]
    public void ParsesProxyAndManualDns()
    {
        Assert.Equal(("proxy.corp", 8443), SystemChecks.ParseProxy("http=proxy.corp:8080;https=proxy.corp:8443"));
        Assert.Equal(("10.1.1.1", 3128), SystemChecks.ParseProxy("10.1.1.1:3128"));
        Assert.Equal(new[] { "10.9.9.9", "10.9.9.10" },
            SystemChecks.ParseManualDns("Configuration for interface \"Wi-Fi\"\n    Statically Configured DNS Servers:    10.9.9.9\n                                          10.9.9.10\n"));
        Assert.Empty(SystemChecks.ParseManualDns("    DNS servers configured through DHCP:  192.168.1.1\n"));
    }

    [Fact]
    public void ParsesFleetServer()
    {
        Assert.Equal("https://fleet.example.com/", Fleet.ParseServer("\"C:\\Program Files\\Orbit\\bin\\orbit\\orbit.exe\" --fleet-url https://fleet.example.com --enroll-secret x")!.ToString());
        Assert.Null(Fleet.ParseServer("C:\\Windows\\svchost.exe -k netsvcs"));
    }

    [Fact]
    public void DnsQueryPacket()
    {
        var q = DnsServerProbe.Query("example.com", 0x1234);
        Assert.Equal(new byte[] { 0x12, 0x34 }, q.Take(2));
        Assert.Equal(7, q[12]); // "example"
        Assert.Equal("1.1.168.192.in-addr.arpa", DnsServerProbe.ReverseName(IPAddress.Parse("192.168.1.1")));
    }
}

// MARK: - PC health, speed, fix all

public class HealthAndFixTests
{
    [Fact]
    public void StorageLimits()
    {
        Assert.Null(PcHealth.StorageIssue((100_000_000_000, 500_000_000_000)));
        Assert.Equal(Severity.Warning, PcHealth.StorageIssue((12_000_000_000, 100_000_000_000))!.Severity);
        Assert.Equal(Severity.Problem, PcHealth.StorageIssue((4_000_000_000, 500_000_000_000))!.Severity);
    }

    [Fact]
    public void ProcessorHogGetsQuitOnlyWhenItIsAnApp()
    {
        var chrome = PcHealth.ProcessorIssue(new[] { new PcHealth.Usage("chrome", "Google Chrome", 55, 0, true, null) }, 60)!;
        Assert.Equal(Severity.Problem, chrome.Severity);
        Assert.Equal(Fix.QuitApp("Google Chrome", "chrome"), chrome.Fixes[0]);
        var defender = PcHealth.ProcessorIssue(new[] { new PcHealth.Usage("MsMpEng", "Microsoft Defender", 30, 0, false, "scanning") }, 40)!;
        Assert.Empty(defender.Fixes);
        Assert.Null(PcHealth.ProcessorIssue(new[] { new PcHealth.Usage("x", "X", 10, 0, true, null) }, 20));
    }

    [Fact]
    public void Uptime() => Assert.NotNull(PcHealth.UpkeepIssue(TimeSpan.FromDays(15)));

    [Fact]
    public void SpeedVerdicts()
    {
        Assert.NotNull(SpeedTest.Evaluate(50).Healthy);
        Assert.Equal("Your Wi‑Fi is slowing your internet", SpeedTest.Evaluate(2, new SpeedTest.WiFiLink(-80, 50)).Issue!.Title);
        Assert.Equal("Your internet is very slow", SpeedTest.Evaluate(2, new SpeedTest.WiFiLink(-50, 400)).Issue!.Title);
        Assert.Equal("speedBelowUsual", SpeedTest.Evaluate(20, null, usual: 100).Issue!.Id);
        Assert.Equal(3, SpeedHistory.Median(new double[] { 5, 1, 3 }));
        Assert.Null(SpeedHistory.Median(new double[] { 5, 1 }));
    }

    [Fact]
    public void FixAllRunsEachFixOnceAndSkipsSettingsButtons()
    {
        var a = new Issue { Id = "a", Fixes = { Fix.Open(Destination.WifiSettings), Fix.FlushDNS } };
        var b = new Issue { Id = "b", Fixes = { Fix.FlushDNS } };
        var c = new Issue { Id = "c", Fixes = { Fix.Open(Destination.StorageSettings) } };
        // LagWatch: Fix all lives in DoctorSession and is tier-aware; Pro behaves exactly like NetFix's AppState.FixAllPlan.
        var plan = LagWatch.Doctor.DoctorSession.FixAllPlan(new[] { a, b, c }, LagWatch.SubscriptionTier.Pro);
        Assert.Single(plan);
        Assert.Equal(Fix.FlushDNS, plan[0].Fix);
    }

    [Fact]
    public void FixAllOnlyIncludesFixesThePlanAllows()
    {
        var dns = new Issue { Id = "dns", Fixes = { Fix.UseAutomaticDNS("Wi-Fi"), Fix.FlushDNS } };
        var hosts = new Issue { Id = "hosts", Fixes = { Fix.UnblockSites(new[] { "example.com" }) } };
        var free = LagWatch.Doctor.DoctorSession.FixAllPlan(new[] { dns, hosts }, LagWatch.SubscriptionTier.Free);
        Assert.Single(free);
        Assert.Equal(Fix.FlushDNS, free[0].Fix);
        var plus = LagWatch.Doctor.DoctorSession.FixAllPlan(new[] { dns, hosts }, LagWatch.SubscriptionTier.Plus);
        Assert.Equal(2, plus.Count());
        Assert.Equal(FixKind.UseAutomaticDNS, plus[0].Fix.Kind);
    }

    [Fact]
    public void EveryFixKindHasATier()
    {
        foreach (FixKind kind in Enum.GetValues(typeof(FixKind)))
        {
            var fix = new Fix(kind, "x", "x");
            var tier = LagWatch.SubscriptionPolicy.RequiredTier(fix);
            Assert.True(LagWatch.SubscriptionPolicy.CanRunFix(LagWatch.SubscriptionTier.Pro, fix));
            if (fix.NeedsAdmin) Assert.NotEqual(LagWatch.SubscriptionTier.Free, tier);
        }
    }

    [Fact]
    public void UnblockScriptOnlyQuotesNames()
    {
        var script = FixRunner.UnblockScript(new[] { "evil'; rm" });
        Assert.Contains("'evil''; rm'", script);
    }
}
