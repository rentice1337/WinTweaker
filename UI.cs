using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WinTweaker;

// ═══════════════════════════════════════════════════════════════
//  Движок анимаций: твины на общем таймере, физичные кривые,
//  учёт системной настройки «Показывать анимацию в Windows».
// ═══════════════════════════════════════════════════════════════

static class Motion
{
    [DllImport("user32.dll")]
    static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint winIni);

    /// <summary>true, если в Windows отключены анимации (Параметры → Специальные возможности → Дисплей).</summary>
    public static readonly bool Reduced = !QueryAnimations();

    static bool QueryAnimations()
    {
        bool v = true;
        try { SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, ref v, 0); } catch { }
        return v;
    }

    /// <summary>expo-out — для появлений и откликов.</summary>
    public static double ExpoOut(double t) => t >= 1 ? 1 : 1 - Math.Pow(2, -10 * t);

    /// <summary>cubic in-out — для плавных акцентов и исчезновений.</summary>
    public static double InOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    static readonly Stopwatch clock = Stopwatch.StartNew();
    static readonly List<Tween> tweens = new();
    static readonly System.Windows.Forms.Timer timer = new() { Interval = 8 };

    static Motion() => timer.Tick += (_, _) => Tick();

    public static double Now => clock.Elapsed.TotalMilliseconds;

    public static Tween Run(double ms, Action<double> step, Func<double, double>? ease = null, double delay = 0, Action? done = null)
    {
        var tw = new Tween(step, ease ?? ExpoOut, Reduced ? 0 : ms, Reduced ? 0 : delay, done, Now);
        if (tw.Duration <= 0 && tw.Delay <= 0)
        {
            step(1);
            tw.Finished = true;
            done?.Invoke();
            return tw;
        }
        tweens.Add(tw);
        timer.Start();
        return tw;
    }

    static void Tick()
    {
        double now = Now;
        foreach (var tw in tweens.ToArray())
        {
            if (tw.Finished) { tweens.Remove(tw); continue; }
            double elapsed = now - tw.Start - tw.Delay;
            if (elapsed < 0) continue;
            double p = tw.Duration <= 0 ? 1 : Math.Min(1, elapsed / tw.Duration);
            tw.Step(tw.Ease(p));
            if (p >= 1)
            {
                tw.Finished = true;
                tweens.Remove(tw);
                tw.Done?.Invoke();
            }
        }
        if (tweens.Count == 0) timer.Stop();
    }
}

sealed class Tween
{
    public readonly Action<double> Step;
    public readonly Func<double, double> Ease;
    public readonly double Duration, Delay, Start;
    public readonly Action? Done;
    public bool Finished;

    public Tween(Action<double> step, Func<double, double> ease, double duration, double delay, Action? done, double start)
    {
        Step = step; Ease = ease; Duration = duration; Delay = delay; Done = done; Start = start;
    }

    public void Cancel() => Finished = true;
}

/// <summary>Анимируемое число: плавно догоняет цель, прерываясь без рывков.</summary>
sealed class AnimFloat
{
    public double Value { get; private set; }
    readonly Action onChange;
    Tween? tw;

    public AnimFloat(double v, Action onChange)
    {
        Value = v;
        this.onChange = onChange;
    }

    public void To(double target, double ms, Func<double, double>? ease = null)
    {
        tw?.Cancel();
        double from = Value;
        if (Math.Abs(from - target) < 0.0001) return;
        tw = Motion.Run(ms, p => { Value = from + (target - from) * p; onChange(); }, ease);
    }

    public void Set(double v)
    {
        tw?.Cancel();
        Value = v;
        onChange();
    }
}

// ═══════════════════════════════════════════════════════════════
//  Тема
// ═══════════════════════════════════════════════════════════════

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(13, 14, 19);
    public static readonly Color Side = Color.FromArgb(17, 18, 25);
    public static readonly Color Card = Color.FromArgb(21, 23, 32);
    public static readonly Color Hover = Color.FromArgb(29, 31, 42);
    public static readonly Color Line = Color.FromArgb(38, 40, 52);
    public static readonly Color Text = Color.FromArgb(234, 235, 240);
    public static readonly Color Muted = Color.FromArgb(124, 127, 142);
    public static readonly Color Dim = Color.FromArgb(76, 79, 94);
    // Акцент меняется на лету (выбор цвета), производные цвета считаются из него
    public static readonly (string Name, Color Color)[] Accents =
    {
        ("Красный", Color.FromArgb(255, 77, 94)),
        ("Синий", Color.FromArgb(64, 140, 255)),
        ("Фиолетовый", Color.FromArgb(150, 105, 255)),
        ("Зелёный", Color.FromArgb(46, 204, 113)),
        ("Оранжевый", Color.FromArgb(255, 149, 0)),
    };
    public static Color Accent { get; private set; } = Accents[0].Color;
    public static Color AccentHi => Lerp(Accent, Color.White, 0.22);
    public static Color AccentDim => Lerp(Color.FromArgb(21, 23, 32), Accent, 0.36);
    public static void SetAccent(Color c) => Accent = c;
    public static readonly Color Off = Color.FromArgb(43, 45, 57);
    public static readonly Color Warn = Color.FromArgb(255, 170, 60);

    public static Font F(float size, FontStyle st = FontStyle.Regular) => new("Segoe UI", size, st);
    public static readonly Font Body = F(9.75f);
    public static readonly Font Bold = F(9.75f, FontStyle.Bold);
    public static readonly Font Small = F(7.75f, FontStyle.Bold);
    public static readonly Font Title = new("Segoe UI Semibold", 20f);
    public static readonly Font Icon = new("Segoe MDL2 Assets", 11f);

    public static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    /// <summary>Цвет с учётом проявления ближайшего FadeIn-предка (карточка «проявляется» из фона).</summary>
    public static Color Fade(Control c, Color col)
    {
        for (var p = c; p != null; p = p.Parent)
            if (p is FadeIn f) return Lerp(Bg, col, f.Appear);
        return col;
    }

    public static double AppearOf(Control c)
    {
        for (var p = c; p != null; p = p.Parent)
            if (p is FadeIn f) return f.Appear;
        return 1;
    }

    public static Color BgOf(Control c) =>
        c.Parent is TweakRow r ? r.Bg
        : c.Parent is FadeIn f ? Fade(f, f.BackColor)
        : c.Parent?.BackColor ?? Bg;

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        rad = Math.Max(0.5f, Math.Min(rad, Math.Min(r.Width, r.Height) / 2));
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Fill(Graphics g, Color c, RectangleF r, float rad)
    {
        if (c.A == 0 || r.Width <= 0 || r.Height <= 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(c);
        using var p = Round(r, rad);
        g.FillPath(b, p);
    }

    public const ControlStyles Smooth = ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                                      | ControlStyles.UserPaint | ControlStyles.ResizeRedraw;
}

class DBPanel : Panel
{
    public DBPanel() { DoubleBuffered = true; }
}

// ═══════════════════════════════════════════════════════════════
//  Базовый элемент с появлением: всплывает на 18 px и проявляется
// ═══════════════════════════════════════════════════════════════

abstract class FadeIn : Control
{
    public double Appear { get; private set; } = 1;
    Point basePos;
    Tween? tw;

    protected FadeIn() { SetStyle(Theme.Smooth, true); }

    public void Place(Point p)
    {
        basePos = p;
        ApplyOffset();
    }

    void ApplyOffset() => Location = new Point(basePos.X, basePos.Y + (int)Math.Round((1 - Appear) * 18));

    public void Hide0()
    {
        tw?.Cancel();
        Appear = 0;
        ApplyOffset();
    }

    public void Reveal(double delay)
    {
        tw?.Cancel();
        tw = Motion.Run(420, p =>
        {
            if (IsDisposed) return;
            Appear = p;
            ApplyOffset();
            Invalidate(true);
        }, Motion.ExpoOut, delay);
    }
}

// ═══════════════════════════════════════════════════════════════
//  Контролы
// ═══════════════════════════════════════════════════════════════

/// <summary>Переключатель: ползунок едет по expo-out, при нажатии «тянется» как в iOS.</summary>
class Toggle : Control
{
    bool on;
    readonly AnimFloat pos, press;
    public event EventHandler? CheckedChanged;

    public Toggle()
    {
        SetStyle(Theme.Smooth, true);
        Size = new Size(38, 20);
        Cursor = Cursors.Hand;
        pos = new AnimFloat(0, () => { Invalidate(); Parent?.Invalidate(false); });
        press = new AnimFloat(0, () => Invalidate());
    }

    public bool Checked { get => on; set { on = value; pos.Set(on ? 1 : 0); } }
    public double Progress => pos.Value;

    protected override void OnMouseDown(MouseEventArgs e) { press.To(1, 120); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { press.To(0, 200); base.OnMouseUp(e); }
    protected override void OnMouseLeave(EventArgs e) { press.To(0, 200); base.OnMouseLeave(e); }

    protected override void OnClick(EventArgs e)
    {
        on = !on;
        pos.To(on ? 1 : 0, 220);
        CheckedChanged?.Invoke(this, e);
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BgOf(this));
        double p = pos.Value;
        Theme.Fill(g, Theme.Fade(this, Theme.Lerp(Theme.Off, Theme.AccentDim, p)),
            new RectangleF(1, 3, Width - 3, Height - 7), (Height - 7) / 2f);

        float d = Height - 4;
        float stretch = (float)(press.Value * 6);
        float x = (float)(1 + p * (Width - d - 3 - stretch));
        Theme.Fill(g, Theme.Fade(this, Theme.Lerp(Theme.Muted, Theme.Accent, p)),
            new RectangleF(x, 1.5f, d + stretch, d), d / 2);
    }
}

/// <summary>Кнопка: плавный hover, press-scale 0.97.</summary>
class FlatBtn : Control
{
    readonly string glyph;
    readonly AnimFloat hover, press;
    public bool Primary { get; set; }
    public bool Danger { get; set; }

    public FlatBtn(string glyph, string text)
    {
        this.glyph = glyph;
        Text = text;
        SetStyle(Theme.Smooth, true);
        Height = 32;
        Width = text == "" ? 38 : TextRenderer.MeasureText(text, Theme.Body).Width + 46;
        Cursor = Cursors.Hand;
        hover = new AnimFloat(0, () => Invalidate());
        press = new AnimFloat(0, () => Invalidate());
    }

    protected override void OnMouseEnter(EventArgs e) { hover.To(1, 150); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover.To(0, 220); press.To(0, 200); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { press.To(1, 100); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { press.To(0, 220); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BgOf(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float s = 1f - (float)(press.Value * 0.03);
        g.TranslateTransform(Width / 2f, Height / 2f);
        g.ScaleTransform(s, s);
        g.TranslateTransform(-Width / 2f, -Height / 2f);

        var rect = new RectangleF(0, 0, Width - 1, Height - 1);
        Color fg;
        if (Primary)
        {
            Theme.Fill(g, Theme.Fade(this, Theme.Lerp(Theme.Accent, Theme.AccentHi, hover.Value)), rect, 8);
            fg = Theme.Fade(this, Color.White);
        }
        else
        {
            var fill = Danger ? Theme.Accent : Theme.Line;
            Theme.Fill(g, Color.FromArgb((int)(255 * hover.Value), fill), rect, 8);
            fg = Theme.Fade(this, Text == "" && !(Danger && hover.Value > 0.5)
                ? Theme.Lerp(Theme.Muted, Theme.Text, hover.Value)
                : Theme.Text);
        }

        var center = TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter;
        if (Text == "")
        {
            TextRenderer.DrawText(g, glyph, Theme.Icon, Rectangle.Round(rect), fg, center);
            return;
        }
        TextRenderer.DrawText(g, glyph, Theme.Icon, new Rectangle(9, 0, 22, Height), fg, center);
        TextRenderer.DrawText(g, Text, Theme.Body, new Rectangle(34, 0, Width - 38, Height), fg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}

/// <summary>Пункт бокового меню: подсветка проявляется, акцентная полоска вырастает слева.</summary>
class NavItem : Control
{
    readonly string glyph;
    readonly AnimFloat hover, sel;

    public NavItem(string glyph, string text)
    {
        this.glyph = glyph;
        Text = text;
        Height = 34;
        Cursor = Cursors.Hand;
        SetStyle(Theme.Smooth, true);
        hover = new AnimFloat(0, () => Invalidate());
        sel = new AnimFloat(0, () => Invalidate());
    }

    public bool Selected
    {
        get => sel.Value > 0.5;
        set => sel.To(value ? 1 : 0, 280);
    }

    public void SetSelectedNow(bool v) => sel.Set(v ? 1 : 0);

    protected override void OnMouseEnter(EventArgs e) { hover.To(1, 150); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover.To(0, 250); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Side);
        double s = sel.Value, h = hover.Value;
        double lit = Math.Max(s, h * 0.55);

        Theme.Fill(g, Theme.Lerp(Theme.Side, Theme.Hover, lit), new RectangleF(0, 1, Width - 1, Height - 3), 8);

        float bar = (float)(16 * s);
        if (bar > 0.5f)
            Theme.Fill(g, Theme.Accent, new RectangleF(0, (Height - bar) / 2f - 1, 3, bar), 1.5f);

        int nudge = (int)Math.Round(3 * h * (1 - s));
        TextRenderer.DrawText(g, glyph, Theme.Icon, new Rectangle(9 + nudge, 0, 26, Height),
            Theme.Lerp(Theme.Muted, Theme.Accent, Math.Max(s, h)),
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(g, Text, Theme.Body, new Rectangle(43 + nudge, 0, Width - 47, Height),
            Theme.Lerp(Color.FromArgb(178, 180, 192), Theme.Text, lit),
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Заголовок раздела с прорисовкой акцентной линии.</summary>
class Header : FadeIn
{
    public string Subtitle { get; set; }

    public Header(string title, string subtitle)
    {
        Text = title;
        Subtitle = subtitle;
        Height = 66;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        TextRenderer.DrawText(g, Text, Theme.Title, new Point(0, 4), Theme.Fade(this, Theme.Text));
        Theme.Fill(g, Theme.Fade(this, Theme.Accent), new RectangleF(3, 44, (float)(28 * Appear), 3), 1.5f);
        TextRenderer.DrawText(g, Subtitle, Theme.Body, new Point(40, 37), Theme.Fade(this, Theme.Muted));
    }
}

/// <summary>Карточка с заголовком и строками.</summary>
class Card : FadeIn
{
    const int Head = 28;

    public Card(string title) { Text = title; }

    public void LayoutRows()
    {
        int y = Head + 5;
        foreach (Control c in Controls)
        {
            c.SetBounds(6, y, Width - 12, 36);
            y += 36;
        }
        Height = y + 5;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        TextRenderer.DrawText(g, Text.ToUpperInvariant(), Theme.Small, new Point(4, 7), Theme.Fade(this, Theme.Muted));
        Theme.Fill(g, Theme.Fade(this, Theme.Card), new RectangleF(0, Head, Width - 1, Height - Head - 1), 10);
    }
}

/// <summary>Skeleton-карточка с бегущим бликом (shimmer) на время первой загрузки.</summary>
class SkeletonCard : FadeIn
{
    readonly int rows;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };

    public SkeletonCard(int rows)
    {
        this.rows = rows;
        Height = 28 + 5 + rows * 36 + 5;
        timer.Tick += (_, _) => Invalidate();
        if (!Motion.Reduced) timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        var bone = Theme.Fade(this, Theme.Off);
        Theme.Fill(g, bone, new RectangleF(4, 9, 84, 8), 4);
        var cardRect = new RectangleF(0, 28, Width - 1, Height - 29);
        Theme.Fill(g, Theme.Fade(this, Theme.Card), cardRect, 10);
        for (int i = 0; i < rows; i++)
        {
            float y = 33 + i * 36 + 13;
            Theme.Fill(g, bone, new RectangleF(16, y, Width * 0.42f - (i * 23 % 60), 10), 5);
            Theme.Fill(g, bone, new RectangleF(Width - 52, y - 2, 36, 14), 7);
        }
        if (Motion.Reduced) return;

        double ph = Motion.Now % 1500 / 1500;
        float band = 220, x = (float)(-band + (Width + band * 2) * Motion.InOut(ph));
        using var clip = Theme.Round(cardRect, 10);
        g.SetClip(clip);
        using var br = new LinearGradientBrush(new RectangleF(x, 0, band, Height), Color.Transparent, Color.Transparent, 0f)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(16, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) },
                Positions = new[] { 0f, 0.5f, 1f }
            }
        };
        g.FillRectangle(br, x, 0, band, Height);
    }
}

/// <summary>Строка настройки: переключатель или кнопка «Запуск» для действий.</summary>
class TweakRow : Control
{
    public readonly Tweak T;
    public readonly Toggle? Toggle;
    public readonly FlatBtn? RunBtn;
    public readonly FlatBtn? FolderBtn;
    readonly AnimFloat hover, changed;

    public TweakRow(Tweak t)
    {
        T = t;
        SetStyle(Theme.Smooth, true);
        Height = 36;
        hover = new AnimFloat(0, () => Invalidate(true));
        changed = new AnimFloat(0, () => Invalidate(false));

        if (t.IsAction)
        {
            RunBtn = new FlatBtn("", "Запуск") { Height = 28 };
            Controls.Add(RunBtn);
        }
        else
        {
            Toggle = new Toggle();
            Controls.Add(Toggle);
        }
        if (t.OpenFolder != null)
        {
            FolderBtn = new FlatBtn("\uE838", "") { Height = 28, Width = 30 };
            Controls.Add(FolderBtn);
        }
        foreach (Control c in Controls)
        {
            c.MouseEnter += (_, _) => hover.To(1, 150);
            c.MouseLeave += (_, _) => CheckHover();
        }
    }

    public Color Bg => Theme.Fade(this, Theme.Lerp(Theme.Card, Theme.Hover, hover.Value));

    public bool Changed
    {
        set => changed.To(value ? 1 : 0, 220);
    }

    public void SetChangedNow(bool v) => changed.Set(v ? 1 : 0);

    void CheckHover()
    {
        if (!ClientRectangle.Contains(PointToClient(Cursor.Position))) hover.To(0, 250);
    }

    protected override void OnMouseEnter(EventArgs e) { hover.To(1, 150); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { CheckHover(); base.OnMouseLeave(e); }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Toggle != null) Toggle.Location = new Point(Width - Toggle.Width - 10, (Height - Toggle.Height) / 2);
        if (RunBtn != null) RunBtn.Location = new Point(Width - RunBtn.Width - 4, (Height - RunBtn.Height) / 2);
        if (FolderBtn != null)
        {
            int left = Toggle?.Left ?? RunBtn!.Left;
            FolderBtn.Location = new Point(left - FolderBtn.Width - 6, (Height - FolderBtn.Height) / 2);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var cardBg = Theme.Fade(this, Theme.Card);
        g.Clear(cardBg);
        if (hover.Value > 0.01) Theme.Fill(g, Bg, new RectangleF(0, 0, Width - 1, Height - 1), 8);

        // точка «изменено, но не применено» — проявляется и сдвигает текст
        double c = changed.Value;
        if (c > 0.01)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float r = (float)(3 * c);
            using var b = new SolidBrush(Theme.Fade(this, Theme.Lerp(cardBg, Theme.Accent, c)));
            g.FillEllipse(b, 12 - r, Height / 2f - r, r * 2, r * 2);
        }

        double on = Toggle?.Progress ?? 1;
        var col = Theme.Fade(this, Theme.Lerp(Theme.Muted, Theme.Text, on));
        int x = 10 + (int)Math.Round(10 * c);
        int right = (Toggle?.Width ?? RunBtn!.Width) + 24 + (FolderBtn != null ? FolderBtn.Width + 6 : 0);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis;

        if (T.Icon != null)
        {
            using var ia = new System.Drawing.Imaging.ImageAttributes();
            ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = (float)(Theme.AppearOf(this) * (0.45 + 0.55 * on)) });
            g.DrawImage(T.Icon, new Rectangle(x, (Height - 20) / 2, 20, 20), 0, 0, T.Icon.Width, T.Icon.Height, GraphicsUnit.Pixel, ia);
            x += 28;
        }

        var rect = new Rectangle(x, 0, Width - right - x, Height);
        TextRenderer.DrawText(g, T.Name, Theme.Body, rect, col, flags);
        if (T.Subtitle != null)
        {
            int nameW = TextRenderer.MeasureText(T.Name, Theme.Body).Width;
            if (nameW + 40 < rect.Width)
                TextRenderer.DrawText(g, "· " + T.Subtitle, Theme.F(8.5f), new Rectangle(x + nameW + 2, 0, rect.Width - nameW - 2, Height),
                    Theme.Fade(this, Theme.Dim), flags);
        }
        if (T.Risky)
        {
            int w = Math.Min(TextRenderer.MeasureText(T.Name, Theme.Body).Width, rect.Width);
            TextRenderer.DrawText(g, "⚠", Theme.Body, new Rectangle(x + w + 2, 0, 24, Height), Theme.Fade(this, Theme.Warn), flags);
        }
    }
}

/// <summary>Поиск: кольцо фокуса проявляется акцентом.</summary>
class SearchBox : Control
{
    readonly TextBox tb;
    readonly AnimFloat focus;

    public SearchBox(TextBox tb)
    {
        this.tb = tb;
        tb.BorderStyle = BorderStyle.None;
        tb.BackColor = Theme.Hover;
        tb.ForeColor = Theme.Text;
        tb.Font = Theme.Body;
        tb.PlaceholderText = "Поиск настроек";
        Controls.Add(tb);
        SetStyle(Theme.Smooth, true);
        Cursor = Cursors.IBeam;
        focus = new AnimFloat(0, () => Invalidate());
        tb.GotFocus += (_, _) => focus.To(1, 180);
        tb.LostFocus += (_, _) => focus.To(0, 250);
    }

    protected override void OnClick(EventArgs e) { tb.Focus(); base.OnClick(e); }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        tb.SetBounds(34, (Height - tb.PreferredHeight) / 2, Width - 44, tb.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.BgOf(this));
        var r = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        Theme.Fill(g, Theme.Hover, r, 8);
        if (focus.Value > 0.01)
        {
            using var pen = new Pen(Color.FromArgb((int)(200 * focus.Value), Theme.Accent), 1.2f);
            using var path = Theme.Round(r, 8);
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, "", Theme.Icon, new Rectangle(8, 0, 22, Height),
            Theme.Lerp(Theme.Muted, Theme.Accent, focus.Value),
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
    }
}

/// <summary>Тонкая полоса прогресса под верхней панелью (неопределённый режим).</summary>
class ProgressLine : Control
{
    readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    readonly AnimFloat alpha;
    bool running;

    public ProgressLine()
    {
        SetStyle(Theme.Smooth, true);
        Height = 2;
        alpha = new AnimFloat(0, () => Invalidate());
        timer.Tick += (_, _) => Invalidate();
    }

    public bool Running
    {
        get => running;
        set
        {
            if (running == value) return;
            running = value;
            if (value) { timer.Start(); alpha.To(1, 200); }
            else
            {
                alpha.To(0, 350);
                Motion.Run(360, _ => { }, null, 0, () => { if (!running) timer.Stop(); });
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        int a = (int)(255 * alpha.Value);
        if (a <= 0) return;
        if (Motion.Reduced)
        {
            using var dim = new SolidBrush(Color.FromArgb(a / 2, Theme.Accent));
            g.FillRectangle(dim, ClientRectangle);
            return;
        }
        double ph = Motion.Now % 1300 / 1300;
        float w = Width * 0.32f;
        float x = (float)(-w + (Width + w) * Motion.InOut(ph));
        using var br = new SolidBrush(Color.FromArgb(a, Theme.Accent));
        g.FillRectangle(br, x, 0, w, Height);
    }
}

/// <summary>Блок пользователя внизу бокового меню.</summary>
class UserBadge : Control
{
    public string User { get; set; } = "";

    public UserBadge()
    {
        SetStyle(Theme.Smooth, true);
        BackColor = Theme.Side;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Side);
        using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 16, 0, Width - 16, 0);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, 18, 15, 36, 36);
        string initial = User.Length > 0 ? User[..1].ToUpperInvariant() : "?";
        TextRenderer.DrawText(g, initial, Theme.F(12f, FontStyle.Bold), new Rectangle(18, 15, 36, 36), Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(g, User, Theme.Bold, new Rectangle(64, 14, Width - 70, 20), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Тёмное контекстное меню.</summary>
class DarkRenderer : ToolStripProfessionalRenderer
{
    public DarkRenderer() : base(new DarkColors()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = Theme.Text;
        base.OnRenderItemText(e);
    }

    class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Card;
        public override Color ImageMarginGradientBegin => Theme.Card;
        public override Color ImageMarginGradientMiddle => Theme.Card;
        public override Color ImageMarginGradientEnd => Theme.Card;
        public override Color MenuBorder => Theme.Line;
        public override Color MenuItemBorder => Theme.Hover;
        public override Color MenuItemSelected => Theme.Hover;
        public override Color MenuItemSelectedGradientBegin => Theme.Hover;
        public override Color MenuItemSelectedGradientEnd => Theme.Hover;
        public override Color SeparatorDark => Theme.Line;
    }
}

static class UI
{
    public static ToolTip MakeTip()
    {
        var t = new ToolTip { OwnerDraw = true, InitialDelay = 450, AutoPopDelay = 20000 };
        var flags = TextFormatFlags.WordBreak;
        t.Popup += (_, e) =>
        {
            var sz = TextRenderer.MeasureText(t.GetToolTip(e.AssociatedControl), Theme.Body, new Size(330, 0), flags);
            e.ToolTipSize = new Size(Math.Min(sz.Width, 330) + 22, sz.Height + 16);
        };
        t.Draw += (_, e) =>
        {
            e.Graphics.Clear(Theme.Hover);
            using var p = new Pen(Theme.Line);
            e.Graphics.DrawRectangle(p, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, Theme.Body,
                new Rectangle(11, 8, e.Bounds.Width - 22, e.Bounds.Height - 16), Theme.Text, flags);
        };
        return t;
    }
}
