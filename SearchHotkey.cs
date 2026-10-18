using System.Drawing.Drawing2D;

namespace WinTweaker;

/// <summary>Горячая клавиша строки поиска (settings.ini; ключ прежний — сочетание пользователя сохраняется).</summary>
static class SearchHotkey
{
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
            ? (partial & Keys.Modifiers) != 0 ? SearchHotkey.Format(partial | Keys.None).Replace("+None", "+…") : "Нажмите сочетание…"
            : SearchHotkey.Format(value);
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
