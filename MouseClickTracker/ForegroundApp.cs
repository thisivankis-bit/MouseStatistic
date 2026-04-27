using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MouseClickTracker;

public static class ForegroundApp
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static (string ProcessName, string Title) Get()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return ("", "");

            GetWindowThreadProcessId(hwnd, out uint pid);
            using var process = Process.GetProcessById((int)pid);

            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);

            return (process.ProcessName, sb.ToString());
        }
        catch
        {
            return ("", "");
        }
    }
}
