using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Taste.Interop;

// ── COM constants ─────────────────────────────────────────────────────────────

internal static class SVGIO
{
    public const uint ALLVIEW   = 0x00000002;
    public const uint SELECTION = 0x00000001;
}

internal static class SIGDN
{
    public const uint FILESYSPATH = 0x80058000;
}

// ── COM interfaces ────────────────────────────────────────────────────────────

[ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"),
 InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IShellWindows
{
    [DispId(4)]  int Count { get; }
    [DispId(0)]  [return: MarshalAs(UnmanagedType.IDispatch)] object Item(object index);
    [DispId(-4)] [return: MarshalAs(UnmanagedType.IUnknown)]  object _NewEnum();
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellServiceProvider
{
    void QueryService(ref Guid guidService, ref Guid riid,
                      [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
}

[ComImport, Guid("000214E2-0000-0000-C000-000000000046"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    void GetWindow(out IntPtr phwnd);
    void ContextSensitiveHelp(bool fEnterMode);
    void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
    void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
    void RemoveMenusSB(IntPtr hmenuShared);
    void SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string pszStatusText);
    void EnableModelessSB(bool fEnable);
    void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
    void BrowseObject(IntPtr pidl, uint wFlags);
    void GetViewStateStream(uint grfMode, out IStream ppStrm);
    void GetControlWindow(uint id, out IntPtr phwnd);
    void SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
    void QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object ppshv);
}

[ComImport, Guid("1AF3A467-214F-4298-908E-06B03E0B39F9"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderView2
{
    void GetCurrentViewMode(out uint pViewMode);
    void SetCurrentViewMode(uint ViewMode);
    void GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    void Item(int iItemIndex, out IntPtr ppidl);
    void ItemCount(uint uFlags, out int pcItems);
    void Items(uint uFlags, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    void GetSelectionMarkedItem(out int piItem);
    void GetFocusedItem(out int piItem);
    void GetItemPosition(IntPtr pidl, out SHELLPOINT ppt);
    void GetSpacing(out SHELLPOINT ppt);
    void GetDefaultSpacing(out SHELLPOINT ppt);
    void GetAutoArrange();
    void SelectItem(int iItem, uint dwFlags);
    void SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);
    void SetGroupBy(IntPtr key, bool fAscending);
    void GetGroupBy(out IntPtr pkey, out bool pfAscending);
    void SetViewProperty(IntPtr pidl, IntPtr propkey, ref object propvar);
    void GetViewProperty(IntPtr pidl, IntPtr propkey, out object propvar);
    void SetTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszPropList);
    void SetExtendedTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszPropList);
    void SetText(uint iType, [MarshalAs(UnmanagedType.LPWStr)] string pwszText);
    void SetCurrentFolderFlags(uint dwMask, uint dwFlags);
    void GetCurrentFolderFlags(out uint pdwFlags);
    void GetSortColumnCount(out int pcColumns);
    void SetSortColumns(IntPtr rgSortColumns, int cColumns);
    void GetSortColumns(out IntPtr rgSortColumns, int cColumns);
    void GetItem(int iItem, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    void GetVisibleItem(int iStart, bool fPrevious, out int piItem);
    void GetSelectedItem(int iStart, out int piItem);
    void GetSelection(bool fNoneImpliesFolder, out IShellItemArray ppsia);
    void GetSelectionState(IntPtr pidl, out uint pdwFlags);
    void InvokeVerbOnSelection([MarshalAs(UnmanagedType.LPWStr)] string pszVerb);
    void SetViewModeAndIconSize(uint uViewMode, int iImageSize);
    void GetViewModeAndIconSize(out uint puViewMode, out int piImageSize);
}

[ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemArray
{
    void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid,
                       [MarshalAs(UnmanagedType.IUnknown)] out object ppvOut);
    void GetPropertyStore(uint flags, ref Guid riid,
                          [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid,
                                    [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    void GetAttributes(uint AttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
    void GetCount(out uint pdwNumItems);
    void GetItemAt(uint dwIndex, out IShellItem ppsi);
    void EnumItems([MarshalAs(UnmanagedType.IUnknown)] out object ppenumShellItems);
}

[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid,
                       [MarshalAs(UnmanagedType.IUnknown)] out object ppvOut);
    void GetParent(out IShellItem ppsi);
    void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
    void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    void Compare(IShellItem psi, uint hint, out int piOrder);
}

[ComImport]
[Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig]
    int GetImage([In] SIZE size, [In] SIIGBF flags, out IntPtr phbm);
}

// ── Structs ───────────────────────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE { public int cx, cy; }

[StructLayout(LayoutKind.Sequential)]
internal struct SHELLPOINT { public int X, Y; }

[Flags]
internal enum SIIGBF : uint
{
    ResizeToFit   = 0x00,
    BiggerSizeOk  = 0x01,
    MemoryOnly    = 0x02,
    IconOnly      = 0x04,
    ThumbnailOnly = 0x08,
    InCacheOnly   = 0x10,
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SHFILEOPSTRUCT
{
    public IntPtr hwnd;
    public uint wFunc;
    public string pFrom;
    public string? pTo;
    public ushort fFlags;
    [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
    public IntPtr hNameMappings;
    public string? lpszProgressTitle;
}

// ── Public helpers ────────────────────────────────────────────────────────────

public static class ShellInterop
{
    private static readonly Guid IID_IShellItemImageFactory = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    // ── Thumbnail / icon loading ───────────────────────────────────

    public static BitmapSource? GetThumbnail(string path, int size)
    {
        try
        {
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, IID_IShellItemImageFactory, out object obj);
            if (hr != 0 || obj is not IShellItemImageFactory factory) return null;

            factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF.ResizeToFit, out IntPtr hbm);
            if (hbm == IntPtr.Zero) return null;

            try   { return HBitmapToBitmapSource(hbm); }
            finally { DeleteObject(hbm); }
        }
        catch { return null; }
    }

    public static BitmapSource? GetShellIcon(string path, int size)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, IID_IShellItemImageFactory, out object obj);
            if (hr != 0 || obj is not IShellItemImageFactory factory) return null;
            hr = factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF.IconOnly, out hbm);
            if (hr != 0 || hbm == IntPtr.Zero) return null;
            return HBitmapToBitmapSource(hbm);
        }
        catch { return null; }
        finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); }
    }

    public static BitmapSource? GetShellThumbnail(string path, int size)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, IID_IShellItemImageFactory, out object obj);
            if (hr != 0 || obj is not IShellItemImageFactory factory) return null;
            hr = factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF.InCacheOnly, out hbm);
            if (hr != 0 || hbm == IntPtr.Zero) return null;
            return HBitmapToBitmapSource(hbm);
        }
        catch { return null; }
        finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); }
    }

    // Shell returns PARGB32 HBITMAPs. CreateBitmapSourceFromHBitmap ignores the alpha channel
    // (uses WICBitmapIgnoreAlpha internally), making transparent areas black. Instead, we use
    // GDI+ LockBits to read the raw BGRA bytes — including alpha — then hand them to WPF as
    // Pbgra32 (premultiplied BGRA), which is the correct matching pixel format.
    private static BitmapSource HBitmapToBitmapSource(IntPtr hbm)
    {
        using var gdiBmp = System.Drawing.Image.FromHbitmap(hbm);
        int w = gdiBmp.Width, h = gdiBmp.Height;
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var data = gdiBmp.LockBits(rect,
            System.Drawing.Imaging.ImageLockMode.ReadOnly, gdiBmp.PixelFormat);
        try
        {
            int absStride = Math.Abs(data.Stride);
            byte[] pixels = new byte[h * absStride];

            if (data.Stride > 0)
            {
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            }
            else
            {
                // Bottom-up: Scan0 is the top-left in display order but at the HIGH end of
                // physical memory. Each successive display row is at Scan0 + row * Stride
                // (Stride is negative, so we walk backward through memory).
                for (int row = 0; row < h; row++)
                    Marshal.Copy(data.Scan0 + row * data.Stride,
                                 pixels, row * absStride, absStride);
            }

            var src = BitmapSource.Create(w, h, 96, 96,
                PixelFormats.Pbgra32, null, pixels, absStride);
            src.Freeze();
            return src;
        }
        finally { gdiBmp.UnlockBits(data); }
    }

    // ── File operations ───────────────────────────────────────────

    internal static int SHFileOperationInternal(ref SHFILEOPSTRUCT op) =>
        SHFileOperation(ref op);

    public static void SendToRecycleBin(IntPtr ownerHwnd, string path)
    {
        const ushort Flags = 0x0040 | 0x0010 | 0x0004; // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
        var op = new SHFILEOPSTRUCT
        {
            hwnd = ownerHwnd,
            wFunc = 0x0003, // FO_DELETE
            pFrom = path + '\0',
            fFlags = Flags,
        };
        SHFileOperation(ref op);
    }

    public static void OpenFile(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int    cbSize;
        public uint   fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int    nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint   dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO pExecInfo);

    public static void ShowProperties(IntPtr ownerHwnd, string path)
    {
        var info = new SHELLEXECUTEINFO
        {
            fMask  = 0x0000000C, // SEE_MASK_INVOKEIDLIST
            hwnd   = ownerHwnd,
            lpVerb = "properties",
            lpFile = path,
            nShow  = 1,
        };
        info.cbSize = Marshal.SizeOf(info);
        ShellExecuteEx(ref info);
    }

    // ── Window attributes ─────────────────────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void SetDarkTitleBar(IntPtr hwnd)
    {
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
    }

    // ── Recycle Bin helpers ───────────────────────────────────────

    public static (string IFile, string RFile)? FindRecycleBinEntry(string originalPath)
    {
        try
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            if (sid == null) return null;

            string root   = Path.GetPathRoot(originalPath) ?? @"C:\";
            string ext    = Path.GetExtension(originalPath);
            string binDir = Path.Combine(root, "$RECYCLE.BIN", sid);
            if (!Directory.Exists(binDir)) return null;

            foreach (var iFile in new DirectoryInfo(binDir)
                         .EnumerateFiles($"$I*{ext}")
                         .OrderByDescending(f => f.LastWriteTimeUtc)
                         .Select(f => f.FullName))
            {
                string? path = ReadRecycleBinPath(iFile);
                if (!string.Equals(path, originalPath, StringComparison.OrdinalIgnoreCase)) continue;

                string rFile = Path.Combine(Path.GetDirectoryName(iFile)!,
                                            "$R" + Path.GetFileName(iFile)[2..]);
                if (File.Exists(rFile) || Directory.Exists(rFile))
                    return (IFile: iFile, RFile: rFile);
            }
            return null;
        }
        catch { return null; }
    }

    private static string? ReadRecycleBinPath(string iFile)
    {
        try
        {
            byte[] data = File.ReadAllBytes(iFile);
            if (data.Length < 28) return null;
            long version = BitConverter.ToInt64(data, 0);
            if (version == 2)
            {
                int charCount = BitConverter.ToInt32(data, 24);
                int needed = 28 + charCount * 2;
                if (charCount <= 0 || data.Length < needed) return null;
                return Encoding.Unicode.GetString(data, 28, charCount * 2).TrimEnd('\0');
            }
            if (data.Length < 544) return null;
            return Encoding.Unicode.GetString(data, 24, 520).TrimEnd('\0');
        }
        catch { return null; }
    }
}
