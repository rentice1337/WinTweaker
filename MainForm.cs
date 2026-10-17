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

    // Боковое меню: раздел → пункты (названия совпадают с Category в Catalog.cs)
    static readonly (string Section, (string Cat, string Glyph)[] Items)[] Nav =
    {
        ("", new[] { (Home, "\uE80F"), (Diagnostics, "\uE9D9"), (History, "\uE81C") }),
        ("Система", new[]
        {
            ("Конфиденциальность", "\uE72E"),
            ("Интерфейс", "\uE771"),
            ("Панель задач", "\uE7C4"),
            ("Проводник", "\uE8B7"),
            ("Контекстное меню", "\uE700"),
            ("Производительность", "\uEC4A"),
            ("Безопасность", "\uEA18"),
        }),
        ("Устройства", new[]
        {
            ("Мышь и клавиатура", "\uE962"),
            ("Звук и уведомления", "\uE767"),
        }),
        ("Софт", new[]
        {
            (Programs.Cat, "\uE7B8"),
            ("Приложения", "\uE71D"),
            (StartupManager.Cat, "\uE7E8"),
            ("Службы", "\uE912"),
            ("Обновления", "\uE895"),
            ("Игры", "\uE7FC"),
            ("Обслуживание", "\uE90F"),
        }),
        ("Сеть", new[]
        {
            ("Сеть", "\uE968"),
            ("Обход блокировок", "\uE774"),
        }),
        ("Программа", new[] { (SettingsPage, "\uE713") }),
    };

    static string GlyphOf(string cat) =>
        Nav.SelectMany(s => s.Items).FirstOrDefault(i => i.Cat == cat).Glyph ?? "";

    readonly List<Tweak> tweaks = Catalog.All();
    readonly Dictionary<Tweak, bool> current = new();
    readonly Dictionary<Tweak, bool> desired = new();
    readonly Dictionary<Tweak, int> curVal = new();
    readonly Dictionary<Tweak, int> desVal = new();
    readonly List<NavItem> navItems = new();
    readonly NavHost nav = new();
    readonly Viewport view = new();
    readonly UserBadge badge = new() { User = Environment.UserName };
    readonly Toggle restoreToggle = new() { Checked = true };
    readonly ProgressLine progress = new() { Dock = DockStyle.Bottom };
    readonly PaletteTrigger trigger = new();
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

    public MainForm()
    {
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
        Controls.Add(main);
        Controls.Add(BuildSidebar());

        ApplyAppSettings();
        view.Resize += (_, _) => Relayout();
        liveTimer.Tick += (_, _) => LiveTick();
        cursorTimer.Tick += (_, _) => { if (WindowState != FormWindowState.Minimized) Spotlight.Move(Cursor.Position); };
        ShowSkeleton();

        Load += (_, _) => Glass.Attach(this);
        Shown += async (_, _) =>
        {
            cursorTimer.Start();
            await LoadStateAsync();
            if (AppSettings.Get("updates", "1") == "1") _ = CheckUpdates(manual: false);
        };
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        using var p = Theme.Round(new RectangleF(0, 0, Width, Height), 12);
        Region = new Region(p);
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
        if (closing) return;
        closing = true;
        Motion.Run(180, p => Opacity = 1 - p, Motion.InOut, 0, Close);
    }

    // ───────── Каркас окна ─────────

    Control BuildSidebar()
    {
        var side = new SidePanel { Dock = DockStyle.Left, Width = 236 };
        Drag(side);
        Drag(nav.Canvas);

        var nc = nav.Nav;
        int y = 4;
        foreach (var (section, items) in Nav)
        {
            if (section != "")
            {
                nc.Labels.Add((section, y + 7));
                y += 25;
            }
            foreach (var (cat, glyph) in items)
            {
                var n = new NavItem(glyph, cat) { Location = new Point(10, y), Width = 214 };
                n.SetSelectedNow(cat == selected);
                n.Click += (_, _) => SelectCategory(cat);
                navItems.Add(n);
                nc.Controls.Add(n);
                if (cat == selected) nc.Ind.Set(y);
                y += NavCanvas.Step;
            }
            y += 6;
        }
        int navHeight = y + 6;

        badge.Dock = DockStyle.Bottom;
        badge.Height = 62;
        side.Controls.Add(nav);
        side.Controls.Add(badge);
        side.Resize += (_, _) =>
        {
            nav.SetBounds(0, 76, side.Width - 1, Math.Max(80, side.Height - 76 - badge.Height));
            nav.SetContentHeight(navHeight);
        };
        return side;
    }

    Control BuildTopBar()
    {
        var top = new TopPanel { Dock = DockStyle.Top, Height = 64, Label = "Точка восстановления" };
        Drag(top);
        top.Controls.Add(progress);

        var apply = new FlatBtn("", "Применить") { Primary = true };
        apply.Click += async (_, _) => await ApplyAsync();
        var refresh = new FlatBtn("", "");
        refresh.Click += async (_, _) => { if (!busy) await LoadStateAsync(); };
        tip.SetToolTip(apply, "Применить отмеченные изменения (Ctrl+Enter)");
        tip.SetToolTip(refresh, "Перепроверить состояние системы (F5)");
        tip.SetToolTip(restoreToggle, "Создавать точку восстановления Windows перед применением изменений.\nТребует включённой защиты системы на диске C:.");
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
        foreach (var n in navItems)
        {
            n.Selected = n.Text == cat;
            if (n.Text == cat) nav.Nav.Ind.To(n.Top, 420);
        }
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
            default:
                var items = tweaks.Where(t => t.Category == selected).ToList();
                cv.Controls.Add(new Header(selected, Subtitle(items)));
                foreach (var card in MakeCards(items)) cv.Controls.Add(card);
                break;
        }
        cv.ResumeLayout();
        Relayout();
        RevealAll();
        if (focus != null) FocusRow(focus);
    }

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
        view.SetContentHeight(Math.Max(y[0], y[1]) + pad);
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

        var cats = Nav.SelectMany(s => s.Items).Select(i => i.Cat)
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

        SysInfo.Cpu(); // первый замер — точка отсчёта
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
        if (live == null) { liveTimer.Stop(); return; }
        var m = SysInfo.Memory();
        live.Push(SysInfo.Cpu(), m.Load, $"{m.UsedGb:0.0} из {m.TotalGb:0.0} ГБ");
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
            if (!w.IsDisposed) w.SetData(res, dnsCurrent);
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
        DetailsDialog.Show(this, t.Name, sub, text);
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
        foreach (var (cat, glyph) in Nav.SelectMany(s => s.Items))
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

    int Pending() =>
        tweaks.Count(t => t.IsToggle && desired.GetValueOrDefault(t) != current.GetValueOrDefault(t)) +
        tweaks.Count(t => t.IsValue && desVal.GetValueOrDefault(t) != curVal.GetValueOrDefault(t));

    void UpdatePending()
    {
        if (busy) return;
        int n = Pending();
        if (n == 0)
        {
            var real = tweaks.Where(t => t.IsToggle && t.Category != StartupManager.Cat && t.Category != Programs.Cat).ToList();
            badge.SetStatus($"Применено {real.Count(t => current.GetValueOrDefault(t))} из {real.Count}", Theme.Muted);
        }
        else badge.SetStatus($"Изменений к применению: {n}", Theme.Accent);
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
            try { watchOn = Watch.Installed(); } catch { }
            try { startup = StartupManager.Load(); } catch { }
            foreach (var t in tweaks.Where(t => t.Category != StartupManager.Cat).Concat(startup))
            {
                if (t.IsValue)
                {
                    int val = 0;
                    try { val = t.Get!(); } catch { }
                    curVal[t] = val;
                    desVal[t] = val;
                    continue;
                }
                bool v = false;
                if (!t.IsAction)
                    try { v = t.IsApplied(); } catch { }
                current[t] = v;
                desired[t] = v;
            }
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

        var risky = changes.Where(t => t.Risky && desired[t]).Select(t => "• " + t.Name).ToList();
        string msg = $"Будет изменено настроек: {total}.";
        var installs = changes.Where(t => t.Category == Programs.Cat).ToList();
        if (installs.Count > 0) msg += $"\n\nПрограмм к установке/удалению: {installs.Count} — это может занять несколько минут.";
        if (risky.Count > 0) msg += "\n\nСреди них рискованные:\n" + string.Join("\n", risky);
        msg += "\n\nВсё попадёт в «Историю» — откатить можно в один клик.";
        if (!Dialog.Confirm(this, "Применить изменения?", msg, "Применить", "Отмена", risky.Count > 0 ? Tone.Warning : Tone.Info))
            return;

        var errors = new List<string>();
        bool makeRestore = restoreToggle.Checked;
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

        await LoadStateAsync();

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
