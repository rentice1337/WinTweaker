using System.Drawing.Drawing2D;

namespace WinTweaker;

/// <summary>
/// Панель внизу страницы, когда что-то отмечено, но не применено: сколько изменений,
/// «Сбросить» и «Применить». Выезжает снизу и уезжает, когда отметок нет.
/// </summary>
class PendingBar : Control
{
    public readonly FlatBtn Apply = new("", "Применить") { Primary = true };
    public readonly FlatBtn Reset = new("", "Сбросить");
    public readonly FlatBtn Review = new("", "Посмотреть");
    readonly AnimFloat show;
    int count;
    const int H = 60, W = 560;

    public PendingBar()
    {
        SetStyle(Theme.Smooth, true);
        Size = new Size(W, H + 16);
        Visible = false;
        Controls.Add(Apply);
        Controls.Add(Review);
        Controls.Add(Reset);
        show = new AnimFloat(0, () => { Reposition(); Invalidate(true); });
    }

    public int Count
    {
        get => count;
        set
        {
            if (count == value) return;
            bool was = count > 0;
            count = value;
            Invalidate(true);
            if (value > 0 && !was) { Visible = true; BringToFront(); show.To(1, 380); }
            else if (value == 0 && was) show.To(0, 260, Motion.InOut);
        }
    }

    /// <summary>По центру снизу родителя, со сдвигом при появлении.</summary>
    public void Reposition()
    {
        if (Parent == null) return;
        int y = Parent.ClientSize.Height - (int)Math.Round((H + 18) * show.Value);
        Location = new Point((Parent.ClientSize.Width - Width) / 2, y);
        if (show.Value <= 0.001 && count == 0) Visible = false;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Apply is null || Reset is null || Review is null) return;
        Apply.Location = new Point(Width - Apply.Width - 8, (H - Apply.Height) / 2);
        Review.Location = new Point(Apply.Left + Apply.Pad - Review.Width - 8, (H - Review.Height) / 2);
        Reset.Location = new Point(Review.Left - Reset.Width - 6, (H - Reset.Height) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Glass.Back(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 2, H - 1);
        Theme.Glow(g, Theme.Accent, r, 16, 3, 2.5f, 40);
        using (var path = Theme.Round(r, 16))
        {
            using var b = new SolidBrush(Color.FromArgb(245, 22, 23, 32));
            g.FillPath(b, path);
            using var pen = new Pen(Color.FromArgb(120, Theme.Accent));
            g.DrawPath(pen, path);
        }
        string word = count % 10 == 1 && count % 100 != 11 ? "изменение" : count % 10 is >= 2 and <= 4 && (count % 100 < 10 || count % 100 >= 20) ? "изменения" : "изменений";
        TextRenderer.DrawText(g, $"Отмечено {count} {word}", Theme.Semi, new Rectangle(20, 10, Reset.Left - 24, 22), Theme.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, "Сработают после «Применить» · всё откатывается в «Истории»", Theme.Tiny, new Rectangle(20, 31, Reset.Left - 24, 18), Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Первый запуск: работа в трее и автозапуск — то, без чего не работают игровой режим и оверлей.</summary>
class WelcomeDialog : RoundForm
{
    public readonly Toggle Tray = new() { Checked = true };
    public readonly Toggle Autostart = new() { Checked = true };
    bool closing;
    int baseTop;

    public WelcomeDialog()
    {
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Opacity = 0;
        Size = new Size(560, 400);
        Tray.Location = new Point(Width - 24 - Tray.Width, 176);
        Autostart.Location = new Point(Width - 24 - Autostart.Width, 236);
        Controls.Add(Tray);
        Controls.Add(Autostart);
        var go = new FlatBtn("", "Начать") { Primary = true };
        go.Location = new Point(Width - 24 - go.Width + go.Pad, Height - 22 - go.Height + go.Pad);
        go.Click += (_, _) => Finish(DialogResult.OK);
        Controls.Add(go);
        KeyDown += (_, e) => { if (e.KeyCode is Keys.Enter) Finish(DialogResult.OK); };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Owner != null) Location = new Point(Owner.Left + (Owner.Width - Width) / 2, Owner.Top + (Owner.Height - Height) / 2);
        baseTop = Top;
        Motion.Run(320, p => { Opacity = p; Top = baseTop + (int)Math.Round(16 * (1 - p)); });
    }

    void Finish(DialogResult r)
    {
        if (closing) return;
        closing = true;
        Motion.Run(160, p => Opacity = 1 - p, Motion.InOut, 0, () => { DialogResult = r; Close(); });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Logo.Tile(g, new RectangleF(24, 24, 46, 46));
        TextRenderer.DrawText(g, "Добро пожаловать в WinTweak", Theme.F(15f, FontStyle.Bold), new Point(84, 24), Theme.Text);
        TextRenderer.DrawText(g, "Настройка Windows и игр в одном окне. Ничего не меняется без вашего «Применить», и всё можно откатить.",
            Theme.Body, new Rectangle(86, 54, Width - 110, 44), Theme.Muted, TextFormatFlags.WordBreak);
        using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 24, 136, Width - 24, 136);
        TextRenderer.DrawText(g, "Работать в трее", Theme.Semi, new Point(24, 170), Theme.Text);
        TextRenderer.DrawText(g, "Крестик прячет окно; в трее — значок и круговой пульт (Ctrl+Alt+W)", Theme.Tiny, new Rectangle(24, 192, Width - 110, 18), Theme.Muted, TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, "Запускать вместе с Windows", Theme.Semi, new Point(24, 230), Theme.Text);
        TextRenderer.DrawText(g, "Нужно, чтобы игровой режим и оверлей включались сами при запуске игр", Theme.Tiny, new Rectangle(24, 252, Width - 110, 18), Theme.Muted, TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, "Это можно поменять в любой момент в «Настройках».", Theme.Tiny, new Rectangle(24, Height - 54, 320, 20), Theme.Dim, TextFormatFlags.Left);
    }
}

/// <summary>Подсказка тура: стеклянная карточка со стрелкой к нужному месту окна.</summary>
class CoachMark : RoundForm
{
    readonly string title, text, step;
    readonly bool arrowUp;
    readonly int arrowX;
    public bool Skipped { get; private set; }
    bool closing;

    public CoachMark(string title, string text, string step, Rectangle target, bool last)
    {
        this.title = title;
        this.text = text;
        this.step = step;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Opacity = 0;
        Size = new Size(340, 168);
        var area = Screen.FromRectangle(target).WorkingArea;
        arrowUp = target.Bottom + Height + 12 < area.Bottom;
        int x = Math.Clamp(target.Left + target.Width / 2 - Width / 2, area.Left + 8, area.Right - Width - 8);
        int y = arrowUp ? target.Bottom + 12 : target.Top - Height - 12;
        Location = new Point(x, Math.Clamp(y, area.Top + 8, area.Bottom - Height - 8));
        arrowX = Math.Clamp(target.Left + target.Width / 2 - x, 24, Width - 24);
        var next = new FlatBtn(last ? "" : "", last ? "Готово" : "Далее") { Primary = true };
        next.Location = new Point(Width - 16 - next.Width + next.Pad, Height - 14 - next.Height + next.Pad);
        next.Click += (_, _) => Finish();
        Controls.Add(next);
        if (!last)
        {
            var skip = new FlatBtn("", "Пропустить");
            skip.Location = new Point(next.Left + next.Pad - skip.Width - 6, next.Top + next.Pad);
            skip.Click += (_, _) => { Skipped = true; Finish(); };
            Controls.Add(skip);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Motion.Run(240, p => Opacity = p);
    }

    void Finish()
    {
        if (closing) return;
        closing = true;
        Motion.Run(140, p => Opacity = 1 - p, Motion.InOut, 0, () => { DialogResult = DialogResult.OK; Close(); });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // стрелка — маленький треугольник к цели
        var pts = arrowUp
            ? new[] { new PointF(arrowX - 8, 9), new PointF(arrowX, 1), new PointF(arrowX + 8, 9) }
            : new[] { new PointF(arrowX - 8, Height - 9), new PointF(arrowX, Height - 1), new PointF(arrowX + 8, Height - 9) };
        using (var b = new SolidBrush(Theme.Accent)) g.FillPolygon(b, pts);
        using (var bar = new SolidBrush(Theme.Accent)) g.FillRectangle(bar, 18, arrowUp ? 10 : 6, 30, 3);
        TextRenderer.DrawText(g, step, Theme.Small, new Rectangle(Width - 80, 14, 62, 16), Theme.Muted, TextFormatFlags.Right);
        TextRenderer.DrawText(g, title, Theme.F(11.5f, FontStyle.Bold), new Rectangle(18, 22, Width - 36, 24), Theme.Text, TextFormatFlags.Left);
        TextRenderer.DrawText(g, text, Theme.Body, new Rectangle(18, 50, Width - 36, 60), Theme.Muted, TextFormatFlags.WordBreak);
    }
}

/// <summary>Первый запуск и тур (вызывается из главного окна после загрузки).</summary>
public partial class MainForm
{
    readonly PendingBar pendingBar = new();

    async Task FirstRunAsync()
    {
        if (AppSettings.Get("firstRun", "0") == "1") return;
        AppSettings.Set("firstRun", "1");
        bool tray, auto;
        using (var w = new WelcomeDialog())
        {
            w.ShowDialog(this);
            tray = w.Tray.Checked;
            auto = w.Autostart.Checked;
        }
        AppSettings.Set("tray", tray ? "1" : "0");
        if (auto)
        {
            try { await Task.Run(Autostart.Install); autostartOn = true; } catch { }
        }
        await Task.Delay(250);
        Tour();
    }

    /// <summary>Короткий тур: «Применить», поиск, пульт в трее.</summary>
    void Tour()
    {
        var area = Screen.FromControl(this).WorkingArea;
        var steps = new (string Title, string Text, Func<Rectangle> Target)[]
        {
            ("Отметили — применили", "Включайте нужное переключателями. Изменения сработают после кнопки «Применить», а откатить их можно в «Истории».",
                () => applyBtn.RectangleToScreen(applyBtn.ClientRectangle)),
            ("Поиск по всему", "Ctrl+K — найдёт любую настройку, раздел или действие. Можно печатать с ошибкой в раскладке.",
                () => trigger.RectangleToScreen(trigger.ClientRectangle)),
            ("Пульт в трее", "WinTweak живёт в трее: клик по значку или Ctrl+Alt+W — круговой пульт с играми, пингом и игровым режимом.",
                () => new Rectangle(area.Right - 120, area.Bottom - 4, 80, 4)),
        };
        for (int i = 0; i < steps.Length; i++)
        {
            using var c = new CoachMark(steps[i].Title, steps[i].Text, $"{i + 1} из {steps.Length}", steps[i].Target(), i == steps.Length - 1);
            c.ShowDialog(this);
            if (c.Skipped) break;
        }
    }
}
