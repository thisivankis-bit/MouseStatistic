using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MouseClickTracker;

public class MouseClickedEventArgs : EventArgs
{
    public bool IsInjected { get; init; }
}

public class MouseHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE   = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;

    private const uint LLMHF_INJECTED            = 0x01;
    private const uint LLMHF_LOWER_IL_INJECTED   = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int  ptX;
        public int  ptY;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private IntPtr _hookHandle = IntPtr.Zero;
    private readonly LowLevelMouseProc _proc;

    public event EventHandler<MouseClickedEventArgs>? Clicked;
    public event EventHandler? Activity;

    public MouseHook()
    {
        _proc = HookCallback;
    }

    public void Start()
    {
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(module.ModuleName!), 0);
    }

    public void Stop()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
            {
                var data     = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var injected = (data.flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) != 0;
                Clicked?.Invoke(this, new MouseClickedEventArgs { IsInjected = injected });
            }
            if (msg == WM_MOUSEMOVE || msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
                Activity?.Invoke(this, EventArgs.Empty);
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
