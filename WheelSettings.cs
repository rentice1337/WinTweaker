using System.Drawing.Drawing2D;

namespace WinTweaker;

/// <summary>Настройки кругового пульта (settings.ini).</summary>
static class WheelSettings
{
    public static readonly (string Id, string Title)[] Actions =
    {
        ("mode", "Игровой режим"),
        ("games", "Игры (кольцо с обложками)"),
        ("open", "Открыть WinTweak"),
        ("ready", "Готовность к игре"),
        ("exit", "Выход"),
        ("gpu", "Сброс видеодрайвера"),
        ("dns", "Очистить кэш DNS"),
        ("ping", "Пинг"),
        ("overlay", "Оверлей: показать / скрыть"),
        ("apply", "Применить изменения"),
        ("hub", "Игровой центр"),
        ("diag", "Диагностика"),
        ("settings", "Настройки WinTweak"),
        ("taskmgr", "Диспетчер задач"),
        ("lock", "Заблокировать ПК"),
        ("explorer", "Перезапустить Проводник"),
        ("report", "Последний отчёт о соединении"),
        ("refresh", "Частота монитора"),
        ("none", "— пусто —"),
    };

    public static readonly string[] Default = { "mode", "games", "open", "ready", "exit", "gpu", "dns", "ping" };
    public static readonly string[] Places = { "сверху", "справа сверху", "справа", "справа снизу", "снизу", "слева снизу", "слева", "слева сверху" };

    public static string[] Sectors
    {
        get
        {
            var a = AppSettings.Get("wheel.sectors", "").Split(',');
            return a.Length == 8 && a.All(x => Actions.Any(y => y.Id == x)) ? a : Default.ToArray();
        }
        set => AppSettings.Set("wheel.sectors", string.Join(",", value));
    }

    static int Int(string key, int def, int min, int max) =>
        int.TryParse(AppSettings.Get(key, def.ToString()), out int v) ? Math.Clamp(v, min, max) : def;

    /// <summary>0 — маленький, 1 — средний, 2 — большой.</summary>
    public static int Size { get => Int("wheel.size", 1, 0, 2); set => AppSettings.Set("wheel.size", value.ToString()); }
    public static float Scale => 1f; // размер один — средний

    public static bool Centered { get => AppSettings.Get("wheel.center", "0") == "1"; set => AppSettings.Set("wheel.center", value ? "1" : "0"); }
    public static bool Hint { get => AppSettings.Get("wheel.hint", "1") == "1"; set => AppSettings.Set("wheel.hint", value ? "1" : "0"); }

    /// <summary>Сколько игр во втором кольце: 6, 8 или 10.</summary>
    public static int Games { get => Int("wheel.games", 10, 6, 10); set => AppSettings.Set("wheel.games", value.ToString()); }

    public static Keys Hotkey
    {
        get
        {
            var v = AppSettings.Get("wheel.hk", "");
            if (v == "") return AppSettings.Get("hotkey", "1") == "0" ? Keys.None : Keys.Control | Keys.Alt | Keys.W;
            return int.TryParse(v, out int k) ? (Keys)k : Keys.None;
        }
        set => AppSettings.Set("wheel.hk", ((int)value).ToString());
    }

    public static string Format(Keys k)
    {
        if (k == Keys.None) return "Выкл";
        var parts = new List<string>();
        if ((k & Keys.Control) != 0) parts.Add("Ctrl");
        if ((k & Keys.Alt) != 0) parts.Add("Alt");
        if ((k & Keys.Shift) != 0) parts.Add("Shift");
        var key = k & Keys.KeyCode;
        string name = key switch
        {
            >= Keys.D0 and <= Keys.D9 => ((int)key - (int)Keys.D0).ToString(),
            >= Keys.NumPad0 and <= Keys.NumPad9 => "Num " + ((int)key - (int)Keys.NumPad0),
            Keys.Oemtilde => "`",
            Keys.Space => "Пробел",
            _ => key.ToString()
        };
        parts.Add(name);
        return string.Join("+", parts);
    }
}

/// <summary>Поле «нажмите сочетание»: клик — ожидание, Esc — отмена, Backspace/Delete — выключить.</summary>
class HotkeyBox : Control
{
    Keys value;
    bool capturing;
    Keys partial;
    readonly AnimFloat hover;
    public event Action<Keys>? Changed;

    public HotkeyBox(Keys value)
    {
        this.value = value;
        SetStyle(Theme.Smooth | ControlStyles.Selectable, true);
        Size = new Size(170, 32);
        Cursor = Cursors.Hand;
        TabStop = true;
        hover = new AnimFloat(0, () => Invalidate());
    }

    public Keys Value { get => value; set { this.value = value; Invalidate(); } }

    protected override void OnMouseEnter(EventArgs e) { hover.To(1, 150); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover.To(0, 240); base.OnMouseLeave(e); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        capturing = true;
        partial = Keys.None;
        Focus();
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        capturing = false;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => capturing || base.IsInputKey(keyData);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!capturing) return base.ProcessCmdKey(ref msg, keyData);
        Capture(keyData);
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (capturing) { Capture(e.KeyData); e.Handled = e.SuppressKeyPress = true; }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (capturing) { partial = e.Modifiers; Invalidate(); }
        base.OnKeyUp(e);
    }

    void Capture(Keys k)
    {
        var key = k & Keys.KeyCode;
        if (key == Keys.Escape) { capturing = false; Invalidate(); return; }
        if (key is Keys.Back or Keys.Delete) { Finish(Keys.None); return; }
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.None)
        {
            partial = k & Keys.Modifiers;
            Invalidate();
            return;
        }
        if ((k & (Keys.Control | Keys.Alt)) == 0) { partial = Keys.None; Invalidate(); return; } // нужен Ctrl или Alt
        Finish(k);
    }

    void Finish(Keys k)
    {
        capturing = false;
        value = k;
        Invalidate();
        Changed?.Invoke(k);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Glass.Back(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
        Theme.Fill(g, capturing ? Color.FromArgb(40, Theme.Accent) : Color.FromArgb((int)(20 + 14 * hover.Value), 255, 255, 255), r, 9);
        using (var pen = new Pen(capturing ? Theme.Accent : Color.FromArgb(40, 255, 255, 255)))
        using (var path = Theme.Round(r, 9))
            g.DrawPath(pen, path);
        string text = capturing
            ? (partial & Keys.Modifiers) != 0 ? WheelSettings.Format(partial | Keys.None).Replace("+None", "+…") : "Нажмите сочетание…"
            : WheelSettings.Format(value);
        TextRenderer.DrawText(g, text, Theme.Semi, Rectangle.Round(r), capturing ? Theme.AccentHi : value == Keys.None ? Theme.Muted : Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Тёмный выпадающий список.</summary>
class DarkCombo : ComboBox
{
    public DarkCombo()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        BackColor = Theme.Hover;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        ItemHeight = 24;
        MaxDropDownItems = 14;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using (var b = new SolidBrush(sel ? Theme.AccentDim : Theme.Hover)) e.Graphics.FillRectangle(b, e.Bounds);
        TextRenderer.DrawText(e.Graphics, Items[e.Index]?.ToString() ?? "", Font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height),
            Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Окно «Секторы пульта»: 8 мест по кругу, у каждого — действие из списка; справа — живое превью.</summary>
class SectorsDialog : RoundForm
{
    readonly DarkCombo[] combos = new DarkCombo[8];
    bool closing;
    int baseTop;
    const int W = 700, RowH = 40, Top0 = 84;

    public SectorsDialog()
    {
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Opacity = 0;
        Size = new Size(W, Top0 + 8 * RowH + 84);
        var cur = WheelSettings.Sectors;
        for (int i = 0; i < 8; i++)
        {
            var c = new DarkCombo { Width = 250 };
            foreach (var (_, title) in WheelSettings.Actions) c.Items.Add(title);
            c.SelectedIndex = Math.Max(0, Array.FindIndex(WheelSettings.Actions, a => a.Id == cur[i]));
            c.Location = new Point(170, Top0 + i * RowH + 4);
            c.SelectedIndexChanged += (_, _) => Invalidate();
            combos[i] = c;
            Controls.Add(c);
        }
        var save = new FlatBtn("", "Сохранить") { Primary = true };
        save.Location = new Point(Width - 24 - save.Width + save.Pad, Height - 22 - save.Height + save.Pad);
        save.Click += (_, _) => { WheelSettings.Sectors = Ids(); Finish(DialogResult.OK); };
        var cancel = new FlatBtn("", "Отмена");
        cancel.Location = new Point(save.Left + save.Pad - cancel.Width - 8, save.Top + save.Pad);
        cancel.Click += (_, _) => Finish(DialogResult.Cancel);
        var reset = new FlatBtn("", "По умолчанию");
        reset.Location = new Point(24, save.Top + save.Pad);
        reset.Click += (_, _) =>
        {
            for (int i = 0; i < 8; i++) combos[i].SelectedIndex = Array.FindIndex(WheelSettings.Actions, a => a.Id == WheelSettings.Default[i]);
        };
        Controls.AddRange(new Control[] { save, cancel, reset });
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Finish(DialogResult.Cancel); };
    }

    string[] Ids() => combos.Select(c => WheelSettings.Actions[Math.Max(0, c.SelectedIndex)].Id).ToArray();

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Owner != null) Location = new Point(Owner.Left + (Owner.Width - Width) / 2, Owner.Top + (Owner.Height - Height) / 2);
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
        g.SmoothingMode = SmoothingMode.AntiAlias;
        TextRenderer.DrawText(g, "Секторы пульта", Theme.F(13f, FontStyle.Bold), new Point(24, 20), Theme.Text);
        TextRenderer.DrawText(g, "8 мест по кругу, по часовой стрелке от верхнего. «Игры» раскрывают второе кольцо с обложками.",
            Theme.Tiny, new Rectangle(24, 48, 420, 30), Theme.Muted, TextFormatFlags.WordBreak);
        for (int i = 0; i < 8; i++)
            TextRenderer.DrawText(g, $"{i + 1}  ·  {WheelSettings.Places[i]}", Theme.Body, new Rectangle(24, Top0 + i * RowH, 140, RowH),
                Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        // превью кольца
        var ids = Ids();
        float cx = 560, cy = Top0 + 4 * RowH, ro = 112, ri = 44;
        for (int i = 0; i < 8; i++)
        {
            float a0 = -90 - 22.5f + i * 45 + 1.5f, sw = 42;
            using var path = new GraphicsPath();
            path.AddArc(cx - ro, cy - ro, ro * 2, ro * 2, a0, sw);
            path.AddArc(cx - ri, cy - ri, ri * 2, ri * 2, a0 + sw, -sw);
            path.CloseFigure();
            bool empty = ids[i] == "none";
            using (var b = new SolidBrush(empty ? Color.FromArgb(14, 255, 255, 255) : Theme.Lerp(Theme.Card, Theme.Accent, ids[i] == "games" ? 0.35 : 0.12)))
                g.FillPath(b, path);
            using (var pen = new Pen(Color.FromArgb(50, 255, 255, 255))) g.DrawPath(pen, path);
            double mid = (a0 + sw / 2) * Math.PI / 180;
            float rm = (ro + ri) / 2;
            var p = new PointF(cx + (float)Math.Cos(mid) * rm, cy + (float)Math.Sin(mid) * rm);
            string label = empty ? "" : WheelSettings.Actions.First(a => a.Id == ids[i]).Title.Split(' ', ':', '(')[0];
            TextRenderer.DrawText(g, label, Theme.Tiny, new Rectangle((int)p.X - 40, (int)p.Y - 10, 80, 20), Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        using (var b = new SolidBrush(Theme.Bg)) g.FillEllipse(b, cx - ri + 6, cy - ri + 6, (ri - 6) * 2, (ri - 6) * 2);
    }
}
