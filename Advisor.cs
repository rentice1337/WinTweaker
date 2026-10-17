namespace WinTweaker;

/// <summary>Рекомендация: что не так и что сделать.</summary>
sealed class Rec
{
    public int Severity;                 // 0 — совет, 1 — стоит сделать, 2 — важно
    public int Weight;                   // сколько баллов отнимает от индекса
    public string Title = "", Detail = "";
    public string[] Tweaks = Array.Empty<string>(); // отметить эти настройки
    public string? Category;             // или открыть раздел
}

/// <summary>Железо, которое влияет на советы. Тип диска определяется один раз в фоне.</summary>
static class Hardware
{
    public static string DiskKind { get; private set; } = "";   // SSD / HDD / "" — неизвестно
    /// <summary>Подключённые сейчас видеокарты (реестр помнит и давно вынутые — поэтому CIM).</summary>
    public static List<string>? Gpus { get; private set; }
    public static int Cores { get; private set; }

    public static void Detect()
    {
        try
        {
            Gpus = Shell.PS("Get-CimInstance Win32_VideoController | Where-Object { $_.Status -eq 'OK' -or $_.ConfigManagerErrorCode -eq 0 } | ForEach-Object { $_.Name }")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(n => !n.Contains("Basic", StringComparison.OrdinalIgnoreCase) && !n.Contains("Remote Display", StringComparison.OrdinalIgnoreCase))
                .Distinct().ToList();
        }
        catch { Gpus = null; }
        try { Cores = int.TryParse(Shell.PS("(Get-CimInstance Win32_Processor | Measure-Object NumberOfCores -Sum).Sum").Trim(), out int c) ? c : 0; }
        catch { Cores = 0; }
        try
        {
            string r = Shell.PS("$n = (Get-Partition -DriveLetter $env:SystemDrive.TrimEnd(':') | Get-Disk).Number; " +
                                "(Get-PhysicalDisk | Where-Object DeviceId -eq $n).MediaType");
            DiskKind = r.Contains("SSD") ? "SSD" : r.Contains("HDD") ? "HDD" : "";
        }
        catch { DiskKind = ""; }
    }

    /// <summary>HAGS: GeForce GTX 10xx/16xx и RTX, Radeon RX 5000 и новее, Intel Arc.</summary>
    public static bool? HagsCapable()
    {
        var gpus = SysInfo.GpuList();
        if (gpus.Count == 0) return null;
        foreach (var g in gpus)
        {
            string n = g.ToUpperInvariant();
            if (n.Contains("RTX") || n.Contains("ARC")) return true;
            var m = System.Text.RegularExpressions.Regex.Match(n, @"GTX\s*(\d{3,4})");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int gtx) && gtx >= 1000) return true;
            var rx = System.Text.RegularExpressions.Regex.Match(n, @"RX\s*(\d{4})");
            if (rx.Success && int.TryParse(rx.Groups[1].Value, out int amd) && amd >= 5000) return true;
        }
        return false;
    }
}

/// <summary>Индекс оптимизации 0–100 и советы, подобранные под конкретный ПК.</summary>
static class Advisor
{
    const string HP = "Схема питания «Высокая производительность»";
    const string HAGS = "Аппаратное планирование GPU (HAGS)";
    const string Hibernate = "Отключить гибернацию";
    static readonly string[] Security = { "Отключить SMBv1", "Защита от нежелательных программ (PUA)", "Показывать расширения файлов" };

    public static (int Score, List<Rec> Recs) Analyze(IReadOnlyList<Tweak> tweaks, Func<Tweak, bool> isOn, List<Tweak> revertedList)
    {
        var recs = new List<Rec>();
        int reverted = revertedList.Count;
        Tweak? T(string name) => tweaks.FirstOrDefault(t => t.Name == name);
        bool On(string name) { var t = T(name); return t != null && isOn(t); }
        double Frac(IEnumerable<Tweak> list) { var l = list.ToList(); return l.Count == 0 ? 1 : (double)l.Count(isOn) / l.Count; }

        // базовая часть индекса — насколько применены безопасные наборы
        double rec = Frac(Presets.Resolve(Presets.All[0], tweaks));
        double priv = Frac(Presets.Resolve(Presets.All[2], tweaks));
        double sec = Frac(tweaks.Where(t => Security.Contains(t.Name)));
        double score = 30 + 35 * rec + 15 * priv + 20 * sec;

        if (reverted > 0)
            recs.Add(new Rec
            {
                Severity = 2, Weight = 10, Tweaks = revertedList.Select(t => t.Name).ToArray(),
                Title = $"Windows откатила {reverted} {WatchBanner.Plural(reverted)}",
                Detail = "Обычно после крупного обновления — нажмите, чтобы отметить их снова"
            });

        var (free, total) = SysInfo.SystemDrive();
        if (total > 0 && (free / total < 0.10 || free < 15))
            recs.Add(new Rec
            {
                Severity = 2, Weight = 12, Category = "Обслуживание",
                Title = "Мало места на системном диске",
                Detail = $"Свободно {free:0} из {total:0} ГБ — при заполненном диске Windows тормозит и не ставит обновления"
            });

        var missingSec = tweaks.Where(t => Security.Contains(t.Name) && !isOn(t)).Select(t => t.Name).ToArray();
        if (missingSec.Length > 0)
            recs.Add(new Rec
            {
                Severity = 2, Weight = 0, Tweaks = missingSec,
                Title = "Базовая защита включена не полностью",
                Detail = string.Join(", ", missingSec)
            });

        if (Hardware.DiskKind == "HDD")
            recs.Add(new Rec
            {
                Severity = 1, Weight = 8,
                Title = "Windows стоит на жёстком диске (HDD)",
                Detail = "Переход на SSD ускорит загрузку в 3–5 раз — больше, чем любые твики"
            });

        var startup = tweaks.Where(t => t.Category == StartupManager.Cat && isOn(t)).ToList();
        if (startup.Count > 8)
            recs.Add(new Rec
            {
                Severity = 1, Weight = 8, Category = StartupManager.Cat,
                Title = $"В автозагрузке {startup.Count} программ",
                Detail = "Каждая замедляет вход в систему — отключите ненужные"
            });

        bool laptop = SysInfo.IsLaptop;
        if (laptop && On(HP))
            recs.Add(new Rec
            {
                Severity = 1, Weight = 4, Category = "Производительность",
                Title = "Ноутбук на «Высокой производительности»",
                Detail = "Процессор не сбрасывает частоту — батарея садится заметно быстрее"
            });
        else if (!laptop && T(HP) != null && !On(HP))
            recs.Add(new Rec
            {
                Severity = 0, Weight = 3, Tweaks = new[] { HP },
                Title = "Включите «Высокую производительность»",
                Detail = "Для стационарного ПК — меньше задержек при всплесках нагрузки"
            });

        var hags = Hardware.HagsCapable();
        if (hags == true && T(HAGS) != null && !On(HAGS))
            recs.Add(new Rec
            {
                Severity = 0, Weight = 2, Tweaks = new[] { HAGS },
                Title = "Видеокарта поддерживает HAGS",
                Detail = "Аппаратное планирование GPU снижает задержку в играх"
            });
        else if (hags == false && On(HAGS))
            recs.Add(new Rec
            {
                Severity = 1, Weight = 3, Category = "Производительность",
                Title = "HAGS включён, но видеокарта его вряд ли поддерживает",
                Detail = "Нужны GeForce GTX 10xx / Radeon RX 5000 и новее — иначе настройка не работает"
            });

        var mem = SysInfo.Memory();
        if (mem.TotalGb > 0 && mem.TotalGb < 7.5)
            recs.Add(new Rec
            {
                Severity = 1, Weight = 4, Tweaks = Presets.All[3].Tweaks,
                Title = $"Мало оперативной памяти: {mem.TotalGb:0.#} ГБ",
                Detail = "Пресет «Слабый ПК» освободит память и процессор"
            });
        else if (Hardware.DiskKind == "SSD" && mem.TotalGb >= 15 && T(Hibernate) != null && !On(Hibernate) && !laptop)
            recs.Add(new Rec
            {
                Severity = 0, Weight = 2, Tweaks = new[] { Hibernate },
                Title = "Освободите место на SSD",
                Detail = $"Отключение гибернации удалит hiberfil.sys ≈ {mem.TotalGb * 0.4:0} ГБ"
            });

        if (SysInfo.UptimeDays > 7)
            recs.Add(new Rec
            {
                Severity = 0, Weight = 4,
                Title = $"Компьютер не перезагружался {SysInfo.UptimeDays:0} дн.",
                Detail = "Перезагрузка освобождает память и доустанавливает обновления"
            });

        if (SysInfo.BuildNumber is > 0 and < 19045)
            recs.Add(new Rec
            {
                Severity = 1, Weight = 5, Category = "Обновления",
                Title = "Устаревшая версия Windows 10",
                Detail = "Последняя версия — 22H2 (сборка 19045); старые не получают исправлений"
            });
        else if (SysInfo.BuildNumber is >= 19041 and < 22000)
            recs.Add(new Rec
            {
                Severity = 0, Weight = 0,
                Title = "Поддержка Windows 10 закончилась",
                Detail = "Бесплатные исправления безопасности прекращены 14.10.2025 — подумайте о Windows 11"
            });

        if (SysInfo.IsHome && tweaks.Any(t => t.Category == "Обновления" && t.Description.Contains("Pro") && isOn(t)))
            recs.Add(new Rec
            {
                Severity = 0, Weight = 0, Category = "Обновления",
                Title = "Часть политик не работает в Windows Home",
                Detail = "Политики обновлений из раздела «Обновления» Home игнорирует"
            });

        score -= recs.Sum(r => r.Weight);
        var sorted = recs.OrderByDescending(r => r.Severity).ThenByDescending(r => r.Weight).ToList();
        return ((int)Math.Round(Math.Clamp(score, 0, 100)), sorted);
    }
}
