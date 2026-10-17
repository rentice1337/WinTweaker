using System.Diagnostics;
using System.Net.NetworkInformation;

namespace WinTweaker;

/// <summary>Неизменяемый снимок показателей — его можно спокойно читать из любого потока.</summary>
sealed record Snap(
    double Cpu, double Ram, double RamUsedGb, double RamTotalGb, double DiskC,
    double DownBps, double UpBps, int? PingMs,
    IReadOnlyList<(string Name, long Bytes, Image? Icon)> Top,
    double[] CpuHist, double[] RamHist, double[] DownHist, double[] UpHist)
{
    public static readonly Snap Empty = new(0, 0, 0, 0, 0, 0, 0, null,
        Array.Empty<(string, long, Image?)>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>());
}

/// <summary>
/// Единый сборщик показателей раз в секунду: CPU, ОЗУ, сеть, пинг, топ процессов.
/// Опрос идёт в фоновом потоке (перечисление сетевых адаптеров и процессов бывает медленным),
/// в поток интерфейса приходит только готовый снимок — главное окно не подтормаживает.
/// </summary>
static class Sampler
{
    public const int History = 60;

    /// <summary>Последний снимок. Ссылка меняется атомарно — читайте её один раз за кадр.</summary>
    public static Snap Current { get; private set; } = Snap.Empty;

    public static double Cpu => Current.Cpu;
    public static double Ram => Current.Ram;
    public static double RamUsedGb => Current.RamUsedGb;
    public static double RamTotalGb => Current.RamTotalGb;
    public static double DiskC => Current.DiskC;
    public static double DownBps => Current.DownBps;
    public static double UpBps => Current.UpBps;
    public static int? PingMs => Current.PingMs;
    public static IReadOnlyList<(string Name, long Bytes, Image? Icon)> Top => Current.Top;
    public static double[] CpuHist => Current.CpuHist;
    public static double[] RamHist => Current.RamHist;
    public static double[] DownHist => Current.DownHist;
    public static double[] UpHist => Current.UpHist;

    /// <summary>Вызывается в потоке интерфейса после каждого снимка.</summary>
    public static event Action? Updated;

    static System.Threading.Timer? timer;
    static SynchronizationContext? ui;
    static readonly Queue<double> cpuQ = new(), ramQ = new(), downQ = new(), upQ = new();
    static long lastRx = -1, lastTx = -1;
    static DateTime lastNet;
    static int tick, busy, topBusy, pingBusy;
    static double disk;
    static volatile int pingMs = -1;
    static volatile IReadOnlyList<(string Name, long Bytes, Image? Icon)> top = Array.Empty<(string, long, Image?)>();
    static readonly Dictionary<string, Image?> icons = new(StringComparer.OrdinalIgnoreCase);

    public static void Start()
    {
        if (timer != null) return;
        ui = SynchronizationContext.Current;
        SysInfo.Cpu();
        timer = new System.Threading.Timer(_ => Sample(), null, 0, 1000);
    }

    static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > History) q.Dequeue();
    }

    static void Sample()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return; // прошлый опрос ещё идёт — пропускаем
        try
        {
            tick++;
            double cpu = SysInfo.Cpu();
            var m = SysInfo.Memory();
            if (tick % 10 == 1)
            {
                var (free, total) = SysInfo.SystemDrive();
                disk = total > 0 ? (total - free) / total * 100 : 0;
            }
            var (down, up) = SampleNet();
            Push(cpuQ, cpu);
            Push(ramQ, m.Load);
            Push(downQ, down);
            Push(upQ, up);
            if (tick % 3 == 1) SampleTop();
            if (tick % 3 == 2) Ping();

            int p = pingMs;
            var snap = new Snap(cpu, m.Load, m.UsedGb, m.TotalGb, disk, down, up, p >= 0 ? p : null, top,
                cpuQ.ToArray(), ramQ.ToArray(), downQ.ToArray(), upQ.ToArray());
            if (ui != null) ui.Post(_ => Publish(snap), null);
            else Publish(snap);
        }
        catch { }
        finally { Volatile.Write(ref busy, 0); }
    }

    static void Publish(Snap s)
    {
        Current = s;
        try { Updated?.Invoke(); } catch { }
    }

    static (double Down, double Up) SampleNet()
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
            double down = 0, up = 0;
            if (lastRx >= 0)
            {
                double sec = Math.Max(0.2, (now - lastNet).TotalSeconds);
                down = Math.Max(0, (rx - lastRx) / sec);
                up = Math.Max(0, (tx - lastTx) / sec);
            }
            lastRx = rx;
            lastTx = tx;
            lastNet = now;
            return (down, up);
        }
        catch { return (0, 0); }
    }

    static void Ping()
    {
        if (Interlocked.Exchange(ref pingBusy, 1) == 1) return;
        Task.Run(async () =>
        {
            try
            {
                using var p = new Ping();
                var r = await p.SendPingAsync("1.1.1.1", 1500);
                pingMs = r.Status == IPStatus.Success ? (int)r.RoundtripTime : -1;
            }
            catch { pingMs = -1; }
            finally { Volatile.Write(ref pingBusy, 0); }
        });
    }

    static void SampleTop()
    {
        if (Interlocked.Exchange(ref topBusy, 1) == 1) return;
        Task.Run(() =>
        {
            try
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
                var result = new List<(string, long, Image?)>();
                foreach (var (name, (bytes, path)) in groups.OrderByDescending(kv => kv.Value.Bytes).Take(5))
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
                top = result;
            }
            catch { }
            finally { Volatile.Write(ref topBusy, 0); }
        });
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
