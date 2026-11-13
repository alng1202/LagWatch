using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Netfix.Diagnostics;

/// <summary>What this PC knows about its network connections. Wrappers read the PC; the parsers are pure, for tests.</summary>
public static class NetworkInfo
{
    public enum AdapterKind { WiFi, Ethernet, Vpn, Other }

    /// <summary>One network adapter, as Windows reports it.</summary>
    public sealed record Adapter(
        string Name, string Description, AdapterKind Kind, bool Up, long SpeedMbps, string Mac,
        int Index, IPAddress? Address, int PrefixLength, IPAddress? Gateway, IReadOnlyList<IPAddress> Dns,
        bool Dhcp, long PacketsIn, long PacketsOut, long ErrorsIn, long ErrorsOut)
    {
        public bool IsWiFi => Kind == AdapterKind.WiFi;
        /// <summary>An iPhone shared over USB.</summary>
        public bool IsIPhoneUSB => Description.Contains("Apple Mobile Device Ethernet", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Descriptions of adapters VPN apps add (any vendor).</summary>
    private static readonly Regex VpnDescription = new(
        @"VPN|TAP-|Wintun|WireGuard|Tunnel|AnyConnect|GlobalProtect|PANGP|Fortinet|Zscaler|NordLynx|Juniper|Pulse|Cloudflare WARP|Tailscale|OpenVPN",
        RegexOptions.IgnoreCase);

    public static AdapterKind KindOf(NetworkInterfaceType type, string description)
    {
        if (type == NetworkInterfaceType.Ppp || VpnDescription.IsMatch(description)) return AdapterKind.Vpn;
        if (type == NetworkInterfaceType.Wireless80211) return AdapterKind.WiFi;
        if (type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit) return AdapterKind.Ethernet;
        return AdapterKind.Other;
    }

    /// <summary>Every adapter except loopback, Bluetooth networking and Windows' hidden virtual ones.</summary>
    public static List<Adapter> Adapters()
    {
        var list = new List<Adapter>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (ni.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) continue;
            if (ni.Description.StartsWith("Microsoft Wi-Fi Direct Virtual", StringComparison.OrdinalIgnoreCase)) continue;
            if (ni.Description.StartsWith("Hyper-V", StringComparison.OrdinalIgnoreCase)) continue;
            try { list.Add(Read(ni)); } catch (NetworkInformationException) { }
        }
        return list;
    }

    private static Adapter Read(NetworkInterface ni)
    {
        var props = ni.GetIPProperties();
        var v4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
        var gateway = props.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        int index = -1;
        bool dhcp = false;
        try
        {
            var p4 = props.GetIPv4Properties();
            index = p4.Index;
            dhcp = p4.IsDhcpEnabled;
        }
        catch (NetworkInformationException) { }
        long pin = 0, pout = 0, ein = 0, eout = 0;
        try
        {
            var s = ni.GetIPStatistics();
            pin = s.UnicastPacketsReceived; pout = s.UnicastPacketsSent;
            ein = s.IncomingPacketsWithErrors; eout = s.OutgoingPacketsWithErrors;
        }
        catch (Exception) { }
        var mac = ni.GetPhysicalAddress().GetAddressBytes();
        return new Adapter(
            ni.Name, ni.Description, KindOf(ni.NetworkInterfaceType, ni.Description), ni.OperationalStatus == OperationalStatus.Up,
            ni.Speed > 0 ? ni.Speed / 1_000_000 : 0, mac.Length == 0 ? "" : BitConverter.ToString(mac),
            index, v4?.Address, v4?.PrefixLength ?? 0, gateway,
            props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList(),
            dhcp, pin, pout, ein, eout);
    }

    /// <summary>The adapter Windows uses to reach the internet (reads the routing table only, so it works offline).</summary>
    public static Adapter? Primary(IReadOnlyList<Adapter>? adapters = null)
    {
        adapters ??= Adapters();
        var index = Native.BestInterface(IPAddress.Parse("1.1.1.1"));
        return adapters.FirstOrDefault(a => a.Index == index && a.Up)
            ?? adapters.FirstOrDefault(a => a.Up && a.Gateway is not null && a.Kind != AdapterKind.Vpn);
    }

    /// <summary>The router the PC would use: the primary adapter's gateway, or any up adapter's.</summary>
    public static IPAddress? Router(IReadOnlyList<Adapter> adapters, Adapter? primary) =>
        primary?.Gateway is { } g && primary.Kind != AdapterKind.Vpn
            ? g
            : adapters.FirstOrDefault(a => a.Up && a.Kind != AdapterKind.Vpn && a.Gateway is not null)?.Gateway;

    // MARK: Addresses

    public static bool IsSelfAssigned(IPAddress a) => a.GetAddressBytes() is [169, 254, ..];

    /// <summary>Addresses the hosts file uses to block a name.</summary>
    public static bool IsBlockingAddress(string address) =>
        address == "0.0.0.0" || address.StartsWith("127.", StringComparison.Ordinal) || address == "::" || address == "::1";

    public static uint ToUInt(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public static uint Mask(int prefix) => prefix <= 0 ? 0 : prefix >= 32 ? uint.MaxValue : uint.MaxValue << (32 - prefix);

    public static bool SameSubnet(IPAddress a, IPAddress b, int prefix) => (ToUInt(a) & Mask(prefix)) == (ToUInt(b) & Mask(prefix));

    /// <summary>Why an address can't work, or null when it's fine (same rules as the Mac).</summary>
    public static string? AddressProblem(IPAddress address, int prefix)
    {
        var b = address.GetAddressBytes();
        if (address.Equals(IPAddress.Any)) return "The address is 0.0.0.0.";
        if (b[0] == 127) return "The address is a loopback address.";
        if (b[0] >= 224) return "The address is a multicast or reserved address.";
        if (prefix is <= 0 or >= 32) return $"The prefix length /{prefix} can't work on a network.";
        var ip = ToUInt(address);
        var mask = Mask(prefix);
        if (prefix <= 30 && (ip & ~mask) == 0) return "The address is the network's own address.";
        if (prefix <= 30 && (ip | mask) == uint.MaxValue) return "The address is the network's broadcast address.";
        return null;
    }

    // MARK: Wi‑Fi (netsh wlan)

    public sealed record WlanInfo(
        string Name, string State, string? Ssid, string? Profile, int? SignalPercent, int? Rssi,
        string? RadioType, string? Band, int? Channel, double? ReceiveMbps, double? TransmitMbps)
    {
        public bool Connected => State.Equals("connected", StringComparison.OrdinalIgnoreCase);
        /// <summary>dBm: Windows 11 reports it; otherwise estimated from the percentage.</summary>
        public int? Dbm => Rssi ?? (SignalPercent is { } p ? SignalToDbm(p) : null);
    }

    /// <summary>dBm ≈ (percent ÷ 2) − 100.</summary>
    public static int SignalToDbm(int percent) => percent / 2 - 100;

    /// <summary>"Name : value" lines from netsh, by name.</summary>
    public static Dictionary<string, string> ParseColonFields(string block)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in block.Split('\n'))
        {
            var i = raw.IndexOf(" : ", StringComparison.Ordinal);
            if (i < 0) continue;
            var key = raw[..i].Trim();
            if (key.Length > 0 && !d.ContainsKey(key)) d[key] = raw[(i + 3)..].Trim();
        }
        return d;
    }

    /// <summary>Parses <c>netsh wlan show interfaces</c>: one entry per Wi‑Fi adapter.</summary>
    public static List<WlanInfo> ParseWlanInterfaces(string text)
    {
        var list = new List<WlanInfo>();
        var blocks = Regex.Split(text.Replace("\r", ""), @"\n(?=\s*Name\s+:)");
        foreach (var block in blocks)
        {
            var f = ParseColonFields(block);
            if (!f.TryGetValue("Name", out var name) || !f.TryGetValue("State", out var state)) continue;
            static int? Int(string? s) => int.TryParse(s?.TrimEnd('%', ' '), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
            static double? Dbl(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            list.Add(new WlanInfo(name, state, f.GetValueOrDefault("SSID"), f.GetValueOrDefault("Profile"),
                Int(f.GetValueOrDefault("Signal")), Int(f.GetValueOrDefault("Rssi")),
                f.GetValueOrDefault("Radio type"), f.GetValueOrDefault("Band"), Int(f.GetValueOrDefault("Channel")),
                Dbl(f.GetValueOrDefault("Receive rate (Mbps)")), Dbl(f.GetValueOrDefault("Transmit rate (Mbps)"))));
        }
        return list;
    }

    public sealed record WlanState(List<WlanInfo> Interfaces, bool LocationBlocked, string Raw);

    public static async Task<WlanState> Wlan()
    {
        var r = await Shell.Run("netsh.exe", new[] { "wlan", "show", "interfaces" }, 8);
        // Windows 11 24H2 hides Wi‑Fi details from apps until Location is allowed for desktop apps.
        var blocked = r.Text.Contains("location permission", StringComparison.OrdinalIgnoreCase);
        return new WlanState(ParseWlanInterfaces(r.Text), blocked, r.Text);
    }

    /// <summary>The Wi‑Fi radio is off (Wi‑Fi turned off in quick settings, or airplane mode).</summary>
    public static async Task<bool> WifiRadioOff()
    {
        var r = await Shell.Run("netsh.exe", new[] { "wlan", "show", "networks" }, 8);
        return r.Text.Contains("powered down", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>How many other networks nearby share a channel (from <c>netsh wlan show networks mode=bssid</c>).</summary>
    public static int NetworksOnChannel(string text, int channel, string? ownSsid)
    {
        int count = 0;
        string? ssid = null;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            var m = Regex.Match(line, @"^SSID \d+ : ?(.*)$");
            if (m.Success) { ssid = m.Groups[1].Value.Trim(); continue; }
            var c = Regex.Match(line, @"^Channel\s+:\s*(\d+)");
            if (c.Success && int.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture) == channel && ssid != ownSsid) count++;
        }
        return count;
    }

    // MARK: Adapters turned off (netsh interface show interface)

    public sealed record InterfaceState(string AdminState, string State, string Name)
    {
        public bool Disabled => AdminState.Equals("Disabled", StringComparison.OrdinalIgnoreCase);
    }

    public static List<InterfaceState> ParseInterfaceList(string text)
    {
        var list = new List<InterfaceState>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var m = Regex.Match(raw, @"^(Enabled|Disabled)\s+(\S+)\s+\S+\s+(.+?)\s*$");
            if (m.Success) list.Add(new InterfaceState(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value));
        }
        return list;
    }

    /// <summary>Physical adapters switched off in Windows (not VPN or virtual ones).</summary>
    public static async Task<List<string>> DisabledAdapters()
    {
        var r = await Shell.Run("netsh.exe", new[] { "interface", "show", "interface" }, 8);
        return ParseInterfaceList(r.Text)
            .Where(i => i.Disabled && !Regex.IsMatch(i.Name, "VPN|Bluetooth|vEthernet|Loopback", RegexOptions.IgnoreCase))
            .Select(i => i.Name).ToList();
    }

    // MARK: Routes (route print -4)

    public sealed record Route(string Destination, string Netmask, string Gateway, string Interface);

    public static List<Route> ParseRoutes(string text)
    {
        var list = new List<Route>();
        var active = false;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (raw.StartsWith("Active Routes", StringComparison.OrdinalIgnoreCase)) { active = true; continue; }
            if (raw.StartsWith("Persistent Routes", StringComparison.OrdinalIgnoreCase)) active = false;
            if (!active) continue;
            var f = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 4 && IPAddress.TryParse(f[0], out _) && IPAddress.TryParse(f[1], out _))
                list.Add(new Route(f[0], f[1], f[2], f[3]));
        }
        return list;
    }

    /// <summary>
    /// The two half-internet routes VPNs add (0.0.0.0/1 and 128.0.0.0/1) through an address that no adapter that's up
    /// has any more: a VPN that closed or crashed left them behind.
    /// </summary>
    public static List<Route> LeftoverVpnRoutes(IEnumerable<Route> routes, IEnumerable<IPAddress> liveAddresses)
    {
        var live = liveAddresses.Select(a => a.ToString()).ToHashSet();
        return routes.Where(r => r.Netmask == "128.0.0.0" && (r.Destination == "0.0.0.0" || r.Destination == "128.0.0.0")
                                 && !live.Contains(r.Interface)).ToList();
    }

    // MARK: VPN (rasdial)

    /// <summary>Connected Windows VPN (RAS) connections, from <c>rasdial</c> with no arguments.</summary>
    public static List<string> ParseRasdial(string text)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var start = lines.FindIndex(l => l.StartsWith("Connected to", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return new();
        return lines.Skip(start + 1).TakeWhile(l => !l.StartsWith("Command completed", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static async Task<List<string>> ConnectedRasVpns() =>
        ParseRasdial((await Shell.Run("rasdial.exe", Array.Empty<string>(), 8)).Text);

    // MARK: Firewall (netsh advfirewall)

    /// <summary>Profiles whose firewall blocks every incoming connection ("BlockInboundAlways"), from <c>firewallpolicy</c>.</summary>
    public static List<string> ParseFirewallBlockAll(string text)
    {
        var list = new List<string>();
        string? profile = null;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var p = Regex.Match(raw, @"^(\w+) Profile Settings");
            if (p.Success) { profile = p.Groups[1].Value; continue; }
            if (profile is not null && raw.StartsWith("Firewall Policy", StringComparison.OrdinalIgnoreCase)
                && raw.Contains("BlockInboundAlways", StringComparison.OrdinalIgnoreCase)) list.Add(profile);
        }
        return list;
    }

    // MARK: Hosts file

    public static string HostsPath => Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");

    /// <summary>Well-known sites a blocking hosts line breaks for work.</summary>
    public static readonly string[] WatchedSites =
    {
        "apple.com", "icloud.com", "google.com", "gstatic.com", "googleapis.com", "youtube.com",
        "microsoft.com", "office.com", "office365.com", "microsoftonline.com", "live.com",
        "outlook.com", "windows.net", "windowsupdate.com", "msftconnecttest.com", "zoom.us", "slack.com", "okta.com",
        "cloudflare.com", "example.com", "amazon.com", "fleetdm.com",
    };

    /// <summary>
    /// Names in the hosts file (its text) that block a watched site: pointed at 0.0.0.0, 127.x, :: or ::1.
    /// Lines that are comments, and names that only send a site somewhere else, aren't counted.
    /// </summary>
    public static List<string> BlockedSites(string text, IEnumerable<string>? watched = null)
    {
        var sites = (watched ?? WatchedSites).ToList();
        var names = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Split('#')[0];
            var fields = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !IsBlockingAddress(fields[0])) continue;
            foreach (var name in fields.Skip(1))
            {
                var lower = name.ToLowerInvariant();
                if (sites.Any(s => lower == s || lower.EndsWith("." + s, StringComparison.Ordinal)) && !names.Contains(lower)) names.Add(lower);
            }
        }
        return names;
    }

    /// <summary>The watched sites this PC's hosts file blocks (<paramref name="fleetServer"/>: also Fleet's own server).</summary>
    public static List<string> BlockedSitesOnThisPC(string? fleetServer = null)
    {
        try
        {
            var text = File.ReadAllText(HostsPath);
            return BlockedSites(text, WatchedSites.Concat(fleetServer is null ? Array.Empty<string>() : new[] { fleetServer.ToLowerInvariant() }));
        }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
    }
}
