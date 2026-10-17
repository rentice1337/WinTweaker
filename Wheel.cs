using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace WinTweaker;

/// <summary>
/// Окно с попиксельной прозрачностью: кадр рисуется в постоянную DIB-секцию (premultiplied alpha)
/// и отдаётся окну через UpdateLayeredWindow — круглые формы, свечение и плавное проявление без рамок.
/// </summary>
abstract class LayeredForm : Form
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, IntPtr pptDst, ref SIZE sz, IntPtr src, ref POINT srcPt, int key, ref BLEND blend, int flags);

    IntPtr memDC, dib, oldBmp;
    Bitmap? surface;
    Graphics? gfx;
    /// <summary>Общая прозрачность окна (для проявления и исчезновения).</summary>
    protected byte Alpha = 255;

    protected LayeredForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // без этого Windows может «прилепить» или развернуть окно при перетаскивании к краю экрана
        MaximizeBox = false;
        MinimizeBox = false;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00080000 /* LAYERED */ | 0x00000080 /* TOOLWINDOW */ | 0x00000008 /* TOPMOST */;
            return cp;
        }
    }

    void EnsureSurface()
    {
        if (surface != null && surface.Width == Width && surface.Height == Height) return;
        FreeSurface();
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = Width, biHeight = -Height, biPlanes = 1, biBitCount = 32 };
        IntPtr screen = GetDC(IntPtr.Zero);
        memDC = CreateCompatibleDC(screen);
        ReleaseDC(IntPtr.Zero, screen);
        dib = CreateDIBSection(memDC, ref bi, 0, out var bits, IntPtr.Zero, 0);
        oldBmp = SelectObject(memDC, dib);
        surface = new Bitmap(Width, Height, Width * 4, PixelFormat.Format32bppPArgb, bits);
        gfx = Graphics.FromImage(surface);
    }

    void FreeSurface()
    {
        gfx?.Dispose(); gfx = null;
        surface?.Dispose(); surface = null;
        if (memDC != IntPtr.Zero)
        {
            SelectObject(memDC, oldBmp);
            DeleteObject(dib);
            DeleteDC(memDC);
            memDC = dib = IntPtr.Zero;
        }
    }

    protected void Present()
    {
        if (!IsHandleCreated || IsDisposed) return;
        try
        {
            EnsureSurface();
            var g = gfx!;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Draw(g);
            g.Flush(FlushIntention.Sync);
            var size = new SIZE { W = Width, H = Height };
            var src = new POINT();
            var blend = new BLEND { Op = 0, Flags = 0, Alpha = Alpha, Format = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, ref size, memDC, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        catch { }
    }

    protected abstract void Draw(Graphics g);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        FreeSurface();
        base.OnFormClosed(e);
    }

    protected static void Txt(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment h = StringAlignment.Center,
        StringAlignment v = StringAlignment.Center, bool wrap = false)
    {
        using var b = new SolidBrush(c);
        using var sf = new StringFormat
        {
            Alignment = h, LineAlignment = v, Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = wrap ? 0 : StringFormatFlags.NoWrap
        };
        g.DrawString(s, f, b, r, sf);
    }
}

/// <summary>Команда пульта: сектор кольца или результат поиска.</summary>
sealed record WheelCmd(string Title, string Short, string Glyph, string Sub, Action Run, bool KeepOpen = false, int Rank = 0);

/// <summary>
/// Круговой пульт WinTweak (левый клик по значку в трее или Ctrl+Alt+W).
///  • 8 секторов вокруг курсора: наводишь — сектор выдвигается и светится, отпускаешь кнопку — действие.
///  • «Игры» раскрывают второе кольцо с обложками — запуск в один клик (игровой режим включится сам).
///  • Начни печатать — кольцо превращается в палитру: сектора заменяются найденными командами,
///    настройками, разделами и играми. Работает и в неправильной раскладке («gbyu» → «пинг»).
///  • Под кольцом — одна главная подсказка «прямо сейчас»; клик по ней — нужное действие.
///  • В центре — живая загрузка ОЗУ и CPU, описание наведённого и результаты (пинг, проверка).
/// </summary>
sealed class TrayWheel : LayeredForm
{
    // геометрия в «базовых» пикселях; размер пульта — масштабом при рисовании и в обработке мыши
    const int S = 600;
    const float C = S / 2f, R0 = 56, Ri = 68, Ro = 150, Gi = 166, Go = 242;
    readonly float k;
    readonly int gamesMax, gamesIdx;
    readonly bool showHint;
    readonly string[] ids;

    static readonly Font IconF = new("Segoe MDL2 Assets", 13f), IconS = new("Segoe MDL2 Assets", 10f);
    static readonly Font LabelF = new("Segoe UI Semibold", 7.75f), BigF = new("Segoe UI Semibold", 17f),
        MidF = new("Segoe UI Semibold", 10f), SmallF = new("Segoe UI", 7.75f), PillF = new("Segoe UI", 8.75f);
    static readonly Dictionary<string, Bitmap> slotCache = new();

    readonly MainForm main;
    readonly WheelCmd[] defaults;
    readonly List<WheelCmd> all;
    List<WheelCmd> results = new();
    string query = "";
    readonly List<InstalledGame> games;
    Hints.Hint hint;

    readonly double[] hl = new double[8];
    int hover = -1, hoverGame = -1, keySel = -1;
    bool hoverPill, hoverCenter;
    Point mouse = new(-1000, -1000);
    double openStart, closeStart = -1, hoverSince, leaveSince, ringP;
    bool ringWanted;
    RectangleF pill;

    string? msg1, msg2;
    Color msgColor = Theme.Text;
    double msgUntil;

    readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    bool dirty = true;

    bool Search => query.Length > 0;
    int SlotCount => gamesIdx < 0 ? 0 : Math.Min(gamesMax, games.Count);

    public TrayWheel(MainForm main, Point at)
    {
        this.main = main;
        KeyPreview = true;
        k = WheelSettings.Scale;
        gamesMax = WheelSettings.Games;
        showHint = WheelSettings.Hint;
        ids = WheelSettings.Sectors;
        gamesIdx = Array.IndexOf(ids, "games");
        int px = (int)Math.Round(S * k);
        Size = new Size(px, px);
        var area = Screen.FromPoint(at).WorkingArea;
        if (WheelSettings.Centered) at = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
        Location = new Point(Math.Clamp(at.X - px / 2, area.Left, Math.Max(area.Left, area.Right - px)),
                             Math.Clamp(at.Y - px / 2, area.Top, Math.Max(area.Top, area.Bottom - px)));
        games = GameLibrary.Games.Where(g => g.Exe != null).Take(gamesMax).ToList();
        hint = Tray.Current;
        try { hint = Hints.Compute(main); } catch { }
        defaults = Defaults();
        all = BuildIndex();
        Alpha = 0;
        timer.Tick += (_, _) => Tick();
        Sampler.Updated += OnSample;
    }

    void OnSample() => dirty = true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        openStart = Motion.Now;
        Present();
        timer.Start();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        CloseAnimated();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Dispose();
        Sampler.Updated -= OnSample;
        base.OnFormClosed(e);
    }

    public void CloseAnimated()
    {
        if (closeStart >= 0 || IsDisposed) return;
        closeStart = Motion.Now;
        if (Motion.Reduced) Close();
    }

    // ───────── Команды ─────────

    WheelCmd[] Defaults() => ids.Select(Make).ToArray();

    /// <summary>Команда по её идентификатору (список — в <see cref="WheelSettings.Actions"/>).</summary>
    WheelCmd Make(string id)
    {
        string hub = MainForm.GamesPageName;
        switch (id)
        {
            case "mode":
                string modeSub = GameMode.Active ? $"включён · {GameMode.Reason}" : GameWatcher.Enabled ? "выключен · включится сам при запуске игры" : "выключен";
                return new WheelCmd("Игровой режим", GameMode.Active ? "Режим: вкл" : "Игровой", "\uE7FC", modeSub, ToggleMode, true);
            case "games":
                return new WheelCmd("Игры", "Игры", "\uE768", games.Count == 0 ? (GameLibrary.Scanned ? "игр не найдено" : "ищу игры…") : $"{games.Count} — запуск в один клик",
                    () => { ringWanted = true; dirty = true; }, true);
            case "open": return new WheelCmd("Открыть WinTweak", "Открыть", "\uE8A7", "главное окно", main.ShowFromTray);
            case "ready": return new WheelCmd("Готовность", "Готовность", "\uE9D9", "проверить ПК перед игрой", RunReady, true);
            case "exit": return new WheelCmd("Выход", "Выход", "\uE7E8", "закрыть WinTweak", main.ExitApp);
            case "gpu":
                return new WheelCmd("Сброс драйвера", "Драйвер", "\uE7F4", "как Win+Ctrl+Shift+B — экран мигнёт",
                    () => { var t = new System.Windows.Forms.Timer { Interval = 180 }; t.Tick += (_, _) => { t.Dispose(); GpuReset.Quick(); }; t.Start(); });
            case "dns": return new WheelCmd("Очистить кэш DNS", "DNS", "\uE774", "если сайт «переехал» или не открывается", FlushDns, true);
            case "ping": return new WheelCmd("Пинг", "Пинг", "\uE701", $"до {GameMode.Target}", RunPing, true);
            case "overlay":
                return new WheelCmd("Оверлей", Overlay.Visible ? "Оверлей: вкл" : "Оверлей", "\uE7F4", Overlay.Visible ? "скрыть игровой оверлей" : "показать игровой оверлей",
                    () => { Overlay.Toggle(); Refresh("overlay"); }, true);
            case "apply":
                return new WheelCmd("Применить изменения", "Применить", "\uE74E", main.PendingCount > 0 ? $"ждут: {main.PendingCount}" : "нет изменений", main.ReviewPending);
            case "hub": return new WheelCmd("Игровой центр", "Игр. центр", "\uE7FC", "игры, пинг, готовность", () => main.OpenPage(hub));
            case "diag": return new WheelCmd("Диагностика", "Диагностика", "\uE9D9", "загрузка, сбои, диски", () => main.OpenPage("Диагностика"));
            case "settings": return new WheelCmd("Настройки WinTweak", "Настройки", "\uE713", "оформление, трей, пульт", () => main.OpenPage("Настройки"));
            case "taskmgr":
                return new WheelCmd("Диспетчер задач", "Диспетчер", "\uE9D9", "кто грузит систему",
                    () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); } catch { } });
            case "lock": return new WheelCmd("Заблокировать ПК", "Блок", "\uE72E", "как Win+L", () => LockWorkStation());
            case "explorer": return new WheelCmd("Перезапустить Проводник", "Проводник", "\uE8B7", "если зависла панель задач", () => Task.Run(Catalog.RestartExplorer));
            case "report":
                var last = Matches.Load().FirstOrDefault();
                return new WheelCmd("Последний отчёт", "Отчёт", "\uE9D9", last != null ? $"{last.Game} · {last.Avg:0} мс" : "отчётов пока нет",
                    () => { if (last != null) main.OpenReport(last); });
            case "refresh": return new WheelCmd("Частота монитора", "Герцы", "\uE7F4", "проверить и включить максимум", () => main.OpenPage(hub));
            default: return new WheelCmd("—", "", "", "пусто", () => { }, true);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool LockWorkStation();

    /// <summary>Обновить подпись сектора после действия (если он есть на кольце).</summary>
    void Refresh(string id)
    {
        int i = Array.IndexOf(ids, id);
        if (i >= 0) defaults[i] = Make(id);
        dirty = true;
    }

    /// <summary>Всё, что можно найти: действия пульта, разделы, игры, настройки.</summary>
    List<WheelCmd> BuildIndex()
    {
        var list = WheelSettings.Actions.Where(a => a.Id != "games").Select(a => Make(a.Id)).ToList();
        list.Add(new WheelCmd("Пинг до серверов", "Серверы", "", "Valve, AWS, свой адрес", () => main.OpenPage(MainForm.GamesPageName), Rank: 1));
        list.Add(new WheelCmd("Трассировка", "Трасса", "", "путь до сервера", () => main.OpenPage(MainForm.GamesPageName), Rank: 1));
        list.Add(new WheelCmd("Перезапуск видеокарты", "Видеокарта", "", "в игровом центре", () => main.OpenPage(MainForm.GamesPageName), Rank: 1));
        list.Add(new WheelCmd("Кэш шейдеров", "Шейдеры", "", "в игровом центре", () => main.OpenPage(MainForm.GamesPageName), Rank: 1));
        foreach (var (cat, glyph) in MainForm.Pages)
            list.Add(new WheelCmd(cat, cat, glyph, "раздел", () => main.OpenPage(cat), Rank: 1));
        foreach (var g in GameLibrary.Games.Where(g => g.Exe != null))
        {
            var game = g;
            list.Add(new WheelCmd(g.Title, g.Title, "", "запустить · " + g.Source, () => GameLibrary.Launch(game), Rank: 2));
        }
        foreach (var t in main.AllTweaks.Where(t => t.Category != StartupManager.Cat && t.Category != Programs.Cat))
        {
            var tw = t;
            list.Add(new WheelCmd(t.Name, t.Name, "", t.Category, () => main.OpenTweak(tw)));
        }
        return list;
    }

    static readonly string En = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
    static readonly string Ru = "йцукенгшщзхъфывапролджэячсмитьбюё";

    static string Swap(string s) => new(s.Select(ch =>
    {
        int i = En.IndexOf(char.ToLowerInvariant(ch));
        if (i >= 0) return Ru[i];
        i = Ru.IndexOf(char.ToLowerInvariant(ch));
        return i >= 0 ? En[i] : ch;
    }).ToArray());

    List<WheelCmd> Find(string q)
    {
        q = q.Trim().ToLowerInvariant();
        if (q == "") return new();
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int Score(WheelCmd c)
        {
            string t = c.Title.ToLowerInvariant(), all = t + " " + c.Sub.ToLowerInvariant();
            if (!words.All(w => all.Contains(w))) return -1;
            int s = t.StartsWith(q) ? 30 : t.Split(' ', '«', '(').Any(w => w.StartsWith(words[0])) ? 20 : t.Contains(q) ? 10 : 5;
            return s + c.Rank * 4;
        }
        return all.Select(c => (c, s: Score(c))).Where(x => x.s >= 0)
            .OrderByDescending(x => x.s).ThenBy(x => x.c.Title.Length).Take(8).Select(x => x.c).ToList();
    }

    void DoSearch()
    {
        results = Find(query);
        if (results.Count == 0 && query.Length > 0) results = Find(Swap(query));
        keySel = results.Count > 0 ? 0 : -1;
        ringWanted = false;
        dirty = true;
    }

    WheelCmd? At(int i) => Search ? (i >= 0 && i < results.Count ? results[i] : null) : i >= 0 && i < 8 ? defaults[i] : null;

    void Execute(WheelCmd? c)
    {
        if (c == null) return;
        if (c.KeepOpen) { c.Run(); dirty = true; return; }
        CloseAnimated();
        try { c.Run(); } catch { }
    }

    void Message(string a, string? b = null, Color? col = null, double sec = 4)
    {
        msg1 = a;
        msg2 = b;
        msgColor = col ?? Theme.Text;
        msgUntil = Motion.Now + sec * 1000;
        dirty = true;
    }

    async void ToggleMode()
    {
        bool on = !GameMode.Active;
        Message(on ? "Включаю…" : "Выключаю…", null, Theme.Muted, 30);
        try
        {
            if (on) await Task.Run(() => GameMode.Enter("включён вручную"));
            else await Task.Run(() => GameMode.Exit(GameMode.Reason ?? "Вручную"));
        }
        catch { }
        if (IsDisposed) return;
        Refresh("mode");
        Message(on ? "Режим включён" : "Режим выключен", on ? "без уведомлений и обновлений" : "всё вернулось как было", on ? Theme.Accent : Theme.Text);
    }

    async void RunPing()
    {
        Message("Пингую…", GameMode.Target, Theme.Muted, 30);
        var times = new List<double>();
        int lost = 0;
        try
        {
            var ip = await GameNet.Resolve(GameMode.Target);
            for (int i = 0; i < 6 && ip != null; i++)
            {
                if (await GameNet.IcmpPing(ip, 1000) is double v) times.Add(v); else lost++;
                await Task.Delay(120);
            }
        }
        catch { }
        if (IsDisposed) return;
        if (times.Count == 0) { Message("Нет ответа", GameMode.Target, Theme.Bad); return; }
        double avg = times.Average();
        Message($"{avg:0} мс", $"±{GameNet.Jitter(times):0} мс · потерь {lost} из 6", GameUI.PingColor(avg), 6);
    }

    async void FlushDns()
    {
        Message("Очищаю…", null, Theme.Muted, 30);
        try { await Task.Run(() => Shell.Run("ipconfig.exe", "/flushdns")); } catch { }
        if (!IsDisposed) Message("Готово", "кэш DNS очищен", Theme.Good);
    }

    async void RunReady()
    {
        Message("Проверяю…", "питание, нагрузка, пинг, драйвер", Theme.Muted, 60);
        List<Readiness.Check> list;
        try { list = await Task.Run(() => Readiness.Run(main.AllTweaks)); }
        catch { if (!IsDisposed) Message("Не вышло", null, Theme.Bad); return; }
        if (IsDisposed) return;
        var issues = list.Where(c => c.Level is Readiness.Level.Bad or Readiness.Level.Warn).ToList();
        if (issues.Count == 0) { Message("Готов к игре", $"проверено {list.Count} пунктов", Theme.Good, 6); return; }
        var first = issues[0];
        Message(issues.Any(c => c.Level == Readiness.Level.Bad) ? "Есть проблемы" : "Почти готов", $"замечаний: {issues.Count}",
            issues.Any(c => c.Level == Readiness.Level.Bad) ? Theme.Bad : Theme.Warn, 8);
        hint = new Hints.Hint($"{first.Title}: {first.Detail}", first.Level, "", m => m.OpenPage(MainForm.GamesPageName));
    }

    // ───────── Ввод ─────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        mouse = new Point((int)(e.X / k), (int)(e.Y / k));
        UpdateHover();
        base.OnMouseMove(e);
    }

    void UpdateHover()
    {
        float dx = mouse.X - C, dy = mouse.Y - C;
        double d = Math.Sqrt(dx * dx + dy * dy);
        double a = (Math.Atan2(dy, dx) * 180 / Math.PI + 90 + 360) % 360;
        bool p = pill.Contains(mouse);
        int sec = -1, game = -1;
        if (!p)
        {
            if (!Search && ringP > 0.5 && d >= Gi - 8 && SlotCount > 0)
            {
                double step = 360.0 / SlotCount;
                game = (int)(((a + step / 2) % 360) / step) % SlotCount;
                sec = gamesIdx;
            }
            else if (d >= R0 + 2) sec = (int)(((a + 22.5) % 360) / 45) % 8;
        }
        if (sec != hover) { hover = sec; hoverSince = Motion.Now; keySel = -1; }
        hoverGame = game;
        hoverPill = p;
        hoverCenter = !p && d < R0 + 2;
        Cursor = hoverPill || hoverGame >= 0 || (hover >= 0 && At(hover) != null) ? Cursors.Hand : Cursors.Default;
        dirty = true;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        mouse = new Point(-1000, -1000);
        hover = hoverGame = -1;
        hoverPill = hoverCenter = false;
        dirty = true;
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right) { CloseAnimated(); return; }
        if (e.Button != MouseButtons.Left) return;
        if (hoverPill)
        {
            if (hint.Act != null) { CloseAnimated(); try { hint.Act(main); } catch { } }
            return;
        }
        if (hoverGame >= 0 && hoverGame < games.Count)
        {
            var g = games[hoverGame];
            CloseAnimated();
            try { GameLibrary.Launch(g); } catch (Exception ex) { Toast.Pop(main, "Не удалось запустить: " + ex.Message, Tone.Error); }
            return;
        }
        if (hoverCenter) { CloseAnimated(); return; }
        if (hover >= 0) Execute(At(hover));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
                if (Search) { query = ""; DoSearch(); } else CloseAnimated();
                return true;
            case Keys.Enter:
                Execute(At(keySel >= 0 ? keySel : hover >= 0 ? hover : Search ? 0 : -1));
                return true;
            case Keys.Back:
                if (query.Length > 0) { query = query[..^1]; DoSearch(); }
                return true;
            case Keys.Left or Keys.Up:
                Step(-1);
                return true;
            case Keys.Right or Keys.Down or Keys.Tab:
                Step(1);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void Step(int d)
    {
        int cur = keySel >= 0 ? keySel : hover >= 0 ? hover : -1;
        int n = Search ? Math.Max(1, results.Count) : 8;
        keySel = cur < 0 ? (d > 0 ? 0 : n - 1) : ((cur + d) % n + n) % n;
        dirty = true;
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (char.IsControl(e.KeyChar) || query.Length > 40) return;
        if (e.KeyChar == ' ' && query.Length == 0) return;
        query += e.KeyChar;
        DoSearch();
        e.Handled = true;
    }

    // ───────── Анимация ─────────

    int Selected => keySel >= 0 ? keySel : hover;

    void Tick()
    {
        double now = Motion.Now;
        bool anim = false;

        if (closeStart >= 0)
        {
            double p = Math.Min(1, (now - closeStart) / 140);
            Alpha = (byte)(255 * (1 - Motion.InOut(p)));
            Present();
            if (p >= 1) Close();
            return;
        }

        double open = Motion.Reduced ? 1 : Math.Min(1, (now - openStart) / 420);
        if (open < 1) anim = true;
        byte a = (byte)(255 * Math.Min(1, open * 3));
        if (a != Alpha) { Alpha = a; anim = true; }

        // кольцо игр: открывается, если задержаться на «Играх»; закрывается, если уйти к другим секторам
        bool onGames = !Search && gamesIdx >= 0 && (hoverGame >= 0 || Selected == gamesIdx);
        if (onGames)
        {
            if (hover == gamesIdx && now - hoverSince > 160) ringWanted = true;
            if (keySel == gamesIdx) ringWanted = true;
            leaveSince = now;
        }
        else if (ringWanted && !hoverPill && now - leaveSince > 220 && (hover >= 0 && hover != gamesIdx || keySel >= 0 && keySel != gamesIdx)) ringWanted = false;
        double rt = ringWanted ? 1 : 0;
        if (Math.Abs(ringP - rt) > 0.002) { ringP += (rt - ringP) * (Motion.Reduced ? 1 : 0.2); anim = true; }
        else ringP = rt;

        for (int i = 0; i < 8; i++)
        {
            double t = i == Selected && At(i) != null && hoverGame < 0 ? 1 : i == gamesIdx && ringP > 0.5 ? 0.6 : 0;
            if (Math.Abs(hl[i] - t) > 0.004) { hl[i] += (t - hl[i]) * (Motion.Reduced ? 1 : 0.24); anim = true; }
            else hl[i] = t;
        }
        if (msg1 != null && now > msgUntil) { msg1 = msg2 = null; dirty = true; }

        if (anim || dirty)
        {
            dirty = false;
            Present();
        }
    }

    // ───────── Рисование ─────────

    static GraphicsPath Annulus(float r1, float r2, float a0, float sweep)
    {
        var p = new GraphicsPath();
        p.AddArc(C - r2, C - r2, r2 * 2, r2 * 2, a0, sweep);
        p.AddArc(C - r1, C - r1, r1 * 2, r1 * 2, a0 + sweep, -sweep);
        p.CloseFigure();
        return p;
    }

    static PointF Polar(double deg, double r) => new((float)(C + Math.Cos(deg * Math.PI / 180) * r), (float)(C + Math.Sin(deg * Math.PI / 180) * r));

    protected override void Draw(Graphics g)
    {
        g.ScaleTransform(k, k);
        double now = Motion.Now;
        double open = Motion.Reduced ? 1 : Math.Min(1, (now - openStart) / 420);

        // почти невидимая подложка — чтобы окно получало мышь и между секторами
        float catchR = Go + 16;
        using (var b = new SolidBrush(Color.FromArgb(1, 0, 0, 0))) g.FillEllipse(b, C - catchR, C - catchR, catchR * 2, catchR * 2);

        if (ringP > 0.01) DrawGames(g);

        for (int i = 0; i < 8; i++)
        {
            double pi = Motion.Reduced ? 1 : Motion.ExpoOut(Math.Clamp((now - openStart - i * 24) / 300, 0, 1));
            DrawSector(g, i, pi);
        }
        DrawCenter(g, Motion.ExpoOut(open));
        if (showHint) DrawPill(g, Motion.ExpoOut(Math.Clamp((now - openStart - 160) / 300, 0, 1)));
        else pill = RectangleF.Empty;
    }

    void DrawSector(Graphics g, int i, double pi)
    {
        if (pi <= 0.01) return;
        var cmd = At(i);
        double h = hl[i];
        float swirl = (float)((1 - pi) * -24);
        float a0 = -90 - 22.5f + i * 45 + 1.4f + swirl, sweep = 45 - 2.8f;
        float rOut = (float)(Ri + (Ro - Ri) * (0.55 + 0.45 * pi) + 7 * h);
        float rIn = (float)(Ri + 3 * h);
        int alpha = (int)(255 * pi);
        using var path = Annulus(rIn, rOut, a0, sweep);

        if (h > 0.02)
            for (int k = 3; k >= 1; k--)
            {
                using var glow = new Pen(Color.FromArgb((int)(24 * h * pi), Theme.Accent), k * 5f) { LineJoin = LineJoin.Round };
                g.DrawPath(glow, path);
            }
        using (var b = new SolidBrush(Color.FromArgb((int)(232 * pi), 19, 20, 28))) g.FillPath(b, path);
        if (h > 0.01)
        {
            var mid = Polar(a0 + sweep / 2, rOut);
            using var lg = new LinearGradientBrush(new PointF(C, C), mid.X == C && mid.Y == C ? new PointF(C + 1, C) : mid,
                Color.FromArgb((int)(90 * h * pi), Theme.Accent), Color.FromArgb((int)(210 * h * pi), Theme.AccentHi));
            g.FillPath(lg, path);
        }
        bool active = !Search && i == 0 && GameMode.Active;
        using (var pen = new Pen(active ? Color.FromArgb((int)(200 * pi), Theme.Accent) : Color.FromArgb((int)((36 + 60 * h) * pi), 255, 255, 255), active ? 1.6f : 1f))
            g.DrawPath(pen, path);

        if (cmd == null) return;
        double mida = a0 + sweep / 2;
        float rm = (rIn + rOut) / 2;
        var c = Polar(mida, rm);
        var fg = Color.FromArgb(alpha, Theme.Lerp(Color.FromArgb(200, 203, 216), Color.White, h));
        var ic = Color.FromArgb(alpha, h > 0.3 || active ? Color.White : Theme.Lerp(Theme.Accent, Color.White, 0.15));
        Txt(g, cmd.Glyph, IconF, ic, new RectangleF(c.X - 20, c.Y - 24, 40, 24));
        Txt(g, cmd.Short, LabelF, fg, new RectangleF(c.X - 40, c.Y + 1, 80, Search ? 28 : 16), wrap: Search);
    }

    void DrawCenter(Graphics g, double p)
    {
        float r = (float)(R0 * (0.7 + 0.3 * p));
        var disc = new RectangleF(C - r, C - r, r * 2, r * 2);
        Theme.Glow(g, Theme.Accent, disc, r, 3, 3f, (int)(46 * p));
        using (var b = new SolidBrush(Color.FromArgb((int)(245 * p), 13, 14, 20))) g.FillEllipse(b, disc);
        using (var pen = new Pen(Color.FromArgb((int)(60 * p), 255, 255, 255))) g.DrawEllipse(pen, disc);

        var s = Sampler.Current;
        Arc(g, r - 6, 4f, s.Ram / 100, Theme.Accent, p);
        Arc(g, r - 12, 2.5f, s.Cpu / 100, Color.FromArgb(120, 170, 255), p);

        int a = (int)(255 * p);
        var box = new RectangleF(C - r + 14, C - r + 14, r * 2 - 28, r * 2 - 28);
        var muted = Color.FromArgb(a, Theme.Muted);
        if (msg1 != null)
        {
            Txt(g, msg1, msg1.Length > 9 ? MidF : BigF, Color.FromArgb(a, msgColor), new RectangleF(box.X - 6, C - 26, box.Width + 12, 30));
            if (msg2 != null) Txt(g, msg2, SmallF, muted, new RectangleF(box.X - 4, C + 4, box.Width + 8, 28), StringAlignment.Center, StringAlignment.Near, true);
            return;
        }
        if (hoverGame >= 0 && hoverGame < games.Count)
        {
            Txt(g, games[hoverGame].Title, MidF, Color.FromArgb(a, Theme.Text), new RectangleF(box.X - 6, C - 30, box.Width + 12, 40), wrap: true);
            Txt(g, "запустить", SmallF, Color.FromArgb(a, Theme.Accent), new RectangleF(box.X, C + 12, box.Width, 16));
            return;
        }
        if (Search)
        {
            Txt(g, "› " + query, MidF, Color.FromArgb(a, Theme.Text), new RectangleF(box.X - 8, C - 26, box.Width + 16, 22));
            var sel = At(Selected);
            string sub = results.Count == 0 ? "ничего не найдено" : sel != null ? sel.Title + (sel.Sub != "" ? " · " + sel.Sub : "") : $"найдено: {results.Count}";
            Txt(g, sub, SmallF, muted, new RectangleF(box.X - 6, C - 2, box.Width + 12, 30), StringAlignment.Center, StringAlignment.Near, true);
            return;
        }
        var cmd = At(Selected);
        if (cmd != null)
        {
            Txt(g, cmd.Title, MidF, Color.FromArgb(a, Theme.Text), new RectangleF(box.X - 8, C - 26, box.Width + 16, 22));
            Txt(g, cmd.Sub, SmallF, muted, new RectangleF(box.X - 6, C - 2, box.Width + 12, 30), StringAlignment.Center, StringAlignment.Near, true);
            return;
        }
        Txt(g, $"{s.Ram:0}%", BigF, Color.FromArgb(a, Theme.Text), new RectangleF(box.X - 6, C - 26, box.Width + 12, 30));
        Txt(g, "ОЗУ", SmallF, muted, new RectangleF(box.X, C + 2, box.Width, 14));
        string line = $"CPU {s.Cpu:0}%" + (s.PingMs is int pm ? $" · {pm} мс" : "");
        Txt(g, line, SmallF, muted, new RectangleF(box.X - 8, C + 15, box.Width + 16, 14));
    }

    static void Arc(Graphics g, float r, float th, double v, Color c, double p)
    {
        var rect = new RectangleF(C - r, C - r, r * 2, r * 2);
        using (var track = new Pen(Color.FromArgb((int)(22 * p), 255, 255, 255), th)) g.DrawEllipse(track, rect);
        float sweep = (float)(360 * Math.Clamp(v, 0, 1) * p);
        if (sweep < 1) return;
        using var pen = new Pen(Color.FromArgb((int)(255 * p), c), th) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, rect, -90, sweep);
    }

    void DrawGames(Graphics g)
    {
        double p = Motion.ExpoOut(Math.Clamp(ringP, 0, 1));
        float go = (float)(Ro + 8 + (Go - Ro - 8) * p), gi = (float)(Ro + 6 + (Gi - Ro - 6) * p);
        using (var band = Annulus(gi, go, -90, 359.9f))
        {
            using var b = new SolidBrush(Color.FromArgb((int)(205 * p), 15, 16, 22));
            g.FillPath(b, band);
            using var pen = new Pen(Color.FromArgb((int)(40 * p), 255, 255, 255));
            g.DrawPath(pen, band);
        }
        int n = SlotCount;
        if (n == 0)
        {
            Txt(g, GameLibrary.Scanned ? "Игры не найдены — добавьте их в «Игровом центре»" : "Ищу игры…", SmallF,
                Color.FromArgb((int)(255 * p), Theme.Muted), new RectangleF(C - 120, C - (gi + go) / 2 - 10, 240, 20));
            return;
        }
        double step = 360.0 / n;
        float rg = (gi + go) / 2;
        for (int k = 0; k < n; k++)
        {
            double pk = Math.Clamp(ringP * 1.7 - k * 0.07, 0, 1);
            if (pk <= 0.01) continue;
            bool hot = k == hoverGame;
            var c = Polar(-90 + k * step, rg);
            float sz = (float)((hot ? 64 : 54) * Motion.ExpoOut(pk));
            var rect = new RectangleF(c.X - sz / 2, c.Y - sz / 2, sz, sz);
            if (hot) Theme.Glow(g, Theme.Accent, rect, sz / 2, 3, 3f, 90);
            var img = Slot(games[k]);
            var st = g.Save();
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            using (var ia = new ImageAttributes())
            {
                var cm = new ColorMatrix { Matrix33 = (float)pk };
                ia.SetColorMatrix(cm);
                g.DrawImage(img, Rectangle.Round(rect), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
            }
            g.Restore(st);
            using var pen = new Pen(hot ? Theme.Accent : Color.FromArgb((int)(70 * pk), 255, 255, 255), hot ? 2f : 1f);
            g.DrawEllipse(pen, rect);
            if (GameWatcher.Current?.Exe.Equals(games[k].Exe, StringComparison.OrdinalIgnoreCase) == true)
                Theme.Fill(g, Theme.Warn, new RectangleF(rect.Right - 12, rect.Top + 2, 10, 10), 5);
        }
    }

    /// <summary>Круглая «монета» игры: центр обложки или значок exe (кэшируется).</summary>
    static Bitmap Slot(InstalledGame game)
    {
        string key = (game.Exe ?? game.Dir) + (game.Cover != null ? "#c" : "");
        if (slotCache.TryGetValue(key, out var bmp)) return bmp;
        const int D = 72;
        bmp = new Bitmap(D, D, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var clip = new GraphicsPath();
            clip.AddEllipse(0, 0, D - 1, D - 1);
            g.SetClip(clip);
            if (game.Cover != null)
            {
                int side = game.Cover.Height;
                g.DrawImage(game.Cover, new Rectangle(0, 0, D, D), new Rectangle((game.Cover.Width - side) / 2, 0, side, side), GraphicsUnit.Pixel);
            }
            else
            {
                using (var lg = new LinearGradientBrush(new Rectangle(0, 0, D, D), Theme.Lerp(Theme.Card, Theme.Accent, 0.4), Theme.Lerp(Theme.Bg, Theme.Accent, 0.1), 45f))
                    g.FillRectangle(lg, 0, 0, D, D);
                if (game.Icon != null) g.DrawImage(game.Icon, D / 2 - 18, D / 2 - 18, 36, 36);
            }
        }
        slotCache[key] = bmp;
        return bmp;
    }

    void DrawPill(Graphics g, double p)
    {
        if (p <= 0.01) { pill = RectangleF.Empty; return; }
        double rp = Motion.ExpoOut(Math.Clamp(ringP, 0, 1));
        float cy = (float)(C + Ro + 34 + (Go - Ro) * rp);
        string text = hint.Text;
        var sz = g.MeasureString(text, PillF);
        float w = Math.Min(380, sz.Width + 58), h = 34;
        pill = new RectangleF(C - w / 2, cy - h / 2 + (float)(10 * (1 - p)), w, h);
        var col = Hints.Tint(hint.Level);
        int a = (int)(255 * p);
        if (hoverPill && hint.Act != null) Theme.Glow(g, col, pill, h / 2, 3, 2.5f, 70);
        using (var path = Theme.Round(pill, h / 2))
        {
            using var b = new SolidBrush(Color.FromArgb((int)(238 * p), 17, 18, 25));
            g.FillPath(b, path);
            using var pen = new Pen(Color.FromArgb((int)((hoverPill ? 160 : 70) * p), col));
            g.DrawPath(pen, path);
        }
        var dot = new RectangleF(pill.X + 14, pill.Y + h / 2 - 5, 10, 10);
        using (var db = new SolidBrush(Color.FromArgb(a, col))) g.FillEllipse(db, dot);
        float tx = dot.Right + 8, tw = pill.Right - tx - (hint.Act != null ? 26 : 14);
        Txt(g, text, PillF, Color.FromArgb(a, Theme.Text), new RectangleF(tx, pill.Y, tw, h), StringAlignment.Near);
        if (hint.Act != null)
            Txt(g, "", IconS, Color.FromArgb(a, hoverPill ? col : Theme.Muted), new RectangleF(pill.Right - 26, pill.Y, 18, h));
    }
}
