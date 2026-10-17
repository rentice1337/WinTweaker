using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WinTweaker;

/// <summary>Настройки игрового оверлея.</summary>
static class OverlaySettings
{
    public static readonly (string Key, string Title)[] Metrics =
    {
        ("fps", "FPS"), ("ft", "Время кадра и график"), ("low", "1% low"), ("cpu", "Загрузка CPU"), ("gpu", "Загрузка GPU"),
        ("temp", "Температура GPU"), ("ping", "Пинг и потери"), ("ram", "ОЗУ"), ("clock", "Часы"), ("session", "Время в игре"),
    };

    public static readonly string[] Styles = { "Капсула", "HUD", "Панель" };

    static int Int(string key, int def, int min, int max) =>
        int.TryParse(AppSettings.Get(key, def.ToString()), out int v) ? Math.Clamp(v, min, max) : def;

    /// <summary>0 — неоновая капсула, 1 — тактический HUD, 2 — панель-дашборд.</summary>
    public static int Style { get => Int("ov.style2", 0, 0, 2); set => AppSettings.Set("ov.style2", value.ToString()); }
    /// <summary>0 — слева сверху, 1 — справа сверху, 2 — слева снизу, 3 — справа снизу, 4 — своё место (перетащено мышью).</summary>
    public static int Corner { get => Int("ov.corner", 0, 0, 4); set => AppSettings.Set("ov.corner", value.ToString()); }
    /// <summary>Своё место: доли свободного пространства экрана по X и Y (0…1).</summary>
    public static (double X, double Y) Place
    {
        get
        {
            double.TryParse(AppSettings.Get("ov.x", "0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x);
            double.TryParse(AppSettings.Get("ov.y", "0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double y);
            return (Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
        }
        set
        {
            AppSettings.Set("ov.x", value.X.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
            AppSettings.Set("ov.y", value.Y.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
    /// <summary>Монитор своего места (имя устройства Windows, например \\.\DISPLAY2).</summary>
    public static string Monitor { get => AppSettings.Get("ov.mon", ""); set => AppSettings.Set("ov.mon", value); }
    /// <summary>Непрозрачность фона, %.</summary>
    public static int Background { get => Int("ov.bg", 70, 0, 100); set => AppSettings.Set("ov.bg", value.ToString()); }
    public static bool Auto { get => AppSettings.Get("ov.auto", "1") == "1"; set => AppSettings.Set("ov.auto", value ? "1" : "0"); }
    public static bool Warnings { get => AppSettings.Get("ov.warn", "1") == "1"; set => AppSettings.Set("ov.warn", value ? "1" : "0"); }
    /// <summary>Тихий режим: полупрозрачный, пока всё в порядке.</summary>
    public static bool Quiet { get => AppSettings.Get("ov.quiet", "0") == "1"; set => AppSettings.Set("ov.quiet", value ? "1" : "0"); }
    /// <summary>Линия цели FPS (частота монитора) на графике.</summary>
    public static bool TargetLine { get => AppSettings.Get("ov.target", "1") == "1"; set => AppSettings.Set("ov.target", value ? "1" : "0"); }
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

