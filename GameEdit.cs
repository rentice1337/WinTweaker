using System.Drawing.Drawing2D;

namespace WinTweaker;

/// <summary>
/// Правка игры: своё название, своя обложка, лаунчер для запуска (если игра стоит в нескольких местах),
/// закрепить сверху, скрыть. Всё применяется сразу.
/// </summary>
sealed class GameEditDialog : RoundForm
{
    readonly InstalledGame game;
    readonly string key;
    readonly TextBox name = new();
    readonly FlatBtn pick = new("", "Выбрать картинку…");
    readonly FlatBtn stdCover = new("", "Стандартная");
    readonly FlatBtn resetName = new("", "");
    readonly FlatBtn done = new("", "Готово") { Primary = true };
    readonly Toggle pin = new(), hide = new();
    readonly Segmented? launcher;
    readonly string[] sources;
    readonly int launcherTop, togglesTop;
    bool closing;
    int baseTop;
    const int W = 560, Pad = 24, CoverTop = 84, NameTop = 222;

    public GameEditDialog(InstalledGame game)
    {
        this.game = game;
        key = game.Key;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Opacity = 0;

        sources = game.Variants.Select(v => v.Source).Distinct().ToArray();
        int y = NameTop + 64;
        if (sources.Length > 1)
        {
            launcher = new Segmented(sources) { Value = Math.Max(0, Array.IndexOf(sources, game.Variants[0].Source)) };
            launcher.Changed += v => GamePrefs.SetLauncher(key, sources[v]);
            launcherTop = y;
            launcher.Location = new Point(Pad, y + 22);
            Controls.Add(launcher);
            y += 70;
        }
        togglesTop = y;
        Size = new Size(W, togglesTop + 2 * 44 + 84);

        name.BorderStyle = BorderStyle.None;
        name.BackColor = Theme.Hover;
        name.ForeColor = Theme.Text;
        name.Font = Theme.F(11f);
        name.Text = game.Title;
        name.SetBounds(Pad + 12, NameTop + 26, W - Pad * 2 - 24 - 44, name.PreferredHeight);
        name.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Finish(); } };
        Controls.Add(name);

        resetName.Location = new Point(W - Pad - resetName.Width, NameTop + 20);
        resetName.Click += (_, _) => { name.Text = game.BaseTitle; name.Focus(); name.SelectAll(); };
        Controls.Add(resetName);

        pick.Location = new Point(Pad + GameLibrary.CoverW + 20, CoverTop + 14);
        pick.Click += (_, _) => PickCover();
        stdCover.Location = new Point(pick.Left, pick.Bottom + 8);
        stdCover.Click += (_, _) => { GamePrefs.ResetCover(key); Sync(); };
        Controls.Add(pick);
        Controls.Add(stdCover);

        pin.Checked = GamePrefs.IsPinned(key);
        hide.Checked = GamePrefs.IsHidden(key);
        pin.Location = new Point(W - Pad - pin.Width, togglesTop + 9);
        hide.Location = new Point(W - Pad - hide.Width, togglesTop + 44 + 9);
        pin.CheckedChanged += (_, _) => { GamePrefs.SetPinned(key, pin.Checked); Sync(); };
        hide.CheckedChanged += (_, _) => { GamePrefs.SetHidden(key, hide.Checked); Sync(); };
        Controls.Add(pin);
        Controls.Add(hide);

        done.Location = new Point(W - Pad - done.Width + done.Pad, Height - Pad - done.Height + done.Pad);
        done.Click += (_, _) => Finish();
        Controls.Add(done);

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Finish(); };
        Sync();
    }

    void Sync()
    {
        stdCover.Visible = GamePrefs.HasCover(key);
        if (pin.Checked != GamePrefs.IsPinned(key)) pin.Checked = GamePrefs.IsPinned(key);
        if (hide.Checked != GamePrefs.IsHidden(key)) hide.Checked = GamePrefs.IsHidden(key);
        Invalidate();
    }

    void PickCover()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Обложка для " + game.Title,
            Filter = "Картинки (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try { GamePrefs.SetCover(key, ofd.FileName); }
        catch (Exception ex) { Dialog.Info(this, "Не получилось", ex.Message, Tone.Error); }
        Sync();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Owner != null) Location = new Point(Owner.Left + (Owner.Width - Width) / 2, Owner.Top + (Owner.Height - Height) / 2);
        baseTop = Top;
        Motion.Run(220, p => { Opacity = p; Top = baseTop + (int)Math.Round(12 * (1 - p)); });
    }

    void Finish()
    {
        if (closing) return;
        closing = true;
        var t = name.Text.Trim();
        // своё имя хранится, только если оно отличается от стандартного
        string? custom = t == "" || t == game.BaseTitle ? null : t;
        if (custom != GamePrefs.NameOf(key)) GamePrefs.SetName(key, custom);
        Motion.Run(130, p => Opacity = 1 - p, Motion.InOut, 0, () => { DialogResult = DialogResult.OK; Close(); });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
        TextRenderer.DrawText(g, "Изменить игру", Theme.F(13f, FontStyle.Bold), new Point(Pad, 20), Theme.Text);
        TextRenderer.DrawText(g, game.BaseTitle + " · " + game.Sources, Theme.Tiny, new Rectangle(Pad, 50, W - Pad * 2, 18), Theme.Muted, flags);

        // обложка — как на плитке
        GameArt.Blit(g, GameArt.Cover(game, GameLibrary.CoverW, GameLibrary.CoverH, 12, topOnly: false), Pad, CoverTop);
        using (var path = Theme.Round(new RectangleF(Pad + 0.5f, CoverTop + 0.5f, GameLibrary.CoverW - 1, GameLibrary.CoverH - 1), 12))
        using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255)))
            g.DrawPath(pen, path);
        int tx = pick.Left;
        TextRenderer.DrawText(g, GamePrefs.HasCover(key) ? "Своя картинка" : game.ShownCover != null ? "Обложка из Steam" : "Значок игры — обложки не нашлось",
            Theme.Tiny, new Rectangle(tx, (stdCover.Visible ? stdCover.Bottom : pick.Bottom) + 10, W - tx - Pad, 34), Theme.Muted, TextFormatFlags.WordBreak);

        Label(g, "НАЗВАНИЕ", NameTop);
        var box = new RectangleF(Pad, NameTop + 18, W - Pad * 2 - 44, 34);
        Theme.Fill(g, Theme.Hover, box, 9);
        using (var pen = new Pen(name.Focused ? Theme.Accent : Color.FromArgb(40, 255, 255, 255)))
        using (var path = Theme.Round(box, 9))
            g.DrawPath(pen, path);

        if (launcher != null)
        {
            Label(g, "ЗАПУСКАТЬ ЧЕРЕЗ", launcherTop);
            TextRenderer.DrawText(g, "игра стоит в нескольких местах — плитка одна", Theme.Tiny,
                new Point(Pad + TextRenderer.MeasureText("ЗАПУСКАТЬ ЧЕРЕЗ", Theme.Small).Width + 10, launcherTop - 1), Theme.Dim);
        }

        Row(g, togglesTop, "Закрепить сверху", "Всегда первой в списке");
        Row(g, togglesTop + 44, "Скрыть", "Не показывать в списке и на пульте. Вернуть — «Изменить» → блок «Скрытые»");
    }

    static void Label(Graphics g, string text, int y) =>
        TextRenderer.DrawText(g, text, Theme.Small, new Point(Pad, y), Theme.Muted);

    void Row(Graphics g, int y, string title, string sub)
    {
        TextRenderer.DrawText(g, title, Theme.Semi, new Rectangle(Pad, y + 2, W - Pad * 2 - 60, 20), Theme.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, sub, Theme.Tiny, new Rectangle(Pad, y + 22, W - Pad * 2 - 60, 18), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>
/// «Игры на кольце»: какие игры показывать во втором кольце пульта и в каком порядке.
/// Автоматически — первые из «Моих игр»; свой список — галочками, порядок — перетаскиванием.
/// </summary>
sealed class WheelGamesDialog : RoundForm
{
    readonly List<InstalledGame> all;
    readonly HashSet<string> hiddenKeys;
    readonly List<string> picked;
    readonly Segmented mode = new(new[] { "Автоматически", "Свой список" });
    readonly FlatBtn save = new("", "Сохранить") { Primary = true };
    readonly FlatBtn cancel = new("", "Отмена");
    readonly int max;
    int scroll, hover = -1, press = -1, warnUntil;
    Point pressAt;
    bool dragging, closing;
    int baseTop;
    const int W = 560, Pad = 24, ListTop = 124, RowH = 46, ListH = 6 * RowH + 23;

    public WheelGamesDialog()
    {
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Opacity = 0;
        DoubleBuffered = true;
        Size = new Size(W, ListTop + ListH + 84);
        max = WheelSettings.Games;

        var shown = GameLibrary.Shown;
        var hidden = GameLibrary.Hidden;
        hiddenKeys = hidden.Select(g => g.Key).ToHashSet();
        var everything = shown.Concat(hidden).ToList();
        if (GamePrefs.Wheel is { } keys)
        {
            picked = keys.Where(k => everything.Any(g => g.Key == k)).Take(max).ToList();
            mode.Value = 1;
        }
        else picked = shown.Take(max).Select(g => g.Key).ToList();
        all = everything;
        Reorder();

        mode.Location = new Point(Pad, 76);
        mode.Changed += _ => Invalidate();
        Controls.Add(mode);

        save.Location = new Point(W - Pad - save.Width + save.Pad, Height - 22 - save.Height + save.Pad);
        save.Click += (_, _) =>
        {
            GamePrefs.SetWheel(Auto ? null : picked);
            Finish(DialogResult.OK);
        };
        cancel.Location = new Point(save.Left + save.Pad - cancel.Width - 8, save.Top + save.Pad);
        cancel.Click += (_, _) => Finish(DialogResult.Cancel);
        Controls.Add(save);
        Controls.Add(cancel);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Finish(DialogResult.Cancel); };
    }

    bool Auto => mode.Value == 0;

    /// <summary>Сначала выбранные по порядку, затем остальные, скрытые — в конце.</summary>
    void Reorder()
    {
        var rest = all.Where(g => !picked.Contains(g.Key)).OrderBy(g => hiddenKeys.Contains(g.Key) ? 1 : 0).ToList();
        var head = picked.Select(k => all.First(g => g.Key == k)).ToList();
        all.Clear();
        all.AddRange(head);
        all.AddRange(rest);
    }

    Rectangle ListRect => new(Pad, ListTop, W - Pad * 2, ListH);
    int MaxScroll => Math.Max(0, all.Count * RowH - ListH);

    int RowAt(Point p)
    {
        if (!ListRect.Contains(p)) return -1;
        int i = (p.Y - ListTop + scroll) / RowH;
        return i >= 0 && i < all.Count ? i : -1;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        scroll = Math.Clamp(scroll - e.Delta / 120 * RowH, 0, MaxScroll);
        Invalidate();
        base.OnMouseWheel(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        press = RowAt(e.Location);
        pressAt = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (press >= 0 && !Auto && (e.Button & MouseButtons.Left) != 0 && press < picked.Count)
        {
            if (!dragging && Math.Abs(e.Y - pressAt.Y) > 5) { dragging = true; Capture = true; Cursor = Cursors.SizeNS; }
            if (dragging)
            {
                int to = Math.Clamp((e.Y - ListTop + scroll) / RowH, 0, picked.Count - 1);
                if (to != press)
                {
                    var k = picked[press];
                    picked.RemoveAt(press);
                    picked.Insert(to, k);
                    press = to;
                    Reorder();
                }
                hover = press;
                Invalidate();
                return;
            }
        }
        int h = RowAt(e.Location);
        if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int p = press;
        press = -1;
        if (dragging) { dragging = false; Capture = false; Cursor = Cursors.Hand; Invalidate(); return; }
        if (p < 0 || RowAt(e.Location) != p) return;
        if (Auto) mode.Value = 1; // начали выбирать сами — свой список, начиная с того, что было автоматически
        var key = all[p].Key;
        if (picked.Remove(key)) { }
        else if (picked.Count >= max)
        {
            warnUntil = Environment.TickCount + 2500;
            Motion.Run(1, _ => { }, null, 2600, () => { if (!IsDisposed) Invalidate(); });
        }
        else picked.Add(key);
        Reorder();
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!dragging && hover != -1) { hover = -1; Invalidate(); }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Owner != null) Location = new Point(Owner.Left + (Owner.Width - Width) / 2, Owner.Top + (Owner.Height - Height) / 2);
        baseTop = Top;
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
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        TextRenderer.DrawText(g, "Игры на кольце", Theme.F(13f, FontStyle.Bold), new Point(Pad, 20), Theme.Text);
        TextRenderer.DrawText(g, Auto
                ? $"Первые {max} из «Моих игр» — в том же порядке. Нажмите на игру, чтобы выбрать свои."
                : "Нажмите на игру — добавить или убрать. Выбранные перетаскивайте, чтобы поменять порядок.",
            Theme.Tiny, new Rectangle(Pad, 48, W - Pad * 2, 18), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        bool warn = Environment.TickCount < warnUntil;
        string count = warn ? $"Уже {max} — больше не поместится. Число игр меняется в настройках пульта" : $"На кольце: {picked.Count} из {max}";
        TextRenderer.DrawText(g, count, Theme.Tiny, new Rectangle(mode.Right + 14, 76, W - mode.Right - 14 - Pad, 30), warn ? Theme.Warn : Theme.Muted,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        var list = ListRect;
        Theme.Fill(g, Color.FromArgb(10, 255, 255, 255), list, 12);
        var st = g.Save();
        g.SetClip(new Rectangle(list.X, list.Y + 1, list.Width, list.Height - 2));
        for (int i = 0; i < all.Count; i++)
        {
            int y = ListTop + i * RowH - scroll;
            if (y + RowH < ListTop || y > ListTop + ListH) continue;
            var game = all[i];
            int order = picked.IndexOf(game.Key);
            bool on = order >= 0;
            var row = new Rectangle(list.X + 6, y + 3, list.Width - 12, RowH - 6);
            if (i == hover || (dragging && i == press))
                Theme.Fill(g, Color.FromArgb(dragging && i == press ? 40 : 18, 255, 255, 255), row, 9);

            // номер на кольце
            var num = new Rectangle(row.X + 8, row.Y + (row.Height - 22) / 2, 22, 22);
            if (on)
            {
                Theme.Fill(g, Auto ? Color.FromArgb(70, Theme.Accent) : Theme.Accent, num, 11);
                TextRenderer.DrawText(g, (order + 1).ToString(), Theme.Small, num, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255))) g.DrawEllipse(pen, num.X + 0.5f, num.Y + 0.5f, num.Width - 1, num.Height - 1);

            GameArt.Blit(g, GameArt.Coin(game, 32), num.Right + 12, row.Y + (row.Height - 32) / 2, on || !Auto ? 1 : 0.6);
            int tx = num.Right + 56;
            bool hiddenGame = hiddenKeys.Contains(game.Key);
            TextRenderer.DrawText(g, game.Title, Theme.Semi, new Rectangle(tx, row.Y + 3, row.Right - tx - 40, 20), on ? Theme.Text : Theme.Muted, flags);
            TextRenderer.DrawText(g, game.Sources + (hiddenGame ? " · скрыта в списке" : ""), Theme.Tiny, new Rectangle(tx, row.Y + 21, row.Right - tx - 40, 16), Theme.Dim, flags);
            if (on && !Auto)
                TextRenderer.DrawText(g, "", Theme.Icon, new Rectangle(row.Right - 34, row.Y, 26, row.Height), Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); // «тащить»
        }
        g.Restore(st);
        if (all.Count == 0)
            TextRenderer.DrawText(g, "Игр пока нет — они появятся после поиска в «Игровом центре»", Theme.Body, list, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        // полоса прокрутки
        if (MaxScroll > 0)
        {
            float h = list.Height * (float)ListH / (all.Count * RowH);
            float y = list.Y + (list.Height - h) * scroll / MaxScroll;
            Theme.Fill(g, Color.FromArgb(60, 255, 255, 255), new RectangleF(list.Right - 5, y + 4, 3, h - 8), 1.5f);
        }
    }
}
