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
        Row(g, togglesTop + 44, "Скрыть", "Не показывать в списке и в поиске. Вернуть — «Изменить» → блок «Скрытые»");
    }

    static void Label(Graphics g, string text, int y) =>
        TextRenderer.DrawText(g, text, Theme.Small, new Point(Pad, y), Theme.Muted);

    void Row(Graphics g, int y, string title, string sub)
    {
        TextRenderer.DrawText(g, title, Theme.Semi, new Rectangle(Pad, y + 2, W - Pad * 2 - 60, 20), Theme.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, sub, Theme.Tiny, new Rectangle(Pad, y + 22, W - Pad * 2 - 60, 18), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}
