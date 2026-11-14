using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ETaskManager.Native;

internal static class NativeMethods
{
    // ---- ntdll: NtQuerySystemInformation ------------------------------------------------------

    public const int SystemProcessInformation = 5;
    public const int SystemProcessorPerformanceInformation = 8;

    public const int STATUS_SUCCESS = 0;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    public const int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    /// <summary>
    /// SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION. All times are in 100 ns units.
    /// NOTE: KernelTime INCLUDES IdleTime (and DPC / interrupt time).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    {
        public long IdleTime;
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    /// <summary>
    /// Field offsets inside SYSTEM_PROCESS_INFORMATION for 64-bit Windows.
    /// The structure is parsed manually because it is variable length (threads follow it).
    /// </summary>
    public static class Spi
    {
        public const int NextEntryOffset = 0x00;        // ULONG
        public const int NumberOfThreads = 0x04;        // ULONG
        public const int WorkingSetPrivateSize = 0x08;  // LARGE_INTEGER
        public const int CreateTime = 0x20;             // LARGE_INTEGER
        public const int UserTime = 0x28;               // LARGE_INTEGER
        public const int KernelTime = 0x30;             // LARGE_INTEGER
        public const int ImageNameLength = 0x38;        // USHORT (bytes)
        public const int ImageNameBuffer = 0x40;        // PWSTR
        public const int UniqueProcessId = 0x50;        // HANDLE
        public const int InheritedFromUniqueProcessId = 0x58; // HANDLE
        public const int HandleCount = 0x60;            // ULONG
        public const int SessionId = 0x64;              // ULONG
        public const int WorkingSetSize = 0x90;         // SIZE_T
        public const int PrivatePageCount = 0xC8;       // SIZE_T (bytes, = commit size)
    }

    // ---- kernel32: memory ---------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
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
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    public struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pPerformanceInformation, uint cb);

    // ---- kernel32: processes ------------------------------------------------------------------

    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint SYNCHRONIZE = 0x00100000;

    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_PARAMETER = 87;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessTimes(SafeProcessHandle hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsProcessCritical(SafeProcessHandle hProcess, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);
}
