using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Netfix.Diagnostics;

/// <summary>
/// Speakers and microphones through Windows' Core Audio (the same API the Sound settings use): the devices, their
/// volume and mute, which one is the default, switching it, playing a test sound on one speaker and reading one
/// microphone's level. No administrator needed.
/// </summary>
public static class Audio
{
    public enum FormFactor
    {
        RemoteNetwork = 0, Speakers = 1, LineLevel = 2, Headphones = 3, Microphone = 4, Headset = 5, Handset = 6,
        UnknownDigitalPassthrough = 7, SPDIF = 8, DigitalAudioDisplayDevice = 9, Unknown = 10,
    }

    /// <summary>One speaker or microphone (a Core Audio endpoint).</summary>
    /// <param name="Name">As Sound settings shows it: "Speakers (Realtek(R) Audio)".</param>
    /// <param name="DeviceName">The hardware's own name: "Realtek(R) Audio", "WH-1000XM4".</param>
    public sealed record Device(string Id, string Name, string DeviceName, bool Input, FormFactor Form, bool Bluetooth,
                                bool? Muted, double? Volume)
    {
        /// <summary>Sound through a monitor or TV (HDMI / DisplayPort).</summary>
        public bool IsDisplay => Form == FormFactor.DigitalAudioDisplayDevice;
        public bool IsHeadphones => Form is FormFactor.Headphones or FormFactor.Headset || Audio.HeadphoneName(Name) || Audio.HeadphoneName(DeviceName);
        /// <summary>The PC's own speakers or microphone (not Bluetooth, a monitor, or headphones).</summary>
        public bool IsBuiltIn => !Bluetooth && !IsDisplay && !IsHeadphones && !DeviceName.Contains("USB", StringComparison.OrdinalIgnoreCase)
                                 && Form is FormFactor.Speakers or FormFactor.Microphone or FormFactor.Unknown or FormFactor.LineLevel;
    }

    public static bool HeadphoneName(string name) =>
        new[] { "headphone", "headset", "airpods", "earbuds", "buds", "hands-free" }.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));

    // MARK: Reading devices

    public static List<Device> Devices(bool input)
    {
        var list = new List<Device>();
        try
        {
            var enumerator = Enumerator();
            Check(enumerator.EnumAudioEndpoints(input ? EDataFlow.Capture : EDataFlow.Render, DEVICE_STATE_ACTIVE, out var collection));
            Check(collection.GetCount(out var count));
            for (uint i = 0; i < count; i++)
            {
                Check(collection.Item(i, out var device));
                if (Read(device, input) is { } d) list.Add(d);
                Marshal.ReleaseComObject(device);
            }
            Marshal.ReleaseComObject(collection);
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { }
        return list;
    }

    /// <summary>The default device (Windows' "Default Device", the console role; calls may use "Default Communication Device").</summary>
    public static Device? Default(bool input, bool communications = false)
    {
        try
        {
            var hr = Enumerator().GetDefaultAudioEndpoint(input ? EDataFlow.Capture : EDataFlow.Render,
                                                          communications ? ERole.Communications : ERole.Console, out var device);
            if (hr != 0 || device is null) return null;
            var d = Read(device, input);
            Marshal.ReleaseComObject(device);
            return d;
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { return null; }
    }

    private static Device? Read(IMMDevice device, bool input)
    {
        if (device.GetId(out var id) != 0) return null;
        string name = "", deviceName = "", enumerator = "";
        var form = FormFactor.Unknown;
        if (device.OpenPropertyStore(STGM_READ, out var store) == 0)
        {
            name = ReadString(store, PKEY_Device_FriendlyName) ?? "";
            deviceName = ReadString(store, PKEY_DeviceInterface_FriendlyName) ?? "";
            enumerator = ReadString(store, PKEY_Device_EnumeratorName) ?? "";
            if (ReadUInt(store, PKEY_AudioEndpoint_FormFactor) is { } f) form = (FormFactor)f;
            Marshal.ReleaseComObject(store);
        }
        bool? muted = null;
        double? volume = null;
        if (EndpointVolume(device) is { } ev)
        {
            if (ev.GetMute(out var m) == 0) muted = m;
            if (ev.GetMasterVolumeLevelScalar(out var v) == 0) volume = v;
            Marshal.ReleaseComObject(ev);
        }
        var bluetooth = enumerator.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) || id.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase);
        return new Device(id, name.Length > 0 ? name : deviceName, deviceName, input, form, bluetooth, muted, volume);
    }

    // MARK: Changing devices

    /// <summary>Unmutes a device and turns it up to at least <paramref name="level"/>.</summary>
    public static bool TurnUp(string id, double level)
    {
        return WithDevice(id, device =>
        {
            if (EndpointVolume(device) is not { } ev) return false;
            var ok = ev.SetMute(false, Guid.Empty) == 0;
            if (ev.GetMasterVolumeLevelScalar(out var v) == 0 && v < level) ok &= ev.SetMasterVolumeLevelScalar((float)level, Guid.Empty) == 0;
            Marshal.ReleaseComObject(ev);
            return ok;
        });
    }

    /// <summary>Makes a device Windows' default for everything: apps, and calls (all three roles), like "Set as default" in Sound settings.</summary>
    public static bool MakeDefault(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            var ok = true;
            foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
                ok &= policy.SetDefaultEndpoint(id, role) == 0;
            Marshal.ReleaseComObject(policy);
            return ok;
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { return false; }
    }

    // MARK: Testing devices

    /// <summary>Plays a short two-note chime on one speaker, whatever the default is. False when it couldn't play.</summary>
    public static Task<bool> PlayTestSound(string id) => Task.Run(() => WithDevice(id, device =>
    {
        var clientIid = typeof(IAudioClient).GUID;
        if (device.Activate(ref clientIid, CLSCTX_ALL, IntPtr.Zero, out var o) != 0) return false;
        var client = (IAudioClient)o;
        IntPtr format = IntPtr.Zero;
        try
        {
            if (client.GetMixFormat(out format) != 0) return false;
            var tag = (ushort)Marshal.ReadInt16(format, 0);
            var channels = (ushort)Marshal.ReadInt16(format, 2);
            var rate = Marshal.ReadInt32(format, 4);
            var bits = (ushort)Marshal.ReadInt16(format, 14);
            var isFloat = tag == 3 || (tag == 0xFFFE && Marshal.ReadInt32(format, 24) == 3);
            if (!(isFloat && bits == 32) && bits != 16) return false;
            if (client.Initialize(0, 0, 12_000_000, 0, format, IntPtr.Zero) != 0) return false; // 1.2 s buffer
            if (client.GetBufferSize(out var frames) != 0) return false;
            var renderIid = typeof(IAudioRenderClient).GUID;
            if (client.GetService(ref renderIid, out var r) != 0) return false;
            var render = (IAudioRenderClient)r;
            var samples = Chime(rate, (int)frames);
            if (render.GetBuffer((uint)samples.Length, out var data) != 0) return false;
            for (int f = 0; f < samples.Length; f++)
                for (int c = 0; c < channels; c++)
                {
                    var index = f * channels + c;
                    if (isFloat) Marshal.WriteInt32(data, index * 4, BitConverter.SingleToInt32Bits(samples[f]));
                    else Marshal.WriteInt16(data, index * 2, (short)(samples[f] * short.MaxValue));
                }
            render.ReleaseBuffer((uint)samples.Length, 0);
            client.Start();
            Thread.Sleep((int)(samples.Length * 1000L / rate) + 150);
            client.Stop();
            Marshal.ReleaseComObject(render);
            return true;
        }
        finally
        {
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            Marshal.ReleaseComObject(client);
        }
    }));

    /// <summary>Two soft notes (E5 then A5), faded in and out so they don't click: up to one second.</summary>
    public static float[] Chime(int rate, int maxFrames)
    {
        var length = Math.Min(rate, maxFrames);
        var samples = new float[length];
        for (int i = 0; i < length; i++)
        {
            var t = (double)i / rate;
            var freq = i < length / 2 ? 659.25 : 880.0;
            var local = i < length / 2 ? i : i - length / 2;
            var half = length / 2;
            var envelope = Math.Min(1, Math.Min(local / (rate * 0.02), (half - local) / (rate * 0.15)));
            samples[i] = (float)(0.25 * Math.Max(0, envelope) * Math.Sin(2 * Math.PI * freq * t));
        }
        return samples;
    }

    /// <summary>
    /// Listens to one microphone for <paramref name="seconds"/> and reports its level (0–1) about ten times a second.
    /// Returns the loudest level heard, or null when the microphone couldn't be opened (for example, blocked in Privacy settings).
    /// </summary>
    public static Task<double?> ListenToMicrophone(string id, double seconds, Action<double> level, CancellationToken cancel = default) => Task.Run(() =>
    {
        double? loudest = null;
        WithDevice(id, device =>
        {
            var clientIid = typeof(IAudioClient).GUID;
            var meterIid = typeof(IAudioMeterInformation).GUID;
            if (device.Activate(ref clientIid, CLSCTX_ALL, IntPtr.Zero, out var o) != 0) return false;
            var client = (IAudioClient)o;
            IntPtr format = IntPtr.Zero;
            try
            {
                // An open recording stream makes the level meter report what the microphone hears.
                if (client.GetMixFormat(out format) != 0 || client.Initialize(0, 0, 10_000_000, 0, format, IntPtr.Zero) != 0) return false;
                if (device.Activate(ref meterIid, CLSCTX_ALL, IntPtr.Zero, out var m) != 0) return false;
                var meter = (IAudioMeterInformation)m;
                var captureIid = typeof(IAudioCaptureClient).GUID;
                client.GetService(ref captureIid, out var c);
                var capture = c as IAudioCaptureClient;
                client.Start();
                loudest = 0;
                var end = DateTime.Now.AddSeconds(seconds);
                while (DateTime.Now < end && !cancel.IsCancellationRequested)
                {
                    Thread.Sleep(100);
                    // Empties the buffer so it never overflows; the samples themselves aren't kept.
                    while (capture is not null && capture.GetNextPacketSize(out var packet) == 0 && packet > 0)
                    {
                        if (capture.GetBuffer(out _, out var n, out _, out _, out _) != 0) break;
                        capture.ReleaseBuffer(n);
                    }
                    if (meter.GetPeakValue(out var peak) == 0)
                    {
                        loudest = Math.Max(loudest ?? 0, peak);
                        level(peak);
                    }
                }
                client.Stop();
                if (capture is not null) Marshal.ReleaseComObject(capture);
                Marshal.ReleaseComObject(meter);
                return true;
            }
            finally
            {
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                Marshal.ReleaseComObject(client);
            }
        });
        return loudest;
    }, cancel);

    /// <summary>
    /// A peak the microphone reaches when someone speaks normally near it (about −30 dB). The meter reads peaks, not an
    /// average like the Mac's −42 dB: a quiet room alone peaked near 0.01 on a laptop's microphone array.
    /// </summary>
    public const double HeardLevel = 0.03;

    // MARK: Plumbing

    private static bool WithDevice(string id, Func<IMMDevice, bool> body)
    {
        try
        {
            if (Enumerator().GetDevice(id, out var device) != 0 || device is null) return false;
            try { return body(device); }
            finally { Marshal.ReleaseComObject(device); }
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { return false; }
    }

    private static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)new MMDeviceEnumerator();

    private static IAudioEndpointVolume? EndpointVolume(IMMDevice device)
    {
        var iid = typeof(IAudioEndpointVolume).GUID;
        return device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var o) == 0 ? o as IAudioEndpointVolume : null;
    }

    private static void Check(int hr) { if (hr != 0) Marshal.ThrowExceptionForHR(hr); }

    private static string? ReadString(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out var pv) != 0) return null;
        try { return pv.vt == 31 && pv.pointer != IntPtr.Zero ? Marshal.PtrToStringUni(pv.pointer) : null; }
        finally { PropVariantClear(ref pv); }
    }

    private static uint? ReadUInt(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out var pv) != 0) return null;
        try { return pv.vt == 19 ? pv.uintValue : null; }
        finally { PropVariantClear(ref pv); }
    }

    private const uint DEVICE_STATE_ACTIVE = 1;
    private const int STGM_READ = 0;
    private const int CLSCTX_ALL = 23;

    private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly PROPERTYKEY PKEY_Device_EnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
    private static readonly PROPERTYKEY PKEY_DeviceInterface_FriendlyName = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);
    private static readonly PROPERTYKEY PKEY_AudioEndpoint_FormFactor = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    private enum EDataFlow { Render = 0, Capture = 1 }
    private enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
        public PROPERTYKEY(Guid f, int p) { fmtid = f; pid = p; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
        [FieldOffset(8)] public uint uintValue;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDB, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDB, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong qpc);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    /// <summary>The interface Sound settings uses to set the default device (not in the SDK headers, but stable since Windows 7).</summary>
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}
