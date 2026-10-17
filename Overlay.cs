using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace WinTweaker;

/// <summary>Настройки игрового оверлея.</summary>
static class OverlaySettings
{
    public static readonly (string Key, string Title)[] Metrics =
    {
        ("fps", "FPS"), ("ft", "Время кадра и график"), ("low", "1% low"), ("cpu", "Загрузка CPU"), ("gpu", "Загрузка GPU"),
        ("temp", "Температура GPU"), ("ping", "Пинг и потери"), ("ram", "ОЗУ"), ("clock", "Часы"), ("session", "Время в игре"),
    };

    static int Int(string key, int def, int min, int max) =>
        int.TryParse(AppSettings.Get(key, def.ToString()), out int v) ? Math.Clamp(v, min, max) : def;

    /// <summary>0 — строка, 1 — панель с графиком.</summary>
    public static int Style { get => Int("ov.style", 1, 0, 1); set => AppSettings.Set("ov.style", value.ToString()); }
    /// <summary>0 — слева сверху, 1 — справа сверху, 2 — слева снизу, 3 — справа снизу.</summary>
    public static int Corner { get => Int("ov.corner", 0, 0, 3); set => AppSettings.Set("ov.corner", value.ToString()); }
    /// <summary>Размер: 0 — 80%, 1 — 100%, 2 — 125%, 3 — 150%.</summary>
    public static int Size { get => Int("ov.size", 1, 0, 3); set => AppSettings.Set("ov.size", value.ToString()); }
    public static float Scale => Size switch { 0 => 0.8f, 2 => 1.25f, 3 => 1.5f, _ => 1f };
    /// <summary>Непрозрачность фона, %.</summary>
    public static int Background { get => Int("ov.bg", 60, 0, 100); set => AppSettings.Set("ov.bg", value.ToString()); }
    public static bool Auto { get => AppSettings.Get("ov.auto", "1") == "1"; set => AppSettings.Set("ov.auto", value ? "1" : "0"); }
    public static bool Warnings { get => AppSettings.Get("ov.warn", "1") == "1"; set => AppSettings.Set("ov.warn", value ? "1" : "0"); }
    public static bool On(string metric) => AppSettings.Get("ov.m." + metric, metric is "clock" ? "0" : "1") == "1";
    public static void Set(string metric, bool on) => AppSettings.Set("ov.m." + metric, on ? "1" : "0");

    public static Keys Hotkey
    {
        get
        {
            var v = AppSettings.Get("ov.hk", "");
            if (v == "") return Keys.Control | Keys.Shift | Keys.O;
            return int.TryParse(v, out int k) ? (Keys)k : Keys.None;
        }
        set => AppSettings.Set("ov.hk", ((int)value).ToString());
    }
}

/// <summary>
/// Счётчик кадров без внедрения в игру: события Windows (ETW) о показе кадров — провайдеры
/// Microsoft-Windows-DXGI (Present_Start, DirectX 10–12) и Microsoft-Windows-D3D9 (DirectX 9).
/// Так работают PresentMon и подобные инструменты; в процесс игры ничего не загружается — античитам нечего замечать.
/// Игры на Vulkan/OpenGL без DXGI эти события не пишут — для них FPS не показывается.
/// </summary>
static class FpsMeter
{
    static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");

    static TraceEventSession? session;
    static Thread? thread;
    static readonly object gate = new();
    static readonly List<double> times = new();   // отметки показа кадров (мс), последние ~12 с
    static int pid;
    static int users;
    static double sumFt, sumCount;                 // для средних за сессию
    static readonly List<double> sessionLows = new();
    public static bool Failed { get; private set; }

    /// <summary>Чей FPS считать.</summary>
    public static int Pid
    {
        get => pid;
        set
        {
            if (pid == value) return;
            lock (gate) { pid = value; times.Clear(); }
        }
    }

    /// <summary>Начать (счётчик ссылок: оверлей и статистика могут работать одновременно).</summary>
    public static void Acquire()
    {
        if (Interlocked.Increment(ref users) > 1 || thread != null) return;
        Failed = false;
        thread = new Thread(Run) { IsBackground = true, Name = "WinTweak FPS" };
        thread.Start();
    }

    public static void Release()
    {
        if (Interlocked.Decrement(ref users) > 0) return;
        users = 0;
        try { session?.Dispose(); } catch { }
        session = null;
        thread = null;
        lock (gate) times.Clear();
    }

    static void Run()
    {
        try
        {
            var s = new TraceEventSession("WinTweak-FPS") { StopOnDispose = true };
            session = s;
            s.EnableProvider(Dxgi, TraceEventLevel.Informational, ulong.MaxValue);
            s.EnableProvider(D3D9, TraceEventLevel.Informational, ulong.MaxValue);
            s.Source.AllEvents += OnEvent;
            s.Source.Process(); // блокирует, пока сессию не закроют
        }
        catch { Failed = true; }
    }

    static void OnEvent(TraceEvent e)
    {
        if (e.ProcessID != pid || pid == 0) return;
        int id = (int)e.ID;
        bool present = (e.ProviderGuid == Dxgi && id == 42) || (e.ProviderGuid == D3D9 && id == 1);
        if (!present) return;
        double t = e.TimeStampRelativeMSec;
        lock (gate)
        {
            if (times.Count > 0)
            {
                double ft = t - times[^1];
                if (ft > 0 && ft < 1000) { sumFt += ft; sumCount++; }
            }
            times.Add(t);
            // держим ~12 секунд
            int cut = 0;
            while (cut < times.Count && t - times[cut] > 12000) cut++;
            if (cut > 0) times.RemoveRange(0, cut);
        }
    }

    public readonly record struct Reading(double Fps, double FrameMs, double Low1, double[] Recent);

    /// <summary>FPS за последнюю секунду, среднее время кадра, 1% low за ~10 с и последние времена кадров для графика.</summary>
    public static Reading? Read()
    {
        double[] t;
        lock (gate) t = times.ToArray();
        if (t.Length < 3) return null;
        double now = t[^1];
        int n1 = t.Count(x => now - x <= 1000);
        var ft = new double[t.Length - 1];
        for (int i = 1; i < t.Length; i++) ft[i - 1] = t[i] - t[i - 1];
        var lastSec = ft.Skip(Math.Max(0, ft.Length - n1)).ToArray();
        double avg = lastSec.Length > 0 ? lastSec.Average() : ft.Average();
        var sorted = ft.OrderByDescending(x => x).ToArray();
        int k = Math.Max(1, sorted.Length / 100);
        double low = 1000 / sorted.Take(k).Average();
        var recent = ft.Skip(Math.Max(0, ft.Length - 240)).ToArray();
        return new Reading(n1, avg, low, recent);
    }

    /// <summary>Средний FPS с момента последнего сброса (для игровой статистики).</summary>
    public static double? SessionAverage(bool reset)
    {
        double? r = sumCount > 30 ? 1000 / (sumFt / sumCount) : null;
        if (reset) { sumFt = 0; sumCount = 0; }
        return r;
    }
}

/// <summary>Загрузка и температура видеокарты: NVIDIA — через nvidia-smi (идёт с драйвером), остальные — счётчики Windows «GPU Engine».</summary>
static class GpuMeter
{
    public static int? Load { get; private set; }
    public static int? Temp { get; private set; }
    static Process? smi;
    static System.Threading.Timer? pdhTimer;
    static int users;

    public static void Acquire()
    {
        if (Interlocked.Increment(ref users) > 1) return;
        string? exe = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
        }.FirstOrDefault(File.Exists);
        if (exe != null)
        {
            try
            {
                smi = Process.Start(new ProcessStartInfo(exe, "--query-gpu=utilization.gpu,temperature.gpu --format=csv,noheader,nounits -lms 1000")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
                });
                if (smi != null)
                {
                    smi.OutputDataReceived += (_, e) =>
                    {
                        var p = (e.Data ?? "").Split(',');
                        if (p.Length >= 2 && int.TryParse(p[0].Trim(), out int u) && int.TryParse(p[1].Trim(), out int c)) { Load = u; Temp = c; }
                    };
                    smi.BeginOutputReadLine();
                    return;
                }
            }
            catch { smi = null; }
        }
        pdhTimer = new System.Threading.Timer(_ => Load = Pdh.GpuLoad(), null, 0, 1000);
    }

    public static void Release()
    {
        if (Interlocked.Decrement(ref users) > 0) return;
        users = 0;
        try { if (smi is { HasExited: false }) smi.Kill(); } catch { }
        smi?.Dispose();
        smi = null;
        pdhTimer?.Dispose();
        pdhTimer = null;
        Load = Temp = null;
    }

    /// <summary>Счётчики производительности Windows: \GPU Engine(*engtype_3D)\Utilization Percentage.</summary>
    static class Pdh
    {
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string? src, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);

        static IntPtr query, counter;

        public static int? GpuLoad()
        {
            try
            {
                if (query == IntPtr.Zero)
                {
                    if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0) return null;
                    if (PdhAddEnglishCounter(query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out counter) != 0) return null;
                    PdhCollectQueryData(query);
                    return null;
                }
                PdhCollectQueryData(query);
                uint size = 0;
                PdhGetFormattedCounterArray(counter, 0x200 /* DOUBLE */, ref size, out _, IntPtr.Zero);
                if (size == 0) return 0;
                var buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (PdhGetFormattedCounterArray(counter, 0x200, ref size, out uint n, buf) != 0) return null;
                    double sum = 0;
                    int item = IntPtr.Size + 4 + 4 + 8; // имя (указатель), статус, выравнивание, значение
                    for (int i = 0; i < n; i++)
                        sum += BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buf, i * item + IntPtr.Size + 8));
                    return (int)Math.Clamp(Math.Round(sum), 0, 100);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { return null; }
        }
    }
}

/// <summary>Управление оверлеем: показ/скрытие, автопоказ в игре.</summary>
static class Overlay
{
    static OverlayWindow? window;
    static bool autoShown;
    public static bool Visible => window is { IsDisposed: false };

    public static void Toggle()
    {
        if (Visible) Hide(); else Show();
    }

    public static void Show()
    {
        if (Visible) return;
        window = new OverlayWindow();
        window.FormClosed += (_, _) => window = null;
        window.Show();
    }

    public static void Hide()
    {
        autoShown = false;
        window?.Close();
        window = null;
    }

    /// <summary>Пересобрать после смены настроек.</summary>
    public static void Reload()
    {
        if (!Visible) return;
        Hide();
        Show();
    }

    public static void GameStarted()
    {
        if (OverlaySettings.Auto && !Visible) { Show(); autoShown = true; }
    }

    public static void GameEnded()
    {
        if (autoShown) Hide();
    }
}

/// <summary>
/// Окно оверлея: поверх всех окон, прозрачное для мыши, без фокуса. Видно поверх игр в оконном и «полноэкранном
/// оконном» режимах; в эксклюзивном полноэкранном режиме Windows не показывает поверх игры другие окна.
/// </summary>
sealed class OverlayWindow : LayeredForm
{
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    readonly float k = OverlaySettings.Scale;
    readonly int style = OverlaySettings.Style, corner = OverlaySettings.Corner, bgAlpha = (int)(OverlaySettings.Background * 2.4);
    readonly bool warn = OverlaySettings.Warnings;
    readonly Dictionary<string, bool> on = OverlaySettings.Metrics.ToDictionary(m => m.Key, m => OverlaySettings.On(m.Key));
    readonly Font big, mid, small;
    readonly Queue<double> fpsHist = new(), pingHist = new();
    int tick;

    public OverlayWindow()
    {
        big = new Font("Segoe UI Semibold", 20f * k, GraphicsUnit.Pixel);
        mid = new Font("Segoe UI Semibold", 13f * k, GraphicsUnit.Pixel);
        small = new Font("Segoe UI", 11.5f * k, GraphicsUnit.Pixel);
        Size = style == 0 ? new Size((int)(420 * k), (int)(30 * k)) : new Size((int)(236 * k), (int)(150 * k));
        timer.Tick += (_, _) => Frame();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000020 /* TRANSPARENT */ | 0x08000000 /* NOACTIVATE */;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (on["fps"] || on["ft"] || on["low"]) FpsMeter.Acquire();
        if (on["gpu"] || on["temp"]) GpuMeter.Acquire();
        Frame();
        timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Dispose();
        if (on["fps"] || on["ft"] || on["low"]) FpsMeter.Release();
        if (on["gpu"] || on["temp"]) GpuMeter.Release();
        big.Dispose(); mid.Dispose(); small.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>Чья статистика: игра из слежения, иначе окно на переднем плане.</summary>
    static int TargetPid()
    {
        if (GameWatcher.Current is { } run) return run.Pid;
        var h = GetForegroundWindow();
        GetWindowThreadProcessId(h, out uint pid);
        return pid == Environment.ProcessId ? FpsMeter.Pid : (int)pid;
    }

    void Frame()
    {
        if (IsDisposed) return;
        FpsMeter.Pid = TargetPid();
        // место: угол экрана, на котором окно на переднем плане (игра)
        var screen = Screen.FromHandle(GetForegroundWindow()).Bounds;
        int m = (int)(14 * k);
        int x = corner is 0 or 2 ? screen.Left + m : screen.Right - Width - m;
        int y = corner is 0 or 1 ? screen.Top + m : screen.Bottom - Height - m;
        if (Location != new Point(x, y) || tick++ % 8 == 0)
            SetWindowPos(Handle, (IntPtr)(-1) /* TOPMOST */, x, y, 0, 0, 0x0001 /* NOSIZE */ | 0x0010 /* NOACTIVATE */);
        Present();
    }

    // ───── значения и цвета предупреждений ─────

    static void Push(Queue<double> q, double v, int max = 120)
    {
        q.Enqueue(v);
        while (q.Count > max) q.Dequeue();
    }

    Color FpsColor(double fps)
    {
        if (!warn || fpsHist.Count < 20) return Theme.Text;
        double avg = fpsHist.Average();
        return fps < avg * 0.6 ? Theme.Bad : fps < avg * 0.8 ? Theme.Warn : Theme.Text;
    }

    Color PingColor(int? ping)
    {
        if (ping == null) return warn ? Theme.Bad : Theme.Text;
        if (!warn) return Theme.Text;
        double avg = pingHist.Count > 5 ? pingHist.Average() : ping.Value;
        return ping > avg + 40 || ping > 150 ? Theme.Bad : ping > avg + 15 || ping > 90 ? Theme.Warn : Theme.Text;
    }

    Color Hot(int? v, int warnAt, int badAt) =>
        !warn || v == null ? Theme.Text : v >= badAt ? Theme.Bad : v >= warnAt ? Theme.Warn : Theme.Text;

    protected override void Draw(Graphics g)
    {
        var s = Sampler.Current;
        var fps = FpsMeter.Read();
        if (fps is { } f && tick % 4 == 0) Push(fpsHist, f.Fps);
        int? ping = GameMode.Monitor is { } mon ? mon.Last : s.PingMs;
        if (ping is int pp && tick % 4 == 0) Push(pingHist, pp, 60);
        int? gpu = GpuMeter.Load, temp = GpuMeter.Temp;
        var run = GameWatcher.Current;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        float rad = 10 * k;
        if (bgAlpha > 0)
        {
            using var path = Theme.Round(rect, rad);
            using var b = new SolidBrush(Color.FromArgb(bgAlpha, 10, 11, 16));
            g.FillPath(b, path);
            using var pen = new Pen(Color.FromArgb(Math.Min(255, bgAlpha / 3 + 20), 255, 255, 255));
            g.DrawPath(pen, path);
        }

        if (style == 0) DrawLine(g, s, fps, ping, gpu, temp, run);
        else DrawPanel(g, s, fps, ping, gpu, temp, run);
    }

    void DrawLine(Graphics g, Snap s, FpsMeter.Reading? fps, int? ping, int? gpu, int? temp, GameWatcher.Running? run)
    {
        var items = new List<(string Text, Color Color)>();
        if (on["fps"]) items.Add((fps is { } f ? $"{f.Fps:0} FPS" : "— FPS", fps is { } f2 ? FpsColor(f2.Fps) : Theme.Muted));
        if (on["ft"] && fps is { } a) items.Add(($"{a.FrameMs:0.0} мс", Theme.Text));
        if (on["low"] && fps is { } b) items.Add(($"1% {b.Low1:0}", Theme.Muted));
        if (on["cpu"]) items.Add(($"CPU {s.Cpu:0}%", Hot((int)s.Cpu, 90, 98)));
        if (on["gpu"] && gpu is int gl) items.Add(($"GPU {gl}%", Theme.Text));
        if (on["temp"] && temp is int t) items.Add(($"{t}°", Hot(t, 83, 90)));
        if (on["ping"]) items.Add((ping is int p ? $"{p} мс" : "пинг —", PingColor(ping)));
        if (on["ram"]) items.Add(($"ОЗУ {s.Ram:0}%", Hot((int)s.Ram, 85, 95)));
        if (on["clock"]) items.Add((DateTime.Now.ToString("HH:mm"), Theme.Muted));
        if (on["session"] && run != null) items.Add((Clock(DateTime.Now - run.Since), Theme.Muted));

        float x = 10 * k;
        foreach (var (text, color) in items)
        {
            Txt(g, text, mid, color, new RectangleF(x, 0, Width, Height), StringAlignment.Near);
            x += g.MeasureString(text, mid).Width + 2 * k;
            Txt(g, "·", mid, Theme.Dim, new RectangleF(x, 0, 12 * k, Height), StringAlignment.Near);
            x += 10 * k;
        }
        int want = (int)(x + 4 * k);
        if (Math.Abs(want - Width) > 6) BeginInvoke(() => { if (!IsDisposed) Width = want; });
    }

    void DrawPanel(Graphics g, Snap s, FpsMeter.Reading? fps, int? ping, int? gpu, int? temp, GameWatcher.Running? run)
    {
        float pad = 12 * k, y = 8 * k, w = Width - pad * 2;
        if (on["fps"])
        {
            Txt(g, fps is { } f ? $"{f.Fps:0}" : "—", big, fps is { } f2 ? FpsColor(f2.Fps) : Theme.Muted, new RectangleF(pad, y, w / 2, 28 * k), StringAlignment.Near);
            Txt(g, "FPS", small, Theme.Muted, new RectangleF(pad + g.MeasureString(fps is { } f3 ? $"{f3.Fps:0}" : "—", big).Width, y + 9 * k, 40 * k, 16 * k), StringAlignment.Near);
        }
        if (on["low"] && fps is { } lo)
            Txt(g, $"1% low {lo.Low1:0}", small, Theme.Muted, new RectangleF(pad, y + 6 * k, w, 18 * k), StringAlignment.Far);
        y += 32 * k;
        if (on["ft"])
        {
            var area = new RectangleF(pad, y, w - 54 * k, 30 * k);
            using (var track = new SolidBrush(Color.FromArgb(28, 255, 255, 255))) g.FillRectangle(track, area.X, area.Bottom, area.Width, 1);
            if (fps is { Recent.Length: > 2 } fr)
            {
                var v = fr.Recent;
                double max = Math.Max(20, v.Max() * 1.1);
                var pts = new PointF[v.Length];
                for (int i = 0; i < v.Length; i++)
                    pts[i] = new PointF(area.X + area.Width * i / (v.Length - 1), area.Bottom - (float)(v[i] / max * area.Height));
                using var pen = new Pen(Theme.Accent, 1.3f * k) { LineJoin = LineJoin.Round };
                g.DrawLines(pen, pts);
                Txt(g, $"{fr.FrameMs:0.0} мс", small, Theme.Text, new RectangleF(area.Right + 4 * k, y + 6 * k, 52 * k, 18 * k), StringAlignment.Near);
            }
            else Txt(g, FpsMeter.Failed ? "FPS недоступен" : "нет кадров DirectX", small, Theme.Dim, area, StringAlignment.Near);
            y += 38 * k;
        }
        var line1 = new List<(string, Color)>();
        if (on["cpu"]) line1.Add(($"CPU {s.Cpu:0}%", Hot((int)s.Cpu, 90, 98)));
        if (on["gpu"]) line1.Add((gpu is int gl ? $"GPU {gl}%" : "GPU —", Theme.Text));
        if (on["temp"] && temp is int t) line1.Add(($"{t}°", Hot(t, 83, 90)));
        var line2 = new List<(string, Color)>();
        if (on["ping"])
        {
            string loss = GameMode.Monitor != null && ping == null ? " · потеря" : "";
            line2.Add((ping is int p ? $"пинг {p} мс{loss}" : "пинг —", PingColor(ping)));
        }
        if (on["ram"]) line2.Add(($"ОЗУ {s.Ram:0}%", Hot((int)s.Ram, 85, 95)));
        var line3 = new List<(string, Color)>();
        if (on["clock"]) line3.Add((DateTime.Now.ToString("HH:mm"), Theme.Muted));
        if (on["session"] && run != null) line3.Add(("в игре " + Clock(DateTime.Now - run.Since), Theme.Muted));
        foreach (var line in new[] { line1, line2, line3 })
        {
            if (line.Count == 0) continue;
            float x = pad;
            foreach (var (text, color) in line)
            {
                Txt(g, text, small, color, new RectangleF(x, y, Width, 18 * k), StringAlignment.Near);
                x += g.MeasureString(text, small).Width + 6 * k;
            }
            y += 19 * k;
        }
        int want = (int)(y + 8 * k);
        if (Math.Abs(want - Height) > 2) BeginInvoke(() => { if (!IsDisposed) Height = want; });
    }

    static string Clock(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
}
