using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinTweaker;

/// <summary>Сведения о системе: реестр и WinAPI, без медленного WMI.</summary>
static class SysInfo
{
    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    public static (double Load, double TotalGb, double UsedGb) Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
        double total = m.ullTotalPhys / 1073741824.0, avail = m.ullAvailPhys / 1073741824.0;
        return (m.dwMemoryLoad, total, total - avail);
    }

    static long prevIdle, prevTotal;

    /// <summary>Загрузка CPU в % с момента прошлого вызова (kernel time включает idle).</summary>
    public static double Cpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        long total = kernel + user;
        long dIdle = idle - prevIdle, dTotal = total - prevTotal;
        prevIdle = idle;
        prevTotal = total;
        return dTotal <= 0 ? 0 : Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
    }

    const string NT = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    static string? Hklm(string key, string name)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            return k?.GetValue(name)?.ToString();
        }
        catch { return null; }
    }

    public static int BuildNumber => int.TryParse(Hklm(NT, "CurrentBuild"), out int b) ? b : 0;
    public static bool IsHome => (Hklm(NT, "EditionID") ?? "").StartsWith("Core", StringComparison.OrdinalIgnoreCase);
    public static bool IsLaptop => (SystemInformation.PowerStatus.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0;
    public static double UptimeDays => Environment.TickCount64 / 86400000.0;

    public static string Os()
    {
        string name = Hklm(NT, "ProductName") ?? "Windows";
        // в Windows 11 ProductName по-прежнему «Windows 10»
        if (BuildNumber >= 22000) name = name.Replace("Windows 10", "Windows 11");
        return $"{name} {Hklm(NT, "DisplayVersion") ?? Hklm(NT, "ReleaseId") ?? ""}".Trim();
    }

    public static string Build()
    {
        string ubr = Hklm(NT, "UBR") ?? "";
        return (Hklm(NT, "CurrentBuild") ?? "?") + (ubr != "" ? "." + ubr : "") + (Environment.Is64BitOperatingSystem ? " · x64" : " · x86");
    }

    public static string CpuName() =>
        (Hklm(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim() ?? "—")
        + $" · {Environment.ProcessorCount} потоков";

    public static List<string> GpuList()
    {
        var names = new List<string>();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (k != null)
                foreach (var sub in k.GetSubKeyNames().Where(s => int.TryParse(s, out _)))
                {
                    try
                    {
                        using var s = k.OpenSubKey(sub);
                        var d = s?.GetValue("DriverDesc")?.ToString();
                        if (!string.IsNullOrWhiteSpace(d) && !d.Contains("Basic", StringComparison.OrdinalIgnoreCase) && !names.Contains(d))
                            names.Add(d);
                    }
                    catch { }
                }
        }
        catch { }
        return names;
    }

    public static string Gpu()
    {
        var g = GpuList();
        return g.Count == 0 ? "—" : string.Join(", ", g);
    }

    public static string Uptime()
    {
        var t = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return t.Days > 0 ? $"{t.Days} д {t.Hours} ч {t.Minutes} мин" : $"{t.Hours} ч {t.Minutes} мин";
    }

    public static (double FreeGb, double TotalGb) SystemDrive()
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            return (d.AvailableFreeSpace / 1073741824.0, d.TotalSize / 1073741824.0);
        }
        catch { return (0, 0); }
    }
}

/// <summary>Виджет дашборда — стеклянная карточка с заголовком.</summary>
abstract class Widget : Surface
{
    protected Widget(string title, int height) : base(title) { Height = height; }
}

/// <summary>Список «параметр — значение».</summary>
class InfoWidget : Widget
{
    readonly (string Key, string Value)[] rows;

    public InfoWidget(string title, params (string, string)[] rows) : base(title, 28 + 14 + rows.Length * 28 + 10)
    {
        this.rows = rows;
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        for (int i = 0; i < rows.Length; i++)
        {
            int y = r.Top + 12 + i * 28;
            TextRenderer.DrawText(g, rows[i].Key, Theme.Body, new Rectangle(r.Left + 18, y, 104, 24), C(Theme.Muted), flags);
            TextRenderer.DrawText(g, rows[i].Value, Theme.Body, new Rectangle(r.Left + 124, y, r.Width - 140, 24), C(Theme.Text), flags);
        }
    }
}

/// <summary>Живая нагрузка CPU и ОЗУ: число плавно догоняет новое значение, под ним график за минуту.</summary>
class LiveWidget : Widget
{
    const int N = 60;
    readonly Queue<double> cpu = new(), ram = new();
    readonly AnimFloat cpuNow, ramNow;
    string ramText = "";

    public LiveWidget() : base("Нагрузка", 28 + 12 + 2 * 84 + 8)
    {
        cpuNow = new AnimFloat(0, () => Invalidate(false));
        ramNow = new AnimFloat(0, () => Invalidate(false));
    }

    public void Push(double c, double r, string ramLabel)
    {
        Add(cpu, c);
        Add(ram, r);
        ramText = ramLabel;
        cpuNow.To(c, 600);
        ramNow.To(r, 600);
    }

    static void Add(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > N) q.Dequeue();
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        Block(g, new Rectangle(r.Left + 18, r.Top + 14, r.Width - 36, 72), "Процессор", cpuNow.Value, "", cpu);
        Block(g, new Rectangle(r.Left + 18, r.Top + 14 + 84, r.Width - 36, 72), "Память", ramNow.Value, ramText, ram);
    }

    void Block(Graphics g, Rectangle r, string label, double value, string extra, Queue<double> samples)
    {
        TextRenderer.DrawText(g, label, Theme.Body, new Point(r.Left, r.Top), C(Theme.Muted));
        if (extra != "")
            TextRenderer.DrawText(g, extra, Theme.Tiny, new Point(r.Left + 76, r.Top + 1), C(Theme.Dim));
        string pct = $"{value:0}%";
        var big = Theme.F(14f, FontStyle.Bold);
        var sz = TextRenderer.MeasureText(pct, big);
        TextRenderer.DrawText(g, pct, big, new Point(r.Right - sz.Width + 6, r.Top - 6), C(value > 85 ? Theme.Warn : Theme.Text));

        var area = new RectangleF(r.Left, r.Top + 26, r.Width, r.Height - 30);
        using (var track = new SolidBrush(Color.FromArgb((int)(18 * Appear), 255, 255, 255)))
            g.FillRectangle(track, area.Left, area.Bottom, area.Width, 1);
        var pts = samples.ToArray();
        if (pts.Length < 2) return;

        float step = area.Width / (N - 1);
        float x0 = area.Right - (pts.Length - 1) * step;
        // минимум 6% высоты, чтобы при простое линия не прилипала к оси
        var line = pts.Select((v, i) => new PointF(x0 + i * step,
            area.Bottom - (float)(Math.Max(6, v) / 100 * area.Height * Appear))).ToArray();

        using var path = new GraphicsPath();
        path.AddLines(line);
        path.AddLine(line[^1], new PointF(line[^1].X, area.Bottom));
        path.AddLine(new PointF(line[^1].X, area.Bottom), new PointF(line[0].X, area.Bottom));
        path.CloseFigure();
        using (var fill = new LinearGradientBrush(new RectangleF(area.X, area.Y - 1, area.Width, area.Height + 2),
                   Color.FromArgb((int)(90 * Appear), Theme.Accent), Color.FromArgb(0, Theme.Accent), 90f))
            g.FillPath(fill, path);
        using (var glow = new Pen(Color.FromArgb((int)(50 * Appear), Theme.Accent), 5f) { LineJoin = LineJoin.Round })
            g.DrawLines(glow, line);
        using var pen = new Pen(C(Theme.Accent), 1.8f) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, line);
        var last = line[^1];
        Theme.Fill(g, Color.FromArgb((int)(80 * Appear), Theme.Accent), new RectangleF(last.X - 6, last.Y - 6, 12, 12), 6);
        Theme.Fill(g, C(Theme.AccentHi), new RectangleF(last.X - 3, last.Y - 3, 6, 6), 3);
    }
}

/// <summary>Свободное место на дисках; полоски растут при появлении.</summary>
class DisksWidget : Widget
{
    readonly List<(string Name, double Used, string Text)> disks;

    public DisksWidget() : this(Load()) { }

    DisksWidget(List<(string, double, string)> d) : base("Диски", 28 + 12 + Math.Max(1, d.Count) * 48 + 4)
    {
        disks = d;
    }

    static List<(string, double, string)> Load()
    {
        var list = new List<(string, double, string)>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                double total = d.TotalSize / 1073741824.0, free = d.AvailableFreeSpace / 1073741824.0;
                string label = d.VolumeLabel == "" ? "Локальный диск" : d.VolumeLabel;
                list.Add(($"{d.Name.TrimEnd('\\')}  {label}", (total - free) / total, $"свободно {free:0} из {total:0} ГБ"));
            }
            catch { }
        }
        return list;
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        for (int i = 0; i < disks.Count; i++)
        {
            var (name, used, text) = disks[i];
            int y = r.Top + 12 + i * 48;
            TextRenderer.DrawText(g, name, Theme.Body, new Point(r.Left + 18, y), C(Theme.Text));
            var ts = TextRenderer.MeasureText(text, Theme.Tiny);
            TextRenderer.DrawText(g, text, Theme.Tiny, new Point(r.Right - 18 - ts.Width, y + 1), C(Theme.Muted));
            var bar = new RectangleF(r.Left + 18, y + 26, r.Width - 36, 6);
            Theme.Fill(g, Color.FromArgb((int)(20 * Appear), 255, 255, 255), bar, 3);
            var col = used > 0.9 ? Theme.Warn : Theme.Accent;
            var fillR = new RectangleF(bar.X, bar.Y, (float)(bar.Width * used * Appear), bar.Height);
            Theme.Glow(g, col, fillR, 3, 2, 1.5f, (int)(60 * Appear));
            Theme.Fill(g, C(col), fillR, 3);
        }
    }
}

/// <summary>Кольцо общего прогресса + полоски по разделам; клик по разделу открывает его.</summary>
class ProgressWidget : Widget
{
    readonly List<(string Cat, int Applied, int Total)> cats;
    public event Action<string>? CategoryClicked;
    int hover = -1;
    const int RowH = 24, RingCol = 150;

    public ProgressWidget(List<(string, int, int)> cats)
        : base("Прогресс настройки", 28 + 16 + Math.Max(140, cats.Count * RowH) + 12)
    {
        this.cats = cats;
    }

    int RowAt(Point p)
    {
        int i = (p.Y - HeadH - 16) / RowH;
        return p.X > RingCol && p.Y > HeadH + 16 && i >= 0 && i < cats.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(false); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(false); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h >= 0) CategoryClicked?.Invoke(cats[h].Cat);
        base.OnMouseClick(e);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        int applied = cats.Sum(c => c.Applied), total = Math.Max(1, cats.Sum(c => c.Total));
        double pct = (double)applied / total;

        const int d = 110;
        var ring = new RectangleF(r.Left + (RingCol - d) / 2f, r.Top + 20, d, d);
        Gauge.Draw(g, ring, pct * Appear, Theme.Accent, Appear, 9);
        var center = Rectangle.Round(ring);
        TextRenderer.DrawText(g, $"{pct * 100 * Appear:0}%", Theme.F(16f, FontStyle.Bold), new Rectangle(center.X, center.Y - 8, center.Width, center.Height),
            C(Theme.Text), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, $"{applied} из {total}", Theme.Tiny, new Rectangle(center.X, center.Y + 17, center.Width, center.Height),
            C(Theme.Muted), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        int x = r.Left + RingCol, w = r.Width - RingCol - 16;
        for (int i = 0; i < cats.Count; i++)
        {
            var (cat, a, t) = cats[i];
            int y = r.Top + 16 + i * RowH;
            if (i == hover) Theme.Fill(g, Color.FromArgb(18, 255, 255, 255), new RectangleF(x - 8, y - 2, w + 16, RowH), 7);
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, cat, Theme.Tiny, new Rectangle(x, y, w / 2 + 6, RowH - 4), C(i == hover ? Theme.Text : Theme.Muted), flags);
            float bx = x + w / 2f + 10, bw = w / 2f - 46;
            Theme.Fill(g, Color.FromArgb((int)(20 * Appear), 255, 255, 255), new RectangleF(bx, y + 8, bw, 4), 2);
            if (t > 0) Theme.Fill(g, C(Theme.Accent), new RectangleF(bx, y + 8, (float)(bw * a / t * Appear), 4), 2);
            TextRenderer.DrawText(g, $"{a}/{t}", Theme.Tiny, new Rectangle((int)(bx + bw + 6), y, 40, RowH - 4), C(Theme.Dim), flags);
        }
    }
}

/// <summary>Кольцевой индикатор со свечением.</summary>
static class Gauge
{
    public static void Draw(Graphics g, RectangleF ring, double value, Color color, double appear, float thick)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var track = new Pen(Color.FromArgb((int)(22 * appear), 255, 255, 255), thick)) g.DrawEllipse(track, ring);
        float sweep = (float)(360 * Math.Clamp(value, 0, 1));
        if (sweep < 0.5f) return;
        for (int i = 3; i >= 1; i--)
        {
            using var glow = new Pen(Color.FromArgb((int)(26 * appear), color), thick + i * 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(glow, ring, -90, sweep);
        }
        using var arc = new Pen(Theme.Lerp(Theme.Bg, color, appear), thick) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(arc, ring, -90, sweep);
    }
}

/// <summary>Плитки действий сеткой 2×N.</summary>
class ActionsWidget : Widget
{
    const int TileH = 60, Gap = 10;

    public ActionsWidget(string title, params ActionTile[] tiles)
        : base(title, 28 + 12 + ((tiles.Length + 1) / 2) * (TileH + Gap) - Gap + 12)
    {
        foreach (var t in tiles) Controls.Add(t);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int w = (Width - 24 - Gap) / 2;
        int i = 0;
        foreach (Control c in Controls)
        {
            c.SetBounds(12 + (i % 2) * (w + Gap), HeadH + 12 + (i / 2) * (TileH + Gap), w, TileH);
            i++;
        }
    }
}

/// <summary>Оценка системы: индекс 0–100 и рекомендации, подобранные под это железо.</summary>
class ScoreWidget : Widget
{
    readonly int score;
    readonly List<Rec> recs;
    int hover = -1;
    public event Action<Rec>? RecClicked;
    const int RowH = 50, RingCol = 170, MaxRows = 5;

    public ScoreWidget(int score, List<Rec> recs)
        : base("Оценка системы", 28 + Math.Max(196, 14 + Math.Max(1, Math.Min(MaxRows, recs.Count)) * RowH + 12))
    {
        this.score = score;
        this.recs = recs.Take(MaxRows).ToList();
    }

    int RowAt(Point p)
    {
        int i = (p.Y - HeadH - 14) / RowH;
        return p.X > RingCol && p.Y > HeadH + 14 && i >= 0 && i < recs.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(false); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(false); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h >= 0) RecClicked?.Invoke(recs[h]);
        base.OnMouseClick(e);
    }

    public static (string Text, Color Color) Grade(int s) =>
        s >= 85 ? ("Отлично", Theme.Good) : s >= 65 ? ("Хорошо", Theme.Accent) : s >= 45 ? ("Есть что улучшить", Theme.Warn) : ("Нужна настройка", Theme.Bad);

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        var (gradeText, gradeColor) = Grade(score);
        const int d = 128;
        var ring = new RectangleF(r.Left + (RingCol - d) / 2f, r.Top + 22, d, d);
        Gauge.Draw(g, ring, score / 100.0 * Appear, gradeColor, Appear, 10);
        var c = Rectangle.Round(ring);
        TextRenderer.DrawText(g, $"{score * Appear:0}", Theme.Big, new Rectangle(c.X, c.Y - 6, c.Width, c.Height),
            C(Theme.Text), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "из 100", Theme.Tiny, new Rectangle(c.X, c.Y + 24, c.Width, c.Height),
            C(Theme.Muted), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, gradeText, Theme.Semi, new Rectangle(r.Left, (int)ring.Bottom + 10, RingCol, 22),
            C(gradeColor), TextFormatFlags.HorizontalCenter);

        int x = r.Left + RingCol, w = r.Width - RingCol - 14;
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        if (recs.Count == 0)
        {
            TextRenderer.DrawText(g, "Рекомендаций нет — система настроена хорошо.", Theme.Body,
                new Rectangle(x, r.Top + 14, w, RowH), C(Theme.Muted), flags);
            return;
        }
        for (int i = 0; i < recs.Count; i++)
        {
            var rec = recs[i];
            int y = r.Top + 14 + i * RowH;
            var row = new RectangleF(x - 8, y, w + 8, RowH - 6);
            if (i == hover) Theme.Fill(g, Color.FromArgb(20, 255, 255, 255), row, 9);
            var col = rec.Severity switch { 2 => Theme.Bad, 1 => Theme.Warn, _ => Theme.Accent };
            var dot = new RectangleF(x + 2, y + 10, 8, 8);
            Theme.Fill(g, Color.FromArgb((int)(70 * Appear), col), RectangleF.Inflate(dot, 3, 3), 7);
            Theme.Fill(g, C(col), dot, 4);
            TextRenderer.DrawText(g, rec.Title, Theme.Semi, new Rectangle(x + 20, y + 3, w - 50, 20), C(Theme.Text), flags);
            TextRenderer.DrawText(g, rec.Detail, Theme.Tiny, new Rectangle(x + 20, y + 22, w - 50, 18), C(Theme.Muted), flags);
            TextRenderer.DrawText(g, "\uE76C", Theme.Icon, new Rectangle(x + w - 26, y, 24, RowH - 6),
                C(i == hover ? Theme.Accent : Theme.Dim), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}

/// <summary>Баннер «Сторожа»: Windows откатила настройки — вернуть одной кнопкой.</summary>
class WatchBanner : Widget
{
    readonly List<Tweak> reverted;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 40 };
    public readonly FlatBtn Fix;

    public WatchBanner(List<Tweak> reverted) : base("", 84)
    {
        this.reverted = reverted;
        Wide = true;
        Edge = Color.FromArgb(120, Theme.Warn);
        Fix = new FlatBtn("\uE777", "Вернуть всё") { Primary = true };
        Controls.Add(Fix);
        timer.Tick += (_, _) => Invalidate(false);
        if (!Motion.Reduced) timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Fix is null) return;
        Fix.Location = new Point(Width - Fix.Width - 14, (Height - Fix.Height) / 2);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        double t = Motion.Reduced ? 0.5 : (Math.Sin(Motion.Now / 420.0) + 1) / 2;
        var icon = new RectangleF(r.Left + 18, r.Top + (r.Height - 40) / 2f, 40, 40);
        Theme.Fill(g, Color.FromArgb((int)((40 + 50 * t) * Appear), Theme.Warn), RectangleF.Inflate(icon, 4 + (float)(3 * t), 4 + (float)(3 * t)), 26);
        Theme.Fill(g, Color.FromArgb((int)(60 * Appear), Theme.Warn), icon, 20);
        TextRenderer.DrawText(g, "\uE7BA", Theme.IconBig, Rectangle.Round(icon), C(Theme.Warn),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        int x = r.Left + 74, w = Width - x - Fix.Width - 30;
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
        TextRenderer.DrawText(g, $"Windows откатила {reverted.Count} {Plural(reverted.Count)}, которые вы включали", Theme.Semi,
            new Rectangle(x, r.Top + 18, w, 22), C(Theme.Text), flags);
        TextRenderer.DrawText(g, string.Join(" · ", reverted.Select(t => t.Name)), Theme.Tiny,
            new Rectangle(x, r.Top + 42, w, 20), C(Theme.Muted), flags);
    }

    public static string Plural(int n)
    {
        int m10 = n % 10, m100 = n % 100;
        return m10 == 1 && m100 != 11 ? "настройку" : m10 is >= 2 and <= 4 && (m100 < 12 || m100 > 14) ? "настройки" : "настроек";
    }
}

/// <summary>Сеанс из истории: что изменилось, кнопка отката для последнего сеанса.</summary>
class SessionWidget : Widget
{
    readonly Journal.Session session;
    const int RowH = 26, MaxRows = 12;
    readonly int shown;

    public SessionWidget(Journal.Session s, FlatBtn? rollback)
        : base(Title(s), 28 + 12 + Math.Min(s.Items.Count, MaxRows) * RowH + (s.Items.Count > MaxRows ? RowH : 0) + (rollback != null ? 58 : 10))
    {
        session = s;
        shown = Math.Min(s.Items.Count, MaxRows);
        if (rollback != null) Controls.Add(rollback);
    }

    static string Title(Journal.Session s) =>
        s.Date.ToString("d MMMM, HH:mm") + $" · изменений: {s.Items.Count}" + (s.RolledBack ? " · откачено" : "");

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        foreach (Control c in Controls) c.Location = new Point(10, Height - c.Height - 6);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        bool dim = session.RolledBack;
        for (int i = 0; i < shown; i++)
        {
            var it = session.Items[i];
            int y = r.Top + 10 + i * RowH;
            string change = it.Irreversible ? "вернуть через Microsoft Store"
                          : (it.WasApplied ? "вкл" : "выкл") + " → " + (it.NowApplied ? "вкл" : "выкл");
            var cw = TextRenderer.MeasureText(change, Theme.Tiny).Width;
            TextRenderer.DrawText(g, it.Name, Theme.Body, new Rectangle(r.Left + 18, y, r.Width - cw - 46, RowH),
                C(dim ? Theme.Dim : Theme.Text), flags);
            TextRenderer.DrawText(g, change, Theme.Tiny, new Rectangle(r.Right - 18 - cw, y, cw + 4, RowH),
                C(it.Irreversible ? Theme.Warn : dim ? Theme.Dim : it.NowApplied ? Theme.Accent : Theme.Muted), flags);
        }
        if (session.Items.Count > MaxRows)
            TextRenderer.DrawText(g, $"и ещё {session.Items.Count - MaxRows}", Theme.Tiny,
                new Rectangle(r.Left + 18, r.Top + 10 + shown * RowH, r.Width - 36, RowH), C(Theme.Dim), flags);
    }
}
