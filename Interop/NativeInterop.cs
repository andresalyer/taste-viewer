using System.Runtime.InteropServices;
using System.Text;

namespace Taste.Interop;

delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

[StructLayout(LayoutKind.Sequential)]
struct KBDLLHOOKSTRUCT
{
    public uint vkCode, scanCode, flags, time;
    public UIntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
struct INPUT
{
    public uint type;
    public KEYBDINPUT ki;
    private ulong _pad1, _pad2; // pad to match union size on 64-bit
}

[StructLayout(LayoutKind.Sequential)]
struct KEYBDINPUT
{
    public ushort wVk;
    public ushort wScan;
    public uint   dwFlags;
    public uint   time;
    public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
struct ICONINFO
{
    [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
    public int    xHotspot;
    public int    yHotspot;
    public IntPtr hbmMask;
    public IntPtr hbmColor;
}

[StructLayout(LayoutKind.Sequential)]
struct RECT
{
    public int Left, Top, Right, Bottom;
}

[StructLayout(LayoutKind.Sequential)]
struct GUITHREADINFO
{
    public uint   cbSize;
    public uint   flags;
    public IntPtr hwndActive;
    public IntPtr hwndFocus;
    public IntPtr hwndCapture;
    public IntPtr hwndMenuOwner;
    public IntPtr hwndMoveSize;
    public IntPtr hwndCaret;
    public RECT   rcCaret;
}

internal static class Native
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN     = 0x0100;
    public const int VK_SPACE       = 0x20;

    public const uint LLKHF_INJECTED   = 0x00000010;
    public const uint INPUT_KEYBOARD   = 1;
    public const uint KEYEVENTF_KEYUP  = 0x0002;

    public const uint   FO_DELETE          = 0x0003;
    public const ushort FOF_ALLOWUNDO      = 0x0040;
    public const ushort FOF_NOCONFIRMATION = 0x0010;
    public const ushort FOF_SILENT         = 0x0004;
    public const ushort FOF_NOERRORUI      = 0x0400;

    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder buf, int max);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("gdi32.dll")]  public static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO pIconInfo);
    [DllImport("user32.dll")] public static extern IntPtr CreateIconIndirect(ref ICONINFO pIconInfo);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyIcon(IntPtr hIcon);
}
