using System.Drawing.Drawing2D;

namespace WinTweaker;

/// <summary>Мелочи оформления для игрового центра.</summary>
static class GameUI
{
    public static Color LevelColor(Readiness.Level l) => l switch
    {
        Readiness.Level.Good => Theme.Good,
        Readiness.Level.Warn => Theme.Warn,
        Readiness.Level.Bad => Theme.Bad,
        _ => Color.FromArgb(120, 170, 255)
    };

    public static Color PingColor(double? ms) => ms is double v ? (v < 50 ? Theme.Good : v < 100 ? Theme.Warn : Theme.Bad) : Theme.Dim;

    /// <summary>Маленькая «таблетка» с текстом. Возвращает ширину.</summary>
    public static int Chip(Graphics g, string text, int x, int y, Color c, double a = 1)
    {
        int w = TextRenderer.MeasureText(text, Theme.Small).Width + 10;
        var r = new RectangleF(x, y, w, 17);
        Theme.Fill(g, Color.FromArgb((int)(48 * a), c), r, 8.5f);
        TextRenderer.DrawText(g, text, Theme.Small, Rectangle.Round(r), Theme.Lerp(Theme.Bg, c, a),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        return w;
    }

    public static void Dot(Graphics g, Color c, float x, float y, float d, double a = 1)
    {
        Theme.Fill(g, Color.FromArgb((int)(60 * a), c), new RectangleF(x - 3, y - 3, d + 6, d + 6), d);
        Theme.Fill(g, Theme.Lerp(Theme.Bg, c, a), new RectangleF(x, y, d, d), d / 2);
    }

    public static readonly Font H1 = Theme.F(12.5f, FontStyle.Bold), H2 = Theme.F(12f, FontStyle.Bold);

    public static string Bytes(long b) => b >= 1L << 30 ? $"{b / 1073741824.0:0.0} ГБ" : b >= 1L << 20 ? $"{b / 1048576.0:0} МБ" : b > 0 ? $"{b / 1024.0:0} КБ" : "пусто";

    public static string Duration(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} ч {t.Minutes} мин" : $"{Math.Max(1, (int)t.TotalMinutes)} мин";
}

// ═══════════════════════════════════════════════════════════════
//  Баннер игрового режима
// ═══════════════════════════════════════════════════════════════

class ModeBanner : Widget
{
    public readonly FlatBtn Switch = new("", "Включить сейчас") { Primary = true };
    public readonly Toggle Auto = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 50 };
    int tick;

    public ModeBanner() : base("", 100)
    {
        Wide = true;
        Auto.Checked = GameWatcher.Enabled;
        Controls.Add(Switch);
        Controls.Add(Auto);
        timer.Tick += (_, _) =>
        {
            // пульс значка — только его область; текст (время, пинг) — раз в секунду
            if (++tick % 20 == 0) Invalidate(false);
            else if (!Motion.Reduced) Invalidate(new Rectangle(0, 0, 100, Height), false);
        };
        Sync();
    }

    public void Sync()
    {
        bool on = GameMode.Active;
        Switch.Text = on ? "Выключить" : "Включить сейчас";
        Switch.Width = TextRenderer.MeasureText(Switch.Text, Theme.Semi).Width + 46 + (Switch.Primary ? 16 : 0);
        Edge = on ? Color.FromArgb(150, Theme.Accent) : null;
        if (on) timer.Start(); else timer.Stop();
        PerformLayout();
        Invalidate(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Auto is null || Switch is null) return;
        Switch.Location = new Point(Width - Switch.Width - 12, (Height - Switch.Height) / 2);
        Auto.Location = new Point(Switch.Left - Auto.Width - 200, (Height - Auto.Height) / 2);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        bool on = GameMode.Active;
        double t = on && !Motion.Reduced ? (Math.Sin(Motion.Now / 380.0) + 1) / 2 : 0.5;
        var icon = new RectangleF(r.Left + 22, r.Top + (r.Height - 48) / 2f, 48, 48);
        var col = on ? Theme.Accent : Theme.Muted;
        if (on) Theme.Fill(g, Color.FromArgb((int)((40 + 60 * t) * Appear), col), RectangleF.Inflate(icon, 5 + (float)(4 * t), 5 + (float)(4 * t)), 32);
        Theme.Fill(g, Color.FromArgb((int)((on ? 80 : 30) * Appear), col), icon, 24);
        TextRenderer.DrawText(g, "", Theme.IconBig, Rectangle.Round(icon), C(on ? Color.White : Theme.Muted),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        int x = r.Left + 92, w = Auto.Left - x - 16;
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
        string title = on ? $"Игровой режим · {GameMode.Reason}" : "Игровой режим выключен";
        TextRenderer.DrawText(g, title, GameUI.H1, new Rectangle(x, r.Top + 24, w, 26), C(Theme.Text), flags);
        string sub;
        if (on)
        {
            var parts = new List<string> { "идёт " + GameUI.Duration(DateTime.Now - GameMode.Since) };
            if (GameMode.Monitor is { } m) parts.Add(m.Last is int p ? $"пинг {p} мс" : "пинг —");
            parts.Add("уведомления и обновления на паузе");
            sub = string.Join(" · ", parts);
        }
        else sub = Auto.Checked ? "Включится сам, когда запустится игра, и выключится после неё" : "Автовключение выключено — переключатель справа";
        TextRenderer.DrawText(g, sub, Theme.Body, new Rectangle(x, r.Top + 52, w, 22), C(Theme.Muted), flags);

        TextRenderer.DrawText(g, "Сам при запуске игры", Theme.Semi, new Rectangle(Auto.Right + 8, r.Top, 190, r.Height), C(Theme.Text),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }
}

// ═══════════════════════════════════════════════════════════════
//  Готовность к игре
// ═══════════════════════════════════════════════════════════════

class ReadyWidget : Widget
{
    public readonly FlatBtn Run = new("", "Проверить") { Primary = true };
    public readonly FlatBtn FixAll = new("", "Исправить всё") { Visible = false };
    public event Action<Readiness.Check>? FixClicked;
    List<Readiness.Check>? checks;
    readonly List<(Rectangle Rect, Readiness.Check Check)> links = new();
    int hot = -1;
    public bool Running { get; private set; }
    const int RowH = 56, Top0 = 66;

    public ReadyWidget() : base("Готовность к игре", 28 + 92)
    {
        Wide = true;
        Controls.Add(Run);
        Controls.Add(FixAll);
    }

    public void Begin()
    {
        Running = true;
        Invalidate(false);
    }

    public void SetData(List<Readiness.Check> data)
    {
        Running = false;
        checks = data;
        FixAll.Visible = data.Any(c => c.Tweaks != null && c.Level is Readiness.Level.Warn or Readiness.Level.Bad);
        int rows = (data.Count + 1) / 2;
        Height = HeadH + Top0 + rows * RowH + 8;
        PerformLayout();
        Invalidate(true);
    }

    public List<Readiness.Check> Data => checks ?? new();

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Run is null || FixAll is null) return;
        Run.Location = new Point(Width - Run.Width - 8, HeadH + 8);
        FixAll.Location = new Point(Run.Left + Run.Pad - FixAll.Width - 8, Run.Top + Run.Pad);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = links.FindIndex(l => l.Rect.Contains(e.Location));
        if (h != hot) { hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(false); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { if (hot != -1) { hot = -1; Invalidate(false); } base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        var l = links.FirstOrDefault(x => x.Rect.Contains(e.Location));
        if (l.Check != null) FixClicked?.Invoke(l.Check);
        base.OnMouseClick(e);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        links.Clear();
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
        int x = r.Left + 20, w = FixAll.Visible ? FixAll.Left - x - 16 : Run.Left - x - 16;
        if (checks == null)
        {
            string s = Running ? "Проверяю питание, запись, фоновую нагрузку, память, пинг и драйвер…"
                               : "Перед матчем: схема питания, фоновая запись, нагрузка и память, загрузки в фоне, пинг, обновления и драйвер видеокарты.";
            TextRenderer.DrawText(g, Running ? "Проверка…" : "Проверить ПК перед игрой", GameUI.H2, new Rectangle(x, r.Top + 16, w, 24), C(Theme.Text), flags);
            TextRenderer.DrawText(g, s, Theme.Body, new Rectangle(x, r.Top + 44, w, 40), C(Theme.Muted), TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            return;
        }
        int bad = checks.Count(c => c.Level == Readiness.Level.Bad), warn = checks.Count(c => c.Level == Readiness.Level.Warn);
        var (title, col) = bad > 0 ? ($"Есть проблемы: {bad}" + (warn > 0 ? $", советов: {warn}" : ""), Theme.Bad)
                         : warn > 0 ? ($"Почти готов — советов: {warn}", Theme.Warn)
                         : ("Готов к игре", Theme.Good);
        GameUI.Dot(g, col, x, r.Top + 24, 12, Appear);
        TextRenderer.DrawText(g, title, GameUI.H1, new Rectangle(x + 24, r.Top + 16, w - 24, 26), C(Theme.Text), flags);
        TextRenderer.DrawText(g, Running ? "Проверяю заново…" : $"Проверено: {checks.Count} пунктов", Theme.Tiny, new Rectangle(x + 24, r.Top + 40, w - 24, 18), C(Theme.Muted), flags);

        int colW = (r.Width - 40 - 20) / 2;
        for (int i = 0; i < checks.Count; i++)
        {
            var c = checks[i];
            int cx = r.Left + 20 + (i % 2) * (colW + 20), cy = r.Top + Top0 + (i / 2) * RowH;
            var lc = GameUI.LevelColor(c.Level);
            GameUI.Dot(g, lc, cx + 2, cy + 6, 8, Appear);
            string? fix = c.Level is Readiness.Level.Warn or Readiness.Level.Bad
                ? c.Tweaks != null ? "Исправить" : c.Action == "close" ? "Фоновые программы" : null
                : null;
            int fixW = fix == null ? 0 : TextRenderer.MeasureText(fix, Theme.Semi).Width + 6;
            TextRenderer.DrawText(g, c.Title, Theme.Semi, new Rectangle(cx + 18, cy, colW - 18 - fixW - 8, 20), C(Theme.Text), flags);
            TextRenderer.DrawText(g, c.Detail, Theme.Tiny, new Rectangle(cx + 18, cy + 20, colW - 18, 32), C(Theme.Muted),
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            if (fix != null)
            {
                var lr = new Rectangle(cx + colW - fixW, cy, fixW, 20);
                int idx = links.Count;
                links.Add((lr, c));
                TextRenderer.DrawText(g, fix, Theme.Semi, lr, C(idx == hot ? Theme.AccentHi : Theme.Accent), TextFormatFlags.Right);
            }
        }
    }
}

// ═══════════════════════════════════════════════════════════════
//  Библиотека игр
// ═══════════════════════════════════════════════════════════════

class LibraryWidget : Widget
{
    public readonly FlatBtn Add = new("", "Добавить игру");
    public readonly FlatBtn Rescan = new("", "");
    public event Action<InstalledGame>? GameClicked;
    List<InstalledGame> games = new();
    int hover = -1;
    readonly AnimFloat lift;
    const int Gap = 14, CapH = 50;

    public LibraryWidget() : base("Мои игры", 200)
    {
        Wide = true;
        Controls.Add(Add);
        Controls.Add(Rescan);
        lift = new AnimFloat(0, () => Invalidate(false));
        Cursor = Cursors.Default;
    }

    public void SetData(List<InstalledGame> list)
    {
        games = list;
        Relayout();
        Invalidate(true);
    }

    int Cols => Math.Max(1, (Width - 24 + Gap) / (GameLibrary.CoverW + Gap));
    int TileW => (Width - 24 - Gap * (Cols - 1)) / Cols;
    int TileH => (int)(TileW * GameLibrary.CoverH / (float)GameLibrary.CoverW) + CapH;
    int GridTop => HeadH + 56;

    void Relayout()
    {
        int n = GameLibrary.Scanned ? games.Count : 4;
        int rows = Math.Max(1, (n + Cols - 1) / Cols);
        int h = n == 0 ? GridTop + 70 : GridTop + rows * (TileH + Gap) - Gap + 12;
        if (Height != h) Height = h;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (lift != null) Relayout();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Add is null || Rescan is null) return;
        Rescan.Location = new Point(Width - Rescan.Width - 12, HeadH + 12);
        Add.Location = new Point(Rescan.Left - Add.Width - 8, HeadH + 12);
    }

    Rectangle TileRect(int i)
    {
        int c = i % Cols, row = i / Cols;
        return new Rectangle(12 + c * (TileW + Gap), GridTop + row * (TileH + Gap), TileW, TileH);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = -1;
        if (GameLibrary.Scanned)
            for (int i = 0; i < games.Count; i++) if (TileRect(i).Contains(e.Location)) { h = i; break; }
        if (h != hover)
        {
            hover = h;
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
            lift.Set(0);
            if (h >= 0) lift.To(1, 260);
            Invalidate(false);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (hover != -1) { hover = -1; Invalidate(false); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (hover >= 0 && hover < games.Count && e.Button == MouseButtons.Left) GameClicked?.Invoke(games[hover]);
        base.OnMouseClick(e);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
        string sub = !GameLibrary.Scanned ? "Ищу игры в Steam, Epic Games, EA app, Battle.net, Riot и BSG…"
            : games.Count == 0 ? "Игр не нашлось — добавьте exe вручную"
            : $"Найдено: {games.Count} · с готовым пресетом: {games.Count(x => x.Info != null)} · нажмите на игру, чтобы настроить";
        TextRenderer.DrawText(g, sub, Theme.Body, new Rectangle(r.Left + 18, r.Top + 18, Add.Left - r.Left - 30, 22), C(Theme.Muted), flags);

        if (!GameLibrary.Scanned)
        {
            double sh = (Motion.Now / 1200.0) % 1;
            for (int i = 0; i < 4; i++)
            {
                var t = TileRect(i);
                Theme.Fill(g, Color.FromArgb((int)((14 + 10 * Math.Abs(Math.Sin((sh + i * 0.15) * Math.PI))) * Appear), 255, 255, 255), t, 12);
            }
            return;
        }
        for (int i = 0; i < games.Count; i++) Tile(g, games[i], TileRect(i), i == hover ? lift.Value : 0);
    }

    void Tile(Graphics g, InstalledGame game, Rectangle t, double h)
    {
        var rect = new RectangleF(t.X, t.Y - (float)(3 * h), t.Width, t.Height);
        if (h > 0.01) Theme.Glow(g, Theme.Accent, rect, 12, 3, 2.5f, (int)(70 * h * Appear));
        using var path = Theme.Round(rect, 12);
        using (var b = new SolidBrush(Color.FromArgb((int)(220 * Appear), 20, 21, 29))) g.FillPath(b, path);

        var cover = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height - CapH);
        var st = g.Save();
        g.SetClip(path);
        if (game.Cover != null)
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(game.Cover, cover);
        }
        else
        {
            // своя «обложка»: градиент акцента и значок игры
            using (var lg = new LinearGradientBrush(cover, Theme.Lerp(Theme.Card, Theme.Accent, 0.35), Theme.Lerp(Theme.Bg, Theme.Accent, 0.08), 35f))
                g.FillRectangle(lg, cover);
            if (game.Icon != null) g.DrawImage(game.Icon, cover.X + cover.Width / 2 - 20, cover.Y + cover.Height / 2 - 20, 40, 40);
            else TextRenderer.DrawText(g, "", Theme.IconBig, Rectangle.Round(cover), Color.FromArgb(200, 255, 255, 255),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        if (Appear < 1) using (var veil = new SolidBrush(Color.FromArgb((int)(255 * (1 - Appear)), Theme.Bg))) g.FillRectangle(veil, cover);
        g.Restore(st);

        using (var pen = new Pen(Color.FromArgb((int)((26 + 120 * h) * Appear), Theme.Lerp(Color.White, Theme.Accent, h)))) g.DrawPath(pen, path);

        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        int cy = (int)cover.Bottom + 8;
        TextRenderer.DrawText(g, game.Title, Theme.Semi, new Rectangle((int)rect.X + 12, cy, (int)rect.Width - 24, 20), C(Theme.Text), flags);
        int x = (int)rect.X + 12;
        TextRenderer.DrawText(g, game.Source, Theme.Tiny, new Rectangle(x, cy + 21, 90, 18), C(Theme.Muted), flags);
        int sw = Math.Min(90, TextRenderer.MeasureText(game.Source, Theme.Tiny).Width) + 8;
        x += sw;
        if (game.Tuned) x += GameUI.Chip(g, "НАСТРОЕНО", x, cy + 21, Theme.Good, Appear) + 4;
        else if (game.Info != null) x += GameUI.Chip(g, "ПРЕСЕТ", x, cy + 21, Theme.Accent, Appear) + 4;
        if (GameWatcher.Current?.Exe.Equals(game.Exe, StringComparison.OrdinalIgnoreCase) == true)
            GameUI.Chip(g, "ИДЁТ", x, cy + 21, Theme.Warn, Appear);
    }
}

// ═══════════════════════════════════════════════════════════════
//  Пинг до серверов
// ═══════════════════════════════════════════════════════════════

class PingWidget : Widget
{
    public readonly Segmented Mode = new(new[] { "Valve", "AWS", "Свой адрес" });
    public readonly FlatBtn Run = new("", "Проверить") { Primary = true };
    public readonly FlatBtn Trace = new("", "Трассировка") { Visible = false };
    List<GameNet.Result>? results;
    int done, total;
    string? error;
    public bool Running { get; private set; }
    const int RowH = 28, Rows = 10;

    public PingWidget() : base("Пинг до серверов", 28 + 64 + Rows * RowH + 14)
    {
        Controls.Add(Mode);
        Controls.Add(Run);
        Controls.Add(Trace);
    }

    public GameNet.Result? Best => results?.FirstOrDefault(r => r.Ms != null);

    public void Begin()
    {
        Running = true;
        results = null;
        error = null;
        done = total = 0;
        Trace.Visible = false;
        Invalidate(true);
    }

    public void Progress(int d, int t) { done = d; total = t; Invalidate(false); }

    public void SetData(List<GameNet.Result>? data, string? err = null)
    {
        Running = false;
        results = data;
        error = err;
        Trace.Visible = Best != null;
        PerformLayout();
        Invalidate(true);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Mode is null || Run is null || Trace is null) return;
        Mode.Location = new Point(14, HeadH + 14);
        Run.Location = new Point(Width - Run.Width - 8, HeadH + 6);
        Trace.Location = new Point(Width - Trace.Width - 14, Height - Trace.Height - 12);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        int top = r.Top + 60;
        if (results == null)
        {
            string text = Running
                ? $"Измеряю {done} из {Math.Max(total, 1)}…"
                : error ?? Mode.Value switch
                {
                    0 => "Ретрансляторы Steam Datagram Relay: через них идёт трафик CS2, Dota 2 и Deadlock. Регион с меньшим пингом — лучший выбор в поиске матча.",
                    1 => "Регионы облака AWS — на нём работают серверы Fortnite и многих других онлайн-игр. Замер — по времени TCP-соединения.",
                    _ => "IP или адрес сервера (виден в сетевой статистике игры или на сайте сервера). 20 пакетов: задержка, разброс и потери."
                };
            TextRenderer.DrawText(g, text, Theme.Body, new Rectangle(r.Left + 18, top, r.Width - 36, 70), C(error != null ? Theme.Warn : Theme.Muted), TextFormatFlags.WordBreak);
            if (Running && total > 0)
            {
                var bar = new RectangleF(r.Left + 18, top + 74, r.Width - 36, 4);
                Theme.Fill(g, Color.FromArgb(22, 255, 255, 255), bar, 2);
                Theme.Fill(g, Theme.Accent, new RectangleF(bar.X, bar.Y, bar.Width * done / total, 4), 2);
            }
            return;
        }
        var shown = results.Take(Rows).ToList();
        double max = Math.Max(30, shown.Where(x => x.Ms != null).Select(x => x.Ms!.Value).DefaultIfEmpty(100).Max() * 1.15);
        var best = Best;
        for (int i = 0; i < shown.Count; i++)
        {
            var res = shown[i];
            int y = top + i * RowH;
            bool isBest = res == best;
            TextRenderer.DrawText(g, res.Region.Name, isBest ? Theme.Semi : Theme.Body, new Rectangle(r.Left + 18, y, 130, RowH - 4), C(isBest ? Theme.Accent : Theme.Text), flags);
            float bx = r.Left + 152, bw = r.Width - 152 - 120;
            var track = new RectangleF(bx, y + 10, bw, 5);
            Theme.Fill(g, Color.FromArgb((int)(18 * Appear), 255, 255, 255), track, 2.5f);
            string label;
            if (res.Ms is not double ms) label = "нет ответа";
            else
            {
                var fill = new RectangleF(bx, y + 10, (float)(bw * Math.Min(1, ms / max) * Appear), 5);
                var c = GameUI.PingColor(ms);
                if (isBest) Theme.Glow(g, c, fill, 2.5f, 2, 1.5f, (int)(80 * Appear));
                Theme.Fill(g, C(c), fill, 2.5f);
                label = $"{ms:0} мс" + (res.Jitter >= 1 ? $" ±{res.Jitter:0}" : "") + (res.Lost > 0 ? $" · −{res.Lost}" : "");
            }
            TextRenderer.DrawText(g, label, Theme.Tiny, new Rectangle((int)(bx + bw + 10), y, 110, RowH - 4),
                C(res.Ms == null ? Theme.Dim : isBest ? Theme.Accent : Theme.Muted), flags);
        }
        if (results.Count > Rows)
            TextRenderer.DrawText(g, $"и ещё {results.Count - Rows} — дальше и медленнее", Theme.Tiny,
                new Rectangle(r.Left + 18, top + Rows * RowH, r.Width - 160, 22), C(Theme.Dim), flags);
    }
}

// ═══════════════════════════════════════════════════════════════
//  Трассировка
// ═══════════════════════════════════════════════════════════════

class TraceWidget : Widget
{
    public readonly FlatBtn Run = new("", "Трассировка…") { Primary = true };
    readonly List<GameNet.Hop> hops = new();
    string? target;
    public bool Running { get; private set; }
    public string? Error { get; set; }
    const int RowH = 30, Top0 = 58;

    public TraceWidget() : base("Путь до сервера", 28 + Top0 + 70)
    {
        Controls.Add(Run);
    }

    public void Begin(string t)
    {
        target = t;
        hops.Clear();
        Error = null;
        Running = true;
        Fit();
        Invalidate(true);
    }

    public void Hop(GameNet.Hop h)
    {
        int i = hops.FindIndex(x => x.Ttl == h.Ttl);
        if (i >= 0) hops[i] = h; else hops.Add(h);
        Fit();
        Invalidate(false);
    }

    public void End(string? error = null)
    {
        Running = false;
        Error = error;
        Fit();
        Invalidate(true);
    }

    public event Action? Resized;

    void Fit()
    {
        int h = HeadH + Top0 + Math.Max(1, hops.Count) * RowH + (hops.Count > 0 ? 54 : 30);
        if (Height != h) { Height = h; Resized?.Invoke(); }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Run is null) return;
        Run.Location = new Point(Width - Run.Width - 8, HeadH + 6);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        string head = target == null ? "Где теряется пинг?" : Running ? $"Трассирую {target}…" : $"До {target}: {hops.Count} узлов";
        TextRenderer.DrawText(g, head, Theme.Semi, new Rectangle(r.Left + 18, r.Top + 16, Run.Left - r.Left - 30, 22), C(Theme.Text), flags);
        if (target == null)
        {
            TextRenderer.DrawText(g, "Покажет каждый узел на пути пакетов до сервера: где растёт задержка и где теряются пакеты — у вас дома, у провайдера или дальше.",
                Theme.Body, new Rectangle(r.Left + 18, r.Top + Top0 - 12, r.Width - 36, 60), C(Theme.Muted), TextFormatFlags.WordBreak);
            return;
        }
        int lineX = r.Left + 30;
        double prev = 0;
        for (int i = 0; i < hops.Count; i++)
        {
            var h = hops[i];
            int y = r.Top + Top0 + i * RowH;
            // линия маршрута
            if (i < hops.Count - 1)
                using (var pen = new Pen(Color.FromArgb((int)(60 * Appear), Theme.Accent), 2)) g.DrawLine(pen, lineX, y + 15, lineX, y + 15 + RowH);
            var col = h.Ip == null ? Theme.Dim : h.Final ? Theme.Accent : GameUI.PingColor(h.Ms);
            if (h.Ms is double jump && prev > 0 && jump - prev > 30) col = Theme.Warn;
            float d = h.Final ? 12 : 9;
            GameUI.Dot(g, col, lineX - d / 2, y + 15 - d / 2, d, Appear);
            TextRenderer.DrawText(g, h.Ttl.ToString(), Theme.Tiny, new Rectangle(r.Left + 4, y, 16, RowH), C(Theme.Dim), flags);
            string name = h.Ip == null ? "* * *  узел не отвечает" : h.Host != null ? $"{h.Host}  ({h.Ip})" : h.Ip;
            TextRenderer.DrawText(g, name, Theme.Body, new Rectangle(lineX + 16, y, r.Width - 220, RowH), C(h.Ip == null ? Theme.Dim : Theme.Text), flags);
            string ms = h.Ms is double v ? $"{v:0} мс" : "—";
            if (h.Ip != null && h.Lost > 0) ms += $" · потери {h.Lost}/{h.Sent}";
            TextRenderer.DrawText(g, ms, Theme.Tiny, new Rectangle(r.Right - 140, y, 124, RowH), C(h.Ip == null ? Theme.Dim : Theme.Muted),
                flags | TextFormatFlags.Right);
            if (h.Ms is double pv) prev = pv;
        }
        if (!Running && hops.Count > 0)
        {
            var (text, c) = Verdict();
            TextRenderer.DrawText(g, text, Theme.Body, new Rectangle(r.Left + 18, r.Top + Top0 + hops.Count * RowH + 8, r.Width - 36, 40), C(c), TextFormatFlags.WordBreak);
        }
    }

    (string, Color) Verdict()
    {
        if (Error != null) return (Error, Theme.Warn);
        var ok = hops.Where(h => h.Ms != null).ToList();
        if (ok.Count == 0) return ("Ни один узел не ответил — возможно, ICMP блокируется сетью", Theme.Warn);
        if (!hops.Any(h => h.Final)) return ("Сервер не отвечает на ping (часто так настроено специально) — смотрите на задержку последних отвечающих узлов", Theme.Muted);
        if (ok.Count > 0 && ok[0].Ms > 20) return ($"Уже до роутера {ok[0].Ms:0} мс — проблема в домашней сети или Wi-Fi", Theme.Warn);
        for (int i = 1; i < ok.Count; i++)
            if (ok[i].Ms - ok[i - 1].Ms > 40 && ok.Skip(i).All(h => h.Ms >= ok[i].Ms * 0.8))
                return ($"Задержка вырастает на узле {ok[i].Ttl} ({ok[i].Host ?? ok[i].Ip}) и дальше не уменьшается — это участок провайдера или магистрали", Theme.Warn);
        return ("Задержка растёт плавно — маршрут в порядке", Theme.Good);
    }
}

// ═══════════════════════════════════════════════════════════════
//  Фоновые программы
// ═══════════════════════════════════════════════════════════════

class BgAppsWidget : Widget
{
    public readonly FlatBtn CloseNow = new("", "Закрыть отмеченные") { Danger = true };
    public readonly FlatBtn Refresh = new("", "");
    List<(BackgroundApps.App App, long Bytes)>? running;
    HashSet<string> selected = BackgroundApps.Selected;
    int hot = -1;
    const int RowH = 40, Top0 = 46;

    public BgAppsWidget() : base("Фоновые программы", 28 + Top0 + RowH + 56)
    {
        Controls.Add(CloseNow);
        Controls.Add(Refresh);
    }

    public event Action? Resized;
    public IEnumerable<BackgroundApps.App> Checked => (running ?? new()).Where(x => selected.Contains(x.App.Key)).Select(x => x.App);

    public void SetData(List<(BackgroundApps.App, long)> data)
    {
        running = data;
        int h = HeadH + Top0 + Math.Max(1, data.Count) * RowH + 56;
        if (Height != h) { Height = h; Resized?.Invoke(); }
        PerformLayout();
        Invalidate(true);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (CloseNow is null || Refresh is null) return;
        Refresh.Location = new Point(Width - Refresh.Width - 12, Height - Refresh.Height - 12);
        CloseNow.Location = new Point(14, Height - CloseNow.Height - 12);
    }

    Rectangle Row(int i) => new(10, HeadH + Top0 + i * RowH, Width - 20, RowH - 4);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = -1;
        for (int i = 0; i < (running?.Count ?? 0); i++) if (Row(i).Contains(e.Location)) { h = i; break; }
        if (h != hot) { hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(false); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { if (hot != -1) { hot = -1; Invalidate(false); } base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (running != null && hot >= 0 && hot < running.Count)
        {
            var key = running[hot].App.Key;
            if (!selected.Remove(key)) selected.Add(key);
            BackgroundApps.Selected = selected;
            Invalidate(false);
        }
        base.OnMouseClick(e);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        TextRenderer.DrawText(g, "Отмеченные закрываются кнопкой и сами — в игровом режиме, если включено «Закрывать фоновые программы»",
            Theme.Tiny, new Rectangle(r.Left + 18, r.Top + 10, r.Width - 36, 30), C(Theme.Muted), TextFormatFlags.WordBreak);
        if (running == null || running.Count == 0)
        {
            TextRenderer.DrawText(g, running == null ? "Смотрю, что запущено…" : "Из списка ничего не запущено — отлично",
                Theme.Body, new Rectangle(r.Left + 18, r.Top + Top0, r.Width - 36, RowH), C(Theme.Text), flags);
            return;
        }
        for (int i = 0; i < running.Count; i++)
        {
            var (app, bytes) = running[i];
            var row = Row(i);
            if (i == hot) Theme.Fill(g, Color.FromArgb((int)(14 * Appear), 255, 255, 255), row, 8);
            bool on = selected.Contains(app.Key);
            var box = new RectangleF(row.X + 10, row.Y + (row.Height - 18) / 2f, 18, 18);
            Theme.Fill(g, C(on ? Theme.Accent : Theme.Off), box, 5);
            if (on) TextRenderer.DrawText(g, "", Theme.Icon, Rectangle.Round(box), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, app.Name, Theme.Semi, new Rectangle(row.X + 38, row.Y + 1, row.Width - 140, 18), C(Theme.Text), flags);
            TextRenderer.DrawText(g, app.Why, Theme.Tiny, new Rectangle(row.X + 38, row.Y + 18, row.Width - 140, 16), C(Theme.Muted), flags);
            TextRenderer.DrawText(g, GameUI.Bytes(bytes), Theme.Tiny, new Rectangle(row.Right - 100, row.Y, 90, row.Height), C(Theme.Muted), flags | TextFormatFlags.Right);
        }
    }
}

// ═══════════════════════════════════════════════════════════════
//  Кэш шейдеров
// ═══════════════════════════════════════════════════════════════

class ShaderWidget : Widget
{
    public readonly FlatBtn Clean = new("", "Очистить");
    List<(ShaderCache.Group Group, long Bytes)>? sizes;
    public bool Busy { get; set; }
    const int RowH = 28, Top0 = 62;

    public ShaderWidget() : base("Кэш шейдеров", 28 + Top0 + 5 * RowH + 56)
    {
        Controls.Add(Clean);
    }

    public long Total => sizes?.Sum(s => s.Bytes) ?? 0;

    public void SetData(List<(ShaderCache.Group, long)> data)
    {
        sizes = data;
        Busy = false;
        Invalidate(true);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Clean is null) return;
        Clean.Location = new Point(14, Height - Clean.Height - 12);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        TextRenderer.DrawText(g, "Чистите после обновления драйвера или если игра начала фризить и показывать артефакты. Первый запуск после очистки дольше — кэш соберётся заново.",
            Theme.Tiny, new Rectangle(r.Left + 18, r.Top + 10, r.Width - 36, 46), C(Theme.Muted), TextFormatFlags.WordBreak);
        if (sizes == null || Busy)
        {
            TextRenderer.DrawText(g, Busy ? "Очищаю…" : "Считаю размер…", Theme.Body, new Rectangle(r.Left + 18, r.Top + Top0, r.Width - 36, RowH), C(Theme.Text), flags);
            return;
        }
        long max = Math.Max(1, sizes.Max(s => s.Bytes));
        for (int i = 0; i < sizes.Count; i++)
        {
            var (grp, bytes) = sizes[i];
            int y = r.Top + Top0 + i * RowH;
            TextRenderer.DrawText(g, grp.Name, Theme.Body, new Rectangle(r.Left + 18, y, 150, RowH), C(bytes > 0 ? Theme.Text : Theme.Dim), flags);
            float bx = r.Left + 172, bw = r.Width - 172 - 100;
            Theme.Fill(g, Color.FromArgb((int)(18 * Appear), 255, 255, 255), new RectangleF(bx, y + 12, bw, 4), 2);
            if (bytes > 0) Theme.Fill(g, C(Theme.Lerp(Theme.Accent, Color.FromArgb(150, 160, 175), 0.3)), new RectangleF(bx, y + 12, (float)(bw * bytes / max * Appear), 4), 2);
            TextRenderer.DrawText(g, GameUI.Bytes(bytes), Theme.Tiny, new Rectangle(r.Right - 96, y, 80, RowH), C(Theme.Muted), flags | TextFormatFlags.Right);
        }
        TextRenderer.DrawText(g, "Всего " + GameUI.Bytes(Total), Theme.Semi, new Rectangle(Clean.Right + 14, Clean.Top, 200, Clean.Height), C(Theme.Text), flags);
    }
}

// ═══════════════════════════════════════════════════════════════
//  Отчёты о соединении
// ═══════════════════════════════════════════════════════════════

class ReportsWidget : Widget
{
    readonly List<MatchReport> reports;
    public event Action<MatchReport>? ReportClicked;
    int hot = -1;
    const int RowH = 46, Max = 6;

    public ReportsWidget(List<MatchReport> reports) : base("Отчёты о соединении", 28 + 12 + Math.Max(1, Math.Min(Max, reports.Count)) * RowH + (reports.Count == 0 ? 30 : 10))
    {
        this.reports = reports;
    }

    Rectangle Row(int i) => new(10, HeadH + 10 + i * RowH, Width - 20, RowH - 4);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = -1;
        for (int i = 0; i < Math.Min(Max, reports.Count); i++) if (Row(i).Contains(e.Location)) { h = i; break; }
        if (h != hot) { hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(false); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { if (hot != -1) { hot = -1; Invalidate(false); } base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (hot >= 0 && hot < reports.Count) ReportClicked?.Invoke(reports[hot]);
        base.OnMouseClick(e);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        if (reports.Count == 0)
        {
            TextRenderer.DrawText(g, "Появятся после первой игры в игровом режиме: монитор раз в секунду меряет пинг до роутера и до интернета и после матча покажет, где были скачки.",
                Theme.Body, new Rectangle(r.Left + 18, r.Top + 12, r.Width - 36, 60), C(Theme.Muted), TextFormatFlags.WordBreak);
            return;
        }
        for (int i = 0; i < Math.Min(Max, reports.Count); i++)
        {
            var rep = reports[i];
            var row = Row(i);
            if (i == hot) Theme.Fill(g, Color.FromArgb((int)(14 * Appear), 255, 255, 255), row, 8);
            var (_, tone) = rep.Verdict();
            GameUI.Dot(g, tone == Tone.Success ? Theme.Good : Theme.Warn, row.X + 12, row.Y + 16, 8, Appear);
            TextRenderer.DrawText(g, rep.Game, Theme.Semi, new Rectangle(row.X + 30, row.Y + 2, row.Width - 150, 20), C(Theme.Text), flags);
            TextRenderer.DrawText(g, $"{rep.Start:d MMM, HH:mm} · {GameUI.Duration(rep.Duration)}", Theme.Tiny, new Rectangle(row.X + 30, row.Y + 21, row.Width - 150, 18), C(Theme.Muted), flags);
            string stat = $"{rep.Avg:0} мс ±{rep.Jitter:0}" + (rep.Loss > 0.05 ? $" · {rep.Loss:0.#}%" : "");
            TextRenderer.DrawText(g, stat, Theme.Semi, new Rectangle(row.Right - 130, row.Y, 120, row.Height), C(GameUI.PingColor(rep.Avg)), flags | TextFormatFlags.Right);
        }
    }
}

// ═══════════════════════════════════════════════════════════════
//  Окно игры: пресет, настройки exe, советы
// ═══════════════════════════════════════════════════════════════

class GameDialog : RoundForm
{
    readonly InstalledGame game;
    readonly List<Tweak> exeTweaks;
    readonly List<(Tweak T, Toggle Tg)> rows = new();
    readonly List<(string Name, bool On)> system;
    readonly string[] tips;
    readonly FlatBtn apply = new("", "Применить рекомендуемое") { Primary = true };
    readonly FlatBtn close = new("", "Закрыть");
    readonly FlatBtn folder = new("", "Папка игры");
    readonly FlatBtn remove = new("", "Убрать");
    readonly FlatBtn mark = new("", "Отметить недостающие");
    bool loaded, closing;
    int baseTop;
    readonly int colW, tipsH;
    const int W = 1000, Pad = 24, Top0 = 168, ExeRowH = 66;

    /// <summary>Отметить системные настройки (обрабатывает главное окно). Возвращает, сколько отмечено.</summary>
    public Func<string[], int>? MarkSystem;
    public bool Changed { get; private set; }

    public GameDialog(InstalledGame game, Func<string, bool> isOn)
    {
        this.game = game;
        exeTweaks = game.Exe != null ? GameExe.For(game.Exe) : new();
        system = GameCatalog.SystemTweaks(game.Info).Select(n => (n, isOn(n))).ToList();
        tips = (game.Info?.Tips ?? Array.Empty<string>()).Concat(game.Info?.Note is string n ? new[] { n } : Array.Empty<string>())
            .Concat(GameCatalog.CommonTips).ToArray();
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Opacity = 0;

        colW = (W - Pad * 2 - 40) / 3;
        tipsH = tips.Sum(t => TextRenderer.MeasureText(t, Theme.Body, new Size(colW - 18, 0), TextFormatFlags.WordBreak).Height + 8);
        int left = exeTweaks.Count * ExeRowH, mid = system.Count * 26 + 50;
        int h = Top0 + 30 + Math.Max(left, Math.Max(mid, tipsH)) + 84;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(W, Math.Min(h, area.Height - 40));

        foreach (var t in exeTweaks)
        {
            var tg = new Toggle { Enabled = false };
            var tw = t;
            tg.CheckedChanged += async (_, _) =>
            {
                bool on = tg.Checked;
                tg.Enabled = false;
                try { await Task.Run(() => GameExe.Set(tw, on)); Changed = true; }
                catch (Exception ex) { tg.Checked = !on; Dialog.Info(this, "Не получилось", ex.Message, Tone.Error); }
                tg.Enabled = true;
            };
            rows.Add((t, tg));
            Controls.Add(tg);
        }

        apply.Click += async (_, _) => await ApplyRecommended();
        close.Click += (_, _) => Finish();
        folder.Click += (_, _) => { try { System.Diagnostics.Process.Start("explorer.exe", game.Exe != null ? $"/select,\"{game.Exe}\"" : $"\"{game.Dir}\""); } catch { } };
        remove.Visible = game.Custom;
        remove.Click += (_, _) => { if (game.Exe != null) GameLibrary.RemoveCustom(game.Exe); Changed = true; Finish(); };
        mark.Click += (_, _) =>
        {
            int n = MarkSystem?.Invoke(system.Where(s => !s.On).Select(s => s.Name).ToArray()) ?? 0;
            Toast.Pop(Owner ?? this, n == 0 ? "Всё уже включено или отмечено" : $"Отмечено {n} — нажмите «Применить» в главном окне", n == 0 ? Tone.Info : Tone.Success);
        };
        mark.Visible = system.Any(s => !s.On);
        Controls.AddRange(new Control[] { apply, close, folder, remove, mark });

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Finish(); };
        Layout_();
    }

    void Layout_()
    {
        apply.Location = new Point(Width - Pad - apply.Width + apply.Pad, Height - Pad - apply.Height + apply.Pad);
        close.Location = new Point(apply.Left + apply.Pad - close.Width - 8, apply.Top + apply.Pad);
        folder.Location = new Point(Pad + GameLibrary.CoverW + 22, 100);
        remove.Location = new Point(folder.Right + 8, 100);
        for (int i = 0; i < rows.Count; i++)
            rows[i].Tg.Location = new Point(Pad + colW - rows[i].Tg.Width, Top0 + 30 + i * ExeRowH + 4);
        int mx = Pad + colW + 20;
        mark.Location = new Point(mx - 6, Top0 + 30 + system.Count * 26 + 8);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Owner != null)
            Location = new Point(Owner.Left + (Owner.Width - Width) / 2, Math.Max(Owner.Top + 10, Owner.Top + (Owner.Height - Height) / 2));
        baseTop = Top;
        Motion.Run(260, p => { Opacity = p; Top = baseTop + (int)Math.Round(14 * (1 - p)); });
        // состояние (QoS — через PowerShell, поэтому в фоне)
        var states = await Task.Run(() => rows.Select(r => { try { return r.T.IsApplied(); } catch { return false; } }).ToList());
        if (IsDisposed) return;
        for (int i = 0; i < rows.Count; i++)
        {
            rows[i].Tg.Checked = states[i];
            rows[i].Tg.Enabled = true;
        }
        loaded = true;
        Invalidate();
    }

    async Task ApplyRecommended()
    {
        if (!loaded) return;
        apply.Enabled = false;
        int n = 0;
        foreach (var (t, tg) in rows)
        {
            if (tg.Checked || !GameExe.Recommended(t.Name, game.Info)) continue;
            try { await Task.Run(() => GameExe.Set(t, true)); tg.Checked = true; n++; Changed = true; } catch { }
        }
        int marked = MarkSystem?.Invoke(system.Where(s => !s.On).Select(s => s.Name).ToArray()) ?? 0;
        apply.Enabled = true;
        string msg = n == 0 && marked == 0 ? "Всё рекомендуемое уже включено"
            : (n > 0 ? $"Для {game.ExeName} включено: {n}" : "") + (marked > 0 ? (n > 0 ? ". " : "") + $"Системных отмечено: {marked} — нажмите «Применить»" : "");
        Toast.Pop(Owner ?? this, msg, n + marked == 0 ? Tone.Info : Tone.Success);
        Finish();
    }

    void Finish()
    {
        if (closing) return;
        closing = true;
        Motion.Run(150, p => Opacity = 1 - p, Motion.InOut, 0, () => { DialogResult = DialogResult.OK; Close(); });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis;

        // шапка: обложка, название, источник, путь
        var cover = new RectangleF(Pad, Pad, GameLibrary.CoverW, GameLibrary.CoverH);
        using (var path = Theme.Round(cover, 12))
        {
            var st = g.Save();
            g.SetClip(path);
            if (game.Cover != null) g.DrawImage(game.Cover, cover);
            else
            {
                using var lg = new LinearGradientBrush(cover, Theme.Lerp(Theme.Card, Theme.Accent, 0.35), Theme.Lerp(Theme.Bg, Theme.Accent, 0.08), 35f);
                g.FillRectangle(lg, cover);
                if (game.Icon != null) g.DrawImage(game.Icon, cover.X + cover.Width / 2 - 20, cover.Y + cover.Height / 2 - 20, 40, 40);
            }
            g.Restore(st);
            using var pen = new Pen(Color.FromArgb(40, 255, 255, 255));
            g.DrawPath(pen, path);
        }
        int hx = Pad + GameLibrary.CoverW + 22;
        TextRenderer.DrawText(g, game.Title, Theme.F(15f, FontStyle.Bold), new Rectangle(hx, Pad - 2, Width - hx - Pad, 30), Theme.Text, flags);
        string sub = game.Source + (game.Info != null ? " · готовый пресет" : " · общий пресет для любой игры");
        TextRenderer.DrawText(g, sub, Theme.Body, new Rectangle(hx, Pad + 30, Width - hx - Pad, 20), game.Info != null ? Theme.Accent : Theme.Muted, flags);
        TextRenderer.DrawText(g, game.Exe ?? game.Dir, Theme.Tiny, new Rectangle(hx, Pad + 52, Width - hx - Pad, 18), Theme.Dim, flags | TextFormatFlags.PathEllipsis);

        // колонка 1: Windows для этой игры
        int x1 = Pad, x2 = Pad + colW + 20, x3 = Pad + (colW + 20) * 2;
        Label(g, "WINDOWS ДЛЯ ЭТОЙ ИГРЫ", x1);
        for (int i = 0; i < rows.Count; i++)
        {
            var t = rows[i].T;
            int y = Top0 + 30 + i * ExeRowH;
            int tw = colW - 60;
            TextRenderer.DrawText(g, t.Name, Theme.Semi, new Rectangle(x1, y, tw, 20), Theme.Text, flags);
            if (GameExe.Recommended(t.Name, game.Info))
            {
                int nw = Math.Min(tw, TextRenderer.MeasureText(t.Name, Theme.Semi).Width);
                if (nw + 90 < tw + 50) GameUI.Chip(g, "СОВЕТУЕМ", x1 + nw + 6, y + 2, Theme.Accent);
            }
            TextRenderer.DrawText(g, t.Description, Theme.Tiny, new Rectangle(x1, y + 21, colW - 56, ExeRowH - 26), Theme.Muted,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }
        if (rows.Count == 0)
            TextRenderer.DrawText(g, "Не найден exe игры — добавьте игру вручную через «Добавить игру»", Theme.Body, new Rectangle(x1, Top0 + 30, colW, 60), Theme.Muted, TextFormatFlags.WordBreak);

        // колонка 2: системные настройки
        Label(g, "СИСТЕМА", x2);
        for (int i = 0; i < system.Count; i++)
        {
            var (name, on) = system[i];
            int y = Top0 + 30 + i * 26;
            GameUI.Dot(g, on ? Theme.Good : Theme.Off, x2 + 2, y + 6, 8);
            TextRenderer.DrawText(g, name, Theme.Body, new Rectangle(x2 + 18, y, colW - 18, 22), on ? Theme.Text : Theme.Muted, flags);
        }

        // колонка 3: советы по самой игре
        Label(g, game.Info != null ? "В САМОЙ ИГРЕ" : "СОВЕТЫ", x3);
        int ty = Top0 + 30;
        foreach (var tip in tips)
        {
            var sz = TextRenderer.MeasureText(tip, Theme.Body, new Size(colW - 18, 0), TextFormatFlags.WordBreak);
            if (ty + sz.Height > Height - 84) break;
            Theme.Fill(g, Theme.Accent, new RectangleF(x3 + 2, ty + 7, 5, 5), 2.5f);
            TextRenderer.DrawText(g, tip, Theme.Body, new Rectangle(x3 + 16, ty, colW - 18, sz.Height), Theme.Text, TextFormatFlags.WordBreak);
            ty += sz.Height + 8;
        }

        using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, Pad, Height - 74, Width - Pad, Height - 74);
        TextRenderer.DrawText(g, "Файлы и конфиги игры не меняются. Всё, что меняется в Windows, — в «Истории» и откатывается.", Theme.Tiny,
            new Rectangle(Pad, Height - 56, close.Left - Pad - 12, 32), Theme.Dim, TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter);
    }

    static void Label(Graphics g, string text, int x) =>
        TextRenderer.DrawText(g, text, Theme.Small, new Point(x - 1, Top0), Theme.Muted);
}

// ═══════════════════════════════════════════════════════════════
//  Окно отчёта о соединении
// ═══════════════════════════════════════════════════════════════

class ReportDialog : RoundForm
{
    readonly MatchReport rep;
    bool closing;
    int baseTop;

    public ReportDialog(MatchReport rep)
    {
        this.rep = rep;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Opacity = 0;
        Size = new Size(720, 470);
        var ok = new FlatBtn("", "Понятно") { Primary = true };
        ok.Location = new Point(Width - 24 - ok.Width + ok.Pad, Height - 22 - ok.Height + ok.Pad);
        ok.Click += (_, _) => Finish();
        Controls.Add(ok);
        KeyDown += (_, e) => { if (e.KeyCode is Keys.Escape or Keys.Enter) Finish(); };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Owner != null) Location = new Point(Owner.Left + (Owner.Width - Width) / 2, Owner.Top + (Owner.Height - Height) / 2);
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
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
        TextRenderer.DrawText(g, rep.Game, Theme.F(14f, FontStyle.Bold), new Rectangle(24, 20, Width - 48, 28), Theme.Text, flags);
        TextRenderer.DrawText(g, $"{rep.Start:d MMMM, HH:mm}–{rep.End:HH:mm} · {GameUI.Duration(rep.Duration)} · пинг до {rep.Target}" + (rep.Wifi ? " · Wi-Fi" : ""),
            Theme.Body, new Rectangle(24, 50, Width - 48, 20), Theme.Muted, flags);

        // цифры
        var stats = new (string K, string V, Color C)[]
        {
            ("Средний пинг", $"{rep.Avg:0} мс", GameUI.PingColor(rep.Avg)),
            ("95% времени", $"до {rep.P95:0} мс", Theme.Text),
            ("Разброс", $"±{rep.Jitter:0} мс", rep.Jitter > 12 ? Theme.Warn : Theme.Text),
            ("Потери", $"{rep.Loss:0.#}%", rep.Loss > 1 ? Theme.Bad : Theme.Text),
            ("Скачки", rep.Spikes.ToString(), rep.Spikes > 5 ? Theme.Warn : Theme.Text),
            ("До роутера", rep.GwAvg > 0 ? $"{rep.GwAvg:0} мс ±{rep.GwJitter:0}" : "—", rep.GwJitter > 12 || rep.GwLoss > 1 ? Theme.Warn : Theme.Text),
        };
        int cw = (Width - 48) / stats.Length;
        for (int i = 0; i < stats.Length; i++)
        {
            int x = 24 + i * cw;
            TextRenderer.DrawText(g, stats[i].K, Theme.Tiny, new Rectangle(x, 86, cw - 6, 18), Theme.Muted, flags);
            TextRenderer.DrawText(g, stats[i].V, Theme.F(11f, FontStyle.Bold), new Rectangle(x, 104, cw - 6, 24), stats[i].C, flags);
        }

        // график
        var area = new RectangleF(24, 148, Width - 48, 170);
        Theme.Fill(g, Color.FromArgb(14, 255, 255, 255), area, 10);
        var s = rep.Series;
        if (s.Length >= 2)
        {
            double max = Math.Max(rep.Avg * 2, s.Where(v => v >= 0).DefaultIfEmpty(50).Max() * 1.1);
            float step = (area.Width - 20) / (s.Length - 1);
            var pts = new List<PointF>();
            for (int i = 0; i < s.Length; i++)
            {
                float x = area.X + 10 + i * step;
                if (s[i] < 0)
                {
                    using var lossPen = new Pen(Color.FromArgb(120, Theme.Bad), 1);
                    g.DrawLine(lossPen, x, area.Y + 6, x, area.Bottom - 6);
                    continue;
                }
                pts.Add(new PointF(x, area.Bottom - 8 - (float)(s[i] / max * (area.Height - 16))));
            }
            float avgY = area.Bottom - 8 - (float)(rep.Avg / max * (area.Height - 16));
            using (var dash = new Pen(Color.FromArgb(70, 255, 255, 255)) { DashStyle = DashStyle.Dash }) g.DrawLine(dash, area.X + 10, avgY, area.Right - 10, avgY);
            if (pts.Count >= 2)
            {
                using var glow = new Pen(Color.FromArgb(50, Theme.Accent), 4f) { LineJoin = LineJoin.Round };
                g.DrawLines(glow, pts.ToArray());
                using var pen = new Pen(Theme.Accent, 1.5f) { LineJoin = LineJoin.Round };
                g.DrawLines(pen, pts.ToArray());
            }
            TextRenderer.DrawText(g, $"{max:0} мс", Theme.Tiny, new Point((int)area.X + 8, (int)area.Y + 4), Theme.Dim);
        }

        var (text, tone) = rep.Verdict();
        var col = tone == Tone.Success ? Theme.Good : Theme.Warn;
        GameUI.Dot(g, col, 26, 340, 10);
        TextRenderer.DrawText(g, text, Theme.Semi, new Rectangle(46, 332, Width - 70, 44), Theme.Text, TextFormatFlags.WordBreak);
        TextRenderer.DrawText(g, "Красные линии — потерянные пакеты. Пунктир — средний пинг.", Theme.Tiny, new Rectangle(24, Height - 50, 420, 20), Theme.Dim, flags);
    }
}
