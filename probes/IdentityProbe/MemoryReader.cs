using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal enum MemoryFeature { ScreenDetection, SpellBarData, DataCollection }

internal sealed class MemoryReader : IDisposable
{
    internal const string LiveExecutablePath = @"C:\EverQuest-Live\eqgame.exe";
    private readonly SafeProcessHandle handle;
    internal readonly ulong Base;
    internal readonly long StartedUtcTicks;
    internal readonly string Path;
    internal readonly int ProcessId;
#if TEAMBAR_APPLICATION
    private readonly MemoryFeature feature;
#endif
    internal MemoryReader(int processId, MemoryFeature feature = MemoryFeature.ScreenDetection) : this(processId, LiveExecutablePath, feature) { }
    internal MemoryReader(int processId, string expectedExecutablePath, MemoryFeature feature = MemoryFeature.ScreenDetection)
    {
#if TEAMBAR_APPLICATION
        this.feature = feature;
        using var permission = TeamBar.GameDataAccess.EnterRead(feature);
#endif
        string expectedPath = System.IO.Path.GetFullPath(expectedExecutablePath);
        ProcessId = processId;
        // Query and read only: never request write, allocation, or remote-thread rights.
        handle = OpenProcess(0x0400 | 0x0010, false, processId);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            using var process = Process.GetProcessById(processId);
            var module = process.MainModule ?? throw new InvalidDataException("Client executable is unavailable.");
            Path = module.FileName; Base = (ulong)module.BaseAddress.ToInt64();
            StartedUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            if (!MatchesExecutablePath(Path, expectedPath))
                throw new InvalidDataException("The process is not the expected installed EQ client.");
        }
        catch { handle.Dispose(); throw; }
    }
    internal static bool MatchesExecutablePath(string actual, string expected) => string.Equals(
        System.IO.Path.GetFullPath(actual), System.IO.Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
    internal byte[]? TryRead(ulong address, int count)
    {
#if TEAMBAR_APPLICATION
        using var permission = TeamBar.GameDataAccess.EnterRead(feature);
#endif
        if (!Pointer(address) || count <= 0 || count > 32 * 1024 * 1024) return null;
        byte[] data = new byte[count];
        return ReadProcessMemory(handle, (nint)address, data, (nuint)count, out var actual) && actual == (nuint)count ? data : null;
    }
    internal byte[] Read(ulong address, int count) => TryRead(address, count) ?? throw new InvalidDataException("Client memory is unavailable or changing.");
    internal ulong U64(ulong address) => BitConverter.ToUInt64(Read(address, 8));
    internal int I32(ulong address) => BitConverter.ToInt32(Read(address, 4));
    internal static bool Pointer(ulong value) => value >= 0x10000 && value < 0x0000800000000000;
    public void Dispose() => handle.Dispose();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, [Out] byte[] buffer, nuint size, out nuint read);
}
