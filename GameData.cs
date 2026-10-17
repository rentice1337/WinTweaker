namespace WinTweaker;

/// <summary>Где стоят серверы игры — для проверки пинга до регионов.</summary>
enum ServerNet { None, Valve, Aws }

/// <summary>
/// Пресет для конкретной игры: как её найти (exe, название в магазинах), что включить в Windows
/// и что выставить в самой игре. Пресет меняет только Windows — файлы и настройки игры не трогаются,
/// советы по игре показываются текстом (античиты не любят чужие правки конфигов).
/// </summary>
sealed record GameInfo(
    string Id,
    string Name,
    string[] Exes,
    string[] Keywords,
    int SteamId,
    bool Shooter,
    bool Dx11,
    ServerNet Servers,
    string[] Tips,
    string? Note = null);

static class GameCatalog
{
    public static readonly GameInfo[] All =
    {
        new("cs2", "Counter-Strike 2", new[] { "cs2.exe" }, new[] { "Counter-Strike 2", "Counter-Strike Global Offensive" },
            730, true, true, ServerNet.Valve, new[]
            {
                "Режим экрана — «Полный экран»: задержка ниже, чем «В окне без рамки»",
                "NVIDIA Reflex — «Вкл. + усиление» (Видео → Расширенные настройки)",
                "Без G-Sync/FreeSync — вертикальная синхронизация выкл. С ними — V-Sync в панели драйвера вкл, в игре выкл",
                "Качество теней — «Высокое»: противники отбрасывают тени, их проще заметить",
                "Сглаживание — CMAA2 или 2× MSAA: почти без потери FPS",
                "Регион матчмейкинга виден по пингу ниже: CS2 работает через ретрансляторы Valve",
            }),
        new("apex", "Apex Legends", new[] { "r5apex_dx12.exe", "r5apex.exe" }, new[] { "Apex Legends" },
            1172470, true, true, ServerNet.None, new[]
            {
                "DirectX 12 — основной режим игры; в Steam версия (DX11/DX12) выбирается при запуске",
                "После патча или нового драйвера первый вход дольше — компилируются шейдеры, это нормально",
                "Параметр запуска +fps_max 0 — снимает встроенный лимит кадров",
                "«Адаптивное разрешение» — 0: иначе картинка «мылится» при просадках",
                "Объёмный свет и динамические тени от солнца — выкл: заметный прирост FPS",
                "NVIDIA Reflex — «Вкл. + усиление»",
            }, "Античит Easy Anti-Cheat: приоритет процессора через Windows ему не мешает"),
        new("hunt", "Hunt: Showdown 1896", new[] { "HuntGame.exe" }, new[] { "Hunt: Showdown", "Hunt Showdown" },
            594650, true, true, ServerNet.None, new[]
            {
                "Запускается именно HuntGame.exe (hunt.exe — только загрузчик античита): настройки ниже — для него",
                "Игра упирается в процессор — перед матчем закройте браузер и фоновые программы",
                "Режим экрана — «Полный экран», NVIDIA Reflex или AMD Anti-Lag — вкл",
                "DLSS/FSR в режиме «Качество» лучше, чем снижать разрешение: дальние цели остаются чёткими",
                "Туман и объёмный свет — «Средне»: заметно выше FPS почти без потери видимости",
                "Ставьте игру на SSD — на HDD долгие загрузки и подгрузка текстур в матче",
            }),
        new("tarkov", "Escape from Tarkov", new[] { "EscapeFromTarkov.exe" }, new[] { "Escape from Tarkov", "EscapeFromTarkov" },
            0, true, true, ServerNet.None, new[]
            {
                "«Автоматическая очистка ОЗУ» в игре — вкл при 16 ГБ памяти, при 32 ГБ и больше — выкл",
                "«Использовать только физические ядра» — попробуйте на процессорах с SMT/HT: у многих ровнее кадры",
                "Не отключайте файл подкачки: при нехватке памяти Tarkov вылетает. Размер — «по выбору системы»",
                "Держите игру на SSD: на HDD долгие загрузки и подлагивания в рейде",
                "Игра занимает 12–20 ГБ памяти — закройте браузер перед рейдом",
                "Сервер рейда выбирается в лаунчере BSG — там же видно пинг до каждого",
            }, "Античит BattlEye: приоритет процессора через Windows ему не мешает"),
        new("valorant", "VALORANT", new[] { "VALORANT-Win64-Shipping.exe" }, new[] { "VALORANT" },
            0, true, true, ServerNet.None, new[]
            {
                "NVIDIA Reflex — «Вкл. + усиление»",
                "Многопоточный рендеринг — вкл (на процессорах от 4 ядер)",
                "Ограничение FPS в меню и на батарее — оставьте, в матче — выкл или выше частоты монитора",
                "Сглаживание MSAA 2× и анизотропия 4× — разумный минимум без «лесенок»",
                "«Повышенная чёткость», размытие и искажения — выкл",
            }, "Античит Vanguard: приоритет процессора через Windows ему не мешает"),
        new("fortnite", "Fortnite", new[] { "FortniteClient-Win64-Shipping.exe" }, new[] { "Fortnite" },
            0, true, false, ServerNet.Aws, new[]
            {
                "Режим рендеринга «Производительность» — максимум FPS на слабых и средних ПК",
                "NVIDIA Reflex — «Вкл. + усиление»",
                "Дальность прорисовки — средняя или дальняя, остальное можно ниже",
                "После обновления первые матчи подтормаживают — компилируются шейдеры",
                "Серверы Fortnite работают в облаке AWS — пинг до регионов ниже",
            }),
        new("dota2", "Dota 2", new[] { "dota2.exe" }, new[] { "Dota 2" },
            570, false, true, ServerNet.Valve, new[]
            {
                "Режим экрана — «Полный экран»",
                "Параметр запуска -vulkan иногда даёт ровнее кадры (особенно на AMD), -dx11 — вернуть обратно",
                "Регион поиска выбирайте по пингу ниже — Dota 2 работает через ретрансляторы Valve",
            }),
        new("pubg", "PUBG: Battlegrounds", new[] { "TslGame.exe" }, new[] { "PUBG", "PLAYERUNKNOWN" },
            578080, true, true, ServerNet.None, new[]
            {
                "Сглаживание и текстуры — повыше, тени, эффекты и листва — пониже: так проще видеть противников",
                "Режим экрана — «Полный экран»",
                "NVIDIA Reflex — «Вкл. + усиление»",
            }),
        new("r6", "Rainbow Six Siege", new[] { "RainbowSix.exe", "RainbowSix_Vulkan.exe", "RainbowSix_DX11.exe" }, new[] { "Rainbow Six" },
            359550, true, true, ServerNet.None, new[]
            {
                "Версия Vulkan на большинстве ПК даёт больше кадров, чем DirectX 11",
                "NVIDIA Reflex — «Вкл. + усиление»",
                "Масштаб рендеринга — 100%, иначе картинка теряет чёткость",
            }),
        new("cod", "Call of Duty / Warzone", new[] { "cod.exe" }, new[] { "Call of Duty" },
            1938090, true, false, ServerNet.None, new[]
            {
                "Дождитесь окончания «Предзагрузки шейдеров» в меню — иначе фризы в первых матчах",
                "Режим экрана — «Полный экран (эксклюзивный)»",
                "NVIDIA Reflex — «Вкл. + усиление»",
                "«Целевой лимит видеопамяти» 80–90% — меньше вылетов при нехватке VRAM",
            }),
        new("ow2", "Overwatch 2", new[] { "Overwatch.exe" }, new[] { "Overwatch" },
            2357570, true, true, ServerNet.None, new[]
            {
                "NVIDIA Reflex — «Вкл. + усиление»",
                "Ограничение FPS — «Пользовательское», чуть выше частоты монитора",
                "Отражения и динамические тени — выкл",
            }),
        new("rust", "Rust", new[] { "RustClient.exe" }, new[] { "=Rust" },
            252490, true, true, ServerNet.None, new[]
            {
                "Игре нужно 16 ГБ памяти и больше — не отключайте файл подкачки",
                "Трава и дальность прорисовки сильнее всего влияют на FPS",
                "Ставьте игру на SSD — на HDD долгая загрузка карты",
            }),
        new("finals", "THE FINALS", new[] { "Discovery.exe" }, new[] { "THE FINALS" },
            2073850, true, false, ServerNet.None, new[]
            {
                "NVIDIA Reflex — «Вкл. + усиление»",
                "DLSS/FSR «Качество» — хорошее соотношение FPS и чёткости",
                "Глобальное освещение — статическое, если не хватает кадров",
            }),
        new("deadlock", "Deadlock", new[] { "deadlock.exe", "project8.exe" }, new[] { "=Deadlock" },
            1422450, true, true, ServerNet.Valve, new[]
            {
                "Режим экрана — «Полный экран»",
                "Регион поиска — по пингу ниже: Deadlock работает через ретрансляторы Valve",
            }),
        new("rivals", "Marvel Rivals", new[] { "Marvel-Win64-Shipping.exe" }, new[] { "Marvel Rivals" },
            2767030, true, false, ServerNet.None, new[]
            {
                "NVIDIA Reflex — «Вкл. + усиление»",
                "DLSS/FSR «Качество» вместо низкого разрешения",
                "Глобальное освещение и отражения — пониже: самые «тяжёлые» настройки",
            }),
        new("lol", "League of Legends", new[] { "League of Legends.exe" }, new[] { "League of Legends" },
            0, false, true, ServerNet.None, new[]
            {
                "Ограничение FPS — чуть выше частоты монитора, а не «Без ограничений»: меньше нагрев и ровнее кадры",
                "Режим экрана — «Полный экран»",
            }),
        new("wot", "Мир танков / World of Tanks", new[] { "WorldOfTanks.exe" }, new[] { "World of Tanks", "Мир танков" },
            0, true, true, ServerNet.None, new[]
            {
                "Сервер выбирайте в лаунчере по пингу — там видно задержку до каждого",
                "Качество теней и растительности сильнее всего влияют на FPS",
                "Режим экрана — «Полноэкранный»",
            }),
        new("gta5", "Grand Theft Auto V", new[] { "GTA5.exe", "GTA5_Enhanced.exe" }, new[] { "Grand Theft Auto V" },
            271590, false, true, ServerNet.None, new[]
            {
                "Enhanced-версия (DirectX 12) тяжелее Legacy — на слабых ПК Legacy даёт больше кадров",
                "Плотность населения и травы сильнее всего нагружают процессор",
            }),
    };

    /// <summary>Пресет по названию из магазина (Steam, Epic, деинсталлятор). "=Rust" — точное совпадение.</summary>
    public static GameInfo? Match(string name)
    {
        name = name.Trim();
        foreach (var g in All)
            foreach (var k in g.Keywords)
            {
                if (k.StartsWith('=') ? name.Equals(k[1..], StringComparison.OrdinalIgnoreCase)
                                      : name.Contains(k, StringComparison.OrdinalIgnoreCase))
                    return g;
            }
        return null;
    }

    public static GameInfo? ByExe(string exe) =>
        All.FirstOrDefault(g => g.Exes.Any(e => e.Equals(exe, StringComparison.OrdinalIgnoreCase)));

    public static GameInfo? ById(string id) => All.FirstOrDefault(g => g.Id == id);

    /// <summary>Системные настройки, которые пресет отмечает (названия из каталога).</summary>
    public static string[] SystemTweaks(GameInfo? g)
    {
        var list = new List<string>
        {
            "Игровой режим",
            "Отключить фоновую запись (Game DVR)",
            "Схема питания «Высокая производительность»",
            "Приоритет мультимедиа и игр",
            "Не усыплять сетевую карту",
        };
        if (g == null || g.Shooter) list.Add("Отключить ускорение мыши");
        if (g?.Dx11 == true) list.Add("Отключить оптимизацию во весь экран");
        return list.ToArray();
    }

    // ───── Общие советы для любой игры ─────
    public static readonly string[] CommonTips =
    {
        "Ограничение кадров ставьте в игре или драйвере, а не через V-Sync: задержка ниже",
        "Монитор с высокой частотой: проверьте в «Параметры → Дисплей → Расширенные», что выбрана максимальная частота",
        "Ноутбук: играйте от сети — на батарее процессор и видеокарта режут частоты",
    };
}
