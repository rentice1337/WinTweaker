using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinTweaker;

// ═══════════════════════════════════════════════════════════════
//  Игровая статистика
// ═══════════════════════════════════════════════════════════════

/// <summary>Одна игровая сессия: сколько играл, средний FPS и пинг.</summary>
sealed class PlaySession
{
    public string Game { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public double? Fps { get; set; }
    public double? Ping { get; set; }
    public double Hours => (End - Start).TotalHours;
}

/// <summary>История игровых сессий (%AppData%\WinTweaker\stats.json, последние 500).</summary>
static class GameStats
{
    static readonly string File_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinTweaker", "stats.json");

    public static List<PlaySession> Load()
    {
        try { return JsonSerializer.Deserialize<List<PlaySession>>(File.ReadAllText(File_)) ?? new(); }
        catch { return new(); }
    }

    public static void Add(PlaySession s)
    {
        if ((s.End - s.Start).TotalMinutes < 1) return;
        var list = Load();
        list.Insert(0, s);
        if (list.Count > 500) list.RemoveRange(500, list.Count - 500);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, JsonSerializer.Serialize(list));
        }
        catch { }
    }
}

class StatsWidget : Widget
{
    readonly List<PlaySession> all;
    readonly List<(string Game, double Hours, double? Fps, double? Ping, int Count)> games;
    readonly double[] days = new double[14];
    const int RowH = 30, Top0 = 50;

    public StatsWidget(List<PlaySession> sessions) : base("Игровая статистика", 28 + Top0 + 6 * RowH + 20)
    {
        Wide = true;
        var since = DateTime.Now.AddDays(-30);
        all = sessions.Where(s => s.Start >= since).ToList();
        games = all.GroupBy(s => s.Game)
            .Select(g => (g.Key, g.Sum(s => s.Hours),
                          g.Where(s => s.Fps != null).Select(s => s.Fps!.Value).DefaultIfEmpty(double.NaN).Average() is double f && !double.IsNaN(f) ? f : (double?)null,
                          g.Where(s => s.Ping != null).Select(s => s.Ping!.Value).DefaultIfEmpty(double.NaN).Average() is double p && !double.IsNaN(p) ? p : (double?)null,
                          g.Count()))
            .OrderByDescending(x => x.Item2).Take(6).ToList();
        var today = DateTime.Today;
        foreach (var s in all)
        {
            int d = (int)(today - s.Start.Date).TotalDays;
            if (d >= 0 && d < 14) days[13 - d] += s.Hours;
        }
        if (games.Count == 0) Height = 28 + 90;
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        if (games.Count == 0)
        {
            TextRenderer.DrawText(g, "Появится после первых игр: WinTweak сам замечает запуск игры и запоминает, сколько вы играли, средний FPS и пинг.",
                Theme.Body, new Rectangle(r.Left + 18, r.Top + 14, r.Width - 36, 50), C(Theme.Muted), TextFormatFlags.WordBreak);
            return;
        }
        double total = all.Sum(s => s.Hours);
        TextRenderer.DrawText(g, $"За 30 дней: {Hours(total)} в {all.Select(s => s.Game).Distinct().Count()} играх · сессий: {all.Count}",
            Theme.Semi, new Rectangle(r.Left + 18, r.Top + 14, r.Width - 36, 22), C(Theme.Text), flags);

        // слева: игры
        int lw = (int)(r.Width * 0.58);
        double max = Math.Max(0.1, games.Max(x => x.Hours));
        for (int i = 0; i < games.Count; i++)
        {
            var (game, hours, fps, ping, count) = games[i];
            int y = r.Top + Top0 + i * RowH;
            TextRenderer.DrawText(g, game, Theme.Body, new Rectangle(r.Left + 18, y, 150, RowH - 4), C(Theme.Text), flags);
            float bx = r.Left + 172, bw = lw - 172 - 150;
            Theme.Fill(g, Color.FromArgb((int)(18 * Appear), 255, 255, 255), new RectangleF(bx, y + 11, bw, 5), 2.5f);
            Theme.Fill(g, C(i == 0 ? Theme.Accent : Theme.Lerp(Theme.Accent, Color.FromArgb(150, 160, 175), 0.45)),
                new RectangleF(bx, y + 11, (float)(bw * hours / max * Appear), 5), 2.5f);
            string stat = Hours(hours) + (fps is double f ? $" · {f:0} FPS" : "") + (ping is double p ? $" · {p:0} мс" : "");
            TextRenderer.DrawText(g, stat, Theme.Tiny, new Rectangle((int)(bx + bw + 10), y, 150, RowH - 4), C(Theme.Muted), flags);
        }

        // справа: часы по дням за 2 недели
        var chart = new RectangleF(r.Left + lw + 20, r.Top + Top0 + 6, r.Right - lw - 40 - r.Left, 6 * RowH - 40);
        double dmax = Math.Max(0.5, days.Max());
        float step = chart.Width / 14, bwid = step * 0.6f;
        for (int i = 0; i < 14; i++)
        {
            float h = (float)(days[i] / dmax * chart.Height * Appear);
            var bar = new RectangleF(chart.X + i * step + (step - bwid) / 2, chart.Bottom - h, bwid, Math.Max(h, 1.5f));
            Theme.Fill(g, C(i == 13 ? Theme.Accent : Color.FromArgb(150, 160, 175)), bar, Math.Min(3, bwid / 2));
        }
        TextRenderer.DrawText(g, "по дням, 2 недели", Theme.Tiny, new Rectangle((int)chart.X, (int)chart.Bottom + 6, (int)chart.Width, 18), C(Theme.Dim), TextFormatFlags.Left);
        TextRenderer.DrawText(g, "сегодня", Theme.Tiny, new Rectangle((int)chart.X, (int)chart.Bottom + 6, (int)chart.Width, 18), C(Theme.Dim), TextFormatFlags.Right);
    }

    static string Hours(double h) => h >= 1 ? $"{h:0.#} ч" : $"{Math.Max(1, h * 60):0} мин";
}

// ═══════════════════════════════════════════════════════════════
//  Частота мониторов
// ═══════════════════════════════════════════════════════════════

/// <summary>Мониторы, работающие не на максимальной частоте (частая история: 144 Гц монитор на 60 Гц).</summary>
static class Displays
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string? device, int i, ref DISPLAY_DEVICE dd, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);

    public sealed record Info(string Device, string Name, int Width, int Height, int Hz, int MaxHz);

    public static List<Info> All()
    {
        var list = new List<Info>();
        for (int i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & 1) == 0) continue; // не подключён к рабочему столу
            var cur = New();
            if (!EnumDisplaySettings(dd.DeviceName, -1 /* ENUM_CURRENT_SETTINGS */, ref cur)) continue;
            int max = cur.dmDisplayFrequency;
            var m = New();
            for (int k = 0; EnumDisplaySettings(dd.DeviceName, k, ref m); k++)
            {
                if (m.dmPelsWidth == cur.dmPelsWidth && m.dmPelsHeight == cur.dmPelsHeight && m.dmBitsPerPel == cur.dmBitsPerPel)
                    max = Math.Max(max, m.dmDisplayFrequency);
                m = New();
            }
            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            string name = EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0) && !string.IsNullOrWhiteSpace(mon.DeviceString) ? mon.DeviceString : dd.DeviceString;
            list.Add(new Info(dd.DeviceName, name, cur.dmPelsWidth, cur.dmPelsHeight, cur.dmDisplayFrequency, max));
        }
        return list;
    }

    static DEVMODE New() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    /// <summary>Поставить максимальную частоту при текущем разрешении. Возвращает, получилось ли.</summary>
    public static bool SetMax(Info d)
    {
        var dm = New();
        if (!EnumDisplaySettings(d.Device, -1, ref dm)) return false;
        dm.dmDisplayFrequency = d.MaxHz;
        dm.dmFields = 0x400000; // DM_DISPLAYFREQUENCY
        return ChangeDisplaySettingsEx(d.Device, ref dm, IntPtr.Zero, 0x01 /* CDS_UPDATEREGISTRY */, IntPtr.Zero) == 0;
    }

    public static List<Info> Slow() => All().Where(d => d.MaxHz > d.Hz + 1).ToList();
}

class DisplayWidget : Widget
{
    List<Displays.Info>? list;
    public readonly FlatBtn Fix = new("\uE73E", "Включить максимум") { Primary = true, Visible = false };
    const int RowH = 34;

    public DisplayWidget() : base("Частота мониторов", 28 + 12 + RowH + 60)
    {
        Controls.Add(Fix);
    }

    public void SetData(List<Displays.Info> d)
    {
        list = d;
        Fix.Visible = d.Any(x => x.MaxHz > x.Hz + 1);
        Height = HeadH + 12 + Math.Max(1, d.Count) * RowH + (Fix.Visible ? 60 : 40);
        PerformLayout();
        Invalidate(true);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Fix is null) return;
        Fix.Location = new Point(Width - Fix.Width - 8, Height - Fix.Height - 4);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        if (list == null) { TextRenderer.DrawText(g, "Проверяю…", Theme.Body, new Rectangle(r.Left + 18, r.Top + 12, 200, RowH), C(Theme.Muted), flags); return; }
        for (int i = 0; i < list.Count; i++)
        {
            var d = list[i];
            int y = r.Top + 12 + i * RowH;
            bool slow = d.MaxHz > d.Hz + 1;
            GameUI.Dot(g, slow ? Theme.Warn : Theme.Good, r.Left + 20, y + 12, 9, Appear);
            TextRenderer.DrawText(g, $"{d.Name} · {d.Width}×{d.Height}", Theme.Body, new Rectangle(r.Left + 38, y, r.Width - 200, RowH), C(Theme.Text), flags);
            TextRenderer.DrawText(g, slow ? $"{d.Hz} Гц из {d.MaxHz}" : $"{d.Hz} Гц — максимум", Theme.Semi, new Rectangle(r.Right - 160, y, 144, RowH),
                C(slow ? Theme.Warn : Theme.Muted), flags | TextFormatFlags.Right);
        }
        if (Fix.Visible)
            TextRenderer.DrawText(g, "Монитор умеет больше — в играх будет плавнее", Theme.Tiny, new Rectangle(r.Left + 18, Fix.Top + Fix.Pad, Fix.Left - r.Left - 24, Fix.Height - Fix.Pad * 2),
                C(Theme.Muted), flags);
    }
}

// ═══════════════════════════════════════════════════════════════
//  Тест мыши: реальная частота опроса
// ═══════════════════════════════════════════════════════════════

class MouseTestDialog : RoundForm
{
    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public int Flags; public IntPtr Target; }
    [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, int n, int size);

    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly List<double> stamps = new();
    readonly List<int> history = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    int now, best;
    bool closing;
    int baseTop;

    public MouseTestDialog()
    {
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Opacity = 0;
        Size = new Size(560, 420);
        var ok = new FlatBtn("\uE73E", "Готово") { Primary = true };
        ok.Location = new Point(Width - 24 - ok.Width + ok.Pad, Height - 22 - ok.Height + ok.Pad);
        ok.Click += (_, _) => Finish();
        var reset = new FlatBtn("\uE72C", "Сначала");
        reset.Location = new Point(ok.Left + ok.Pad - reset.Width - 8, ok.Top + ok.Pad);
        reset.Click += (_, _) => { best = 0; history.Clear(); Invalidate(); };
        Controls.Add(ok);
        Controls.Add(reset);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Finish(); };
        timer.Tick += (_, _) => Sample();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dev = new[] { new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = 0x100 /* INPUTSINK */, Target = Handle } };
        RegisterRawInputDevices(dev, 1, Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x00FF /* WM_INPUT */) stamps.Add(clock.Elapsed.TotalMilliseconds);
        base.WndProc(ref m);
    }

    /// <summary>Частота — по плотным отрезкам движения (раз в 100 мс); паузы не считаются.</summary>
    void Sample()
    {
        double t = clock.Elapsed.TotalMilliseconds;
        stamps.RemoveAll(x => t - x > 200);
        int n = stamps.Count(x => t - x <= 100);
        now = n >= 5 ? n * 10 : 0;
        if (now > 0)
        {
            history.Add(now);
            if (history.Count > 120) history.RemoveAt(0);
            // лучший «устойчивый» результат: медиана верхней четверти замеров
            var top = history.OrderByDescending(x => x).Take(Math.Max(1, history.Count / 4)).ToList();
            best = top[top.Count / 2];
        }
        Invalidate();
    }

    static string Class(int hz) => hz switch
    {
        0 => "—",
        < 190 => "≈125 Гц — офисная мышь",
        < 380 => "≈250 Гц",
        < 760 => "≈500 Гц",
        < 1500 => "≈1000 Гц — норма для игровых мышей",
        < 3000 => "≈2000 Гц",
        < 6000 => "≈4000 Гц",
        _ => "≈8000 Гц"
    };

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Owner != null) Location = new Point(Owner.Left + (Owner.Width - Width) / 2, Owner.Top + (Owner.Height - Height) / 2);
        baseTop = Top;
        Motion.Run(240, p => { Opacity = p; Top = baseTop + (int)Math.Round(12 * (1 - p)); });
        timer.Start();
    }

    void Finish()
    {
        if (closing) return;
        closing = true;
        timer.Stop();
        Motion.Run(140, p => Opacity = 1 - p, Motion.InOut, 0, Close);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        TextRenderer.DrawText(g, "Тест мыши", Theme.F(13f, FontStyle.Bold), new Point(24, 20), Theme.Text);
        TextRenderer.DrawText(g, "Быстро водите мышью кругами 3–5 секунд. Считаются только сообщения от самой мыши (Raw Input).",
            Theme.Tiny, new Rectangle(24, 48, Width - 48, 32), Theme.Muted, TextFormatFlags.WordBreak);
        TextRenderer.DrawText(g, best > 0 ? $"{best}" : "—", Theme.F(34f, FontStyle.Bold), new Rectangle(24, 84, 220, 60), Theme.Accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "Гц · частота опроса", Theme.Body, new Rectangle(24, 142, 260, 20), Theme.Muted, TextFormatFlags.Left);
        TextRenderer.DrawText(g, Class(best), Theme.Semi, new Rectangle(24, 166, Width - 48, 22), Theme.Text, TextFormatFlags.Left);
        TextRenderer.DrawText(g, now > 0 ? $"сейчас {now} Гц" : "двигайте мышью…", Theme.Tiny, new Rectangle(Width - 224, 100, 200, 20), Theme.Muted, TextFormatFlags.Right);

        var area = new RectangleF(24, 204, Width - 48, 120);
        Theme.Fill(g, Color.FromArgb(14, 255, 255, 255), area, 10);
        if (history.Count >= 2)
        {
            double max = Math.Max(200, history.Max() * 1.15);
            var pts = history.Select((v, i) => new PointF(area.X + 8 + (area.Width - 16) * i / Math.Max(1, history.Count - 1),
                area.Bottom - 8 - (float)(v / max * (area.Height - 16)))).ToArray();
            using var pen = new Pen(Theme.Accent, 1.6f) { LineJoin = LineJoin.Round };
            g.DrawLines(pen, pts);
            // разброс: если линия «пилит» — нестабильный опрос (беспроводная мышь, USB-хаб, перегруженный порт)
            double avg = history.Average(), dev = Math.Sqrt(history.Average(v => (v - avg) * (v - avg)));
            string stab = avg > 0 && dev / avg < 0.12 ? "опрос ровный" : "опрос неровный — попробуйте другой USB-порт или провод";
            TextRenderer.DrawText(g, stab, Theme.Tiny, new Rectangle(24, (int)area.Bottom + 6, Width - 48, 18), Theme.Muted, TextFormatFlags.Left);
        }
    }
}

// ═══════════════════════════════════════════════════════════════
//  Куда делось место
// ═══════════════════════════════════════════════════════════════

static class DiskSpace
{
    public sealed record Item(string Path, string Name, long Bytes, bool IsDir);

    static readonly EnumerationOptions Deep = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false,
    };

    static readonly EnumerationOptions Flat = new()
    {
        RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false,
    };

    static long Size(string dir, ref long files)
    {
        long sum = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Deep))
            {
                try { sum += f.Length; } catch { }
                Interlocked.Increment(ref files);
            }
        }
        catch { }
        return sum;
    }

    /// <summary>Содержимое папки с размерами (папки — рекурсивно), по убыванию.</summary>
    public static List<Item> Scan(string root, Action<long>? progress = null)
    {
        var result = new List<Item>();
        var di = new DirectoryInfo(root);
        long files = 0;
        var dirs = new List<DirectoryInfo>();
        try { dirs = di.EnumerateDirectories("*", Flat).ToList(); } catch { }
        var bag = new System.Collections.Concurrent.ConcurrentBag<Item>();
        using var timer = new System.Threading.Timer(_ => progress?.Invoke(Interlocked.Read(ref files)), null, 300, 300);
        Parallel.ForEach(dirs, new ParallelOptions { MaxDegreeOfParallelism = 4 }, d =>
        {
            long size = Size(d.FullName, ref files);
            bag.Add(new Item(d.FullName, d.Name, size, true));
        });
        result.AddRange(bag);
        try
        {
            long loose = 0;
            foreach (var f in di.EnumerateFiles("*", Flat))
            {
                try
                {
                    if (f.Length > 256L << 20) result.Add(new Item(f.FullName, f.Name, f.Length, false));
                    else loose += f.Length;
                }
                catch { }
            }
            if (loose > 0) result.Add(new Item(root, "Файлы в этой папке", loose, false));
        }
        catch { }
        return result.OrderByDescending(x => x.Bytes).ToList();
    }
}

class DiskSpaceWidget : Widget
{
    public readonly FlatBtn Back = new("\uE72B", "Назад") { Visible = false };
    public readonly FlatBtn Run = new("\uE721", "Сканировать") { Primary = true };
    public event Action<DiskSpace.Item>? Open;
    public event Action<DiskSpace.Item>? Reveal;
    List<DiskSpace.Item>? items;
    public string Root { get; private set; } = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
    public readonly Stack<string> Trail = new();
    public bool Busy { get; private set; }
    long scanned;
    int hot = -1;
    const int RowH = 30, Rows = 12, Top0 = 54;

    public DiskSpaceWidget() : base("Куда делось место", 28 + Top0 + 70)
    {
        Wide = true;
        Controls.Add(Back);
        Controls.Add(Run);
    }

    public void Begin(string root)
    {
        Root = root;
        Busy = true;
        items = null;
        scanned = 0;
        Invalidate(false);
    }

    public void Progress(long files) { scanned = files; Invalidate(false); }

    public void SetData(List<DiskSpace.Item> list)
    {
        Busy = false;
        items = list;
        Back.Visible = Trail.Count > 0;
        int n = Math.Min(Rows, list.Count);
        Height = HeadH + Top0 + Math.Max(1, n) * RowH + 16;
        PerformLayout();
        Invalidate(true);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Run is null || Back is null) return;
        Run.Location = new Point(Width - Run.Width - 8, HeadH + 4);
        Back.Location = new Point(Run.Left + Run.Pad - Back.Width - 8, Run.Top + Run.Pad);
    }

    Rectangle Row(int i) => new(10, HeadH + Top0 + i * RowH, Width - 20, RowH - 2);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = -1;
        for (int i = 0; i < Math.Min(Rows, items?.Count ?? 0); i++) if (Row(i).Contains(e.Location)) { h = i; break; }
        if (h != hot) { hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(false); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { if (hot != -1) { hot = -1; Invalidate(false); } base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (items != null && hot >= 0 && hot < items.Count && !Busy)
        {
            var it = items[hot];
            // справа — «показать в Проводнике», остальное — зайти внутрь
            if (e.X > Width - 110 || !it.IsDir) Reveal?.Invoke(it); else Open?.Invoke(it);
        }
        base.OnMouseClick(e);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        var (free, total) = SysInfo.SystemDrive();
        string head = Busy ? $"Сканирую {Root}… файлов: {scanned:N0}" : items == null ? $"Самые большие папки на {Root} — нажмите «Сканировать»" : Root;
        TextRenderer.DrawText(g, head, Theme.Semi, new Rectangle(r.Left + 18, r.Top + 10, Back.Left - r.Left - 30, 22), C(Theme.Text), flags);
        TextRenderer.DrawText(g, $"Свободно на системном диске {free:0} из {total:0} ГБ · клик по папке — внутрь, по правому краю — в Проводнике",
            Theme.Tiny, new Rectangle(r.Left + 18, r.Top + 32, Back.Left - r.Left - 30, 18), C(Theme.Muted), flags);
        if (items == null) return;
        long max = Math.Max(1, items.FirstOrDefault()?.Bytes ?? 1);
        for (int i = 0; i < Math.Min(Rows, items.Count); i++)
        {
            var it = items[i];
            var row = Row(i);
            if (i == hot) Theme.Fill(g, Color.FromArgb((int)(14 * Appear), 255, 255, 255), row, 7);
            TextRenderer.DrawText(g, it.IsDir ? "\uE8B7" : "\uE8A5", Theme.Icon, new Rectangle(row.X + 6, row.Y, 22, row.Height), C(it.IsDir ? Theme.Accent : Theme.Muted),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, it.Name, Theme.Body, new Rectangle(row.X + 32, row.Y, 260, row.Height), C(Theme.Text), flags);
            float bx = row.X + 300, bw = row.Width - 300 - 210;
            Theme.Fill(g, Color.FromArgb((int)(18 * Appear), 255, 255, 255), new RectangleF(bx, row.Y + 12, bw, 5), 2.5f);
            Theme.Fill(g, C(i == 0 ? Theme.Accent : Theme.Lerp(Theme.Accent, Color.FromArgb(150, 160, 175), 0.45)),
                new RectangleF(bx, row.Y + 12, (float)(bw * it.Bytes / max * Appear), 5), 2.5f);
            TextRenderer.DrawText(g, GameUI.Bytes(it.Bytes), Theme.Semi, new Rectangle((int)(bx + bw + 10), row.Y, 90, row.Height), C(Theme.Text), flags);
            if (i == hot) TextRenderer.DrawText(g, "в Проводнике", Theme.Tiny, new Rectangle(row.Right - 104, row.Y, 96, row.Height), C(Theme.Accent), flags | TextFormatFlags.Right);
        }
    }
}

// ═══════════════════════════════════════════════════════════════
//  Здоровье дисков
// ═══════════════════════════════════════════════════════════════

static class DiskHealth
{
    public sealed record Disk(string Name, string Kind, string Health, double SizeGb, int? Wear, int? Temp, int? Hours, long? Errors);

    public static List<Disk> Read()
    {
        var text = Shell.PS(
            "Get-PhysicalDisk | ForEach-Object { $r = $_ | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue; " +
            "@($_.FriendlyName, $_.MediaType, $_.BusType, $_.HealthStatus, $_.Size, $r.Wear, $r.Temperature, $r.PowerOnHours, " +
            "([long]$r.ReadErrorsUncorrected + [long]$r.WriteErrorsUncorrected)) -join '|' }");
        var list = new List<Disk>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split('|');
            if (p.Length < 9) continue;
            int? I(string s) => int.TryParse(s, out int v) ? v : null;
            string kind = p[2] == "NVMe" ? "NVMe SSD" : p[1] is "SSD" ? "SSD" : p[1] is "HDD" ? "HDD" : p[2];
            list.Add(new Disk(p[0], kind, p[3], long.TryParse(p[4], out long sz) ? sz / 1e9 : 0, I(p[5]), I(p[6]), I(p[7]),
                long.TryParse(p[8], out long er) ? er : null));
        }
        return list;
    }
}

class DiskHealthWidget : Widget
{
    List<DiskHealth.Disk>? disks;
    const int RowH = 58;

    public DiskHealthWidget() : base("Здоровье дисков", 28 + 12 + RowH + 12) { }

    public void SetData(List<DiskHealth.Disk> d)
    {
        disks = d;
        Height = HeadH + 12 + Math.Max(1, d.Count) * RowH + 12;
        Invalidate(false);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
        if (disks == null || disks.Count == 0)
        {
            TextRenderer.DrawText(g, disks == null ? "Читаю SMART…" : "Нет данных о дисках", Theme.Body, new Rectangle(r.Left + 18, r.Top + 14, r.Width - 36, 22), C(Theme.Muted), flags);
            return;
        }
        for (int i = 0; i < disks.Count; i++)
        {
            var d = disks[i];
            int y = r.Top + 12 + i * RowH;
            bool ok = d.Health == "Healthy" && (d.Errors ?? 0) == 0 && (d.Wear ?? 0) < 80;
            var col = d.Health == "Unhealthy" || (d.Wear ?? 0) >= 90 ? Theme.Bad : ok ? Theme.Good : Theme.Warn;
            GameUI.Dot(g, col, r.Left + 20, y + 6, 9, Appear);
            TextRenderer.DrawText(g, d.Name, Theme.Semi, new Rectangle(r.Left + 38, y, r.Width - 160, 20), C(Theme.Text), flags);
            string state = d.Health switch { "Healthy" => "исправен", "Warning" => "есть предупреждения", "Unhealthy" => "неисправен", _ => d.Health };
            TextRenderer.DrawText(g, state, Theme.Tiny, new Rectangle(r.Right - 140, y + 2, 124, 18), C(col), flags | TextFormatFlags.Right);
            var parts = new List<string> { d.Kind, $"{d.SizeGb:0} ГБ" };
            if (d.Temp is int t && t > 0) parts.Add($"{t}°C");
            if (d.Hours is int h && h > 0) parts.Add($"{h:N0} ч работы");
            if (d.Errors is long er && er > 0) parts.Add($"ошибок: {er}");
            TextRenderer.DrawText(g, string.Join(" · ", parts), Theme.Tiny, new Rectangle(r.Left + 38, y + 20, r.Width - 56, 18), C(Theme.Muted), flags);
            if (d.Wear is int w)
            {
                var bar = new RectangleF(r.Left + 38, y + 42, r.Width - 56 - 120, 4);
                Theme.Fill(g, Color.FromArgb((int)(18 * Appear), 255, 255, 255), bar, 2);
                float life = Math.Clamp(100 - w, 0, 100) / 100f;
                Theme.Fill(g, C(life > 0.3 ? Theme.Good : life > 0.1 ? Theme.Warn : Theme.Bad), new RectangleF(bar.X, bar.Y, bar.Width * life * (float)Appear, 4), 2);
                TextRenderer.DrawText(g, $"ресурс {100 - w}%", Theme.Tiny, new Rectangle((int)bar.Right + 10, y + 34, 110, 18), C(Theme.Muted), flags);
            }
        }
    }
}
