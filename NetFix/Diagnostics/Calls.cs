using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using Netfix.Model;
using global::Windows.Devices.Enumeration;

namespace Netfix.Diagnostics;

/// <summary>
/// "Check sound & camera", the Sound, camera & Bluetooth scan: the speaker and microphone in use, their volume, the
/// Windows Audio service, cameras, Windows' camera and microphone privacy switches for call apps, and Bluetooth.
/// </summary>
public static class Calls
{
    public enum Permission { Microphone, Camera }

    /// <summary>Where Windows blocks a call app from the microphone or camera.</summary>
    public enum BlockScope
    {
        /// <summary>A Windows policy (Group Policy) on this PC: changed with an administrator account.</summary>
        Policy,
        /// <summary>"Microphone access" / "Camera access" is off for the whole PC.</summary>
        Device,
        /// <summary>"Let apps access your microphone" is off.</summary>
        AllApps,
        /// <summary>"Let desktop apps access your microphone" is off: Zoom, Teams (classic), Webex, Slack…</summary>
        DesktopApps,
        /// <summary>One Store app (the new Teams) is switched off.</summary>
        App,
    }

    public sealed record Block(Permission Permission, BlockScope Scope, string? App = null);

    /// <summary>Store call apps and their names in Settings → Privacy & security.</summary>
    public static readonly Dictionary<string, string> PackagedCallApps = new()
    {
        ["MSTeams_8wekyb3d8bbwe"] = "Microsoft Teams",
        ["MicrosoftTeams_8wekyb3d8bbwe"] = "Microsoft Teams",
        ["Microsoft.SkypeApp_kzf8qxf38zg5c"] = "Skype",
        ["5319275A.WhatsAppDesktop_cv1g1gvanyjgm"] = "WhatsApp",
        ["ZoomVideoCommunicationsInc.ZoomforWindows_1n6bfcqfgnp3m"] = "Zoom",
    };

    private const string Consent = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    /// <summary>Reads Windows' privacy switches for the microphone and camera (what Settings → Privacy & security shows).</summary>
    public static List<Block> Blocks()
    {
        var blocks = new List<Block>();
        foreach (var permission in new[] { Permission.Microphone, Permission.Camera })
        {
            var capability = permission == Permission.Microphone ? "microphone" : "webcam";
            var policyName = permission == Permission.Microphone ? "LetAppsAccessMicrophone" : "LetAppsAccessCamera";
            using var policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy");
            if (policy?.GetValue(policyName) is int p && p == 2) { blocks.Add(new Block(permission, BlockScope.Policy)); continue; }
            if (Denied(Registry.LocalMachine, $@"{Consent}\{capability}")) { blocks.Add(new Block(permission, BlockScope.Device)); continue; }
            if (Denied(Registry.CurrentUser, $@"{Consent}\{capability}")) { blocks.Add(new Block(permission, BlockScope.AllApps)); continue; }
            if (Denied(Registry.CurrentUser, $@"{Consent}\{capability}\NonPackaged")) blocks.Add(new Block(permission, BlockScope.DesktopApps));
            foreach (var (package, app) in PackagedCallApps)
                if (Denied(Registry.CurrentUser, $@"{Consent}\{capability}\{package}") && blocks.All(b => b.App != app || b.Permission != permission))
                    blocks.Add(new Block(permission, BlockScope.App, app));
        }
        return blocks;
    }

    private static bool Denied(RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue("Value") is string v && v.Equals("Deny", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    /// <summary>Whether the Windows Audio service is running (without it, nothing plays or records).</summary>
    public static async Task<bool> SoundServiceRunning()
    {
        var r = await Shell.Run("sc.exe", new[] { "query", "Audiosrv" }, 6);
        return !r.Ok || r.Text.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<List<string>> Cameras()
    {
        try { return (await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture)).Where(d => d.IsEnabled).Select(d => d.Name).ToList(); }
        catch (Exception) { return new(); }
    }

    /// <summary>Everything a call needs, as plain-language results.</summary>
    public static async Task<(List<Issue> Issues, List<HealthItem> Healthy)> Check()
    {
        var running = SoundServiceRunning();
        var cameras = Cameras();
        var bluetooth = Bluetooth.Read();
        var outputs = Audio.Devices(false);
        var inputs = Audio.Devices(true);
        var speakers = Audio.Default(false);
        var microphone = Audio.Default(true);
        var bt = await bluetooth;

        var result = Evaluate(speakers, microphone, outputs, inputs, await cameras, await running, Blocks(), CallPicks.Load());
        // Headphones right there but sound on the PC: that's the cause of muted PC speakers, not "sound turned off".
        if (Bluetooth.HeadphonesIssue(bt, speakers, outputs) is { } headphones)
        {
            result.Issues.RemoveAll(i => i.Id == "speakersOff");
            result.Issues.Insert(0, headphones);
        }
        // Any Bluetooth microphone in use puts the headphones in call mode.
        var headsetMic = microphone is { Bluetooth: true } ? microphone : null;
        var (btIssues, btHealthy) = Bluetooth.Evaluate(bt, headsetMic, inputs.Any(i => i.IsBuiltIn));
        result.Issues.AddRange(btIssues);
        result.Healthy.AddRange(btHealthy);
        return result;
    }

    /// <summary>Turns what was found into issues and healthy items. Pure, unit-tested.</summary>
    public static (List<Issue> Issues, List<HealthItem> Healthy) Evaluate(
        Audio.Device? speakers, Audio.Device? microphone, IReadOnlyList<Audio.Device> outputs, IReadOnlyList<Audio.Device> inputs,
        IReadOnlyList<string> cameras, bool soundRunning, IReadOnlyList<Block> blocks, CallPicks picks)
    {
        var issues = new List<Issue>();
        var healthy = new List<HealthItem>();
        var soundSteps = new List<string>
        {
            "Click Restart sound above, and click Yes if Windows asks.",
            "Using a headset? Unplug it and plug it back in, or turn Bluetooth headphones off and on.",
            "Still nothing? Restart your PC.",
            Support.PcStillNotWorking,
        };

        // The speaker and microphone the user picked for calls, when plugged in, and whether Windows uses them.
        var pickedSpeaker = picks.SpeakerId is { } sid ? outputs.FirstOrDefault(o => o.Id == sid) : null;
        var pickedMicrophone = picks.MicrophoneId is { } mid ? inputs.FirstOrDefault(i => i.Id == mid) : null;
        var wrongSpeaker = pickedSpeaker is not null && pickedSpeaker.Id != speakers?.Id ? pickedSpeaker : null;
        var wrongMicrophone = pickedMicrophone is not null && pickedMicrophone.Id != microphone?.Id ? pickedMicrophone : null;

        if (!soundRunning)
            issues.Add(new Issue
            {
                Id = "soundSystem", Topic = Topic.Sound, Title = "Your PC's sound isn't working",
                Explanation = "The part of Windows that plays and records sound (Windows Audio) has stopped, so calls have no sound and no microphone.",
                Fixes = { Fix.RestartSound }, Steps = soundSteps, Technical = "The Audiosrv service is not running",
            });

        if (speakers is not null)
        {
            var percent = speakers.Volume is { } v ? (int)Math.Round(v * 100) : (int?)null;
            if (speakers.Muted == true || (speakers.Volume ?? 1) < 0.05)
                issues.Add(new Issue
                {
                    Id = "speakersOff", Topic = Topic.Sound, Title = "Your sound is turned off",
                    Explanation = "Your PC's sound is muted or all the way down, so you can't hear anyone.",
                    Fixes = { Fix.UnmuteSpeakers },
                    Steps = { "Click Turn sound on above.", "Still quiet? Check the volume in the call app too." },
                    Technical = $"{speakers.Name}: muted={speakers.Muted?.ToString() ?? "n/a"}, volume={(percent is null ? "n/a" : percent + "%")}",
                });
            else if (speakers.IsDisplay && pickedSpeaker is null)
            {
                var builtIn = outputs.FirstOrDefault(o => o.IsBuiltIn);
                var issue = new Issue
                {
                    Id = "speakersElsewhere", Severity = Severity.Warning, Topic = Topic.Sound, Title = "Your sound is going to your monitor",
                    Explanation = $"Sound plays through “{speakers.Name}”. If you can't hear it, it may have no speakers or be turned down.",
                    Steps = { builtIn is null ? "Click Open Sound settings." : $"Click Use {builtIn.Name} above.",
                              "Or click Open Sound settings, then pick your speakers or headset under Output." },
                    Technical = $"Default output: {speakers.Name}",
                };
                if (builtIn is not null) issue.Fixes.Add(Fix.UseSpeaker(builtIn.Id, builtIn.Name));
                issue.Fixes.Add(Fix.Open(Destination.SoundSettings));
                issues.Add(issue);
            }
            else healthy.Add(new HealthItem(Topic.Sound, speakers.Name + (percent is null ? "" : $", volume {percent}%")));
        }
        else if (soundRunning)
            issues.Add(new Issue
            {
                Id = "noSpeakers", Topic = Topic.Sound, Title = "Your PC can't find any speakers",
                Explanation = "There's nothing to play sound through, so you can't hear anyone.",
                Fixes = { Fix.RestartSound, Fix.Open(Destination.SoundSettings) }, Steps = soundSteps,
            });

        if (microphone is not null)
        {
            if (microphone.Muted == true || (microphone.Volume ?? 1) < 0.1)
                issues.Add(new Issue
                {
                    Id = "microphoneOff", Topic = Topic.Microphone, Title = "Your microphone is turned down",
                    Explanation = "Your microphone is muted or all the way down, so people in your calls can't hear you.",
                    Fixes = { Fix.TurnUpMicrophone },
                    Steps = { "Click Turn microphone up above.", "Check you aren't muted in the call app too." },
                    Technical = $"{microphone.Name}: muted={microphone.Muted?.ToString() ?? "n/a"}, volume={(microphone.Volume is { } mv ? $"{mv * 100:0}%" : "n/a")}",
                });
            else healthy.Add(new HealthItem(Topic.Microphone, microphone.Name));
        }
        else if (soundRunning)
            issues.Add(new Issue
            {
                Id = "noMicrophone", Topic = Topic.Microphone, Title = "Your PC can't find a microphone",
                Explanation = "There's no microphone to use, so people in your calls can't hear you.",
                Fixes = { Fix.RestartSound, Fix.Open(Destination.SoundSettings) },
                Steps = new List<string> { "Using a laptop with the lid closed? Open the lid, or plug in a headset.", "Using a headset? Unplug it and plug it back in." }
                    .Concat(soundSteps.Skip(2)).ToList(),
            });

        // Picks plugged in but not used: call apps set to Default follow Windows, so switch it back.
        if (soundRunning && (wrongSpeaker is not null || wrongMicrophone is not null))
        {
            var title = (wrongSpeaker, wrongMicrophone) switch
            {
                ({ } s, { } m) when s.DeviceName == m.DeviceName && s.DeviceName.Length > 0 => $"Your PC isn't using “{s.DeviceName}”",
                ({ }, { }) => "Your PC isn't using the speaker and microphone you picked",
                ({ } s, null) => $"Your sound isn't going to “{s.Name}”",
                (null, { } m) => $"Your PC isn't listening to “{m.Name}”",
                _ => "",
            };
            var explanation = string.Join(" ", new[]
            {
                wrongSpeaker is null ? null : $"You picked “{wrongSpeaker.Name}” for sound, but your PC plays through “{speakers?.Name ?? "nothing"}”.",
                wrongMicrophone is null ? null : $"You picked “{wrongMicrophone.Name}” as your microphone, but your PC listens to “{microphone?.Name ?? "nothing"}”.",
            }.Where(s => s is not null)) + " Call apps set to Default follow your PC, so you may not hear people, or they may not hear you.";
            var fix = Fix.UseCallPicks(wrongSpeaker?.Name, wrongMicrophone?.Name);
            issues.Add(new Issue
            {
                Id = "callPicks", Topic = Topic.CallAudio, Title = title, Explanation = explanation, Fixes = { fix },
                Steps = { $"Click {fix.Label} above.", "In the call app, set the speaker and microphone to Default (or “Same as System”), if it has it." },
                Technical = $"Picked output {picks.SpeakerName ?? "none"}, default {speakers?.Name ?? "none"}; picked input {picks.MicrophoneName ?? "none"}, default {microphone?.Name ?? "none"}",
            });
        }

        if (cameras.Count > 0)
            healthy.Add(new HealthItem(Topic.Camera, cameras[0] + (cameras.Count > 1 ? $" and {cameras.Count - 1} more" : "")));
        else
            issues.Add(new Issue
            {
                Id = "noCamera", Topic = Topic.Camera, Title = "Your PC can't find a camera",
                Explanation = "There's no camera to use, so people in your calls can't see you.",
                Steps = { "Using a laptop with the lid closed? Open the lid.", "Some laptops have a camera switch or a cover over the camera. Slide it open.",
                          "Using a USB camera? Unplug it and plug it back in.", "Still missing? Restart your PC.", Support.PcStillNotWorking },
            });

        foreach (var block in blocks) issues.Add(BlockIssue(block));
        if (blocks.Count == 0)
            healthy.Add(new HealthItem(Topic.CallPermissions, "Call apps are allowed to use your camera and microphone"));
        return (issues, healthy);
    }

    public static Issue BlockIssue(Block block)
    {
        var thing = block.Permission == Permission.Microphone ? "microphone" : "camera";
        var Thing = block.Permission == Permission.Microphone ? "Microphone" : "Camera";
        var who = block.Permission == Permission.Microphone ? "people in your calls can't hear you" : "people in your calls can't see you";
        var open = Fix.Open(block.Permission == Permission.Microphone ? Destination.MicrophonePrivacy : Destination.CameraPrivacy);
        var topic = block.Permission == Permission.Microphone ? Topic.Microphone : Topic.Camera;
        var id = $"blocked-{thing}-{block.Scope}-{block.App}";
        return block.Scope switch
        {
            BlockScope.Policy => new Issue
            {
                Id = id, Topic = topic, Title = $"A Windows policy blocks apps from your {thing}",
                Explanation = $"A Windows policy setting on this PC stops apps using your {thing}, so {who}.",
                Steps = { "Sign in with this PC's administrator account.",
                          "Click Start, type gpedit.msc and press Enter.",
                          "Go to Computer Configuration, then Administrative Templates, then Windows Components, then App Privacy.",
                          $"Open Let Windows apps access the {thing}, choose Not Configured, then click OK.",
                          "Restart your PC." },
                Technical = $"Group Policy: LetAppsAccess{(block.Permission == Permission.Microphone ? "Microphone" : "Camera")} = Force Deny",
            },
            BlockScope.Device or BlockScope.AllApps => new Issue
            {
                Id = id, Topic = topic, Title = $"Your PC blocks every app from your {thing}",
                Explanation = $"{Thing} access is turned off in Windows' privacy settings, so {who}.",
                Fixes = { open },
                Steps = { "Click Open Privacy settings.", $"Turn on {Thing} access.", $"Turn on Let apps access your {thing}.",
                          $"Turn on Let desktop apps access your {thing}.", "Close the call app and open it again.",
                          "Switch greyed out? Sign in with this PC's administrator account and try again." },
                Technical = block.Scope == BlockScope.Device ? $"HKLM ConsentStore\\{thing} = Deny" : $"HKCU ConsentStore\\{thing} = Deny",
            },
            BlockScope.DesktopApps => new Issue
            {
                Id = id, Topic = topic, Title = $"Call apps aren't allowed to use your {thing}",
                Explanation = $"Windows stops desktop apps like Zoom, Teams and Webex from using your {thing}, so {who}.",
                Fixes = { open },
                Steps = { "Click Open Privacy settings.", $"Scroll down and turn on Let desktop apps access your {thing}.",
                          "Close the call app and open it again.", "Switch greyed out? Sign in with this PC's administrator account and try again." },
                Technical = $"HKCU ConsentStore\\{thing}\\NonPackaged = Deny",
            },
            _ => new Issue
            {
                Id = id, Topic = topic, Title = $"{block.App} isn't allowed to use your {thing}",
                Explanation = $"Windows blocked {block.App} from your {thing}, so {who}.",
                Fixes = { open },
                Steps = { "Click Open Privacy settings.", $"Turn on {block.App} in the list.", $"Close {block.App} and open it again." },
                Technical = $"HKCU ConsentStore\\{thing}\\{block.App} = Deny",
            },
        };
    }
}

/// <summary>The speaker and microphone the user picked for calls (with Use this), kept in Netfix's settings folder.</summary>
public sealed class CallPicks
{
    public string? SpeakerId { get; set; }
    public string? SpeakerName { get; set; }
    public string? MicrophoneId { get; set; }
    public string? MicrophoneName { get; set; }

    private static string FilePath => Path.Combine(App.Preferences.Folder, "call-picks.json");

    public static CallPicks Load()
    {
        try { return JsonSerializer.Deserialize<CallPicks>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(App.Preferences.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception) { }
    }

    /// <summary>Makes the picks that are plugged in Windows' default again. Returns the names switched to.</summary>
    public List<string> Apply()
    {
        var switched = new List<string>();
        if (SpeakerId is { } s && Audio.Devices(false).Any(d => d.Id == s) && Audio.MakeDefault(s)) switched.Add(SpeakerName ?? "your speaker");
        if (MicrophoneId is { } m && Audio.Devices(true).Any(d => d.Id == m) && Audio.MakeDefault(m)) switched.Add(MicrophoneName ?? "your microphone");
        return switched;
    }
}
