using System.Runtime.InteropServices;

internal static class TestProcessPolicy
{
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    internal static void Configure(){
        // Process-local only, inherited by crash-fixture children; never change Windows-wide error reporting.
        if(OperatingSystem.IsWindows())SetErrorMode(0x0001|0x0002|0x8000);
    }
}
