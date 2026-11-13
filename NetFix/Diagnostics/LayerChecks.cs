using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Microsoft.Win32;
using Netfix.Model;
using static Netfix.Diagnostics.NetworkInfo;

namespace Netfix.Diagnostics;

/// <summary>One OSI layer's checks. Layers 1–4 must only touch this PC and the local network.</summary>
public interface ILayerCheck
{
    OSILayer Layer { get; }
    Task<List<Finding>> Run();
}

// MARK: - Layer 1: Physical (offline)

public sealed class PhysicalCheck : ILayerCheck
{
    public OSILayer Layer => OSILayer.Physical;

    public async Task<List<Finding>> Run()
    {
        var adapters = Adapters();
        var primary = Primary(adapters);
        var disabledTask = DisabledAdapters();
        var wlanTask = Wlan();
        var radioOffTask = WifiRadioOff();
        var findings = new List<Finding>();

        var active = adapters.Where(a => a.Up && a.Kind is AdapterKind.WiFi or AdapterKind.Ethernet).ToList();
        var wlan = await wlanTask;
        var wifi = wlan.Interfaces.FirstOrDefault(w => w.Connected) ?? wlan.Interfaces.FirstOrDefault();
        var hasWifiAdapter = adapters.Any(a => a.IsWiFi) || wlan.Interfaces.Count > 0;
        var radioOff = hasWifiAdapter && (wifi is null || !wifi.Connected) && await radioOffTask;
        var inUse = primary is { Kind: AdapterKind.Vpn } ? active.FirstOrDefault(a => a.Gateway is not null) ?? active.FirstOrDefault() : primary;

        if (inUse is not null)
        {
            var vpn = adapters.FirstOrDefault(a => a.Up && a.Kind == AdapterKind.Vpn);
            findings.Add(ConnectionTypeFinding(inUse, wifi?.Connected == true ? wifi.Ssid : null, vpn?.Description,
                                               active.Where(a => a != inUse).Select(a => a.Kind)));
        }

        if (hasWifiAdapter)
        {
            var wifiFindings = WifiFindings(wifi, radioOff, wlan.LocationBlocked);
            // The PC is on a cable: Wi‑Fi details are information only.
            findings.AddRange(inUse is { IsWiFi: false } ? wifiFindings.Select(NotInUse) : wifiFindings);
            if (inUse is { Kind: AdapterKind.Ethernet } && WifiBackupFinding(Native.Power()?.HasBattery == true, !radioOff, wifi?.Connected == true) is { } backup)
                findings.Add(backup);
        }

        foreach (var cable in active.Where(a => a.Kind == AdapterKind.Ethernet))
            findings.AddRange(EthernetFindings(cable, await MaxSpeedMbps(cable.Name)));

        // Adapters switched off in Windows (Settings → Network & internet → Advanced network settings).
        foreach (var name in await disabledTask)
        {
            findings.Add(active.Count == 0
                ? new Finding("Connection turned off", CheckStatus.Fail, $"{name} is turned off in Windows.",
                              advice: "Turn it back on in Settings → Network & internet → Advanced network settings.",
                              code: IssueCode.ServiceDisabled, fixes: new[] { Fix.EnableAdapter(name) })
                : new Finding("Connection turned off", CheckStatus.Info, $"{name} is turned off in Windows."));
        }

        if (active.Count == 0)
        {
            findings.Add(new Finding("Network link", CheckStatus.Fail, "No Wi‑Fi or Ethernet link is active.",
                advice: "Turn Wi‑Fi on and join a network, or check that the Ethernet cable is firmly plugged in at both ends (try another cable or port).",
                code: IssueCode.NoLink));
        }
        return findings;
    }

    /// <summary>Turns a Wi‑Fi finding into information only, for when the PC is using a cable instead.</summary>
    private static Finding NotInUse(Finding f) =>
        f.Code == IssueCode.WifiOff ? f : new Finding(f.Title, CheckStatus.Info, f.Detail + " (not in use — this PC is using a cable)");

    public static List<Finding> WifiFindings(WlanInfo? wifi, bool radioOff, bool locationBlocked = false)
    {
        if (radioOff)
            return new() { new Finding("Wi‑Fi radio", CheckStatus.Info, "Wi‑Fi is turned off.",
                advice: "Turn Wi‑Fi on from the taskbar's quick settings.", code: IssueCode.WifiOff) };
        if (locationBlocked)
            return new() { new Finding("Wi‑Fi radio", CheckStatus.Info,
                "Windows hides Wi‑Fi details from apps until Location is on (Settings → Privacy & security → Location → Let desktop apps access your location).") };
        if (wifi is null || !wifi.Connected)
            return new() { new Finding("Wi‑Fi radio", CheckStatus.Info, "Wi‑Fi is on but not connected to a network.") };

        var findings = new List<Finding>();
        if (wifi.Dbm is { } dbm)
        {
            var signal = $"{wifi.SignalPercent}% (about {dbm} dBm)" + (wifi.RadioType is { } r ? $" · {r}" : "");
            // −67 dBm and up is enough for video calls; fair still works, so only Network Details mentions it.
            if (dbm >= -67)
                findings.Add(new Finding("Wi‑Fi signal", CheckStatus.Pass, "Strong · " + signal));
            else if (dbm >= -72)
                findings.Add(new Finding("Wi‑Fi signal", CheckStatus.Warning, "Fair · " + signal,
                    advice: "Move closer to the router or remove obstructions (walls, metal, microwaves) to strengthen the signal.",
                    code: IssueCode.FairSignal));
            else
                findings.Add(new Finding("Wi‑Fi signal", CheckStatus.Fail, "Weak · " + signal,
                    advice: "The Wi‑Fi signal is weak. Move closer to the router, use Ethernet, or add a mesh node / access point.",
                    code: IssueCode.WeakSignal));
        }
        if (wifi.Channel is { } channel)
            findings.Add(new Finding("Wi‑Fi channel", CheckStatus.Info, $"Channel {channel}" + (wifi.Band is { } b ? $" ({b})" : "")));
        if (wifi.TransmitMbps is { } tx)
            findings.Add(new Finding("Wi‑Fi link rate", CheckStatus.Info, $"{tx:0} Mbps send · {wifi.ReceiveMbps ?? 0:0} Mbps receive"));
        return findings;
    }

    /// <summary>Says whether the PC is online by Wi‑Fi, a network cable, or an iPhone, and whether a VPN runs on top of it.</summary>
    public static Finding ConnectionTypeFinding(Adapter inUse, string? wifiNetwork, string? vpn, IEnumerable<AdapterKind> otherActive)
    {
        var others = otherActive.ToList();
        string detail = inUse.IsIPhoneUSB ? $"Using an iPhone or iPad connection · {inUse.Name}"
            : inUse.IsWiFi ? "Using Wi‑Fi" + (wifiNetwork is null ? "" : $" · network “{wifiNetwork}”")
            : $"Using a network cable (Ethernet) · {inUse.Name}";
        // The iPhone hotspot hands out 172.20.10.x.
        if (!inUse.IsIPhoneUSB && inUse.Gateway?.ToString().StartsWith("172.20.10.", StringComparison.Ordinal) == true)
            detail += " · through a phone hotspot";
        if (vpn is not null) detail += $" · through a VPN ({vpn})";
        if (inUse.Kind == AdapterKind.Ethernet && others.Contains(AdapterKind.WiFi)) detail += " · Wi‑Fi is also connected but the cable is used first";
        else if (inUse.IsWiFi && others.Contains(AdapterKind.Ethernet)) detail += " · a cable is also plugged in but Wi‑Fi is used first";
        return new Finding("Connection type", CheckStatus.Info, detail, fixes: inUse.Kind == AdapterKind.Ethernet
            ? new[] { Fix.ResetNetworkPort(inUse.Name) } : Array.Empty<Fix>());
    }

    /// <summary>
    /// Wi‑Fi as a backup for the cable: a laptop on a cable (often through a dock) only stays online after unplugging
    /// if Wi‑Fi is on and already joined to a network. Desktops aren't unplugged, so they're left alone.
    /// </summary>
    public static Finding? WifiBackupFinding(bool laptop, bool wifiOn, bool wifiJoined)
    {
        if (!laptop) return null;
        if (!wifiOn)
            return new Finding("Wi‑Fi backup", CheckStatus.Warning, "Wi‑Fi is off while this PC uses a cable; unplugging the cable or dock leaves it offline.",
                advice: "Turn Wi‑Fi on and leave it on: the cable is still used first while it's plugged in.",
                code: IssueCode.WifiOffOnCable, fixes: new[] { Fix.TurnWiFiOn });
        if (!wifiJoined)
            return new Finding("Wi‑Fi backup", CheckStatus.Warning, "Wi‑Fi is on but not connected to any network while this PC uses a cable; unplugging the cable or dock leaves it offline.",
                advice: "Connect to a Wi‑Fi network and tick Connect automatically.",
                code: IssueCode.WifiNotJoinedOnCable, fixes: new[] { Fix.Open(Destination.WifiSettings) });
        return null;
    }

    /// <summary>The fastest speed a cable adapter supports, from its Speed & Duplex choices (null when it doesn't say).</summary>
    private static async Task<long?> MaxSpeedMbps(string adapter)
    {
        var r = await Shell.Run("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command",
            $"(Get-NetAdapterAdvancedProperty -Name {Shell.PSQuote(adapter)} -RegistryKeyword '*SpeedDuplex' -ErrorAction SilentlyContinue).ValidDisplayValues" }, 10);
        return MaxSpeedFromChoices(r.Text);
    }

    /// <summary>"1.0 Gbps Full Duplex", "100 Mbps Half Duplex", "2.5 Gbps Full Duplex"… → the top speed in Mbps.</summary>
    public static long? MaxSpeedFromChoices(string text)
    {
        long? max = null;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"([\d.]+)\s*(G|M)b(ps)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) continue;
            var mbps = (long)(m.Groups[2].Value.ToUpperInvariant() == "G" ? v * 1000 : v);
            if (max is null || mbps > max) max = mbps;
        }
        return max;
    }

    /// <summary>
    /// Cable check: a healthy cable connects at the adapter's top speed and passes data without errors.
    /// Reads only this PC's counters, so it works offline.
    /// </summary>
    public static List<Finding> EthernetFindings(Adapter cable, long? maxMbps)
    {
        var findings = new List<Finding>();
        var reset = new[] { Fix.ResetNetworkPort(cable.Name) };
        const string cableSteps = "Unplug the cable at both ends and plug it back in until it clicks, try a different cable (Cat 5e or newer), or try another port on the router.";
        var port = cable.Name;
        var speed = cable.SpeedMbps;
        if (speed > 0)
        {
            if (maxMbps is { } max && speed < max && speed <= 100)
                findings.Add(new Finding("Cable speed", CheckStatus.Warning, $"{port} connected at {SpeedLabel(speed)}, but the adapter supports {SpeedLabel(max)}.",
                    advice: "A damaged or old cable, a loose plug, or an older router port is limiting the speed. " + cableSteps,
                    code: IssueCode.CableSlow, fixes: reset));
            else if (maxMbps is null && speed <= 10)
                findings.Add(new Finding("Cable speed", CheckStatus.Warning, $"{port} connected at only {SpeedLabel(speed)}.",
                    advice: "10 Mbps usually means a damaged cable or a bad port. " + cableSteps,
                    code: IssueCode.CableSlow, fixes: reset));
            else
                findings.Add(new Finding("Cable speed", CheckStatus.Pass, $"{port} connected at {SpeedLabel(speed)}" + (speed == maxMbps ? " (the adapter's top speed)" : "")));
        }
        else findings.Add(new Finding("Cable speed", CheckStatus.Info, $"{port} didn't report a link speed."));

        var packets = cable.PacketsIn + cable.PacketsOut;
        var errors = cable.ErrorsIn + cable.ErrorsOut;
        var percent = packets == 0 ? 0 : errors * 100.0 / packets;
        var detail = $"{errors} error(s) in {packets} packets ({percent:0.00}%)";
        // A few errors from plugging the cable in are normal; a steady share of bad packets isn't.
        findings.Add(errors >= 20 && percent >= 0.1
            ? new Finding("Cable errors", CheckStatus.Warning, $"{port}: {detail}",
                advice: "Data is arriving damaged and has to be resent. This is almost always a bad cable, a loose plug, or a worn port. " + cableSteps,
                code: IssueCode.CableErrors, fixes: reset)
            : new Finding("Cable errors", CheckStatus.Pass, $"{port}: {detail}"));
        return findings;
    }

    /// <summary>100 → "100 Mbps", 1000 → "1 Gbps", 2500 → "2.5 Gbps".</summary>
    public static string SpeedLabel(long mbps) =>
        mbps >= 1000 && mbps % 1000 == 0 ? $"{mbps / 1000} Gbps" : mbps >= 1000 ? $"{mbps / 1000.0:0.0} Gbps" : $"{mbps} Mbps";
}

// MARK: - Layer 2: Data Link (offline)

public sealed class DataLinkCheck : ILayerCheck
{
    public OSILayer Layer => OSILayer.DataLink;

    public async Task<List<Finding>> Run()
    {
        var adapters = Adapters();
        var primary = Primary(adapters);
        if (primary is null)
            return new() { new Finding("Primary interface", CheckStatus.Fail, "No active interface carries traffic.",
                advice: "Fix Layer 1 first: connect to Wi‑Fi or plug in Ethernet.", code: IssueCode.NoLink) };

        var findings = new List<Finding>();
        if (primary.Kind == AdapterKind.Vpn)
        {
            findings.Add(new Finding("Primary interface", CheckStatus.Info, $"{primary.Name} is a VPN tunnel; it has no Layer 2 neighbors to check."));
            return findings;
        }
        findings.Add(primary.Mac.Length > 0
            ? new Finding("MAC address", CheckStatus.Pass, $"{primary.Name} · {primary.Mac}")
            : new Finding("MAC address", CheckStatus.Warning, $"{primary.Name} reports no hardware address."));

        if (primary.Gateway is not { } gateway)
        {
            findings.Add(new Finding("Router ARP", CheckStatus.Skipped, "No IPv4 default gateway to look up."));
            return findings;
        }
        var mac = await Task.Run(() => Native.ArpLookup(gateway));
        findings.Add(mac is not null
            ? new Finding("Router ARP", CheckStatus.Pass, $"{gateway} is at {mac}")
            : new Finding("Router ARP", CheckStatus.Fail, $"The router ({gateway}) did not answer ARP.",
                advice: "Your PC can't reach the router on the local network. Disconnect and reconnect the Wi‑Fi, check the cable, or restart the router.",
                code: IssueCode.RouterUnreachable));

        var arp = await Shell.Run("arp.exe", new[] { "-a", "-N", primary.Address?.ToString() ?? "" }, 6);
        var neighbors = arp.Text.Split('\n').Count(l => l.Contains("dynamic", StringComparison.OrdinalIgnoreCase));
        findings.Add(new Finding("Local neighbors", CheckStatus.Info, $"{neighbors} device(s) in the ARP cache on {primary.Name}"));
        return findings;
    }
}

// MARK: - Layer 3: Network (offline)

public sealed class NetworkCheck : ILayerCheck
{
    public OSILayer Layer => OSILayer.Network;

    public async Task<List<Finding>> Run()
    {
        var conflicts = SystemChecks.RecentAddressConflicts();
        var rasVpns = ConnectedRasVpns();
        var routesTask = Shell.Run("route.exe", new[] { "print", "-4" }, 8);
        var adapters = Adapters();
        var primary = Primary(adapters);
        var findings = new List<Finding>();

        var lan = primary is { Kind: AdapterKind.Vpn } ? adapters.FirstOrDefault(a => a.Up && a.Kind != AdapterKind.Vpn && a.Address is not null) : primary;
        if (lan is null)
        {
            findings.Add(new Finding("IP address", CheckStatus.Fail, "No interface has an IP address.",
                advice: "Connect to a network first (Layer 1).", code: IssueCode.NoLink));
            return findings;
        }

        if (lan.Address is { } ip)
        {
            findings.AddRange(IpAddressFindings(ip, lan.PrefixLength, lan.Gateway, lan.Name));
            findings.Add(new Finding("Address source", CheckStatus.Info, lan.Dhcp ? "Assigned by the router (DHCP)" : "Set manually",
                                     code: lan.Dhcp ? null : IssueCode.ManualAddress));
            findings.Add(AddressConflictFinding(await conflicts, lan.Name));
        }
        else
        {
            findings.Add(new Finding("IP address", CheckStatus.Fail, $"{lan.Name} has no IPv4 address.",
                advice: "Renew the address from the router (ipconfig /renew) or check whether a manual IP is configured incorrectly.",
                code: IssueCode.NoIP));
        }

        findings.Add(DnsSettingsFinding(lan.Dns.Select(d => d.ToString()).ToList()));

        var ras = await rasVpns;
        var vpnAdapter = adapters.FirstOrDefault(a => a.Up && a.Kind == AdapterKind.Vpn);
        findings.Add(VpnFinding(ras, vpnAdapter?.Description, primary?.Kind == AdapterKind.Vpn));

        // Routes a crashed or closed VPN left behind: they take all internet traffic away from the router.
        if (vpnAdapter is null && ras.Count == 0)
        {
            var live = adapters.Where(a => a.Up && a.Address is not null).Select(a => a.Address!).Append(IPAddress.Loopback);
            var leftovers = LeftoverVpnRoutes(ParseRoutes((await routesTask).Text), live);
            if (leftovers.Count > 0)
            {
                var list = string.Join(", ", leftovers.Select(r => $"{r.Destination}/1 via {r.Gateway} ({r.Interface})"));
                findings.Add(new Finding("Old VPN routes", CheckStatus.Fail, $"Routes left by a VPN that isn't on: {list}",
                    advice: "These send internet traffic away from the router. Delete them, or restart the PC.",
                    code: IssueCode.VpnLeftoverRoutes, fixes: new[] { Fix.RemoveLeftoverRoutes(leftovers.Select(r => r.Destination)) }));
            }
        }

        if (lan.Gateway is not { } gateway)
        {
            findings.Add(new Finding("Default gateway", CheckStatus.Fail, "There is no default route.",
                advice: "Without a gateway nothing leaves your local network. Renew the address from the router, or set the router address manually.",
                code: IssueCode.NoGateway));
            return findings;
        }
        findings.Add(new Finding("Default gateway", CheckStatus.Pass, $"{gateway} via {lan.Name}"));

        var stats = await PingProbe.Ping(gateway);
        var detail = $"{stats.Received}/{stats.Sent} replies" + (stats.Received > 0 ? $", avg {stats.AverageMs:0.0} ms" : "");
        if (stats.Received == 0)
            findings.Add(new Finding("Ping router", CheckStatus.Fail, $"No replies from {gateway}.",
                advice: "The router isn't answering. Some routers block ping — check Layer 4 — otherwise restart the router.",
                code: IssueCode.RouterNoPing));
        else if (stats.LossPercent > 0)
            findings.Add(new Finding("Ping router", CheckStatus.Warning, $"{(int)stats.LossPercent}% packet loss · {detail}",
                advice: "Packet loss on the local network usually means poor Wi‑Fi signal or interference, or a bad cable.",
                code: IssueCode.PacketLoss));
        else if (stats.AverageMs > 30)
            findings.Add(new Finding("Ping router", CheckStatus.Warning, $"High local latency · {detail}",
                advice: "Router latency should be under ~10 ms. Check Wi‑Fi signal and whether something is saturating the network.",
                code: IssueCode.SlowLocal));
        else
            findings.Add(new Finding("Ping router", CheckStatus.Pass, detail));
        return findings;
    }

    /// <summary>Whether a VPN is carrying traffic. A VPN that's on is only a problem when something else fails too (see NetworkAdvisor).</summary>
    public static Finding VpnFinding(IReadOnlyList<string> rasConnected, string? vpnAdapter, bool routedThroughTunnel)
    {
        if (rasConnected.Count > 0)
            return new Finding("VPN", CheckStatus.Info, $"{rasConnected[0]} is connected" + (routedThroughTunnel ? " · all traffic goes through it" : ""),
                               code: IssueCode.VpnOn, fixes: rasConnected.Select(Fix.DisconnectVPN));
        if (vpnAdapter is not null)
            return new Finding("VPN", CheckStatus.Info, $"A VPN app's connection is up ({vpnAdapter})" + (routedThroughTunnel ? " · all traffic goes through it" : ""),
                               code: IssueCode.VpnOn);
        return new Finding("VPN", CheckStatus.Info, "No VPN is on.");
    }

    /// <summary>Checks the address, prefix length and router address form a working setup.</summary>
    public static List<Finding> IpAddressFindings(IPAddress ip, int prefix, IPAddress? gateway, string adapter)
    {
        if (IsSelfAssigned(ip))
            return new() { new Finding("IP address", CheckStatus.Fail, $"{ip} is self-assigned — DHCP did not answer.",
                advice: "Renew the address (ipconfig /release, then ipconfig /renew) or restart the router.", code: IssueCode.SelfAssignedIP) };

        var problems = new List<string>();
        var routerProblem = false;
        if (AddressProblem(ip, prefix) is { } p) problems.Add(p);
        if (gateway is not null)
        {
            if (gateway.Equals(ip)) { problems.Add($"The router has the same address as this PC ({gateway})."); routerProblem = true; }
            else if (prefix is > 0 and < 32 && !SameSubnet(ip, gateway, prefix)) { problems.Add($"The router ({gateway}) is outside this network."); routerProblem = true; }
        }
        if (problems.Count == 0)
            return new() { new Finding("IP address", CheckStatus.Pass, $"{ip}/{prefix} on {adapter} is a valid address"
                + (gateway is null ? "" : $" in the same network as the router ({gateway})")) };
        return new() { new Finding("IP address", CheckStatus.Fail, $"{ip}/{prefix}: " + string.Join(" ", problems),
            advice: "Set the adapter to get an address automatically (Settings → Network & internet → the network → IP assignment → Automatic (DHCP)).",
            code: routerProblem ? IssueCode.GatewayMismatch : IssueCode.InvalidIP) };
    }

    /// <summary>At least one DNS server is configured and every entry is a usable address. Reads local settings only.</summary>
    public static Finding DnsSettingsFinding(IReadOnlyList<string> servers)
    {
        var valid = servers.Where(s => IPAddress.TryParse(s, out var a) && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.Broadcast)).ToList();
        var invalid = servers.Except(valid).ToList();
        if (servers.Count == 0)
            return new Finding("DNS settings", CheckStatus.Fail, "No DNS servers are configured.",
                advice: "Set DNS to automatic in Settings → Network & internet → the network → DNS server assignment.", code: IssueCode.NoDNS);
        if (invalid.Count > 0)
            return new Finding("DNS settings", valid.Count == 0 ? CheckStatus.Fail : CheckStatus.Warning,
                $"Invalid DNS server entries: {string.Join(", ", invalid)}" + (valid.Count == 0 ? "" : $" · usable: {string.Join(", ", valid)}"),
                advice: "Remove the invalid entries in Settings → Network & internet → the network → DNS server assignment.", code: IssueCode.BadDNSServer);
        return new Finding("DNS settings", CheckStatus.Pass, $"DNS servers: {string.Join(", ", valid)}");
    }

    public static Finding AddressConflictFinding(int conflicts, string adapter) => conflicts > 0
        ? new Finding("Address conflict", CheckStatus.Fail, $"Windows reported {conflicts} address conflict(s) in the last 30 minutes (Tcpip event 4199).",
            advice: "Another device is using this PC's address. Renew the address (ipconfig /release, then /renew).",
            code: IssueCode.IpConflict, fixes: new[] { Fix.RenewAddress(adapter) })
        : new Finding("Address conflict", CheckStatus.Pass, "No other device is using this PC's address.");
}

// MARK: - Layer 4: Transport (offline)

public sealed class TransportCheck : ILayerCheck
{
    public OSILayer Layer => OSILayer.Transport;

    public async Task<List<Finding>> Run()
    {
        var adapters = Adapters();
        var primary = Primary(adapters);
        var gateway = Router(adapters, primary);
        var findings = new List<Finding>();

        // Windows always listens on port 135 (RPC) on itself.
        var loop = await TcpProbe.Probe("127.0.0.1", 135, 3);
        findings.Add(loop.Answered
            ? new Finding("Local TCP stack", CheckStatus.Pass, "This PC can open TCP connections to itself (127.0.0.1).")
            : new Finding("Local TCP stack", CheckStatus.Fail, $"Loopback connection failed: {loop.Description}",
                advice: "This PC's own networking is misbehaving. Restart the PC; if it persists, remove any network filter or VPN software.",
                code: IssueCode.TcpStackBroken));

        if (gateway is not null)
        {
            var host = gateway.ToString();
            var results = await Task.WhenAll(new[] { 53, 80, 443 }.Select(async port => (port, outcome: await TcpProbe.Probe(host, port, 3))));
            var detail = string.Join(" · ", results.Select(r => $"{r.port} {r.outcome.Description}"));
            findings.Add(results.Any(r => r.outcome.Answered)
                ? new Finding("TCP to router", CheckStatus.Pass, $"Router completes TCP handshakes · {detail}")
                // Many routers (especially managed networks) silently drop these ports, so this alone isn't a fault.
                : new Finding("TCP to router", CheckStatus.Info, "Router didn't answer on 53/80/443 — it's likely filtering them. If Layer 3 ping also failed, restart the router."));
        }
        else findings.Add(new Finding("TCP to router", CheckStatus.Skipped, "No local IPv4 gateway to test."));

        findings.Add(await LocalDnsFinding(primary?.Dns ?? Array.Empty<IPAddress>(), gateway));
        findings.Add(SystemChecks.FirewallFinding(await SystemChecks.FirewallBlocksAll()));

        var established = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Count(c => c.State == TcpState.Established);
        findings.Add(new Finding("Open connections", CheckStatus.Info, $"{established} established TCP connection(s) on this PC"));
        return findings;
    }

    public static bool IsLocal(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
               || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    /// <summary>
    /// Asks each DNS server on the local network (usually the router) the name of its own address and confirms it
    /// answers. Internet DNS servers are never contacted here — that's Layer 7.
    /// </summary>
    private static async Task<Finding> LocalDnsFinding(IReadOnlyList<IPAddress> servers, IPAddress? gateway)
    {
        var local = servers.Where(IsLocal).Take(2).ToList();
        if (local.Count == 0)
            return new Finding("Local DNS server", CheckStatus.Skipped, servers.Count == 0
                ? "No DNS servers to test."
                : $"Your DNS servers are on the internet ({string.Join(", ", servers)}); Layer 7 tests them.");
        var results = new List<string>();
        var any = false;
        foreach (var server in local)
        {
            var answered = await DnsServerProbe.Ask(server, DnsServerProbe.ReverseName(server)) == DnsServerProbe.Outcome.Answered;
            any |= answered;
            results.Add(answered ? $"{server} answered" : $"{server} didn't answer");
        }
        var detail = string.Join(" · ", results);
        return any ? new Finding("Local DNS server", CheckStatus.Pass, detail)
                   : LocalDnsDownFinding(detail, local.Select(s => s.ToString()).ToList(), gateway?.ToString());
    }

    /// <summary>
    /// No local DNS server answered. When the router is one of them, restarting it may help; when they're the network's
    /// own servers (offices), only the network's owner can fix them.
    /// </summary>
    public static Finding LocalDnsDownFinding(string detail, IReadOnlyList<string> servers, string? gateway) =>
        gateway is null || servers.Contains(gateway)
            ? new Finding("Local DNS server", CheckStatus.Fail, detail,
                advice: "Your router isn't answering DNS. Restart the router, or set a public DNS server such as 1.1.1.1.", code: IssueCode.LocalDNSDown)
            : new Finding("Local DNS server", CheckStatus.Fail, detail,
                advice: "This network's own DNS servers (not the router) aren't answering. Only the network's owner can fix them.", code: IssueCode.NetworkDNSDown);
}

// MARK: - Layer 5: Session (internet)

public sealed class SessionCheck : ILayerCheck
{
    public OSILayer Layer => OSILayer.Session;

    /// <summary>Connect by IP so this layer doesn't depend on DNS (that's Layer 7).</summary>
    public static readonly (string Name, string Ip)[] Targets = { ("Cloudflare", "1.1.1.1"), ("Google", "8.8.8.8"), ("Quad9", "9.9.9.9") };
    /// <summary>The same servers on ports other than 443 (web on 80, DNS over TCP on 53).</summary>
    public static readonly (string Ip, int Port)[] OtherPorts = { ("1.1.1.1", 80), ("8.8.8.8", 53), ("9.9.9.9", 53) };

    /// <summary>No server accepted a secure (443) connection. If they answer on other ports, the network blocks secure connections.</summary>
    public static Finding UnreachableFinding(bool otherPortsAnswer) => otherPortsAnswer
        ? new Finding("Internet sessions", CheckStatus.Fail, "Internet servers answer on other ports, but no secure (443) connection gets through.",
            advice: "A firewall on this network blocks secure connections. Only the network's owner can change that.", code: IssueCode.HttpsBlocked)
        : new Finding("Internet sessions", CheckStatus.Fail, "No internet server accepted a connection.",
            advice: "Your local network works but traffic isn't getting past the router. Restart the modem and router, check for an outage, or disconnect a VPN/proxy.",
            code: IssueCode.InternetUnreachable);

    public async Task<List<Finding>> Run()
    {
        var outcomes = await Task.WhenAll(Targets.Select(t => TcpProbe.Probe(t.Ip, 443, 5)));
        var findings = new List<Finding>();
        for (int i = 0; i < Targets.Length; i++)
        {
            var title = $"{Targets[i].Name} ({Targets[i].Ip}:443)";
            var o = outcomes[i];
            if (o.Open && o.Ms > 200)
                findings.Add(new Finding(title, CheckStatus.Warning, $"Session opened slowly ({o.Ms:0} ms)",
                    advice: "High latency to the internet. Check for heavy downloads on your network.", code: IssueCode.SlowInternetLatency));
            else if (o.Open)
                findings.Add(new Finding(title, CheckStatus.Pass, $"Session opened in {o.Ms:0} ms"));
            else
                findings.Add(new Finding(title, CheckStatus.Warning, $"Could not open a session: {o.Description}"));
        }
        if (!outcomes.Any(o => o.Open))
        {
            var other = await Task.WhenAll(OtherPorts.Select(p => TcpProbe.Probe(p.Ip, p.Port, 4)));
            findings.Add(UnreachableFinding(other.Any(o => o.Answered)));
        }
        return findings;
    }
}

// MARK: - Layer 6: Presentation (internet)

public sealed class PresentationCheck : ILayerCheck
{
    public OSILayer Layer => OSILayer.Presentation;

    public static readonly string[] Targets = { "https://www.apple.com", "https://www.cloudflare.com" };

    public async Task<List<Finding>> Run()
    {
        var findings = new List<Finding>();
        foreach (var url in Targets)
        {
            var host = new Uri(url).Host;
            var r = await HttpProbe.Fetch(url, "HEAD");
            if (r.Error is null)
            {
                findings.Add(new Finding(host, CheckStatus.Pass, $"{TlsName(r.Tls)} handshake OK, certificate trusted"));
                continue;
            }
            if (r.Cert == HttpProbe.CertProblem.None)
            {
                // A failed lookup or a blocked name looks like a failed secure connection; check that first.
                var lookup = await DnsProbe.Resolve(host);
                if (lookup.Error is not null)
                {
                    findings.Add(new Finding(host, CheckStatus.Warning, "Couldn't test the secure connection: the website lookup failed.",
                        advice: "See Layer 7: fix website lookups first."));
                    continue;
                }
                if (lookup.Addresses.Any(IsBlockingAddress))
                {
                    findings.Add(new Finding(host, CheckStatus.Warning, $"Couldn't test the secure connection: {host} is blocked on this PC ({string.Join(", ", lookup.Addresses)}).",
                        advice: "See Layer 7: the hosts file blocks it."));
                    continue;
                }
            }
            findings.Add(await CertificateFinding(r, host));
        }
        return findings;
    }

    private static string TlsName(System.Security.Authentication.SslProtocols? p) => p switch
    {
        System.Security.Authentication.SslProtocols.Tls13 => "TLS 1.3",
        System.Security.Authentication.SslProtocols.Tls12 => "TLS 1.2",
        _ => "TLS",
    };

    private static async Task<Finding> CertificateFinding(HttpProbe.Result r, string host)
    {
        switch (r.Cert)
        {
            case HttpProbe.CertProblem.Date:
                return new Finding(host, CheckStatus.Fail, "Certificate date is invalid.",
                    advice: "Your PC's clock is probably wrong. Settings → Time & language → Date & time → Set time automatically → Sync now.", code: IssueCode.ClockWrong);
            case HttpProbe.CertProblem.Untrusted:
                // An untrusted certificate with a wrong clock is still the clock.
                if (await SystemChecks.ClockOffMinutes() is { } off && Math.Abs(off) > 60 * 24)
                    return new Finding(host, CheckStatus.Fail, $"Certificate is not trusted, and this PC's clock is off by {Math.Abs(off) / 60:0} hours.",
                        advice: "Settings → Time & language → Date & time → Set time automatically → Sync now.", code: IssueCode.ClockWrong);
                return new Finding(host, CheckStatus.Fail, $"Certificate is not trusted (issued by {r.Issuer ?? "unknown"}).",
                    advice: "Something is intercepting encrypted traffic: a captive portal, a corporate proxy, or security software. Sign in to the Wi‑Fi portal or disable HTTPS inspection.",
                    code: IssueCode.TrafficIntercepted);
            default:
                return new Finding(host, CheckStatus.Fail, r.Error ?? "TLS handshake failed.",
                    advice: "Encrypted connections aren't completing. Check Layer 5 results and any VPN or proxy.", code: IssueCode.TlsBlocked);
        }
    }
}

// MARK: - Layer 7: Application (internet)

public sealed class ApplicationCheck : ILayerCheck
{
    public OSILayer Layer => OSILayer.Application;

    /// <summary>Whether a public DNS server answers a question sent to it directly (bypassing the PC's settings).</summary>
    public static async Task<bool> PublicDnsAnswers()
    {
        foreach (var server in new[] { "1.1.1.1", "8.8.8.8" })
            if (await DnsServerProbe.Ask(IPAddress.Parse(server)) == DnsServerProbe.Outcome.Answered) return true;
        return false;
    }

    /// <summary>
    /// Why lookups fail when they all do. A public server answers: the PC's own servers are the problem, and if they
    /// were typed in by hand, switching to automatic fixes it (null otherwise: the router's DNS, covered by DnsFailed).
    /// Nothing answers but the internet is up: the network blocks lookups.
    /// </summary>
    public static Finding? LookupCause(bool publicAnswers, IReadOnlyList<string> manualServers, bool internetUp, string adapter)
    {
        if (publicAnswers)
        {
            if (manualServers.Count == 0) return null;
            return new Finding("DNS servers", CheckStatus.Fail, $"The DNS servers typed in by hand ({string.Join(", ", manualServers)}) don't answer; a public one does.",
                advice: "Switch DNS back to automatic in Settings → Network & internet → the network → DNS server assignment.",
                code: IssueCode.DnsServersDown, fixes: new[] { Fix.UseAutomaticDNS(adapter) });
        }
        if (!internetUp) return null;
        return new Finding("DNS blocked", CheckStatus.Fail, "No DNS server answers, not even a public one, though the internet is reachable.",
            advice: "This network blocks DNS. Sign in to its portal, or ask its owner.", code: IssueCode.DnsBlocked);
    }

    /// <summary>The hosts file blocking well-known sites.</summary>
    public static Finding HostsFinding(IReadOnlyList<string> blocked) => blocked.Count == 0
        ? new Finding("Hosts file", CheckStatus.Pass, "Doesn't block any well-known sites")
        : new Finding("Hosts file", CheckStatus.Fail, $"The hosts file blocks {string.Join(", ", blocked)}",
            advice: "Comment out those lines in C:\\Windows\\System32\\drivers\\etc\\hosts, then run ipconfig /flushdns.",
            code: IssueCode.HostsFileBlocks, fixes: new[] { Fix.UnblockSites(blocked) });

    public async Task<List<Finding>> Run()
    {
        var findings = new List<Finding>();
        foreach (var host in new[] { "www.apple.com", "example.com" })
        {
            var r = await DnsProbe.Resolve(host);
            if (r.Error is not null)
                findings.Add(new Finding($"DNS · {host}", CheckStatus.Fail, $"Lookup failed: {r.Error}",
                    advice: "Names aren't resolving. Refresh website lookups (ipconfig /flushdns), or restart the router.", code: IssueCode.DnsFailed));
            else if (r.Ms > 500)
                findings.Add(new Finding($"DNS · {host}", CheckStatus.Warning, $"Resolved slowly ({r.Ms:0} ms)",
                    advice: "Slow DNS makes every site feel slow.", code: IssueCode.DnsSlow));
            else
                findings.Add(new Finding($"DNS · {host}", CheckStatus.Pass, $"{r.Addresses.FirstOrDefault() ?? "resolved"} ({r.Ms:0} ms)"));
        }

        // Both lookups failed: find out whether it's the PC's DNS servers or the network.
        if (findings.Count(f => f.Code == IssueCode.DnsFailed) == 2)
        {
            var adapters = Adapters();
            var primary = Primary(adapters);
            var manual = primary is null ? new List<string>() : await SystemChecks.ManualDnsServers(primary.Name);
            var publicAnswers = PublicDnsAnswers();
            var internetUp = TcpProbe.Probe("1.1.1.1", 443, 4);
            if (LookupCause(await publicAnswers, manual, (await internetUp).Open, primary?.Name ?? "Wi-Fi") is { } cause) findings.Add(cause);
        }

        // The hosts file can block websites before any lookup happens.
        findings.Add(HostsFinding(BlockedSitesOnThisPC(Fleet.Server()?.Host)));

        var captive = await HttpProbe.Fetch("http://www.msftconnecttest.com/connecttest.txt", useProxy: false);
        if (captive.Error is not null)
            findings.Add(new Finding("Captive portal", CheckStatus.Warning, "Couldn't reach Windows' connection test page."));
        else if (captive.Body.Contains("Microsoft Connect Test", StringComparison.Ordinal))
            findings.Add(new Finding("Captive portal", CheckStatus.Pass, "No login page is intercepting traffic."));
        else
            findings.Add(new Finding("Captive portal", CheckStatus.Fail, "A login page is intercepting web traffic.",
                advice: "This network needs you to sign in or accept terms. Open http://www.msftconnecttest.com/redirect to bring up the login page.",
                code: IssueCode.CaptivePortal));

        findings.AddRange(await SystemChecks.ProxyFindings());

        var web = await HttpProbe.Fetch("https://www.google.com/generate_204");
        findings.Add(web.StatusCode is { } code && code < 400
            ? new Finding("Web request", CheckStatus.Pass, $"HTTPS GET returned {code} in {web.Ms:0} ms")
            : new Finding("Web request", CheckStatus.Fail, web.Error ?? $"Unexpected HTTP status {web.StatusCode ?? 0}",
                advice: "Web requests aren't completing. Check the DNS and TLS results above and any proxy settings.", code: IssueCode.WebFailed));

        if (await Fleet.Check() is { } fleet) findings.Add(fleet);
        return findings;
    }
}

/// <summary>Windows settings that break the network: the Windows-troubleshooter equivalents.</summary>
public static class SystemChecks
{
    /// <summary>Address-conflict events (Tcpip 4199) in the System log in the last 30 minutes.</summary>
    public static async Task<int> RecentAddressConflicts()
    {
        var r = await Shell.Run("wevtutil.exe", new[] { "qe", "System", "/q:*[System[(EventID=4199) and TimeCreated[timediff(@SystemTime) <= 1800000]]]", "/c:20", "/f:text" }, 8);
        return r.Ok ? r.Text.Split('\n').Count(l => l.TrimStart().StartsWith("Event ID:", StringComparison.OrdinalIgnoreCase)) : 0;
    }

    public static async Task<List<string>> FirewallBlocksAll()
    {
        var r = await Shell.Run("netsh.exe", new[] { "advfirewall", "show", "allprofiles", "firewallpolicy" }, 8);
        return ParseFirewallBlockAll(r.Text);
    }

    public static Finding FirewallFinding(IReadOnlyList<string> blockAllProfiles) => blockAllProfiles.Count > 0
        ? new Finding("Firewall", CheckStatus.Warning, $"Windows Defender Firewall blocks all incoming connections ({string.Join(", ", blockAllProfiles)} profile).",
            advice: "Screen sharing and file sharing to this PC won't work. Windows Security → Firewall & network protection.",
            code: IssueCode.FirewallBlocksIncoming)
        : new Finding("Firewall", CheckStatus.Info, "Windows Defender Firewall allows the incoming connections its rules permit.");

    /// <summary>Minutes this PC's clock is ahead (+) or behind (−) a web server's, or null when it can't tell.</summary>
    public static async Task<double?> ClockOffMinutes() =>
        await HttpProbe.ServerTime() is { } server ? (DateTimeOffset.UtcNow - server).TotalMinutes : null;

    /// <summary>DNS servers typed in by hand on an adapter (the registry's NameServer; empty = automatic).</summary>
    public static async Task<List<string>> ManualDnsServers(string adapter)
    {
        var r = await Shell.Run("netsh.exe", new[] { "interface", "ipv4", "show", "dnsservers", $"name={adapter}" }, 6);
        return ParseManualDns(r.Text);
    }

    /// <summary>From <c>netsh interface ipv4 show dnsservers</c>: the servers when they're "Statically Configured".</summary>
    public static List<string> ParseManualDns(string text)
    {
        if (!text.Contains("Statically Configured DNS Servers", StringComparison.OrdinalIgnoreCase)) return new();
        return System.Text.RegularExpressions.Regex.Matches(text, @"\b\d{1,3}(\.\d{1,3}){3}\b").Select(m => m.Value).Distinct().ToList();
    }

    /// <summary>The user's proxy (Settings → Network & internet → Proxy), and whether it answers.</summary>
    public static async Task<List<Finding>> ProxyFindings()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        var enabled = (key?.GetValue("ProxyEnable") as int?) == 1;
        var server = key?.GetValue("ProxyServer") as string;
        var pac = key?.GetValue("AutoConfigURL") as string;
        var findings = new List<Finding>();
        if (!string.IsNullOrWhiteSpace(pac))
            findings.Add(new Finding("Proxy file", CheckStatus.Info, $"Proxy settings come from {pac}"));
        if (!enabled || string.IsNullOrWhiteSpace(server))
        {
            if (string.IsNullOrWhiteSpace(pac)) findings.Add(new Finding("Proxy", CheckStatus.Pass, "No proxy is set."));
            return findings;
        }
        if (ParseProxy(server) is not { } hostPort)
        {
            findings.Add(new Finding("Proxy", CheckStatus.Info, $"Proxy: {server}"));
            return findings;
        }
        var probe = await TcpProbe.Probe(hostPort.Host, hostPort.Port, 4);
        findings.Add(probe.Open
            ? new Finding("Proxy", CheckStatus.Pass, $"Proxy {hostPort.Host}:{hostPort.Port} answers")
            : new Finding("Proxy", CheckStatus.Fail, $"Proxy {hostPort.Host}:{hostPort.Port} doesn't answer ({probe.Description}).",
                advice: "Websites go through this proxy, so they won't load. Turn it off in Proxy settings unless you set it up on purpose.",
                code: IssueCode.ProxyBroken, fixes: new[] { Fix.Open(Destination.ProxySettings) }));
        return findings;
    }

    /// <summary>"proxy:8080" or "http=proxy:8080;https=proxy:8443" → the HTTPS (else first) host and port.</summary>
    public static (string Host, int Port)? ParseProxy(string setting)
    {
        var parts = setting.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
        var pick = parts.FirstOrDefault(p => p.StartsWith("https=", StringComparison.OrdinalIgnoreCase)) ?? parts.FirstOrDefault();
        if (pick is null) return null;
        var value = pick.Contains('=') ? pick[(pick.IndexOf('=') + 1)..] : pick;
        value = value.Replace("http://", "", StringComparison.OrdinalIgnoreCase).Replace("https://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        var colon = value.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(value[(colon + 1)..], out var port)) return (value, 80);
        return (value[..colon], port);
    }
}
