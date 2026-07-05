using System.Runtime.InteropServices;

namespace Karu;

/// <summary>システムの空き物理メモリの取得。メモリ逼迫時の緊急休眠判定に使う。</summary>
static class SystemMemory
{
    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>空き物理メモリ(GB)。取得失敗時は「余裕あり」として扱う。</summary>
    public static double AvailableGB()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? m.ullAvailPhys / 1073741824.0 : double.MaxValue;
    }
}
