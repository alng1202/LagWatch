using System;
using System.Collections.Generic;
using System.Linq;

namespace Netfix.Model;

/// <summary>Ordered by severity so the worst status of a report is simply the max.</summary>
public enum CheckStatus { Info = 0, Skipped, Pass, Warning, Fail }

/// <summary>Identifies a known problem so it can be explained in plain language (see <c>NetworkAdvisor</c>).</summary>
public enum IssueCode
{
    NoLink, WifiOff, WeakSignal, FairSignal, CrowdedChannel,
    /// <summary>A laptop on a cable (often a dock) whose Wi‑Fi is off, or on but not joined: unplugging leaves it offline.</summary>
    WifiOffOnCable, WifiNotJoinedOnCable,
    CableSlow, CableHalfDuplex, CableErrors,
    ManualAddress,
    VpnOn,
    ServiceDisabled, IpConflict, FirewallBlocksIncoming, ProxyBroken,
    RouterUnreachable, RouterNoPing, SelfAssignedIP, NoIP, InvalidIP, GatewayMismatch, NoGateway, PacketLoss, SlowLocal,
    BadDNSServer,
    TcpStackBroken, LocalDNSDown,
    /// <summary>The network's own DNS servers (not the router's own) don't answer.</summary>
    NetworkDNSDown,
    /// <summary>Wide routes left by a VPN that's no longer on, sending internet traffic to a gateway that isn't the router.</summary>
    VpnLeftoverRoutes,
    InternetUnreachable, SlowInternetLatency,
    /// <summary>The internet answers on other ports, but this network blocks secure (443) connections.</summary>
    HttpsBlocked,
    ClockWrong, TrafficIntercepted, TlsBlocked,
    NoDNS, DnsFailed, DnsSlow, CaptivePortal, WebFailed,
    /// <summary>Lookups fail with the PC's own DNS servers, typed in by hand, while a public one answers.</summary>
    DnsServersDown,
    /// <summary>No DNS server answers, not even a public one, though the internet works: the network blocks lookups.</summary>
    DnsBlocked,
    /// <summary>The hosts file blocks a well-known site (left by old software or a blocker).</summary>
    HostsFileBlocks,
    FleetUnreachable,
}

/// <summary>One observation made while checking a layer.</summary>
public sealed class Finding
{
    public string Title { get; }
    public CheckStatus Status { get; }
    public string Detail { get; }
    /// <summary>Suggested fix, shown when the finding is a warning or failure.</summary>
    public string? Advice { get; }
    public IssueCode? Code { get; }
    /// <summary>One-click fixes that need details only the check knows (which VPN, which adapter).</summary>
    public IReadOnlyList<Fix> Fixes { get; }

    public Finding(string title, CheckStatus status, string detail, string? advice = null, IssueCode? code = null, IEnumerable<Fix>? fixes = null)
    {
        Title = title;
        Status = status;
        Detail = detail;
        Advice = advice;
        Code = code;
        Fixes = fixes?.ToList() ?? new List<Fix>();
    }

    public override string ToString() => $"{Title} [{Status}]: {Detail}";
}

/// <summary>The result of checking a single OSI layer.</summary>
public sealed class LayerReport
{
    public OSILayer Layer { get; }
    public IReadOnlyList<Finding> Findings { get; }
    public DateTime Date { get; }

    public LayerReport(OSILayer layer, IEnumerable<Finding> findings, DateTime? date = null)
    {
        Layer = layer;
        Findings = findings.ToList();
        Date = date ?? DateTime.Now;
    }

    public CheckStatus Status
    {
        get
        {
            if (Findings.Count > 0 && Findings.All(f => f.Status == CheckStatus.Skipped)) return CheckStatus.Skipped;
            var counted = Findings.Where(f => f.Status != CheckStatus.Info && f.Status != CheckStatus.Skipped).ToList();
            return counted.Count == 0 ? CheckStatus.Pass : counted.Max(f => f.Status);
        }
    }

    /// <summary>One-line summary: the most severe finding's detail.</summary>
    public string Summary
    {
        get
        {
            var status = Status;
            var worst = Findings.FirstOrDefault(f => f.Status == status) ?? Findings.FirstOrDefault();
            if (worst is null) return "No checks ran.";
            return status == CheckStatus.Pass ? "All checks passed." : $"{worst.Title}: {worst.Detail}";
        }
    }

    public IReadOnlyList<string> Advice =>
        Findings.Where(f => f.Status >= CheckStatus.Warning && f.Advice is not null).Select(f => f.Advice!).ToList();

    public static LayerReport Skipped(OSILayer layer, string reason) =>
        new(layer, new[] { new Finding("Not run", CheckStatus.Skipped, reason) });
}
