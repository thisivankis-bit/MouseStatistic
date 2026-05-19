using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MouseClickTracker;

public class KeyPressedEventArgs : EventArgs
{
    public bool IsRepeat   { get; init; }
    public bool IsInjected { get; init; }
}

public class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int WM_KEYUP       = 0x0101;
    private const int WM_SYSKEYUP    = 0x0105;

    private const uint LLKHF_INJECTED          = 0x10;
    private const uint LLKHF_LOWER_IL_INJECTED = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private IntPtr _hookHandle = IntPtr.Zero;
    private readonly LowLevelKeyboardProc _proc;

    // WH_KEYBOARD_LL doesn't expose the WM_KEYDOWN auto-repeat bit, so we track held vkCodes ourselves.
    private readonly HashSet<uint> _heldKeys = new();
    private readonly object _heldLock = new();

    public event EventHandler<KeyPressedEventArgs>? Pressed;
    public event EventHandler? Activity;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    public void Start()
    {
        using var process = Process.GetCurrentProcess();
        using var module  = process.MainModule!;
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName!), 0);
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
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                var data     = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var injected = (data.flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) != 0;
                bool isRepeat;
                lock (_heldLock)
                {
                    isRepeat = !_heldKeys.Add(data.vkCode);
                }
                Pressed?.Invoke(this, new KeyPressedEventArgs { IsRepeat = isRepeat, IsInjected = injected });
                Activity?.Invoke(this, EventArgs.Empty);
            }
            else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                lock (_heldLock)
                {
                    _heldKeys.Remove(data.vkCode);
                }
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
