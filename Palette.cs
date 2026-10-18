using System.Drawing.Drawing2D;

namespace WinTweaker;

/// <summary>Элемент палитры: раздел, настройка, действие или команда.</summary>
sealed class PaletteItem
{
    public string Glyph = "";
    public string Title = "";
    public string Sub = "";
    public string Search = "";
    public int Order;                     // при пустом запросе — порядок показа
    public Func<(string Text, Color Color)?>? Badge;
    public Action Run = () => { };
    public bool KeepOpen;                 // строка остаётся открытой после выполнения
    public InstalledGame? Image;          // игра — вместо значка её круглая обложка
}

/// <summary>
/// Строка поиска (Ctrl+K в окне, Ctrl+Alt+W и клик по значку в трее): игры, настройки, действия и разделы.
/// Нечёткий поиск, стрелки — выбор, Enter — выполнить. Из трея открывается по центру экрана поверх всех окон.
/// </summary>
class Palette : RoundForm
{
    readonly TextBox box = new();
    readonly PaletteList list;
    readonly List<PaletteItem> all;
    readonly Form owner;
    readonly bool inWindow;
    bool closing;

    const int W = 640, BoxH = 58, FootH = 34;

    public Palette(Form owner, List<PaletteItem> items, bool inWindow)
    {
        this.owner = owner;
        this.inWindow = inWindow;
        TopMost = !inWindow;
        all = items;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Opacity = 0;

        box.BorderStyle = BorderStyle.None;
        box.BackColor = Theme.Card;
        box.ForeColor = Theme.Text;
        box.Font = Theme.F(13f);
        box.PlaceholderText = "Игра, настройка, действие или раздел…";
        Controls.Add(box);

        list = new PaletteList();
        list.Picked += Execute;
        Controls.Add(list);

        box.TextChanged += (_, _) => Filter();
        box.KeyDown += OnKeys;
        Deactivate += (_, _) => { if (!closing) CloseAnimated(); };
        Filter();
    }

    void OnKeys(object? s, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Down: list.Move(1); e.Handled = e.SuppressKeyPress = true; break;
            case Keys.Up: list.Move(-1); e.Handled = e.SuppressKeyPress = true; break;
            case Keys.PageDown: list.Move(PaletteList.Rows); e.Handled = e.SuppressKeyPress = true; break;
            case Keys.PageUp: list.Move(-PaletteList.Rows); e.Handled = e.SuppressKeyPress = true; break;
            case Keys.Enter:
                if (list.Current != null) Execute(list.Current);
                e.Handled = e.SuppressKeyPress = true;
                break;
            case Keys.Escape: CloseAnimated(); e.Handled = e.SuppressKeyPress = true; break;
        }
    }

    void Execute(PaletteItem item)
    {
        if (item.KeepOpen)
        {
            item.Run();
            list.Invalidate();
            return;
        }
        CloseAnimated();
        // выполняем после закрытия, чтобы диалоги и переходы не конфликтовали с палитрой
        owner.BeginInvoke(item.Run);
    }

    void Filter()
    {
        string q = box.Text.Trim();
        IEnumerable<PaletteItem> res;
        if (q == "") res = all.OrderBy(i => i.Order).Take(40);
        else
        {
            var words = q.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            res = all.Select(i => (i, s: Score(i, q.ToLowerInvariant(), words)))
                     .Where(x => x.s > 0)
                     .OrderByDescending(x => x.s).ThenBy(x => x.i.Order)
                     .Select(x => x.i).Take(60);
        }
        list.SetItems(res.ToList());
        Resize_();
    }

    /// <summary>Начало названия &gt; подстрока в названии &gt; все слова &gt; описание.</summary>
    static int Score(PaletteItem i, string q, string[] words)
    {
        string t = i.Title.ToLowerInvariant();
        if (t.StartsWith(q)) return 400 - t.Length;
        if (t.Contains(q)) return 300 - t.IndexOf(q, StringComparison.Ordinal);
        string hay = (t + " " + i.Sub + " " + i.Search).ToLowerInvariant();
        if (words.All(w => hay.Contains(w))) return 200 - words.Length;
        if (words.Any(w => w.Length > 2 && hay.Contains(w))) return 50;
        return 0;
    }

    void Resize_()
    {
        int rows = Math.Max(1, Math.Min(PaletteList.Rows, list.Count));
        Size = new Size(W, BoxH + rows * PaletteList.RowH + 12 + FootH);
        box.SetBounds(56, (BoxH - box.PreferredHeight) / 2 + 2, W - 80, box.PreferredHeight);
        list.SetBounds(8, BoxH + 4, W - 16, rows * PaletteList.RowH);
        Invalidate();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        int x, y;
        if (inWindow) { x = owner.Left + (owner.Width - Width) / 2; y = owner.Top + 86; }
        else
        {
            // из трея или по горячей клавише — по центру экрана, где курсор
            var sc = Screen.FromPoint(Cursor.Position).WorkingArea;
            x = sc.Left + (sc.Width - Width) / 2;
            y = sc.Top + sc.Height / 5;
        }
        Location = new Point(x, y + 10);
        box.Focus();
        Motion.Run(220, p => { if (!IsDisposed) { Opacity = p; Top = y + (int)Math.Round(10 * (1 - p)); } });
    }

    public void CloseAnimated()
    {
        if (closing) return;
        closing = true;
        Motion.Run(130, p => { if (!IsDisposed) Opacity = 1 - p; }, Motion.InOut, 0, () => { if (!IsDisposed) Close(); });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        TextRenderer.DrawText(g, "", Theme.IconBig, new Rectangle(16, 0, 32, BoxH), Theme.Accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 0, BoxH, Width, BoxH);
        if (list.Count == 0)
            TextRenderer.DrawText(g, "Ничего не найдено", Theme.Body, new Rectangle(0, BoxH + 4, Width, PaletteList.RowH), Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        int fy = Height - FootH;
        using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 0, fy, Width, fy);
        int x = 16;
        foreach (var (key, text) in new[] { ("↑↓", "выбор"), ("Enter", "выполнить"), ("Esc", "закрыть") })
        {
            var ks = TextRenderer.MeasureText(key, Theme.Small);
            var cap = new RectangleF(x, fy + 8, ks.Width + 10, 18);
            Theme.Fill(g, Theme.Hover, cap, 4);
            TextRenderer.DrawText(g, key, Theme.Small, Rectangle.Round(cap), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            x += (int)cap.Width + 6;
            var ts = TextRenderer.MeasureText(text, Theme.Tiny);
            TextRenderer.DrawText(g, text, Theme.Tiny, new Point(x, fy + 9), Theme.Dim);
            x += ts.Width + 14;
        }
        string count = $"{list.Count} рез.";
        var cs = TextRenderer.MeasureText(count, Theme.Tiny);
        TextRenderer.DrawText(g, count, Theme.Tiny, new Point(Width - cs.Width - 16, fy + 9), Theme.Dim);
    }
}

/// <summary>Список результатов: выделение плавно переезжает, колесо прокручивает.</summary>
class PaletteList : Control, IWheelTarget
{
    public const int RowH = 50, Rows = 9;
    List<PaletteItem> items = new();
    int sel, top;
    readonly AnimFloat selY;
    public event Action<PaletteItem>? Picked;

    public PaletteList()
    {
        SetStyle(Theme.Smooth, true);
        Cursor = Cursors.Hand;
        selY = new AnimFloat(0, () => Invalidate());
    }

    public int Count => items.Count;
    public PaletteItem? Current => sel >= 0 && sel < items.Count ? items[sel] : null;
    public bool CanScroll => items.Count > Rows;

    public void SetItems(List<PaletteItem> list)
    {
        items = list;
        sel = 0;
        top = 0;
        selY.Set(0);
        Invalidate();
    }

    public void Move(int d)
    {
        if (items.Count == 0) return;
        sel = Math.Clamp(sel + d, 0, items.Count - 1);
        if (sel < top) top = sel;
        if (sel >= top + Rows) top = sel - Rows + 1;
        selY.To((sel - top) * RowH, 160);
        Invalidate();
    }

    public void Wheel(int delta)
    {
        int max = Math.Max(0, items.Count - Rows);
        top = Math.Clamp(top - Math.Sign(delta) * 2, 0, max);
        sel = Math.Clamp(sel, top, Math.Min(items.Count - 1, top + Rows - 1));
        selY.Set((sel - top) * RowH);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = top + e.Y / RowH;
        if (i != sel && i < items.Count)
        {
            sel = i;
            selY.To((sel - top) * RowH, 120);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int i = top + e.Y / RowH;
        if (i < items.Count) Picked?.Invoke(items[i]);
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (items.Count == 0) return;

        var hl = new RectangleF(0, (float)selY.Value, Width - 1, RowH - 2);
        Theme.Fill(g, Theme.Hover, hl, 10);
        var bar = new RectangleF(1, hl.Y + 12, 3, RowH - 26);
        Theme.Glow(g, Theme.Accent, bar, 1.5f, 3, 1.5f, 110);
        Theme.Fill(g, Theme.Accent, bar, 1.5f);

        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        for (int i = 0; i < Rows && top + i < items.Count; i++)
        {
            var it = items[top + i];
            int y = i * RowH;
            bool isSel = top + i == sel;
            var circle = new RectangleF(14, y + 9, 30, 30);
            if (it.Image != null) GameArt.Blit(g, GameArt.Coin(it.Image, 30), 14, y + 9);
            else
            {
                Theme.Fill(g, Color.FromArgb(isSel ? 70 : 34, Theme.Accent), circle, 15);
                TextRenderer.DrawText(g, it.Glyph, Theme.Icon, Rectangle.Round(circle), isSel ? Theme.Text : Theme.Accent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            int right = 16;
            var badge = it.Badge?.Invoke();
            if (badge != null)
            {
                var (text, color) = badge.Value;
                var bs = TextRenderer.MeasureText(text, Theme.Small);
                var chip = new RectangleF(Width - bs.Width - 30, y + 15, bs.Width + 14, 20);
                Theme.Fill(g, Color.FromArgb(40, color), chip, 10);
                TextRenderer.DrawText(g, text, Theme.Small, Rectangle.Round(chip), color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                right = (int)chip.Width + 26;
            }
            TextRenderer.DrawText(g, it.Title, isSel ? Theme.Semi : Theme.Body, new Rectangle(56, y + 5, Width - 56 - right, 22),
                isSel ? Theme.Text : Color.FromArgb(205, 207, 218), flags);
            TextRenderer.DrawText(g, it.Sub, Theme.Tiny, new Rectangle(56, y + 25, Width - 56 - right, 18), Theme.Muted, flags);
        }
    }
}
