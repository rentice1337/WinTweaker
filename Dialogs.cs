using System.Drawing.Drawing2D;

namespace WinTweaker;

/// <summary>Настройки самой программы: %AppData%\WinTweaker\settings.ini (key=value).</summary>
static class AppSettings
{
    static readonly string File_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinTweaker", "settings.ini");

    static Dictionary<string, string> Load()
    {
        try
        {
            return File.Exists(File_)
                ? File.ReadAllLines(File_).Select(l => l.Split('=', 2)).Where(p => p.Length == 2)
                    .ToDictionary(p => p[0].Trim(), p => p[1].Trim())
                : new();
        }
        catch { return new(); }
    }

    public static string Get(string key, string def) => Load().GetValueOrDefault(key, def);

    public static void Set(string key, string value)
    {
        try
        {
            var s = Load();
            s[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllLines(File_, s.Select(kv => kv.Key + "=" + kv.Value));
        }
        catch { /* настройки не критичны */ }
    }
}

/// <summary>Общая основа для окон без рамки: скругление, тень, сглаживание.</summary>
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

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        using var p = Theme.Round(new RectangleF(0, 0, Width, Height), 12);
        Region = new Region(p);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Card);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Theme.Line);
        using var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), 12);
        e.Graphics.DrawPath(pen, path);
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
