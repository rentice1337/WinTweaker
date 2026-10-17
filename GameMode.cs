using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinTweaker;

/// <summary>
/// Игровой режим «Не беспокоить»: на время игры — без уведомлений, без виджетов и анимаций WinTweak,
/// без загрузки обновлений, со схемой «Высокая производительность», по желанию — без фоновых программ
/// и без фонового поиска Wi-Fi сетей. После игры всё возвращается как было.
/// Исходное состояние сохраняется в файл до изменений: если программа или ПК выключатся во время игры,
/// при следующем запуске WinTweak всё вернёт.
/// </summary>
static class GameMode
{
    public static bool Active => state != null;
    public static string? Reason { get; private set; }
    public static DateTime Since { get; private set; }
    public static ConnMonitor? Monitor { get; private set; }
    public static event Action? Changed;
    static SynchronizationContext? ui;

    /// <summary>Из потока интерфейса при запуске: запомнить поток для событий и вернуть «хвосты» прошлого сеанса.</summary>
    public static void Init()
    {
        ui = SynchronizationContext.Current;
        Task.Run(Recover);
    }

    static void Notify()
    {
        if (ui != null) ui.Post(_ => Changed?.Invoke(), null); else Changed?.Invoke();
    }

    static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinTweaker", "gamemode.json");
    const string Push = @"HKCU\Software\Microsoft\Windows\CurrentVersion\PushNotifications";
    const string Noc = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    const string HP = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    sealed class State
    {
        public object? Toast { get; set; }          // прежнее ToastEnabled (null — не было)
        public object? NocToasts { get; set; }
        public bool ToastsChanged { get; set; }
        public string? Power { get; set; }
        public bool WuWasRunning { get; set; }
        public List<string> Wifi { get; set; } = new();
        public List<string> Relaunch { get; set; } = new();
        public bool Anim { get; set; }
    }
    static State? state;

    // ───── настройки (AppSettings) ─────
    public static bool Opt(string key) => AppSettings.Get("gm." + key, key is "close" or "wifi" ? "0" : "1") == "1";
    public static void SetOpt(string key, bool on) => AppSettings.Set("gm." + key, on ? "1" : "0");
    public static string Target
    {
        get => AppSettings.Get("gm.target", "1.1.1.1");
        set => AppSettings.Set("gm.target", value);
    }

    /// <summary>Включить. Вызывать из фонового потока (останавливаются службы, меняется схема питания).</summary>
    public static void Enter(string reason, string? monitorTarget = null)
    {
        if (state != null) return;
        var s = new State();
        Reason = reason;
        Since = DateTime.Now;

        if (Opt("toasts"))
        {
            s.ToastsChanged = true;
            s.Toast = Reg.Get(Push, "ToastEnabled");
            s.NocToasts = Reg.Get(Noc, "NOC_GLOBAL_SETTING_TOASTS_ENABLED");
        }
        if (Opt("power"))
            s.Power = Regex.Match(Shell.Run("powercfg", "/getactivescheme"), @"[0-9a-fA-F-]{36}").Value;
        if (Opt("updates"))
            s.WuWasRunning = Shell.Run("sc.exe", "query wuauserv").Contains("RUNNING");
        if (Opt("wifi"))
            s.Wifi = WifiInterfaces();
        s.Anim = Motion.UserOff;
        Save(s); // сначала запомнить, потом менять
        state = s;

        try
        {
            if (s.ToastsChanged)
            {
                RawSet(Push, "ToastEnabled", 0);
                RawSet(Noc, "NOC_GLOBAL_SETTING_TOASTS_ENABLED", 0);
            }
            if (Opt("widgets")) Widgets.Suspended = true;
            if (Opt("anim")) Motion.UserOff = true;
            if (s.Power != null && !s.Power.Equals(HP, StringComparison.OrdinalIgnoreCase) && !s.Power.Equals(Ultimate, StringComparison.OrdinalIgnoreCase))
            {
                if (!Shell.Run("powercfg", "/list").Contains(HP, StringComparison.OrdinalIgnoreCase))
                    Shell.Run("powercfg", $"-duplicatescheme {Ultimate} {HP}");
                Shell.Run("powercfg", $"/setactive {HP}");
            }
            if (Opt("updates"))
            {
                // загрузки обновлений и раздача через «Оптимизацию доставки» не съедят канал во время игры
                Shell.Run("net.exe", "stop wuauserv");
                Shell.Run("net.exe", "stop dosvc");
            }
            foreach (var w in s.Wifi) Shell.Run("netsh.exe", $"wlan set autoconfig enabled=no interface=\"{w}\"");
            if (Opt("close"))
            {
                s.Relaunch = BackgroundApps.CloseSelected(Opt("relaunch"));
                Save(s);
            }
            if (Opt("monitor")) Monitor = new ConnMonitor(monitorTarget ?? Target);
        }
        catch { }
        Notify();
    }

    /// <summary>Выключить и вернуть всё. Возвращает отчёт о соединении, если монитор работал.</summary>
    public static MatchReport? Exit(string game)
    {
        var s = state;
        if (s == null) return null;
        MatchReport? report = null;
        try { report = Monitor?.Stop(game); } catch { }
        Monitor = null;
        Restore(s);
        state = null;
        Reason = null;
        try { File.Delete(StateFile); } catch { }
        if (report != null && report.Duration.TotalSeconds >= 60) Matches.Add(report);
        Notify();
        return report;
    }

    static void Restore(State s)
    {
        try
        {
            if (s.ToastsChanged)
            {
                RawRestore(Push, "ToastEnabled", s.Toast);
                RawRestore(Noc, "NOC_GLOBAL_SETTING_TOASTS_ENABLED", s.NocToasts);
            }
        }
        catch { }
        Widgets.Suspended = false;
        Motion.UserOff = AppSettings.Get("anim", "1") == "0";
        try { if (!string.IsNullOrEmpty(s.Power)) Shell.Run("powercfg", $"/setactive {s.Power}"); } catch { }
        try { if (s.WuWasRunning) Shell.Run("net.exe", "start wuauserv"); } catch { }
        foreach (var w in s.Wifi)
            try { Shell.Run("netsh.exe", $"wlan set autoconfig enabled=yes interface=\"{w}\""); } catch { }
        foreach (var exe in s.Relaunch)
            try { if (File.Exists(exe)) Process.Start("explorer.exe", $"\"{exe}\""); } catch { } // через Проводник — без прав администратора
    }

    /// <summary>При запуске: если прошлый раз игровой режим не выключился (сбой, выключение ПК) — вернуть всё.</summary>
    public static void Recover()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            var s = JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile));
            File.Delete(StateFile);
            if (s != null) Restore(s);
        }
        catch { }
    }

    static void Save(State s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(s));
        }
        catch { }
    }

    // временные изменения — мимо журнала: они откатываются самим режимом
    static void RawSet(string path, string name, int v)
    {
        var (root, sub) = Reg.SplitPath(path);
        using var k = root.CreateSubKey(sub, true);
        k.SetValue(name, v, Microsoft.Win32.RegistryValueKind.DWord);
    }

    static void RawRestore(string path, string name, object? old)
    {
        var (root, sub) = Reg.SplitPath(path);
        using var k = root.CreateSubKey(sub, true);
        if (old == null) k.DeleteValue(name, false);
        else if (old is JsonElement je && je.TryGetInt32(out int j)) k.SetValue(name, j, Microsoft.Win32.RegistryValueKind.DWord);
        else if (old is int i) k.SetValue(name, i, Microsoft.Win32.RegistryValueKind.DWord);
    }

    static List<string> WifiInterfaces()
    {
        var list = new List<string>();
        try
        {
            // имя интерфейса и состояние «подключено» — без зависимости от языка: берём только подключённые с SSID
            string text = Shell.Run("netsh.exe", "wlan show interfaces");
            string? name = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                int c = line.IndexOf(':');
                if (c < 0) continue;
                string key = line[..c].Trim(), val = line[(c + 1)..].Trim();
                if (key is "Name" or "Имя") name = val;
                if (key == "SSID" && val != "" && name != null && !list.Contains(name)) list.Add(name);
            }
        }
        catch { }
        return list;
    }
}

/// <summary>Слежение за запуском игр: раз в 3 секунды — список процессов (в фоне), без хуков и драйверов.</summary>
static class GameWatcher
{
    public sealed record Running(string Name, string Exe, int Pid, GameInfo? Info, DateTime Since);

    public static Running? Current { get; private set; }
    public static event Action<Running>? Started;
    public static event Action<Running, MatchReport?>? Ended;

    static System.Threading.Timer? timer;
    static SynchronizationContext? ui;
    static int busy;

    public static bool Enabled
    {
        get => GameMode.Opt("auto");
        set { GameMode.SetOpt("auto", value); }
    }

    public static void Start()
    {
        if (timer != null) return;
        ui = SynchronizationContext.Current;
        timer = new System.Threading.Timer(_ => Tick(), null, 4000, 3000);
    }

    /// <summary>Имена exe, за которыми следим: пресеты + найденные игры.</summary>
    static Dictionary<string, (string Name, GameInfo? Info)> Watched()
    {
        var map = new Dictionary<string, (string, GameInfo?)>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in GameCatalog.All)
            foreach (var e in g.Exes) map[Path.GetFileNameWithoutExtension(e)] = (g.Name, g);
        foreach (var g in GameLibrary.Games.Where(g => g.Exe != null))
            map.TryAdd(Path.GetFileNameWithoutExtension(g.Exe!), (g.Title, g.Info));
        return map;
    }

    static void Tick()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        try
        {
            if (Current != null)
            {
                bool alive;
                try { using var p = Process.GetProcessById(Current.Pid); alive = !p.HasExited; }
                catch { alive = false; }
                if (!alive)
                {
                    var ended = Current;
                    Current = null;
                    var report = GameMode.Active && GameMode.Reason == ended.Name ? GameMode.Exit(ended.Name) : null;
                    Post(() => Ended?.Invoke(ended, report));
                }
                return;
            }
            if (!Enabled) return;
            var watched = Watched();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (!watched.TryGetValue(p.ProcessName, out var g)) continue;
                    string exe = "";
                    try { exe = p.MainModule?.FileName ?? ""; } catch { }
                    var run = new Running(g.Name, exe, p.Id, g.Info, DateTime.Now);
                    Current = run;
                    if (!GameMode.Active)
                    {
                        string? target = g.Info?.Servers == ServerNet.Valve ? AppSettings.Get("gm.valveBest", "") : null;
                        GameMode.Enter(g.Name, string.IsNullOrEmpty(target) ? null : target);
                    }
                    Post(() => Started?.Invoke(run));
                    break;
                }
            }
        }
        catch { }
        finally { Volatile.Write(ref busy, 0); }
    }

    static void Post(Action a)
    {
        if (ui != null) ui.Post(_ => a(), null); else a();
    }
}

/// <summary>Фоновые программы, которые стоит закрыть перед игрой: браузеры, торренты, облака, мессенджеры.</summary>
static class BackgroundApps
{
    public sealed record App(string Key, string Name, string[] Procs, bool Relaunch, bool Default, string Why, string? Company = null);

    public static readonly App[] All =
    {
        new("qbit", "qBittorrent", new[] { "qbittorrent" }, true, true, "торрент — забирает канал, растёт пинг"),
        new("utorrent", "µTorrent", new[] { "uTorrent" }, true, true, "торрент — забирает канал, растёт пинг"),
        new("bittorrent", "BitTorrent", new[] { "BitTorrent" }, true, true, "торрент — забирает канал, растёт пинг"),
        new("onedrive", "OneDrive", new[] { "OneDrive" }, true, true, "синхронизация — диск и сеть"),
        new("yadisk", "Яндекс Диск", new[] { "YandexDisk2" }, true, true, "синхронизация — диск и сеть"),
        new("gdrive", "Google Диск", new[] { "GoogleDriveFS" }, true, true, "синхронизация — диск и сеть"),
        new("dropbox", "Dropbox", new[] { "Dropbox" }, true, true, "синхронизация — диск и сеть"),
        new("adobe", "Adobe Creative Cloud", new[] { "Creative Cloud", "CCXProcess", "CCLibrary", "AdobeIPCBroker" }, true, true, "фоновые службы и обновления"),
        new("chrome", "Google Chrome", new[] { "chrome" }, false, false, "браузер — память и фоновые вкладки"),
        new("edge", "Microsoft Edge", new[] { "msedge" }, false, false, "браузер — память и фоновые вкладки"),
        new("yandex", "Яндекс Браузер", new[] { "browser" }, false, false, "браузер — память и фоновые вкладки", "Yandex"),
        new("firefox", "Firefox", new[] { "firefox" }, false, false, "браузер — память и фоновые вкладки"),
        new("opera", "Opera / Opera GX", new[] { "opera" }, false, false, "браузер — память и фоновые вкладки"),
        new("telegram", "Telegram", new[] { "Telegram" }, true, false, "мессенджер"),
        new("whatsapp", "WhatsApp", new[] { "WhatsApp" }, true, false, "мессенджер"),
        new("discord", "Discord", new[] { "Discord" }, true, false, "голосовой чат — не закрывайте, если играете с друзьями"),
        new("spotify", "Spotify", new[] { "Spotify" }, true, false, "музыка"),
        new("teams", "Microsoft Teams", new[] { "ms-teams", "Teams" }, true, false, "мессенджер"),
        new("zoom", "Zoom", new[] { "Zoom" }, true, false, "видеосвязь"),
        new("epic", "Epic Games Launcher", new[] { "EpicGamesLauncher" }, true, false, "лаунчер в фоне (не закрывайте для игр Epic)"),
    };

    public static HashSet<string> Selected
    {
        get
        {
            var s = AppSettings.Get("gm.apps", "");
            return s == "" ? All.Where(a => a.Default).Select(a => a.Key).ToHashSet() : s.Split(',').ToHashSet();
        }
        set => AppSettings.Set("gm.apps", value.Count == 0 ? "-" : string.Join(",", value));
    }

    /// <summary>Что из списка запущено сейчас и сколько памяти занимает.</summary>
    public static List<(App App, long Bytes)> Running()
    {
        var result = new List<(App, long)>();
        var procs = Process.GetProcesses();
        try
        {
            foreach (var a in All)
            {
                long bytes = 0;
                bool any = false;
                foreach (var p in procs.Where(p => a.Procs.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)))
                {
                    if (a.Company != null && !CompanyIs(p, a.Company)) continue;
                    any = true;
                    try { bytes += p.WorkingSet64; } catch { }
                }
                if (any) result.Add((a, bytes));
            }
        }
        finally { foreach (var p in procs) p.Dispose(); }
        return result;
    }

    static bool CompanyIs(Process p, string company)
    {
        try { return FileVersionInfo.GetVersionInfo(p.MainModule!.FileName).CompanyName?.Contains(company, StringComparison.OrdinalIgnoreCase) == true; }
        catch { return false; }
    }

    public static List<string> CloseSelected(bool relaunch) => Close(All.Where(a => Selected.Contains(a.Key)), relaunch);

    /// <summary>Закрыть: сначала вежливо (как крестиком), через 3 с — принудительно. Возвращает exe для перезапуска.</summary>
    public static List<string> Close(IEnumerable<App> apps, bool relaunch)
    {
        var again = new List<string>();
        var victims = new List<Process>();
        foreach (var a in apps)
        {
            var procs = a.Procs.SelectMany(Process.GetProcessesByName)
                .Where(p => a.Company == null || CompanyIs(p, a.Company)).ToList();
            if (procs.Count == 0) continue;
            if (relaunch && a.Relaunch)
            {
                string? exe = null;
                foreach (var p in procs) { try { exe ??= p.MainModule?.FileName; } catch { } }
                if (exe != null) again.Add(exe);
            }
            if (a.Key == "onedrive")
            {
                // у OneDrive есть штатное завершение
                try { Process.Start(new ProcessStartInfo(procs[0].MainModule!.FileName, "/shutdown") { UseShellExecute = false }); } catch { }
            }
            foreach (var p in procs)
            {
                try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { }
                victims.Add(p);
            }
        }
        var deadline = DateTime.Now.AddSeconds(3);
        foreach (var p in victims)
        {
            try
            {
                var left = deadline - DateTime.Now;
                if (left > TimeSpan.Zero) p.WaitForExit((int)left.TotalMilliseconds);
                if (!p.HasExited) p.Kill(true);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return again;
    }
}

/// <summary>Сброс видеодрайвера: быстрый (как Win+Ctrl+Shift+B) и полный перезапуск видеокарты.</summary>
static class GpuReset
{
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    /// <summary>Штатная комбинация Windows Win+Ctrl+Shift+B: экран мигнёт, драйвер перезапустится, игры не закрываются.</summary>
    public static void Quick()
    {
        byte[] keys = { 0x5B /* LWIN */, 0x11 /* CTRL */, 0x10 /* SHIFT */, 0x42 /* B */ };
        foreach (var k in keys) keybd_event(k, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        foreach (var k in keys.Reverse()) keybd_event(k, 0, 2 /* KEYUP */, UIntPtr.Zero);
    }

    /// <summary>Перезапуск устройства видеокарты (pnputil /restart-device). Экран погаснет на несколько секунд.</summary>
    public static string Full() => Shell.PS(
        "$d = Get-PnpDevice -Class Display -Status OK -ErrorAction SilentlyContinue; " +
        "if (-not $d) { 'нет видеокарт'; return }; " +
        "foreach ($x in $d) { $r = pnputil /restart-device \"$($x.InstanceId)\" 2>&1; " +
        "if ($LASTEXITCODE -ne 0) { Disable-PnpDevice -InstanceId $x.InstanceId -Confirm:$false; Start-Sleep 2; Enable-PnpDevice -InstanceId $x.InstanceId -Confirm:$false } }; 'ok'");
}

/// <summary>Кэш шейдеров видеодрайверов и DirectX.</summary>
static class ShaderCache
{
    public sealed record Group(string Name, string[] Dirs);

    public static List<Group> Groups()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string low = Path.Combine(Path.GetDirectoryName(local)!, "LocalLow");
        string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var list = new List<Group>
        {
            new("NVIDIA", new[]
            {
                Path.Combine(local, "NVIDIA", "DXCache"), Path.Combine(local, "NVIDIA", "GLCache"),
                Path.Combine(low, "NVIDIA", "PerDriverVersion", "DXCache"), Path.Combine(low, "NVIDIA", "PerDriverVersion", "GLCache"),
                Path.Combine(pd, "NVIDIA Corporation", "NV_Cache"),
            }),
            new("AMD", new[]
            {
                Path.Combine(local, "AMD", "DxCache"), Path.Combine(local, "AMD", "DxcCache"),
                Path.Combine(local, "AMD", "GLCache"), Path.Combine(local, "AMD", "VkCache"),
            }),
            new("Intel", new[] { Path.Combine(low, "Intel", "ShaderCache"), Path.Combine(local, "Intel", "ShaderCache") }),
            new("DirectX", new[] { Path.Combine(local, "D3DSCache") }),
        };
        var steam = GameLibrary.SteamPath();
        if (steam != null) list.Add(new("Steam (Vulkan / OpenGL)", new[] { Path.Combine(steam, "steamapps", "shadercache") }));
        return list;
    }

    public static long Size(Group g)
    {
        long sum = 0;
        foreach (var d in g.Dirs.Where(Directory.Exists))
            try
            {
                foreach (var f in new DirectoryInfo(d).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                    sum += f.Length;
            }
            catch { }
        return sum;
    }

    /// <summary>Удалить содержимое. Занятые файлы (открытая игра) пропускаются. Возвращает освобождённые байты.</summary>
    public static long Clean(Group g)
    {
        long freed = 0;
        foreach (var d in g.Dirs.Where(Directory.Exists))
        {
            var di = new DirectoryInfo(d);
            foreach (var f in di.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList())
                try { long n = f.Length; f.Delete(); freed += n; } catch { }
            foreach (var sub in di.EnumerateDirectories("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                         .OrderByDescending(x => x.FullName.Length).ToList())
                try { if (!sub.EnumerateFileSystemInfos().Any()) sub.Delete(); } catch { }
        }
        return freed;
    }
}

/// <summary>Проверка «готов ли ПК к игре»: светофор с советами и исправлением в один клик.</summary>
static class Readiness
{
    public enum Level { Good, Warn, Bad, Info }

    public sealed record Check(string Title, string Detail, Level Level, string[]? Tweaks = null, string? Action = null);

    public static List<Check> Run(IReadOnlyList<Tweak> tweaks)
    {
        var list = new List<Check>();
        bool On(string name) { try { return tweaks.FirstOrDefault(t => t.Name == name)?.IsApplied() == true; } catch { return false; } }

        // питание
        var ps = SystemInformation.PowerStatus;
        string scheme = Shell.Run("powercfg", "/getactivescheme");
        bool high = scheme.Contains("8c5e7fda", StringComparison.OrdinalIgnoreCase) || scheme.Contains("e9a42b02", StringComparison.OrdinalIgnoreCase);
        if (SysInfo.IsLaptop && ps.PowerLineStatus == PowerLineStatus.Offline)
            list.Add(new("Ноутбук от батареи", "Процессор и видеокарта снижают частоты — подключите зарядку", Level.Bad));
        else if (high)
            list.Add(new("Схема питания", "«Высокая производительность» — процессор не сбрасывает частоту", Level.Good));
        else
            list.Add(new("Схема питания", "Сбалансированная: в начале боя частоты поднимаются с задержкой", Level.Warn, new[] { "Схема питания «Высокая производительность»" }));

        // запись и игровой режим
        if (On("Отключить фоновую запись (Game DVR)"))
            list.Add(new("Фоновая запись", "Выключена — FPS не тратится на запись последних минут", Level.Good));
        else
            list.Add(new("Фоновая запись (Game DVR)", "Включена — постоянно пишет видео в фоне и ест кадры", Level.Warn, new[] { "Отключить фоновую запись (Game DVR)" }));
        var gm = Reg.Get(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled");
        if (gm == null || gm.ToString() == "1")
            list.Add(new("Игровой режим Windows", "Включён: обновления не ставятся во время игры", Level.Good));
        else
            list.Add(new("Игровой режим Windows", "Выключен", Level.Warn, new[] { "Игровой режим" }));

        var accel = Reg.Get(@"HKCU\Control Panel\Mouse", "MouseSpeed")?.ToString();
        if (accel == "0") list.Add(new("Ускорение мыши", "Выключено — прицел ведёт себя одинаково при любой скорости", Level.Good));
        else list.Add(new("Ускорение мыши", "Включено — в шутерах мешает мышечной памяти", Level.Warn, new[] { "Отключить ускорение мыши" }));

        // нагрузка, память, сеть — по последним секундам сборщика
        var snap = Sampler.Current;
        double cpu = snap.CpuHist.TakeLast(10).DefaultIfEmpty(snap.Cpu).Average();
        if (cpu > 30) list.Add(new("Фоновая нагрузка", $"Процессор уже занят на {cpu:0}% без игры — закройте лишнее", Level.Bad, null, "close"));
        else if (cpu > 12) list.Add(new("Фоновая нагрузка", $"Процессор занят на {cpu:0}% — что-то работает в фоне", Level.Warn, null, "close"));
        else list.Add(new("Фоновая нагрузка", $"Процессор свободен ({cpu:0}%)", Level.Good));

        double free = snap.RamTotalGb - snap.RamUsedGb;
        if (free < 2) list.Add(new("Свободная память", $"Всего {free:0.0} ГБ — игра будет подгружать с диска и фризить", Level.Bad, null, "close"));
        else if (free < 4) list.Add(new("Свободная память", $"{free:0.0} ГБ — тяжёлым играм может не хватить", Level.Warn, null, "close"));
        else list.Add(new("Свободная память", $"{free:0.0} ГБ свободно", Level.Good));

        double down = snap.DownHist.TakeLast(10).DefaultIfEmpty(0).Average();
        if (down > 1024 * 1024) list.Add(new("Загрузки в фоне", $"Что-то качает со скоростью {Sampler.Speed(down)} — торрент или обновление?", Level.Warn, null, "close"));

        // пинг
        try
        {
            var ip = GameNet.Resolve(GameMode.Target).GetAwaiter().GetResult();
            var times = new List<double>();
            int lost = 0;
            for (int i = 0; i < 10 && ip != null; i++)
            {
                var r = GameNet.IcmpPing(ip, 1000).GetAwaiter().GetResult();
                if (r is double v) times.Add(v); else lost++;
                Thread.Sleep(150);
            }
            if (ip == null || times.Count == 0)
                list.Add(new("Интернет", "Нет ответа от " + GameMode.Target, Level.Bad));
            else
            {
                double avg = times.Average(), jit = GameNet.Jitter(times);
                string d = $"{avg:0} мс до {GameMode.Target}, разброс {jit:0} мс" + (lost > 0 ? $", потеряно {lost} из 10" : "");
                var lvl = lost > 1 || jit > 20 ? Level.Bad : lost > 0 || jit > 8 || avg > 80 ? Level.Warn : Level.Good;
                list.Add(new("Соединение", d, lvl));
            }
        }
        catch { }
        if (GameNet.WifiName() is string wifi)
            list.Add(new("Wi-Fi", $"Подключение по «{wifi}» — кабель даёт ровнее пинг. В игровом режиме можно выключить фоновый поиск сетей", Level.Info));

        // обновления, аптайм, диск, файл подкачки
        if (Reg.KeyExists(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            list.Add(new("Обновление ждёт перезагрузки", "Перезагрузитесь до игры — иначе Windows может начать установку в самый неподходящий момент", Level.Warn));
        if (SysInfo.UptimeDays > 7)
            list.Add(new("Давно без перезагрузки", $"{SysInfo.UptimeDays:0} дн. — перезагрузка освободит память и сбросит «залипшие» драйверы", Level.Warn));
        var (fr, tot) = SysInfo.SystemDrive();
        if (tot > 0 && fr / tot < 0.1)
            list.Add(new("Мало места на диске C:", $"Свободно {fr:0} ГБ — кэшам шейдеров и файлу подкачки нужно место", Level.Warn));
        var pf = Reg.Get(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "PagingFiles") as string[];
        if (pf != null && pf.All(string.IsNullOrWhiteSpace))
            list.Add(new("Файл подкачки отключён", "Tarkov, Rust и другие тяжёлые игры при нехватке памяти вылетают — включите «по выбору системы»", Level.Warn));

        // драйвер видеокарты
        try
        {
            var lines = Shell.PS("Get-CimInstance Win32_VideoController | Where-Object { $_.Status -eq 'OK' } | " +
                                 "ForEach-Object { $_.Name + '|' + $(if ($_.DriverDate) { $_.DriverDate.ToString('yyyy-MM-dd') } else { '' }) }")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var l in lines)
            {
                var p = l.Split('|');
                if (p[0].Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || p[0].Contains("Базовый", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(new("Нет драйвера видеокарты", "Работает стандартный драйвер Microsoft — установите драйвер NVIDIA/AMD/Intel", Level.Bad));
                    continue;
                }
                if (p[0].Contains("Remote", StringComparison.OrdinalIgnoreCase) || p.Length < 2 || !DateTime.TryParse(p[1], out var date)) continue;
                int days = (int)(DateTime.Now - date).TotalDays;
                if (days > 240)
                    list.Add(new("Драйвер видеокарты", $"{p[0]}: драйвер от {date:d MMMM yyyy} — новые игры оптимизируются в свежих версиях", Level.Warn));
                else
                    list.Add(new("Драйвер видеокарты", $"{p[0]}: от {date:d MMMM yyyy}", Level.Good));
            }
        }
        catch { }

        return list.OrderBy(c => c.Level switch { Level.Bad => 0, Level.Warn => 1, Level.Info => 2, _ => 3 }).ToList();
    }
}
