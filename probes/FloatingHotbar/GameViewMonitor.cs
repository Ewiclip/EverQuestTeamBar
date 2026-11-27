using System.Runtime.InteropServices;
using Microsoft.Win32;

internal record GameViewMonitor(string Device, Rectangle Bounds, Rectangle WorkArea, bool Primary = false, string? MonitorId = null)
{
    internal static GameViewMonitor[] Connected() => Screen.AllScreens.Select(screen => new GameViewMonitor(
        screen.DeviceName, screen.Bounds, screen.WorkingArea, screen.Primary, IdentityFor(screen.DeviceName).Id)).ToArray();

    internal static GameViewMonitor? DefaultSwapScreen(IEnumerable<GameViewMonitor?> configured)
    {
        var connected = configured.OfType<GameViewMonitor>().ToArray();
        return connected.FirstOrDefault(monitor => monitor.Primary) ?? (connected.Length == 1 ? connected[0] : null);
    }

    internal static GameViewMonitor? Find(GameViewScreen saved, IReadOnlyList<GameViewMonitor> connected)
    {
        GameViewMonitor? match = null;
        foreach (var device in connected)
        {
            bool same = saved.MonitorId is { } id
                ? StringComparer.OrdinalIgnoreCase.Equals(device.MonitorId, id)
                : StringComparer.OrdinalIgnoreCase.Equals(device.Device, saved.Monitor);
            if (!same) continue;
            if (match is not null) return null;
            match = device;
        }
        return match;
    }

    internal static (string? Id, string? Description) IdentityFor(string device) => DisplayIdentities.Get(device);

    // Windows can reuse a display number. Refresh the device interfaces when
    // the topology changes, without querying the driver on each overlay tick.
    private static class DisplayIdentities
    {
        private static readonly Dictionary<string, (string? Id, string? Description)> ByDevice = new(StringComparer.OrdinalIgnoreCase);

        static DisplayIdentities() => SystemEvents.DisplaySettingsChanged += (_, _) => { lock (ByDevice) ByDevice.Clear(); };

        internal static (string? Id, string? Description) Get(string device)
        {
            lock (ByDevice)
            {
                if (ByDevice.TryGetValue(device, out var identity)) return identity;
                var entry = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>(), Name = "", Label = "", Id = "", Key = "" };
                for (uint i = 0; EnumDisplayDevicesW(device, i, ref entry, 1); i++)
                {
                    if ((entry.Flags & 1) == 0 || string.IsNullOrWhiteSpace(entry.Id)) continue;
                    identity = (entry.Id, entry.Label);
                    break;
                }
                ByDevice[device] = identity;
                return identity;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DisplayDevice
    {
        internal int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Label;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Key;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string device, uint index, ref DisplayDevice display, uint flags);
}
