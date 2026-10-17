using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace WinTweaker;

/// <summary>
/// Виджет рабочего стола: окно с попиксельной прозрачностью (UpdateLayeredWindow) — скругления и свечение
/// поверх обоев, без рамок. Всегда под остальными окнами, нет в Alt+Tab и на панели задач.
/// Перетаскивается мышью (если не закреплён), правый клик — меню.
///
/// Производительность: все виджеты живут в собственном потоке (см. <see cref="Widgets"/>) — их отрисовка
/// никогда не отнимает кадры у главного окна. Кадр рисуется прямо в постоянную DIB-секцию (без выделения
/// картинок и копий на каждый кадр), фон с неоновым свечением кэшируется. Пока на переднем плане
/// полноэкранное приложение (игра, видео), виджеты не перерисовываются вовсе.
/// </summary>
abstract class DesktopWidget : Form
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPOS { public IntPtr hwnd, after; public int x, y, cx, cy; public uint flags; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, IntPtr pptDst, ref SIZE sz, IntPtr src, ref POINT srcPt, int key, ref BLEND blend, int flags);

    public string Key { get; }
    public bool Small { get; }
    public static bool Locked { get; set; }

    /// <summary>Снимок показателей для текущего кадра.</summary>
    protected Snap S = Snap.Empty;
    protected double Frame = 1;       // 0..1 — плавный переход к новым значениям
    readonly System.Windows.Forms.Timer anim = new();
    double frameStart;
    bool paused;
    protected virtual bool Continuous => false; // постоянная анимация (волны)

    // постоянная поверхность кадра
    IntPtr memDC, dib, oldBmp;
    Bitmap? surface;
    Graphics? gfx;
    // кэш фона
    Bitmap? bg;
    Color bgAccent;

    static Font? menuFont;

    protected DesktopWidget(string key, bool small)
    {
        Key = key;
        Small = small;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = SizeFor(small);
        anim.Interval = Continuous ? 40 : 33;
        anim.Tick += (_, _) => OnAnim();

        // свой шрифт: объекты GDI+ нельзя делить между потоками
        menuFont ??= new Font("Segoe UI", 9.75f);
        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), Font = menuFont };
        var compact = new ToolStripMenuItem("Компактный вид", null, (_, _) => Widgets.SetCompact(Key, !Small)) { Checked = Small };
        var lockItem = new ToolStripMenuItem("Закрепить все виджеты", null, (_, _) => { Locked = !Locked; AppSettings.Set("w.lock", Locked ? "1" : "0"); });
        menu.Items.Add(compact);
        menu.Items.Add(lockItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Скрыть этот виджет", null, (_, _) => Widgets.SetEnabled(Key, false));
        menu.Opening += (_, _) => lockItem.Checked = Locked;
        ContextMenuStrip = menu;
    }

    protected abstract Size SizeFor(bool small);
    protected float Inset => Small ? 10 : 12;
    protected virtual float Radius => Small ? 20 : 26;
    protected RectangleF Plate => new(Inset, Inset, Width - Inset * 2, Height - Inset * 2);

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
        S = Sampler.Current;
        frameStart = Motion.Now;
        Render();
        if (Continuous) anim.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        anim.Dispose();
        FreeSurface();
        bg?.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>Новый снимок (в потоке виджетов). paused — на переднем плане полноэкранное приложение.</summary>
    public void OnSample(Snap snap, bool fullscreen)
    {
        if (!Visible || IsDisposed) return;
        S = snap;
        if (fullscreen)
        {
            if (!paused) { paused = true; anim.Stop(); }
            return;
        }
        if (paused) { paused = false; if (Continuous) anim.Start(); }
        frameStart = Motion.Now;
        Frame = 0;
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

    void EnsureSurface()
    {
        if (surface != null && surface.Width == Width && surface.Height == Height) return;
        FreeSurface();
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = Width, biHeight = -Height, biPlanes = 1, biBitCount = 32 };
        IntPtr screen = GetDC(IntPtr.Zero);
        memDC = CreateCompatibleDC(screen);
        ReleaseDC(IntPtr.Zero, screen);
        dib = CreateDIBSection(memDC, ref bi, 0, out var bits, IntPtr.Zero, 0);
        oldBmp = SelectObject(memDC, dib);
        // GDI+ рисует прямо в память DIB, в формате с premultiplied alpha — именно его ждёт UpdateLayeredWindow
        surface = new Bitmap(Width, Height, Width * 4, PixelFormat.Format32bppPArgb, bits);
        gfx = Graphics.FromImage(surface);
        gfx.SmoothingMode = SmoothingMode.AntiAlias;
        gfx.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    void FreeSurface()
    {
        gfx?.Dispose(); gfx = null;
        surface?.Dispose(); surface = null;
        if (memDC != IntPtr.Zero)
        {
            SelectObject(memDC, oldBmp);
            DeleteObject(dib);
            DeleteDC(memDC);
            memDC = dib = IntPtr.Zero;
        }
    }

    /// <summary>Фон с неоновым свечением — дорогой в отрисовке, поэтому рисуется один раз на цвет акцента.</summary>
    Bitmap Background()
    {
        if (bg != null && bgAccent == Theme.Accent && bg.Size == Size) return bg;
        bg?.Dispose();
        bg = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        bgAccent = Theme.Accent;
        using var g = Graphics.FromImage(bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        Card(g, Plate, Radius);
        return bg;
    }

    /// <summary>Нарисовать кадр и отдать его окну с альфа-каналом.</summary>
    public void Render()
    {
        if (!IsHandleCreated || IsDisposed || paused) return;
        try
        {
            EnsureSurface();
            var g = gfx!;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(Background(), 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;
            Draw(g);
            g.Flush(FlushIntention.Sync);

            var size = new SIZE { W = Width, H = Height };
            var src = new POINT();
            var blend = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, ref size, memDC, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        catch { /* виджет не должен ронять программу */ }
    }

    protected abstract void Draw(Graphics g);

    // ───────── Общие элементы оформления ─────────

    static readonly Color PlateColor = Color.FromArgb(205, 14, 15, 21);

    static void Card(Graphics g, RectangleF r, float rad)
    {
        Theme.Glow(g, Theme.Accent, r, rad, 4, 2.2f, 34);
        using var path = Theme.Round(r, rad);
        using (var b = new LinearGradientBrush(r, Color.FromArgb(215, 22, 24, 33), PlateColor, 90f)) g.FillPath(b, path);
        using var pen = new Pen(Color.FromArgb(40, 255, 255, 255));
        g.DrawPath(pen, path);
    }

    protected static void Text(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment h = StringAlignment.Near, StringAlignment v = StringAlignment.Near)
    {
        using var b = new SolidBrush(c);
        using var sf = new StringFormat { Alignment = h, LineAlignment = v, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(s, f, b, r, sf);
    }

    protected static Color PingColor(int? p) => p is int q ? (q < 40 ? Theme.Good : q < 100 ? Theme.Warn : Theme.Bad) : Theme.Muted;

    protected static string Mb(long bytes) => bytes >= 1L << 30 ? $"{bytes / 1073741824.0:0.0} ГБ" : $"{bytes / 1048576.0:0} МБ";

    protected static void Area(Graphics g, RectangleF r, double[] v, double max, Color c, float width = 1.6f)
    {
        if (v.Length < 2) return;
        float step = r.Width / (Sampler.History - 1), x0 = r.Right - (v.Length - 1) * step;
        var pts = new PointF[v.Length];
        for (int i = 0; i < v.Length; i++) pts[i] = new PointF(x0 + i * step, r.Bottom - (float)(Math.Min(1, v[i] / max) * r.Height));
        using (var path = new GraphicsPath())
        {
            path.AddLines(pts);
            path.AddLine(pts[^1], new PointF(pts[^1].X, r.Bottom));
            path.AddLine(new PointF(pts[^1].X, r.Bottom), new PointF(pts[0].X, r.Bottom));
            using var fill = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Color.FromArgb(70, c), Color.FromArgb(0, c), 90f);
            g.FillPath(fill, path);
        }
        using var pen = new Pen(c, width) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, pts);
    }
}

/// <summary>«Орбита системы»: кольца CPU, ОЗУ и диска C: вокруг часов.</summary>
class OrbitWidget : DesktopWidget
{
    double cpu, ram, disk, cpu0, ram0, disk0;
    static readonly Font clock = new("Segoe UI Semibold", 26f), clockS = new("Segoe UI Semibold", 15f),
        small = new("Segoe UI", 8.5f), tiny = new("Segoe UI Semibold", 7.5f), tinyS = new("Segoe UI Semibold", 7f);
    static readonly Color RamC = Color.FromArgb(120, 170, 255), DiskC = Color.FromArgb(150, 160, 175);

    public OrbitWidget(bool small) : base("orbit", small) { }
    protected override Size SizeFor(bool small) => small ? new Size(164, 178) : new Size(250, 268);
    protected override float Radius => Small ? 22 : 28;

    protected override void Draw(Graphics g)
    {
        if (Frame >= 1 || Frame == 0) { cpu0 = cpu; ram0 = ram; disk0 = disk; }
        double c = Lerp(cpu0, S.Cpu), r = Lerp(ram0, S.Ram), d = Lerp(disk0, S.DiskC);
        cpu = c; ram = r; disk = d;
        float cx = Width / 2f;

        if (Small)
        {
            float cy = 10 + 68;
            Ring(g, cx, cy, 58, c / 100, Theme.Accent, 5, 2);
            Ring(g, cx, cy, 48, r / 100, RamC, 5, 2);
            Ring(g, cx, cy, 38, d / 100, DiskC, 4, 1);
            Text(g, DateTime.Now.ToString("HH:mm"), clockS, Theme.Text, new RectangleF(cx - 40, cy - 16, 80, 26), StringAlignment.Center);
            Text(g, DateTime.Now.ToString("ddd, d MMM"), tinyS, Theme.Muted, new RectangleF(cx - 40, cy + 8, 80, 14), StringAlignment.Center);
            float y = Height - 40, w = (Width - 32) / 3f;
            Chip(g, 16, y, w, c, Theme.Accent);
            Chip(g, 16 + w, y, w, r, RamC);
            Chip(g, 16 + w * 2, y, w, d, DiskC);
            return;
        }

        float cy2 = 12 + 112;
        Ring(g, cx, cy2, 92, c / 100, Theme.Accent, 7, 3);
        Ring(g, cx, cy2, 76, r / 100, RamC, 7, 3);
        Ring(g, cx, cy2, 60, d / 100, DiskC, 5, 2);
        Text(g, DateTime.Now.ToString("HH:mm"), clock, Theme.Text, new RectangleF(cx - 60, cy2 - 30, 120, 44), StringAlignment.Center);
        Text(g, DateTime.Now.ToString("ddd, d MMM"), small, Theme.Muted, new RectangleF(cx - 60, cy2 + 12, 120, 18), StringAlignment.Center);

        float yy = Height - 52, ww = (Width - 48) / 3f;
        Legend(g, 24, yy, ww, "CPU", c, Theme.Accent);
        Legend(g, 24 + ww, yy, ww, "ОЗУ", r, RamC);
        Legend(g, 24 + ww * 2, yy, ww, "C:", d, DiskC);
    }

    double Lerp(double a, double b) => a + (b - a) * Frame;

    static void Ring(Graphics g, float cx, float cy, float r, double v, Color c, float th, int glowLayers)
    {
        var rect = new RectangleF(cx - r, cy - r, r * 2, r * 2);
        using (var track = new Pen(Color.FromArgb(28, 255, 255, 255), th)) g.DrawEllipse(track, rect);
        float sweep = (float)(360 * Math.Clamp(v, 0, 1));
        if (sweep < 1) return;
        for (int i = glowLayers; i >= 1; i--)
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

    static void Chip(Graphics g, float x, float y, float w, double v, Color c)
    {
        Theme.Fill(g, c, new RectangleF(x + 6, y + 6, 6, 6), 3);
        Text(g, $"{v:0}%", tiny, Theme.Text, new RectangleF(x + 14, y + 1, w - 14, 16));
    }
}

/// <summary>«Жидкая» память: уровень ОЗУ — жидкость с двумя волнами в стеклянной колбе.</summary>
class LiquidWidget : DesktopWidget
{
    double level = -1;
    static readonly Font big = new("Segoe UI Semibold", 24f), bigS = new("Segoe UI Semibold", 15f),
        small = new("Segoe UI", 8.5f), tiny = new("Segoe UI", 7.5f);
    protected override bool Continuous => !Motion.Reduced;

    public LiquidWidget(bool small) : base("liquid", small) { }
    protected override Size SizeFor(bool small) => small ? new Size(136, 148) : new Size(200, 222);

    protected override void Draw(Graphics g)
    {
        double target = S.Ram / 100;
        level = level < 0 ? target : level + (target - level) * 0.08; // мягкое «доливание»

        float d = Small ? 88 : 140, cx = Width / 2f, cy = Inset + (Small ? 10 : 18) + d / 2;
        var circle = new RectangleF(cx - d / 2, cy - d / 2, d, d);
        using var clip = new GraphicsPath();
        clip.AddEllipse(circle);

        Theme.Fill(g, Color.FromArgb(26, 255, 255, 255), circle, d / 2);
        var state = g.Save();
        g.SetClip(clip);
        float surface = circle.Bottom - (float)(d * level);
        double t = Motion.Now / 1000.0;
        var col = level > 0.85 ? Theme.Warn : Theme.Accent;
        float amp = Small ? 3.5f : 5;
        Wave(g, circle, surface, amp, Small ? 0.07 : 0.045, t * 1.6, Color.FromArgb(90, col));
        Wave(g, circle, surface + 3, amp + 1, Small ? 0.055 : 0.035, -t * 1.1 + 1.7, Color.FromArgb(200, col));
        // пузырьки
        for (int i = 0; i < 5; i++)
        {
            double ph = (t * 0.25 + i * 0.37) % 1;
            float bx = circle.X + d * (0.2f + 0.15f * i), by = circle.Bottom - (float)(ph * (circle.Bottom - surface));
            float bs = Small ? 3 : 4;
            if (by > surface + 6) Theme.Fill(g, Color.FromArgb((int)(90 * (1 - ph)), 255, 255, 255), new RectangleF(bx, by, bs, bs), bs / 2);
        }
        g.Restore(state);
        using (var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1.5f)) g.DrawEllipse(pen, circle);
        // блик стекла
        using (var hl = new Pen(Color.FromArgb(70, 255, 255, 255), Small ? 2f : 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(hl, RectangleF.Inflate(circle, Small ? -6 : -9, Small ? -6 : -9), 200, 50);

        if (Small)
        {
            Text(g, $"{S.Ram:0}%", bigS, Color.White, new RectangleF(cx - 40, cy - 16, 80, 26), StringAlignment.Center);
            Text(g, "ОЗУ", tiny, Color.FromArgb(220, 255, 255, 255), new RectangleF(cx - 40, cy + 9, 80, 14), StringAlignment.Center);
            Text(g, $"{S.RamUsedGb:0.0} / {S.RamTotalGb:0} ГБ", tiny, Theme.Muted, new RectangleF(14, Height - 34, Width - 28, 16), StringAlignment.Center);
            return;
        }
        Text(g, $"{S.Ram:0}%", big, Color.White, new RectangleF(cx - 60, cy - 26, 120, 44), StringAlignment.Center);
        Text(g, "ОЗУ", small, Color.FromArgb(220, 255, 255, 255), new RectangleF(cx - 60, cy + 16, 120, 18), StringAlignment.Center);
        Text(g, $"{S.RamUsedGb:0.0} из {S.RamTotalGb:0.0} ГБ", small, Theme.Muted, new RectangleF(20, Height - 44, Width - 40, 18), StringAlignment.Center);
    }

    static void Wave(Graphics g, RectangleF box, float y, float amp, double freq, double phase, Color c)
    {
        int n = (int)((box.Width + 4) / 3) + 3;
        var pts = new PointF[n + 2];
        for (int i = 0; i < n; i++)
        {
            float x = box.Left - 2 + i * 3;
            pts[i] = new PointF(x, y + (float)(Math.Sin(x * freq + phase) * amp));
        }
        pts[n] = new PointF(box.Right + 4, box.Bottom + 2);
        pts[n + 1] = new PointF(box.Left - 2, box.Bottom + 2);
        using var b = new LinearGradientBrush(new RectangleF(box.X, y - amp, box.Width, box.Bottom - y + amp + 1),
            c, Color.FromArgb(Math.Min(255, c.A + 40), Theme.Lerp(c, Color.Black, 0.35)), 90f);
        g.FillPolygon(b, pts);
    }
}

/// <summary>Сеть: скорость загрузки и отдачи, график за минуту, пинг до 1.1.1.1.</summary>
class NetWidget : DesktopWidget
{
    static readonly Font big = new("Segoe UI Semibold", 15f), mid = new("Segoe UI Semibold", 9.5f),
        small = new("Segoe UI", 8.5f), tiny = new("Segoe UI Semibold", 7.5f);
    static readonly Color Down = Color.FromArgb(90, 200, 255);

    public NetWidget(bool small) : base("net", small) { }
    protected override Size SizeFor(bool small) => small ? new Size(224, 96) : new Size(300, 176);
    protected override float Radius => Small ? 18 : 22;

    protected override void Draw(Graphics g)
    {
        double max = Math.Max(64 * 1024, Math.Max(S.DownHist.DefaultIfEmpty(0).Max(), S.UpHist.DefaultIfEmpty(0).Max()) * 1.15);
        string ping = S.PingMs is int p ? $"{p} мс" : "—";

        if (Small)
        {
            Text(g, "↓", mid, Down, new RectangleF(22, 19, 14, 18));
            Text(g, Sampler.Speed(S.DownBps), mid, Theme.Text, new RectangleF(36, 19, 82, 18));
            Text(g, "↑", mid, Theme.Accent, new RectangleF(22, 38, 14, 18));
            Text(g, Sampler.Speed(S.UpBps), mid, Theme.Text, new RectangleF(36, 38, 82, 18));
            Theme.Fill(g, PingColor(S.PingMs), new RectangleF(25, 63, 6, 6), 3);
            Text(g, "пинг " + ping, tiny, Theme.Muted, new RectangleF(36, 58, 82, 16));
            var chart = new RectangleF(122, 24, Width - 122 - 22, Height - 24 - 22);
            Area(g, chart, S.DownHist, max, Down, 1.3f);
            Area(g, chart, S.UpHist, max, Theme.Accent, 1.3f);
            return;
        }

        Text(g, "↓ ЗАГРУЗКА", tiny, Down, new RectangleF(30, 26, 120, 14));
        Text(g, Sampler.Speed(S.DownBps), big, Theme.Text, new RectangleF(28, 38, 130, 28));
        Text(g, "↑ ОТДАЧА", tiny, Theme.Accent, new RectangleF(160, 26, 120, 14));
        Text(g, Sampler.Speed(S.UpBps), big, Theme.Text, new RectangleF(158, 38, 120, 28));
        var chart2 = new RectangleF(30, 76, Width - 60, 52);
        Area(g, chart2, S.DownHist, max, Down);
        Area(g, chart2, S.UpHist, max, Theme.Accent);
        Theme.Fill(g, PingColor(S.PingMs), new RectangleF(30, Height - 38, 7, 7), 3.5f);
        Text(g, "пинг " + ping, small, Theme.Muted, new RectangleF(42, Height - 44, 140, 18));
    }
}

/// <summary>Топ процессов по памяти: иконка, имя, полоска, объём.</summary>
class TopWidget : DesktopWidget
{
    static readonly Font name = new("Segoe UI", 9f), nameS = new("Segoe UI", 8.25f),
        tiny = new("Segoe UI Semibold", 7.5f), val = new("Segoe UI Semibold", 8.5f), valS = new("Segoe UI Semibold", 7.75f);

    readonly double[] shown = { -1, -1, -1, -1, -1 };

    public TopWidget(bool small) : base("top", small) { }
    protected override Size SizeFor(bool small) => small ? new Size(228, 140) : new Size(300, 232);
    protected override float Radius => Small ? 18 : 22;

    protected override void Draw(Graphics g)
    {
        var top = S.Top;
        long max = top.Count == 0 ? 1 : Math.Max(1, top[0].Bytes);
        int rows = Small ? Math.Min(3, top.Count) : top.Count;
        float left = Small ? 22 : 30, rowH = Small ? 28 : 32, y0 = Small ? 36 : 48, ic = Small ? 14 : 18;
        Text(g, Small ? "ПАМЯТЬ" : "ПАМЯТЬ ЗАНИМАЮТ", tiny, Theme.Muted, new RectangleF(left, Small ? 19 : 26, 200, 14));
        for (int i = 0; i < rows; i++)
        {
            var (n, bytes, icon) = top[i];
            float y = y0 + i * rowH;
            if (icon != null) g.DrawImage(icon, new RectangleF(left, y + 2, ic, ic));
            else Theme.Fill(g, Color.FromArgb(50, Theme.Accent), new RectangleF(left, y + 2, ic, ic), 5);
            float tx = left + ic + 8;
            Text(g, n, Small ? nameS : name, Theme.Text, new RectangleF(tx, y + (Small ? 1 : 0), Width - tx - 90, 18));
            Text(g, Mb(bytes), Small ? valS : val, Theme.Muted, new RectangleF(Width - left - 80, y + (Small ? 1 : 0), 80, 18), StringAlignment.Far);
            var bar = new RectangleF(tx, y + (Small ? 19 : 20), Width - tx - left, Small ? 2 : 3);
            Theme.Fill(g, Color.FromArgb(26, 255, 255, 255), bar, 1.5f);
            // полоски плавно перетекают к новым значениям, а не вырастают с нуля каждую секунду
            double target = (double)bytes / max;
            if (shown[i] < 0) shown[i] = target;
            shown[i] += (target - shown[i]) * (Motion.Reduced ? 1 : 0.22);
            float w = (float)(bar.Width * shown[i]);
            Theme.Fill(g, i == 0 ? Theme.Accent : Theme.Lerp(Theme.Accent, Color.FromArgb(150, 160, 175), 0.5), new RectangleF(bar.X, bar.Y, Math.Max(3, w), bar.Height), 1.5f);
        }
        if (top.Count == 0) Text(g, "Собираю данные…", name, Theme.Muted, new RectangleF(left, y0 + 8, 220, 20));
    }
}

/// <summary>
/// Управление виджетами. Все окна виджетов создаются и живут в отдельном STA-потоке со своим
/// циклом сообщений: главное окно и виджеты не мешают друг другу кадрами.
/// Публичные методы можно звать из любого потока.
/// </summary>
static class Widgets
{
    public static readonly (string Key, string Name, string Desc, Func<bool, DesktopWidget> Make)[] All =
    {
        ("orbit", "Орбита системы", "Кольца CPU, ОЗУ и диска C: вокруг часов", s => new OrbitWidget(s)),
        ("liquid", "Жидкая память", "Уровень ОЗУ — жидкость с волнами", s => new LiquidWidget(s)),
        ("net", "Сеть", "Скорость загрузки/отдачи и пинг", s => new NetWidget(s)),
        ("top", "Топ процессов", "Кто занимает больше всего памяти", s => new TopWidget(s)),
    };

    static readonly Dictionary<string, DesktopWidget> open = new(); // только поток виджетов
    static Thread? thread;
    static Control? pump;
    static volatile bool suspended;

    public static bool Master
    {
        get => AppSettings.Get("widgets", "0") == "1";
        set { AppSettings.Set("widgets", value ? "1" : "0"); Apply(); }
    }

    public static bool IsEnabled(string key) => AppSettings.Get("w." + key + ".on", key is "orbit" or "liquid" ? "1" : "0") == "1";
    public static bool IsCompact(string key) => AppSettings.Get("w." + key + ".small", "0") == "1";

    public static void SetEnabled(string key, bool on)
    {
        AppSettings.Set("w." + key + ".on", on ? "1" : "0");
        if (on && !Master) AppSettings.Set("widgets", "1");
        Apply();
    }

    /// <summary>Компактный вид одного виджета (окно пересоздаётся на том же месте).</summary>
    public static void SetCompact(string key, bool small)
    {
        AppSettings.Set("w." + key + ".small", small ? "1" : "0");
        Post(() =>
        {
            if (open.Remove(key, out var w)) w.Close();
            ApplyCore();
        });
    }

    /// <summary>Все виджеты разом: обычные или компактные.</summary>
    public static bool AllCompact
    {
        get => All.All(w => IsCompact(w.Key));
        set
        {
            foreach (var w in All) AppSettings.Set("w." + w.Key + ".small", value ? "1" : "0");
            Post(() =>
            {
                foreach (var w in open.Values.ToList()) w.Close();
                open.Clear();
                ApplyCore();
            });
        }
    }

    /// <summary>Временно убрать все виджеты (игровой режим «Не беспокоить»), не меняя настроек.</summary>
    public static bool Suspended
    {
        get => suspended;
        set { if (suspended == value) return; suspended = value; Apply(); }
    }

    public static void Init()
    {
        DesktopWidget.Locked = AppSettings.Get("w.lock", "0") == "1";
        Sampler.Updated += () =>
        {
            var snap = Sampler.Current;
            if (pump == null) return;
            Post(() =>
            {
                if (open.Count == 0) return;
                bool fs = FullscreenApp.Active();
                foreach (var w in open.Values) w.OnSample(snap, fs);
            });
        };
        Apply();
    }

    public static void Apply()
    {
        if (thread == null && !(Master && !suspended)) return; // поток не нужен, пока нет виджетов
        Post(ApplyCore);
    }

    public static void CloseAll()
    {
        if (thread == null) return;
        Post(() =>
        {
            foreach (var w in open.Values.ToList()) w.Close();
            open.Clear();
        });
    }

    static void Post(Action a)
    {
        EnsureThread();
        try { pump!.BeginInvoke(a); } catch { }
    }

    static void EnsureThread()
    {
        if (thread != null) return;
        using var ready = new ManualResetEventSlim();
        thread = new Thread(() =>
        {
            pump = new Control();
            pump.CreateControl();
            _ = pump.Handle;
            ready.Set();
            Application.Run();
        }) { IsBackground = true, Name = "WinTweak widgets" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
    }

    static void ApplyCore()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        int nextTop = area.Top + 16;
        foreach (var (key, _, _, make) in All)
        {
            bool want = Master && IsEnabled(key) && !suspended;
            bool small = IsCompact(key);
            open.TryGetValue(key, out var w);
            if (w != null && (!want || w.Small != small))
            {
                w.Close();
                open.Remove(key);
                w = null;
            }
            if (want && w == null)
            {
                w = make(small);
                w.Location = Position(key, w.Size, area, nextTop);
                open[key] = w;
                var self = w;
                w.FormClosed += (_, _) => { if (open.TryGetValue(key, out var cur) && cur == self) open.Remove(key); };
                w.Show();
            }
            if (want) nextTop += (w?.Height ?? 0) + 4;
        }
    }

    /// <summary>Сохранённая позиция или столбик у правого края рабочего стола.</summary>
    static Point Position(string key, Size size, Rectangle area, int top)
    {
        var saved = AppSettings.Get("w." + key, "").Split(',');
        if (saved.Length == 2 && int.TryParse(saved[0], out int x) && int.TryParse(saved[1], out int y)
            && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(x, y, size.Width, size.Height))))
            return new Point(x, y);
        return new Point(area.Right - size.Width - 16, Math.Min(top, area.Bottom - size.Height));
    }
}

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
