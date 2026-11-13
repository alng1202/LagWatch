using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Netfix.Model;

namespace Netfix.Diagnostics;

/// <summary>Measures download speed. Needs the internet (it's a layer 5–7 check), so only run it when online.</summary>
public static class SpeedTest
{
    public const int DownloadBytes = 10_000_000;

    /// <summary>Slower than this, video calls start to break up: the only point where speed is a problem worth a card.</summary>
    public const double TooSlow = 4.0;
    /// <summary>Below this share of the network's usual speed, it's "slower than usual" even when calls would still work.</summary>
    public const double MuchSlower = 0.5;

    /// <summary>Megabits per second, or null if the download failed.</summary>
    public static async Task<double?> DownloadMbps()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var sw = Stopwatch.StartNew();
        try
        {
            var data = await client.GetByteArrayAsync($"https://speed.cloudflare.com/__down?bytes={DownloadBytes}");
            if (data.Length == 0) return null;
            return data.Length * 8 / Math.Max(sw.Elapsed.TotalSeconds, 0.001) / 1_000_000;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return null; }
    }

    /// <summary>The Wi‑Fi connection the speed was measured over: signal (dBm) and link rate (Mbps, 0 if unknown).</summary>
    public sealed record WiFiLink(int Rssi, double Rate)
    {
        /// <summary>The Wi‑Fi itself would hold the internet back: a weak signal, or a slow link.</summary>
        public bool IsWeak => Rssi < -72 || (Rate > 0 && Rate < 20);
    }

    /// <summary>
    /// The speed result. A slow speed is blamed on the Wi‑Fi only when the Wi‑Fi is weak; otherwise it's the internet
    /// service. Null <paramref name="wifi"/>: not on Wi‑Fi. <paramref name="usual"/>: this network's usual speed.
    /// </summary>
    public static (Issue? Issue, HealthItem? Healthy) Evaluate(double? mbps, WiFiLink? wifi = null, double? usual = null)
    {
        if (mbps is not { } speedMbps) return (null, null);
        var speed = $"{Math.Round(speedMbps):0} Mbps";
        var usualText = usual is { } u ? $"usually about {Math.Round(u):0} Mbps on this network" : null;
        var link = wifi is null ? "; not on Wi‑Fi" : $"; Wi‑Fi: about {wifi.Rssi} dBm, link rate {wifi.Rate:0} Mbps";
        var technical = $"Download: {speedMbps:0.0} Mbps (10 MB from speed.cloudflare.com)" + (usual is { } u2 ? $"; usual on this network: {u2:0.0} Mbps" : "") + link;
        var wifiToBlame = wifi is not null && (wifi.IsWeak || (usual is { } u3 && wifi.Rate > 0 && wifi.Rate < u3));
        var connection = wifi is null ? "connection" : "Wi‑Fi";

        if (speedMbps >= TooSlow)
        {
            if (usual is { } usualMbps && speedMbps < usualMbps * MuchSlower)
            {
                return (new Issue
                {
                    Id = "speedBelowUsual", Severity = Severity.Warning, Topic = Topic.Speed,
                    Title = wifiToBlame ? "Your Wi‑Fi is slower than usual" : "Your internet is slower than usual",
                    Explanation = $"Downloads are running at about {speed}, {usualText}. " + (wifiToBlame
                        ? "Your Wi‑Fi connection is weaker than usual, and that's what holds it back."
                        : $"Your {connection} to the router is fine, so the slowdown is the internet itself: it may be busy right now."),
                    Steps = wifiToBlame
                        ? new() { "Move closer to your router (the Wi‑Fi box).", "Or plug in a network cable.", "At home? Restart your router.", $"Still slow tomorrow? {Support.ContactStep}" }
                        : new() { "Ask if anyone nearby is streaming or downloading something big.", "Pause big downloads or uploads on this PC.", "At home? Restart your router.", $"Still slow tomorrow? {Support.ContactStep}" },
                    Technical = technical,
                }, null);
            }
            return (null, new HealthItem(Topic.Speed, speedMbps >= 25 ? $"{speed} — fast enough for anything" : $"{speed} — fine for calls and browsing"));
        }
        var measured = $"Downloads are running at about {speed}" + (usualText is null ? "" : $" ({usualText})") + ", too slow for smooth video calls.";
        if (wifi is not null && (wifi.IsWeak || wifiToBlame))
            return (new Issue
            {
                Id = "speed", Topic = Topic.Speed, Title = "Your Wi‑Fi is slowing your internet",
                Explanation = measured + " Your Wi‑Fi connection is weak, and that's what holds it back.",
                Steps = { "Move closer to your router (the Wi‑Fi box).", "Or plug in a network cable.", "At home? Restart your router.", $"Always slow? {Support.ContactStep}" },
                Technical = technical,
            }, null);
        return (new Issue
        {
            Id = "speed", Topic = Topic.Speed, Title = "Your internet is very slow",
            Explanation = measured + $" Your {connection} to the router is fine, so the slowdown is the internet itself: it may be busy, or the plan may be slow.",
            Steps = { "Ask if anyone nearby is streaming or downloading something big.", "Pause big downloads or uploads on this PC.", "At home? Restart your router.", $"Always slow? {Support.ContactStep}" },
            Technical = technical,
        }, null);
    }
}

/// <summary>The last 10 speeds per network, keyed by the router's hardware address (+ VPN), in Netfix's settings folder.</summary>
public static class SpeedHistory
{
    public const int Keep = 10;
    private static string FilePath => Path.Combine(App.Preferences.Folder, "speed-history.json");

    private static Dictionary<string, List<double>> Load()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, List<double>>>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception) { return new(); }
    }

    /// <summary>The median of this network's recent speeds, once there are at least 3.</summary>
    public static double? Usual(string network) => Median(Load().GetValueOrDefault(network) ?? new());

    public static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count < 3) return null;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    public static void Record(string network, double mbps)
    {
        var all = Load();
        var list = all.GetValueOrDefault(network) ?? new();
        list.Add(mbps);
        all[network] = list.TakeLast(Keep).ToList();
        try
        {
            Directory.CreateDirectory(App.Preferences.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all));
        }
        catch (Exception) { }
    }
}
