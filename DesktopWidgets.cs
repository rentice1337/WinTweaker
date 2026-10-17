using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace WinTweaker;

/// <summary>
/// Виджет рабочего стола: окно с попиксельной прозрачностью (UpdateLayeredWindow) — скругления и свечение
/// поверх обоев, без рамок. Всегда под остальными окнами, нет в Alt+Tab и на панели задач.
/// Перетаскивается мышью (если не закреплён), правый клик — меню.
/// </summary>
abstract class DesktopWidget : Form
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPOS { public IntPtr hwnd, after; public int x, y, cx, cy; public uint flags; }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pt, ref SIZE sz, IntPtr src, ref POINT srcPt, int key, ref BLEND blend, int flags);

    public string Key { get; }
    public static bool Locked { get; set; }
    protected double Frame;           // 0..1 — плавный переход к новым значениям
    readonly System.Windows.Forms.Timer anim = new() { Interval = 33 };
    double frameStart;
    protected virtual bool Continuous => false; // постоянная анимация (волны)

    protected DesktopWidget(string key, Size size)
    {
        Key = key;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = size;
        anim.Tick += (_, _) => OnAnim();
        Sampler.Updated += OnSample;
        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), Font = Theme.Body };
        menu.Items.Add("Закрепить все виджеты", null, (_, _) => { Locked = !Locked; AppSettings.Set("w.lock", Locked ? "1" : "0"); });
        menu.Items.Add("Скрыть этот виджет", null, (_, _) => Widgets.SetEnabled(Key, false));
        menu.Opening += (_, _) => ((ToolStripMenuItem)menu.Items[0]).Checked = Locked;
        ContextMenuStrip = menu;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00080000 /* LAYERED */ | 0x00000080 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        frameStart = Motion.Now;
        anim.Start();
        Render();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Sampler.Updated -= OnSample;
        anim.Dispose();
        base.OnFormClosed(e);
    }

    void OnSample()
    {
        if (!Visible) return;
        frameStart = Motion.Now;
        if (!anim.Enabled) anim.Start();
    }

    void OnAnim()
    {
        double t = Math.Min(1, (Motion.Now - frameStart) / 450);
        Frame = Motion.Reduced ? 1 : Motion.ExpoOut(t);
        Render();
        if (t >= 1 && !Continuous) anim.Stop();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_WINDOWPOSCHANGING = 0x46, WM_NCHITTEST = 0x84, WM_NCRBUTTONUP = 0xA5, WM_EXITSIZEMOVE = 0x232;
        switch (m.Msg)
        {
            case WM_WINDOWPOSCHANGING:
                // всегда под остальными окнами — как часть рабочего стола
                var wp = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
                wp.after = (IntPtr)1; // HWND_BOTTOM
                wp.flags &= ~0x0004u;  // снять SWP_NOZORDER
                Marshal.StructureToPtr(wp, m.LParam, false);
                break;
            case WM_NCHITTEST:
                base.WndProc(ref m);
                if (m.Result == (IntPtr)1) m.Result = (IntPtr)(Locked ? 1 : 2); // HTCLIENT / HTCAPTION — тянуть за любое место
                return;
            case WM_NCRBUTTONUP:
                ContextMenuStrip?.Show(Cursor.Position);
                return;
            case WM_EXITSIZEMOVE:
                AppSettings.Set("w." + Key, $"{Left},{Top}");
                break;
        }
        base.WndProc(ref m);
    }

    /// <summary>Нарисовать кадр в 32-битную картинку и отдать её окну с альфа-каналом.</summary>
    public void Render()
    {
        if (!IsHandleCreated || IsDisposed) return;
        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            Draw(g);
        }
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
        try
        {
            var size = new SIZE { W = Width, H = Height };
            var src = new POINT();
            var pos = new POINT { X = Left, Y = Top };
            var blend = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
            UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected abstract void Draw(Graphics g);

    // ───────── Общие элементы оформления ─────────

    protected static readonly Color Plate = Color.FromArgb(205, 14, 15, 21);

    protected void Card(Graphics g, RectangleF r, float rad)
    {
        Theme.Glow(g, Theme.Accent, r, rad, 4, 2.2f, 34);
        using (var path = Theme.Round(r, rad))
        {
            using var b = new LinearGradientBrush(r, Color.FromArgb(215, 22, 24, 33), Plate, 90f);
            g.FillPath(b, path);
            using var pen = new Pen(Color.FromArgb(40, 255, 255, 255));
            g.DrawPath(pen, path);
        }
    }

    protected static void Text(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment h = StringAlignment.Near, StringAlignment v = StringAlignment.Near)
    {
        using var b = new SolidBrush(c);
        using var sf = new StringFormat { Alignment = h, LineAlignment = v, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(s, f, b, r, sf);
    }

    protected static double Last(Queue<double> q) => q.Count == 0 ? 0 : q.Last();
}

/// <summary>«Орбита системы»: кольца CPU, ОЗУ и диска C: вокруг часов.</summary>
class OrbitWidget : DesktopWidget
{
    double cpu, ram, disk, cpu0, ram0, disk0;
    static readonly Font clock = new("Segoe UI Semibold", 26f), small = new("Segoe UI", 8.5f), tiny = new("Segoe UI Semibold", 7.5f);

    public OrbitWidget() : base("orbit", new Size(250, 268)) { }

    protected override void Draw(Graphics g)
    {
        if (Frame >= 1 || Frame == 0) { cpu0 = cpu; ram0 = ram; disk0 = disk; }
        double c = Lerp(cpu0, Sampler.Cpu), r = Lerp(ram0, Sampler.Ram), d = Lerp(disk0, Sampler.DiskC);
        cpu = c; ram = r; disk = d;

        var plate = new RectangleF(12, 12, Width - 24, Height - 24);
        Card(g, plate, 28);
        float cx = Width / 2f, cy = 12 + 112;
        Ring(g, cx, cy, 92, c / 100, Theme.Accent, 7);
        Ring(g, cx, cy, 76, r / 100, Color.FromArgb(120, 170, 255), 7);
        Ring(g, cx, cy, 60, d / 100, Color.FromArgb(150, 160, 175), 5);

        Text(g, DateTime.Now.ToString("HH:mm"), clock, Theme.Text, new RectangleF(cx - 60, cy - 30, 120, 44), StringAlignment.Center);
        Text(g, DateTime.Now.ToString("ddd, d MMM"), small, Theme.Muted, new RectangleF(cx - 60, cy + 12, 120, 18), StringAlignment.Center);

        float y = Height - 52, w = (Width - 48) / 3f;
        Legend(g, 24, y, w, "CPU", c, Theme.Accent);
        Legend(g, 24 + w, y, w, "ОЗУ", r, Color.FromArgb(120, 170, 255));
        Legend(g, 24 + w * 2, y, w, "C:", d, Color.FromArgb(150, 160, 175));
    }

    double Lerp(double a, double b) => a + (b - a) * Frame;

    static void Ring(Graphics g, float cx, float cy, float r, double v, Color c, float th)
    {
        var rect = new RectangleF(cx - r, cy - r, r * 2, r * 2);
        using (var track = new Pen(Color.FromArgb(28, 255, 255, 255), th)) g.DrawEllipse(track, rect);
        float sweep = (float)(360 * Math.Clamp(v, 0, 1));
        if (sweep < 1) return;
        for (int i = 3; i >= 1; i--)
        {
            using var glow = new Pen(Color.FromArgb(22, c), th + i * 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(glow, rect, -90, sweep);
        }
        using var arc = new Pen(c, th) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(arc, rect, -90, sweep);
        // светящаяся «планета» на конце дуги
        double a = (-90 + sweep) * Math.PI / 180;
        float px = cx + (float)(r * Math.Cos(a)), py = cy + (float)(r * Math.Sin(a));
        Theme.Fill(g, Color.FromArgb(90, c), new RectangleF(px - th, py - th, th * 2, th * 2), th);
        Theme.Fill(g, Color.White, new RectangleF(px - th / 2.6f, py - th / 2.6f, th / 1.3f, th / 1.3f), th);
    }

    static void Legend(Graphics g, float x, float y, float w, string label, double v, Color c)
    {
        Theme.Fill(g, c, new RectangleF(x + 6, y + 6, 7, 7), 3.5f);
        Text(g, label, tiny, Theme.Muted, new RectangleF(x + 16, y + 1, w - 16, 16));
        Text(g, $"{v:0}%", small, Theme.Text, new RectangleF(x + 16, y + 15, w - 16, 18));
    }
}

/// <summary>«Жидкая» память: уровень ОЗУ — жидкость с двумя волнами в стеклянной колбе.</summary>
class LiquidWidget : DesktopWidget
{
    double level = -1;
    static readonly Font big = new("Segoe UI Semibold", 24f), small = new("Segoe UI", 8.5f);
    protected override bool Continuous => !Motion.Reduced;

    public LiquidWidget() : base("liquid", new Size(200, 222)) { }

    protected override void Draw(Graphics g)
    {
        double target = Sampler.Ram / 100;
        level = level < 0 ? target : level + (target - level) * 0.08; // мягкое «доливание»

        var plate = new RectangleF(12, 12, Width - 24, Height - 24);
        Card(g, plate, 26);
        float d = 140, cx = Width / 2f, cy = 12 + 18 + d / 2;
        var circle = new RectangleF(cx - d / 2, cy - d / 2, d, d);
        using var clip = new GraphicsPath();
        clip.AddEllipse(circle);

        Theme.Fill(g, Color.FromArgb(26, 255, 255, 255), circle, d / 2);
        var state = g.Save();
        g.SetClip(clip);
        float surface = circle.Bottom - (float)(d * level);
        double t = Motion.Now / 1000.0;
        var col = level > 0.85 ? Theme.Warn : Theme.Accent;
        Wave(g, circle, surface, 5, 0.045, t * 1.6, Color.FromArgb(90, col));
        Wave(g, circle, surface + 3, 6, 0.035, -t * 1.1 + 1.7, Color.FromArgb(200, col));
        // пузырьки
        for (int i = 0; i < 5; i++)
        {
            double ph = (t * 0.25 + i * 0.37) % 1;
            float bx = circle.X + d * (0.2f + 0.15f * i), by = circle.Bottom - (float)(ph * (circle.Bottom - surface));
            if (by > surface + 6) Theme.Fill(g, Color.FromArgb((int)(90 * (1 - ph)), 255, 255, 255), new RectangleF(bx, by, 4, 4), 2);
        }
        g.Restore(state);
        using (var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1.5f)) g.DrawEllipse(pen, circle);
        // блик стекла
        using (var hl = new Pen(Color.FromArgb(70, 255, 255, 255), 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(hl, RectangleF.Inflate(circle, -9, -9), 200, 50);

        Text(g, $"{Sampler.Ram:0}%", big, Color.White, new RectangleF(cx - 60, cy - 26, 120, 44), StringAlignment.Center);
        Text(g, "ОЗУ", small, Color.FromArgb(220, 255, 255, 255), new RectangleF(cx - 60, cy + 16, 120, 18), StringAlignment.Center);
        Text(g, $"{Sampler.RamUsedGb:0.0} из {Sampler.RamTotalGb:0.0} ГБ", small, Theme.Muted,
            new RectangleF(20, Height - 44, Width - 40, 18), StringAlignment.Center);
    }

    static void Wave(Graphics g, RectangleF box, float y, float amp, double freq, double phase, Color c)
    {
        var pts = new List<PointF>();
        for (float x = box.Left - 2; x <= box.Right + 2; x += 3)
            pts.Add(new PointF(x, y + (float)(Math.Sin(x * freq + phase) * amp)));
        pts.Add(new PointF(box.Right + 2, box.Bottom + 2));
        pts.Add(new PointF(box.Left - 2, box.Bottom + 2));
        using var b = new LinearGradientBrush(new RectangleF(box.X, y - amp, box.Width, box.Bottom - y + amp + 1),
            c, Color.FromArgb(Math.Min(255, c.A + 40), Theme.Lerp(c, Color.Black, 0.35)), 90f);
        g.FillPolygon(b, pts.ToArray());
    }
}

/// <summary>Сеть: скорость загрузки и отдачи, график за минуту, пинг до 1.1.1.1.</summary>
class NetWidget : DesktopWidget
{
    static readonly Font big = new("Segoe UI Semibold", 15f), small = new("Segoe UI", 8.5f), tiny = new("Segoe UI Semibold", 7.5f);
    static readonly Color Down = Color.FromArgb(90, 200, 255);

    public NetWidget() : base("net", new Size(300, 176)) { }

    protected override void Draw(Graphics g)
    {
        var plate = new RectangleF(12, 12, Width - 24, Height - 24);
        Card(g, plate, 22);
        Text(g, "↓ ЗАГРУЗКА", tiny, Down, new RectangleF(30, 26, 120, 14));
        Text(g, Sampler.Speed(Sampler.DownBps), big, Theme.Text, new RectangleF(28, 38, 130, 28));
        Text(g, "↑ ОТДАЧА", tiny, Theme.Accent, new RectangleF(160, 26, 120, 14));
        Text(g, Sampler.Speed(Sampler.UpBps), big, Theme.Text, new RectangleF(158, 38, 120, 28));

        var chart = new RectangleF(30, 76, Width - 60, 52);
        double max = Math.Max(64 * 1024, Math.Max(Sampler.DownHist.DefaultIfEmpty(0).Max(), Sampler.UpHist.DefaultIfEmpty(0).Max()) * 1.15);
        Line(g, chart, Sampler.DownHist, max, Down);
        Line(g, chart, Sampler.UpHist, max, Theme.Accent);

        string ping = Sampler.PingMs is int p ? $"пинг {p} мс" : "пинг —";
        var pc = Sampler.PingMs is int q ? (q < 40 ? Theme.Good : q < 100 ? Theme.Warn : Theme.Bad) : Theme.Muted;
        Theme.Fill(g, pc, new RectangleF(30, Height - 38, 7, 7), 3.5f);
        Text(g, ping, small, Theme.Muted, new RectangleF(42, Height - 44, 140, 18));
    }

    static void Line(Graphics g, RectangleF r, Queue<double> q, double max, Color c)
    {
        var v = q.ToArray();
        if (v.Length < 2) return;
        float step = r.Width / (Sampler.History - 1), x0 = r.Right - (v.Length - 1) * step;
        var pts = v.Select((x, i) => new PointF(x0 + i * step, r.Bottom - (float)(x / max * r.Height))).ToArray();
        using (var path = new GraphicsPath())
        {
            path.AddLines(pts);
            path.AddLine(pts[^1], new PointF(pts[^1].X, r.Bottom));
            path.AddLine(new PointF(pts[^1].X, r.Bottom), new PointF(pts[0].X, r.Bottom));
            using var fill = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Color.FromArgb(70, c), Color.FromArgb(0, c), 90f);
            g.FillPath(fill, path);
        }
        using var pen = new Pen(c, 1.6f) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, pts);
    }
}

/// <summary>Топ процессов по памяти: иконка, имя, полоска, объём.</summary>
class TopWidget : DesktopWidget
{
    static readonly Font name = new("Segoe UI", 9f), tiny = new("Segoe UI Semibold", 7.5f), val = new("Segoe UI Semibold", 8.5f);

    public TopWidget() : base("top", new Size(300, 232)) { }

    protected override void Draw(Graphics g)
    {
        var plate = new RectangleF(12, 12, Width - 24, Height - 24);
        Card(g, plate, 22);
        Text(g, "ПАМЯТЬ ЗАНИМАЮТ", tiny, Theme.Muted, new RectangleF(30, 26, 200, 14));
        var top = Sampler.Top;
        long max = top.Count == 0 ? 1 : Math.Max(1, top[0].Bytes);
        for (int i = 0; i < top.Count; i++)
        {
            var (n, bytes, icon) = top[i];
            float y = 48 + i * 32;
            if (icon != null) g.DrawImage(icon, new RectangleF(30, y + 2, 18, 18));
            else Theme.Fill(g, Color.FromArgb(50, Theme.Accent), new RectangleF(30, y + 2, 18, 18), 5);
            Text(g, n, name, Theme.Text, new RectangleF(56, y, 150, 18));
            string mb = bytes >= 1L << 30 ? $"{bytes / 1073741824.0:0.0} ГБ" : $"{bytes / 1048576.0:0} МБ";
            Text(g, mb, val, Theme.Muted, new RectangleF(Width - 110, y, 80, 18), StringAlignment.Far);
            var bar = new RectangleF(56, y + 20, Width - 86, 3);
            Theme.Fill(g, Color.FromArgb(26, 255, 255, 255), bar, 1.5f);
            float w = (float)(bar.Width * bytes / max * Math.Max(Frame, 0.0001));
            Theme.Fill(g, i == 0 ? Theme.Accent : Theme.Lerp(Theme.Accent, Color.FromArgb(150, 160, 175), 0.5), new RectangleF(bar.X, bar.Y, Math.Max(3, w), 3), 1.5f);
        }
        if (top.Count == 0) Text(g, "Собираю данные…", name, Theme.Muted, new RectangleF(30, 60, 220, 20));
    }
}

/// <summary>Управление виджетами: включение, позиции, общий выключатель.</summary>
static class Widgets
{
    public static readonly (string Key, string Name, string Desc, Func<DesktopWidget> Make)[] All =
    {
        ("orbit", "Орбита системы", "Кольца CPU, ОЗУ и диска C: вокруг часов", () => new OrbitWidget()),
        ("liquid", "Жидкая память", "Уровень ОЗУ — жидкость с волнами", () => new LiquidWidget()),
        ("net", "Сеть", "Скорость загрузки/отдачи и пинг", () => new NetWidget()),
        ("top", "Топ процессов", "Кто занимает больше всего памяти", () => new TopWidget()),
    };

    static readonly Dictionary<string, DesktopWidget> open = new();
    public static event Action? Changed;

    public static bool Master
    {
        get => AppSettings.Get("widgets", "0") == "1";
        set { AppSettings.Set("widgets", value ? "1" : "0"); Apply(); }
    }

    public static bool IsEnabled(string key) => AppSettings.Get("w." + key + ".on", key is "orbit" or "liquid" ? "1" : "0") == "1";

    public static void SetEnabled(string key, bool on)
    {
        AppSettings.Set("w." + key + ".on", on ? "1" : "0");
        if (on && !Master) AppSettings.Set("widgets", "1");
        Apply();
    }

    public static void Init()
    {
        DesktopWidget.Locked = AppSettings.Get("w.lock", "0") == "1";
        Apply();
    }

    public static void Apply()
    {
        int i = 0;
        foreach (var (key, _, _, make) in All)
        {
            bool want = Master && IsEnabled(key);
            open.TryGetValue(key, out var w);
            if (want && w == null)
            {
                w = make();
                w.Location = Position(key, w.Size, i);
                open[key] = w;
                w.FormClosed += (_, _) => open.Remove(key);
                w.Show();
            }
            else if (!want && w != null)
            {
                w.Close();
                open.Remove(key);
            }
            i++;
        }
        Changed?.Invoke();
    }

    public static void CloseAll()
    {
        foreach (var w in open.Values.ToList()) w.Close();
        open.Clear();
    }

    /// <summary>Сохранённая позиция или столбик у правого края рабочего стола.</summary>
    static Point Position(string key, Size size, int index)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        var saved = AppSettings.Get("w." + key, "").Split(',');
        if (saved.Length == 2 && int.TryParse(saved[0], out int x) && int.TryParse(saved[1], out int y)
            && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(x, y, size.Width, size.Height))))
            return new Point(x, y);
        int top = area.Top + 16 + index * 236;
        return new Point(area.Right - size.Width - 16, Math.Min(top, area.Bottom - size.Height));
    }
}
