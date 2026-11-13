using System;
using System.Collections.Generic;
using System.Linq;

namespace Netfix.Model;

/// <summary>What a plain-language result is about. Used to group results and to list what's healthy.</summary>
public enum Topic
{
    // Network (ordered bottom-up, like the OSI layers they come from)
    Connection, Router, Vpn, Internet, Security, Websites, Incoming, Speed,
    // This PC
    Processor, Memory, Storage, Battery, Upkeep,
    // Calls (Teams, Zoom and other call apps), and Bluetooth
    Sound, Microphone, CallAudio, Camera, CallPermissions, Bluetooth,
}

public static class Topics
{
    /// <summary>Checks of this PC itself (not the network), grouped under "This PC" in the results.</summary>
    public static bool IsPC(this Topic t) => t >= Topic.Processor;

    public static string Name(this Topic t) => t switch
    {
        Topic.Connection => "Wi‑Fi / cable",
        Topic.Router => "Router",
        Topic.Vpn => "VPN",
        Topic.Incoming => "Connections from other devices",
        Topic.Internet => "Internet",
        Topic.Security => "Secure websites",
        Topic.Websites => "Websites",
        Topic.Speed => "Internet speed",
        Topic.Processor => "Processor",
        Topic.Memory => "Memory",
        Topic.Storage => "Storage",
        Topic.Battery => "Battery & power",
        Topic.Upkeep => "Restarts",
        Topic.Sound => "Sound",
        Topic.Microphone => "Microphone",
        Topic.CallAudio => "Call speaker & microphone",
        Topic.Camera => "Camera",
        Topic.CallPermissions => "Call app permissions",
        _ => "Bluetooth",
    };

    /// <summary>Segoe Fluent Icons / MDL2 glyph.</summary>
    public static string Glyph(this Topic t) => t switch
    {
        Topic.Connection => "",
        Topic.Router => "",
        Topic.Vpn => "",
        Topic.Incoming => "",
        Topic.Internet => "",
        Topic.Security => "",
        Topic.Websites => "",
        Topic.Speed => "",
        Topic.Processor => "",
        Topic.Memory => "",
        Topic.Storage => "",
        Topic.Battery => "",
        Topic.Upkeep => "",
        Topic.Sound => "",
        Topic.Microphone => "",
        Topic.CallAudio => "",
        Topic.Camera => "",
        Topic.CallPermissions => "",
        _ => "",
    };
}

public enum Severity { Warning, Problem }

/// <summary>A problem explained for a non-technical user, with ways to fix it.</summary>
public sealed class Issue
{
    public string Id { get; init; } = "";
    public Severity Severity { get; set; } = Severity.Problem;
    public Topic Topic { get; init; }
    public string Title { get; init; } = "";
    public string Explanation { get; set; } = "";
    /// <summary>One-click fixes, best first.</summary>
    public List<Fix> Fixes { get; set; } = new();
    /// <summary>Step-by-step instructions for things Netfix can't do for the user.</summary>
    public List<string> Steps { get; set; } = new();
    /// <summary>Jargon-y detail for the curious, hidden by default.</summary>
    public string? Technical { get; set; }
}

/// <summary>Something that's fine, shown in the "looks good" list.</summary>
public sealed record HealthItem(Topic Topic, string Detail);

public enum FixKind
{
    QuitApp, TurnWiFiOn, RestartWiFi, RenewAddress, FlushDNS, DisconnectVPN, ResetNetworkPort,
    UseAutomaticAddress, UseAutomaticDNS, UnblockSites, RemoveLeftoverRoutes, EnableAdapter,
    UnmuteSpeakers, TurnUpMicrophone, UseSpeaker, UsePCMicrophone, UseCallPicks, RestartSound, TurnBluetoothOn, RestartBluetooth,
    Open,
}

public enum Destination
{
    WifiSettings, NetworkSettings, ProxySettings, StorageSettings, DateTimeSettings, BatterySettings,
    TaskManager, SignInPage, Fleet,
    SoundSettings, MicrophonePrivacy, CameraPrivacy, BluetoothSettings,
}

/// <summary>
/// A one-click fix. Anything that changes the PC is only run after the user clicks it,
/// and quitting an app asks for confirmation first.
/// </summary>
/// <param name="Arg">What the fix acts on: an adapter name, a process name, a VPN name, or a list joined with '\n'.</param>
/// <param name="Name">The name shown to the user (the app, the VPN).</param>
public sealed record Fix(FixKind Kind, string Arg = "", string Name = "", Destination Destination = default)
{
    public static Fix QuitApp(string name, string processName) => new(FixKind.QuitApp, processName, name);
    public static readonly Fix TurnWiFiOn = new(FixKind.TurnWiFiOn);
    public static readonly Fix RestartWiFi = new(FixKind.RestartWiFi);
    public static Fix RenewAddress(string adapter) => new(FixKind.RenewAddress, adapter);
    public static readonly Fix FlushDNS = new(FixKind.FlushDNS);
    public static Fix DisconnectVPN(string name) => new(FixKind.DisconnectVPN, name, name);
    public static Fix ResetNetworkPort(string adapter) => new(FixKind.ResetNetworkPort, adapter);
    public static Fix UseAutomaticAddress(string adapter) => new(FixKind.UseAutomaticAddress, adapter);
    public static Fix UseAutomaticDNS(string adapter) => new(FixKind.UseAutomaticDNS, adapter);
    public static Fix UnblockSites(IEnumerable<string> names) => new(FixKind.UnblockSites, string.Join("\n", names));
    public static Fix RemoveLeftoverRoutes(IEnumerable<string> routes) => new(FixKind.RemoveLeftoverRoutes, string.Join("\n", routes));
    public static Fix EnableAdapter(string adapter) => new(FixKind.EnableAdapter, adapter, adapter);
    public static readonly Fix UnmuteSpeakers = new(FixKind.UnmuteSpeakers);
    public static readonly Fix TurnUpMicrophone = new(FixKind.TurnUpMicrophone);
    /// <summary>Makes this speaker or pair of headphones the one sound plays from (by Core Audio device id).</summary>
    public static Fix UseSpeaker(string id, string name) => new(FixKind.UseSpeaker, id, name);
    /// <summary>Makes the PC's own microphone the one in use, so Bluetooth headphones keep their full sound.</summary>
    public static readonly Fix UsePCMicrophone = new(FixKind.UsePCMicrophone);
    /// <summary>Switches back to the speaker and/or microphone the user picked for calls (names shown on the button).</summary>
    public static Fix UseCallPicks(string? speaker, string? microphone) => new(FixKind.UseCallPicks, speaker ?? "", microphone ?? "");
    public static readonly Fix RestartSound = new(FixKind.RestartSound);
    public static readonly Fix TurnBluetoothOn = new(FixKind.TurnBluetoothOn);
    /// <summary>Turns Bluetooth off and on, which reconnects headphones, mice and keyboards.</summary>
    public static readonly Fix RestartBluetooth = new(FixKind.RestartBluetooth);
    public static Fix Open(Destination d, string arg = "") => new(FixKind.Open, arg, "", d);

    public IReadOnlyList<string> ArgList => Arg.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public string Label => Kind switch
    {
        FixKind.QuitApp => $"Quit {Name}",
        FixKind.TurnWiFiOn => "Turn Wi‑Fi on",
        FixKind.RestartWiFi => "Restart Wi‑Fi",
        FixKind.RenewAddress => "Rejoin the network",
        FixKind.FlushDNS => "Refresh website lookups",
        FixKind.DisconnectVPN => "Turn off VPN",
        FixKind.ResetNetworkPort => "Reset cable connection",
        FixKind.UseAutomaticAddress => "Fix network settings",
        FixKind.UseAutomaticDNS => "Fix website lookups",
        FixKind.UnblockSites => "Remove the block",
        FixKind.RemoveLeftoverRoutes => "Clear old VPN settings",
        FixKind.EnableAdapter => "Turn this connection back on",
        FixKind.UnmuteSpeakers => "Turn sound on",
        FixKind.TurnUpMicrophone => "Turn microphone up",
        FixKind.UseSpeaker => $"Use {Name}",
        FixKind.UsePCMicrophone => "Use PC microphone",
        FixKind.UseCallPicks => (Arg, Name) switch
        {
            ({ Length: > 0 } s, { Length: > 0 } m) when s == m => $"Switch to {s}",
            ({ Length: > 0 }, { Length: > 0 }) => "Switch to your speaker and microphone",
            ({ Length: > 0 } s, _) => $"Switch to {s}",
            (_, { Length: > 0 } m) => $"Switch to {m}",
            _ => "Switch to your picks",
        },
        FixKind.RestartSound => "Restart sound",
        FixKind.TurnBluetoothOn => "Turn Bluetooth on",
        FixKind.RestartBluetooth => "Restart Bluetooth",
        _ => Destination switch
        {
            Destination.WifiSettings => "Open Wi‑Fi settings",
            Destination.NetworkSettings => "Open Network settings",
            Destination.ProxySettings => "Open Proxy settings",
            Destination.StorageSettings => "Open Storage settings",
            Destination.DateTimeSettings => "Open Date & time settings",
            Destination.BatterySettings => "Open Power & battery settings",
            Destination.TaskManager => "Open Task Manager",
            Destination.SignInPage => "Open sign-in page",
            Destination.SoundSettings => "Open Sound settings",
            Destination.MicrophonePrivacy or Destination.CameraPrivacy => "Open Privacy settings",
            Destination.BluetoothSettings => "Open Bluetooth settings",
            _ => "Open Fleet",
        },
    };

    /// <summary>Segoe Fluent Icons / MDL2 glyph for the button.</summary>
    public string Glyph => Kind switch
    {
        FixKind.QuitApp => "",
        FixKind.TurnWiFiOn or FixKind.RestartWiFi => "",
        FixKind.RenewAddress => "",
        FixKind.FlushDNS => "",
        FixKind.DisconnectVPN or FixKind.RemoveLeftoverRoutes => "",
        FixKind.ResetNetworkPort => "",
        FixKind.UseAutomaticAddress or FixKind.UseAutomaticDNS => "",
        FixKind.UnblockSites => "",
        FixKind.EnableAdapter => "",
        FixKind.UnmuteSpeakers or FixKind.UseSpeaker => "",
        FixKind.TurnUpMicrophone or FixKind.UsePCMicrophone => "",
        FixKind.UseCallPicks => "",
        FixKind.RestartSound => "",
        FixKind.TurnBluetoothOn or FixKind.RestartBluetooth => "",
        _ => "",
    };

    /// <summary>Fix all runs it: it changes something to fix the problem. Buttons that only open settings don't count.</summary>
    public bool RunsInFixAll => Kind != FixKind.Open;

    /// <summary>Windows will ask for an administrator (UAC).</summary>
    public bool NeedsAdmin => Kind is FixKind.RenewAddress or FixKind.ResetNetworkPort or FixKind.UseAutomaticAddress
        or FixKind.UseAutomaticDNS or FixKind.UnblockSites or FixKind.RemoveLeftoverRoutes or FixKind.EnableAdapter or FixKind.RestartSound;

    /// <summary>Text for an extra confirmation step, for fixes that could lose the user's work.</summary>
    public string? Confirmation => Kind switch
    {
        FixKind.QuitApp => $"{Name} will be asked to close. If you have unsaved work, it will give you a chance to save it first.",
        FixKind.ResetNetworkPort => "Your cable connection will switch off for a few seconds and come back, like unplugging the cable.",
        FixKind.UseAutomaticAddress => $"This replaces network settings that were typed in by hand. Netfix shows you the old ones afterwards, in case you need them.",
        FixKind.UseAutomaticDNS => $"This replaces website lookup settings that were typed in by hand, so your router handles them. Netfix shows you the old ones afterwards, in case you need them.",
        FixKind.UnblockSites => UnblockConfirmation(ArgList),
        FixKind.RestartSound => "Sound stops for a moment and comes back. In a call, you may need to pick your speaker and microphone again.",
        FixKind.RestartBluetooth => "Bluetooth turns off for a few seconds. Headphones, mice and keyboards reconnect by themselves. In a call, your sound may switch to the PC for a moment.",
        _ => null,
    };

    private static string UnblockConfirmation(IReadOnlyList<string> names) =>
        $"Netfix turns off the lines in your PC's hosts file that block {string.Join(", ", names.Take(3))}"
        + (names.Count > 3 ? $" and {names.Count - 3} more" : "")
        + ". They stay in the file, marked “Turned off by Netfix”, so they can be put back.";
}

/// <summary>Which scan ran: the full checkup or one of the custom scans.</summary>
public enum CheckScope { Full, Performance, Network, Hardware, Sound }

public static class CheckScopes
{
    /// <summary>Speakers, microphone, camera, call-app permissions and Bluetooth (the full checkup includes them).</summary>
    public static bool ChecksSound(this CheckScope s) => s is CheckScope.Full or CheckScope.Sound;
    public static bool ChecksPerformance(this CheckScope s) => s is CheckScope.Full or CheckScope.Performance;
    public static bool ChecksHardware(this CheckScope s) => s is CheckScope.Full or CheckScope.Hardware;
    public static bool ChecksNetwork(this CheckScope s) => s is CheckScope.Full or CheckScope.Network;
    /// <summary>The speed test only runs with the full checkup.</summary>
    public static bool RunsSpeedTest(this CheckScope s) => s == CheckScope.Full;

    public static string Title(this CheckScope s) => s switch
    {
        CheckScope.Performance => "Performance",
        CheckScope.Network => "Network",
        CheckScope.Hardware => "Hardware",
        CheckScope.Sound => "Sound, camera & Bluetooth",
        _ => "Check My Device",
    };
}

public sealed class CheckupResult
{
    public List<Issue> Issues { get; init; } = new();
    public List<HealthItem> Healthy { get; init; } = new();
    /// <summary>Topics that couldn't be checked because a more basic problem got in the way.</summary>
    public List<Topic> Blocked { get; init; } = new();
    /// <summary>Which scan produced it (used for "Check again" and the summary).</summary>
    public CheckScope Scope { get; init; } = CheckScope.Full;
    public DateTime Date { get; init; } = DateTime.Now;
    /// <summary>The layer reports behind it, for Network Details and the network report.</summary>
    public List<LayerReport> Reports { get; init; } = new();

    public int ProblemCount => Issues.Count(i => i.Severity == Severity.Problem);

    public string Headline => (Issues.Count, ProblemCount) switch
    {
        (0, _) => "Everything looks good",
        (1, 1) => "We found a problem",
        (1, _) => "We found something worth a look",
        var (n, _) => $"We found {n} things to look at",
    };
}
