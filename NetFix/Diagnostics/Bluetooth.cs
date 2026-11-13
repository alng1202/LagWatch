using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Netfix.Model;
using global::Windows.Devices.Bluetooth;
using global::Windows.Devices.Enumeration;
using global::Windows.Devices.Radios;

namespace Netfix.Diagnostics;

/// <summary>
/// Bluetooth (part of the Sound, camera & Bluetooth checks): on or off, paired devices, which are connected, their
/// batteries, and headphones that aren't being used. Uses Windows' own device APIs; no permission prompts.
/// </summary>
public static class Bluetooth
{
    /// <summary>A battery below this is worth a warning.</summary>
    public const int LowBattery = 15;

    public sealed record PairedDevice(string Id, string Name, bool Connected, bool IsHeadphones, int? Battery);

    /// <summary>On or off, and the paired devices. Null when this PC has no Bluetooth.</summary>
    public sealed record State(bool On, List<PairedDevice> Devices)
    {
        public IEnumerable<PairedDevice> Connected => Devices.Where(d => d.Connected);
    }

    private const string IsConnected = "System.Devices.Aep.IsConnected";
    private const string MajorClass = "System.Devices.Aep.Bluetooth.Cod.Major";
    /// <summary>DEVPKEY_Bluetooth_Battery, set by Windows for devices that report their battery.</summary>
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    public static async Task<Radio?> Radio()
    {
        try
        {
            if (await global::Windows.Devices.Radios.Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null;
            return (await global::Windows.Devices.Radios.Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
        }
        catch (Exception) { return null; }
    }

    public static async Task<State?> Read()
    {
        var radio = await Radio();
        if (radio is null) return null;
        var devices = new List<PairedDevice>();
        try
        {
            var batteries = await Batteries();
            var found = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                                                              new[] { IsConnected, MajorClass });
            foreach (var d in found)
            {
                var connected = d.Properties.TryGetValue(IsConnected, out var c) && c is true;
                // Major device class 4 is Audio/Video (headphones, speakers, headsets).
                var audio = d.Properties.TryGetValue(MajorClass, out var m) && m is byte b && b == 4;
                devices.Add(new PairedDevice(d.Id, d.Name, connected, audio || Audio.HeadphoneName(d.Name),
                                             batteries.TryGetValue(d.Name, out var level) ? level : null));
            }
            var le = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), new[] { IsConnected });
            foreach (var d in le.Where(d => devices.All(x => x.Name != d.Name)))
            {
                var connected = d.Properties.TryGetValue(IsConnected, out var c) && c is true;
                devices.Add(new PairedDevice(d.Id, d.Name, connected, Audio.HeadphoneName(d.Name),
                                             batteries.TryGetValue(d.Name, out var level) ? level : null));
            }
        }
        catch (Exception) { }
        return new State(radio.State == RadioState.On, devices);
    }

    /// <summary>Battery levels Windows knows, by device name (the same numbers Settings → Bluetooth & devices shows).</summary>
    private static async Task<Dictionary<string, int>> Batteries()
    {
        var levels = new Dictionary<string, int>();
        try
        {
            var nodes = await DeviceInformation.FindAllAsync("System.Devices.Present:=System.StructuredQueryType.Boolean#True",
                                                              new[] { BatteryKey }, DeviceInformationKind.Device);
            foreach (var n in nodes)
                if (n.Properties.TryGetValue(BatteryKey, out var v) && v is byte level && !string.IsNullOrEmpty(n.Name))
                    levels.TryAdd(n.Name, level);
        }
        catch (Exception) { }
        return levels;
    }

    /// <summary>Turns the Bluetooth radio on or off. False when Windows doesn't allow it.</summary>
    public static async Task<bool> SetPower(bool on)
    {
        var radio = await Radio();
        if (radio is null) return false;
        try { return await radio.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed; }
        catch (Exception) { return false; }
    }

    private static string Is(string name, string singular, string plural) => name.EndsWith("s") ? plural : singular;

    /// <summary>Off with devices paired, headphones in call mode, low batteries; otherwise a "looks good" line naming what's connected.</summary>
    public static (List<Issue> Issues, List<HealthItem> Healthy) Evaluate(State? state, Audio.Device? headsetMicrophone, bool pcHasMicrophone)
    {
        if (state is null) return (new(), new());
        if (!state.On)
        {
            if (state.Devices.Count == 0) return (new(), new());
            return (new()
            {
                new Issue
                {
                    Id = "btOff", Topic = Topic.Bluetooth, Title = "Bluetooth is off",
                    Explanation = "Your Bluetooth headphones, mouse or keyboard can't connect while it's off.",
                    Fixes = { Fix.TurnBluetoothOn },
                    Steps = { "Click Turn Bluetooth on.", "Wait a few seconds for your devices to connect." },
                    Technical = "Paired: " + string.Join(", ", state.Devices.Select(d => d.Name)),
                },
            }, new());
        }

        var issues = new List<Issue>();
        if (headsetMicrophone is not null && pcHasMicrophone)
        {
            var name = headsetMicrophone.DeviceName.Length > 0 ? headsetMicrophone.DeviceName : headsetMicrophone.Name;
            issues.Add(new Issue
            {
                Id = "btCallMode", Severity = Severity.Warning, Topic = Topic.Bluetooth,
                Title = $"{name} sound worse while their microphone is on",
                Explanation = "Bluetooth headphones switch to a low-quality call mode when their microphone is in use, so sound gets flat and muffled. Use your PC's microphone and they keep full sound.",
                Fixes = { Fix.UsePCMicrophone },
                Steps = { "Click Use PC microphone.", "In Zoom or Teams, pick your PC's microphone if it asks." },
                Technical = $"Default microphone: {headsetMicrophone.Name} (Bluetooth: the hands-free profile replaces high-quality sound while the mic is open)",
            });
        }
        foreach (var device in state.Connected)
        {
            if (device.Battery is { } level && level < LowBattery)
                issues.Add(new Issue
                {
                    Id = $"btBattery-{device.Name}", Severity = Severity.Warning, Topic = Topic.Bluetooth,
                    Title = $"{device.Name} {Is(device.Name, "is", "are")} almost out of battery",
                    Explanation = $"Only {level}% left, so {Is(device.Name, "it", "they")} may disconnect soon.",
                    Steps = { $"Charge {device.Name}." },
                });
        }
        if (issues.Count > 0) return (issues, new());
        var connected = state.Connected.Select(d => d.Battery is { } b ? $"{d.Name} ({b}%)" : d.Name).ToList();
        return (new(), new() { new HealthItem(Topic.Bluetooth, connected.Count == 0 ? "On, nothing connected" : "On · " + string.Join(", ", connected)) });
    }

    /// <summary>
    /// Sound plays from the PC's own speakers while headphones are right there: connected but not in use, or Bluetooth
    /// headphones that are paired but not connected while the PC's speakers are silent (then the person expected the
    /// headphones; without this, the muted speakers read as "Your sound is turned off", whose fix makes the PC louder).
    /// </summary>
    public static Issue? HeadphonesIssue(State? state, Audio.Device? output, IReadOnlyList<Audio.Device> outputs)
    {
        if (output is null || !output.IsBuiltIn) return null;
        var bluetooth = (state?.Connected ?? Enumerable.Empty<PairedDevice>()).Where(d => d.IsHeadphones).Select(d => d.Name).ToHashSet();
        if (outputs.FirstOrDefault(o => o.Id != output.Id && (o.IsHeadphones || o.Bluetooth || bluetooth.Contains(o.DeviceName))) is { } ready)
        {
            var name = ready.DeviceName.Length > 0 ? ready.DeviceName : ready.Name;
            return new Issue
            {
                Id = "headphonesNotUsed", Severity = Severity.Warning, Topic = Topic.Sound,
                Title = $"Your sound is playing from your PC, not {name}",
                Explanation = $"{name} {Is(name, "is", "are")} connected, but your PC is using its own speakers.",
                Fixes = { Fix.UseSpeaker(ready.Id, name) },
                Steps = { $"Click Use {name}.", "In Zoom or Teams, set the speaker to “Same as System” (or Default) if it asks." },
                Technical = $"Default output: {output.Name}; available: {ready.Name}",
            };
        }
        var silent = output.Muted == true || (output.Volume ?? 1) < 0.05;
        if (silent && state is { On: true } && state.Devices.FirstOrDefault(d => !d.Connected && d.IsHeadphones) is { } pair)
            return new Issue
            {
                Id = "headphonesNotUsed", Severity = Severity.Warning, Topic = Topic.Sound,
                Title = $"{pair.Name} {Is(pair.Name, "isn't", "aren't")} connected",
                Explanation = $"Your PC's speakers are off, so you hear nothing. Your {pair.Name} may be in their case, or connected to your phone.",
                Fixes = { Fix.Open(Destination.BluetoothSettings) },
                Steps = { "Take them out of the case.", "Click Open Bluetooth settings.", $"Click {pair.Name}, then Connect." },
                Technical = $"Default output: {output.Name}; {pair.Name} paired, not connected",
            };
        return null;
    }
}
