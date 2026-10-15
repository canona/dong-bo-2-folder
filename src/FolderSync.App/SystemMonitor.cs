using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace FolderSync.App;

/// <summary>1 lan do: % tai giong cot CPU / Memory / Disk / Network cua Task Manager (null = khong do duoc).</summary>
public sealed record SystemStats(
    double? Cpu, double? Memory, double? Disk, double? Network,
    ulong MemUsedBytes, ulong MemTotalBytes,
    string? DiskName, string? NetName, double NetBitsPerSec, long NetLinkBitsPerSec);

/// <summary>
/// Do tai may cho thanh trang thai. Chi dung API co san cua Windows (khong them goi NuGet):
/// - CPU: GetSystemTimes (chenh lech giua 2 lan do).
/// - Memory: GlobalMemoryStatusEx.dwMemoryLoad.
/// - Disk: PDH "\PhysicalDisk(*)\% Idle Time" -> 100 - idle cua o BAN NHAT (Task Manager cung lay o cao nhat).
///   Dung PdhAddEnglishCounter: ten counter bi dich theo ngon ngu Windows, ten tieng Anh thi may nao cung chay.
/// - Network: byte nhan + gui / giay so voi toc do lien ket cua card BAN NHAT.
/// Goi Sample() tren thread nen (liet ke card mang co the mat vai chuc ms); khong an toan da luong.
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    private ulong _lastIdle, _lastKernel, _lastUser;
    private bool _hasCpu;
    private IntPtr _pdhQuery, _pdhDiskIdle;
    private readonly Dictionary<string, long> _lastNetBytes = new();
    private DateTime _lastNetTime;

    public SystemMonitor()
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _pdhQuery) == 0
                && PdhAddEnglishCounter(_pdhQuery, @"\PhysicalDisk(*)\% Idle Time", IntPtr.Zero, out _pdhDiskIdle) == 0)
                PdhCollectQueryData(_pdhQuery); // counter ti le can 2 lan thu -> lan dau chi lam moc
            else
                ClosePdh();
        }
        catch (Exception) { ClosePdh(); } // thieu pdh.dll / bi chan -> cot Disk hien "--"
    }

    public SystemStats Sample()
    {
        var cpu = SampleCpu();
        var (mem, used, total) = SampleMemory();
        var (disk, diskName) = SampleDisk();
        var (net, netName, bps, link) = SampleNetwork();
        return new SystemStats(cpu, mem, disk, net, used, total, diskName, netName, bps, link);
    }

    private double? SampleCpu()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return null;
        ulong idle = ToUlong(idleFt), kernel = ToUlong(kernelFt), user = ToUlong(userFt);
        double? result = null;
        if (_hasCpu)
        {
            // kernel time da GOM ca idle
            double totalDelta = (kernel - _lastKernel) + (user - _lastUser);
            if (totalDelta > 0) result = Math.Clamp(100.0 * (1 - (idle - _lastIdle) / totalDelta), 0, 100);
        }
        (_lastIdle, _lastKernel, _lastUser, _hasCpu) = (idle, kernel, user, true);
        return result;
    }

    private static (double?, ulong, ulong) SampleMemory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (null, 0, 0);
        return (m.dwMemoryLoad, m.ullTotalPhys - m.ullAvailPhys, m.ullTotalPhys);
    }

    private (double?, string?) SampleDisk()
    {
        if (_pdhDiskIdle == IntPtr.Zero || PdhCollectQueryData(_pdhQuery) != 0) return (null, null);
        uint size = 0;
        int rc = PdhGetFormattedCounterArray(_pdhDiskIdle, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);
        if (rc != PDH_MORE_DATA || size == 0) return (null, null);
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            rc = PdhGetFormattedCounterArray(_pdhDiskIdle, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out var count, buffer);
            if (rc != 0) return (null, null);
            double? best = null; string? bestName = null;
            int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                var name = Marshal.PtrToStringUni(item.szName) ?? "";
                if (item.CStatus != 0 || name == "_Total") continue; // _Total la trung binh -> che mat o dang ban
                double active = Math.Clamp(100 - item.doubleValue, 0, 100);
                if (best is null || active > best) { best = active; bestName = name; }
            }
            return (best, bestName);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private (double?, string?, double, long) SampleNetwork()
    {
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return (null, null, 0, 0); }
        var now = DateTime.UtcNow;
        double seconds = (now - _lastNetTime).TotalSeconds;
        bool first = _lastNetTime == default;
        _lastNetTime = now;

        double? best = null; string? bestName = null; double bestBps = 0; long bestLink = 0;
        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.Speed <= 0
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            long bytes;
            try { var s = nic.GetIPStatistics(); bytes = s.BytesReceived + s.BytesSent; }
            catch (NetworkInformationException) { continue; }
            bool had = _lastNetBytes.TryGetValue(nic.Id, out var prev);
            _lastNetBytes[nic.Id] = bytes;
            if (first || !had || seconds <= 0 || bytes < prev) continue;
            double bps = (bytes - prev) * 8 / seconds;
            double pct = Math.Clamp(100 * bps / nic.Speed, 0, 100);
            if (best is null || pct > best) { best = pct; bestName = nic.Name; bestBps = bps; bestLink = nic.Speed; }
        }
        // Co card dang ket noi nhung chua du 2 lan do -> 0% thay vi "--" nhay nhay luc khoi dong
        return (best ?? (first ? null : 0), bestName, bestBps, bestLink);
    }

    private void ClosePdh()
    {
        if (_pdhQuery != IntPtr.Zero) PdhCloseQuery(_pdhQuery);
        _pdhQuery = _pdhDiskIdle = IntPtr.Zero;
    }

    public void Dispose() => ClosePdh();

    private static ulong ToUlong(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    // ---------- Win32 ----------

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    // PDH_FMT_COUNTERVALUE_ITEM_W: con tro ten + PDH_FMT_COUNTERVALUE { DWORD CStatus; union { double ... } }
    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr szName;
        public uint CStatus;
        private uint _pad; // union 8 byte can le 8
        public double doubleValue;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize,
        out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
}
