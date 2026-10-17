using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace WinTweaker;

/// <summary>
/// «Стекло» в духе Mica: под окном — размытые обои рабочего стола, затемнение и неоновые пятна акцента.
/// Картинка готовится один раз (и после перемещения окна), дальше каждый контрол рисует свой кусок
/// через TextureBrush — это дёшево. Слои поверх: боковая панель → карточка → строка с подсветкой.
/// Если в Windows выключены «Эффекты прозрачности», обои не используются — остаются тёмный фон и свечение.
/// </summary>
static class Glass
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SystemParametersInfo(uint action, uint param, StringBuilder value, uint winIni);

    public static readonly Color BgTint = Color.FromArgb(206, 9, 10, 15);
    public static readonly Color SideTint = Color.FromArgb(120, 6, 7, 11);
    public static readonly Color CardTint = Color.FromArgb(118, 32, 34, 47);
    public static readonly Color TileTint = Color.FromArgb(14, 255, 255, 255);
    public static readonly Color HoverTint = Color.FromArgb(20, 255, 255, 255);
    public static readonly Color Border = Color.FromArgb(26, 255, 255, 255);

    static Bitmap? backdrop;   // высота 3H: [кадр, отражённый кадр, кадр] — период 2H, любой кусок ≤ H вырезается одним блитом
    static int frameH;
    static Form? host;

    public static bool WallpaperOn { get; private set; }

    public static void Attach(Form f)
    {
        host = f;
        Rebuild();
        f.ResizeEnd += (_, _) => { Rebuild(); f.Invalidate(true); };
        SystemEvents.UserPreferenceChanged += OnPrefs;
        f.FormClosed += (_, _) => SystemEvents.UserPreferenceChanged -= OnPrefs;
    }

    static void OnPrefs(object? s, UserPreferenceChangedEventArgs e)
    {
        var h = host;
        if (h == null || h.IsDisposed || !h.IsHandleCreated) return;
        if (e.Category is UserPreferenceCategory.Desktop or UserPreferenceCategory.General or UserPreferenceCategory.Color)
            h.BeginInvoke(() => { Rebuild(); h.Invalidate(true); });
    }

    /// <summary>Пересобрать фон (после перемещения окна, смены обоев или акцента).</summary>
    public static void Rebuild()
    {
        if (host == null || host.Width < 50 || host.Height < 50) return;
        bool transparency = !Equals(Reg.Get(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency"), 0);
        WallpaperOn = transparency && AppSettings.Get("glass", "1") == "1";

        var size = host.Size;
        using var frame = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Theme.Bg);
            if (WallpaperOn)
            {
                try { DrawWallpaper(g, size, host.Bounds); } catch { /* нет обоев — остаётся тёмный фон */ }
            }
            using (var tint = new SolidBrush(BgTint)) g.FillRectangle(tint, 0, 0, size.Width, size.Height);
            Blob(g, new PointF(size.Width * 0.22f, -size.Height * 0.10f), size.Width * 0.55f, Color.FromArgb(56, Theme.Accent));
            Blob(g, new PointF(size.Width * 1.02f, size.Height * 1.05f), size.Width * 0.50f, Color.FromArgb(34, Theme.Accent));
            Blob(g, new PointF(size.Width * 0.70f, size.Height * 0.30f), size.Width * 0.35f, Color.FromArgb(12, 120, 140, 255));
        }

        var tall = new Bitmap(size.Width, size.Height * 3, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(tall))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(frame, 0, 0);
            using (var flipped = (Bitmap)frame.Clone())
            {
                flipped.RotateFlip(RotateFlipType.RotateNoneFlipY);
                g.DrawImageUnscaled(flipped, 0, size.Height);
            }
            g.DrawImageUnscaled(frame, 0, size.Height * 2);
        }
        var old = backdrop;
        backdrop = tall;
        frameH = size.Height;
        old?.Dispose();
        Spotlight.ResetAssets();
    }

    static void Blob(Graphics g, PointF c, float r, Color col)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(c.X - r, c.Y - r, r * 2, r * 2);
        using var b = new PathGradientBrush(path)
        {
            CenterColor = col,
            SurroundColors = new[] { Color.FromArgb(0, col) }
        };
        g.FillPath(b, path);
    }

    static void DrawWallpaper(Graphics g, Size size, Rectangle window)
    {
        using var wp = LoadWallpaper();
        if (wp == null) return;
        var screen = Screen.FromRectangle(window).Bounds;

        // обои в режиме «Заполнение»: масштаб, при котором картинка покрывает экран
        float scale = Math.Max((float)screen.Width / wp.Width, (float)screen.Height / wp.Height);
        float ox = screen.X + (screen.Width - wp.Width * scale) / 2;
        float oy = screen.Y + (screen.Height - wp.Height * scale) / 2;
        var src = new RectangleF((window.X - ox) / scale, (window.Y - oy) / scale, size.Width / scale, size.Height / scale);

        // размытие: уменьшаем в ~24 раза и растягиваем обратно бикубикой
        int sw = Math.Max(4, size.Width / 24), sh = Math.Max(4, size.Height / 24);
        using var small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb);
        using (var sg = Graphics.FromImage(small))
        {
            sg.Clear(Theme.Bg);
            sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
            sg.DrawImage(wp, new Rectangle(0, 0, sw, sh), src, GraphicsUnit.Pixel);
        }
        using var ia = new ImageAttributes();
        ia.SetWrapMode(WrapMode.TileFlipXY); // без тёмной каймы по краям
        ia.SetColorMatrix(Saturation(0.8f));
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(small, new Rectangle(0, 0, size.Width, size.Height), 0, 0, sw, sh, GraphicsUnit.Pixel, ia);
    }

    static ColorMatrix Saturation(float s)
    {
        const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
        float a = (1 - s);
        return new ColorMatrix(new[]
        {
            new[] { a * lr + s, a * lr, a * lr, 0, 0 },
            new[] { a * lg, a * lg + s, a * lg, 0, 0 },
            new[] { a * lb, a * lb, a * lb + s, 0, 0 },
            new float[] { 0, 0, 0, 1, 0 },
            new float[] { 0, 0, 0, 0, 1 }
        });
    }

    static Image? LoadWallpaper()
    {
        try
        {
            // TranscodedWallpaper — актуальные обои, даже при слайд-шоу
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Themes\TranscodedWallpaper");
            if (!File.Exists(path))
            {
                var sb = new StringBuilder(520);
                path = SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, 520, sb, 0) ? sb.ToString() : "";
            }
            if (path == "" || !File.Exists(path)) return null;
            return Image.FromStream(new MemoryStream(File.ReadAllBytes(path)));
        }
        catch { return null; }
    }

    // ───────── Рисование фона под контролом ─────────

    /// <summary>Залить контрол фоном окна со всеми слоями под ним.</summary>
    public static void Back(Graphics g, Control c)
    {
        var form = c.FindForm();
        if (backdrop == null || form == null || form != host)
        {
            g.Clear(SolidOf(c));
            return;
        }

        // рисуем только то, что реально перерисовывается
        var clip = Rectangle.Ceiling(g.VisibleClipBounds);
        clip.Intersect(new Rectangle(0, 0, c.Width, c.Height));
        if (clip.Width <= 0 || clip.Height <= 0) return;

        var o = Origin(c, form);
        int period = frameH * 2;
        int sy = ((o.Y + clip.Y) % period + period) % period;
        int sx = Math.Clamp(o.X + clip.X, 0, Math.Max(0, backdrop.Width - clip.Width));
        int h = Math.Min(clip.Height, backdrop.Height - sy);
        var state = g.Save();
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.None;
        g.DrawImage(backdrop, new Rectangle(clip.X, clip.Y, clip.Width, h), new Rectangle(sx, sy, clip.Width, h), GraphicsUnit.Pixel);
        g.Restore(state);

        // слои над фоном
        for (var p = c; p != null; p = p.Parent)
        {
            if (p is SidePanel) { Overlay(g, c, SideTint); break; }
        }
        var surface = Ancestor<Surface>(c);
        if (surface != null)
        {
            Overlay(g, c, Scale(CardTint, surface.Appear));
            surface.PaintGlowUnder(g, c);
        }
        var row = Ancestor<TweakRow>(c);
        if (row != null) Overlay(g, c, row.Overlay);
    }

    static void Overlay(Graphics g, Control c, Color col)
    {
        if (col.A == 0) return;
        using var b = new SolidBrush(col);
        g.FillRectangle(b, g.VisibleClipBounds);
    }

    public static Color Scale(Color c, double k) => Color.FromArgb((int)Math.Clamp(c.A * k, 0, 255), c);

    /// <summary>Ближайший предок типа T (сам контрол не учитывается).</summary>
    static T? Ancestor<T>(Control c) where T : Control
    {
        for (var p = c.Parent; p != null; p = p.Parent)
            if (p is T t) return t;
        return null;
    }

    /// <summary>Позиция контрола в окне. Холст прокрутки не учитывается — фон «приклеен» к странице.</summary>
    static Point Origin(Control c, Form f)
    {
        int x = 0, y = 0;
        for (var p = c; p != null && p != f; p = p.Parent)
        {
            x += p.Left;
            if (p is not Canvas) y += p.Top;
        }
        return new Point(x, y);
    }

    static Color SolidOf(Control c)
    {
        for (var p = c.Parent; p != null; p = p.Parent)
            if (p.BackColor.A == 255) return p.BackColor;
        return Theme.Card;
    }
}

/// <summary>Подсветка рамок карточек вокруг курсора («прожектор»).</summary>
static class Spotlight
{
    public static Point Cursor { get; private set; } = new(-10000, -10000);
    static readonly List<Surface> surfaces = new();
    static readonly HashSet<Surface> lit = new();
    public const int Reach = 90, R = 200;

    static Bitmap? soft, edge;
    static Color edgeFor;

    /// <summary>Мягкий белый блик для тела карточки.</summary>
    public static Bitmap Soft => soft ??= Radial(Color.FromArgb(16, 255, 255, 255));

    /// <summary>Свечение рамки цветом акцента (пересоздаётся при смене акцента).</summary>
    public static Bitmap Edge
    {
        get
        {
            if (edge == null || edgeFor != Theme.Accent)
            {
                edge?.Dispose();
                edge = Radial(Color.FromArgb(240, Theme.Accent));
                edgeFor = Theme.Accent;
            }
            return edge;
        }
    }

    public static void ResetAssets()
    {
        edge?.Dispose();
        edge = null;
    }

    static Bitmap Radial(Color center)
    {
        var bmp = new Bitmap(R * 2, R * 2, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        using var path = new GraphicsPath();
        path.AddEllipse(0, 0, R * 2, R * 2);
        using var b = new PathGradientBrush(path) { CenterColor = center, SurroundColors = new[] { Color.FromArgb(0, center) } };
        g.FillPath(b, path);
        return bmp;
    }

    public static bool AnyLit => lit.Count > 0;

    public static void Register(Surface s) => surfaces.Add(s);

    public static void Unregister(Surface s)
    {
        surfaces.Remove(s);
        lit.Remove(s);
    }

    /// <summary>Выключается в «Настройках».</summary>
    public static bool Enabled { get; set; } = true;

    public static void Move(Point screen)
    {
        if (!Enabled) return;
        if (Math.Abs(screen.X - Cursor.X) + Math.Abs(screen.Y - Cursor.Y) < 2) return;
        Cursor = screen;
        if (Motion.Reduced) return;
        foreach (var s in surfaces.ToArray())
        {
            if (s.IsDisposed || !s.Visible || !s.IsHandleCreated) continue;
            var r = s.RectangleToScreen(s.ClientRectangle);
            r.Inflate(Reach, Reach);
            bool near = r.Contains(screen);
            if (near || lit.Contains(s))
            {
                // карточки с кнопками перерисовываются целиком (блик есть и под кнопками), списки — только рамка
                s.Invalidate(s.LightsChildren);
                if (near) lit.Add(s); else lit.Remove(s);
            }
        }
    }
}

/// <summary>Кто умеет прокручиваться колесом мыши.</summary>
interface IWheelTarget
{
    bool CanScroll { get; }
    void Wheel(int delta);
}

/// <summary>Колесо мыши уходит тому, кто под курсором, а не тому, у кого фокус.</summary>
sealed class WheelFilter : IMessageFilter
{
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != 0x020A) return false; // WM_MOUSEWHEEL
        var pt = System.Windows.Forms.Cursor.Position;
        var under = Control.FromChildHandle(WindowFromPoint(new POINT { X = pt.X, Y = pt.Y }));
        for (var c = under; c != null; c = c.Parent)
        {
            if (c is IWheelTarget t && t.CanScroll)
            {
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                t.Wheel(delta);
                return true;
            }
        }
        return false;
    }
}

/// <summary>Холст с содержимым: прокрутка = сдвиг холста целиком (одно окно — без мерцания).</summary>
class Canvas : Control
{
    sealed class Ghost
    {
        public Control Target = null!;
        public Bitmap Bmp = null!;
        public double Delay;
        public double A;
    }

    readonly List<Ghost> ghosts = new();
    Tween? anim;
    double start;
    const double Dur = 420, Rise = 16;

    public Canvas() { SetStyle(Theme.Smooth, true); }

    /// <summary>
    /// Каскадное появление: каждая карточка снимается в картинку, прячется, а холст рисует снимки
    /// с нарастающей прозрачностью и подъёмом. Дочерние окна в это время не трогаются — отсюда плавность.
    /// </summary>
    public void Reveal(IList<Control> items, double stagger)
    {
        Finish();
        if (Motion.Reduced) return;
        int i = 0;
        foreach (var c in items)
        {
            if (c.Width <= 0 || c.Height <= 0 || c.Top > Height) continue;
            var bmp = new Bitmap(c.Width, c.Height, PixelFormat.Format32bppPArgb);
            try { c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height)); }
            catch { bmp.Dispose(); continue; }
            ghosts.Add(new Ghost { Target = c, Bmp = bmp, Delay = i++ * stagger });
            c.Visible = false;
        }
        if (ghosts.Count == 0) return;
        start = Motion.Now;
        double total = Dur + ghosts.Max(g => g.Delay);
        anim = Motion.Run(total, _ => Step(), x => x, 0, Finish);
    }

    void Step()
    {
        if (IsDisposed) return;
        double now = Motion.Now - start;
        var dirty = Rectangle.Empty;
        foreach (var g in ghosts.ToArray())
        {
            double t = Math.Clamp((now - g.Delay) / Dur, 0, 1);
            if (t <= 0) continue;
            g.A = Motion.ExpoOut(t);
            var r = new Rectangle(g.Target.Left, g.Target.Top, g.Bmp.Width, g.Bmp.Height + (int)Rise + 1);
            dirty = dirty.IsEmpty ? r : Rectangle.Union(dirty, r);
            if (t >= 1)
            {
                ghosts.Remove(g);
                g.Target.Visible = true;
                g.Bmp.Dispose();
            }
        }
        if (!dirty.IsEmpty) Invalidate(dirty, false);
    }

    void Finish()
    {
        anim?.Cancel();
        anim = null;
        foreach (var g in ghosts)
        {
            if (!g.Target.IsDisposed) g.Target.Visible = true;
            g.Bmp.Dispose();
        }
        ghosts.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Finish();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Glass.Back(e.Graphics, this);
        if (ghosts.Count == 0) return;
        var g = e.Graphics;
        using var ia = new ImageAttributes();
        foreach (var gh in ghosts)
        {
            if (gh.A <= 0.004) continue;
            int y = gh.Target.Top + (int)Math.Round((1 - gh.A) * Rise);
            var dest = new Rectangle(gh.Target.Left, y, gh.Bmp.Width, gh.Bmp.Height);
            if (!dest.IntersectsWith(e.ClipRectangle)) continue;
            if (gh.A >= 0.995) { g.DrawImageUnscaled(gh.Bmp, dest.Location); continue; }
            ia.SetColorMatrix(new ColorMatrix { Matrix33 = (float)gh.A });
            g.DrawImage(gh.Bmp, dest, 0, 0, gh.Bmp.Width, gh.Bmp.Height, GraphicsUnit.Pixel, ia);
        }
    }
}

/// <summary>Окно прокрутки без полосы: плавная прокрутка колесом с инерцией expo-out.</summary>
class Viewport : Control, IWheelTarget
{
    public readonly Canvas Canvas;
    readonly AnimFloat pos;
    double target;
    int contentHeight;

    public Viewport() : this(new Canvas()) { }

    public Viewport(Canvas canvas)
    {
        SetStyle(Theme.Smooth, true);
        Canvas = canvas;
        Controls.Add(Canvas);
        pos = new AnimFloat(0, () => { Canvas.Top = -(int)Math.Round(pos!.Value); OnScrolled(); });
    }

    int Max => Math.Max(0, contentHeight - Height);
    public bool AtStart => target <= 0.5;
    public bool AtEnd => target >= Max - 0.5;

    /// <summary>После каждого шага прокрутки.</summary>
    protected virtual void OnScrolled() { }
    public bool CanScroll => Max > 0;
    public int Offset => (int)Math.Round(target);

    public void SetContentHeight(int h)
    {
        contentHeight = h;
        Canvas.SetBounds(0, Canvas.Top, Width, Math.Max(h, Height));
        if (target > Max) { target = Max; pos.Set(target); }
    }

    public void Wheel(int delta)
    {
        target = Math.Clamp(target - delta / 120.0 * 120, 0, Max);
        pos.To(target, 360);
    }

    public void ScrollTo(int y)
    {
        target = Math.Clamp(y, 0, Max);
        pos.To(target, 480);
    }

    public void Reset()
    {
        target = 0;
        pos.Set(0);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Canvas.SetBounds(0, Canvas.Top, Width, Math.Max(contentHeight, Height));
        if (target > Max) { target = Max; pos.Set(target); }
    }

    protected override void OnPaint(PaintEventArgs e) => Glass.Back(e.Graphics, this);
}
