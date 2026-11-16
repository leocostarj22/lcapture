using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace LCapture.Interop;

/// <summary>A window or a whole monitor that Windows.Graphics.Capture can record.</summary>
public sealed record CaptureTarget(string Title, IntPtr Window, IntPtr Monitor, int ProcessId)
{
    public bool IsMonitor => Monitor != IntPtr.Zero;
    public override string ToString() => Title;
}

/// <summary>
/// Native glue for window capture: the Direct3D device it needs, capture items made from an HWND
/// or HMONITOR (so we know the window's process, for its sound), and the list of capturable windows.
/// </summary>
internal static unsafe class GraphicsCaptureInterop
{
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    // Window capture needs Windows 10 1903; the item interop and the device helpers are older.
    public static bool IsSupported =>
        Environment.OSVersion.Version.Build >= 18362 && GraphicsCaptureSession.IsSupported();

    // ---- Direct3D device ----

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static IDirect3DDevice CreateDevice()
    {
        const int hardware = 1, warp = 5;
        const uint bgraSupport = 0x20, sdkVersion = 7;

        int hr = D3D11CreateDevice(IntPtr.Zero, hardware, IntPtr.Zero, bgraSupport, IntPtr.Zero, 0, sdkVersion,
                                   out var device, out _, out var context);
        if (hr < 0)
            hr = D3D11CreateDevice(IntPtr.Zero, warp, IntPtr.Zero, bgraSupport, IntPtr.Zero, 0, sdkVersion,
                                   out device, out _, out context);
        Marshal.ThrowExceptionForHR(hr);

        try
        {
            var iid = IidDxgiDevice;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in iid, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgi);
            }
        }
        finally
        {
            if (context != IntPtr.Zero) Marshal.Release(context);
            Marshal.Release(device);
        }
    }

    // ---- Capture items ----

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    public static GraphicsCaptureItem CreateItem(CaptureTarget target)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var hstring));
        IntPtr factory;
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, IidGraphicsCaptureItemInterop, out factory));
        }
        finally
        {
            WindowsDeleteString(hstring);
        }

        try
        {
            // IGraphicsCaptureItemInterop: IUnknown (3 slots), CreateForWindow, CreateForMonitor.
            var vtable = *(IntPtr**)factory;
            var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)
                vtable[target.IsMonitor ? 4 : 3];
            var iid = IidGraphicsCaptureItem;
            IntPtr item;
            Marshal.ThrowExceptionForHR(create(factory, target.IsMonitor ? target.Monitor : target.Window, &iid, &item));
            try
            {
                return GraphicsCaptureItem.FromAbi(item);
            }
            finally
            {
                Marshal.Release(item);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    // ---- Window and monitor list ----

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public int Left, Top, Right, Bottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    /// <summary>Windows a user would recognise in Alt+Tab, then one entry per monitor.</summary>
    public static IReadOnlyList<CaptureTarget> GetTargets(IntPtr ownWindow)
    {
        const uint gwOwner = 4;
        const int gwlExStyle = -20, wsExToolWindow = 0x80, dwmCloaked = 14;
        int ownProcess = Environment.ProcessId;

        var windows = new List<CaptureTarget>();
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == ownWindow || !IsWindowVisible(hwnd) || GetWindow(hwnd, gwOwner) != IntPtr.Zero) return true;
            if ((GetWindowLong(hwnd, gwlExStyle) & wsExToolWindow) != 0) return true;
            if (DwmGetWindowAttribute(hwnd, dwmCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            int length = GetWindowTextLength(hwnd);
            if (length == 0) return true;
            var title = new StringBuilder(length + 1);
            GetWindowText(hwnd, title, title.Capacity);

            GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == ownProcess) return true;

            string app = "";
            try { app = Process.GetProcessById(pid).ProcessName; } catch { /* exited or protected */ }
            var label = app.Length > 0 ? $"{Shorten(title.ToString())} — {app}" : Shorten(title.ToString());
            windows.Add(new CaptureTarget(label, hwnd, IntPtr.Zero, pid));
            return true;
        }, IntPtr.Zero);

        var monitors = new List<CaptureTarget>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                bool primary = (info.Flags & 1) != 0;
                var label = $"Ecrã inteiro {monitors.Count + 1} ({info.Right - info.Left}×{info.Bottom - info.Top})" +
                            (primary ? " · principal" : "");
                monitors.Add(new CaptureTarget(label, IntPtr.Zero, monitor, 0));
            }
            return true;
        }, IntPtr.Zero);

        return [.. windows.OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase), .. monitors];
    }

    private static string Shorten(string title) => title.Length > 60 ? title[..57] + "…" : title;
}
