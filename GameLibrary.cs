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
    public string Title => Info?.Name ?? Name;
    public int CoverId => SteamId > 0 ? SteamId : Info?.SteamId ?? 0;
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

    public static List<InstalledGame> Games { get; private set; } = new();
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
            // одна игра из разных источников — оставляем первую (Steam/Epic точнее деинсталлятора)
            if (found.Any(x => x.Dir.Equals(g.Dir, StringComparison.OrdinalIgnoreCase) ||
                               (g.Info != null && x.Info?.Id == g.Info.Id))) return;
            found.Add(g);
        }

        try { foreach (var g in Steam()) Add(g); } catch { }
        try { foreach (var g in Epic()) Add(g); } catch { }
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

    static readonly string[] SteamSkip = { "Steamworks Common", "Redistributable", "Proton", "Steam Linux Runtime", "SteamVR", "Steam Audio", "Dedicated Server", "SDK" };

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
                bool isGame = !r.TryGetProperty("AppCategories", out var cats) ||
                              cats.EnumerateArray().Any(c => c.GetString()?.Equals("games", StringComparison.OrdinalIgnoreCase) == true);
                if (name != "" && Directory.Exists(loc) && isGame)
                {
                    var info = GameCatalog.Match(name);
                    var full = exe != "" ? Path.Combine(loc, exe) : null;
                    // у Fortnite запускается лаунчер — сам клиент найдём по имени из пресета
                    if (info != null || full == null || !File.Exists(full)) full = null;
                    g = new InstalledGame { Name = name, Source = "Epic Games", Dir = loc, Exe = full, Info = info };
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
                string loc = (k?.GetValue("InstallLocation") as string ?? "").Trim('"');
                var info = name == "" ? null : GameCatalog.Match(name);
                if (info == null || loc == "" || !Directory.Exists(loc)) continue;
                string pub = k?.GetValue("Publisher") as string ?? "";
                yield return new InstalledGame { Name = name, Source = SourceOf(pub, loc), Dir = loc, Info = info };
            }
        }
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

    /// <summary>Загрузить и сразу уменьшить до размера плитки: файл не блокируется, рисовать дёшево.</summary>
    static Image? Load(string file)
    {
        try
        {
            using var ms = new MemoryStream(File.ReadAllBytes(file));
            using var src = Image.FromStream(ms);
            var bmp = new Bitmap(CoverW, CoverH, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            // обрезка «по центру» под пропорции плитки
            float scale = Math.Max((float)CoverW / src.Width, (float)CoverH / src.Height);
            float w = src.Width * scale, h = src.Height * scale;
            g.DrawImage(src, (CoverW - w) / 2, (CoverH - h) / 2, w, h);
            return bmp;
        }
        catch { return null; }
    }

    static Image? IconOf(string exe)
    {
        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
        if (icon == null) return null;
        using var big = icon.ToBitmap();
        var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(big, 0, 0, 32, 32);
        return bmp;
    }
}
