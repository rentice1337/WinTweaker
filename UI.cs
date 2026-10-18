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
    static readonly bool systemReduced = !QueryAnimations();
    /// <summary>Выключено пользователем в «Настройках».</summary>
    public static bool UserOff { get; set; }
    /// <summary>Только для замера плавности: анимации идут, даже если в Windows они выключены.</summary>
    public static bool ForceOn { get; set; }
    public static bool Reduced => !ForceOn && (systemReduced || UserOff);

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

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);

    static readonly Stopwatch clock = Stopwatch.StartNew();
    static readonly List<Tween> tweens = new();
    [DllImport("dwmapi.dll")] static extern int DwmFlush();

    // Кадры анимаций — по обновлению экрана, а не по таймеру WinForms: таймер Windows тикает шагами 15,6 мс,
    // и «каждые 16 мс» превращались в 31 мс — потолок ~32 кадра/с. Отдельный поток ждёт следующий кадр
    // композитора (DwmFlush: 60 Гц — 16,7 мс, 144 Гц — 6,9 мс) и просит UI-поток посчитать кадр.
    static Control? sync;
    static readonly AutoResetEvent wake = new(false);
    static volatile bool running;
    static int queued;
    static Thread? pacer;

    static Motion()
    {
        try { timeBeginPeriod(1); } catch { }
    }

    static void Start()
    {
        if (sync == null) { sync = new Control(); _ = sync.Handle; } // окно для BeginInvoke — в потоке интерфейса (Run зовут из него)
        if (running) return;
        running = true;
        if (pacer == null)
        {
            pacer = new Thread(Pace) { IsBackground = true, Name = "WinTweak frames", Priority = ThreadPriority.AboveNormal };
            pacer.Start();
        }
        else wake.Set();
    }

    static void Pace()
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (!running) { wake.WaitOne(); sw.Restart(); continue; }
            // ждём кадр композитора; если композиции нет — сами отмеряем ~60 кадров/с
            if (DwmFlush() != 0 || sw.Elapsed.TotalMilliseconds < 2)
                while (sw.Elapsed.TotalMilliseconds < 16.6) Thread.Sleep(1);
            sw.Restart();
            // кадр не копится: пока UI-поток не обработал прошлый, новый не ставим
            if (Interlocked.Exchange(ref queued, 1) == 0)
            {
                try { sync!.BeginInvoke(FrameOnUi); }
                catch { queued = 0; Thread.Sleep(16); }
            }
        }
    }

    static void FrameOnUi()
    {
        queued = 0;
        Tick();
    }

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
        Start();
        return tw;
    }

    // ───── замер кадров: если анимации стабильно идут рывками — событие Slow (включить лёгкий интерфейс) ─────

    static double lastTick = -1;
    static readonly List<double> gaps = new();
    static int slowWindows;
    static bool slowFired;

    /// <summary>Интерфейс стабильно рисуется медленнее ~25 кадров/с; аргумент — замеренные кадры/с.</summary>
    public static event Action<int>? Slow;

    static void Measure(double now)
    {
        if (slowFired) return;
        if (lastTick >= 0 && now - lastTick < 1000) gaps.Add(now - lastTick); // паузы между анимациями не считаем
        lastTick = now;
        if (gaps.Count < 45) return;
        gaps.Sort();
        double median = gaps[gaps.Count / 2]; // медиана — одна долгая перерисовка страницы не в счёт
        gaps.Clear();
        slowWindows = median > 40 ? slowWindows + 1 : 0;
        if (slowWindows >= 2)
        {
            slowFired = true;
            Slow?.Invoke((int)Math.Round(1000 / median));
        }
    }

    /// <summary>Есть ли сейчас анимации.</summary>
    public static bool Busy => tweens.Count > 0;
    /// <summary>Для замера плавности: время каждого кадра анимаций.</summary>
    public static List<double>? Record;
    /// <summary>Для замера: длительность работы каждого кадра (расчёт + отрисовка).</summary>
    public static List<double>? Work;

    static void Tick()
    {
        double now = Now;
        Record?.Add(now);
        Measure(now);
        foreach (var tw in tweens.ToArray())
        {
            if (tw.Finished) { tweens.Remove(tw); continue; }
            double elapsed = now - tw.Start - tw.Delay;
            if (elapsed < 0) continue;
            double p = tw.Duration <= 0 ? 1 : Math.Min(1, elapsed / tw.Duration);
            try { tw.Step(tw.Ease(p)); } catch (ObjectDisposedException) { tw.Finished = true; }
            if (p >= 1)
            {
                tw.Finished = true;
                tweens.Remove(tw);
                tw.Done?.Invoke();
            }
        }
        if (Work != null)
        {
            // для замера: сколько длилась работа кадра вместе с отрисовкой (до момента, когда очередь окна опустела)
            double t0 = now;
            void Idle(object? s, EventArgs e) { Application.Idle -= Idle; Work?.Add(Now - t0); }
            Application.Idle += Idle;
        }
        if (tweens.Count == 0) { running = false; lastTick = -1; }
    }
}

/// <summary>
/// Лёгкий интерфейс: без стеклянного фона, подсветки рамок и анимаций — для слабых ПК и больших разрешений,
/// где эти эффекты (их рисует процессор) опускают окно до 15 кадров/с. Включается сам, если интерфейс тормозит.
/// </summary>
static class UiMode
{
    /// <summary>Настройка «lite»: "" — решает программа, "auto" — включён ею, "1" — включён пользователем, "0" — выключен пользователем.</summary>
    static string Setting => AppSettings.Get("lite", "");
    public static bool Lite => Setting is "1" or "auto";
    /// <summary>Можно ли включить автоматически (пользователь сам ничего не выбирал).</summary>
    public static bool AutoAllowed => Setting == "";
    public static void Set(bool on, bool byUser) => AppSettings.Set("lite", on ? (byUser ? "1" : "auto") : "0");
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
    public static readonly Color Bg = Color.FromArgb(12, 13, 18);
    public static readonly Color Side = Color.FromArgb(16, 17, 24);
    public static readonly Color Card = Color.FromArgb(22, 24, 33);
    public static readonly Color Hover = Color.FromArgb(31, 33, 45);
    public static readonly Color Line = Color.FromArgb(40, 42, 55);
    public static readonly Color Text = Color.FromArgb(236, 237, 242);
    public static readonly Color Muted = Color.FromArgb(132, 135, 152);
    public static readonly Color Dim = Color.FromArgb(84, 87, 104);
    public static readonly Color Off = Color.FromArgb(48, 50, 64);
    public static readonly Color Warn = Color.FromArgb(255, 176, 64);
    public static readonly Color Good = Color.FromArgb(64, 214, 140);
    public static readonly Color Bad = Color.FromArgb(255, 92, 92);

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
    public static Color AccentHi => Lerp(Accent, Color.White, 0.25);
    public static Color AccentDim => Lerp(Card, Accent, 0.38);
    public static void SetAccent(Color c) => Accent = c;

    public static Font F(float size, FontStyle st = FontStyle.Regular) => new("Segoe UI", size, st);
    public static readonly Font Body = F(9.75f);
    public static readonly Font Bold = F(9.75f, FontStyle.Bold);
    public static readonly Font Semi = new("Segoe UI Semibold", 9.75f);
    public static readonly Font Small = F(7.75f, FontStyle.Bold);
    public static readonly Font Tiny = F(8.25f);
    public static readonly Font Title = new("Segoe UI Semibold", 22f);
    public static readonly Font Big = new("Segoe UI Semibold", 26f);
    public static readonly Font Icon = new("Segoe MDL2 Assets", 11f);
    public static readonly Font IconBig = new("Segoe MDL2 Assets", 14f);
    public static readonly Font Mono = new("Consolas", 9.5f);

    public static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    /// <summary>Цвет текста с учётом проявления ближайшей карточки (проявляется из фона).</summary>
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

    /// <summary>Неоновое свечение: несколько расширяющихся слоёв с падающей прозрачностью.</summary>
    public static void Glow(Graphics g, Color c, RectangleF r, float rad, int layers, float step, int alpha)
    {
        for (int i = layers; i >= 1; i--)
        {
            var rr = RectangleF.Inflate(r, i * step, i * step);
            Fill(g, Color.FromArgb(Math.Clamp(alpha * (layers - i + 1) / layers / 2, 0, 255), c), rr, rad + i * step);
        }
    }

    public const ControlStyles Smooth = ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                                      | ControlStyles.UserPaint | ControlStyles.ResizeRedraw;
}

// ═══════════════════════════════════════════════════════════════
//  Базовый элемент с появлением: всплывает на 18 px и проявляется
// ═══════════════════════════════════════════════════════════════

abstract class FadeIn : Control
{
    public double Appear { get; private set; } = 1;
    /// <summary>Растягивать на обе колонки раскладки.</summary>
    public bool Wide { get; set; }
    Point basePos;
    Tween? tw;

    protected FadeIn() { SetStyle(Theme.Smooth, true); }

    public Point BasePos => basePos;

    public void Place(Point p)
    {
        basePos = p;
        ApplyOffset();
    }

    void ApplyOffset() => Location = new Point(basePos.X, basePos.Y + (int)Math.Round((1 - Appear) * 18));
    // Примечание: страницы появляются через Canvas.Reveal (снимки), Appear у живых карточек всегда 1.

    public void Hide0()
    {
        tw?.Cancel();
        Appear = 0;
        ApplyOffset();
    }

    public void Reveal(double delay)
    {
        tw?.Cancel();
        tw = Motion.Run(460, p =>
        {
            if (IsDisposed) return;
            Appear = p;
            ApplyOffset();
            Invalidate(true);
        }, Motion.ExpoOut, delay);
    }
}

/// <summary>Стеклянная карточка: заголовок, полупрозрачное тело, рамка с «прожектором» под курсором.</summary>
abstract class Surface : FadeIn
{
    public int HeadH => string.IsNullOrEmpty(Text) ? 0 : 28;
    /// <summary>Цвет рамки (по умолчанию — едва заметная белая).</summary>
    public Color? Edge { get; set; }

    protected Surface(string title)
    {
        Text = title;
        Spotlight.Register(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Spotlight.Unregister(this);
        base.Dispose(disposing);
    }

    protected Color C(Color c) => Theme.Fade(this, c);
    protected RectangleF BodyF => new(0.5f, HeadH + 0.5f, Width - 2, Height - HeadH - 2);
    protected Rectangle Body => new(0, HeadH, Width - 1, Height - HeadH - 1);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Glass.Back(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (HeadH > 0)
            TextRenderer.DrawText(g, Text.ToUpperInvariant(), Theme.Small, new Point(4, 8), C(Theme.Muted));

        var body = BodyF;
        using var path = Theme.Round(body, 12);
        using (var b = new SolidBrush(Glass.Scale(Glass.CardTint, Appear))) g.FillPath(b, path);

        var cur = PointToClient(Spotlight.Cursor);
        var reach = ClientRectangle;
        reach.Inflate(Spotlight.Reach, Spotlight.Reach);
        bool near = !Motion.Reduced && Spotlight.Enabled && reach.Contains(cur);
        if (near) EnsureRegions(path);

        // перерисовка только рамки (подсветка под курсором) — содержимое карточки не трогаем
        var inner = Rectangle.Inflate(Rectangle.Round(body), -Spotlight.Band, -Spotlight.Band);
        if (e.ClipRectangle.IntersectsWith(inner)) PaintBody(g, Rectangle.Round(body));

        using (var pen = new Pen(Glass.Scale(Edge ?? Glass.Border, Appear))) g.DrawPath(pen, path);
        if (near && edgeRegion != null)
        {
            // рамка подсвечивается акцентом там, где ближе курсор
            var st = g.Save();
            g.SetClip(edgeRegion, CombineMode.Intersect);
            g.DrawImageUnscaled(Spotlight.Edge, cur.X - Spotlight.R, cur.Y - Spotlight.R);
            g.Restore(st);
        }
    }

    Region? edgeRegion;
    Size regionsFor;

    void EnsureRegions(GraphicsPath path)
    {
        if (edgeRegion != null && regionsFor == Size) return;
        edgeRegion?.Dispose();
        using var ring = (GraphicsPath)path.Clone();
        using (var pen = new Pen(Color.Black, 2f)) ring.Widen(pen);
        edgeRegion = new Region(ring);
        regionsFor = Size;
    }

    /// <summary>Полосы вдоль рамки возле точки (координаты карточки) — только их перерисовывает подсветка.</summary>
    public IEnumerable<Rectangle> EdgeBands(Point p)
    {
        int b = Spotlight.Band, top = HeadH;
        var area = new Rectangle(p.X - Spotlight.R, p.Y - Spotlight.R, Spotlight.R * 2, Spotlight.R * 2);
        var bands = new[]
        {
            new Rectangle(0, top - 1, Width, b + 1),
            new Rectangle(0, Height - b, Width, b),
            new Rectangle(0, top, b, Height - top),
            new Rectangle(Width - b, top, b, Height - top),
        };
        foreach (var r in bands)
        {
            var x = Rectangle.Intersect(r, area);
            if (x.Width > 0 && x.Height > 0) yield return x;
        }
    }

    protected virtual void PaintBody(Graphics g, Rectangle r) { }
}

/// <summary>Заголовок раздела: крупный текст, неоновая черта и подзаголовок.</summary>
class Header : FadeIn
{
    public string Subtitle { get; set; }

    public Header(string title, string subtitle)
    {
        Text = title;
        Subtitle = subtitle;
        Height = 74;
        Wide = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Glass.Back(g, this);
        TextRenderer.DrawText(g, Text, Theme.Title, new Point(-2, 2), Theme.Fade(this, Theme.Text));
        float w = (float)(30 * Appear);
        var bar = new RectangleF(2, 50, w, 3);
        Theme.Glow(g, Theme.Accent, bar, 1.5f, 3, 2f, (int)(90 * Appear));
        Theme.Fill(g, Theme.Fade(this, Theme.Accent), bar, 1.5f);
        TextRenderer.DrawText(g, Subtitle, Theme.Body, new Point(42, 42), Theme.Fade(this, Theme.Muted));
    }
}

/// <summary>Карточка со строками настроек.</summary>
class Card : Surface
{
    public Card(string title) : base(title) { }

    public void LayoutRows()
    {
        int y = HeadH + 6;
        foreach (Control c in Controls)
        {
            c.SetBounds(6, y, Width - 12, c.Height);
            y += c.Height;
        }
        Height = y + 6;
    }
}

/// <summary>Заготовка карточки с бегущим бликом на время первой загрузки.</summary>
class SkeletonCard : Surface
{
    readonly int rows;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 30 };

    public SkeletonCard(int rows) : base("")
    {
        this.rows = rows;
        Height = 12 + rows * 38 + 12;
        timer.Tick += (_, _) => Invalidate(false);
        if (!Motion.Reduced) timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        var bone = Color.FromArgb((int)(22 * Appear), 255, 255, 255);
        for (int i = 0; i < rows; i++)
        {
            float y = r.Top + 12 + i * 38 + 13;
            Theme.Fill(g, bone, new RectangleF(18, y, Width * 0.45f - (i * 29 % 70), 11), 5.5f);
            Theme.Fill(g, bone, new RectangleF(Width - 58, y - 2, 40, 15), 7.5f);
        }
        if (Motion.Reduced) return;
        double ph = Motion.Now % 1500 / 1500;
        float band = 260, x = (float)(-band + (Width + band * 2) * Motion.InOut(ph));
        using var clip = Theme.Round(r, 12);
        var old = g.Clip;
        g.SetClip(clip);
        using var br = new LinearGradientBrush(new RectangleF(x, 0, band, Height), Color.Transparent, Color.Transparent, 0f)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(20, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) },
                Positions = new[] { 0f, 0.5f, 1f }
            }
        };
        g.FillRectangle(br, x, 0, band, Height);
        g.Clip = old;
    }
}

// ═══════════════════════════════════════════════════════════════
//  Контролы
// ═══════════════════════════════════════════════════════════════

/// <summary>Переключатель: ползунок едет по expo-out, тянется при нажатии и светится во включённом состоянии.</summary>
class Toggle : Control
{
    bool on;
    readonly AnimFloat pos, press;
    public event EventHandler? CheckedChanged;

    public Toggle()
    {
        SetStyle(Theme.Smooth, true);
        Size = new Size(46, 26);
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
        pos.To(on ? 1 : 0, 240);
        CheckedChanged?.Invoke(this, e);
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Glass.Back(g, this);
        double p = pos.Value;
        var track = new RectangleF(4, 6, Width - 8, Height - 12);
        Theme.Fill(g, Theme.Fade(this, Theme.Lerp(Theme.Off, Theme.AccentDim, p)), track, track.Height / 2);

        float d = Height - 10;
        float stretch = (float)(press.Value * 6);
        float x = (float)(5 + p * (Width - d - 10 - stretch));
        var knob = new RectangleF(x, 5, d + stretch, d);
        if (p > 0.02)
            Theme.Fill(g, Color.FromArgb((int)(70 * p * Theme.AppearOf(this)), Theme.Accent), RectangleF.Inflate(knob, 3.5f, 3.5f), d);
        Theme.Fill(g, Theme.Fade(this, Theme.Lerp(Theme.Muted, Theme.Accent, p)), knob, d / 2);
    }
}

/// <summary>Кнопка: плавная подсветка, нажатие 0.97, у основной — неоновое свечение.</summary>
class FlatBtn : Control
{
    readonly string glyph;
    readonly AnimFloat hover, press;
    bool primary;
    public bool Danger { get; set; }

    public FlatBtn(string glyph, string text)
    {
        this.glyph = glyph;
        Text = text;
        SetStyle(Theme.Smooth, true);
        Height = 32;
        Width = text == "" ? 38 : TextRenderer.MeasureText(text, Theme.Semi).Width + 46;
        Cursor = Cursors.Hand;
        hover = new AnimFloat(0, () => Invalidate());
        press = new AnimFloat(0, () => Invalidate());
    }

    /// <summary>Отступ под свечение у основной кнопки (контрол больше видимой кнопки).</summary>
    public int Pad => primary ? 8 : 0;

    int count;
    /// <summary>Число на кнопке (сколько изменений ждёт применения).</summary>
    public int Count
    {
        get => count;
        set { if (count != value) { count = value; Invalidate(); } }
    }

    public bool Primary
    {
        get => primary;
        set
        {
            if (primary == value) return;
            primary = value;
            int d = value ? 16 : -16;
            Size = new Size(Width + d, Height + d);
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e) { hover.To(1, 150); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover.To(0, 240); press.To(0, 200); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { press.To(1, 100); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { press.To(0, 220); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Glass.Back(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float s = 1f - (float)(press.Value * 0.03);
        g.TranslateTransform(Width / 2f, Height / 2f);
        g.ScaleTransform(s, s);
        g.TranslateTransform(-Width / 2f, -Height / 2f);

        var rect = new RectangleF(Pad, Pad, Width - Pad * 2 - 1, Height - Pad * 2 - 1);
        double h = hover.Value;
        Color fg;
        if (primary)
        {
            Theme.Glow(g, Theme.Accent, rect, 9, 4, 1.8f, (int)((50 + 50 * h) * Theme.AppearOf(this)));
            using var path = Theme.Round(rect, 9);
            using var grad = new LinearGradientBrush(rect, Theme.Fade(this, Theme.Lerp(Theme.AccentHi, Color.White, 0.15 * h)),
                Theme.Fade(this, Theme.Accent), 90f);
            g.FillPath(grad, path);
            fg = Theme.Fade(this, Color.White);
        }
        else
        {
            var fill = Danger ? Color.FromArgb((int)(255 * h), Theme.Bad) : Color.FromArgb((int)(26 * h), 255, 255, 255);
            Theme.Fill(g, fill, rect, 9);
            fg = Theme.Fade(this, Text == "" && !(Danger && h > 0.5) ? Theme.Lerp(Theme.Muted, Theme.Text, h) : Theme.Text);
        }

        var center = TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter;
        var r = Rectangle.Round(rect);
        if (Text == "")
        {
            TextRenderer.DrawText(g, glyph, Theme.Icon, r, fg, center);
            return;
        }
        TextRenderer.DrawText(g, glyph, Theme.Icon, new Rectangle(r.X + 9, r.Y, 22, r.Height), fg, center);
        TextRenderer.DrawText(g, Text, Theme.Semi, new Rectangle(r.X + 34, r.Y, r.Width - 38, r.Height), fg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        if (count > 0)
        {
            // счётчик — белая «таблетка» в правом верхнем углу кнопки
            string c = count > 99 ? "99+" : count.ToString();
            int cw = Math.Max(18, TextRenderer.MeasureText(c, Theme.Small).Width + 8);
            var pill = new RectangleF(r.Right - cw + 6, r.Y - 7, cw, 18);
            Theme.Fill(g, Color.White, pill, 9);
            TextRenderer.DrawText(g, c, Theme.Small, Rectangle.Round(pill), Theme.Accent, center);
        }
    }
}

/// <summary>Плитка действия: иконка в круге, заголовок и подпись.</summary>
class ActionTile : Control
{
    readonly string glyph, sub;
    readonly AnimFloat hover, press;

    public ActionTile(string glyph, string title, string sub = "")
    {
        this.glyph = glyph;
        this.sub = sub;
        Text = title;
        SetStyle(Theme.Smooth, true);
        Height = 60;
        Cursor = Cursors.Hand;
        hover = new AnimFloat(0, () => Invalidate());
        press = new AnimFloat(0, () => Invalidate());
    }

    protected override void OnMouseEnter(EventArgs e) { hover.To(1, 150); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover.To(0, 240); press.To(0, 200); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { press.To(1, 100); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { press.To(0, 220); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Glass.Back(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = 1f - (float)(press.Value * 0.03);
        g.TranslateTransform(Width / 2f, Height / 2f);
        g.ScaleTransform(s, s);
        g.TranslateTransform(-Width / 2f, -Height / 2f);

        double h = hover.Value, a = Theme.AppearOf(this);
        var rect = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        Theme.Fill(g, Color.FromArgb((int)((14 + 14 * h) * a), 255, 255, 255), rect, 10);
        using (var pen = new Pen(Color.FromArgb((int)((18 + 120 * h) * a), Theme.Lerp(Color.White, Theme.Accent, h))))
        using (var path = Theme.Round(rect, 10))
            g.DrawPath(pen, path);

        var circle = new RectangleF(12, (Height - 36) / 2f, 36, 36);
        if (h > 0.02) Theme.Fill(g, Color.FromArgb((int)(60 * h * a), Theme.Accent), RectangleF.Inflate(circle, 4, 4), 22);
        Theme.Fill(g, Color.FromArgb((int)((44 + 40 * h) * a), Theme.Accent), circle, 18);
        TextRenderer.DrawText(g, glyph, Theme.Icon, Rectangle.Round(circle), Theme.Fade(this, Theme.Lerp(Theme.Accent, Color.White, 0.25 * h)),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        int tx = 58, tw = Width - tx - 10;
        if (sub == "")
            TextRenderer.DrawText(g, Text, Theme.Semi, new Rectangle(tx, 0, tw, Height), Theme.Fade(this, Theme.Text), flags);
        else
        {
            TextRenderer.DrawText(g, Text, Theme.Semi, new Rectangle(tx, 10, tw, 20), Theme.Fade(this, Theme.Text), flags);
            TextRenderer.DrawText(g, sub, Theme.Tiny, new Rectangle(tx, 30, tw, 18), Theme.Fade(this, Theme.Muted), flags);
        }
    }
}

// ═══════════════════════════════════════════════════════════════
//  Боковая панель
// ═══════════════════════════════════════════════════════════════

/// <summary>Боковая панель: тёмное стекло, неоновый логотип.</summary>
class SidePanel : Control
{
    public SidePanel() { SetStyle(Theme.Smooth, true); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Glass.Back(g, this);
        using (var pen = new Pen(Color.FromArgb(16, 255, 255, 255))) g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
        Logo.Draw(g, new Point(20, 20));
    }
}

/// <summary>Логотип: плитка-переключатель цвета акцента и строгий текст с версией.</summary>
static class Logo
{
    static readonly Font name = new("Segoe UI Semibold", 15f);

    /// <summary>Строка авторских прав — та же, что в свойствах exe.</summary>
    public const string Copyright = "© 2026 rentice1337. Все права защищены.";

    public static string Version
    {
        get
        {
            var v = typeof(Logo).Assembly.GetName().Version;
            return v == null ? "1.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}"; // как в теге релиза: 2.0.0
        }
    }

    public static void Tile(Graphics g, RectangleF tile)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.Round(tile, tile.Width * 0.28f))
        using (var grad = new LinearGradientBrush(tile, Theme.AccentHi, Theme.Lerp(Theme.Accent, Color.Black, 0.15), 45f))
            g.FillPath(grad, path);
        float k = tile.Width / 38f;
        Theme.Fill(g, Color.FromArgb(120, 255, 255, 255), new RectangleF(tile.X + 7 * k, tile.Y + 14 * k, 24 * k, 10 * k), 5 * k);
        Theme.Fill(g, Color.White, new RectangleF(tile.X + 18.5f * k, tile.Y + 11.5f * k, 15 * k, 15 * k), 7.5f * k);
    }

    public static void Draw(Graphics g, Point at)
    {
        Tile(g, new RectangleF(at.X, at.Y, 38, 38));
        TextRenderer.DrawText(g, "WinTweak", name, new Point(at.X + 48, at.Y - 2), Theme.Text);
        TextRenderer.DrawText(g, $"v{Version} · Windows 10", Theme.Tiny, new Point(at.X + 50, at.Y + 23), Theme.Muted);
    }
}


/// <summary>Холст меню: подписи разделов и общий скользящий индикатор выбранного пункта.</summary>
class NavCanvas : Canvas
{
    public const int ItemH = 30, Step = 30;
    public readonly List<(string Text, int Y)> Labels = new();
    public readonly AnimFloat Ind;
    public double IndY => Ind.Value;

    public NavCanvas()
    {
        double last = 0;
        Ind = new AnimFloat(0, () =>
        {
            double lo = Math.Min(last, Ind!.Value) - 2, hi = Math.Max(last, Ind.Value) + ItemH + 2;
            foreach (Control c in Controls)
                if (c.Bottom > lo && c.Top < hi) c.Invalidate();
            last = Ind.Value;
        });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        foreach (var (text, y) in Labels)
            TextRenderer.DrawText(e.Graphics, text.ToUpperInvariant(), Theme.Small, new Point(16, y), Theme.Dim);
    }
}

/// <summary>Меню с прокруткой колесом, если пункты не помещаются по высоте.</summary>
class NavHost : Viewport
{
    public NavHost() : base(new NavCanvas()) { }
    public NavCanvas Nav => (NavCanvas)Canvas;

    // пункты у краёв перерисовываются: на них «затухание», намекающее, что меню прокручивается
    protected override void OnScrolled() => Canvas.Invalidate(true);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Canvas.Invalidate(true);
    }

    /// <summary>Прокрутить так, чтобы пункт был виден.</summary>
    public void Reveal(Control item)
    {
        int y = item.Top + Canvas.Top;
        if (y < 8) ScrollTo(item.Top - 40);
        else if (y + item.Height > Height - 8) ScrollTo(item.Bottom - Height + 40);
    }
}

/// <summary>Пункт меню. Подсветку выбранного рисует общий индикатор, который плавно переезжает между пунктами.</summary>
class NavItem : Control
{
    readonly string glyph;
    readonly AnimFloat hover, sel;

    public NavItem(string glyph, string text)
    {
        this.glyph = glyph;
        Text = text;
        Height = NavCanvas.ItemH;
        Cursor = Cursors.Hand;
        SetStyle(Theme.Smooth, true);
        hover = new AnimFloat(0, () => Invalidate());
        sel = new AnimFloat(0, () => Invalidate());
    }

    public bool Selected
    {
        get => sel.Value > 0.5;
        set => sel.To(value ? 1 : 0, 300);
    }

    public void SetSelectedNow(bool v) => sel.Set(v ? 1 : 0);

    protected override void OnMouseEnter(EventArgs e) { hover.To(1, 150); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover.To(0, 260); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Glass.Back(g, this);
        double s = sel.Value, h = hover.Value;

        if (h > 0.01)
            Theme.Fill(g, Color.FromArgb((int)(14 * h), 255, 255, 255), new RectangleF(0, 0, Width - 1, Height - 1), 9);

        // кусок общего индикатора, попадающий в этот пункт (так он едет между пунктами без швов)
        if (Parent is NavCanvas nc)
        {
            var pill = new RectangleF(0, (float)(nc.IndY - Top), Width - 1, NavCanvas.ItemH - 1);
            if (pill.Bottom > 0 && pill.Top < Height)
            {
                Theme.Fill(g, Color.FromArgb(26, 255, 255, 255), pill, 9);
                var bar = new RectangleF(1, pill.Y + 8, 3, pill.Height - 16);
                Theme.Glow(g, Theme.Accent, bar, 1.5f, 3, 1.6f, 120);
                Theme.Fill(g, Theme.Accent, bar, 1.5f);
            }
        }

        else if (s > 0.01)
        {
            // пункт вне общего меню («Настройки» внизу) рисует свою подсветку
            var pill = new RectangleF(0, 0, Width - 1, Height - 1);
            Theme.Fill(g, Color.FromArgb((int)(26 * s), 255, 255, 255), pill, 9);
            var bar = new RectangleF(1, 8, 3, Height - 16);
            Theme.Fill(g, Color.FromArgb((int)(255 * s), Theme.Accent), bar, 1.5f);
        }

        int nudge = (int)Math.Round(3 * h * (1 - s));
        TextRenderer.DrawText(g, glyph, Theme.Icon, new Rectangle(10 + nudge, 0, 26, Height),
            Theme.Lerp(Theme.Muted, Theme.Accent, Math.Max(s, h * 0.7)),
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(g, Text, s > 0.5 ? Theme.Semi : Theme.Body, new Rectangle(44 + nudge, 0, Width - 48, Height),
            Theme.Lerp(Color.FromArgb(170, 173, 188), Theme.Text, Math.Max(s, h)),
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        // затухание у краёв прокручиваемого меню
        if (Parent is NavCanvas canvas && canvas.Parent is NavHost host && host.CanScroll)
        {
            const int F = 34;
            int y = Top + canvas.Top;
            if (!host.AtEnd) Fade(g, host.Height - F - y, F, false);
            if (!host.AtStart) Fade(g, -y, F, true);
        }
    }

    void Fade(Graphics g, int zoneTop, int f, bool top)
    {
        var zone = new Rectangle(0, zoneTop, Width, f);
        if (!zone.IntersectsWith(ClientRectangle)) return;
        var c0 = Color.FromArgb(top ? 235 : 0, 12, 13, 18);
        var c1 = Color.FromArgb(top ? 0 : 235, 12, 13, 18);
        using var b = new LinearGradientBrush(new Rectangle(0, zoneTop - 1, Width, f + 2), c0, c1, 90f);
        g.FillRectangle(b, Rectangle.Intersect(zone, ClientRectangle));
    }
}

/// <summary>Блок пользователя внизу меню: аватар, имя и строка состояния.</summary>
class UserBadge : Control
{
    string status = "";
    Color statusColor = Theme.Muted;
    public string User { get; set; } = "";

    public UserBadge() { SetStyle(Theme.Smooth, true); Cursor = Cursors.Hand; }

    public void SetStatus(string text, Color color)
    {
        status = text;
        statusColor = color;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Glass.Back(g, this);
        using (var pen = new Pen(Color.FromArgb(16, 255, 255, 255))) g.DrawLine(pen, 16, 0, Width - 16, 0);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var av = new RectangleF(18, 13, 34, 34);
        Theme.Glow(g, Theme.Accent, av, 17, 3, 1.5f, 70);
        using (var b = new LinearGradientBrush(av, Theme.AccentHi, Theme.Accent, 45f)) g.FillEllipse(b, av);
        string initial = User.Length > 0 ? User[..1].ToUpperInvariant() : "?";
        TextRenderer.DrawText(g, initial, Theme.F(12f, FontStyle.Bold), Rectangle.Round(av), Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
        TextRenderer.DrawText(g, User, Theme.Semi, new Rectangle(62, 12, Width - 70, 20), Theme.Text, flags);
        TextRenderer.DrawText(g, status, Theme.Tiny, new Rectangle(62, 32, Width - 70, 18), statusColor, flags);
    }
}

/// <summary>Верхняя панель: стекло, подпись у переключателя и тонкая линия снизу.</summary>
class TopPanel : Control
{
    public string Label { get; set; } = "";
    public Point LabelAt { get; set; }

    public TopPanel() { SetStyle(Theme.Smooth, true); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Glass.Back(g, this);
        if (Label != "")
            TextRenderer.DrawText(g, Label, Theme.Body, LabelAt, Theme.Muted);
        using var pen = new Pen(Color.FromArgb(14, 255, 255, 255));
        g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}

/// <summary>Поле «Поиск и команды» — открывает палитру команд (Ctrl+K).</summary>
class PaletteTrigger : Control
{
    readonly AnimFloat hover;

    public PaletteTrigger()
    {
        SetStyle(Theme.Smooth, true);
        Size = new Size(250, 34);
        Cursor = Cursors.Hand;
        hover = new AnimFloat(0, () => Invalidate());
    }

    protected override void OnMouseEnter(EventArgs e) { hover.To(1, 150); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover.To(0, 240); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Glass.Back(g, this);
        double h = hover.Value;
        var r = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        Theme.Fill(g, Color.FromArgb((int)(12 + 10 * h), 255, 255, 255), r, 9);
        using (var pen = new Pen(Color.FromArgb((int)(20 + 110 * h), Theme.Lerp(Color.White, Theme.Accent, h))))
        using (var path = Theme.Round(r, 9))
            g.DrawPath(pen, path);
        TextRenderer.DrawText(g, "", Theme.Icon, new Rectangle(8, 0, 24, Height), Theme.Lerp(Theme.Muted, Theme.Accent, h),
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        if (Width >= 190)
            TextRenderer.DrawText(g, "Поиск и команды", Theme.Body, new Rectangle(36, 0, Width - 100, Height), Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        if (Width < 110) return;
        var cap = new RectangleF(Width - 62, 8, 52, Height - 16);
        Theme.Fill(g, Color.FromArgb(18, 255, 255, 255), cap, 5);
        TextRenderer.DrawText(g, "Ctrl K", Theme.Small, Rectangle.Round(cap), Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
    }
}

/// <summary>Строка настройки: переключатель или «Запуск», кнопка «Что меняется», подсветка при переходе.</summary>
class TweakRow : Control
{
    public readonly Tweak T;
    public readonly Toggle? Toggle;
    public readonly FlatBtn? RunBtn;
    public readonly FlatBtn? FolderBtn;
    public readonly FlatBtn InfoBtn;
    public readonly Segmented? Seg;
    public readonly Slider? Slide;
    readonly AnimFloat hover, changed, flash;
    Control Main => (Control?)Toggle ?? (Control?)RunBtn ?? (Control?)Seg ?? Slide!;

    public TweakRow(Tweak t)
    {
        T = t;
        SetStyle(Theme.Smooth, true);
        Height = Short(t) != "" ? 54 : 38;
        hover = new AnimFloat(0, () => Invalidate(true));
        changed = new AnimFloat(0, () => Invalidate(false));
        flash = new AnimFloat(0, () => Invalidate(true));

        if (t.IsAction)
        {
            RunBtn = new FlatBtn("\uE768", "Запуск") { Height = 30 };
            Controls.Add(RunBtn);
        }
        else if (t.Options != null)
        {
            Seg = new Segmented(t.Options);
            Controls.Add(Seg);
        }
        else if (t.Range != null)
        {
            var r = t.Range.Value;
            Slide = new Slider(r.Min, r.Max, r.Step, t.ValueText);
            Controls.Add(Slide);
        }
        else
        {
            Toggle = new Toggle();
            Controls.Add(Toggle);
        }
        InfoBtn = new FlatBtn("", "") { Height = 30, Width = 30 };
        Controls.Add(InfoBtn);
        if (t.OpenFolder != null)
        {
            FolderBtn = new FlatBtn("", "") { Height = 30, Width = 30 };
            Controls.Add(FolderBtn);
        }
        foreach (Control c in Controls)
        {
            c.MouseEnter += (_, _) => hover.To(1, 150);
            c.MouseLeave += (_, _) => CheckHover();
        }
    }

    /// <summary>Подсветка строки — её же видят дочерние контролы через Glass.Back.</summary>
    public Color Overlay => Color.FromArgb((int)Math.Clamp(18 * hover.Value + 46 * flash.Value, 0, 255),
        Theme.Lerp(Color.White, Theme.Accent, flash.Value));

    public bool Changed { set => changed.To(value ? 1 : 0, 220); }
    public void SetChangedNow(bool v) => changed.Set(v ? 1 : 0);

    /// <summary>Вспышка акцентом — когда пользователь пришёл к настройке из палитры команд.</summary>
    public void Flash()
    {
        flash.Set(1);
        flash.To(0, 1600, Motion.InOut);
    }

    void CheckHover()
    {
        if (!ClientRectangle.Contains(PointToClient(Cursor.Position))) hover.To(0, 260);
    }

    protected override void OnMouseEnter(EventArgs e) { hover.To(1, 150); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { CheckHover(); base.OnMouseLeave(e); }

    /// <summary>Коротко, что делает настройка: подпись (издатель) или первое предложение описания.</summary>
    static string Short(Tweak t)
    {
        if (!string.IsNullOrEmpty(t.Subtitle)) return t.Subtitle;
        var d = (t.Description ?? "").Split('\n')[0].Trim();
        int dot = d.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 20) d = d[..(dot + 1)];
        return d;
    }

    /// <summary>Метки: насколько безопасно и что потребуется.</summary>
    static List<(string Text, Color Color)> Labels(Tweak t)
    {
        var list = new List<(string, Color)>();
        if (t.Category is StartupManager.Cat || t.Category == Programs.Cat) return list;
        if (t.Risky) list.Add(("для опытных", Theme.Warn));
        else if (t.IsAction) list.Add(("разовое действие", Color.FromArgb(150, 160, 175)));
        else if (t.Revert != null || t.IsValue) list.Add(("безопасно", Theme.Good));
        if (t.NeedsRestart) list.Add(("нужна перезагрузка", Color.FromArgb(120, 170, 255)));
        return list;
    }

    int ButtonsWidth => Main.Width + 14 + InfoBtn.Width + 4 + (FolderBtn != null ? FolderBtn.Width + 4 : 0);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (InfoBtn is null) return; // раскладка может прийти из конструктора, до создания кнопок
        var main = Main;
        main.Location = new Point(Width - main.Width - (Toggle != null ? 6 : 4), (Height - main.Height) / 2);
        int x = main.Left;
        InfoBtn.Location = new Point(x - InfoBtn.Width - 4, (Height - InfoBtn.Height) / 2);
        if (FolderBtn != null) FolderBtn.Location = new Point(InfoBtn.Left - FolderBtn.Width - 4, (Height - FolderBtn.Height) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Glass.Back(g, this);
        var ov = Overlay;
        if (ov.A > 0) Theme.Fill(g, ov, new RectangleF(0, 0, Width - 1, Height - 1), 9);

        // точка «изменено, но не применено»
        double c = changed.Value;
        if (c > 0.01)
        {
            float r = (float)(3.2 * c);
            Theme.Fill(g, Color.FromArgb((int)(80 * c * Theme.AppearOf(this)), Theme.Accent), new RectangleF(12 - r - 3, Height / 2f - r - 3, (r + 3) * 2, (r + 3) * 2), r + 3);
            Theme.Fill(g, Theme.Fade(this, Theme.Accent), new RectangleF(12 - r, Height / 2f - r, r * 2, r * 2), r);
        }

        double on = Toggle?.Progress ?? 1;
        var col = Theme.Fade(this, Theme.Lerp(Theme.Muted, Theme.Text, on));
        int x = 12 + (int)Math.Round(10 * c);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis;

        if (T.Icon != null)
        {
            using var ia = new System.Drawing.Imaging.ImageAttributes();
            ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = (float)(Theme.AppearOf(this) * (0.45 + 0.55 * on)) });
            g.DrawImage(T.Icon, new Rectangle(x, (Height - 20) / 2, 20, 20), 0, 0, T.Icon.Width, T.Icon.Height, GraphicsUnit.Pixel, ia);
            x += 28;
        }

        string sub = Short(T);
        bool two = sub != "" && Height >= 50;
        var rect = new Rectangle(x, two ? 7 : 0, Width - ButtonsWidth - x, two ? 22 : Height);
        TextRenderer.DrawText(g, T.Name, Theme.Semi, rect, col, flags);
        int nameW = Math.Min(TextRenderer.MeasureText(T.Name, Theme.Semi).Width, rect.Width);
        // метки после названия
        int lx = x + nameW + 8;
        foreach (var (text, color) in Labels(T))
        {
            int w = TextRenderer.MeasureText(text, Theme.Tiny).Width + 10;
            if (lx + w > rect.Right) break;
            var pill = new RectangleF(lx, rect.Y + (rect.Height - 17) / 2f, w, 17);
            Theme.Fill(g, Color.FromArgb((int)(34 * Theme.AppearOf(this)), color), pill, 8.5f);
            TextRenderer.DrawText(g, text, Theme.Tiny, Rectangle.Round(pill), Theme.Fade(this, color),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            lx += w + 5;
        }
        if (two)
            TextRenderer.DrawText(g, sub, Theme.Tiny, new Rectangle(x, 28, rect.Width, 18), Theme.Fade(this, Theme.Muted), flags);
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
        Glass.Back(g, this);
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
        using var br = new LinearGradientBrush(new RectangleF(x - 1, 0, w + 2, Height), Color.FromArgb(0, Theme.Accent), Color.FromArgb(a, Theme.AccentHi), 0f);
        g.FillRectangle(br, x, 0, w, Height);
    }
}

/// <summary>Тёмное контекстное меню.</summary>
class DarkRenderer : ToolStripProfessionalRenderer
{
    public DarkRenderer() : base(new DarkColors()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Dim;
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

/// <summary>Выбор из вариантов: сегменты, подсветка плавно переезжает к выбранному.</summary>
class Segmented : Control
{
    readonly string[] opts;
    readonly int[] widths;
    int sel, hot = -1;
    readonly AnimFloat x, w;
    public event Action<int>? Changed;

    public Segmented(string[] options)
    {
        opts = options;
        SetStyle(Theme.Smooth, true);
        Cursor = Cursors.Hand;
        widths = opts.Select(o => TextRenderer.MeasureText(o, Theme.Tiny).Width + 18).ToArray();
        Size = new Size(widths.Sum() + 6, 30);
        x = new AnimFloat(XOf(0), () => Invalidate());
        w = new AnimFloat(widths.Length > 0 ? widths[0] : 0, () => Invalidate());
    }

    float XOf(int i) => 3 + widths.Take(i).Sum();

    public int Value
    {
        get => sel;
        set
        {
            sel = Math.Clamp(value, 0, opts.Length - 1);
            x.Set(XOf(sel));
            w.Set(widths[sel]);
        }
    }

    int At(int px)
    {
        float cx = 3;
        for (int i = 0; i < widths.Length; i++)
        {
            if (px >= cx && px < cx + widths[i]) return i;
            cx += widths[i];
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = At(e.X);
        if (h != hot) { hot = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hot = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int i = At(e.X);
        if (i >= 0 && i != sel)
        {
            sel = i;
            x.To(XOf(i), 260);
            w.To(widths[i], 260);
            Changed?.Invoke(i);
        }
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Glass.Back(g, this);
        double a = Theme.AppearOf(this);
        var track = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        Theme.Fill(g, Color.FromArgb((int)(16 * a), 255, 255, 255), track, 9);
        var pill = new RectangleF((float)x.Value, 3, (float)w.Value, Height - 7);
        Theme.Glow(g, Theme.Accent, pill, 7, 2, 1.5f, (int)(60 * a));
        Theme.Fill(g, Theme.Fade(this, Theme.AccentDim), pill, 7);
        float cx = 3;
        for (int i = 0; i < opts.Length; i++)
        {
            var col = i == sel ? Color.White : i == hot ? Theme.Text : Theme.Muted;
            TextRenderer.DrawText(g, opts[i], Theme.Tiny, new Rectangle((int)cx, 0, widths[i], Height - 1), Theme.Fade(this, col),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            cx += widths[i];
        }
    }
}

/// <summary>Ползунок со значением справа; тянется мышью, шаг задаётся настройкой.</summary>
class Slider : Control
{
    readonly int min, max, step;
    readonly Func<int, string> fmt;
    int val;
    bool drag;
    readonly AnimFloat knob, hover;
    public event Action<int>? Changed;
    const int LabelW = 92;

    public Slider(int min, int max, int step, Func<int, string> format)
    {
        this.min = min;
        this.max = Math.Max(min + 1, max);
        this.step = Math.Max(1, step);
        fmt = format;
        SetStyle(Theme.Smooth, true);
        Size = new Size(250, 30);
        Cursor = Cursors.Hand;
        knob = new AnimFloat(0, () => Invalidate());
        hover = new AnimFloat(0, () => Invalidate());
    }

    float TrackL => 8;
    float TrackR => Width - LabelW - 10;
    double Frac(int v) => (double)(v - min) / (max - min);

    public int Value
    {
        get => val;
        set
        {
            val = Math.Clamp(value, min, max);
            knob.Set(Frac(val));
        }
    }

    void SetFrom(int px, bool animate)
    {
        double f = Math.Clamp((px - TrackL) / (TrackR - TrackL), 0, 1);
        int v = min + (int)Math.Round(f * (max - min) / step) * step;
        v = Math.Clamp(v, min, max);
        if (v == val) return;
        val = v;
        if (animate) knob.To(Frac(v), 160); else knob.Set(Frac(v));
        Changed?.Invoke(v);
    }

    protected override void OnMouseEnter(EventArgs e) { hover.To(1, 150); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { if (!drag) hover.To(0, 240); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && e.X < TrackR + 8) { drag = true; Capture = true; SetFrom(e.X, true); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (drag) SetFrom(e.X, false);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        drag = false;
        Capture = false;
        if (!ClientRectangle.Contains(e.Location)) hover.To(0, 240);
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Glass.Back(g, this);
        double a = Theme.AppearOf(this), h = hover.Value;
        float cy = Height / 2f, l = TrackL, r = TrackR;
        float kx = l + (float)(knob.Value * (r - l));
        Theme.Fill(g, Color.FromArgb((int)(26 * a), 255, 255, 255), new RectangleF(l, cy - 2, r - l, 4), 2);
        var fill = new RectangleF(l, cy - 2, kx - l, 4);
        Theme.Glow(g, Theme.Accent, fill, 2, 2, 1.5f, (int)(70 * a));
        Theme.Fill(g, Theme.Fade(this, Theme.Accent), fill, 2);
        float d = 14 + (float)(2 * h);
        var kn = new RectangleF(kx - d / 2, cy - d / 2, d, d);
        Theme.Fill(g, Color.FromArgb((int)((50 + 50 * h) * a), Theme.Accent), RectangleF.Inflate(kn, 4, 4), d);
        Theme.Fill(g, Theme.Fade(this, Color.White), kn, d / 2);
        TextRenderer.DrawText(g, fmt(val), Theme.Semi, new Rectangle(Width - LabelW, 0, LabelW, Height),
            Theme.Fade(this, Theme.Text), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>
/// Вкладки раздела: «пилюли» с иконками, переносятся на новую строку, если не помещаются.
/// Подсветка выбранной вкладки переезжает со старого места, число на вкладке — сколько изменений в ней ждут «Применить».
/// </summary>
class TabStrip : FadeIn
{
    readonly (string Cat, string Glyph, string Label)[] tabs;
    readonly string selected, section;
    readonly Func<string, int> pending;
    readonly List<RectangleF> rects = new();
    readonly AnimFloat move, hoverA;
    int hover = -1;
    RectangleF from;
    static (string Section, RectangleF Rect)? last;
    public event Action<string>? Picked;
    const int TabH = 34, Gap = 6;

    public TabStrip(string section, (string Cat, string Glyph, string Label)[] tabs, string selected, Func<string, int> pending)
    {
        this.section = section;
        this.tabs = tabs;
        this.selected = selected;
        this.pending = pending;
        // анимации создаём до первой смены размера: Height вызывает Arrange
        move = new AnimFloat(1, () => Invalidate());
        hoverA = new AnimFloat(0, () => Invalidate());
        Wide = true;
        Cursor = Cursors.Hand;
        Height = TabH + 12;
    }

    bool arranging;

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (tabs == null || move == null || arranging) return;
        Arrange();
    }

    void Arrange()
    {
        if (arranging) return;
        arranging = true;
        try { ArrangeCore(); }
        finally { arranging = false; }
    }

    void ArrangeCore()
    {
        rects.Clear();
        float x = 0, y = 2;
        foreach (var t in tabs)
        {
            float w = TextRenderer.MeasureText(t.Label, Theme.Semi).Width + 50;
            if (x > 0 && x + w > Width) { x = 0; y += TabH + Gap; }
            rects.Add(new RectangleF(x, y, w, TabH));
            x += w + Gap;
        }
        int h = (int)(y + TabH + 12);
        if (Height != h) Height = h;
        // подсветка переезжает с прошлой вкладки этого раздела
        int i = Array.FindIndex(tabs, t => t.Cat == selected);
        if (Width <= 0) return; // ещё не разложено — подсветку переносим при настоящей ширине
        if (i >= 0 && last is { } l && l.Section == section && l.Rect != rects[i] && move.Value >= 1)
        {
            from = l.Rect;
            move.Set(0);
            move.To(1, 380);
        }
        if (i >= 0) last = (section, rects[i]);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = rects.FindIndex(r => r.Contains(e.Location));
        if (h != hover) { hover = h; hoverA.Set(0); if (h >= 0) hoverA.To(1, 160); Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int i = rects.FindIndex(r => r.Contains(e.Location));
        if (i >= 0 && tabs[i].Cat != selected) Picked?.Invoke(tabs[i].Cat);
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Glass.Back(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (rects.Count != tabs.Length) Arrange();
        int sel = Array.FindIndex(tabs, t => t.Cat == selected);
        if (sel >= 0)
        {
            double p = move.Value;
            var to = rects[sel];
            var r = p >= 1 ? to : new RectangleF(
                (float)(from.X + (to.X - from.X) * p), (float)(from.Y + (to.Y - from.Y) * p),
                (float)(from.Width + (to.Width - from.Width) * p), TabH);
            Theme.Glow(g, Theme.Accent, r, 10, 3, 2f, (int)(50 * Appear));
            using var path = Theme.Round(r, 10);
            using var b = new LinearGradientBrush(r, Theme.Fade(this, Theme.AccentHi), Theme.Fade(this, Theme.Accent), 90f);
            g.FillPath(b, path);
        }
        for (int i = 0; i < tabs.Length; i++)
        {
            var (cat, glyph, label) = tabs[i];
            var r = rects[i];
            bool on = i == sel;
            if (!on)
            {
                double hv = i == hover ? hoverA.Value : 0;
                Theme.Fill(g, Color.FromArgb((int)((16 + 16 * hv) * Appear), 255, 255, 255), r, 10);
            }
            var fg = Theme.Fade(this, on ? Color.White : i == hover ? Theme.Text : Color.FromArgb(185, 188, 202));
            TextRenderer.DrawText(g, glyph, Theme.Icon, new Rectangle((int)r.X + 10, (int)r.Y, 22, TabH), fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, label, Theme.Semi, new Rectangle((int)r.X + 36, (int)r.Y, (int)r.Width - 40, TabH), fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            int n = pending(cat);
            if (n > 0)
            {
                string c = n > 99 ? "99+" : n.ToString();
                int cw = Math.Max(18, TextRenderer.MeasureText(c, Theme.Small).Width + 8);
                var pill = new RectangleF(r.Right - cw + 6, r.Y - 6, cw, 17);
                Theme.Fill(g, on ? Color.White : Theme.Accent, pill, 8.5f);
                TextRenderer.DrawText(g, c, Theme.Small, Rectangle.Round(pill), on ? Theme.Accent : Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }
}
