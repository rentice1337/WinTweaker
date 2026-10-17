using System.Text.Json;
using Microsoft.Win32;

namespace WinTweaker;

/// <summary>
/// Каталог программ: включить = установить через winget, выключить = удалить.
/// Установлена ли программа — по списку «Программы и компоненты» в реестре (мгновенно, без запуска winget).
/// </summary>
static class Programs
{
    public const string Cat = "Программы";

    sealed record App(string Group, string Name, string Id, string Match, string Desc);

    static readonly App[] Apps =
    {
        new("Браузеры", "Google Chrome", "Google.Chrome", "Google Chrome", "Самый популярный браузер."),
        new("Браузеры", "Mozilla Firefox", "Mozilla.Firefox", "Mozilla Firefox", "Открытый браузер с упором на приватность."),
        new("Браузеры", "Brave", "Brave.Brave", "Brave", "Chromium со встроенной блокировкой рекламы."),
        new("Браузеры", "Яндекс Браузер", "Yandex.Browser", "Yandex", "Браузер с сервисами Яндекса."),
        new("Браузеры", "Opera GX", "Opera.OperaGX", "Opera GX", "Игровой браузер с ограничением ресурсов."),

        new("Общение", "Discord", "Discord.Discord", "Discord", "Голосовой и текстовый чат."),
        new("Общение", "Telegram", "Telegram.TelegramDesktop", "Telegram Desktop", "Мессенджер."),
        new("Общение", "Zoom", "Zoom.Zoom", "Zoom", "Видеоконференции."),

        new("Игры", "Steam", "Valve.Steam", "Steam", "Магазин и лаунчер игр."),
        new("Игры", "Epic Games Launcher", "EpicGames.EpicGamesLauncher", "Epic Games Launcher", "Магазин Epic и бесплатные раздачи."),
        new("Игры", "GOG Galaxy", "GOG.Galaxy", "GOG GALAXY", "Игры без DRM."),
        new("Игры", "Battle.net", "Blizzard.BattleNet", "Battle.net", "Игры Blizzard."),
        new("Игры", "EA app", "ElectronicArts.EADesktop", "EA app", "Игры Electronic Arts."),

        new("Медиа", "VLC", "VideoLAN.VLC", "VLC media player", "Плеер, который открывает всё."),
        new("Медиа", "Spotify", "Spotify.Spotify", "Spotify", "Музыка."),
        new("Медиа", "OBS Studio", "OBSProject.OBSStudio", "OBS Studio", "Запись экрана и стримы."),
        new("Медиа", "AIMP", "AIMP.AIMP", "AIMP", "Лёгкий аудиоплеер."),
        new("Медиа", "K-Lite Codec Pack", "CodecGuide.K-LiteCodecPack.Standard", "K-Lite Codec Pack", "Кодеки для любых видео."),

        new("Утилиты", "7-Zip", "7zip.7zip", "7-Zip", "Архиватор."),
        new("Утилиты", "Everything", "voidtools.Everything", "Everything", "Мгновенный поиск файлов."),
        new("Утилиты", "PowerToys", "Microsoft.PowerToys", "PowerToys", "Утилиты Microsoft для продвинутых пользователей."),
        new("Утилиты", "Notepad++", "Notepad++.Notepad++", "Notepad++", "Текстовый редактор."),
        new("Утилиты", "ShareX", "ShareX.ShareX", "ShareX", "Скриншоты и запись экрана."),
        new("Утилиты", "qBittorrent", "qBittorrent.qBittorrent", "qBittorrent", "Торрент-клиент без рекламы."),

        new("Железо", "CPU-Z", "CPUID.CPU-Z", "CPU-Z", "Информация о процессоре и памяти."),
        new("Железо", "HWiNFO", "REALiX.HWiNFO", "HWiNFO", "Датчики и температуры."),
        new("Железо", "CrystalDiskInfo", "CrystalDewWorld.CrystalDiskInfo", "CrystalDiskInfo", "Здоровье дисков."),
        new("Железо", "MSI Afterburner", "Guru3D.Afterburner", "MSI Afterburner", "Мониторинг и разгон видеокарты."),

        new("Разработка", "Visual Studio Code", "Microsoft.VisualStudioCode", "Microsoft Visual Studio Code", "Редактор кода."),
        new("Разработка", "Git", "Git.Git", "Git", "Система контроля версий."),
        new("Разработка", "Python 3.12", "Python.Python.3.12", "Python 3.12", "Язык программирования."),
        new("Разработка", "Node.js LTS", "OpenJS.NodeJS.LTS", "Node.js", "JavaScript вне браузера."),

        new("Компоненты", ".NET Desktop Runtime 8", "Microsoft.DotNet.DesktopRuntime.8", "Microsoft Windows Desktop Runtime - 8", "Нужен многим программам на .NET."),
    };

    public static IEnumerable<Tweak> All() => Apps.Select(a => new Tweak
    {
        Category = Cat, Group = a.Group, Name = a.Name,
        Description = a.Desc + "\n\nВключить — установить через winget (тихо, с сайта разработчика). Выключить — удалить.",
        Details = $"Установить:\n  winget install --id {a.Id} -e --silent --accept-package-agreements --accept-source-agreements\n" +
                  $"Удалить:\n  winget uninstall --id {a.Id} -e --silent\n\nНужен «Установщик приложений» (App Installer) из Microsoft Store.",
        IsApplied = () => Installed.Has(a.Match),
        Apply = () => Winget($"install --id {a.Id} -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity", a.Name),
        Revert = () => Winget($"uninstall --id {a.Id} -e --silent --accept-source-agreements --disable-interactivity", a.Name)
    }).Append(VcRedist());

    // ───── Visual C++ всех версий ─────

    static readonly (string Year, string Arch)[] Vc =
        new[] { "2005", "2008", "2010", "2012", "2013", "2015+" }.SelectMany(y => new[] { (y, "x86"), (y, "x64") }).ToArray();

    static string VcId((string Year, string Arch) v) => $"Microsoft.VCRedist.{v.Year}.{v.Arch}";

    /// <summary>Установлена ли версия: по названию в «Программы и компоненты» (у 2005 x86 в названии нет «x86»).</summary>
    static bool VcInstalled((string Year, string Arch) v)
    {
        var years = v.Year == "2015+" ? new[] { "2015", "2017", "2019", "2022" } : new[] { v.Year };
        return Installed.Names.Any(n =>
            n.StartsWith("Microsoft Visual C++ ", StringComparison.OrdinalIgnoreCase) &&
            n.Contains("Redistributable", StringComparison.OrdinalIgnoreCase) &&
            years.Any(y => n.Contains(y)) &&
            (n.Contains(v.Arch, StringComparison.OrdinalIgnoreCase) ||
             v.Year == "2005" && v.Arch == "x86" && !n.Contains("x64", StringComparison.OrdinalIgnoreCase)));
    }

    static Tweak VcRedist() => new()
    {
        Category = Cat, Group = "Компоненты", Name = "Visual C++ 2005–2022 — все версии",
        Description = "Библиотеки Microsoft Visual C++ всех версий, x86 и x64 — без них не запускаются многие игры и программы. " +
                      "То же, что сборники «All in One», но официальные пакеты напрямую от Microsoft.\n\n" +
                      "Включить — поставить недостающие версии (установленные пропускаются). Выключение ничего не удаляет: эти библиотеки нужны другим программам.",
        Details = "Через winget, только недостающие:\n" + string.Join("\n", Vc.Select(v => "  winget install --id " + VcId(v) + " -e --silent")) +
                  "\n\nНужен «Установщик приложений» (App Installer) из Microsoft Store.",
        IsApplied = () => Vc.All(VcInstalled),
        Apply = () =>
        {
            var failed = new List<string>();
            foreach (var v in Vc.Where(v => !VcInstalled(v)))
            {
                int code = Shell.RunCode("cmd.exe", $"/c winget install --id {VcId(v)} -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity");
                if (code == 9009) throw new InvalidOperationException("winget не найден — установите «Установщик приложений» из Microsoft Store.");
                // 0x8A15002B — уже стоит последняя версия
                if (code != 0 && (uint)code != 0x8A15002B) failed.Add($"{v.Year} {v.Arch} (0x{code:X8})");
            }
            Installed.Reset();
            if (failed.Count > 0) throw new InvalidOperationException("Не установились: " + string.Join(", ", failed));
        },
    };

    static void Winget(string args, string name)
    {
        int code = Shell.RunCode("cmd.exe", "/c winget " + args);
        if (code == 9009) // команда не найдена
            throw new InvalidOperationException("winget не найден — установите «Установщик приложений» из Microsoft Store.");
        if (code != 0)
            throw new InvalidOperationException($"winget вернул код 0x{code:X8} для «{name}».");
        Installed.Reset();
    }

    /// <summary>Названия из «Программы и компоненты» (HKLM, HKLM WOW64, HKCU).</summary>
    public static class Installed
    {
        static List<string>? names;

        public static void Reset() => names = null;

        public static IReadOnlyList<string> Names => names ??= Load();

        public static bool Has(string prefix)
        {
            names ??= Load();
            // короткие имена (Git) — только точное совпадение, иначе «Git» совпадёт с «GitHub Desktop»
            return names.Any(n => prefix.Length <= 3 ? n.Equals(prefix, StringComparison.OrdinalIgnoreCase) : n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        static List<string> Load()
        {
            var list = new List<string>();
            var roots = new (RegistryKey Hive, string Path)[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            };
            foreach (var (hive, path) in roots)
            {
                try
                {
                    using var k = hive.OpenSubKey(path);
                    if (k == null) continue;
                    foreach (var sub in k.GetSubKeyNames())
                    {
                        try
                        {
                            using var s = k.OpenSubKey(sub);
                            if (s?.GetValue("DisplayName") is string n && n.Trim() != "") list.Add(n.Trim());
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return list;
        }
    }
}

/// <summary>Свои наборы: именованные списки отмеченных настроек (%AppData%\WinTweaker\sets.json).</summary>
static class Sets
{
    public sealed class Set
    {
        public string Name { get; set; } = "";
        public DateTime Created { get; set; }
        public List<string> Tweaks { get; set; } = new();
    }

    static readonly string File_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinTweaker", "sets.json");

    public static List<Set> Load()
    {
        try
        {
            return File.Exists(File_) ? JsonSerializer.Deserialize<List<Set>>(File.ReadAllText(File_)) ?? new() : new();
        }
        catch { return new(); }
    }

    public static void Save(List<Set> sets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        File.WriteAllText(File_, JsonSerializer.Serialize(sets, new JsonSerializerOptions { WriteIndented = true }));
    }
}
