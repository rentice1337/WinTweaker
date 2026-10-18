using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinTweaker;

/// <summary>Найденная на компьютере игра.</summary>
sealed class InstalledGame
{
    public required string Name { get; init; }
    public required string Source { get; init; }   // Steam, Epic, EA, Riot, BSG…
    public required string Dir { get; init; }
    public string? Exe { get; set; }
    public int SteamId { get; init; }
    public GameInfo? Info { get; init; }
    public bool Custom { get; init; }
    /// <summary>Идентификатор игры в Epic (для запуска через лаунчер).</summary>
    public string? EpicApp { get; init; }
    public Image? Cover { get; set; }
    public Image? Icon { get; set; }
    /// <summary>Для exe включён высокий приоритет (признак «настроено» на плитке).</summary>
    public bool Tuned { get; set; }

    public void CheckTuned()
    {
        try { Tuned = Exe != null && GameExe.For(Exe).First(t => t.Name == GameExe.NPriority).IsApplied(); }
        catch { Tuned = false; }
    }

    public string ExeName => Exe != null ? Path.GetFileName(Exe) : "";
    /// <summary>Название без правок пользователя.</summary>
    public string BaseTitle => Info?.Name ?? Name;
    /// <summary>Ключ игры: один для всех копий из разных лаунчеров (по нему хранятся правки).</summary>
    public string Key => GamePrefs.KeyOf(BaseTitle);
    /// <summary>Название на экране: своё, если задано.</summary>
    public string Title => GamePrefs.NameOf(Key) ?? BaseTitle;
    public int CoverId => SteamId > 0 ? SteamId : Info?.SteamId ?? 0;

    List<InstalledGame>? variants;
    /// <summary>Все копии этой игры (Steam, EA app, вручную…); первая — та, что запускается.</summary>
    public IReadOnlyList<InstalledGame> Variants => variants ?? (IReadOnlyList<InstalledGame>)new[] { this };
    internal void SetVariants(List<InstalledGame> list) => variants = list;
    /// <summary>Откуда игра: «Steam» или «Steam · EA app · Вручную».</summary>
    public string Sources => string.Join(" · ", Variants.Select(v => v.Source).Distinct());

    /// <summary>Обложка на экране: своя, затем своя у любой из копий.</summary>
    public Image? ShownCover => GamePrefs.CoverOf(Key) ?? Variants.Select(v => v.Cover).FirstOrDefault(c => c != null);
    public Image? ShownIcon => Variants.Select(v => v.Icon).FirstOrDefault(c => c != null);
    public bool AnyTuned => Variants.Any(v => v.Tuned);
}

/// <summary>
/// Поиск установленных игр: библиотеки Steam (libraryfolders.vdf + appmanifest), манифесты Epic Games,
/// записи деинсталляторов (EA app, Battle.net, Riot, BSG, Ubisoft, Lesta) и игры, добавленные вручную.
/// Обложки — из локального кэша Steam или CDN Steam (кэшируются в %AppData%\WinTweaker\covers).
/// </summary>
static class GameLibrary
{
    static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinTweaker");
    static readonly string CacheFile = Path.Combine(Data, "games.json");
    static readonly string CoverDir = Path.Combine(Data, "covers");
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>Все найденные копии игр — для распознавания запущенной игры (скрытые тоже).</summary>
    public static List<InstalledGame> Games { get; private set; } = new();

    /// <summary>Игры для показа: копии из разных лаунчеров склеены, скрытые убраны, порядок — как у пользователя.</summary>
    public static List<InstalledGame> Shown => View().Where(g => !GamePrefs.IsHidden(g.Key)).ToList();
    public static List<InstalledGame> Hidden => View().Where(g => GamePrefs.IsHidden(g.Key)).ToList();

    static List<InstalledGame> View()
    {
        var list = Games.Where(g => g.Exe != null).ToList();
        var groups = new List<InstalledGame>();
        foreach (var grp in list.GroupBy(g => g.Key))
        {
            var copies = grp.ToList();
            // запускается выбранный лаунчер, иначе первый найденный (Steam/Epic/EA точнее, вручную — последним)
            var pref = GamePrefs.LauncherOf(grp.Key);
            var ordered = copies.OrderBy(c => pref != null && c.Source == pref ? 0 : 1).ThenBy(c => c.Custom ? 1 : 0).ThenBy(c => copies.IndexOf(c)).ToList();
            var main = ordered[0];
            main.SetVariants(ordered);
            groups.Add(main);
        }
        var baseIdx = groups.Select((g, i) => (g, i)).ToDictionary(x => x.g, x => x.i);
        return groups.OrderBy(g => GamePrefs.IsPinned(g.Key) ? 0 : 1)
            .ThenBy(g => GamePrefs.OrderOf(g.Key))
            .ThenBy(g => baseIdx[g]).ToList();
    }
    public static bool Scanned { get; private set; }
    public static event Action? Changed;

    sealed class Cache
    {
        public Dictionary<string, string> Exes { get; set; } = new(StringComparer.OrdinalIgnoreCase); // папка → exe
        public List<string> Custom { get; set; } = new();
    }

    static Cache LoadCache()
    {
        try { return JsonSerializer.Deserialize<Cache>(File.ReadAllText(CacheFile)) ?? new(); }
        catch { return new(); }
    }

    static void SaveCache(Cache c)
    {
        try
        {
            Directory.CreateDirectory(Data);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(c));
        }
        catch { }
    }

    public static async Task<List<InstalledGame>> ScanAsync()
    {
        var list = await Task.Run(Scan);
        Games = list;
        Scanned = true;
        Changed?.Invoke();
        _ = LoadCoversAsync(list);
        return list;
    }

    static List<InstalledGame> Scan()
    {
        var cache = LoadCache();
        var found = new List<InstalledGame>();
        void Add(InstalledGame g)
        {
            // добавленная вручную — всегда (кроме точно такого же exe)
            if (g.Custom)
            {
                found.RemoveAll(x => !x.Custom && x.Exe != null && x.Exe.Equals(g.Exe, StringComparison.OrdinalIgnoreCase));
                if (!found.Any(x => x.Custom && x.Exe!.Equals(g.Exe, StringComparison.OrdinalIgnoreCase))) found.Add(g);
                return;
            }
            // одна и та же установка из разных источников — оставляем первую (Steam/Epic/EA точнее деинсталлятора);
            // одна игра в двух магазинах (Apex в Steam и в EA app) — показываем обе
            if (found.Any(x => SameInstall(x.Dir, g.Dir))) return;
            found.Add(g);
        }

        try { foreach (var g in Steam()) Add(g); } catch { }
        try { foreach (var g in Epic()) Add(g); } catch { }
        try { foreach (var g in EaApp()) Add(g); } catch { }
        try { foreach (var g in Uninstallers()) Add(g); } catch { }
        try { foreach (var g in KnownFolders()) Add(g); } catch { }
        foreach (var exe in cache.Custom.Where(File.Exists))
        {
            var info = GameCatalog.ByExe(Path.GetFileName(exe));
            Add(new InstalledGame
            {
                Name = info?.Name ?? Path.GetFileNameWithoutExtension(exe), Source = "Вручную",
                Dir = Path.GetDirectoryName(exe)!, Exe = exe, Info = info, Custom = true
            });
        }

        // главный exe: из кэша, по пресету или самый большой подходящий
        foreach (var g in found.Where(g => g.Exe == null || !File.Exists(g.Exe)))
        {
            if (cache.Exes.TryGetValue(g.Dir, out var c) && File.Exists(c)) { g.Exe = c; continue; }
            g.Exe = FindExe(g.Dir, g.Info);
            if (g.Exe != null) cache.Exes[g.Dir] = g.Exe;
        }
        SaveCache(cache);

        foreach (var g in found.Where(g => g.Exe != null))
        {
            try { g.Icon = IconOf(g.Exe!); } catch { }
            g.CheckTuned();
        }

        // сначала игры с пресетами, затем по алфавиту
        return found.Where(g => g.Exe != null)
            .OrderBy(g => g.Info == null).ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static bool SameInstall(string a, string b)
    {
        a = a.TrimEnd('\\') + "\\";
        b = b.TrimEnd('\\') + "\\";
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    public static void AddCustom(string exe)
    {
        var c = LoadCache();
        if (!c.Custom.Contains(exe, StringComparer.OrdinalIgnoreCase)) c.Custom.Add(exe);
        SaveCache(c);
    }

    public static void RemoveCustom(string exe)
    {
        var c = LoadCache();
        c.Custom.RemoveAll(x => x.Equals(exe, StringComparison.OrdinalIgnoreCase));
        SaveCache(c);
    }

    // ───────── Steam ─────────

    static readonly string[] SteamSkip = { "Steamworks Common", "Redistributable", "Proton", "Steam Linux Runtime", "SteamVR", "Steam Audio", "Dedicated Server", "SDK",
        "Wallpaper Engine", "OBS Studio", "Blender", "Soundpad", "Lossless Scaling", "Aseprite", "VTube Studio", "Krita", "RPG Maker" };

    public static string? SteamPath()
    {
        var p = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string
             ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")?.GetValue("InstallPath") as string;
        return p == null ? null : p.Replace('/', '\\');
    }

    static IEnumerable<InstalledGame> Steam()
    {
        var steam = SteamPath();
        if (steam == null || !Directory.Exists(steam)) yield break;
        var libs = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));

        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string text;
                try { text = File.ReadAllText(acf); } catch { continue; }
                string V(string key) => Regex.Match(text, $"\"{key}\"\\s+\"([^\"]*)\"").Groups[1].Value;
                string name = V("name"), dirName = V("installdir");
                if (name == "" || dirName == "" || SteamSkip.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
                int.TryParse(V("appid"), out int id);
                var dir = Path.Combine(apps, "common", dirName);
                if (!Directory.Exists(dir)) continue;
                yield return new InstalledGame { Name = name, Source = "Steam", Dir = dir, SteamId = id, Info = GameCatalog.Match(name) };
            }
        }
    }

    // ───────── Epic Games ─────────

    static IEnumerable<InstalledGame> Epic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.item"))
        {
            InstalledGame? g = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var r = doc.RootElement;
                string name = r.TryGetProperty("DisplayName", out var n) ? n.GetString() ?? "" : "";
                string loc = r.TryGetProperty("InstallLocation", out var l) ? l.GetString() ?? "" : "";
                string exe = r.TryGetProperty("LaunchExecutable", out var e) ? e.GetString() ?? "" : "";
                string app = r.TryGetProperty("AppName", out var an) ? an.GetString() ?? "" : "";
                bool isGame = !r.TryGetProperty("AppCategories", out var cats) ||
                              cats.EnumerateArray().Any(c => c.GetString()?.Equals("games", StringComparison.OrdinalIgnoreCase) == true);
                if (name != "" && Directory.Exists(loc) && isGame)
                {
                    var info = GameCatalog.Match(name);
                    var full = exe != "" ? Path.Combine(loc, exe) : null;
                    // у Fortnite запускается лаунчер — сам клиент найдём по имени из пресета
                    if (info != null || full == null || !File.Exists(full)) full = null;
                    g = new InstalledGame { Name = name, Source = "Epic Games", Dir = loc, Exe = full, Info = info, EpicApp = app == "" ? null : app };
                }
            }
            catch { }
            if (g != null) yield return g;
        }
    }

    // ───────── Деинсталляторы (EA, Battle.net, Riot, BSG, Ubisoft, Lesta) ─────────

    static IEnumerable<InstalledGame> Uninstallers()
    {
        var roots = new (RegistryKey Hive, string Path)[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
        };
        foreach (var (hive, path) in roots)
        {
            using var root = hive.OpenSubKey(path);
            if (root == null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                string name = k?.GetValue("DisplayName") as string ?? "";
                if (name == "" || Launchers.Any(l => name.Equals(l, StringComparison.OrdinalIgnoreCase))) continue;
                string pub = k?.GetValue("Publisher") as string ?? "";
                var info = GameCatalog.Match(name);
                // игры без пресета — только от игровых издателей (иначе в список попадут все программы)
                if (info == null && !GamePublishers.Any(p => pub.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
                string loc = (k?.GetValue("InstallLocation") as string ?? "").Trim().Trim('"');
                if (loc == "") loc = DirOf(k?.GetValue("DisplayIcon") as string) ?? DirOf(k?.GetValue("UninstallString") as string) ?? "";
                if (loc == "" || !Directory.Exists(loc)) continue;
                yield return new InstalledGame { Name = name, Source = SourceOf(pub, loc), Dir = loc.TrimEnd('\\'), Info = info };
            }
        }
    }

    static readonly string[] GamePublishers =
    {
        "Electronic Arts", "Respawn", "Riot Games", "Battlestate", "Blizzard", "Activision", "Ubisoft", "Lesta", "Wargaming", "Crytek", "Bungie",
    };

    static readonly string[] Launchers =
    {
        "EA app", "EA", "Origin", "EA Desktop", "Battle.net", "Riot Client", "Riot Vanguard", "Ubisoft Connect", "Uplay",
        "Lesta Game Center", "Wargaming.net Game Center", "Game Center", "Battlestate Games Launcher", "BsgLauncher",
    };

    /// <summary>Папка из пути вида "C:\Games\X\game.exe",0 (DisplayIcon/UninstallString). Деинсталляторы EA лежат в __Installer — берём папку игры.</summary>
    static string? DirOf(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        cmd = cmd.Trim();
        string path = cmd.StartsWith('"') ? cmd[1..Math.Max(1, cmd.IndexOf('"', 1))] : cmd.Split(',')[0];
        int exe = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) path = path[..(exe + 4)];
        try
        {
            var dir = Path.GetDirectoryName(path);
            while (dir != null && (Path.GetFileName(dir).StartsWith("__Installer", StringComparison.OrdinalIgnoreCase)
                                   || Path.GetFileName(dir).Equals("Cleanup", StringComparison.OrdinalIgnoreCase)
                                   || Path.GetFileName(dir).Equals("EAInstaller", StringComparison.OrdinalIgnoreCase)))
                dir = Path.GetDirectoryName(dir);
            return dir;
        }
        catch { return null; }
    }

    // ───────── EA app ─────────

    /// <summary>
    /// Игры EA app: папки установки из настроек EA app (machine.ini / user_*.ini, параметр *.downloadinplacedir),
    /// стандартная «Program Files\EA Games» и ключи реестра «Install Dir», которые пишут игры EA.
    /// Игра EA узнаётся по папке __Installer (в ней installerdata.xml с названием).
    /// </summary>
    static IEnumerable<InstalledGame> EaApp()
    {
        var roots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EA Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "EA Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Origin Games"),
        };
        var cfgFiles = new List<string>();
        var pd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EA Desktop");
        if (Directory.Exists(pd)) cfgFiles.AddRange(Directory.EnumerateFiles(pd, "machine.*"));
        var ud = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Electronic Arts", "EA Desktop");
        if (Directory.Exists(ud)) cfgFiles.AddRange(Directory.EnumerateFiles(ud, "*.ini"));
        foreach (var f in cfgFiles)
        {
            string[] lines;
            try { lines = File.ReadAllLines(f); } catch { continue; }
            foreach (var l in lines)
            {
                int eq = l.IndexOf('=');
                if (eq < 0 || !l[..eq].Trim().EndsWith("downloadinplacedir", StringComparison.OrdinalIgnoreCase)) continue;
                var dir = l[(eq + 1)..].Trim().Trim('"');
                if (dir != "") roots.Add(dir);
            }
        }

        // главный источник — список установленных игр самого EA app (файл IS)
        var known = new List<EaInstalls.Entry>();
        try { known = EaInstalls.Read(); } catch { }
        foreach (var e in known)
        {
            string title = EaTitle(e.Dir) ?? Pretty(e.Slug) ?? Path.GetFileName(e.Dir);
            var info = GameCatalog.Match(title) ?? GameCatalog.Match(Pretty(e.Slug) ?? "") ?? GameCatalog.Match(Path.GetFileName(e.Dir));
            yield return new InstalledGame { Name = info?.Name ?? title, Source = "EA app", Dir = e.Dir, Info = info };
        }

        var dirs = new List<string>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            try { dirs.AddRange(Directory.EnumerateDirectories(root)); } catch { }
        }
        // ключи реестра, которые пишут игры EA/Origin: ...\EA Games\<Игра>\Install Dir
        foreach (var basePath in new[] { @"SOFTWARE\WOW6432Node\EA Games", @"SOFTWARE\EA Games", @"SOFTWARE\WOW6432Node\Electronic Arts", @"SOFTWARE\Electronic Arts", @"SOFTWARE\Respawn" })
        {
            using var k = Registry.LocalMachine.OpenSubKey(basePath);
            if (k == null) continue;
            foreach (var sub in k.GetSubKeyNames())
            {
                if (sub.StartsWith("EA Desktop", StringComparison.OrdinalIgnoreCase) || sub.StartsWith("EA Core", StringComparison.OrdinalIgnoreCase)) continue;
                using var gk = k.OpenSubKey(sub);
                var d = (gk?.GetValue("Install Dir") ?? gk?.GetValue("InstallDir") ?? gk?.GetValue("Install Location")) as string;
                if (!string.IsNullOrWhiteSpace(d)) dirs.Add(d.Trim().Trim('"').TrimEnd('\\'));
            }
        }

        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            string name = EaTitle(dir) ?? Path.GetFileName(dir);
            if (name == "" || Launchers.Any(l => name.Equals(l, StringComparison.OrdinalIgnoreCase))) continue;
            var info = GameCatalog.Match(name) ?? GameCatalog.Match(Path.GetFileName(dir));
            yield return new InstalledGame { Name = info?.Name ?? name, Source = "EA app", Dir = dir, Info = info };
        }
    }

    /// <summary>«apex-legends» → «Apex Legends».</summary>
    static string? Pretty(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var words = slug.Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    /// <summary>Название игры из __Installer\installerdata.xml (gameTitle en_US или первый).</summary>
    static string? EaTitle(string dir)
    {
        try
        {
            var f = Path.Combine(dir, "__Installer", "installerdata.xml");
            if (!File.Exists(f)) return null;
            var xml = File.ReadAllText(f);
            var m = Regex.Match(xml, "<gameTitle[^>]*locale=\"en_US\"[^>]*>([^<]+)</gameTitle>", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(xml, "<gameTitle[^>]*>([^<]+)</gameTitle>", RegexOptions.IgnoreCase);
            return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : null;
        }
        catch { return null; }
    }

    static string SourceOf(string publisher, string dir)
    {
        string s = publisher + " " + dir;
        if (s.Contains("Riot", StringComparison.OrdinalIgnoreCase)) return "Riot";
        if (s.Contains("Battlestate", StringComparison.OrdinalIgnoreCase)) return "BSG";
        if (s.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase) || s.Contains("EA Games", StringComparison.OrdinalIgnoreCase)) return "EA app";
        if (s.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) || s.Contains("Activision", StringComparison.OrdinalIgnoreCase)) return "Battle.net";
        if (s.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase)) return "Ubisoft";
        if (s.Contains("Lesta", StringComparison.OrdinalIgnoreCase) || s.Contains("Wargaming", StringComparison.OrdinalIgnoreCase)) return "Лаунчер";
        return "Установлена";
    }

    static IEnumerable<InstalledGame> KnownFolders()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var known = new (string Dir, string Id, string Source)[]
        {
            (@"C:\Battlestate Games\EFT", "tarkov", "BSG"),
            (@"C:\Battlestate Games\Escape from Tarkov", "tarkov", "BSG"),
            (@"C:\Riot Games\VALORANT\live", "valorant", "Riot"),
            (@"C:\Riot Games\League of Legends", "lol", "Riot"),
            (Path.Combine(pf, "EA Games", "Apex"), "apex", "EA app"),
            (@"C:\Games\Tanki", "wot", "Лаунчер"),
        };
        foreach (var (dir, id, src) in known)
            if (Directory.Exists(dir) && GameCatalog.ById(id) is { } info)
                yield return new InstalledGame { Name = info.Name, Source = src, Dir = dir, Info = info };
    }

    // ───────── Запуск ─────────

    /// <summary>
    /// Запустить игру так, как её запускает лаунчер: Steam и Epic — по ссылке, Riot — через Riot Client,
    /// остальные — exe (EA app и подобные перехватывают запуск сами). WinTweak работает от администратора,
    /// а игра должна стартовать с обычными правами — поэтому запуск идёт через Проводник.
    /// </summary>
    public static void Launch(InstalledGame g)
    {
        if (g.Source == "Steam" && g.SteamId > 0) { Open($"steam://rungameid/{g.SteamId}"); return; }
        if (g.EpicApp != null) { Open($"com.epicgames.launcher://apps/{Uri.EscapeDataString(g.EpicApp)}?action=launch&silent=true"); return; }
        if (g.Info?.Id is "valorant" or "lol" && RiotClient() is string rc)
        {
            string product = g.Info.Id == "valorant" ? "valorant" : "league_of_legends";
            OpenWithArgs(rc, $"--launch-product={product} --launch-patchline=live");
            return;
        }
        if (g.Info?.Id == "tarkov" && BsgLauncher() is string bsg) { Open(bsg); return; }
        if (g.Exe != null) Open(g.Exe);
    }

    static void Open(string target) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = false });

    /// <summary>exe с параметрами без прав администратора: временный ярлык, открытый через Проводник.</summary>
    static void OpenWithArgs(string exe, string args)
    {
        var dir = Path.Combine(Path.GetTempPath(), "WinTweak");
        Directory.CreateDirectory(dir);
        var lnk = Path.Combine(dir, Path.GetFileNameWithoutExtension(exe) + "-" + Math.Abs(args.GetHashCode()) + ".lnk");
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell недоступен");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic sc = shell.CreateShortcut(lnk);
        sc.TargetPath = exe;
        sc.Arguments = args;
        sc.WorkingDirectory = Path.GetDirectoryName(exe);
        sc.Save();
        Open(lnk);
    }

    static string? RiotClient()
    {
        try
        {
            var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games", "RiotClientInstalls.json");
            if (File.Exists(f))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                foreach (var key in new[] { "rc_default", "rc_live" })
                    if (doc.RootElement.TryGetProperty(key, out var v) && v.GetString() is string p && File.Exists(p)) return p;
            }
        }
        catch { }
        var def = @"C:\Riot Games\Riot Client\RiotClientServices.exe";
        return File.Exists(def) ? def : null;
    }

    static string? BsgLauncher()
    {
        foreach (var p in new[] { @"C:\Battlestate Games\BsgLauncher\BsgLauncher.exe", @"C:\Program Files\Battlestate Games\BsgLauncher\BsgLauncher.exe" })
            if (File.Exists(p)) return p;
        return null;
    }

    // ───────── Главный exe ─────────

    static readonly string[] NotGame =
    {
        "crash", "unins", "setup", "redist", "launcher", "helper", "report", "easyanticheat", "battleye", "beservice",
        "dxsetup", "vc_redist", "vcredist", "dotnet", "updater", "update", "prereq", "start_protected_game", "cefprocess",
        "webhelper", "install", "config", "settings", "eac", "overlay", "dump", "benchmark",
    };

    static string? FindExe(string dir, GameInfo? info)
    {
        var opt = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true };
        try
        {
            if (info != null)
                foreach (var name in info.Exes)
                {
                    var hit = Directory.EnumerateFiles(dir, name, opt).FirstOrDefault();
                    if (hit != null) return hit;
                }
            opt.MaxRecursionDepth = 4;
            return Directory.EnumerateFiles(dir, "*.exe", opt)
                .Where(f => !NotGame.Any(n => Path.GetFileName(f).Contains(n, StringComparison.OrdinalIgnoreCase)))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.Length)
                .FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    // ───────── Обложки и значки ─────────

    public const int CoverW = 230, CoverH = 107;

    static async Task LoadCoversAsync(List<InstalledGame> list)
    {
        foreach (var g in list.Where(g => g.CoverId > 0 && g.Cover == null))
        {
            try
            {
                var img = await Task.Run(() => CoverFromDisk(g.CoverId));
                img ??= await Download(g.CoverId);
                if (img != null) { g.Cover = img; Changed?.Invoke(); }
            }
            catch { }
        }
    }

    static Image? CoverFromDisk(int id)
    {
        var cached = Path.Combine(CoverDir, id + ".jpg");
        if (File.Exists(cached)) return Load(cached);
        var steam = SteamPath();
        if (steam == null) return null;
        var lc = Path.Combine(steam, "appcache", "librarycache");
        var old = Path.Combine(lc, $"{id}_header.jpg");
        if (File.Exists(old)) return Load(old);
        var dir = Path.Combine(lc, id.ToString());
        if (Directory.Exists(dir))
        {
            var f = Directory.EnumerateFiles(dir, "header.jpg", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 }).FirstOrDefault();
            if (f != null) return Load(f);
        }
        return null;
    }

    static async Task<Image?> Download(int id)
    {
        foreach (var url in new[]
        {
            $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{id}/header.jpg",
            $"https://cdn.akamai.steamstatic.com/steam/apps/{id}/header.jpg",
        })
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(url);
                Directory.CreateDirectory(CoverDir);
                var file = Path.Combine(CoverDir, id + ".jpg");
                await File.WriteAllBytesAsync(file, bytes);
                return Load(file);
            }
            catch { }
        }
        return null;
    }

    static Image? Load(string file) => LoadCover(file);

    /// <summary>
    /// Загрузить обложку и обрезать по центру под пропорции плитки. Храним в двойном размере (как header.jpg Steam, 460×215):
    /// плитки и окна на экранах с масштабом больше 100% остаются чёткими. Файл не блокируется.
    /// </summary>
    public static Image? LoadCover(string file)
    {
        try
        {
            using var ms = new MemoryStream(File.ReadAllBytes(file));
            using var src = Image.FromStream(ms);
            int tw = CoverW * 2, th = CoverH * 2;
            var bmp = new Bitmap(tw, th, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float scale = Math.Max((float)tw / src.Width, (float)th / src.Height);
            float sw = tw / scale, sh = th / scale;
            using var ia = new System.Drawing.Imaging.ImageAttributes();
            ia.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(src, new Rectangle(0, 0, tw, th), (src.Width - sw) / 2, (src.Height - sh) / 2, sw, sh, GraphicsUnit.Pixel, ia);
            return bmp;
        }
        catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint count, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    /// <summary>Значок exe в большом размере (256 px, если он есть в файле) — раньше брался 32 px и расплывался на плитке.</summary>
    static Image? IconOf(string exe)
    {
        const int S = 128;
        Bitmap? big = null;
        foreach (int size in new[] { 256, 128, 64, 48 })
        {
            var h = new IntPtr[1];
            var ids = new int[1];
            if (PrivateExtractIcons(exe, 0, size, size, h, ids, 1, 0) == 0 || h[0] == IntPtr.Zero) continue;
            try { using var ic = System.Drawing.Icon.FromHandle(h[0]); big = ic.ToBitmap(); }
            catch { }
            finally { DestroyIcon(h[0]); }
            if (big != null) break;
        }
        if (big == null)
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon == null) return null;
            big = icon.ToBitmap();
        }
        using (big)
        {
            var bmp = new Bitmap(S, S, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(big, 0, 0, S, S);
            return bmp;
        }
    }
}
