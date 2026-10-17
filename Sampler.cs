using System.Diagnostics;
using System.Net.NetworkInformation;

namespace WinTweaker;

/// <summary>
/// Единый сборщик показателей раз в секунду: CPU, ОЗУ, сеть, пинг, топ процессов.
/// Им пользуются главное окно, трей и виджеты — система опрашивается один раз.
/// </summary>
static class Sampler
{
    public const int History = 60;

    public static double Cpu { get; private set; }
    public static double Ram { get; private set; }
    public static double RamUsedGb { get; private set; }
    public static double RamTotalGb { get; private set; }
    public static double DiskC { get; private set; }
    public static double DownBps { get; private set; }
    public static double UpBps { get; private set; }
    public static int? PingMs { get; private set; }
    public static List<(string Name, long Bytes, Image? Icon)> Top { get; private set; } = new();

    public static readonly Queue<double> CpuHist = new(), RamHist = new(), DownHist = new(), UpHist = new();
    public static event Action? Updated;

    static readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    static long lastRx = -1, lastTx = -1;
    static DateTime lastNet;
    static int tick;
    static bool topBusy, pingBusy;
    static readonly Dictionary<string, Image?> icons = new(StringComparer.OrdinalIgnoreCase);

    public static void Start()
    {
        if (timer.Enabled) return;
        timer.Tick += (_, _) => Sample();
        SysInfo.Cpu();
        Sample();
        timer.Start();
    }

    static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > History) q.Dequeue();
    }

    static void Sample()
    {
        tick++;
        Cpu = SysInfo.Cpu();
        var m = SysInfo.Memory();
        Ram = m.Load;
        RamUsedGb = m.UsedGb;
        RamTotalGb = m.TotalGb;
        if (tick % 10 == 1)
        {
            var (free, total) = SysInfo.SystemDrive();
            DiskC = total > 0 ? (total - free) / total * 100 : 0;
        }
        SampleNet();
        Push(CpuHist, Cpu);
        Push(RamHist, Ram);
        Push(DownHist, DownBps);
        Push(UpHist, UpBps);
        if (tick % 3 == 1) _ = SampleTopAsync();
        if (tick % 3 == 2) _ = PingAsync();
        Updated?.Invoke();
    }

    static void SampleNet()
    {
        try
        {
            long rx = 0, tx = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var s = ni.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
            var now = DateTime.UtcNow;
            if (lastRx >= 0)
            {
                double sec = Math.Max(0.2, (now - lastNet).TotalSeconds);
                DownBps = Math.Max(0, (rx - lastRx) / sec);
                UpBps = Math.Max(0, (tx - lastTx) / sec);
            }
            lastRx = rx;
            lastTx = tx;
            lastNet = now;
        }
        catch { }
    }

    static async Task PingAsync()
    {
        if (pingBusy) return;
        pingBusy = true;
        try
        {
            using var p = new Ping();
            var r = await p.SendPingAsync("1.1.1.1", 1500);
            PingMs = r.Status == IPStatus.Success ? (int)r.RoundtripTime : null;
        }
        catch { PingMs = null; }
        finally { pingBusy = false; }
    }

    static async Task SampleTopAsync()
    {
        if (topBusy) return;
        topBusy = true;
        try
        {
            var top = await Task.Run(() =>
            {
                var groups = new Dictionary<string, (long Bytes, string? Path)>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id is 0 or 4) continue;
                        string name = p.ProcessName;
                        long ws = p.WorkingSet64;
                        groups.TryGetValue(name, out var g);
                        string? path = g.Path;
                        if (path == null && !icons.ContainsKey(name))
                            try { path = p.MainModule?.FileName; } catch { }
                        groups[name] = (g.Bytes + ws, path);
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
                var list = groups.OrderByDescending(kv => kv.Value.Bytes).Take(5).ToList();
                var result = new List<(string, long, Image?)>();
                foreach (var (name, (bytes, path)) in list)
                {
                    if (!icons.TryGetValue(name, out var icon))
                    {
                        icon = null;
                        if (path != null)
                            try { using var ic = Icon.ExtractAssociatedIcon(path); icon = ic?.ToBitmap(); } catch { }
                        icons[name] = icon;
                    }
                    result.Add((name, bytes, icon));
                }
                return result;
            });
            Top = top;
        }
        catch { }
        finally { topBusy = false; }
    }

    public static string Speed(double bps)
    {
        double kb = bps / 1024;
        if (kb < 1) return "0 КБ/с";
        if (kb < 1024) return $"{kb:0} КБ/с";
        double mb = kb / 1024;
        return mb < 10 ? $"{mb:0.0} МБ/с" : $"{mb:0} МБ/с";
    }
}
