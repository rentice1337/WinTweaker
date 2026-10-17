using System.Runtime.InteropServices;

namespace WinTweaker;

public partial class MainForm : Form
{
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);

    const string Home = "Главная";
    const string History = "История";
    const string Diagnostics = "Диагностика";
    const string SettingsPage = "Настройки";

    // Боковое меню: 6 крупных разделов (+ «Настройки» внизу), внутри — вкладки.
    // Вкладка = категория (названия совпадают с Category в Catalog.cs), Label — подпись на вкладке.
    static readonly (string Name, string Glyph, (string Cat, string Glyph, string Label)[] Tabs)[] Sections =
    {
        (Home, "", new[] { (Home, "", Home) }),
        ("Игры", "", new[] { (GamesHub, "", GamesHub), ("Игры", "", "Настройки для игр") }),
        ("Система", "", new[]
        {
            ("Конфиденциальность", "", "Конфиденциальность"),
            ("Интерфейс", "", "Интерфейс"),
            ("Панель задач", "", "Панель задач"),
            ("Проводник", "", "Проводник"),
            ("Контекстное меню", "", "Контекстное меню"),
            ("Производительность", "", "Производительность"),
            ("Безопасность", "", "Безопасность"),
            ("Мышь и клавиатура", "", "Мышь и клавиатура"),
            ("Звук и уведомления", "", "Звук и уведомления"),
            ("Обновления", "", "Обновления"),
        }),
        ("Программы", "", new[]
        {
            (Programs.Cat, "", "Установка"),
            ("Приложения", "", "Приложения Windows"),
            (StartupManager.Cat, "", "Автозагрузка"),
            ("Службы", "", "Службы"),
        }),
        ("Сеть", "", new[] { ("Сеть", "", "Сеть"), ("Обход блокировок", "", "Обход блокировок") }),
        ("Инструменты", "", new[] { (Diagnostics, "", Diagnostics), ("Обслуживание", "", "Обслуживание"), (History, "", History) }),
        (SettingsPage, "", new[] { (SettingsPage, "", SettingsPage) }),
    };

    /// <summary>Все страницы (категории) с иконками — для палитры, пульта и сводки.</summary>
    static IEnumerable<(string Cat, string Glyph)> AllPages => Sections.SelectMany(s => s.Tabs.Select(t => (t.Cat, t.Glyph)));

    static (string Name, string Glyph, (string Cat, string Glyph, string Label)[] Tabs) SectionOf(string cat)
    {
        foreach (var s in Sections) if (s.Tabs.Any(t => t.Cat == cat)) return s;
        return Sections[0];
    }

    /// <summary>Последняя открытая вкладка каждого раздела.</summary>
    readonly Dictionary<string, string> lastTab = new();

    static string GlyphOf(string cat) =>
        AllPages.FirstOrDefault(i => i.Cat == cat).Glyph ?? "";

    readonly List<Tweak> tweaks = Catalog.All();
    readonly Dictionary<Tweak, bool> current = new();
    readonly Dictionary<Tweak, bool> desired = new();
    readonly Dictionary<Tweak, int> curVal = new();
    readonly Dictionary<Tweak, int> desVal = new();
    readonly List<NavItem> navItems = new();
    NavItem? settingsItem;
    TabStrip? tabStrip;
    readonly NavHost nav = new();
    readonly Viewport view = new();
    readonly UserBadge badge = new() { User = Environment.UserName };
    readonly Toggle restoreToggle = new() { Checked = true };
    readonly ProgressLine progress = new() { Dock = DockStyle.Bottom };
    readonly PaletteTrigger trigger = new();
    readonly FlatBtn applyBtn = new("\uE74E", "Применить") { Primary = true };
    readonly ToolTip tip = UI.MakeTip();
    readonly System.Windows.Forms.Timer liveTimer = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer cursorTimer = new() { Interval = 33 };
    List<Tweak> reverted = new();
    List<Diag.Boot>? bootCache;
    List<Diag.Crash>? crashCache;
    List<DnsBench.Result>? dnsCache;
    string? dnsCurrent;
    string selected = Home;
    bool busy, closing, loaded, hwDetected;

    readonly bool startHidden;
    Tray? tray;
    bool exiting, trayHintShown;

    public MainForm(bool startHidden = false)
    {
        this.startHidden = startHidden;
        Text = "WinTweaker";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1180, 860);
        MinimumSize = new Size(1040, 640);
        BackColor = Theme.Bg;
        Font = Theme.Body;
        DoubleBuffered = true;
        KeyPreview = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        int acc = int.TryParse(AppSettings.Get("accent", "0"), out var a) ? Math.Clamp(a, 0, Theme.Accents.Length - 1) : 0;
        Theme.SetAccent(Theme.Accents[acc].Color);

        // порядок важен для Dock: сначала то, что заполняет, потом края
        var main = new Control { Dock = DockStyle.Fill };
        view.Dock = DockStyle.Fill;
        main.Controls.Add(view);
        main.Controls.Add(BuildTopBar());
        // панель «Отмечено N изменений» поверх страницы, внизу по центру
        main.Controls.Add(pendingBar);
        pendingBar.BringToFront();
        main.Resize += (_, _) => pendingBar.Reposition();
        pendingBar.Apply.Click += async (_, _) => await ApplyAsync();
        pendingBar.Reset.Click += (_, _) => ResetPending();
        Controls.Add(main);
        Controls.Add(BuildSidebar());

        ApplyAppSettings();
        view.Resize += (_, _) => Relayout();
        liveTimer.Tick += (_, _) => LiveTick();
        cursorTimer.Tick += (_, _) =>
        {
            // окно спрятано или курсор далеко от него — подсветке нечего делать
            if (!Visible || WindowState == FormWindowState.Minimized) return;
            var c = Cursor.Position;
            var near = Bounds;
            near.Inflate(Spotlight.Reach, Spotlight.Reach);
            if (!near.Contains(c) && !Spotlight.AnyLit) return;
            Spotlight.Move(c);
        };
        ShowSkeleton();

        Sampler.Start();
        tray = new Tray(this);
        InitGames();
        Load += (_, _) => Glass.Attach(this);
        Shown += async (_, _) =>
        {
            cursorTimer.Start();
            await LoadStateAsync();
            await FirstRunAsync();
            await OfferRetiredDns();
            if (!GameLibrary.Scanned) _ = GameLibrary.ScanAsync(); // для автоопределения игр
            if (AppSettings.Get("updates", "1") == "1") _ = CheckUpdates(manual: false);
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WinCorners.Apply(this, Theme.Line); // Windows 11 — гладкое скругление; Windows 10 — прямые углы без лесенки
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.K:
            case Keys.Control | Keys.F:
                OpenPalette();
                return true;
            case Keys.Control | Keys.Enter:
                _ = ApplyAsync();
                return true;
            case Keys.F5:
                if (!busy) _ = LoadStateAsync();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void Drag(Control c) => c.MouseDown += (_, e) =>
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, 2, 0);
    };

    void FadeClose()
    {
        if (AppSettings.Get("tray", "1") == "1") HideToTray();
        else ExitApp();
    }

    // ───────── Трей ─────────

    /// <summary>Запуск с --tray: окно создаётся, но не показывается, пока не откроют из трея.</summary>
    protected override void SetVisibleCore(bool value)
    {
        if (startHidden && !IsHandleCreated)
        {
            CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    void HideToTray()
    {
        liveTimer.Stop();
        Hide();
        if (!trayHintShown && AppSettings.Get("trayHint", "0") == "0")
        {
            trayHintShown = true;
            AppSettings.Set("trayHint", "1");
            tray?.Balloon("WinTweak работает в трее", "Клик по значку или Ctrl+Alt+W — круговой пульт. Выход — правый клик по значку.");
        }
    }

    public void ShowFromTray()
    {
        if (exiting) return;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        if (selected == Home && loaded) liveTimer.Start();
    }

    public void ExitApp()
    {
        if (exiting) return;
        exiting = true;
        if (GameMode.Active) GameMode.Exit(GameMode.Reason ?? ""); // вернуть уведомления, питание, службы
        tray?.Dispose();
        tray = null;
        if (!Visible) { Close(); Application.Exit(); return; }
        if (closing) return;
        closing = true;
        Motion.Run(180, p => Opacity = 1 - p, Motion.InOut, 0, () => { Close(); Application.Exit(); });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 и закрытие с панели задач тоже сворачивают в трей; выход — из трея
        if (!exiting && e.CloseReason == CloseReason.UserClosing && AppSettings.Get("tray", "1") == "1")
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        if (!exiting)
        {
            exiting = true;
            if (GameMode.Active) GameMode.Exit(GameMode.Reason ?? "");
            tray?.Dispose();
        }
        base.OnFormClosing(e);
    }

    // ───────── Каркас окна ─────────

    Control BuildSidebar()
    {
        var side = new SidePanel { Dock = DockStyle.Left, Width = 236 };
        Drag(side);
        Drag(nav.Canvas);

        var nc = nav.Nav;
        int y = 6;
        foreach (var sec in Sections)
        {
            string name = sec.Name;
            var first = sec.Tabs[0].Cat;
            var n = new NavItem(sec.Glyph, name) { Width = 214 };
            n.SetSelectedNow(sec.Tabs.Any(t => t.Cat == selected));
            n.Click += (_, _) => SelectCategory(lastTab.GetValueOrDefault(name) ?? first);
            navItems.Add(n);
            if (name == SettingsPage)
            {
                // «Настройки» — внизу меню, над плашкой пользователя
                settingsItem = n;
                side.Controls.Add(n);
                continue;
            }
            n.Location = new Point(10, y);
            nc.Controls.Add(n);
            if (n.Selected) nc.Ind.Set(y);
            y += NavCanvas.Step + 4;
        }
        int navHeight = y + 6;

        badge.Dock = DockStyle.Bottom;
        badge.Click += async (_, _) => { if (Pending() > 0) await ApplyAsync(); };
        tip.SetToolTip(badge, "Показать изменения, отмеченные к применению");
        badge.Height = 62;
        side.Controls.Add(nav);
        side.Controls.Add(badge);
        badge.Dock = DockStyle.None; // не перекрывать правую линию боковой панели
        side.Resize += (_, _) =>
        {
            badge.SetBounds(0, side.Height - badge.Height, side.Width - 1, badge.Height);
            settingsItem?.SetBounds(10, side.Height - badge.Height - NavCanvas.ItemH - 10, 214, NavCanvas.ItemH);
            nav.SetBounds(0, 76, side.Width - 1, Math.Max(80, side.Height - 76 - badge.Height - NavCanvas.ItemH - 20));
            nav.SetContentHeight(navHeight);
        };
        return side;
    }

    Control BuildTopBar()
    {
        var top = new TopPanel { Dock = DockStyle.Top, Height = 64, Label = "Точка восстановления" };
        Drag(top);
        top.Controls.Add(progress);

        var apply = applyBtn;
        apply.Click += async (_, _) => await ApplyAsync();
        var refresh = new FlatBtn("", "");
        refresh.Click += async (_, _) => { if (!busy) await LoadStateAsync(); };
        tip.SetToolTip(apply, "Применить отмеченные изменения (Ctrl+Enter)");
        tip.SetToolTip(refresh, "Перепроверить состояние системы (F5)");
        tip.SetToolTip(restoreToggle, "Создавать точку восстановления Windows перед рискованными изменениями:\n«для опытных», службы, удаление приложений. Остальное откатывается из «Истории».\nТребует включённой защиты системы на диске C:.");
        tip.SetToolTip(trigger, "Палитра команд — найти и переключить любую настройку (Ctrl+K)");


        int x = 18;
        apply.Location = new Point(x - apply.Pad, (top.Height - apply.Height) / 2);
        x += apply.Width - apply.Pad * 2 + 8;
        foreach (var c in new Control[] { refresh })
        {
            c.Location = new Point(x, 16);
            x += c.Width + 6;
        }
        restoreToggle.Location = new Point(x + 12, 19);
        top.LabelAt = new Point(restoreToggle.Right + 4, 22);
        int labelEnd = top.LabelAt.X + TextRenderer.MeasureText(top.Label, Theme.Body).Width + 18;
        top.Controls.AddRange(new Control[] { apply, refresh, restoreToggle });
        trigger.Click += (_, _) => OpenPalette();

        var close = new FlatBtn("", "") { Danger = true };
        close.Click += (_, _) => FadeClose();
        var min = new FlatBtn("", "");
        min.Click += (_, _) => WindowState = FormWindowState.Minimized;
        top.Controls.AddRange(new Control[] { close, min, trigger });
        top.Resize += (_, _) =>
        {
            close.Location = new Point(top.Width - close.Width - 12, 16);
            min.Location = new Point(close.Left - min.Width - 2, 16);
            int w = Math.Clamp(min.Left - 14 - labelEnd, 44, 260);
            trigger.SetBounds(min.Left - 14 - w, 15, w, 34);
        };
        return top;
    }

    void ToggleGlass()
    {
        bool on = AppSettings.Get("glass", "1") == "1";
        AppSettings.Set("glass", on ? "0" : "1");
        Glass.Rebuild();
        Invalidate(true);
        if (!on && !Glass.WallpaperOn)
            Toast.Pop(this, "В Windows выключены «Эффекты прозрачности» — обои под окном не показываются", Tone.Info);
    }

    // Плавная смена акцента: цвет перетекает 300 мс, затем пересобирается фон со свечением
    void ChangeAccent(int i)
    {
        var from = Theme.Accent;
        var to = Theme.Accents[i].Color;
        AppSettings.Set("accent", i.ToString());
        Motion.Run(300, p =>
        {
            Theme.SetAccent(Theme.Lerp(from, to, p));
            Invalidate(true);
        }, Motion.InOut, 0, () =>
        {
            Glass.Rebuild();
            Invalidate(true);
            Toast.Pop(this, "Акцент: " + Theme.Accents[i].Name, Tone.Info);
        });
    }

    // ───────── Навигация и раскладка ─────────

    void SelectCategory(string cat) => SelectCategory(cat, null);

    void SelectCategory(string cat, Tweak? focus)
    {
        if (cat == selected && focus == null && loaded) return;
        selected = cat;
        var sec = SectionOf(cat);
        lastTab[sec.Name] = cat;
        foreach (var n in navItems)
        {
            bool mine = n.Text == sec.Name;
            n.Selected = mine;
            if (mine && n.Parent == nav.Nav) { nav.Nav.Ind.To(n.Top, 420); nav.Reveal(n); }
        }
        // индикатор меню уезжает, если выбраны «Настройки» внизу
        if (sec.Name == SettingsPage) nav.Nav.Ind.To(-NavCanvas.ItemH * 2, 300);
        Build(focus);
    }

    void ClearContent()
    {
        var cv = view.Canvas;
        var old = cv.Controls.Cast<Control>().ToList();
        cv.Controls.Clear();
        old.ForEach(c => c.Dispose());
        view.Reset();
    }

    void ShowSkeleton()
    {
        view.Canvas.SuspendLayout();
        ClearContent();
        foreach (int rows in new[] { 5, 4, 3, 5 })
            view.Canvas.Controls.Add(new SkeletonCard(rows));
        view.Canvas.ResumeLayout();
        Relayout();
    }

    void Build(Tweak? focus = null)
    {
        liveTimer.Stop();
        var cv = view.Canvas;
        cv.SuspendLayout();
        ClearContent();
        switch (selected)
        {
            case Home: BuildDashboard(); break;
            case History: BuildHistory(); break;
            case Diagnostics: BuildDiagnostics(); break;
            case SettingsPage: BuildSettings(); break;
            case GamesHub: BuildGames(); break;
            default:
                var items = tweaks.Where(t => t.Category == selected).ToList();
                cv.Controls.Add(new Header(selected, Subtitle(items)));
                foreach (var card in MakeCards(items)) cv.Controls.Add(card);
                break;
        }
        AddTabs();
        cv.ResumeLayout();
        Relayout();
        RevealAll();
        if (focus != null) FocusRow(focus);
    }

    /// <summary>Вкладки раздела над заголовком страницы (если в разделе больше одной вкладки).</summary>
    void AddTabs()
    {
        tabStrip = null;
        var sec = SectionOf(selected);
        var cv = view.Canvas;
        var tab = sec.Tabs.FirstOrDefault(t => t.Cat == selected);
        if (cv.Controls.Count > 0 && cv.Controls[0] is Header h && tab.Label != null) h.Text = tab.Label;
        if (sec.Tabs.Length < 2) return;
        tabStrip = new TabStrip(sec.Name, sec.Tabs, selected, PendingIn);
        tabStrip.Picked += c => SelectCategory(c);
        cv.Controls.Add(tabStrip);
        cv.Controls.SetChildIndex(tabStrip, 0);
    }

    /// <summary>Сколько изменений в категории ждут «Применить».</summary>
    int PendingIn(string cat) => tweaks.Count(t => t.Category == cat &&
        (t.IsToggle ? desired.GetValueOrDefault(t) != current.GetValueOrDefault(t)
                    : t.IsValue && desVal.GetValueOrDefault(t) != curVal.GetValueOrDefault(t)));

    // каскадное появление: заголовок, затем карточки с шагом 40 мс (снимками — без перерисовки живых окон)
    void RevealAll()
    {
        view.Canvas.Reveal(view.Canvas.Controls.Cast<Control>().ToList(), 40);
    }

    // Раскладка: широкие блоки на всю ширину, остальные — в две колонки «кирпичиком»
    void Relayout()
    {
        const int pad = 22, gap = 14;
        int width = view.ClientSize.Width;
        if (width <= 0) return;
        int full = Math.Max(300, width - pad * 2);
        int w = Math.Max(260, (width - pad * 3) / 2);
        int[] y = { 14, 14 };
        foreach (Control c in view.Canvas.Controls)
        {
            Point p;
            if (c is FadeIn { Wide: true })
            {
                int top = Math.Max(y[0], y[1]);
                c.Width = full;
                p = new Point(pad, top);
                y[0] = y[1] = top + c.Height + (c is Header ? 0 : gap);
            }
            else
            {
                int col = y[0] <= y[1] ? 0 : 1;
                c.Width = w;
                if (c is Card card) card.LayoutRows();
                p = new Point(pad + col * (w + pad), y[col]);
                y[col] += c.Height + gap;
            }
            if (c is FadeIn f) f.Place(p); else c.Location = p;
        }
        view.SetContentHeight(Math.Max(y[0], y[1]) + pad + 72); // место под панель «Отмечено N изменений»
    }

    IEnumerable<TweakRow> Rows() =>
        view.Canvas.Controls.OfType<Card>().SelectMany(c => c.Controls.OfType<TweakRow>());

    /// <summary>Прокрутить к настройке и подсветить её вспышкой.</summary>
    void FocusRow(Tweak t)
    {
        var row = Rows().FirstOrDefault(r => r.T == t);
        if (row?.Parent is not Card card) return;
        view.ScrollTo(card.BasePos.Y + row.Top - view.Height / 3);
        Motion.Run(1, _ => { }, null, 260, () => { if (!row.IsDisposed) row.Flash(); });
    }

    void GoTo(Tweak t)
    {
        if (t.Category != selected) SelectCategory(t.Category, t);
        else FocusRow(t);
    }

    // ───────── Главная ─────────

    void BuildDashboard()
    {
        var cv = view.Canvas;
        int h = DateTime.Now.Hour;
        string hello = h < 5 ? "Доброй ночи" : h < 12 ? "Доброе утро" : h < 18 ? "Добрый день" : "Добрый вечер";
        cv.Controls.Add(new Header(Home, $"{hello}, {Environment.UserName} · Ctrl+K — поиск по всем настройкам"));

        if (reverted.Count > 0)
        {
            var banner = new WatchBanner(reverted);
            banner.Fix.Click += async (_, _) => await FixReverted();
            cv.Controls.Add(banner);
        }

        var presetItems = Presets.All.Select(p =>
        {
            var list = Presets.Resolve(p, tweaks);
            return (p,
                list.Count(t => current.GetValueOrDefault(t)),
                list.Count,
                list.Count(t => desired.GetValueOrDefault(t) && !current.GetValueOrDefault(t)));
        }).ToArray();
        var presets = new PresetsWidget(presetItems);
        presets.Picked += ApplyPreset;
        cv.Controls.Add(presets);

        var (score, recs) = Advisor.Analyze(tweaks, t => current.GetValueOrDefault(t), reverted);
        var scoreW = new ScoreWidget(score, recs);
        scoreW.RecClicked += OnRec;
        cv.Controls.Add(scoreW);

        var mem = SysInfo.Memory();
        cv.Controls.Add(new InfoWidget("Система",
            ("Windows", SysInfo.Os()),
            ("Сборка", SysInfo.Build()),
            ("Процессор", SysInfo.CpuName()),
            ("Видеокарта", SysInfo.Gpu()),
            ("Память", $"{mem.TotalGb:0.#} ГБ" + (Hardware.DiskKind != "" ? $" · диск {Hardware.DiskKind}" : "")),
            ("Работает", SysInfo.Uptime())));

        cv.Controls.Add(new LiveWidget());

        var cats = AllPages.Select(i => i.Cat)
            .Where(c => c is not (Home or History or Diagnostics or SettingsPage) && c != StartupManager.Cat && c != Programs.Cat)
            .Select(c =>
            {
                var list = tweaks.Where(t => t.Category == c && !t.IsAction).ToList();
                return (c, list.Count(t => current.GetValueOrDefault(t)), list.Count);
            })
            .Where(x => x.Item3 > 0).ToList();
        var prog = new ProgressWidget(cats);
        prog.CategoryClicked += SelectCategory;
        cv.Controls.Add(prog);

        cv.Controls.Add(new DisksWidget());

        var clean = new ActionTile("", "Очистить временные файлы", "%TEMP% и Windows\\Temp");
        clean.Click += async (_, _) =>
        {
            var t = tweaks.FirstOrDefault(x => x.Name == "Удалить временные файлы");
            if (t != null && !busy && await RunBusy("Очистка…", t.Apply)) Toast.Pop(this, "Временные файлы удалены");
        };
        var restore = new ActionTile("", "Точка восстановления", "Снимок системы сейчас");
        restore.Click += async (_, _) =>
        {
            if (!busy && await RunBusy("Создание точки восстановления…", CreateRestorePoint)) Toast.Pop(this, "Точка восстановления создана");
        };
        var explorer = new ActionTile("", "Перезапустить Проводник", "Если зависла панель задач");
        explorer.Click += async (_, _) =>
        {
            if (!busy && await RunBusy("Перезапуск Проводника…", Catalog.RestartExplorer)) Toast.Pop(this, "Проводник перезапущен");
        };
        var diag = new ActionTile("", "Диагностика", "Загрузка, сбои, тест DNS");
        diag.Click += (_, _) => SelectCategory(Diagnostics);
        var code = new ActionTile("", "Код профиля", "Скопировать для друга");
        code.Click += (_, _) => CopyCode();
        var pal = new ActionTile("", "Палитра команд", "Ctrl+K — найти что угодно");
        pal.Click += (_, _) => OpenPalette();
        cv.Controls.Add(new ActionsWidget("Быстрые действия", clean, restore, explorer, diag, code, pal));

        LiveTick();
        liveTimer.Start();
    }

    async Task FixReverted()
    {
        if (busy || reverted.Count == 0) return;
        foreach (var t in reverted) desired[t] = true;
        UpdatePending();
        await ApplyAsync();
    }

    void OnRec(Rec rec)
    {
        if (rec.Tweaks.Length > 0)
        {
            int marked = 0;
            foreach (var t in tweaks.Where(t => rec.Tweaks.Contains(t.Name) && !t.IsAction && !t.Risky))
                if (!desired.GetValueOrDefault(t)) { desired[t] = true; marked++; }
            if (marked == 0) { Toast.Pop(this, "Уже отмечено — нажмите «Применить»", Tone.Info); return; }
            UpdatePending();
            Build();
            Toast.Pop(this, $"Отмечено {marked} — проверьте и нажмите «Применить»");
        }
        else if (rec.Category != null) SelectCategory(rec.Category);
        else Dialog.Info(this, rec.Title, rec.Detail);
    }

    // Пресет только отмечает переключатели — применяет пользователь кнопкой «Применить»
    void ApplyPreset(Presets.Preset p)
    {
        if (busy) return;
        int marked = 0;
        foreach (var t in Presets.Resolve(p, tweaks).Where(t => !desired.GetValueOrDefault(t)))
        {
            desired[t] = true;
            marked++;
        }
        if (marked == 0)
        {
            Toast.Pop(this, $"«{p.Name}»: всё уже включено или отмечено", Tone.Info);
            return;
        }
        UpdatePending();
        Build();
        Toast.Pop(this, $"«{p.Name}»: отмечено {marked} — проверьте и нажмите «Применить»");
    }

    void LiveTick()
    {
        var live = view.Canvas.Controls.OfType<LiveWidget>().FirstOrDefault();
        if (live == null || !Visible) { liveTimer.Stop(); return; }
        live.Push(Sampler.Cpu, Sampler.Ram, $"{Sampler.RamUsedGb:0.0} из {Sampler.RamTotalGb:0.0} ГБ");
    }

    // ───────── Диагностика ─────────

    void BuildDiagnostics()
    {
        var cv = view.Canvas;
        cv.Controls.Add(new Header(Diagnostics, "по журналам Windows — без сторонних программ"));
        var boot = new BootWidget();
        cv.Controls.Add(boot);
        var crash = new CrashWidget();
        crash.CrashClicked += ShowCrash;
        cv.Controls.Add(crash);
        var dns = new DnsWidget();
        dns.Run.Click += async (_, _) => await RunDns(dns);
        dns.UseBest.Click += (_, _) => UseDns(dns.Best);
        if (dnsCache != null) dns.SetData(dnsCache, dnsCurrent);
        cv.Controls.Add(dns);
        _ = LoadDiagAsync(boot, crash);

        var health = new DiskHealthWidget();
        cv.Controls.Add(health);
        _ = LoadHealth(health);

        var space = new DiskSpaceWidget();
        space.Run.Click += async (_, _) => { space.Trail.Clear(); await ScanSpace(space, space.Root); };
        space.Back.Click += async (_, _) => { if (space.Trail.Count > 0) await ScanSpace(space, space.Trail.Pop(), push: false); };
        space.Open += async it => { space.Trail.Push(space.Root); await ScanSpace(space, it.Path, push: false); };
        space.Reveal += it => { try { System.Diagnostics.Process.Start("explorer.exe", it.IsDir && it.Name != "Файлы в этой папке" ? $"\"{it.Path}\"" : $"/select,\"{it.Path}\""); } catch { } };
        cv.Controls.Add(space);
    }

    async Task LoadHealth(DiskHealthWidget w)
    {
        var list = await Task.Run(() => { try { return DiskHealth.Read(); } catch { return new List<DiskHealth.Disk>(); } });
        if (w.IsDisposed) return;
        w.SetData(list);
        Relayout();
    }

    async Task ScanSpace(DiskSpaceWidget w, string root, bool push = true)
    {
        if (w.Busy) return;
        w.Begin(root);
        var list = await Task.Run(() => DiskSpace.Scan(root, n => BeginInvoke(() => { if (!w.IsDisposed) w.Progress(n); })));
        if (w.IsDisposed) return;
        w.SetData(list);
        Relayout();
    }

    async Task LoadDiagAsync(BootWidget boot, CrashWidget crash)
    {
        if (bootCache == null || crashCache == null)
        {
            var t1 = Task.Run(() => Diag.Boots());
            var t2 = Task.Run(() => Diag.Crashes());
            try { bootCache = await t1; } catch { bootCache = new(); }
            try { crashCache = await t2; } catch { crashCache = new(); }
        }
        if (boot.IsDisposed || crash.IsDisposed) return;
        var first = Journal.LoadAll().Where(s => !s.RolledBack).Select(s => (DateTime?)s.Date).Min();
        boot.SetData(bootCache, first);
        crash.SetData(crashCache);
        Relayout();
    }

    void ShowCrash(Diag.Crash c)
    {
        var (name, what, fix) = Diag.Explain(c);
        string text = $"{c.When:d MMMM yyyy, HH:mm}" + (c.Code != 0 ? $" · код 0x{c.Code:X8}" : "") +
                      $"\n\n{what}\n\nЧто сделать: {fix}" + (c.Dump != "" ? $"\n\nДамп памяти: {c.Dump}" : "");
        Dialog.Info(this, name, text, c.Code != 0 ? Tone.Error : Tone.Warning);
    }

    async Task RunDns(DnsWidget w)
    {
        if (w.Running) return;
        w.Begin();
        try
        {
            var res = await Task.Run(() => DnsBench.RunAsync((d, t) => BeginInvoke(() => { if (!w.IsDisposed) w.Progress(d, t); })));
            dnsCache = res;
            dnsCurrent = System.Text.RegularExpressions.Regex.Match(DnsCache.Current, @"\b(\d{1,3}\.){3}\d{1,3}\b").Value;
            if (!w.IsDisposed) { w.SetData(res, dnsCurrent); Relayout(); }
        }
        catch (Exception ex)
        {
            if (!w.IsDisposed) w.SetData(new List<DnsBench.Result>(), null);
            Dialog.Info(this, "Тест DNS не удался", ex.Message, Tone.Error);
        }
    }

    void UseDns(DnsBench.Result? best)
    {
        var t = tweaks.FirstOrDefault(x => x.Name == best?.Server.Tweak);
        if (t == null) return;
        foreach (var d in tweaks.Where(x => x.Category == "Обход блокировок" && (x.Group ?? "").StartsWith("DNS")))
            desired[d] = d == t;
        UpdatePending();
        Toast.Pop(this, $"Отмечен «{t.Name}» — нажмите «Применить»");
    }

    // ───────── История и откат ─────────

    void BuildHistory()
    {
        var cv = view.Canvas;
        var sessions = Journal.LoadAll();
        var open = sessions.Where(s => !s.RolledBack).ToList();
        cv.Controls.Add(new Header(History, sessions.Count == 0
            ? "изменений пока не было — здесь появится каждое нажатие «Применить»"
            : $"сеансов: {sessions.Count} · можно откатить: {open.Count}"));

        var all = new ActionTile("", "Откатить всё", open.Count > 0 ? $"Сеансов к откату: {open.Count}" : "Откатывать нечего");
        all.Click += async (_, _) =>
        {
            if (open.Count == 0) { Toast.Pop(this, "Откатывать нечего", Tone.Info); return; }
            await RollbackAsync(open);
        };
        var folder = new ActionTile("", "Папка истории", "Журналы в формате JSON");
        folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(Journal.Folder);
            System.Diagnostics.Process.Start("explorer.exe", Journal.Folder);
        };
        cv.Controls.Add(new ActionsWidget("Действия", all, folder) { Wide = true });

        var latest = open.FirstOrDefault(); // откатываем строго с конца — сеансы не конфликтуют
        foreach (var s in sessions.Take(30))
        {
            FlatBtn? btn = null;
            if (s == latest)
            {
                btn = new FlatBtn("", "Откатить этот сеанс");
                btn.Click += async (_, _) => await RollbackAsync(new List<Journal.Session> { s });
            }
            cv.Controls.Add(new SessionWidget(s, btn));
        }
    }

    async Task RollbackAsync(List<Journal.Session> sessions)
    {
        if (busy) return;
        var items = sessions.SelectMany(s => s.Items).ToList();
        var irreversible = items.Where(i => i.Irreversible).Select(i => i.Name).Distinct().ToList();
        string msg = $"Будет отменено изменений: {items.Count(i => !i.Irreversible)}.";
        if (irreversible.Count > 0)
            msg += "\n\nНельзя вернуть автоматически — установите из Microsoft Store:\n" + string.Join("\n", irreversible.Select(n => "• " + n));
        if (!Dialog.Confirm(this, "Откатить изменения?", msg, "Откатить", "Отмена", Tone.Warning)) return;

        var snapshot = tweaks.ToList();
        bool ok = await RunBusy("Откат…", () =>
        {
            foreach (var s in sessions) Journal.Rollback(s, snapshot); // от новых к старым
        });
        await LoadStateAsync();
        if (ok)
        {
            Toast.Pop(this, $"Откачено изменений: {items.Count(i => !i.Irreversible)}");
            if (items.Any(i => snapshot.FirstOrDefault(t => t.Name == i.Name)?.NeedsRestart == true))
                Toast.Pop(this, "Часть изменений вернётся после перезагрузки", Tone.Warning);
        }
    }

    // ───────── Карточки настроек ─────────

    string Subtitle(List<Tweak> items)
    {
        var real = items.Where(t => t.IsToggle).ToList();
        int values = items.Count(t => t.IsValue);
        int actions = items.Count(t => t.IsAction);
        string s = selected == Programs.Cat
            ? $"программ: {real.Count} · установлено: {real.Count(t => current.GetValueOrDefault(t))} · включить = установить через winget"
            : $"настроек: {real.Count + values} · включено: {real.Count(t => current.GetValueOrDefault(t))}";
        if (actions > 0) s += $" · действий: {actions}";
        if (items.Count == 0) s = "здесь пока пусто";
        return s;
    }

    IEnumerable<Card> MakeCards(IEnumerable<Tweak> items)
    {
        foreach (var g in items.GroupBy(t => t.Group ?? ""))
        {
            var list = g.ToList();
            int chunk = g.Key == "" ? 7 : 100; // без явных групп — карточки по 7 строк
            for (int i = 0; i < list.Count; i += chunk)
            {
                string title = g.Key != "" ? g.Key : i == 0 ? "Основное" : "Дополнительно";
                var card = new Card(title);
                foreach (var t in list.Skip(i).Take(chunk))
                    card.Controls.Add(MakeRow(t));
                yield return card;
            }
        }
    }

    TweakRow MakeRow(Tweak t)
    {
        var r = new TweakRow(t);
        string desc = Describe(t);
        tip.SetToolTip(r, desc);
        tip.SetToolTip(r.InfoBtn, "Что именно меняется");
        r.InfoBtn.Click += (_, _) => ShowDetails(t);

        if (r.FolderBtn != null)
        {
            tip.SetToolTip(r.FolderBtn, "Открыть расположение файла");
            r.FolderBtn.Click += (_, _) => { try { t.OpenFolder!(); } catch { } };
        }

        if (r.Seg != null || r.Slide != null)
        {
            int v = desVal.GetValueOrDefault(t);
            if (r.Seg != null) r.Seg.Value = v;
            if (r.Slide != null) r.Slide.Value = v;
            r.SetChangedNow(v != curVal.GetValueOrDefault(t));
            void OnValue(int nv)
            {
                desVal[t] = nv;
                r.Changed = nv != curVal.GetValueOrDefault(t);
                UpdatePending();
            }
            if (r.Seg != null) r.Seg.Changed += OnValue;
            if (r.Slide != null) r.Slide.Changed += OnValue;
        }
        else if (r.Toggle != null)
        {
            r.Toggle.Checked = desired.GetValueOrDefault(t);
            r.SetChangedNow(desired.GetValueOrDefault(t) != current.GetValueOrDefault(t));
            tip.SetToolTip(r.Toggle, desc);
            r.Toggle.CheckedChanged += (_, _) =>
            {
                // настройку без отката (например, Visual C++) выключить нельзя — возвращаем переключатель
                if (!r.Toggle.Checked && t.Revert == null && current.GetValueOrDefault(t))
                {
                    r.Toggle.Checked = true;
                    Toast.Pop(this, $"«{t.Name}» не выключается — это можно только включить", Tone.Info);
                    return;
                }
                desired[t] = r.Toggle.Checked;
                r.Changed = desired[t] != current.GetValueOrDefault(t);
                UpdatePending();
            };
        }
        else
        {
            tip.SetToolTip(r.RunBtn!, desc);
            r.RunBtn!.Click += async (_, _) => await RunAction(t);
        }
        return r;
    }

    async Task RunAction(Tweak t)
    {
        if (busy) return;
        if (t.Risky && !Dialog.Confirm(this, t.Name, t.Description + "\n\nВыполнить?", "Выполнить", "Отмена", Tone.Warning)) return;
        if (await RunBusy(t.Name + "…", t.Apply)) Toast.Pop(this, "Готово: " + t.Name);
    }

    void ShowDetails(Tweak t)
    {
        string text = t.Details ?? "Подробностей нет.\n\n" + t.Description;
        string sub = t.Category + (t.Group != null ? " · " + t.Group : "") + (t.Risky ? " · ⚠ рискованная" : "") + (t.NeedsRestart ? " · нужна перезагрузка" : "");
        DetailsDialog.Show(Form.ActiveForm ?? this, t.Name, sub, text);
    }

    static string Describe(Tweak t)
    {
        string s = t.Description == "" ? t.Name : t.Description;
        if (t.Risky) s += "\n\n⚠ Рискованная настройка.";
        if (!t.IsAction && t.Revert == null) s += "\n\nНельзя отменить программой.";
        if (t.NeedsRestart) s += "\n\nНужна перезагрузка.";
        return s;
    }

    // ───────── Палитра команд ─────────

    void OpenPalette()
    {
        if (!loaded || busy) return;
        if (OwnedForms.OfType<Palette>().Any()) return;
        var p = new Palette(this, PaletteItems());
        p.FormClosed += (_, _) => p.Dispose();
        p.Show(this);
    }

    List<PaletteItem> PaletteItems()
    {
        var list = new List<PaletteItem>();
        int order = 0;
        void Cmd(string glyph, string title, string sub, Action run) =>
            list.Add(new PaletteItem { Glyph = glyph, Title = title, Sub = sub, Run = run, Order = order++, Search = "команда" });

        int pending = Pending();
        Cmd("", "Применить изменения", pending > 0 ? $"Отмечено изменений: {pending} · Ctrl+Enter" : "Нечего применять", () => _ = ApplyAsync());
        Cmd("", "Перепроверить настройки", "Перечитать состояние системы · F5", () => _ = LoadStateAsync());
        Cmd("", "Скопировать код профиля", "Строка WT1-… для друга", CopyCode);
        Cmd("", "Вставить код профиля", "Из буфера обмена", PasteCode);
        Cmd("", "Откатить последний сеанс", "Из «Истории»", () =>
        {
            var s = Journal.LoadAll().FirstOrDefault(x => !x.RolledBack);
            if (s == null) Toast.Pop(this, "Откатывать нечего", Tone.Info);
            else _ = RollbackAsync(new List<Journal.Session> { s });
        });
        Cmd("", "Тест скорости DNS", "Диагностика", () => SelectCategory(Diagnostics));
        Cmd("", "Перезапустить Проводник", "Обслуживание", () => _ = RunBusy("Перезапуск Проводника…", Catalog.RestartExplorer));
        foreach (var (cat, glyph) in AllPages)
            list.Add(new PaletteItem { Glyph = glyph, Title = cat, Sub = "Перейти в раздел", Search = "раздел", Order = order++, Run = () => SelectCategory(cat) });
        for (int i = 0; i < Theme.Accents.Length; i++)
        {
            int idx = i;
            list.Add(new PaletteItem { Glyph = "", Title = "Акцент: " + Theme.Accents[i].Name, Sub = "Цвет интерфейса", Search = "цвет тема", Order = 500 + i, Run = () => ChangeAccent(idx) });
        }
        foreach (var set in Sets.Load())
        {
            var st = set;
            list.Add(new PaletteItem { Glyph = "\uE8FD", Title = "Набор: " + st.Name, Sub = $"Отметить {st.Tweaks.Count} настроек", Search = "набор мой", Order = 600 + order++, Run = () => MarkSet(st) });
        }
        foreach (var t in tweaks)
        {
            var tw = t;
            list.Add(new PaletteItem
            {
                Glyph = tw.IsAction ? "" : GlyphOf(tw.Category),
                Title = tw.Name,
                Sub = tw.Category + (tw.Group != null ? " · " + tw.Group : ""),
                Search = tw.Description,
                Order = 1000 + order++,
                KeepOpen = tw.IsToggle,
                Badge = tw.IsAction ? () => ("перейти", Theme.Muted)
                      : tw.IsValue ? () => (tw.ValueText(desVal.GetValueOrDefault(tw)), desVal.GetValueOrDefault(tw) != curVal.GetValueOrDefault(tw) ? Theme.Accent : Theme.Muted)
                      : () => BadgeFor(tw),
                Run = tw.IsToggle ? () => TogglePalette(tw) : () => GoTo(tw)
            });
        }
        return list;
    }

    (string, Color)? BadgeFor(Tweak t)
    {
        bool cur = current.GetValueOrDefault(t), des = desired.GetValueOrDefault(t);
        if (cur != des) return (des ? "→ вкл" : "→ выкл", Theme.Accent);
        return cur ? ("вкл", Theme.Good) : ("выкл", Theme.Muted);
    }

    void TogglePalette(Tweak t)
    {
        if (desired.GetValueOrDefault(t) && t.Revert == null && current.GetValueOrDefault(t))
        {
            Toast.Pop(this, $"«{t.Name}» не выключается — это можно только включить", Tone.Info);
            return;
        }
        desired[t] = !desired.GetValueOrDefault(t);
        foreach (var r in Rows().Where(r => r.T == t && r.Toggle != null))
        {
            r.Toggle!.Checked = desired[t];
            r.Changed = desired[t] != current.GetValueOrDefault(t);
        }
        UpdatePending();
    }

    // ───────── Профили ─────────

    IEnumerable<Tweak> Shareable => tweaks.Where(t => t.IsToggle && t.Category != StartupManager.Cat);

    void CopyCode()
    {
        var on = Shareable.Where(t => desired.GetValueOrDefault(t)).ToList();
        if (on.Count == 0) { Toast.Pop(this, "Нечего копировать — ни одна настройка не включена", Tone.Info); return; }
        try
        {
            Clipboard.SetText(ProfileCode.Encode(on));
            Toast.Pop(this, $"Код профиля скопирован: {on.Count} {WatchBanner.Plural(on.Count)}. Отправьте его другу");
        }
        catch (Exception ex) { Dialog.Info(this, "Не удалось скопировать", ex.Message, Tone.Error); }
    }

    void PasteCode()
    {
        string text = "";
        try { text = Clipboard.GetText(); } catch { }
        var list = ProfileCode.Decode(text, Shareable);
        if (list == null)
        {
            Dialog.Info(this, "В буфере нет кода профиля", "Скопируйте строку, которая начинается с «WT1-», и повторите.");
            return;
        }
        if (list.Count == 0)
        {
            Dialog.Info(this, "Код не подошёл", "Ни одна настройка из кода не найдена — возможно, он от другой версии WinTweaker.", Tone.Warning);
            return;
        }
        int add = list.Count(t => !desired.GetValueOrDefault(t));
        if (!Dialog.Confirm(this, $"Профиль: {list.Count} {WatchBanner.Plural(list.Count)}",
                $"Будут отмечены ещё {add}. Уже включённые останутся как есть. Применить — кнопкой «Применить».", "Отметить")) return;
        foreach (var t in list) desired[t] = true;
        UpdatePending();
        Build();
        Toast.Pop(this, $"Отмечено {add} — проверьте и нажмите «Применить»");
    }

    void SaveProfile()
    {
        using var dlg = new SaveFileDialog { Filter = "Профиль WinTweaker|*.txt", FileName = "profile.txt" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        File.WriteAllLines(dlg.FileName, Shareable.Where(t => desired.GetValueOrDefault(t)).Select(t => t.Name));
        Toast.Pop(this, "Профиль сохранён");
    }

    void LoadProfile()
    {
        using var dlg = new OpenFileDialog { Filter = "Профиль WinTweaker|*.txt" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        var names = File.ReadAllLines(dlg.FileName).Select(s => s.Trim()).ToHashSet();
        foreach (var t in Shareable) desired[t] = names.Contains(t.Name);
        Build();
        UpdatePending();
        Toast.Pop(this, "Профиль загружен — нажмите «Применить»", Tone.Info);
    }

    // ───────── Состояние и применение ─────────

    List<ChangeItem> PendingItems()
    {
        var list = new List<ChangeItem>();
        foreach (var t in tweaks.Where(t => t.IsValue && desVal.GetValueOrDefault(t) != curVal.GetValueOrDefault(t)))
            list.Add(new ChangeItem { Tweak = t, From = t.ValueText(curVal[t]), To = t.ValueText(desVal[t]), TurnsOn = true });
        foreach (var t in tweaks.Where(t => t.IsToggle && desired.GetValueOrDefault(t) != current.GetValueOrDefault(t) && (desired[t] || t.Revert != null)))
        {
            bool prog = t.Category == Programs.Cat;
            list.Add(new ChangeItem
            {
                Tweak = t, TurnsOn = desired[t],
                From = prog ? (current[t] ? "установлена" : "нет") : current[t] ? "вкл" : "выкл",
                To = prog ? (desired[t] ? "установить" : "удалить") : desired[t] ? "вкл" : "выкл"
            });
        }
        return list.OrderBy(i => i.Tweak.Category).ToList();
    }

    /// <summary>Убрать одно изменение: вернуть отметку к текущему состоянию.</summary>
    void Discard(Tweak t)
    {
        if (t.IsValue) desVal[t] = curVal.GetValueOrDefault(t);
        else desired[t] = current.GetValueOrDefault(t);
        SyncRow(t);
        UpdatePending();
    }

    void ResetPending()
    {
        foreach (var t in tweaks)
        {
            if (t.IsValue) desVal[t] = curVal.GetValueOrDefault(t);
            else if (!t.IsAction) desired[t] = current.GetValueOrDefault(t);
        }
        UpdatePending();
        Build();
        Toast.Pop(this, "Все отметки сброшены", Tone.Info);
    }

    void SyncRow(Tweak t)
    {
        foreach (var r in Rows().Where(r => r.T == t))
        {
            if (r.Toggle != null) r.Toggle.Checked = desired.GetValueOrDefault(t);
            if (r.Seg != null) r.Seg.Value = desVal.GetValueOrDefault(t);
            if (r.Slide != null) r.Slide.Value = desVal.GetValueOrDefault(t);
            r.Changed = t.IsValue ? desVal.GetValueOrDefault(t) != curVal.GetValueOrDefault(t) : desired.GetValueOrDefault(t) != current.GetValueOrDefault(t);
        }
    }

    int Pending() =>
        tweaks.Count(t => t.IsToggle && desired.GetValueOrDefault(t) != current.GetValueOrDefault(t) && (desired.GetValueOrDefault(t) || t.Revert != null)) +
        tweaks.Count(t => t.IsValue && desVal.GetValueOrDefault(t) != curVal.GetValueOrDefault(t));

    void UpdatePending()
    {
        if (busy) return;
        int n = Pending();
        if (n == 0)
        {
            applyBtn.Count = 0;
            var real = tweaks.Where(t => t.IsToggle && t.Category != StartupManager.Cat && t.Category != Programs.Cat).ToList();
            badge.SetStatus($"Применено {real.Count(t => current.GetValueOrDefault(t))} из {real.Count}", Theme.Muted);
        }
        else badge.SetStatus($"Изменений к применению: {n} — показать", Theme.Accent);
        applyBtn.Count = n;
        pendingBar.Count = n;
        tabStrip?.Invalidate();
    }

    async Task LoadStateAsync()
    {
        var startup = new List<Tweak>();
        bool detect = !hwDetected;
        hwDetected = true;
        await RunBusy("Проверка настроек…", () =>
        {
            AppxCache.Reset();
            DnsCache.Reset();
            Programs.Installed.Reset();
            if (detect) Hardware.Detect();
            // медленные общие данные (PowerShell, планировщик) — одновременно, а не по очереди
            Parallel.Invoke(
                () => { try { AppxCache.Has("\u0000"); } catch { } },
                () => { try { _ = DnsCache.Current; } catch { } },
                () => { try { _ = Programs.Installed.Names; } catch { } },
                () => { try { watchOn = Watch.Installed(); } catch { } },
                () => { try { autostartOn = Autostart.Installed(); } catch { } },
                () => { try { startup = StartupManager.Load(); } catch { } });
            CheckState(tweaks.Where(t => t.Category != StartupManager.Cat).Concat(startup).ToList());
        });
        // список автозагрузки читается заново при каждой проверке
        foreach (var old in tweaks.Where(t => t.Category == StartupManager.Cat).ToList())
        {
            current.Remove(old);
            desired.Remove(old);
        }
        tweaks.RemoveAll(t => t.Category == StartupManager.Cat);
        tweaks.AddRange(startup);

        try { reverted = Watch.Reverted(tweaks, t => current.GetValueOrDefault(t)); } catch { reverted = new(); }
        loaded = true;
        Build();
        UpdatePending();
    }

    /// <summary>
    /// Прочитать текущее состояние настроек в несколько потоков: многие проверки ждут внешние команды
    /// (powercfg, PowerShell), и по очереди они шли десятки секунд. Результаты записываются уже в одном потоке.
    /// </summary>
    void CheckState(List<Tweak> list)
    {
        var vals = new int[list.Count];
        var flags = new bool[list.Count];
        Parallel.For(0, list.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8) }, i =>
        {
            var t = list[i];
            try
            {
                if (t.IsValue) vals[i] = t.Get!();
                else if (!t.IsAction) flags[i] = t.IsApplied();
            }
            catch { }
        });
        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i];
            if (t.IsValue) { curVal[t] = vals[i]; desVal[t] = vals[i]; }
            else { current[t] = flags[i]; desired[t] = flags[i]; }
        }
    }

    /// <summary>После применения — перепроверить только разделы, где были изменения (а не все 170+ настроек).</summary>
    async Task RefreshAfterApply(List<Tweak> changed)
    {
        if (changed.Any(t => t.Category == StartupManager.Cat)) { await LoadStateAsync(); return; }
        var cats = changed.Select(t => t.Category).ToHashSet();
        var list = tweaks.Where(t => cats.Contains(t.Category)).ToList();
        await RunBusy("Проверка…", () =>
        {
            if (cats.Contains("Приложения")) AppxCache.Reset();
            DnsCache.Reset();
            Programs.Installed.Reset();
            CheckState(list);
            if (changed.Any(t => t.Name.StartsWith("Сторож"))) try { watchOn = Watch.Installed(); } catch { }
        });
        try { reverted = Watch.Reverted(tweaks, t => current.GetValueOrDefault(t)); } catch { reverted = new(); }
        Build();
        UpdatePending();
    }

    /// <summary>
    /// Нужна ли точка восстановления: только если среди изменений есть то, что может навредить системе, —
    /// «для опытных», службы, удаление приложений. Интерфейс и приватность откатываются из «Истории» и без неё.
    /// </summary>
    static bool NeedsRestorePoint(IEnumerable<Tweak> changes) =>
        changes.Any(t => t.Risky || t.Category is "Службы" or "Приложения");

    async Task ApplyAsync()
    {
        if (busy) return;
        var changes = tweaks
            .Where(t => t.IsToggle && desired.GetValueOrDefault(t) != current.GetValueOrDefault(t) && (desired[t] || t.Revert != null))
            .ToList();
        var vchanges = tweaks.Where(t => t.IsValue && desVal.GetValueOrDefault(t) != curVal.GetValueOrDefault(t)).ToList();
        int total = changes.Count + vchanges.Count;
        if (total == 0)
        {
            Toast.Pop(this, "Нет изменений для применения", Tone.Info);
            return;
        }

        // окно проверки: можно убрать отдельные изменения, посмотреть ключи реестра или сбросить всё
        var review = PendingItems();
        using (var dlg = new ChangesDialog(review, Discard, ShowDetails))
        {
            var res = dlg.ShowDialog(this);
            if (dlg.ResetAll) { ResetPending(); return; }
            if (res != DialogResult.OK) return;
        }
        changes = changes.Where(t => desired.GetValueOrDefault(t) != current.GetValueOrDefault(t)).ToList();
        vchanges = vchanges.Where(t => desVal.GetValueOrDefault(t) != curVal.GetValueOrDefault(t)).ToList();
        total = changes.Count + vchanges.Count;
        if (total == 0) return;

        var errors = new List<string>();
        bool makeRestore = restoreToggle.Checked && NeedsRestorePoint(changes.Concat(vchanges));
        bool restart = changes.Concat(vchanges).Any(t => t.NeedsRestart);
        await RunBusy("Применение…", () =>
        {
            if (makeRestore)
            {
                SetStatus("Точка восстановления…");
                try { CreateRestorePoint(); } catch (Exception ex) { errors.Add("Точка восстановления: " + ex.Message); }
            }
            int n = 0;
            var session = Journal.StartSession();
            foreach (var t in vchanges)
            {
                SetStatus($"[{++n}/{total}] {t.Name}");
                Journal.BeginValue(session, t, curVal[t], desVal[t]);
                try { t.Set!(desVal[t]); }
                catch (Exception ex) { errors.Add($"{t.Name}: {ex.Message}"); }
                finally { Journal.End(); }
            }
            foreach (var t in changes)
            {
                SetStatus($"[{++n}/{total}] {t.Name}");
                Journal.Begin(session, t, current[t], desired[t]);
                try
                {
                    if (desired[t]) t.Apply(); else t.Revert?.Invoke();
                }
                catch (Exception ex) { errors.Add($"{t.Name}: {ex.Message}"); }
                finally { Journal.End(); }
            }
            try { Journal.Save(session); } catch (Exception ex) { errors.Add("Журнал отката: " + ex.Message); }
        });

        await RefreshAfterApply(changes.Concat(vchanges).ToList());

        if (errors.Count > 0)
            Dialog.Info(this, "Применено с ошибками", string.Join("\n", errors), Tone.Error);
        else
            Toast.Pop(this, $"Применено изменений: {total}");
        if (restart) Toast.Pop(this, "Часть настроек заработает после перезагрузки", Tone.Warning);

        bool touchesShell = changes.Concat(vchanges).Any(t => t.Category is "Интерфейс" or "Проводник" or "Панель задач" or "Контекстное меню" or "Звук и уведомления")
                            && AppSettings.Get("askExplorer", "1") == "1";
        if (touchesShell && Dialog.Confirm(this, "Перезапустить Проводник?",
                "Так изменения панели задач и Проводника вступят в силу сразу. Открытые окна Проводника закроются.",
                "Перезапустить", "Не сейчас"))
            Catalog.RestartExplorer();
    }

    /// <summary>
    /// Если на компьютере остался DNS, убранный из списка как неработающий (Xbox DNS), — один раз предложить вернуть DNS провайдера.
    /// </summary>
    async Task OfferRetiredDns()
    {
        if (AppSettings.Get("retiredDns.asked", "") == "1") return;
        string? name = null;
        try { name = await Task.Run(RussiaNetwork.RetiredInUse); } catch { }
        if (name == null || IsDisposed) return;
        AppSettings.Set("retiredDns.asked", "1");
        if (Dialog.Confirm(this, $"{name} больше не работает",
                $"У вас включён {name}. В октябре 2026 он попал под блокировки, а его создатели сообщили о вероятном закрытии — " +
                "из-за этого сайты и игры могут открываться с ошибками.\n\nВернуть DNS от провайдера? Другой DNS можно выбрать в «Сеть» → «Обход блокировок».",
                "Вернуть DNS провайдера", "Выбрать другой"))
        {
            if (await RunBusy("Возвращаю DNS провайдера…", RussiaNetwork.ResetDns))
            {
                Toast.Pop(this, "DNS провайдера возвращён");
                await LoadStateAsync();
            }
        }
        else SelectCategory(RussiaNetwork.Page);
    }

    static void CreateRestorePoint()
    {
        // По умолчанию Windows создаёт не больше одной точки в 24 ч — снимаем ограничение
        Reg.Set(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "SystemRestorePointCreationFrequency", 0);
        Shell.PS("Enable-ComputerRestore -Drive $env:SystemDrive; " +
                 "Checkpoint-Computer -Description 'WinTweaker' -RestorePointType MODIFY_SETTINGS");
    }

    void SetStatus(string text) => BeginInvoke(() => badge.SetStatus(text, Theme.Accent));

    async Task<bool> RunBusy(string text, Action work)
    {
        busy = true;
        progress.Running = true;
        badge.SetStatus(text, Theme.Accent);
        UseWaitCursor = true;
        view.Enabled = false;
        try { await Task.Run(work); return true; }
        catch (Exception ex)
        {
            Dialog.Info(this, "Ошибка", ex.Message, Tone.Error);
            return false;
        }
        finally
        {
            view.Enabled = true;
            UseWaitCursor = false;
            progress.Running = false;
            busy = false;
            UpdatePending();
        }
    }
}
