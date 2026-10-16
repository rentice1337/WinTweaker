using System.Runtime.InteropServices;

namespace WinTweaker;

public class MainForm : Form
{
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);

    // Боковое меню: раздел → категории (названия совпадают с Category в Catalog.cs)
    static readonly (string Section, (string Cat, string Glyph)[] Items)[] Nav =
    {
        ("", new[] { ("Главная", "\uE80F"), (History, "\uE81C") }),
        ("Система", new[]
        {
            ("Конфиденциальность", ""),
            ("Интерфейс", ""),
            ("Проводник", ""),
            ("Производительность", ""),
            ("Безопасность", ""),
        }),
        ("Софт", new[]
        {
            ("Приложения", ""),
            (StartupManager.Cat, "\uE7E8"),
            ("Службы", ""),
            ("Обновления", ""),
            ("Игры", ""),
            ("Обслуживание", ""),
        }),
        ("Сеть", new[]
        {
            ("Сеть", ""),
            ("Обход блокировок", ""),
        }),
    };

    readonly List<Tweak> tweaks = Catalog.All();
    readonly Dictionary<Tweak, bool> current = new();
    readonly Dictionary<Tweak, bool> desired = new();
    readonly List<NavItem> navItems = new();
    readonly DBPanel content = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
    readonly TextBox search = new();
    readonly Toggle restoreToggle = new() { Checked = true };
    readonly ProgressLine progress = new() { Dock = DockStyle.Bottom };
    readonly Label status = new() { AutoSize = false, ForeColor = Theme.Muted, Font = Theme.F(8.5f), BackColor = Theme.Side };
    readonly ToolTip tip = UI.MakeTip();
    string selected = Home;
    const string Home = "Главная";
    const string History = "История";
    readonly System.Windows.Forms.Timer liveTimer = new() { Interval = 1000 };
    bool busy, closing, loaded;

    public MainForm()
    {
        Text = "WinTweaker";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1060, 720);
        MinimumSize = new Size(860, 560);
        BackColor = Theme.Bg;
        Font = Theme.Body;
        DoubleBuffered = true;
        Opacity = 0;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        int acc = int.TryParse(AppSettings.Get("accent", "0"), out var a) ? Math.Clamp(a, 0, Theme.Accents.Length - 1) : 0;
        Theme.SetAccent(Theme.Accents[acc].Color);

        Controls.Add(BuildSidebar());
        Controls.Add(BuildTopBar());
        Controls.Add(content);
        content.BringToFront();

        content.Resize += (_, _) => Relayout();
        liveTimer.Tick += (_, _) => LiveTick();
        search.TextChanged += (_, _) => { if (loaded) Build(); };
        ShowSkeleton();

        Shown += async (_, _) =>
        {
            Motion.Run(320, p => Opacity = p, Motion.ExpoOut);
            await LoadStateAsync();
        };
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        using var p = Theme.Round(new RectangleF(0, 0, Width, Height), 12);
        Region = new Region(p);
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
        var side = new Panel { Dock = DockStyle.Left, Width = 226, BackColor = Theme.Side };
        Drag(side);

        var logo = new Label
        {
            Text = "WINTWEAK", Font = new Font("Segoe UI Black", 19f), ForeColor = Theme.Text,
            AutoSize = true, Location = new Point(20, 16), BackColor = Theme.Side
        };
        Drag(logo);
        side.Controls.Add(logo);

        int y = 72;
        foreach (var (section, items) in Nav)
        {
            if (section != "")
            side.Controls.Add(new Label
            {
                Text = section, ForeColor = Theme.Dim, Font = Theme.F(8.5f),
                Location = new Point(22, y), AutoSize = true, BackColor = Theme.Side
            });
            if (section != "") y += 21;
            foreach (var (cat, glyph) in items)
            {
                var n = new NavItem(glyph, cat) { Location = new Point(12, y), Width = 202 };
                n.SetSelectedNow(cat == selected);
                n.Click += (_, _) => SelectCategory(cat);
                navItems.Add(n);
                side.Controls.Add(n);
                y += 35;
            }
            y += 8;
        }

        var user = new UserBadge { Dock = DockStyle.Bottom, Height = 66, User = Environment.UserName };
        status.SetBounds(64, 35, 156, 20);
        user.Controls.Add(status);
        side.Controls.Add(user);
        return side;
    }

    Control BuildTopBar()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = Theme.Bg };
        Drag(top);
        top.Controls.Add(progress);

        var apply = new FlatBtn("", "Применить") { Primary = true };
        apply.Click += async (_, _) => await ApplyAsync();
        var refresh = new FlatBtn("", "Обновить");
        refresh.Click += async (_, _) => await LoadStateAsync();
        var profile = new FlatBtn("", "Профиль");

        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), Font = Theme.Body };
        menu.Items.Add("Сохранить профиль…", null, (_, _) => SaveProfile());
        menu.Items.Add("Загрузить профиль…", null, (_, _) => LoadProfile());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Создать точку восстановления", null,
            async (_, _) => await RunBusy("Создание точки восстановления…", CreateRestorePoint));
        profile.Click += (_, _) => menu.Show(profile, new Point(0, profile.Height + 4));

        int x = 18;
        foreach (var c in new Control[] { apply, refresh, profile })
        {
            c.Location = new Point(x, 14);
            top.Controls.Add(c);
            x += c.Width + 6;
        }
        restoreToggle.Location = new Point(x + 16, 20);
        top.Controls.Add(restoreToggle);
        top.Controls.Add(new Label
        {
            Text = "Точка восстановления", ForeColor = Theme.Muted, AutoSize = true,
            BackColor = Theme.Bg, Location = new Point(x + 60, 21)
        });
        var dots = new AccentDots(Array.FindIndex(Theme.Accents, c => c.Color == Theme.Accent)) { Location = new Point(x + 210, 15) };
        dots.Picked += ChangeAccent;
        top.Controls.Add(dots);
        tip.SetToolTip(dots, "Цвет акцента");
        tip.SetToolTip(restoreToggle, "Создавать точку восстановления Windows перед применением изменений.\nТребует включённой защиты системы на диске C:.");

        var close = new FlatBtn("", "") { Danger = true };
        close.Click += (_, _) => FadeClose();
        var min = new FlatBtn("", "");
        min.Click += (_, _) => WindowState = FormWindowState.Minimized;
        var sbox = new SearchBox(search) { Size = new Size(220, 32) };
        top.Controls.AddRange(new Control[] { close, min, sbox });
        top.Resize += (_, _) =>
        {
            close.Location = new Point(top.Width - close.Width - 12, 14);
            min.Location = new Point(close.Left - min.Width - 2, 14);
            sbox.Location = new Point(min.Left - sbox.Width - 12, 14);
        };
        return top;
    }

    // Плавная смена акцента: цвет перетекает 300 мс, всё окно перерисовывается
    void ChangeAccent(int i)
    {
        var from = Theme.Accent;
        var to = Theme.Accents[i].Color;
        AppSettings.Set("accent", i.ToString());
        Motion.Run(300, p =>
        {
            Theme.SetAccent(Theme.Lerp(from, to, p));
            Invalidate(true);
        }, Motion.InOut, 0, () => Toast.Pop(this, "Акцент: " + AccentDots.NameOf(i), Tone.Info));
    }

    // ───────── Контент ─────────

    void ClearContent()
    {
        var old = content.Controls.Cast<Control>().ToList();
        content.Controls.Clear();
        old.ForEach(c => c.Dispose());
        content.AutoScrollPosition = Point.Empty;
    }

    void ShowSkeleton()
    {
        content.SuspendLayout();
        ClearContent();
        foreach (int rows in new[] { 6, 4, 5, 3 })
            content.Controls.Add(new SkeletonCard(rows));
        content.ResumeLayout();
        Relayout();
    }

    void SelectCategory(string cat)
    {
        if (cat == selected && search.Text == "") return;
        selected = cat;
        foreach (var n in navItems) n.Selected = n.Text == cat;
        if (search.Text != "") search.Text = ""; // вызовет Build()
        else Build();
    }

    void Build()
    {
        content.SuspendLayout();
        ClearContent();
        liveTimer.Stop();

        string q = search.Text.Trim();
        bool searching = q != "";
        if (!searching && selected == History)
        {
            BuildHistory();
            content.ResumeLayout();
            Relayout();
            RevealAll();
            return;
        }
        if (!searching && selected == Home)
        {
            BuildDashboard();
            content.ResumeLayout();
            Relayout();
            RevealAll();
            return;
        }
        var items = (searching
            ? tweaks.Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                             || t.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
            : tweaks.Where(t => t.Category == selected)).ToList();

        content.Controls.Add(new Header(searching ? $"Поиск: «{q}»" : selected, Subtitle(items)));
        foreach (var card in MakeCards(items, searching))
            content.Controls.Add(card);

        content.ResumeLayout();
        Relayout();

        RevealAll();
    }

    // каскадное появление: заголовок, затем карточки с шагом 45 мс
    void RevealAll()
    {
        int i = 0;
        foreach (var f in content.Controls.OfType<FadeIn>())
        {
            f.Hide0();
            f.Reveal(i++ * 45);
        }
    }

    // ───────── Главная ─────────

    void BuildDashboard()
    {
        int h = DateTime.Now.Hour;
        string hello = h < 5 ? "Доброй ночи" : h < 12 ? "Доброе утро" : h < 18 ? "Добрый день" : "Добрый вечер";
        content.Controls.Add(new Header(Home, $"{hello}, {Environment.UserName}"));

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
        content.Controls.Add(presets);

        var mem = SysInfo.Memory();
        content.Controls.Add(new InfoWidget("Система",
            ("Windows", SysInfo.Os()),
            ("Сборка", SysInfo.Build()),
            ("Процессор", SysInfo.CpuName()),
            ("Видеокарта", SysInfo.Gpu()),
            ("Память", $"{mem.TotalGb:0.#} ГБ"),
            ("Работает", SysInfo.Uptime())));

        var live = new LiveWidget();
        content.Controls.Add(live);

        var cats = Nav.SelectMany(s => s.Items).Select(i => i.Cat).Where(c => c != Home && c != StartupManager.Cat)
            .Select(c =>
            {
                var list = tweaks.Where(t => t.Category == c && !t.IsAction).ToList();
                return (c, list.Count(t => current.GetValueOrDefault(t)), list.Count);
            })
            .Where(x => x.Item3 > 0).ToList();
        var progress = new ProgressWidget(cats);
        progress.CategoryClicked += SelectCategory;
        content.Controls.Add(progress);

        content.Controls.Add(new DisksWidget());

        var clean = new FlatBtn("\uE74D", "Очистить временные файлы");
        clean.Click += async (_, _) =>
        {
            var t = tweaks.FirstOrDefault(x => x.Name == "Удалить временные файлы");
            if (t != null && !busy && await RunBusy("Очистка…", t.Apply)) Toast.Pop(this, "Временные файлы удалены");
        };
        var restore = new FlatBtn("\uE81C", "Создать точку восстановления");
        restore.Click += async (_, _) =>
        {
            if (!busy && await RunBusy("Создание точки восстановления…", CreateRestorePoint)) Toast.Pop(this, "Точка восстановления создана");
        };
        var explorer = new FlatBtn("\uEC50", "Перезапустить Проводник");
        explorer.Click += async (_, _) =>
        {
            if (!busy && await RunBusy("Перезапуск Проводника…", Catalog.RestartExplorer)) Toast.Pop(this, "Проводник перезапущен");
        };
        var recheck = new FlatBtn("\uE72C", "Перепроверить настройки");
        recheck.Click += async (_, _) => { if (!busy) await LoadStateAsync(); };
        content.Controls.Add(new ActionsWidget("Быстрые действия", clean, restore, explorer, recheck));

        SysInfo.Cpu(); // первый замер — точка отсчёта
        LiveTick();
        liveTimer.Start();
    }

    // ───────── История и откат ─────────

    void BuildHistory()
    {
        var sessions = Journal.LoadAll();
        var open = sessions.Where(s => !s.RolledBack).ToList();
        content.Controls.Add(new Header(History, sessions.Count == 0
            ? "изменений пока не было — здесь появится каждое нажатие «Применить»"
            : $"сеансов: {sessions.Count} · можно откатить: {open.Count}"));

        var all = new FlatBtn("\uE7A7", "Откатить всё") { Primary = open.Count > 0 };
        all.Click += async (_, _) =>
        {
            if (open.Count == 0) { Toast.Pop(this, "Откатывать нечего", Tone.Info); return; }
            await RollbackAsync(open);
        };
        var folder = new FlatBtn("\uE838", "Открыть папку истории");
        folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(Journal.Folder);
            System.Diagnostics.Process.Start("explorer.exe", Journal.Folder);
        };
        content.Controls.Add(new ActionsWidget("Действия", all, folder));

        var latest = open.FirstOrDefault(); // откатываем строго с конца — сеансы не конфликтуют
        foreach (var s in sessions.Take(30))
        {
            FlatBtn? btn = null;
            if (s == latest)
            {
                btn = new FlatBtn("\uE7A7", "Откатить этот сеанс");
                btn.Click += async (_, _) => await RollbackAsync(new List<Journal.Session> { s });
            }
            content.Controls.Add(new SessionWidget(s, btn));
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

    // Пресет только отмечает переключатели — применяет пользователь кнопкой «Применить»
    void ApplyPreset(Presets.Preset p)
    {
        if (busy) return;
        var list = Presets.Resolve(p, tweaks);
        int marked = 0;
        foreach (var t in list.Where(t => !desired.GetValueOrDefault(t)))
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
        var live = content.Controls.OfType<LiveWidget>().FirstOrDefault();
        if (live == null) { liveTimer.Stop(); return; }
        var m = SysInfo.Memory();
        live.Push(SysInfo.Cpu(), m.Load, $"{m.UsedGb:0.0} из {m.TotalGb:0.0} ГБ");
    }

    string Subtitle(List<Tweak> items)
    {
        var real = items.Where(t => !t.IsAction).ToList();
        int actions = items.Count - real.Count;
        string s = $"настроек: {real.Count} · включено: {real.Count(t => current.GetValueOrDefault(t))}";
        if (actions > 0) s += $" · действий: {actions}";
        if (items.Count == 0) s = "ничего не найдено";
        return s;
    }

    IEnumerable<Card> MakeCards(IEnumerable<Tweak> items, bool byCategory)
    {
        foreach (var g in items.GroupBy(t => byCategory ? t.Category : t.Group ?? ""))
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

        if (r.FolderBtn != null)
        {
            tip.SetToolTip(r.FolderBtn, "Открыть расположение файла");
            r.FolderBtn.Click += (_, _) => { try { t.OpenFolder!(); } catch { } };
        }

        if (r.Toggle != null)
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
            r.RunBtn!.Click += async (_, _) =>
            {
                if (busy) return;
                if (t.Risky && !Dialog.Confirm(this, t.Name, t.Description + "\n\nВыполнить?", "Выполнить", "Отмена", Tone.Warning)) return;
                if (await RunBusy(t.Name + "…", t.Apply)) Toast.Pop(this, "Готово: " + t.Name);
            };
        }
        return r;
    }

    static string Describe(Tweak t)
    {
        string s = t.Description == "" ? t.Name : t.Description;
        if (t.Risky) s += "\n\n⚠ Рискованная настройка.";
        if (!t.IsAction && t.Revert == null) s += "\n\nНельзя отменить программой.";
        if (t.NeedsRestart) s += "\n\nНужна перезагрузка.";
        return s;
    }

    // Раскладка: заголовок на всю ширину, ниже карточки в две колонки «кирпичиком»
    void Relayout()
    {
        const int pad = 18;
        var s = content.AutoScrollPosition;
        int full = Math.Max(300, content.ClientSize.Width - pad * 2);
        int w = Math.Max(260, (content.ClientSize.Width - pad * 3) / 2);
        int top = 10;

        foreach (var h in content.Controls.OfType<Header>())
        {
            h.Width = full;
            h.Place(new Point(pad + s.X, top + s.Y));
            top += h.Height + 2;
        }

        int[] y = { top, top };
        foreach (Control c in content.Controls)
        {
            if (c is Header) continue;
            int col = y[0] <= y[1] ? 0 : 1;
            c.Width = w;
            if (c is Card card) card.LayoutRows();
            var p = new Point(pad + col * (w + pad) + s.X, y[col] + s.Y);
            if (c is FadeIn f) f.Place(p); else c.Location = p;
            y[col] += c.Height + 12;
        }
        content.AutoScrollMinSize = new Size(0, Math.Max(y[0], y[1]) + pad);
    }

    void UpdatePending()
    {
        if (busy) return;
        int n = tweaks.Count(t => !t.IsAction && desired.GetValueOrDefault(t) != current.GetValueOrDefault(t));
        if (n == 0)
        {
            var real = tweaks.Where(t => !t.IsAction && t.Category != StartupManager.Cat).ToList();
            status.Text = $"Применено: {real.Count(t => current.GetValueOrDefault(t))} из {real.Count}";
            status.ForeColor = Theme.Muted;
        }
        else
        {
            status.Text = $"Изменений: {n}";
            status.ForeColor = Theme.Accent;
        }
    }

    // ───────── Логика ─────────

    async Task LoadStateAsync()
    {
        var startup = new List<Tweak>();
        await RunBusy("Проверка настроек…", () =>
        {
            AppxCache.Reset();
            DnsCache.Reset();
            try { startup = StartupManager.Load(); } catch { }
            foreach (var t in tweaks.Where(t => t.Category != StartupManager.Cat).Concat(startup))
            {
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

        loaded = true;
        Build();
        UpdatePending();
    }

    async Task ApplyAsync()
    {
        if (busy) return;
        var changes = tweaks
            .Where(t => !t.IsAction && desired[t] != current[t] && (desired[t] || t.Revert != null))
            .ToList();
        if (changes.Count == 0)
        {
            Toast.Pop(this, "Нет изменений для применения", Tone.Info);
            return;
        }

        var risky = changes.Where(t => t.Risky && desired[t]).Select(t => "• " + t.Name).ToList();
        string msg = $"Будет изменено настроек: {changes.Count}.";
        if (risky.Count > 0) msg += "\n\nСреди них рискованные:\n" + string.Join("\n", risky);
        if (!Dialog.Confirm(this, "Применить изменения?", msg, "Применить", "Отмена", risky.Count > 0 ? Tone.Warning : Tone.Info))
            return;

        var errors = new List<string>();
        bool makeRestore = restoreToggle.Checked;
        bool restart = changes.Any(t => t.NeedsRestart);
        await RunBusy("Применение…", () =>
        {
            if (makeRestore)
            {
                SetStatus("Точка восстановления…");
                try { CreateRestorePoint(); } catch (Exception ex) { errors.Add("Точка восстановления: " + ex.Message); }
            }
            int n = 0;
            var session = Journal.StartSession();
            foreach (var t in changes)
            {
                SetStatus($"[{++n}/{changes.Count}] {t.Name}");
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
            Toast.Pop(this, $"Применено изменений: {changes.Count}");
        if (restart) Toast.Pop(this, "Часть настроек заработает после перезагрузки", Tone.Warning);

        if (Dialog.Confirm(this, "Перезапустить Проводник?",
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

    void SaveProfile()
    {
        using var dlg = new SaveFileDialog { Filter = "Профиль WinTweaker|*.txt", FileName = "profile.txt" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        File.WriteAllLines(dlg.FileName, tweaks.Where(t => !t.IsAction && desired[t]).Select(t => t.Name));
        Toast.Pop(this, "Профиль сохранён");
    }

    void LoadProfile()
    {
        using var dlg = new OpenFileDialog { Filter = "Профиль WinTweaker|*.txt" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        var names = File.ReadAllLines(dlg.FileName).Select(s => s.Trim()).ToHashSet();
        foreach (var t in tweaks.Where(t => !t.IsAction)) desired[t] = names.Contains(t.Name);
        Build();
        UpdatePending();
        Toast.Pop(this, "Профиль загружен — нажмите «Применить»", Tone.Info);
    }

    void SetStatus(string text) => BeginInvoke(() =>
    {
        status.Text = text;
        status.ForeColor = Theme.Accent;
    });

    async Task<bool> RunBusy(string text, Action work)
    {
        busy = true;
        progress.Running = true;
        status.Text = text;
        status.ForeColor = Theme.Accent;
        UseWaitCursor = true;
        content.Enabled = false;
        try { await Task.Run(work); return true; }
        catch (Exception ex)
        {
            Dialog.Info(this, "Ошибка", ex.Message, Tone.Error);
            return false;
        }
        finally
        {
            content.Enabled = true;
            UseWaitCursor = false;
            progress.Running = false;
            busy = false;
            UpdatePending();
        }
    }
}
