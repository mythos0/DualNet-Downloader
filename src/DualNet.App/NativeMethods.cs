using System.Runtime.InteropServices;
using System.Text;

namespace DualNet.App;

/// <summary>Win32 interop: single-instance URL forwarding + system theme detection.</summary>
internal static class NativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    private const uint WM_COPYDATA = 0x004A;

    public static void SendSecondInstanceUrl(string url)
    {
        try
        {
            var hwnd = FindWindow(null, "DualNet Downloader");
            if (hwnd == IntPtr.Zero) return;
            var bytes = Encoding.UTF8.GetBytes(url ?? "");
            var ptr = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                var cds = new COPYDATASTRUCT { dwData = IntPtr.Zero, cbData = bytes.Length, lpData = ptr };
                SendMessage(hwnd, WM_COPYDATA, IntPtr.Zero, ref cds);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch { }
    }

    public static void ReceiveCopyData(IntPtr lParam)
    {
        try
        {
            var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
            if (cds.cbData <= 0) return;
            var bytes = new byte[cds.cbData];
            Marshal.Copy(cds.lpData, bytes, 0, bytes.Length);
            var url = Encoding.UTF8.GetString(bytes);
            if (!string.IsNullOrWhiteSpace(url))
            {
                var window = App.Current.MainWindow2;
                window?.Dispatcher.BeginInvoke(() => window.OpenUrlInNewTab(url));
            }
        }
        catch { }
    }
}
