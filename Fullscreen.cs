using System.Runtime.InteropServices;

namespace WinTweaker;

/// <summary>Полноэкранное приложение на переднем плане (игра, видео, презентация).</summary>
static class FullscreenApp
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);

    /// <summary>PID процесса полноэкранного окна на переднем плане или 0.</summary>
    public static int ForegroundPid()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return 0;
            var cls = new System.Text.StringBuilder(64);
            GetClassName(h, cls, 64);
            if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow") return 0;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == Environment.ProcessId) return 0;
            if (!GetWindowRect(h, out var r)) return 0;
            var mon = Screen.FromHandle(h).Bounds;
            return r.L <= mon.Left && r.T <= mon.Top && r.R >= mon.Right && r.B >= mon.Bottom ? (int)pid : 0;
        }
        catch { return 0; }
    }

    public static bool Active() => ForegroundPid() != 0;
}
