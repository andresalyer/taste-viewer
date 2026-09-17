using System.Runtime.InteropServices;
using System.Windows.Threading;
using Taste.Interop;

namespace Taste.Preview;

sealed class ExplorerWatcher : IDisposable
{
    public event Action<string, string, List<string>>? FileSelected;

    public bool ExplorerSpacebarEnabled { get; set; } = true;

    private HookProc  _hookProc = null!;
    private IntPtr    _hook     = IntPtr.Zero;
    private Dispatcher _dispatcher = null!;

    public void Start()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _hookProc   = HookCallback;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hookProc, Native.GetModuleHandle(null), 0);
    }

    public void Stop() => Dispose();

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == new IntPtr(Native.WM_KEYDOWN))
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            bool injected = (kb.flags & Native.LLKHF_INJECTED) != 0;
            if (!injected && kb.vkCode == Native.VK_SPACE)
            {
                // Let Taste handle its own spacebar via the normal key handler
                if (IsTasteForeground())
                    return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

                if (ExplorerSpacebarEnabled && IsExplorerForeground())
                {
                    var hwnd = Native.GetForegroundWindow();

                    // Explorer's inline rename box is a plain "Edit" control. If it has focus,
                    // the user is typing a filename, not asking for a preview — let space through.
                    if (IsExplorerRenaming(hwnd))
                        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

                    // COM calls forbidden inside hook callback (RPC_E_CANTCALLOUT_ININPUTSYNCCALL).
                    // Suppress now and handle after the callback returns.
                    _dispatcher.BeginInvoke(() => HandleSpacebar(hwnd));
                    return new IntPtr(1);
                }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    void HandleSpacebar(IntPtr explorerHwnd)
    {
        try
        {
            var (selectedPath, folderPath, allFiles) = GetExplorerState(explorerHwnd);

            if (selectedPath is null)
            {
                InjectSpacebar();
                return;
            }

            FileSelected?.Invoke(selectedPath, folderPath ?? "", allFiles);
        }
        catch
        {
            InjectSpacebar();
        }
    }

    static void InjectSpacebar()
    {
        var inputs = new INPUT[]
        {
            new() { type = Native.INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = (ushort)Native.VK_SPACE } },
            new() { type = Native.INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = (ushort)Native.VK_SPACE, dwFlags = Native.KEYEVENTF_KEYUP } },
        };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    static bool IsExplorerForeground()
    {
        var hwnd = Native.GetForegroundWindow();
        var sb   = new System.Text.StringBuilder(256);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString() is "CabinetWClass" or "ExploreWClass";
    }

    // GetGUIThreadInfo works cross-process without AttachThreadInput, unlike GetFocus.
    static bool IsExplorerRenaming(IntPtr explorerHwnd)
    {
        uint threadId = Native.GetWindowThreadProcessId(explorerHwnd, out _);
        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (!Native.GetGUIThreadInfo(threadId, ref info) || info.hwndFocus == IntPtr.Zero)
            return false;

        var sb = new System.Text.StringBuilder(256);
        Native.GetClassName(info.hwndFocus, sb, sb.Capacity);
        return sb.ToString() == "Edit";
    }

    // Check by process ID — reliable across any WPF window class name
    static bool IsTasteForeground()
    {
        var hwnd = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    static (string? selected, string? folder, List<string> allFiles) GetExplorerState(IntPtr targetHwnd)
    {
        var swType = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))
            ?? throw new InvalidOperationException();
        dynamic shellWindows = Activator.CreateInstance(swType)!;

        IShellBrowser? browser = null;
        int count = (int)shellWindows.Count;
        for (int i = 0; i < count; i++)
        {
            dynamic item = shellWindows.Item(i);
            IntPtr itemHwnd = IntPtr.Zero;
            try { itemHwnd = new IntPtr((long)item.HWND); } catch { continue; }
            if (itemHwnd != targetHwnd) continue;

            var sp   = (IShellServiceProvider)(object)item;
            var guid = typeof(IShellBrowser).GUID;
            sp.QueryService(ref guid, ref guid, out var sbObj);
            browser = (IShellBrowser)sbObj;
            break;
        }

        if (browser is null) return (null, null, new List<string>());

        browser.QueryActiveShellView(out var shvObj);
        var fv2 = (IFolderView2)shvObj;

        fv2.GetSelection(false, out var selArray);
        string? selectedPath = null;
        if (selArray is not null)
        {
            selArray.GetCount(out uint selCount);
            if (selCount > 0)
            {
                selArray.GetItemAt(0, out var selItem);
                selItem.GetDisplayName(SIGDN.FILESYSPATH, out selectedPath);
            }
        }

        var siGuid = typeof(IShellItem).GUID;
        fv2.GetFolder(ref siGuid, out var folderObj);
        ((IShellItem)folderObj).GetDisplayName(SIGDN.FILESYSPATH, out string? folderPath);

        var siaGuid = typeof(IShellItemArray).GUID;
        fv2.Items(SVGIO.ALLVIEW, ref siaGuid, out var allObj);
        var allArray = (IShellItemArray)allObj;
        allArray.GetCount(out uint total);

        var allFiles = new List<string>((int)total);
        for (uint j = 0; j < total; j++)
        {
            allArray.GetItemAt(j, out var fi);
            fi.GetDisplayName(SIGDN.FILESYSPATH, out var fp);
            if (fp is not null) allFiles.Add(fp);
        }

        return (selectedPath, folderPath, allFiles);
    }
}
