namespace WinTweaker;

/// <summary>Страница «Игровой центр»: игры и пресеты, игровой режим, готовность, пинг, трассировка, инструменты.</summary>
public partial class MainForm
{
    const string GamesHub = "Игровой центр";

    ModeBanner? gameBanner;
    LibraryWidget? libWidget;
    BgAppsWidget? bgWidget;
    List<Readiness.Check>? readyCache;
    readonly Dictionary<int, List<GameNet.Result>> pingCache = new();
    int pingMode;

    /// <summary>Подписки игрового центра (один раз из конструктора).</summary>
    void InitGames()
    {
        GameMode.Init();
        GameWatcher.Start();
        GameMode.Changed += () => { if (gameBanner is { IsDisposed: false }) gameBanner.Sync(); };
        GameLibrary.Changed += () =>
        {
            if (libWidget is not { IsDisposed: false }) return;
            libWidget.SetData(GameLibrary.Games);
            if (selected == GamesHub) Relayout();
        };
        GameWatcher.Started += _ =>
        {
            gameBanner?.Sync();
            libWidget?.Invalidate(false);
        };
        GameWatcher.Ended += (run, rep) =>
        {
            if (rep != null)
                tray?.Balloon($"{run.Name} · {GameUI.Duration(rep.Duration)}",
                    $"Пинг {rep.Avg:0} мс ±{rep.Jitter:0}, потери {rep.Loss:0.#}%. {rep.Verdict().Text}");
            if (selected == GamesHub && !ContainsFocus) Build();
            else { gameBanner?.Sync(); libWidget?.Invalidate(false); }
        };
    }

    void BuildGames()
    {
        var cv = view.Canvas;
        cv.Controls.Add(new Header(GamesHub, "пресеты для игр, игровой режим, пинг до серверов и проверка перед матчем"));

        // ── игровой режим ──
        var banner = new ModeBanner();
        banner.Switch.Click += async (_, _) => await ToggleGameMode();
        banner.Auto.CheckedChanged += (_, _) => { GameWatcher.Enabled = banner.Auto.Checked; banner.Invalidate(false); };
        cv.Controls.Add(banner);
        gameBanner = banner;

        // ── готовность ──
        var ready = new ReadyWidget();
        ready.Run.Click += async (_, _) => await RunReady(ready);
        ready.FixAll.Click += (_, _) => FixChecks(ready.Data.Where(c => c.Tweaks != null && c.Level is Readiness.Level.Warn or Readiness.Level.Bad));
        ready.FixClicked += c =>
        {
            if (c.Tweaks != null) FixChecks(new[] { c });
            else if (c.Action == "close" && bgWidget is { IsDisposed: false } bg)
            {
                view.ScrollTo(bg.BasePos.Y - 20);
                Toast.Pop(this, "Отметьте лишнее и нажмите «Закрыть отмеченные»", Tone.Info);
            }
        };
        if (readyCache != null) ready.SetData(readyCache);
        cv.Controls.Add(ready);

        // ── библиотека ──
        var lib = new LibraryWidget();
        lib.SetData(GameLibrary.Games);
        lib.GameClicked += OpenGame;
        lib.Add.Click += async (_, _) => await AddGame();
        lib.Rescan.Click += async (_, _) => { await GameLibrary.ScanAsync(); Toast.Pop(this, $"Найдено игр: {GameLibrary.Games.Count}", Tone.Info); };
        cv.Controls.Add(lib);
        libWidget = lib;
        if (!GameLibrary.Scanned) _ = GameLibrary.ScanAsync();

        // ── пинг и трассировка ──
        var ping = new PingWidget();
        var trace = new TraceWidget();
        ping.Mode.Value = pingMode;
        if (pingCache.TryGetValue(pingMode, out var cached)) ping.SetData(cached);
        ping.Mode.Changed += m =>
        {
            pingMode = m;
            ping.SetData(pingCache.TryGetValue(m, out var c) ? c : null);
        };
        ping.Run.Click += async (_, _) => await RunPing(ping);
        ping.Trace.Click += async (_, _) => { if (ping.Best is { } b) await RunTrace(trace, b.Region.Host); };
        trace.Run.Click += async (_, _) =>
        {
            var host = Prompt.Ask(this, "Трассировка", "IP или адрес сервера", ping.Best?.Region.Host ?? GameMode.Target);
            if (!string.IsNullOrWhiteSpace(host)) await RunTrace(trace, host.Trim());
        };
        trace.Resized += Relayout;
        cv.Controls.Add(ping);
        cv.Controls.Add(trace);

        // ── что делает игровой режим ──
        var opts = new OptionsCard("Что делает игровой режим");
        void Opt(string key, string title, string sub) => opts.Add(title, sub, () => GameMode.Opt(key), on => GameMode.SetOpt(key, on));
        Opt("toasts", "Без уведомлений", "Всплывающие уведомления Windows и приложений не вылезают поверх игры");
        Opt("anim", "Без анимаций WinTweak", "Окно программы не тратит ни кадра на эффекты");
        Opt("updates", "Пауза загрузки обновлений", "Останавливаются Центр обновления и «Оптимизация доставки» — канал только для игры");
        Opt("power", "Схема «Высокая производительность»", "На время игры, потом возвращается прежняя");
        Opt("monitor", "Монитор соединения", "Пинг до роутера и до интернета каждую секунду, после игры — отчёт");
        Opt("close", "Закрывать фоновые программы", "Отмеченные в «Фоновых программах»: торренты, облака и т.д.");
        Opt("relaunch", "Запускать их снова после игры", "Мессенджеры, облака и торренты; браузеры — нет");
        Opt("wifi", "Wi-Fi: без фонового поиска сетей", "Убирает скачки пинга раз в минуту. Подключение остаётся, после игры поиск включается");
        var target = new FlatBtn("\uE774", GameMode.Target);
        target.Click += (_, _) =>
        {
            var v = Prompt.Ask(this, "Цель для пинга", "Куда меряется пинг в мониторе и проверке готовности (IP или адрес)", GameMode.Target);
            if (string.IsNullOrWhiteSpace(v)) return;
            GameMode.Target = v.Trim();
            target.Text = GameMode.Target;
            target.Width = TextRenderer.MeasureText(target.Text, Theme.Semi).Width + 46;
            target.Parent?.PerformLayout();
        };
        opts.AddCustom("Цель для пинга", "Для игр Valve — лучший ретранслятор из «Пинга до серверов»", target);
        cv.Controls.Add(opts);

        // ── фоновые программы ──
        var bgw = new BgAppsWidget();
        bgw.Resized += Relayout;
        bgw.RefreshBtn.Click += async (_, _) => await LoadBgApps(bgw);
        bgw.CloseNow.Click += async (_, _) => await CloseBgApps(bgw);
        cv.Controls.Add(bgw);
        bgWidget = bgw;
        _ = LoadBgApps(bgw);

        // ── видеодрайвер ──
        var quick = new ActionTile("\uE7F4", "Быстрый сброс драйвера", "Win+Ctrl+Shift+B — экран мигнёт");
        quick.Click += (_, _) => { GpuReset.Quick(); Toast.Pop(this, "Видеодрайвер перезапущен", Tone.Info); };
        var full = new ActionTile("\uE7E8", "Перезапуск видеокарты", "Если завис драйвер или чёрный экран");
        full.Click += async (_, _) => await FullGpuReset();
        cv.Controls.Add(new ActionsWidget("Видеодрайвер", quick, full));

        // ── кэш шейдеров ──
        var shader = new ShaderWidget();
        shader.Clean.Click += async (_, _) => await CleanShaders(shader);
        cv.Controls.Add(shader);
        _ = LoadShaders(shader);

        // ── отчёты ──
        var reports = new ReportsWidget(Matches.Load());
        reports.ReportClicked += r => { using var d = new ReportDialog(r); d.ShowDialog(this); };
        cv.Controls.Add(reports);

        // ── ссылки ──
        var sys = new ActionTile("\uE945", "Системные настройки для игр", "Game DVR, мышь, сеть для игр");
        sys.Click += (_, _) => SelectCategory("Игры");
        var preset = new ActionTile("\uE7FC", "Пресет «Игровой»", "Отметить всё для FPS разом");
        preset.Click += (_, _) => { var p = Presets.All.FirstOrDefault(x => x.Name == "Игровой"); if (p != null) ApplyPreset(p); };
        cv.Controls.Add(new ActionsWidget("Ещё", sys, preset));
    }

    // ───────── Игровой режим ─────────

    async Task ToggleGameMode()
    {
        if (GameMode.Active)
        {
            var rep = await Task.Run(() => GameMode.Exit(GameMode.Reason ?? "Вручную"));
            Toast.Pop(this, rep != null && rep.Duration.TotalSeconds >= 60
                ? $"Игровой режим выключен · пинг {rep.Avg:0} мс ±{rep.Jitter:0} — отчёт сохранён"
                : "Игровой режим выключен — всё вернулось как было", Tone.Info);
        }
        else
        {
            await Task.Run(() => GameMode.Enter("включён вручную"));
            Toast.Pop(this, "Игровой режим включён");
        }
        gameBanner?.Sync();
    }

    // ───────── Готовность ─────────

    async Task RunReady(ReadyWidget w)
    {
        if (w.Running) return;
        w.Begin();
        var list = await Task.Run(() => Readiness.Run(tweaks));
        readyCache = list;
        if (w.IsDisposed) return;
        w.SetData(list);
        Relayout();
    }

    void FixChecks(IEnumerable<Readiness.Check> checks)
    {
        int n = MarkTweaks(checks.SelectMany(c => c.Tweaks ?? Array.Empty<string>()));
        Toast.Pop(this, n == 0 ? "Всё уже отмечено — нажмите «Применить»" : $"Отмечено {n} — проверьте и нажмите «Применить»", n == 0 ? Tone.Info : Tone.Success);
    }

    /// <summary>Отметить переключатели по названиям (применяет пользователь). Возвращает, сколько отмечено.</summary>
    int MarkTweaks(IEnumerable<string> names)
    {
        int n = 0;
        foreach (var name in names.Distinct())
        {
            var t = tweaks.FirstOrDefault(x => x.Name == name && x.IsToggle);
            if (t == null || current.GetValueOrDefault(t) || desired.GetValueOrDefault(t)) continue;
            desired[t] = true;
            n++;
        }
        if (n > 0) UpdatePending();
        return n;
    }

    bool IsOnOrMarked(string name)
    {
        var t = tweaks.FirstOrDefault(x => x.Name == name);
        return t != null && (current.GetValueOrDefault(t) || desired.GetValueOrDefault(t));
    }

    // ───────── Библиотека ─────────

    void OpenGame(InstalledGame g)
    {
        using var d = new GameDialog(g, IsOnOrMarked) { MarkSystem = MarkTweaks };
        d.ShowDialog(this);
        if (!d.Changed) return;
        if (g.Custom && g.Exe != null && !File.Exists(g.Exe)) { _ = GameLibrary.ScanAsync(); return; }
        Task.Run(g.CheckTuned).ContinueWith(_ => BeginInvoke(() => libWidget?.Invalidate(false)));
        if (g.Custom) _ = GameLibrary.ScanAsync(); // «Убрать» — обновить список
    }

    async Task AddGame()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Exe игры",
            Filter = "Программы (*.exe)|*.exe",
            InitialDirectory = Directory.Exists(@"C:\Games") ? @"C:\Games" : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        GameLibrary.AddCustom(ofd.FileName);
        await GameLibrary.ScanAsync();
        var g = GameLibrary.Games.FirstOrDefault(x => string.Equals(x.Exe, ofd.FileName, StringComparison.OrdinalIgnoreCase));
        if (g != null) OpenGame(g);
    }

    // ───────── Пинг и трассировка ─────────

    async Task RunPing(PingWidget w)
    {
        if (w.Running) return;
        int mode = w.Mode.Value;
        List<GameNet.Region>? regions = null;
        if (mode == 2)
        {
            var host = Prompt.Ask(this, "Свой адрес", "IP или адрес игрового сервера", AppSettings.Get("gm.custom", ""));
            if (string.IsNullOrWhiteSpace(host)) return;
            host = host.Trim();
            AppSettings.Set("gm.custom", host);
            regions = new() { new GameNet.Region("custom", host, host, false) };
        }
        w.Begin();
        try
        {
            if (mode == 0) regions = await GameNet.ValveAsync();
            else if (mode == 1) regions = GameNet.Aws.ToList();
            if (regions == null || regions.Count == 0)
            {
                if (!w.IsDisposed) w.SetData(null, "Не удалось получить список регионов Valve — проверьте интернет и попробуйте ещё раз");
                return;
            }
            var list = regions;
            var res = await Task.Run(() => GameNet.MeasureAsync(list, mode == 2 ? 20 : 4,
                (d, t) => BeginInvoke(() => { if (!w.IsDisposed) w.Progress(d, t); })));
            pingCache[mode] = res;
            if (mode == 0 && res.FirstOrDefault(r => r.Ms != null) is { } best) AppSettings.Set("gm.valveBest", best.Region.Host);
            if (!w.IsDisposed && w.Mode.Value == mode) w.SetData(res);
        }
        catch (Exception ex)
        {
            if (!w.IsDisposed) w.SetData(null, ex.Message);
        }
    }

    async Task RunTrace(TraceWidget w, string host)
    {
        if (w.Running) return;
        w.Begin(host);
        Relayout();
        view.ScrollTo(w.BasePos.Y - 20);
        try
        {
            await Task.Run(() => GameNet.TraceAsync(host, h => BeginInvoke(() => { if (!w.IsDisposed) w.Hop(h); }), CancellationToken.None));
            if (!w.IsDisposed) w.End();
        }
        catch (Exception ex)
        {
            if (!w.IsDisposed) w.End(ex.Message);
        }
    }

    // ───────── Фоновые программы ─────────

    static async Task LoadBgApps(BgAppsWidget w)
    {
        var list = await Task.Run(BackgroundApps.Running);
        if (!w.IsDisposed) w.SetData(list);
    }

    async Task CloseBgApps(BgAppsWidget w)
    {
        var apps = w.Checked.ToList();
        if (apps.Count == 0) { Toast.Pop(this, "Отметьте программы в списке", Tone.Info); return; }
        if (!Dialog.Confirm(this, "Закрыть программы?", string.Join(", ", apps.Select(a => a.Name)) +
                "\n\nСначала вежливо, как крестиком; через 3 секунды — принудительно. Несохранённое в них может пропасть.",
                "Закрыть", "Отмена", Tone.Warning)) return;
        await Task.Run(() => BackgroundApps.Close(apps, relaunch: false));
        Toast.Pop(this, $"Закрыто: {apps.Count}");
        await LoadBgApps(w);
    }

    // ───────── Видеодрайвер и шейдеры ─────────

    async Task FullGpuReset()
    {
        if (!Dialog.Confirm(this, "Перезапустить видеокарту?",
                "Экран погаснет на несколько секунд, окна могут перестроиться. Сохраните работу и закройте игры — им перезапуск видеокарты не понравится.",
                "Перезапустить", "Отмена", Tone.Warning)) return;
        string r = "";
        bool ok = await RunBusy("Перезапуск видеокарты…", () => r = GpuReset.Full());
        if (ok) Toast.Pop(this, r.Contains("ok") ? "Видеокарта перезапущена" : "Видеокарты не найдены", r.Contains("ok") ? Tone.Success : Tone.Warning);
    }

    static async Task LoadShaders(ShaderWidget w)
    {
        var data = await Task.Run(() => ShaderCache.Groups().Select(g => (g, ShaderCache.Size(g))).ToList());
        if (!w.IsDisposed) w.SetData(data);
    }

    async Task CleanShaders(ShaderWidget w)
    {
        if (w.Busy) return;
        if (w.Total == 0) { Toast.Pop(this, "Кэш шейдеров уже пуст", Tone.Info); return; }
        if (!Dialog.Confirm(this, "Очистить кэш шейдеров?", $"Будет удалено около {GameUI.Bytes(w.Total)}. Закройте игры — занятые файлы пропускаются. " +
                "Первые запуски игр после очистки будут дольше: кэш соберётся заново.", "Очистить", "Отмена", Tone.Warning)) return;
        w.Busy = true;
        w.Invalidate(false);
        long freed = await Task.Run(() => ShaderCache.Groups().Sum(ShaderCache.Clean));
        Toast.Pop(this, $"Освобождено {GameUI.Bytes(freed)}");
        await LoadShaders(w);
    }
}
