using System.Collections.Generic;
using System.Linq;
using Netfix.Model;

namespace Netfix.Diagnostics;

/// <summary>
/// Turns technical layer reports into plain-language issues for everyday users.
/// Troubleshooting is bottom-up: once a basic problem is found (say, no Wi‑Fi), problems further up
/// (websites failing) are just consequences, so they are hidden rather than piled on.
/// </summary>
public static class NetworkAdvisor
{
    // Steps are written for someone who has never troubleshot anything: one short action each,
    // everyday words, and the exact names of buttons and menus they'll see.

    /// <summary>Restarting the router.</summary>
    public static List<string> RestartRouterSteps => new()
    {
        "Find your router. It's the box with blinking lights that gives you Wi‑Fi.",
        "Unplug its power cord. If there's a second box for the internet, unplug that too.",
        "Wait 30 seconds.",
        "Plug everything back in.",
        "Wait 3 minutes, then click Check again.",
    };

    public static readonly List<string> ManualAddressSteps = new()
    {
        "Click Open Network settings.",
        "Click the network you're using (Wi‑Fi or Ethernet), then its properties.",
        "Next to IP assignment, click Edit.",
        "Choose Automatic (DHCP), then click Save.",
        "Click Check again.",
    };

    public static readonly List<string> CableSteps = new()
    {
        "Unplug the network cable from your computer, then push it back in until it clicks.",
        "Do the same at the other end of the cable.",
        "Try a different cable, if you have one.",
        "Try a different socket on the router.",
        "If the cable goes into a dock or adapter, try plugging it into a different one.",
    };

    public static List<string> VpnSteps => new()
    {
        "At home? Turn the VPN off and click Check again.",
        "On hotel, train or café Wi‑Fi? Sign in to the Wi‑Fi first, then turn the VPN on.",
        "In the VPN app, pick a location near you.",
        "Close the VPN app, then open it again.",
        "Still stuck? Restart your PC.",
        "Still not working? Contact your VPN company's support.",
    };

    public sealed record Evaluation(List<Issue> Issues, List<HealthItem> Healthy, List<Topic> Blocked);

    public static Evaluation Evaluate(IReadOnlyList<LayerReport> reports, string adapter)
    {
        var byLayer = reports.ToDictionary(r => r.Layer);
        var found = reports.SelectMany(r => r.Findings.Where(f => f.Code is not null).Select(f => (Layer: r.Layer, Finding: f))).ToList();
        var codes = found.Select(f => f.Finding.Code!.Value).ToHashSet();
        void Drop(params IssueCode[] drop) => found.RemoveAll(f => drop.Contains(f.Finding.Code!.Value));

        // Many routers ignore ping. If the router answered on the local link, a failed ping isn't a problem.
        if (codes.Contains(IssueCode.RouterNoPing)
            && byLayer.GetValueOrDefault(OSILayer.DataLink)?.Findings.Any(f => f.Title == "Router ARP" && f.Status == CheckStatus.Pass) == true)
            Drop(IssueCode.RouterNoPing);
        // A router whose own internet is down may be too busy forwarding to answer DNS; the outage is the real cause.
        if (codes.Contains(IssueCode.InternetUnreachable)) Drop(IssueCode.LocalDNSDown, IssueCode.NetworkDNSDown);
        // A connection switched off in settings explains "not connected"; show that instead.
        if (codes.Contains(IssueCode.ServiceDisabled)) Drop(IssueCode.NoLink);
        // A fair signal works fine; it's only worth a card when it's weak.
        Drop(IssueCode.FairSignal);
        // "Wi‑Fi is off" only matters when nothing else connects the PC.
        if (!codes.Contains(IssueCode.NoLink)) Drop(IssueCode.WifiOff);
        // A specific reason lookups fail replaces "Websites can't be found", and the failed web request with it.
        if (codes.Contains(IssueCode.DnsServersDown) || codes.Contains(IssueCode.DnsBlocked))
            Drop(IssueCode.DnsFailed, IssueCode.WebFailed, IssueCode.CaptivePortal);
        // Fleet failing along with all websites is just part of that bigger problem.
        if (codes.Overlaps(new[] { IssueCode.NoDNS, IssueCode.DnsFailed, IssueCode.CaptivePortal, IssueCode.WebFailed, IssueCode.TlsBlocked,
                                   IssueCode.InternetUnreachable, IssueCode.HttpsBlocked, IssueCode.DnsBlocked, IssueCode.DnsServersDown,
                                   IssueCode.VpnLeftoverRoutes, IssueCode.LocalDNSDown, IssueCode.NetworkDNSDown }))
            Drop(IssueCode.FleetUnreachable);

        var vpnFinding = found.FirstOrDefault(f => f.Finding.Code == IssueCode.VpnOn).Finding;
        var vpnBlocksFleet = vpnFinding is not null && found.Any(f => f.Finding.Code == IssueCode.FleetUnreachable);

        var relevant = found.Where(f => f.Finding.Status >= CheckStatus.Warning || f.Finding.Code == IssueCode.WifiOff).ToList();
        var failures = relevant.Where(f => f.Finding.Status == CheckStatus.Fail).Select(f => f.Layer).ToList();
        OSILayer? lowestFailure = failures.Count > 0 ? failures.Min() : null;

        // A VPN that's on is only worth mentioning when something from the router upward is failing, and nothing more
        // basic (no Wi‑Fi, no cable) explains it. When Fleet is the only thing failing, the VPN is named in that issue instead.
        if (!vpnBlocksFleet && found.FirstOrDefault(f => f.Finding.Code == IssueCode.VpnOn) is { Finding: not null } vpn
            && relevant.Any(f => f.Layer >= OSILayer.Network && f.Finding.Status == CheckStatus.Fail)
            && (lowestFailure is null || lowestFailure >= OSILayer.Network))
        {
            relevant.Insert(0, (vpn.Layer, new Finding(vpn.Finding.Title, CheckStatus.Warning, vpn.Finding.Detail, code: IssueCode.VpnOn, fixes: vpn.Finding.Fixes)));
        }

        // Fixes for the connection in use (e.g. resetting the cable port) help when the router can't be reached.
        var connectionFixes = byLayer.GetValueOrDefault(OSILayer.Physical)?.Findings.FirstOrDefault(f => f.Title == "Connection type")?.Fixes ?? new List<Fix>();
        var manualAddress = codes.Contains(IssueCode.ManualAddress);
        var onHotspot = byLayer.GetValueOrDefault(OSILayer.Physical)?.Findings.Any(f => f.Title == "Connection type" && IsHotspot(f.Detail)) == true;

        var issues = new List<Issue>();
        foreach (var (layer, finding) in relevant)
        {
            if (lowestFailure is { } low && layer > low) continue;
            if (finding.Code is not { } code || code == IssueCode.WifiOff || issues.Any(i => i.Id == code.ToString())) continue;
            var issue = vpnBlocksFleet && code == IssueCode.FleetUnreachable ? VpnBlocksFleetIssue(vpnFinding!.Fixes)
                : code == IssueCode.InternetUnreachable && onHotspot ? HotspotNoInternetIssue()
                : PlainIssue(code, adapter);
            foreach (var fix in finding.Fixes.Reverse()) if (!issue.Fixes.Contains(fix)) issue.Fixes.Insert(0, fix);
            if (code is IssueCode.RouterUnreachable or IssueCode.RouterNoPing or IssueCode.SelfAssignedIP or IssueCode.NoIP or IssueCode.NoGateway)
                issue.Fixes.AddRange(connectionFixes.Where(f => !issue.Fixes.Contains(f)));
            // Settings typed in by hand: switch to automatic for good, not just for this connection.
            if (manualAddress && issue.Fixes.IndexOf(Fix.RenewAddress(adapter)) is var i and >= 0)
                issue.Fixes[i] = Fix.UseAutomaticAddress(adapter);
            issue.Severity = finding.Status == CheckStatus.Fail ? Severity.Problem : Severity.Warning;
            issue.Technical = $"{layer.Title()} — {finding.Title}: {finding.Detail}";
            if (vpnBlocksFleet && code == IssueCode.FleetUnreachable) issue.Technical += $" · VPN: {vpnFinding!.Detail}";
            if (code == IssueCode.NoLink && codes.Contains(IssueCode.WifiOff)) issue.Fixes.Insert(0, Fix.TurnWiFiOn);
            issues.Add(issue);
        }

        // Topics above the lowest failure couldn't really be tested.
        var networkTopics = new[] { (Topic.Connection, OSILayer.Physical), (Topic.Router, OSILayer.Network), (Topic.Internet, OSILayer.Session),
                                    (Topic.Security, OSILayer.Presentation), (Topic.Websites, OSILayer.Application) };
        var healthy = new List<HealthItem>();
        var blocked = new List<Topic>();
        foreach (var (topic, layer) in networkTopics)
        {
            if (issues.Any(i => i.Topic == topic) || !byLayer.TryGetValue(layer, out var report)) continue;
            if ((lowestFailure is { } low && layer > low) || report.Status == CheckStatus.Skipped) { blocked.Add(topic); continue; }
            healthy.Add(new HealthItem(topic, HealthyDetail(topic, report)));
        }
        return new Evaluation(issues, healthy, blocked);
    }

    private static string HealthyDetail(Topic topic, LayerReport report) => topic switch
    {
        Topic.Connection when report.Findings.Any(f => f.Title == "Cable speed" && f.Status == CheckStatus.Pass)
                              && !report.Findings.Any(f => f.Title == "Cable errors" && f.Status != CheckStatus.Pass) => "Connected by cable, at full speed",
        Topic.Connection when report.Findings.Any(f => f.Title == "Connection type" && IsHotspot(f.Detail)) => "Connected through a phone hotspot",
        Topic.Connection when report.Findings.FirstOrDefault(f => f.Title == "Wi‑Fi signal")?.Status == CheckStatus.Pass => "Connected, strong Wi‑Fi signal",
        Topic.Connection => "Connected",
        Topic.Router => "Your router is responding",
        Topic.Internet => "Connected to the internet",
        Topic.Security => "Secure websites open",
        Topic.Websites => "Websites can be found and loaded",
        _ => "OK",
    };

    /// <summary>The connection finding's detail says the PC is on a phone hotspot (by Wi‑Fi or an iPhone cable).</summary>
    public static bool IsHotspot(string connectionDetail) =>
        connectionDetail.Contains("hotspot") || connectionDetail.Contains("iPhone or iPad connection");

    /// <summary>
    /// No internet through a phone hotspot: the phone has nothing to share. Replaces "Your internet is down",
    /// whose router steps don't apply.
    /// </summary>
    public static Issue HotspotNoInternetIssue() => new()
    {
        Id = nameof(IssueCode.InternetUnreachable), Topic = Topic.Internet,
        Title = "Your phone isn't sharing its internet",
        Explanation = "Your PC is connected to your phone's hotspot, but the phone has no internet to share. Usually cellular data is turned off on the phone, or there's no signal.",
        Steps = new()
        {
            "On your phone, open Settings.",
            "iPhone: tap Cellular, then turn on Cellular Data.",
            "Android: tap Network & internet, then turn on Mobile data.",
            "Look at your phone's signal bars. No bars means there's no internet to share.",
            "In another country? Turn on Data Roaming on your phone.",
            "Turn the hotspot off, then on again. Then click Check again.",
            "Still nothing? Your plan's hotspot data may be used up. Look for a text message about it on your phone.",
            Support.StillNotWorking,
        },
    };

    public static Issue PlainIssue(IssueCode code, string adapter)
    {
        Issue I(Topic topic, string title, string explanation, IEnumerable<Fix>? fixes = null, IEnumerable<string>? steps = null) => new()
        {
            Id = code.ToString(), Topic = topic, Title = title, Explanation = explanation,
            Fixes = fixes?.ToList() ?? new(), Steps = steps?.ToList() ?? new(),
        };

        return code switch
        {
            IssueCode.NoLink or IssueCode.WifiOff => I(Topic.Connection, "Your PC isn't connected",
                "Wi‑Fi isn't connected and no network cable is plugged in.",
                new[] { Fix.Open(Destination.WifiSettings) },
                new[] { "Click the Wi‑Fi icon at the bottom right of your screen, next to the clock.",
                        "Choose your network from the list, then click Connect.",
                        "Using a cable? Push it in firmly at both ends.",
                        "Using your phone's hotspot? Keep the hotspot screen open on your phone while you connect." }),
            IssueCode.WifiOffOnCable => I(Topic.Connection, "You'll lose internet when you unplug",
                "Your PC is online through its cable or dock, but Wi‑Fi is off. Unplug it and you're offline.",
                steps: new[] { "Click Turn Wi‑Fi on.",
                               "Click the Wi‑Fi icon at the bottom right of your screen.",
                               "Choose your network and tick Connect automatically.",
                               "Leave Wi‑Fi on, even at your desk. The cable is still used first." }),
            IssueCode.WifiNotJoinedOnCable => I(Topic.Connection, "You'll lose internet when you unplug",
                "Your PC is online through its cable or dock, but Wi‑Fi isn't connected to a network. Unplug it and you're offline.",
                steps: new[] { "Click the Wi‑Fi icon at the bottom right of your screen.",
                               "Choose your network.",
                               "Tick Connect automatically, then click Connect." }),
            IssueCode.WeakSignal => I(Topic.Connection, "Your Wi‑Fi signal is weak",
                "You're too far from the Wi‑Fi, or something is in the way. That makes everything slow.",
                steps: new[] { "Move closer to your router (the Wi‑Fi box).",
                               "Move the router somewhere open and high, not in a cupboard.",
                               "Keep it away from microwaves and thick walls." }),
            IssueCode.FairSignal => I(Topic.Connection, "Your Wi‑Fi signal could be stronger",
                "It works, but it may be slow now and then.",
                steps: new[] { "Move a little closer to your router (the Wi‑Fi box).",
                               "Keep the router away from microwaves and thick walls." }),
            IssueCode.CrowdedChannel => I(Topic.Connection, "Your Wi‑Fi is crowded",
                "Lots of Wi‑Fi networks nearby use the same channel as yours, so they take turns and everything slows down.",
                steps: RestartRouterSteps),
            IssueCode.CableSlow => I(Topic.Connection, "Your network cable is slow",
                "The cable is working, but slower than it should. An old or damaged cable, or a loose plug, usually causes this.",
                steps: CableSteps),
            IssueCode.CableHalfDuplex => I(Topic.Connection, "Your network cable isn't set up right",
                "A setting or a faulty cable is making the cable connection slow and choppy.",
                steps: CableSteps),
            IssueCode.CableErrors => I(Topic.Connection, "Your network cable is faulty",
                "Some of the data going through the cable gets damaged, which makes things slow and choppy. It's almost always the cable or a loose plug.",
                steps: CableSteps),
            IssueCode.VpnOn => I(Topic.Vpn, "Your VPN may be causing this",
                "Your VPN is on, and something isn't working. VPNs often cause trouble, especially on public Wi‑Fi.",
                steps: VpnSteps),
            IssueCode.ServiceDisabled => I(Topic.Connection, "Your connection is turned off in settings",
                "Wi‑Fi or your network cable has been switched off in Windows, so it can't connect.",
                steps: new[] { "Click Turn this connection back on above.",
                               "Click Yes if Windows asks.",
                               "Wait a few seconds, then click Check again.",
                               "Still off? Restart your PC." }),
            IssueCode.IpConflict => I(Topic.Router, "Another device is using your PC's address",
                "Two devices on this network are sharing one address, so your connection keeps breaking.",
                new[] { Fix.RenewAddress(adapter) },
                new[] { "Click Rejoin the network above.", "Click Yes if Windows asks.", "Still happening? Restart your router." }),
            IssueCode.FirewallBlocksIncoming => I(Topic.Incoming, "Your PC is blocking connections from other devices",
                "Screen sharing and file sharing to this PC won't work. Your internet isn't affected.",
                steps: new[] { "Don't need screen or file sharing? Leave it as it is. It keeps your PC safer.",
                               "Need it? Open Windows Security, then Firewall & network protection, then Allow an app through firewall." }),
            IssueCode.ProxyBroken => I(Topic.Websites, "A proxy setting is stopping websites",
                "Your PC sends websites through a proxy, and the proxy isn't answering.",
                new[] { Fix.Open(Destination.ProxySettings) },
                new[] { "Click Open Proxy settings.",
                        "Under Manual proxy setup, turn off Use a proxy server, unless you set it up on purpose.",
                        "Click Check again." }),
            IssueCode.RouterUnreachable or IssueCode.RouterNoPing => I(Topic.Router, "Your PC can't reach your router",
                "You're connected, but the router (the Wi‑Fi box) isn't answering.",
                new[] { Fix.RestartWiFi }, RestartRouterSteps),
            IssueCode.SelfAssignedIP or IssueCode.NoIP => I(Topic.Router, "Your PC couldn't join the network properly",
                "The router (the Wi‑Fi box) didn't let your PC in all the way, so nothing can get online.",
                new[] { Fix.RenewAddress(adapter), Fix.RestartWiFi }, RestartRouterSteps),
            IssueCode.InvalidIP or IssueCode.ManualAddress => I(Topic.Router, "Your network settings have a mistake",
                "Your PC's network settings were typed in by hand, and they don't work. The fix is to let your router set them for you.",
                new[] { Fix.RenewAddress(adapter), Fix.Open(Destination.NetworkSettings) }, ManualAddressSteps),
            IssueCode.GatewayMismatch => I(Topic.Router, "Your network settings don't match your router",
                "Your PC's network settings were typed in by hand, and they don't fit this router. The fix is to let your router set them for you.",
                new[] { Fix.RenewAddress(adapter), Fix.Open(Destination.NetworkSettings) }, ManualAddressSteps),
            IssueCode.BadDNSServer => I(Topic.Websites, "A setting for finding websites has a mistake",
                "Your PC uses this setting to find websites, and one entry in it is wrong.",
                new[] { Fix.UseAutomaticDNS(adapter), Fix.Open(Destination.NetworkSettings) },
                new[] { "Click Fix website lookups above.", "Click Yes if Windows asks.", "Click Check again." }),
            IssueCode.NetworkDNSDown => I(Topic.Websites, "Your network's website finders aren't answering",
                "This network has its own servers that find websites for your PC, and they've stopped answering. Your PC is fine.",
                steps: RestartRouterSteps.Append("Need it now? Use your phone's hotspot.").Append(Support.StillNotWorking)),
            IssueCode.LocalDNSDown => I(Topic.Websites, "Your router isn't helping your PC find websites",
                "Your router normally finds websites for your PC, but it has stopped. Websites won't load until it works again.",
                new[] { Fix.RestartWiFi, Fix.Open(Destination.NetworkSettings) },
                RestartRouterSteps.Append(Support.StillNotWorking)),
            IssueCode.NoGateway => I(Topic.Router, "Your PC doesn't know the way to the internet",
                "Your PC is connected, but it isn't set up to reach the internet through your router.",
                new[] { Fix.RenewAddress(adapter), Fix.Open(Destination.NetworkSettings) }, RestartRouterSteps),
            IssueCode.PacketLoss => I(Topic.Router, "Your connection keeps dropping out",
                "Some of what your PC sends never reaches the router. That causes stutters and slow pages, usually because the Wi‑Fi is weak.",
                new[] { Fix.RestartWiFi },
                new[] { "Move closer to your router (the Wi‑Fi box).", "Click Restart Wi‑Fi above.",
                        "Still dropping? Restart your router.", $"Happens a lot? {Support.ContactStep}" }),
            IssueCode.SlowLocal => I(Topic.Router, "Your router is slow to answer",
                "The router (the Wi‑Fi box) is taking a while to respond. Weak Wi‑Fi or a big download can cause this.",
                steps: new[] { "Ask if someone at home is downloading or streaming something big.", "Move closer to your router.", "Restart your router." }),
            IssueCode.TcpStackBroken => I(Topic.Router, "Your PC's networking is stuck",
                "Part of your PC that handles internet connections has stopped working properly.",
                steps: new[] { "Save your work.", "Click Start, then the power button, then Restart.", Support.PcStillNotWorking }),
            IssueCode.InternetUnreachable => I(Topic.Internet, "Your internet is down",
                "Your PC and router are fine, but the internet coming into your home isn't working.",
                steps: RestartRouterSteps.Append($"Still down? {Support.ContactStep}")),
            IssueCode.SlowInternetLatency => I(Topic.Internet, "Your internet is slow to respond",
                "Websites and video calls may lag.",
                steps: new[] { "Ask if someone at home is downloading or streaming something big.", "Restart your router.", $"Happens a lot? {Support.ContactStep}" }),
            IssueCode.ClockWrong => I(Topic.Security, "Your PC's clock is wrong",
                "Websites check the date to keep you safe, so a wrong clock stops them loading.",
                new[] { Fix.Open(Destination.DateTimeSettings) },
                new[] { "Click Open Date & time settings.", "Turn on Set time automatically.", "Click Sync now.", "Click Check again." }),
            IssueCode.TrafficIntercepted => I(Topic.Security, "Something is blocking secure websites",
                "Usually this is Wi‑Fi in a hotel, café or airport that wants you to sign in first.",
                new[] { Fix.Open(Destination.SignInPage) },
                new[] { "Click Open sign-in page.", "Accept the terms on the page that opens.",
                        "Not on public Wi‑Fi? Turn off your VPN and try again.", Support.StillNotWorking }),
            IssueCode.TlsBlocked => I(Topic.Security, "Secure websites aren't loading",
                "Your PC is online, but websites that start with https won't open.",
                new[] { Fix.Open(Destination.SignInPage) },
                new[] { "On public Wi‑Fi? Click Open sign-in page and accept the terms.", "Turn off your VPN, if you use one.",
                        "Try your phone's hotspot to see if it's this Wi‑Fi." }),
            IssueCode.NoDNS => I(Topic.Websites, "Your PC can't look up websites",
                "A setting your PC uses to find websites is missing, so no websites will load.",
                new[] { Fix.RenewAddress(adapter), Fix.Open(Destination.NetworkSettings) },
                new[] { "Click Rejoin the network above.", "Click Yes if Windows asks.", "Click Check again." }),
            IssueCode.DnsFailed => I(Topic.Websites, "Websites can't be found",
                "Your PC is online, but the service that finds websites isn't answering.",
                new[] { Fix.FlushDNS, Fix.Open(Destination.NetworkSettings) },
                new[] { "Click Refresh website lookups above." }.Concat(RestartRouterSteps)),
            IssueCode.DnsSlow => I(Topic.Websites, "Websites are slow to start loading",
                "Finding each website is taking too long, so every new page is slow to start.",
                new[] { Fix.FlushDNS },
                new[] { "Click Refresh website lookups above.", "Restart your router.", $"Happens a lot? {Support.ContactStep}" }),
            IssueCode.CaptivePortal => I(Topic.Websites, "This Wi‑Fi wants you to sign in",
                "Hotel, café, train and airport Wi‑Fi blocks the internet until you accept their terms.",
                new[] { Fix.Open(Destination.SignInPage) },
                new[] { "Click Open sign-in page.", "Accept the terms on the page that opens.", "Using a VPN? Turn it on only after you've signed in." }),
            IssueCode.DnsServersDown => I(Topic.Websites, "Your website lookup setting doesn't work",
                "Your PC was set by hand to find websites through a server that isn't answering. Your router can do this for you.",
                new[] { Fix.UseAutomaticDNS(adapter) },
                new[] { "Click Fix website lookups above, and click Yes if Windows asks.", "Click Check again." }),
            IssueCode.DnsBlocked => I(Topic.Websites, "This network is blocking website lookups",
                "Your PC is online, but this network won't let it find websites. Hotel, café and guest Wi‑Fi do this until you sign in. Your PC is fine.",
                new[] { Fix.Open(Destination.SignInPage) },
                new[] { "On hotel, café or guest Wi‑Fi? Click Open sign-in page and sign in.",
                        "At home? Restart your router.", "Need it now? Use your phone's hotspot." }),
            IssueCode.HostsFileBlocks => I(Topic.Websites, "A setting on your PC is blocking websites",
                "A hidden list on your PC (the hosts file) blocks some websites you need. Old software or an ad blocker usually leaves this behind.",
                steps: new[] { "Click Remove the block above, and click Yes if Windows asks.", "Click Check again." }),
            IssueCode.HttpsBlocked => I(Topic.Internet, "This network blocks secure websites",
                "Your PC reaches the internet, but this network stops secure websites (the ones starting with https) from loading. Your PC and router are fine.",
                new[] { Fix.Open(Destination.SignInPage) },
                new[] { "On hotel, café or guest Wi‑Fi? Click Open sign-in page and sign in.",
                        "At home? Restart your router.", "Need it now? Use your phone's hotspot." }),
            IssueCode.VpnLeftoverRoutes => I(Topic.Vpn, "An old VPN connection is blocking your internet",
                "A VPN that closed or crashed left its settings behind, so your PC still sends internet traffic to it.",
                steps: new[] { "Click Clear old VPN settings above, and click Yes if Windows asks.",
                               "Happens again after using the VPN? Restart your PC.", "Still happening? Contact your VPN company's support." }),
            IssueCode.WebFailed => I(Topic.Websites, "Websites aren't loading",
                "Your PC reached the internet, but a web page still didn't load.",
                new[] { Fix.FlushDNS },
                new[] { "Turn off your VPN, if you use one.", "Restart your router.", Support.StillNotWorking }),
            _ /* FleetUnreachable */ => I(Topic.Websites, "Fleet can't load your device page",
                "Fleet, the device-management app on this PC, can't reach its server, so its My device page keeps loading.",
                new[] { Fix.Open(Destination.Fleet) },
                new[] { "Wait a few minutes, then click Open Fleet above.", "Still loading? Restart your PC." }),
        };
    }

    /// <summary>A VPN is on and Fleet's server doesn't answer: the VPN is the cause.</summary>
    public static Issue VpnBlocksFleetIssue(IEnumerable<Fix> vpnFixes)
    {
        var turnOff = vpnFixes.Where(f => f.Kind == FixKind.DisconnectVPN).ToList();
        return new Issue
        {
            Id = nameof(IssueCode.FleetUnreachable), Topic = Topic.Vpn,
            Title = "Your VPN is stopping Fleet from loading",
            Explanation = "Fleet, the device-management app on this PC, keeps loading because your VPN is on.",
            Fixes = turnOff.Append(Fix.Open(Destination.Fleet)).ToList(),
            Steps = new()
            {
                turnOff.Count == 0 ? "Open your VPN app and click Disconnect." : "Click Turn off VPN above.",
                "Click Open Fleet above. If the page was already open, reload it.",
                "Turn the VPN back on afterwards if you need it.",
            },
        };
    }
}
