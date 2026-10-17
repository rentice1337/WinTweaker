namespace WinTweaker;

/// <summary>
/// Настройки Windows для одного exe игры. Всё — штатными механизмами Windows и с записью в журнал:
///  • приоритет процессора — Image File Execution Options\PerfOptions (Windows сама ставит его при каждом запуске);
///  • предпочтение видеокарты — то же, что «Параметры → Дисплей → Графика»;
///  • отключение оптимизации во весь экран — то же, что флажок на вкладке «Совместимость» свойств exe;
///  • QoS — политика DSCP 46 для трафика игры (помогает, если роутер поддерживает приоритизацию/WMM).
/// Файлы игры не меняются.
/// </summary>
static class GameExe
{
    public const string CatPrefix = "Игра: ";
    const string Ifeo = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\";
    const string GpuPref = @"HKCU\Software\Microsoft\DirectX\UserGpuPreferences";
    const string Layers = @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    const string QosKey = @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\QoS";
    const string Fso = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

    public const string NPriority = "Высокий приоритет процессора";
    public const string NGpu = "Мощная видеокарта";
    public const string NFso = "Отключить оптимизацию во весь экран";
    public const string NQos = "Приоритет игрового трафика (QoS)";

    public static string Category(string exe) => CatPrefix + Path.GetFileName(exe);

    /// <summary>Настройки для exe. path — полный путь (для видеокарты и совместимости нужен именно он).</summary>
    public static List<Tweak> For(string path)
    {
        string exe = Path.GetFileName(path), cat = Category(path);
        string perf = Ifeo + exe + @"\PerfOptions";
        string policy = "WinTweak " + exe;
        var list = new List<Tweak>
        {
            new()
            {
                Category = cat, Name = NPriority,
                Description = "Windows сама запускает игру с приоритетом «Высокий»: фоновые программы реже отнимают у неё процессор. " +
                              "Не «Реального времени» — он может подвесить систему. Античитам не мешает.",
                Details = $"Применить:\n  [{perf}]\n    CpuPriorityClass = 3  (REG_DWORD, «Высокий»)\n\nОтменить: удалить параметр.",
                IsApplied = () => Reg.Get(perf, "CpuPriorityClass")?.ToString() == "3",
                Apply = () => Reg.Set(perf, "CpuPriorityClass", 3),
                Revert = () => Reg.Delete(perf, "CpuPriorityClass"),
            },
            new()
            {
                Category = cat, Name = NGpu,
                Description = "На ноутбуках с двумя видеокартами игра всегда запускается на дискретной, а не на встроенной. " +
                              "На ПК с одной видеокартой ничего не меняет.",
                Details = $"Применить:\n  [{GpuPref}]\n    {path} = \"GpuPreference=2;\"  (REG_SZ)\n\n" +
                          "То же, что «Параметры → Система → Дисплей → Графика → Высокая производительность».",
                IsApplied = () => Tokens(Reg.Get(GpuPref, path) as string, ';').Contains("GpuPreference=2"),
                Apply = () =>
                {
                    var t = Tokens(Reg.Get(GpuPref, path) as string, ';').Where(x => !x.StartsWith("GpuPreference=")).ToList();
                    t.Insert(0, "GpuPreference=2");
                    Reg.Set(GpuPref, path, string.Join(";", t) + ";");
                },
                Revert = () =>
                {
                    var t = Tokens(Reg.Get(GpuPref, path) as string, ';').Where(x => !x.StartsWith("GpuPreference=")).ToList();
                    if (t.Count == 0) Reg.Delete(GpuPref, path); else Reg.Set(GpuPref, path, string.Join(";", t) + ";");
                },
            },
            new()
            {
                Category = cat, Name = NFso,
                Description = "Как флажок на вкладке «Совместимость»: игра получает настоящий эксклюзивный полноэкранный режим. " +
                              "Помогает в играх на DirectX 9–11, если в «Полном экране» чувствуется задержка или «рваный» кадр.",
                Details = $"Применить:\n  [{Layers}]\n    {path} = \"~ {Fso}\"  (REG_SZ, другие флаги совместимости сохраняются)",
                IsApplied = () => Tokens(Reg.Get(Layers, path) as string, ' ').Contains(Fso),
                Apply = () =>
                {
                    var t = Tokens(Reg.Get(Layers, path) as string, ' ').Where(x => x != "~" && x != Fso).ToList();
                    t.Insert(0, Fso);
                    Reg.Set(Layers, path, "~ " + string.Join(" ", t));
                },
                Revert = () =>
                {
                    var t = Tokens(Reg.Get(Layers, path) as string, ' ').Where(x => x != "~" && x != Fso).ToList();
                    if (t.Count == 0) Reg.Delete(Layers, path); else Reg.Set(Layers, path, "~ " + string.Join(" ", t));
                },
            },
            new()
            {
                Category = cat, Name = NQos,
                Description = "Трафик игры помечается как самый срочный (DSCP 46). Помогает, когда кто-то дома качает или смотрит видео — " +
                              "но только если роутер поддерживает приоритизацию (QoS/WMM). В остальных случаях безвреден.",
                Details = $"PowerShell:\n  New-NetQosPolicy -Name '{policy}' -AppPathNameMatchCondition '{exe}' -DSCPAction 46 -NetworkProfile All\n" +
                          $"Для ПК вне домена:\n  [{QosKey}]\n    Do not use NLA = \"1\"  (REG_SZ)\n\nОтменить:\n  Remove-NetQosPolicy -Name '{policy}'",
                IsApplied = () => Shell.PS($"if (Get-NetQosPolicy -Name '{Esc(policy)}' -ErrorAction SilentlyContinue) {{ 'yes' }}").Contains("yes"),
                Apply = () =>
                {
                    Reg.Set(QosKey, "Do not use NLA", "1");
                    Shell.PS($"Remove-NetQosPolicy -Name '{Esc(policy)}' -Confirm:$false -ErrorAction SilentlyContinue; " +
                             $"New-NetQosPolicy -Name '{Esc(policy)}' -AppPathNameMatchCondition '{Esc(exe)}' -DSCPAction 46 -NetworkProfile All | Out-Null");
                },
                Revert = () => Shell.PS($"Remove-NetQosPolicy -Name '{Esc(policy)}' -Confirm:$false -ErrorAction SilentlyContinue"),
            },
        };
        return list;
    }

    /// <summary>Что включать по умолчанию для игры.</summary>
    public static bool Recommended(string name, GameInfo? info) => name switch
    {
        NPriority => true,
        NGpu => true,
        NFso => info?.Dx11 == true,
        NQos => info != null,
        _ => false
    };

    /// <summary>Для отката из «Истории»: восстановить настройку по категории и названию.</summary>
    public static Tweak? Rebuild(string category, string name)
    {
        if (!category.StartsWith(CatPrefix)) return null;
        return For(category[CatPrefix.Length..]).FirstOrDefault(t => t.Name == name);
    }

    static List<string> Tokens(string? s, char sep) =>
        (s ?? "").Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    static string Esc(string s) => s.Replace("'", "''");

    /// <summary>Применить/отменить с записью в журнал (видно и откатывается в «Истории»).</summary>
    public static void Set(Tweak t, bool on)
    {
        var s = Journal.StartSession();
        bool was = false;
        try { was = t.IsApplied(); } catch { }
        Journal.Begin(s, t, was, on);
        try
        {
            if (on) t.Apply(); else t.Revert?.Invoke();
        }
        finally
        {
            Journal.End();
            Journal.Save(s);
        }
    }
}
