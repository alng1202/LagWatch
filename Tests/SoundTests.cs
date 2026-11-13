using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Netfix.Diagnostics;
using Netfix.Model;
using Xunit;
using Xunit.Abstractions;
using static Netfix.Diagnostics.Audio;

namespace Netfix.Tests;

public class SoundTests
{
    private static Device Speakers(bool? muted = false, double volume = 0.6) =>
        new("{0.0.0}.{spk}", "Speakers (Realtek(R) Audio)", "Realtek(R) Audio", false, FormFactor.Speakers, false, muted, volume);
    private static Device Mic(double volume = 0.8) =>
        new("{0.0.1}.{mic}", "Microphone Array (Realtek(R) Audio)", "Realtek(R) Audio", true, FormFactor.Microphone, false, false, volume);
    private static Device Headphones(bool bt = true) =>
        new("{0.0.0}.{bt}", "Headphones (WH-1000XM4)", "WH-1000XM4", false, FormFactor.Headphones, bt, false, 0.5);
    private static Device Monitor() =>
        new("{0.0.0}.{hdmi}", "DELL U2720Q (Intel(R) Display Audio)", "Intel(R) Display Audio", false, FormFactor.DigitalAudioDisplayDevice, false, false, 0.5);

    private static (List<Issue> Issues, List<HealthItem> Healthy) Eval(Device? spk, Device? mic, IReadOnlyList<Device>? outputs = null,
        IReadOnlyList<string>? cameras = null, bool running = true, IReadOnlyList<Calls.Block>? blocks = null, CallPicks? picks = null) =>
        Calls.Evaluate(spk, mic, outputs ?? (spk is null ? new List<Device>() : new() { spk }), mic is null ? new List<Device>() : new() { mic },
                       cameras ?? new[] { "Integrated Camera" }, running, blocks ?? new List<Calls.Block>(), picks ?? new CallPicks());

    [Fact]
    public void AllGood()
    {
        var (issues, healthy) = Eval(Speakers(), Mic());
        Assert.Empty(issues);
        Assert.Contains(healthy, h => h.Topic == Topic.Sound && h.Detail.Contains("volume 60%"));
        Assert.Contains(healthy, h => h.Topic == Topic.Camera);
        Assert.Contains(healthy, h => h.Topic == Topic.CallPermissions);
    }

    [Fact]
    public void MutedOrSilentSpeakersAndMic()
    {
        Assert.Equal(Fix.UnmuteSpeakers, Eval(Speakers(muted: true), Mic()).Issues.Single().Fixes[0]);
        Assert.Equal("speakersOff", Eval(Speakers(volume: 0.02), Mic()).Issues.Single().Id);
        Assert.Equal(Fix.TurnUpMicrophone, Eval(Speakers(), Mic(volume: 0.05)).Issues.Single().Fixes[0]);
    }

    [Fact]
    public void SoundServiceStoppedNeedsRestart()
    {
        var issue = Eval(null, null, running: false).Issues.Single(i => i.Id == "soundSystem");
        Assert.Equal(Fix.RestartSound, issue.Fixes[0]);
        Assert.True(Fix.RestartSound.NeedsAdmin);
        // No "can't find speakers" on top of a stopped sound service.
        Assert.DoesNotContain(Eval(null, null, running: false).Issues, i => i.Id == "noSpeakers");
    }

    [Fact]
    public void MonitorSpeakersOfferThePCs()
    {
        var spk = Speakers();
        var issue = Eval(Monitor(), Mic(), outputs: new[] { Monitor(), spk }).Issues.Single();
        Assert.Equal("speakersElsewhere", issue.Id);
        Assert.Equal(Fix.UseSpeaker(spk.Id, spk.Name), issue.Fixes[0]);
    }

    [Fact]
    public void NoCamera() => Assert.Equal("noCamera", Eval(Speakers(), Mic(), cameras: Array.Empty<string>()).Issues.Single().Id);

    [Fact]
    public void PrivacyBlocks()
    {
        var desktop = Eval(Speakers(), Mic(), blocks: new[] { new Calls.Block(Calls.Permission.Microphone, Calls.BlockScope.DesktopApps) }).Issues.Single();
        Assert.Equal("Call apps aren't allowed to use your microphone", desktop.Title);
        Assert.Equal(Fix.Open(Destination.MicrophonePrivacy), desktop.Fixes[0]);
        var teams = Calls.BlockIssue(new Calls.Block(Calls.Permission.Camera, Calls.BlockScope.App, "Microsoft Teams"));
        Assert.Equal("Microsoft Teams isn't allowed to use your camera", teams.Title);
        var policy = Calls.BlockIssue(new Calls.Block(Calls.Permission.Microphone, Calls.BlockScope.Policy));
        Assert.Empty(policy.Fixes);
        Assert.Contains(policy.Steps, s => s.Contains("gpedit.msc"));
    }

    [Fact]
    public void PicksPluggedInButNotUsed()
    {
        var hp = Headphones(bt: false);
        var picks = new CallPicks { SpeakerId = hp.Id, SpeakerName = hp.Name };
        var issue = Eval(Speakers(), Mic(), outputs: new[] { Speakers(), hp }, picks: picks).Issues.Single(i => i.Id == "callPicks");
        Assert.Equal($"Switch to {hp.Name}", issue.Fixes[0].Label);
        // A pick that isn't plugged in is no problem.
        Assert.Empty(Eval(Speakers(), Mic(), picks: picks).Issues);
    }

    [Fact]
    public void HeadphonesConnectedButSoundOnThePC()
    {
        var hp = Headphones();
        var issue = Bluetooth.HeadphonesIssue(new Bluetooth.State(true, new()), Speakers(), new[] { Speakers(), hp })!;
        Assert.Equal("Your sound is playing from your PC, not WH-1000XM4", issue.Title);
        Assert.Equal(Fix.UseSpeaker(hp.Id, "WH-1000XM4"), issue.Fixes[0]);
        Assert.Null(Bluetooth.HeadphonesIssue(null, hp, new[] { Speakers(), hp }));
    }

    [Fact]
    public void PairedHeadphonesOnlyMatterWhenThePCIsSilent()
    {
        var state = new Bluetooth.State(true, new() { new("id", "AirPods", false, true, null) });
        Assert.Null(Bluetooth.HeadphonesIssue(state, Speakers(), new[] { Speakers() }));
        var issue = Bluetooth.HeadphonesIssue(state, Speakers(muted: true), new[] { Speakers(muted: true) })!;
        Assert.Equal("AirPods aren't connected", issue.Title);
        Assert.Equal(Fix.Open(Destination.BluetoothSettings), issue.Fixes[0]);
    }

    [Fact]
    public void BluetoothVerdicts()
    {
        var off = Bluetooth.Evaluate(new Bluetooth.State(false, new() { new("id", "MX Master 3", false, false, null) }), null, true);
        Assert.Equal(Fix.TurnBluetoothOn, off.Issues.Single().Fixes[0]);
        Assert.Empty(Bluetooth.Evaluate(new Bluetooth.State(false, new()), null, true).Issues);

        var low = Bluetooth.Evaluate(new Bluetooth.State(true, new() { new("id", "MX Master 3", true, false, 9) }), null, true);
        Assert.Equal("MX Master 3 is almost out of battery", low.Issues.Single().Title);

        var callMode = Bluetooth.Evaluate(new Bluetooth.State(true, new()), Headphones() with { Input = true }, pcHasMicrophone: true);
        Assert.Equal(Fix.UsePCMicrophone, callMode.Issues.Single().Fixes[0]);
        Assert.Empty(Bluetooth.Evaluate(new Bluetooth.State(true, new()), Headphones() with { Input = true }, pcHasMicrophone: false).Issues);

        var fine = Bluetooth.Evaluate(new Bluetooth.State(true, new() { new("id", "WH-1000XM4", true, true, 80) }), null, true);
        Assert.Equal("On · WH-1000XM4 (80%)", fine.Healthy.Single().Detail);
    }

    [Fact]
    public void DeviceKinds()
    {
        Assert.True(Speakers().IsBuiltIn);
        Assert.False(Headphones(bt: false).IsBuiltIn);
        Assert.False(Monitor().IsBuiltIn);
        Assert.True(Monitor().IsDisplay);
    }

    [Fact]
    public void ChimeFitsTheBufferAndStaysQuiet()
    {
        var samples = Chime(48_000, 20_000);
        Assert.Equal(20_000, samples.Length);
        Assert.All(samples, s => Assert.InRange(s, -0.26f, 0.26f));
        Assert.InRange(Math.Abs(samples[0]), 0, 0.001); // fades in, no click
    }

    [Fact]
    public void QuietRoomIsNotHeard() => Assert.True(0.0098 < HeardLevel); // a silent room's peak, measured on a real laptop

    [Fact]
    public void SoundScopeOnlyInFullAndSound()
    {
        Assert.True(CheckScope.Full.ChecksSound());
        Assert.True(CheckScope.Sound.ChecksSound());
        Assert.False(CheckScope.Network.ChecksSound());
        Assert.False(CheckScope.Sound.RunsSpeedTest());
    }
}

/// <summary>Reads this PC's real devices (nothing is changed). Run with NETFIX_LIVE=1.</summary>
public class LiveSoundTests
{
    private readonly ITestOutputHelper output;
    public LiveSoundTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task LiveSound()
    {
        if (Environment.GetEnvironmentVariable("NETFIX_LIVE") != "1") return;
        foreach (var d in Devices(false).Concat(Devices(true)))
            output.WriteLine($"{(d.Input ? "in " : "out")} {d.Name} | {d.DeviceName} | {d.Form} | bt={d.Bluetooth} builtIn={d.IsBuiltIn} muted={d.Muted} vol={d.Volume:0.00}");
        output.WriteLine($"default out: {Default(false)?.Name}; default in: {Default(true)?.Name}");
        output.WriteLine("cameras: " + string.Join(", ", await Calls.Cameras()));
        output.WriteLine("blocks: " + string.Join(", ", Calls.Blocks()));
        output.WriteLine("audio service running: " + await Calls.SoundServiceRunning());
        var bt = await Bluetooth.Read();
        output.WriteLine(bt is null ? "no Bluetooth" : $"bluetooth on={bt.On}: " + string.Join(", ", bt.Devices.Select(d => $"{d.Name} conn={d.Connected} hp={d.IsHeadphones} bat={d.Battery}")));
        var (issues, healthy) = await Calls.Check();
        foreach (var i in issues) output.WriteLine($"ISSUE {i.Title}");
        foreach (var h in healthy) output.WriteLine($"ok {h.Topic}: {h.Detail}");
        Assert.NotNull(Default(false));
    }

    /// <summary>Plays the chime on the default speaker and listens to the default microphone for 5 s. Run with NETFIX_LIVE_AUDIO=1.</summary>
    [Fact]
    public async Task LiveAudioTests()
    {
        if (Environment.GetEnvironmentVariable("NETFIX_LIVE_AUDIO") != "1") return;
        var speaker = Default(false)!;
        var started = DateTime.Now;
        var played = await PlayTestSound(speaker.Id);
        output.WriteLine($"chime on {speaker.Name}: played={played} in {(DateTime.Now - started).TotalSeconds:0.0} s (muted={speaker.Muted}, volume={speaker.Volume:0.00})");
        Assert.True(played);

        var mic = Default(true)!;
        var levels = new List<double>();
        var loudest = await ListenToMicrophone(mic.Id, 5, l => { lock (levels) levels.Add(l); });
        output.WriteLine($"mic {mic.Name}: loudest={loudest:0.0000} over {levels.Count} readings; heard={(loudest >= HeardLevel)}");
        output.WriteLine("levels: " + string.Join(" ", levels.Select(l => l.ToString("0.000"))));
        Assert.NotNull(loudest);
    }
}
