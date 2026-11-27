using System.IO.Pipes;
namespace TeamBar;

internal static class PipeServerConnection
{
    internal static bool Reset(NamedPipeServerStream server)
    {
        // IsConnected is false for Broken as well as Disconnected. Broken still
        // requires Disconnect; otherwise WaitForConnection throws synchronously
        // forever and a server loop can monopolize its UI synchronization context.
        try { server.Disconnect(); return true; }
        catch (ObjectDisposedException) { return false; }
        catch (InvalidOperationException) { return true; } // Already disconnected, or never connected.
        catch (IOException)
        {
            // An unrecoverable native disconnect must not leave a spinning loop.
            server.Dispose();
            return false;
        }
    }
}
