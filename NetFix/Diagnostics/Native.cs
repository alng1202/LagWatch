using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Netfix.Diagnostics;

/// <summary>The few Windows APIs .NET doesn't wrap. None of them need administrator rights.</summary>
internal static class Native
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIP, uint srcIP, byte[] macAddr, ref int physicalAddrLen);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    private static uint ToUInt(IPAddress a) => BitConverter.ToUInt32(a.GetAddressBytes(), 0);

    /// <summary>
    /// Asks the device at <paramref name="ip"/> for its hardware address on the local network (ARP).
    /// Null when nothing answers. Takes up to ~3 s when nothing is there.
    /// </summary>
    public static string? ArpLookup(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return null;
        var mac = new byte[6];
        var len = mac.Length;
        if (SendARP(ToUInt(ip), 0, mac, ref len) != 0 || len == 0) return null;
        return BitConverter.ToString(mac, 0, len);
    }

    /// <summary>The interface index Windows would use to reach <paramref name="ip"/>. Reads the routing table only (works offline).</summary>
    public static int? BestInterface(IPAddress ip) =>
        GetBestInterface(ToUInt(ip), out var index) == 0 ? (int)index : null;

    /// <summary>Free and total physical memory, in bytes.</summary>
    public static (ulong Free, ulong Total)? Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? (m.ullAvailPhys, m.ullTotalPhys) : null;
    }

    public sealed record PowerStatus(bool HasBattery, bool OnBattery, int? Percent, bool BatterySaver);

    public static PowerStatus? Power()
    {
        if (!GetSystemPowerStatus(out var s)) return null;
        var hasBattery = s.BatteryFlag != 128 && s.BatteryFlag != 255;
        return new PowerStatus(hasBattery, s.ACLineStatus == 0,
                               s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null,
                               (s.SystemStatusFlag & 1) == 1);
    }
}
