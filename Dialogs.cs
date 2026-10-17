using System.Drawing.Drawing2D;

namespace WinTweaker;

/// <summary>Настройки самой программы: %AppData%\WinTweaker\settings.ini (key=value).</summary>
static class AppSettings
{
    static readonly string File_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinTweaker", "settings.ini");

    // Читается с диска один раз и живёт в памяти: Get вызывается и из потока виджетов, и часто
    static Dictionary<string, string>? cache;
    static readonly object gate = new();

    static Dictionary<string, string> Load()
    {
        if (cache != null) return cache;
        try
        {
            cache = File.Exists(File_)
                ? File.ReadAllLines(File_).Select(l => l.Split('=', 2)).Where(p => p.Length == 2)
                    .GroupBy(p => p[0].Trim()).ToDictionary(g => g.Key, g => g.Last()[1].Trim())
                : new();
        }
        catch { cache = new(); }
        return cache;
    }

    public static string Get(string key, string def)
    {
        lock (gate) return Load().GetValueOrDefault(key, def);
    }

    public static void Set(string key, string value)
    {
        lock (gate)
        {
            var s = Load();
            if (s.TryGetValue(key, out var old) && old == value) return;
            s[key] = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
                File.WriteAllLines(File_, s.Select(kv => kv.Key + "=" + kv.Value));
            }
            catch { /* настройки не критичны */ }
        }
    }
}

/// <summary>
/// Углы окон без лесенки. Обрезка окна регионом (Region) не сглаживается — по краю видны ступеньки.
/// Windows 11 умеет скруглять окна сама, гладко и с тенью; на Windows 10 такого нет — там углы прямые с тонкой рамкой,
/// как у системных окон.
/// </summary>
static class WinCorners
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    /// <summary>Windows 11 (сборка 22000+) — скругляет сама.</summary>
    public static readonly bool Native = Environment.OSVersion.Version.Build >= 22000;
    /// <summary>Радиус для рамки, которую рисует само окно: 0 — прямые углы (Windows 10).</summary>
    public static float Radius => Native ? 8 : 0;

    public static void Apply(Form f, Color border)
    {
        if (!Native || !f.IsHandleCreated) return;
        try
        {
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(f.Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, 4);
            int color = border.R | border.G << 8 | border.B << 16;
            DwmSetWindowAttribute(f.Handle, 34 /* DWMWA_BORDER_COLOR */, ref color, 4);
        }
        catch { }
    }
}

/// <summary>Общая основа для окон без рамки: гладкие углы, тень.</summary>
abstract class RoundForm : Form
{
    protected RoundForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Theme.Card;
        DoubleBuffered = true;
        Font = Theme.Body;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WinCorners.Apply(this, Theme.Line);
    }

    // ───── справочные окна закрываются кликом в любом месте вне окна ─────

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

    /// <summary>Справочное окно: клик мышью в любом месте экрана вне окна закрывает его (как «Понятно»).</summary>
    protected bool CloseOnClickOutside { get; set; }
    System.Windows.Forms.Timer? outside;
    bool wasDown = true; // кнопка могла быть ещё нажата после клика, открывшего окно

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!CloseOnClickOutside) return;
        outside = new System.Windows.Forms.Timer { Interval = 30 };
        outside.Tick += (_, _) =>
        {
            bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (down && !wasDown && !Bounds.Contains(Cursor.Position))
            {
                outside.Stop();
                ClickedOutside();
            }
            wasDown = down;
        };
        outside.Start();
    }

    /// <summary>Что сделать при клике вне окна (по умолчанию — закрыть).</summary>
    protected virtual void ClickedOutside() => Close();

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        outside?.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Card);
        if (WinCorners.Native) return; // рамку и скругление рисует Windows 11
        using var pen = new Pen(Theme.Line);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

public enum Tone { Info, Success, Warning, Error }

/// <summary>Тёмный диалог вместо MessageBox: проявляется и всплывает на 14 px.</summary>
class Dialog : RoundForm
{
    readonly string title, message;
    readonly Tone tone;
    int baseTop;

    Dialog(string title, string message, Tone tone, string ok, string? cancel)
    {
        this.title = title;
        this.message = message;
        this.tone = tone;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Opacity = 0;

        const int w = 440, pad = 24;
        var textSize = TextRenderer.MeasureText(message, Theme.Body, new Size(w - pad * 2 - 44, 0), TextFormatFlags.WordBreak);
        Size = new Size(w, Math.Max(150, 70 + textSize.Height + 76));

        CloseOnClickOutside = cancel == null; // сообщение без выбора — справочное
        var okBtn = new FlatBtn("\uE73E", ok) { Primary = true };
        okBtn.Click += (_, _) => Finish(DialogResult.OK);
        okBtn.Location = new Point(Width - pad - okBtn.Width, Height - pad - okBtn.Height);
        Controls.Add(okBtn);

        if (cancel != null)
        {
            var no = new FlatBtn("", cancel);
            no.Click += (_, _) => Finish(DialogResult.Cancel);
            no.Location = new Point(okBtn.Left + okBtn.Pad - no.Width - 8, okBtn.Top + okBtn.Pad);
            Controls.Add(no);
        }

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) Finish(DialogResult.OK);
            if (e.KeyCode == Keys.Escape) Finish(cancel != null ? DialogResult.Cancel : DialogResult.OK);
        };
    }

    static string Glyph(Tone t) => t switch
    {
        Tone.Success => "",
        Tone.Warning => "",
        Tone.Error => "",
        _ => ""
    };

    static Color ToneColor(Tone t) => t switch
    {
        Tone.Warning => Theme.Warn,
        Tone.Error => Color.FromArgb(255, 90, 90),
        _ => Theme.Accent
    };

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Owner != null)
            Location = new Point(Owner.Left + (Owner.Width - Width) / 2, Owner.Top + (Owner.Height - Height) / 2);
        baseTop = Top;
        Top = baseTop + 14;
        Motion.Run(260, p => { Opacity = p; Top = baseTop + (int)Math.Round(14 * (1 - p)); });
    }

    bool closing;

    protected override void ClickedOutside() => Finish(DialogResult.OK);

    void Finish(DialogResult r)
    {
        if (closing) return;
        closing = true;
        Motion.Run(150, p => Opacity = 1 - p, Motion.InOut, 0, () => { DialogResult = r; Close(); });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var c = ToneColor(tone);
        using (var b = new SolidBrush(Theme.Lerp(Theme.Card, c, 0.18))) g.FillEllipse(b, 24, 24, 34, 34);
        TextRenderer.DrawText(g, Glyph(tone), Theme.Icon, new Rectangle(24, 24, 34, 34), c,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(g, title, Theme.F(12f, FontStyle.Bold), new Point(70, 22), Theme.Text);
        TextRenderer.DrawText(g, message, Theme.Body, new Rectangle(70, 52, Width - 94, Height - 120), Theme.Muted,
            TextFormatFlags.WordBreak);
    }

    public static bool Confirm(IWin32Window? owner, string title, string message,
        string ok = "Продолжить", string cancel = "Отмена", Tone tone = Tone.Info)
    {
        using var d = new Dialog(title, message, tone, ok, cancel);
        return d.ShowDialog(owner) == DialogResult.OK;
    }

    public static void Info(IWin32Window? owner, string title, string message, Tone tone = Tone.Info)
    {
        using var d = new Dialog(title, message, tone, "Понятно", null);
        d.ShowDialog(owner);
    }

    /// <summary>Показ из фонового потока — через UI-поток главного окна.</summary>
    public static void InfoFromAnyThread(string title, string message, Tone tone = Tone.Info)
    {
        var main = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
        if (main == null) return;
        if (main.InvokeRequired) main.Invoke(() => Info(main, title, message, tone));
        else Info(main, title, message, tone);
    }
}

/// <summary>Тост в правом нижнем углу окна: выезжает, ждёт и исчезает. Несколько тостов складываются стопкой.</summary>
class Toast : RoundForm
{
    static readonly List<Toast> active = new();
    readonly string text;
    readonly Tone tone;
    readonly Form owner;
    readonly AnimFloat life;
    double slide;

    Toast(Form owner, string text, Tone tone)
    {
        this.owner = owner;
        this.text = text;
        this.tone = tone;
        StartPosition = FormStartPosition.Manual;
        Opacity = 0;
        var sz = TextRenderer.MeasureText(text, Theme.Body, new Size(280, 0), TextFormatFlags.WordBreak);
        Size = new Size(Math.Max(220, sz.Width + 70), sz.Height + 30);
        life = new AnimFloat(1, () => Invalidate());
        Click += (_, _) => Dismiss();
        Cursor = Cursors.Hand;
    }

    protected override bool ShowWithoutActivation => true;

    public static void Pop(Form owner, string text, Tone tone = Tone.Success)
    {
        if (owner.IsDisposed) return;
        var t = new Toast(owner, text, tone);
        active.Add(t);
        t.Show(owner);
        Restack();
        t.Motion_In();
    }

    void Motion_In()
    {
        Motion.Run(320, p => { slide = p; Opacity = p; Restack(); });
        life.To(0, 4000, x => x);             // полоска оставшегося времени
        Motion.Run(1, _ => { }, null, 4000, Dismiss);
    }

    bool leaving;

    void Dismiss()
    {
        if (leaving || IsDisposed) return;
        leaving = true;
        Motion.Run(220, p => { if (!IsDisposed) Opacity = (1 - p) * slide; }, Motion.InOut, 0, () =>
        {
            active.Remove(this);
            Close();
            Dispose();
            Restack();
        });
    }

    static void Restack()
    {
        int y = 0;
        for (int i = active.Count - 1; i >= 0; i--)
        {
            var t = active[i];
            if (t.IsDisposed) continue;
            int right = t.owner.Right - 18, bottom = t.owner.Bottom - 18 - y;
            t.Location = new Point(right - t.Width + (int)Math.Round(24 * (1 - t.slide)), bottom - t.Height);
            y += t.Height + 8;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var c = tone switch
        {
            Tone.Error => Color.FromArgb(255, 90, 90),
            Tone.Warning => Theme.Warn,
            _ => Theme.Accent
        };
        string glyph = tone switch { Tone.Success => "", Tone.Warning => "", Tone.Error => "", _ => "" };
        TextRenderer.DrawText(g, glyph, Theme.Icon, new Rectangle(14, 0, 22, Height), c,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(g, text, Theme.Body, new Rectangle(44, 15, Width - 58, Height - 28), Theme.Text, TextFormatFlags.WordBreak);
        using var b = new SolidBrush(Color.FromArgb(160, c));
        g.FillRectangle(b, 12, Height - 3, (float)((Width - 24) * life.Value), 2);
    }
}

/// <summary>Выбор цвета акцента: 5 точек, выбранная обведена кольцом, при наведении точка растёт.</summary>
class AccentDots : Control
{
    readonly AnimFloat[] grow;
    int selected, hover = -1;
    const int D = 14, Gap = 10, X0 = 8;
    public event Action<int>? Picked;

    public AccentDots(int selected)
    {
        this.selected = selected;
        SetStyle(Theme.Smooth, true);
        grow = Theme.Accents.Select(_ => new AnimFloat(0, () => Invalidate())).ToArray();
        Size = new Size(X0 * 2 + Theme.Accents.Length * (D + Gap) - Gap, 30);
        Cursor = Cursors.Hand;
    }

    int Hit(Point p)
    {
        int i = (p.X - X0 + Gap / 2) / (D + Gap);
        return i >= 0 && i < grow.Length ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = Hit(e.Location);
        if (h != hover)
        {
            if (hover >= 0) grow[hover].To(0, 200);
            if (h >= 0) grow[h].To(1, 150);
            hover = h;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (hover >= 0) grow[hover].To(0, 200);
        hover = -1;
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int h = Hit(e.Location);
        if (h >= 0 && h != selected)
        {
            selected = h;
            Invalidate();
            Picked?.Invoke(h);
        }
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Glass.Back(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < grow.Length; i++)
        {
            float cx = X0 + i * (D + Gap) + D / 2f, cy = Height / 2f;
            float r = (float)(D / 2f + 2 * grow[i].Value);
            using var b = new SolidBrush(Theme.Accents[i].Color);
            g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            if (i == selected)
            {
                using var pen = new Pen(Theme.Text, 1.5f);
                g.DrawEllipse(pen, cx - r - 3, cy - r - 3, (r + 3) * 2, (r + 3) * 2);
            }
        }
    }

    public static string NameOf(int i) => Theme.Accents[i].Name;
}

/// <summary>«Что именно меняется»: моноширинный текст с ключами реестра и командами, кнопка «Копировать».</summary>
class DetailsDialog : RoundForm
{
    readonly string title, sub;
    int baseTop;
    bool closing;

    DetailsDialog(string title, string sub, string text)
    {
        this.title = title;
        this.sub = sub;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Opacity = 0;

        CloseOnClickOutside = true; // справочное окно — закрывается кликом мимо
        var lines = text.Split('\n');
        int h = Math.Clamp(lines.Length * 18 + 24, 90, 380);
        Size = new Size(640, 92 + h + 72);

        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, WordWrap = true,
            BackColor = Color.FromArgb(16, 17, 24), ForeColor = Color.FromArgb(200, 220, 255), Font = Theme.Mono,
            Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            ScrollBars = lines.Length * 18 + 24 > 380 ? ScrollBars.Vertical : ScrollBars.None
        };
        box.SetBounds(36, 92 + 12, Width - 72, h - 24);
        Controls.Add(box);

        var close = new FlatBtn("", "Понятно") { Primary = true };
        close.Location = new Point(Width - 24 - close.Width + close.Pad, Height - 22 - close.Height + close.Pad);
        close.Click += (_, _) => Finish();
        Controls.Add(close);

        var copy = new FlatBtn("", "Копировать");
        copy.Location = new Point(close.Left + close.Pad - copy.Width - 8, close.Top + close.Pad);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(text); } catch { }
            if (Owner != null) Toast.Pop(Owner, "Скопировано в буфер обмена", Tone.Info);
        };
        Controls.Add(copy);

        KeyDown += (_, e) => { if (e.KeyCode is Keys.Escape or Keys.Enter) Finish(); };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Owner != null)
            Location = new Point(Owner.Left + (Owner.Width - Width) / 2, Owner.Top + (Owner.Height - Height) / 2);
        baseTop = Top;
        Motion.Run(240, p => { Opacity = p; Top = baseTop + (int)Math.Round(12 * (1 - p)); });
    }

    protected override void ClickedOutside() => Finish();

    void Finish()
    {
        if (closing) return;
        closing = true;
        Motion.Run(140, p => Opacity = 1 - p, Motion.InOut, 0, Close);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var icon = new RectangleF(24, 24, 36, 36);
        Theme.Fill(g, Theme.Lerp(Theme.Card, Theme.Accent, 0.2), icon, 18);
        TextRenderer.DrawText(g, "", Theme.Icon, Rectangle.Round(icon), Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, title, Theme.F(12f, FontStyle.Bold), new Rectangle(72, 22, Width - 96, 24), Theme.Text, TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, sub, Theme.Tiny, new Rectangle(72, 46, Width - 96, 20), Theme.Muted, TextFormatFlags.EndEllipsis);
        var code = new RectangleF(24, 92, Width - 48, Height - 92 - 72);
        Theme.Fill(g, Color.FromArgb(16, 17, 24), code, 10);
        using var pen = new Pen(Theme.Line);
        using var path = Theme.Round(code, 10);
        g.DrawPath(pen, path);
    }

    public static void Show(IWin32Window owner, string title, string sub, string text)
    {
        using var d = new DetailsDialog(title, sub, text);
        d.ShowDialog(owner);
    }
}

/// <summary>Ввод короткой строки (например, имя набора) в стиле программы.</summary>
class Prompt : RoundForm
{
    readonly string title, message;
    readonly TextBox box = new();
    int baseTop;
    bool closing;

    Prompt(string title, string message, string value)
    {
        this.title = title;
        this.message = message;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Opacity = 0;
        Size = new Size(460, 220);

        box.BorderStyle = BorderStyle.None;
        box.BackColor = Theme.Hover;
        box.ForeColor = Theme.Text;
        box.Font = Theme.F(11f);
        box.Text = value;
        box.SetBounds(38, 104, Width - 76, box.PreferredHeight);
        Controls.Add(box);

        var ok = new FlatBtn("", "Сохранить") { Primary = true };
        ok.Location = new Point(Width - 24 - ok.Width + ok.Pad, Height - 22 - ok.Height + ok.Pad);
        ok.Click += (_, _) => Finish(DialogResult.OK);
        Controls.Add(ok);
        var no = new FlatBtn("", "Отмена");
        no.Location = new Point(ok.Left + ok.Pad - no.Width - 8, ok.Top + ok.Pad);
        no.Click += (_, _) => Finish(DialogResult.Cancel);
        Controls.Add(no);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Finish(DialogResult.OK); e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.Escape) Finish(DialogResult.Cancel);
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Owner != null)
            Location = new Point(Owner.Left + (Owner.Width - Width) / 2, Owner.Top + (Owner.Height - Height) / 2);
        baseTop = Top;
        box.Focus();
        box.SelectAll();
        Motion.Run(220, p => { Opacity = p; Top = baseTop + (int)Math.Round(12 * (1 - p)); });
    }

    void Finish(DialogResult r)
    {
        if (closing) return;
        closing = true;
        Motion.Run(130, p => Opacity = 1 - p, Motion.InOut, 0, () => { DialogResult = r; Close(); });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        TextRenderer.DrawText(g, title, Theme.F(12f, FontStyle.Bold), new Point(24, 22), Theme.Text);
        TextRenderer.DrawText(g, message, Theme.Body, new Rectangle(24, 50, Width - 48, 40), Theme.Muted, TextFormatFlags.WordBreak);
        var field = new RectangleF(24, 94, Width - 48, 40);
        Theme.Fill(g, Theme.Hover, field, 9);
        using var pen = new Pen(Color.FromArgb(160, Theme.Accent), 1.2f);
        using var path = Theme.Round(field, 9);
        g.DrawPath(pen, path);
    }

    public static string? Ask(IWin32Window owner, string title, string message, string value = "")
    {
        using var p = new Prompt(title, message, value);
        return p.ShowDialog(owner) == DialogResult.OK ? p.box.Text : null;
    }
}

/// <summary>Одно изменение в окне «Изменения к применению».</summary>
sealed class ChangeItem
{
    public Tweak Tweak = null!;
    public string From = "", To = "";
    public bool TurnsOn;
}

/// <summary>
/// Окно «Изменения к применению»: что, где и как изменится; × — убрать изменение,
/// { } — точные ключи реестра и команды. Отсюда же — применить или сбросить всё.
/// </summary>
class ChangesDialog : RoundForm
{
    readonly List<ChangeItem> items;
    readonly ChangesList list;
    readonly Action<Tweak> discard;
    readonly Action<Tweak> details;
    readonly FlatBtn ok, reset;
    int baseTop;
    bool closing;
    public bool ResetAll { get; private set; }

    const int W = 680, Head = 84, Foot = 74;

    public ChangesDialog(List<ChangeItem> items, Action<Tweak> discard, Action<Tweak> details)
    {
        this.items = items;
        this.discard = discard;
        this.details = details;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Opacity = 0;

        list = new ChangesList(items);
        list.Remove += Remove;
        list.Details += t => details(t);
        Controls.Add(list);

        ok = new FlatBtn("", "Применить") { Primary = true };
        ok.Click += (_, _) => Finish(DialogResult.OK);
        Controls.Add(ok);
        var cancel = new FlatBtn("", "Закрыть");
        cancel.Click += (_, _) => Finish(DialogResult.Cancel);
        Controls.Add(cancel);
        reset = new FlatBtn("", "Сбросить все") { Danger = true };
        reset.Click += (_, _) => { ResetAll = true; Finish(DialogResult.Abort); };
        Controls.Add(reset);

        Resize_();
        ok.Location = new Point(Width - 24 - ok.Width + ok.Pad, Height - 22 - ok.Height + ok.Pad);
        cancel.Location = new Point(ok.Left + ok.Pad - cancel.Width - 8, ok.Top + ok.Pad);
        reset.Location = new Point(24, ok.Top + ok.Pad);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) Finish(DialogResult.OK);
            if (e.KeyCode == Keys.Escape) Finish(DialogResult.Cancel);
        };
    }

    void Resize_()
    {
        int rows = Math.Clamp(items.Count, 1, 9);
        Size = new Size(W, Head + rows * ChangesList.RowH + 8 + Foot);
        list.SetBounds(16, Head, W - 32, rows * ChangesList.RowH);
        if (ok != null) ok.Location = new Point(Width - 24 - ok.Width + ok.Pad, Height - 22 - ok.Height + ok.Pad);
    }

    void Remove(ChangeItem it)
    {
        discard(it.Tweak);
        items.Remove(it);
        if (items.Count == 0) { Finish(DialogResult.Cancel); return; }
        list.Refresh_();
        Invalidate();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Owner != null)
            Location = new Point(Owner.Left + (Owner.Width - Width) / 2, Owner.Top + Math.Max(40, (Owner.Height - Height) / 2));
        baseTop = Top;
        Motion.Run(240, p => { Opacity = p; Top = baseTop + (int)Math.Round(12 * (1 - p)); });
    }

    void Finish(DialogResult r)
    {
        if (closing) return;
        closing = true;
        Motion.Run(140, p => Opacity = 1 - p, Motion.InOut, 0, () => { DialogResult = r; Close(); });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var icon = new RectangleF(24, 22, 38, 38);
        Theme.Fill(g, Theme.Lerp(Theme.Card, Theme.Accent, 0.22), icon, 19);
        TextRenderer.DrawText(g, items.Count.ToString(), Theme.F(12f, FontStyle.Bold), Rectangle.Round(icon), Theme.Accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "Изменения к применению", Theme.F(12f, FontStyle.Bold), new Point(74, 20), Theme.Text);
        int risky = items.Count(i => i.Tweak.Risky && i.TurnsOn);
        int restart = items.Count(i => i.Tweak.NeedsRestart);
        string sub = "× — убрать изменение · { } — что именно меняется";
        if (risky > 0) sub = $"⚠ рискованных: {risky} · " + sub;
        if (restart > 0) sub = $"после перезагрузки: {restart} · " + sub;
        TextRenderer.DrawText(g, sub, Theme.Tiny, new Rectangle(76, 46, Width - 100, 18), risky > 0 ? Theme.Warn : Theme.Muted, TextFormatFlags.EndEllipsis);
        using var pen = new Pen(Theme.Line);
        g.DrawLine(pen, 0, Head - 8, Width, Head - 8);
        g.DrawLine(pen, 0, Height - Foot + 4, Width, Height - Foot + 4);
        TextRenderer.DrawText(g, "Всё попадёт в «Историю» — откат в один клик", Theme.Tiny,
            new Rectangle(reset.Right + 12, Height - Foot + 4, Math.Max(0, Width - reset.Right - 12 - (Width - (ok.Left + ok.Pad)) - 160), Foot - 4),
            Theme.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Список изменений с прокруткой колесом.</summary>
class ChangesList : Control, IWheelTarget
{
    public const int RowH = 52;
    readonly List<ChangeItem> items;
    int top, hover = -1, hoverBtn = -1; // 0 — детали, 1 — убрать
    public event Action<ChangeItem>? Remove;
    public event Action<Tweak>? Details;

    public ChangesList(List<ChangeItem> items)
    {
        this.items = items;
        SetStyle(Theme.Smooth, true);
    }

    int Visible => Math.Max(1, Height / RowH);
    public bool CanScroll => items.Count > Visible;

    public void Wheel(int delta)
    {
        top = Math.Clamp(top - Math.Sign(delta), 0, Math.Max(0, items.Count - Visible));
        Invalidate();
    }

    public void Refresh_()
    {
        top = Math.Clamp(top, 0, Math.Max(0, items.Count - Visible));
        Invalidate();
    }

    Rectangle XBtn(int row) => new(Width - 40, row * RowH + (RowH - 30) / 2, 30, 30);
    Rectangle InfoBtn(int row) => new(Width - 76, row * RowH + (RowH - 30) / 2, 30, 30);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int row = e.Y / RowH, i = top + row;
        int h = i < items.Count ? i : -1;
        int b = h < 0 ? -1 : XBtn(row).Contains(e.Location) ? 1 : InfoBtn(row).Contains(e.Location) ? 0 : -1;
        if (h != hover || b != hoverBtn) { hover = h; hoverBtn = b; Cursor = b >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; hoverBtn = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int row = e.Y / RowH, i = top + row;
        if (i < items.Count)
        {
            if (XBtn(row).Contains(e.Location)) Remove?.Invoke(items[i]);
            else if (InfoBtn(row).Contains(e.Location)) Details?.Invoke(items[i].Tweak);
        }
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Card);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        for (int r = 0; r < Visible && top + r < items.Count; r++)
        {
            var it = items[top + r];
            int y = r * RowH;
            bool hot = top + r == hover;
            if (hot) Theme.Fill(g, Theme.Hover, new RectangleF(0, y + 2, Width - 1, RowH - 4), 9);

            var dot = new RectangleF(12, y + RowH / 2f - 4, 8, 8);
            var col = it.Tweak.Risky && it.TurnsOn ? Theme.Warn : it.TurnsOn ? Theme.Accent : Theme.Muted;
            Theme.Fill(g, Color.FromArgb(70, col), RectangleF.Inflate(dot, 3, 3), 7);
            Theme.Fill(g, col, dot, 4);

            string change = $"{it.From}  →  {it.To}";
            int cw = Math.Min(200, TextRenderer.MeasureText(change, Theme.Semi).Width + 4);
            int nameW = Width - 32 - cw - 96;
            TextRenderer.DrawText(g, it.Tweak.Name + (it.Tweak.Risky && it.TurnsOn ? "  ⚠" : ""), Theme.Semi,
                new Rectangle(32, y + 6, nameW, 22), Theme.Text, flags);
            TextRenderer.DrawText(g, it.Tweak.Category + (it.Tweak.Group != null ? " · " + it.Tweak.Group : ""), Theme.Tiny,
                new Rectangle(32, y + 26, nameW, 18), Theme.Muted, flags);
            TextRenderer.DrawText(g, change, Theme.Semi, new Rectangle(Width - 86 - cw, y, cw, RowH), it.TurnsOn ? Theme.Accent : Theme.Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (hot)
            {
                var ib = InfoBtn(r);
                if (hoverBtn == 0) Theme.Fill(g, Theme.Line, ib, 8);
                TextRenderer.DrawText(g, "", Theme.Icon, ib, hoverBtn == 0 ? Theme.Text : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            var xb = XBtn(r);
            if (hot && hoverBtn == 1) Theme.Fill(g, Theme.Bad, xb, 8);
            TextRenderer.DrawText(g, "", Theme.Icon, xb, hot && hoverBtn == 1 ? Color.White : Theme.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        if (CanScroll)
        {
            float h = Height * (float)Visible / items.Count, yb = Height * (float)top / items.Count;
            Theme.Fill(g, Theme.Line, new RectangleF(Width - 3, yb, 3, h), 1.5f);
        }
    }
}
