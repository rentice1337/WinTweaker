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

    static string? Hklm(string key, string name)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            return k?.GetValue(name)?.ToString();
        }
        catch { return null; }
    }

    public static string Os()
    {
        const string K = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        string name = Hklm(K, "ProductName") ?? "Windows";
        string build = Hklm(K, "CurrentBuild") ?? "?";
        // в Windows 11 ProductName по-прежнему «Windows 10»
        if (int.TryParse(build, out int b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
        return $"{name} {Hklm(K, "DisplayVersion") ?? Hklm(K, "ReleaseId") ?? ""}".Trim();
    }

    public static string Build()
    {
        const string K = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        string ubr = Hklm(K, "UBR") ?? "";
        return (Hklm(K, "CurrentBuild") ?? "?") + (ubr != "" ? "." + ubr : "") + (Environment.Is64BitOperatingSystem ? " · x64" : " · x86");
    }

    public static string CpuName() =>
        (Hklm(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Trim() ?? "—")
        + $" · {Environment.ProcessorCount} потоков";

    public static string Gpu()
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
        return names.Count == 0 ? "—" : string.Join(", ", names);
    }

    public static string Uptime()
    {
        var t = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return t.Days > 0 ? $"{t.Days} д {t.Hours} ч {t.Minutes} мин" : $"{t.Hours} ч {t.Minutes} мин";
    }
}

/// <summary>Базовый виджет дашборда: заголовок и карточка, проявляется вместе с остальными.</summary>
abstract class Widget : FadeIn
{
    protected const int Head = 28;

    protected Widget(string title, int height)
    {
        Text = title;
        Height = height;
        BackColor = Theme.Card;
    }

    protected Rectangle Body => new(0, Head, Width - 1, Height - Head - 1);
    protected Color C(Color c) => Theme.Fade(this, c);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        TextRenderer.DrawText(g, Text.ToUpperInvariant(), Theme.Small, new Point(4, 7), C(Theme.Muted));
        Theme.Fill(g, C(Theme.Card), Body, 10);
        PaintBody(g, Body);
    }

    protected abstract void PaintBody(Graphics g, Rectangle r);
}

/// <summary>Список «параметр — значение».</summary>
class InfoWidget : Widget
{
    readonly (string Key, string Value)[] rows;

    public InfoWidget(string title, params (string, string)[] rows) : base(title, Head + 14 + rows.Length * 27 + 10)
    {
        this.rows = rows;
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        for (int i = 0; i < rows.Length; i++)
        {
            int y = r.Top + 12 + i * 27;
            TextRenderer.DrawText(g, rows[i].Key, Theme.Body, new Rectangle(r.Left + 16, y, 110, 24), C(Theme.Muted), flags);
            TextRenderer.DrawText(g, rows[i].Value, Theme.Body, new Rectangle(r.Left + 128, y, r.Width - 144, 24), C(Theme.Text), flags);
        }
    }
}

/// <summary>Живая нагрузка CPU и ОЗУ: число плавно догоняет новое значение, под ним спарклайн за минуту.</summary>
class LiveWidget : Widget
{
    const int N = 60;
    readonly Queue<double> cpu = new(), ram = new();
    readonly AnimFloat cpuNow, ramNow;
    string ramText = "";

    public LiveWidget() : base("Нагрузка", Head + 12 + 2 * 78 + 8)
    {
        cpuNow = new AnimFloat(0, () => Invalidate());
        ramNow = new AnimFloat(0, () => Invalidate());
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
        Block(g, new Rectangle(r.Left + 16, r.Top + 12, r.Width - 32, 70), "Процессор", cpuNow.Value, "", cpu);
        Block(g, new Rectangle(r.Left + 16, r.Top + 12 + 78, r.Width - 32, 70), "Память", ramNow.Value, ramText, ram);
    }

    void Block(Graphics g, Rectangle r, string label, double value, string extra, Queue<double> samples)
    {
        TextRenderer.DrawText(g, label, Theme.Body, new Point(r.Left, r.Top), C(Theme.Muted));
        if (extra != "")
            TextRenderer.DrawText(g, extra, Theme.Body, new Point(r.Left + 80, r.Top), C(Theme.Dim));
        string pct = $"{value:0}%";
        var big = Theme.F(13f, FontStyle.Bold);
        var sz = TextRenderer.MeasureText(pct, big);
        TextRenderer.DrawText(g, pct, big, new Point(r.Right - sz.Width + 4, r.Top - 4), C(value > 85 ? Theme.Warn : Theme.Text));

        var area = new RectangleF(r.Left, r.Top + 26, r.Width, r.Height - 30);
        using (var track = new SolidBrush(C(Theme.Hover))) g.FillRectangle(track, area.Left, area.Bottom, area.Width, 1);
        var pts = samples.ToArray();
        if (pts.Length < 2) return;

        float step = area.Width / (N - 1);
        float x0 = area.Right - (pts.Length - 1) * step;
        var line = pts.Select((v, i) => new PointF(x0 + i * step, area.Bottom - (float)(v / 100 * area.Height * Appear))).ToArray();

        using var path = new GraphicsPath();
        path.AddLines(line);
        path.AddLine(line[^1], new PointF(line[^1].X, area.Bottom));
        path.AddLine(new PointF(line[^1].X, area.Bottom), new PointF(line[0].X, area.Bottom));
        path.CloseFigure();
        using (var fill = new LinearGradientBrush(new RectangleF(area.X, area.Y - 1, area.Width, area.Height + 2),
                   Color.FromArgb((int)(70 * Appear), Theme.Accent), Color.FromArgb(0, Theme.Accent), 90f))
            g.FillPath(fill, path);
        using var pen = new Pen(C(Theme.Accent), 1.6f) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, line);
    }
}

/// <summary>Свободное место на дисках; полоски растут при появлении.</summary>
class DisksWidget : Widget
{
    readonly List<(string Name, double Used, string Text)> disks;

    public DisksWidget() : this(Load()) { }

    DisksWidget(List<(string, double, string)> d) : base("Диски", Head + 12 + Math.Max(1, d.Count) * 46 + 4)
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
            int y = r.Top + 12 + i * 46;
            TextRenderer.DrawText(g, name, Theme.Body, new Point(r.Left + 16, y), C(Theme.Text));
            var ts = TextRenderer.MeasureText(text, Theme.Body);
            TextRenderer.DrawText(g, text, Theme.Body, new Point(r.Right - 16 - ts.Width, y), C(Theme.Muted));
            var bar = new RectangleF(r.Left + 16, y + 24, r.Width - 32, 6);
            Theme.Fill(g, C(Theme.Off), bar, 3);
            Theme.Fill(g, C(used > 0.9 ? Theme.Warn : Theme.Accent), new RectangleF(bar.X, bar.Y, (float)(bar.Width * used * Appear), bar.Height), 3);
        }
    }
}

/// <summary>Кольцо общего прогресса + полоски по разделам; клик по разделу открывает его.</summary>
class ProgressWidget : Widget
{
    readonly List<(string Cat, int Applied, int Total)> cats;
    public event Action<string>? CategoryClicked;
    int hover = -1;
    const int RowH = 23, RingCol = 140;

    public ProgressWidget(List<(string, int, int)> cats)
        : base("Прогресс настройки", Head + 16 + Math.Max(130, cats.Count * RowH) + 12)
    {
        this.cats = cats;
    }

    int RowAt(Point p)
    {
        int i = (p.Y - Head - 16) / RowH;
        return p.X > RingCol && p.Y > Head + 16 && i >= 0 && i < cats.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

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

        // кольцо
        const int d = 104;
        var ring = new RectangleF(r.Left + (RingCol - d) / 2f, r.Top + 18, d, d);
        using (var track = new Pen(C(Theme.Off), 9)) g.DrawEllipse(track, ring);
        float sweep = (float)(360 * pct * Appear);
        if (sweep > 0.5f)
        {
            using var arc = new Pen(C(Theme.Accent), 9) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arc, ring, -90, sweep);
        }
        var center = Rectangle.Round(ring);
        TextRenderer.DrawText(g, $"{pct * 100 * Appear:0}%", Theme.F(15f, FontStyle.Bold), new Rectangle(center.X, center.Y - 8, center.Width, center.Height),
            C(Theme.Text), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, $"{applied} из {total}", Theme.F(8.25f), new Rectangle(center.X, center.Y + 16, center.Width, center.Height),
            C(Theme.Muted), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        // разделы
        int x = r.Left + RingCol, w = r.Width - RingCol - 16;
        for (int i = 0; i < cats.Count; i++)
        {
            var (cat, a, t) = cats[i];
            int y = r.Top + 16 + i * RowH;
            if (i == hover) Theme.Fill(g, C(Theme.Hover), new RectangleF(x - 8, y - 2, w + 16, RowH), 6);
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, cat, Theme.F(8.75f), new Rectangle(x, y, w / 2, RowH - 4), C(i == hover ? Theme.Text : Theme.Muted), flags);
            float bx = x + w / 2f + 4, bw = w / 2f - 40;
            Theme.Fill(g, C(Theme.Off), new RectangleF(bx, y + 8, bw, 4), 2);
            if (t > 0) Theme.Fill(g, C(Theme.Accent), new RectangleF(bx, y + 8, (float)(bw * a / t * Appear), 4), 2);
            TextRenderer.DrawText(g, $"{a}/{t}", Theme.F(8.25f), new Rectangle((int)(bx + bw + 4), y, 40, RowH - 4), C(Theme.Dim), flags);
        }
    }
}

/// <summary>Быстрые действия — кнопки в одну колонку.</summary>
class ActionsWidget : Widget
{
    public ActionsWidget(string title, params FlatBtn[] buttons) : base(title, Head + 12 + buttons.Length * 38 + 8)
    {
        foreach (var b in buttons) Controls.Add(b);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int y = Head + 12;
        foreach (Control c in Controls)
        {
            c.Location = new Point(12, y);
            y += 38;
        }
    }

    protected override void PaintBody(Graphics g, Rectangle r) { }
}

/// <summary>Сеанс из истории: что изменилось, кнопка отката для последнего сеанса.</summary>
class SessionWidget : Widget
{
    readonly Journal.Session session;
    const int RowH = 24, MaxRows = 12;
    readonly int shown;

    public SessionWidget(Journal.Session s, FlatBtn? rollback)
        : base(Title(s), Head + 12 + Math.Min(s.Items.Count, MaxRows) * RowH + (s.Items.Count > MaxRows ? RowH : 0) + (rollback != null ? 46 : 10))
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
        foreach (Control c in Controls) c.Location = new Point(12, Height - 42);
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
            var cw = TextRenderer.MeasureText(change, Theme.F(8.5f)).Width;
            TextRenderer.DrawText(g, it.Name, Theme.Body, new Rectangle(r.Left + 16, y, r.Width - cw - 44, RowH),
                C(dim ? Theme.Dim : Theme.Text), flags);
            TextRenderer.DrawText(g, change, Theme.F(8.5f), new Rectangle(r.Right - 16 - cw, y, cw + 4, RowH),
                C(it.Irreversible ? Theme.Warn : dim ? Theme.Dim : Theme.Muted), flags);
        }
        if (session.Items.Count > MaxRows)
            TextRenderer.DrawText(g, $"и ещё {session.Items.Count - MaxRows}", Theme.F(8.5f),
                new Rectangle(r.Left + 16, r.Top + 10 + shown * RowH, r.Width - 32, RowH), C(Theme.Dim), flags);
    }
}
