using System.Runtime.InteropServices;

namespace TeamBar;

internal sealed partial class GameViewPlacements
{
    private static void RestoreSystemFrameRendering(nint window)
    {
        // Recovery for a journal written by a version that hid the native frame.
        SetFramePolicy(window, useWindowStyle);
    }

    private static int? RecoverableFramePolicy(nint window)
    {
        if (ReadFrameAttribute(window, nonClientRenderingPolicy, out int policy, sizeof(int)) == 0 &&
            policy is >= 0 and <= 2) return policy;
        // On Windows versions where the explicit policy is write-only, a normally
        // themed window can be returned to the policy selected by its native style.
        const int nonClientRenderingEnabled = 1;
        return ReadFrameAttribute(window, nonClientRenderingEnabled, out int enabled, sizeof(int)) == 0 && enabled != 0
            ? useWindowStyle : null;
    }

    private static void SetFramePolicy(nint window, int policy)
    {
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(window, nonClientRenderingPolicy, ref policy, sizeof(int)));
        GameViewWindows.FlushComposition();
    }

    private const int nonClientRenderingPolicy = 2;
    private const int useWindowStyle = 0, disableFrameRendering = 1;
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int ReadFrameAttribute(nint window, int attribute, out int value, int bytes);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int bytes);
}
