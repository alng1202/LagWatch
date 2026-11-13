using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using Netfix.Model;

namespace Netfix.Diagnostics;

/// <summary>
/// Runs layer checks and enforces the project rule: layers 1–4 always run (they are offline-capable);
/// layers 5–7 are skipped when there is no internet.
/// </summary>
public sealed class DiagnosticEngine
{
    public const string OfflineReason = "Layers 5–7 need the internet to reach non-local servers, and this PC is offline.";

    private readonly Dictionary<OSILayer, ILayerCheck> checks;
    private readonly Func<bool> internetAvailable;

    public DiagnosticEngine(IEnumerable<ILayerCheck> checks, Func<bool> internetAvailable)
    {
        this.checks = checks.ToDictionary(c => c.Layer);
        this.internetAvailable = internetAvailable;
    }

    public static DiagnosticEngine Live() => new(
        new ILayerCheck[] { new PhysicalCheck(), new DataLinkCheck(), new NetworkCheck(), new TransportCheck(),
                            new SessionCheck(), new PresentationCheck(), new ApplicationCheck() },
        ConnectivityMonitor.CurrentlyOnline);

    public async Task<LayerReport> Run(OSILayer layer)
    {
        if (layer.RequiresInternet() && !internetAvailable()) return LayerReport.Skipped(layer, OfflineReason);
        if (!checks.TryGetValue(layer, out var check)) return LayerReport.Skipped(layer, "No check is available for this layer.");
        try { return new LayerReport(layer, await check.Run()); }
        catch (Exception e) { return new LayerReport(layer, new[] { new Finding("Check failed", CheckStatus.Warning, e.Message) }); }
    }
}

/// <summary>Watches whether the PC has a usable path off the local network (an adapter that's up with a router).</summary>
public sealed class ConnectivityMonitor : IDisposable
{
    public event Action? Changed;
    public bool IsOnline { get; private set; }
    public string Description { get; private set; } = "Checking…";

    public ConnectivityMonitor()
    {
        Update();
        NetworkChange.NetworkAddressChanged += OnChange;
        NetworkChange.NetworkAvailabilityChanged += OnAvailability;
    }

    private void OnChange(object? sender, EventArgs e) => Update();
    private void OnAvailability(object? sender, NetworkAvailabilityEventArgs e) => Update();

    /// <summary>Snapshot used by the engine at the moment a check starts.</summary>
    public static bool CurrentlyOnline()
    {
        try
        {
            return NetworkInfo.Adapters().Any(a => a.Up && (a.Gateway is not null || a.Kind == NetworkInfo.AdapterKind.Vpn));
        }
        catch (Exception) { return false; }
    }

    private void Update()
    {
        var adapters = NetworkInfo.Adapters();
        var primary = NetworkInfo.Primary(adapters);
        IsOnline = adapters.Any(a => a.Up && a.Gateway is not null);
        var medium = primary?.Kind switch
        {
            NetworkInfo.AdapterKind.WiFi => "Wi‑Fi",
            NetworkInfo.AdapterKind.Ethernet => "Ethernet",
            NetworkInfo.AdapterKind.Vpn => "VPN",
            _ => "network",
        };
        Description = IsOnline ? $"Online via {medium}" : "Offline — layers 1–4 only";
        Changed?.Invoke();
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnChange;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailability;
    }
}

/// <summary>Turns a set of reports into a bottom-up troubleshooting verdict.</summary>
public static class Diagnosis
{
    public static string Summarize(IEnumerable<LayerReport> reports)
    {
        var sorted = reports.OrderBy(r => r.Layer).ToList();
        if (sorted.Count == 0) return "No layers have been checked yet.";
        var lines = new List<string>();
        var ran = sorted.Where(r => r.Status != CheckStatus.Skipped).ToList();
        var skipped = sorted.Where(r => r.Status == CheckStatus.Skipped).ToList();

        if (ran.FirstOrDefault(r => r.Status == CheckStatus.Fail) is { } failing)
        {
            lines.Add($"The lowest failing layer is {failing.Layer.Title()}. {failing.Summary}");
            lines.Add("Fix this first — every layer above it depends on it.");
            lines.AddRange(failing.Advice.Select(a => "• " + a));
        }
        else if (ran.Count > 0 && ran.All(r => r.Status == CheckStatus.Pass))
        {
            lines.Add($"Everything checked looks healthy (layer{(ran.Count == 1 ? "" : "s")} {string.Join(", ", ran.Select(r => (int)r.Layer))}).");
        }

        var warnings = ran.Where(r => r.Status == CheckStatus.Warning).ToList();
        if (warnings.Count > 0)
        {
            lines.Add("Worth a look:");
            foreach (var r in warnings)
            {
                lines.Add($"• {r.Layer.Title()}: {r.Summary}");
                if (r.Advice.FirstOrDefault() is { } advice) lines.Add($"  ↳ {advice}");
            }
        }
        if (skipped.Any(r => r.Layer.RequiresInternet()))
            lines.Add("Layers 5–7 were skipped: they need the internet to reach non-local servers. Fix the offline layers (1–4) first.");
        return string.Join("\n", lines);
    }
}
