using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WinTweaker;

/// <summary>
/// Расширенный каталог: панель задач, контекстное меню, мышь и клавиатура, звук и уведомления, Edge,
/// а также настройки со значением (варианты и ползунки) в существующих разделах.
/// </summary>
public static partial class Catalog
{
    const string Desk = @"HKCU\Control Panel\Desktop";
    const string Mouse = @"HKCU\Control Panel\Mouse";
    const string Kbd = @"HKCU\Control Panel\Keyboard";
    const string Blocked = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    const string EdgePol = @"HKLM\SOFTWARE\Policies\Microsoft\Edge";
    const string Restart = "\n\nВступит в силу после перезапуска Проводника.";

    static void More(List<Tweak> t)
    {
        Taskbar(t);
        ContextMenu(t);
        Input(t);
        SoundNotify(t);
        Edge(t);
        ValuesInExisting(t);
        t.AddRange(Programs.All());
    }

    // ───────── Панель задач и Пуск ─────────
    static void Taskbar(List<Tweak> t)
    {
        const string C = "Панель задач";
        const string Stuck = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3";

        t.Add(new Tweak
        {
            Category = C, Group = "Панель задач", Name = "Расположение панели задач",
            Description = "С какой стороны экрана находится панель задач." + Restart,
            Options = new[] { "Снизу", "Слева", "Сверху", "Справа" },
            Details = $"[{Stuck}]\n  Settings — двоичный параметр, байт 12: 3 снизу, 0 слева, 1 сверху, 2 справа",
            Get = () => Reg.Get(Stuck, "Settings") is byte[] b && b.Length > 12 ? b[12] switch { 0 => 1, 1 => 2, 2 => 3, _ => 0 } : 0,
            Set = i =>
            {
                if (Reg.Get(Stuck, "Settings") is not byte[] b || b.Length <= 12) return;
                var copy = (byte[])b.Clone();
                copy[12] = (byte)(i switch { 1 => 0, 2 => 1, 3 => 2, _ => 3 });
                Reg.Set(Stuck, "Settings", copy);
            }
        });
        t.Add(new Tweak
        {
            Category = C, Group = "Панель задач", Name = "Автоматически скрывать панель задач",
            Description = "Панель появляется, когда подводите курсор к краю экрана." + Restart,
            Details = $"[{Stuck}]\n  Settings — байт 8: 03 скрывать, 02 не скрывать",
            IsApplied = () => Reg.Get(Stuck, "Settings") is byte[] b && b.Length > 8 && b[8] == 3,
            Apply = () => StuckByte(Stuck, 8, 3),
            Revert = () => StuckByte(Stuck, 8, 2)
        });
        t.Add(RegChoice(C, "Панель задач", "Группировка кнопок", "Объединять ли окна одной программы в одну кнопку.",
            Adv, "TaskbarGlomLevel", new object?[] { 0, 1, 2 }, new[] { "Всегда", "При заполнении", "Никогда" }, 0, restart: true));
        t.Add(RegChoice(C, "Панель задач", "Поиск на панели задач", "Как показывать поиск рядом с кнопкой «Пуск».",
            Search, "SearchboxTaskbarMode", new object?[] { 0, 1, 2 }, new[] { "Скрыт", "Значок", "Поле" }, 2));
        t.Add(RegSlider(C, "Панель задач", "Задержка эскизов окон", "Через сколько появляется миниатюра окна при наведении на кнопку.",
            Adv, "ExtendedUIHoverTime", 0, 2000, 100, "мс", 400, asString: false));
        t.Add(R(C, "Только на основном мониторе", "Не показывать панель задач на дополнительных мониторах." + Restart,
            Adv, "MMTaskbarEnabled", 0, 1).In("Панель задач"));

        t.Add(R(C, "Не показывать недавно добавленные", "Убирает блок «Недавно добавленные» из меню «Пуск».",
            @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Explorer", "HideRecentlyAddedApps", 1, null).In("Пуск"));
        t.Add(R(C, "Не показывать часто используемые", "Убирает «Часто используемые» из меню «Пуск».",
            Adv, "Start_TrackProgs", 0, 1).In("Пуск"));
        t.Add(R(C, "Отключить живые плитки", "Плитки в «Пуске» перестают обновляться и показывать новости/рекламу.",
            @"HKCU\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\PushNotifications", "NoTileApplicationNotification", 1, null).In("Пуск"));

        const string Icons = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
        t.Add(R(C, "Значок «Этот компьютер»", "Показывать «Этот компьютер» на рабочем столе.",
            Icons, "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", 0, 1).In("Рабочий стол"));
        t.Add(R(C, "Значок «Папка пользователя»", "Показывать вашу папку на рабочем столе.",
            Icons, "{59031a47-3f72-44a7-89c5-5595fe6b30ee}", 0, 1).In("Рабочий стол"));
        t.Add(R(C, "Значок «Панель управления»", "Показывать «Панель управления» на рабочем столе.",
            Icons, "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", 0, 1).In("Рабочий стол"));
        t.Add(R(C, "Скрыть «Корзину» с рабочего стола", "",
            Icons, "{645FF040-5081-101B-9F08-00AA002F954E}", 1, 0).In("Рабочий стол"));
    }

    static void StuckByte(string key, int index, byte value)
    {
        if (Reg.Get(key, "Settings") is not byte[] b || b.Length <= index) return;
        var copy = (byte[])b.Clone();
        copy[index] = value;
        Reg.Set(key, "Settings", copy);
    }

    // ───────── Контекстное меню ─────────
    static void ContextMenu(List<Tweak> t)
    {
        const string C = "Контекстное меню";
        const string Add = "Добавить пункты", Remove = "Убрать лишнее";

        t.Add(Verb(C, Add, "«Копировать путь» без Shift", "Пункт «Копировать как путь» всегда в меню файлов и папок, не только с зажатым Shift.",
            new[] { @"HKCR\AllFilesystemObjects\shell\WinTweak.CopyPath" },
            new (string, object)[]
            {
                ("MUIVerb", "@shell32.dll,-30329"), ("Icon", "imageres.dll,-5302"),
                ("VerbHandler", "{f3d06e7c-1e45-4a26-847e-f9fcdee59be0}"), ("VerbName", "copyaspath"),
                ("CommandStateHandler", "{3B1599F9-E00A-4BBF-AD3E-B3F99FA87779}"), ("CommandStateSync", ""),
                ("InvokeCommandOnSelection", 1)
            }, null));
        t.Add(Verb(C, Add, "«Открыть PowerShell здесь»", "В меню папки и пустого места в папке.",
            new[] { @"HKCR\Directory\shell\WinTweak.PowerShell", @"HKCR\Directory\Background\shell\WinTweak.PowerShell" },
            new (string, object)[] { ("MUIVerb", "Открыть PowerShell здесь"), ("Icon", "powershell.exe") },
            "powershell.exe -NoExit -Command \"Set-Location -LiteralPath '%V'\""));
        t.Add(Verb(C, Add, "«Открыть командную строку здесь»", "В меню папки и пустого места в папке.",
            new[] { @"HKCR\Directory\shell\WinTweak.Cmd", @"HKCR\Directory\Background\shell\WinTweak.Cmd" },
            new (string, object)[] { ("MUIVerb", "Открыть командную строку здесь"), ("Icon", "cmd.exe") },
            "cmd.exe /s /k pushd \"%V\""));
        t.Add(Verb(C, Add, "«Открыть в Блокноте» для всех файлов", "Быстро посмотреть любой файл как текст.",
            new[] { @"HKCR\*\shell\WinTweak.Notepad" },
            new (string, object)[] { ("MUIVerb", "Открыть в Блокноте"), ("Icon", "notepad.exe") },
            "notepad.exe \"%1\""));
        t.Add(Verb(C, Add, "«Стать владельцем»", "Получить полный доступ к файлу или папке, которые «заняты системой» (takeown + icacls). Запрашивает права администратора.",
            new[] { @"HKCR\*\shell\runas", @"HKCR\Directory\shell\runas" },
            new (string, object)[] { ("MUIVerb", "Стать владельцем"), ("Icon", "imageres.dll,-78"), ("HasLUAShield", ""), ("NoWorkingDirectory", "") },
            "cmd.exe /c takeown /f \"%1\" /r /d y && icacls \"%1\" /grant *S-1-5-32-544:F /t /c /q",
            risky: true));
        t.Add(Verb(C, Add, "«Перезапустить Проводник» на рабочем столе", "Пункт в меню рабочего стола — если зависла панель задач.",
            new[] { @"HKCR\DesktopBackground\Shell\WinTweak.RestartExplorer" },
            new (string, object)[] { ("MUIVerb", "Перезапустить Проводник"), ("Icon", "explorer.exe"), ("Position", "Bottom") },
            "cmd.exe /c taskkill /f /im explorer.exe & start explorer.exe"));
        t.Add(KeyAdd(C, Add, "«Копировать в папку…» и «Переместить в папку…»", "Классические пункты для копирования/переноса в выбранную папку.",
            @"HKCR\AllFilesystemObjects\shellex\ContextMenuHandlers\{C2FBB630-2971-11D1-A18C-00C04FD75D13}",
            @"HKCR\AllFilesystemObjects\shellex\ContextMenuHandlers\{C2FBB631-2971-11D1-A18C-00C04FD75D13}"));

        // Убрать: блокировка расширений оболочки по CLSID — штатный механизм Windows, всё обратимо
        t.Add(Block(C, Remove, "Убрать «Поделиться»", "{e2bf9676-5f8f-435c-97eb-11607a5bedf7}"));
        t.Add(Block(C, Remove, "Убрать «Отправить на устройство»", "{7AD84985-87B4-4a16-BE58-8B72A5B390F7}"));
        t.Add(Block(C, Remove, "Убрать «Восстановить прежнюю версию»", "{596AB062-B4D2-4215-9F74-E9109B0A8153}"));
        t.Add(Block(C, Remove, "Убрать «Добавить в библиотеку»", "{3dad6c5d-2167-4cae-9914-f99e41c12cfa}"));
        t.Add(Block(C, Remove, "Убрать «Предоставить доступ к»", "{f81e9010-6ea4-11ce-a7ff-00aa003ca9f6}"));
        t.Add(Block(C, Remove, "Убрать «Закрепить на начальном экране»", "{470C0EBD-5D73-4d58-9CED-E91E22E23282}"));
        t.Add(Block(C, Remove, "Убрать «Проверить с помощью Microsoft Defender»", "{09A47860-11B0-4DA5-AFA5-26D86198A780}"));
    }

    /// <summary>Свой пункт меню: раздел с параметрами (+ подраздел command). Помечается маркером WinTweak.</summary>
    static Tweak Verb(string cat, string group, string name, string desc, string[] keys, (string Name, object Value)[] values, string? command, bool risky = false) => new()
    {
        Category = cat, Group = group, Name = name, Risky = risky,
        Description = desc + "\n\nПункт появится сразу, иногда — после перезапуска Проводника.",
        Details = "Создать разделы:\n" + string.Join("\n", keys.Select(k => "  [" + k + "]")) + "\n" +
                  string.Join("\n", values.Select(v => $"    {v.Name} = {v.Value}")) +
                  (command != null ? "\n  [...\\command]\n    (По умолчанию) = " + command : "") +
                  "\n\nОтменить — удалить эти разделы (только созданные WinTweaker).",
        IsApplied = () => keys.All(k => Reg.Get(k, "WinTweak") != null),
        Apply = () =>
        {
            foreach (var k in keys)
            {
                Reg.CreateKey(k);
                foreach (var (n, v) in values) Reg.Set(k, n, v);
                Reg.Set(k, "WinTweak", 1);
                if (command != null)
                {
                    Reg.CreateKey(k + @"\command");
                    Reg.Set(k + @"\command", "", command);
                    Reg.Set(k + @"\command", "IsolatedCommand", command);
                }
            }
        },
        Revert = () =>
        {
            foreach (var k in keys)
                if (Reg.Get(k, "WinTweak") != null) Reg.DeleteKey(k);
        }
    };

    static Tweak KeyAdd(string cat, string group, string name, string desc, params string[] keys) => new()
    {
        Category = cat, Group = group, Name = name, Description = desc,
        Details = "Создать разделы:\n" + string.Join("\n", keys.Select(k => "  " + k)) + "\n\nОтменить — удалить их.",
        IsApplied = () => keys.All(Reg.KeyExists),
        Apply = () => { foreach (var k in keys) Reg.CreateKey(k); },
        Revert = () => { foreach (var k in keys) Reg.DeleteKey(k); }
    };

    static Tweak Block(string cat, string group, string name, string clsid) => new()
    {
        Category = cat, Group = group, Name = name,
        Description = "Пункт исчезнет из контекстного меню. Используется штатная блокировка расширений оболочки — сама функция не удаляется." + Restart,
        Details = $"[{Blocked}]\n  {clsid} = \"\"  (REG_SZ)\n\nОтменить — удалить параметр.",
        IsApplied = () => Reg.Get(Blocked, clsid) != null,
        Apply = () => Reg.Set(Blocked, clsid, ""),
        Revert = () => Reg.Delete(Blocked, clsid)
    };

    // ───────── Мышь и клавиатура ─────────
    static void Input(List<Tweak> t)
    {
        const string C = "Мышь и клавиатура";
        t.Add(RegSlider(C, "Мышь", "Скорость указателя", "Как в «Параметры → Мышь». 10 — по умолчанию. Применяется сразу.",
            Mouse, "MouseSensitivity", 1, 20, 1, "", 10, asString: true, live: v => Live.Spi(0x0071, 0, (IntPtr)v)));
        t.Add(RegSlider(C, "Мышь", "Прокрутка колесом", "Сколько строк прокручивает один щелчок колеса. Применяется сразу.",
            Desk, "WheelScrollLines", 1, 20, 1, "строк", 3, asString: true, live: v => Live.Spi(0x0069, (uint)v, IntPtr.Zero)));
        t.Add(RegSlider(C, "Мышь", "Скорость двойного щелчка", "Максимальная пауза между щелчками. Меньше — нужно щёлкать быстрее.",
            Mouse, "DoubleClickSpeed", 200, 900, 50, "мс", 500, asString: true, live: v => Live.DoubleClick(v)));
        t.Add(RegChoice(C, "Клавиатура", "Задержка перед повтором", "Через сколько начинает повторяться зажатая клавиша. Применяется сразу.",
            Kbd, "KeyboardDelay", new object?[] { "0", "1", "2", "3" }, new[] { "Мин.", "Короткая", "Средняя", "Длинная" }, 1,
            live: i => Live.Spi(0x0017, (uint)i, IntPtr.Zero)));
        t.Add(RegSlider(C, "Клавиатура", "Скорость повтора", "Как быстро повторяется зажатая клавиша (31 — максимум). Применяется сразу.",
            Kbd, "KeyboardSpeed", 0, 31, 1, "", 31, asString: true, live: v => Live.Spi(0x000B, (uint)v, IntPtr.Zero)));

        const string Toggle = @"HKCU\Keyboard Layout\Toggle";
        t.Add(new Tweak
        {
            Category = C, Group = "Клавиатура", Name = "Переключение раскладки",
            Description = "Сочетание для смены языка ввода. Win+Пробел работает всегда." + Relogin,
            Options = new[] { "Alt+Shift", "Ctrl+Shift", "Ё", "Выкл." },
            Details = $"[{Toggle}]\n  Hotkey, Language Hotkey = \"1\" Alt+Shift, \"2\" Ctrl+Shift, \"4\" Ё, \"3\" выкл.\n  Layout Hotkey = \"3\" (чтобы не конфликтовало)",
            Get = () => (Reg.Get(Toggle, "Language Hotkey") ?? Reg.Get(Toggle, "Hotkey"))?.ToString() switch { "2" => 1, "4" => 2, "3" => 3, _ => 0 },
            Set = i =>
            {
                string v = i switch { 1 => "2", 2 => "4", 3 => "3", _ => "1" };
                Reg.Set(Toggle, "Hotkey", v);
                Reg.Set(Toggle, "Language Hotkey", v);
                Reg.Set(Toggle, "Layout Hotkey", "3");
            }
        });
        t.Add(MX(C, "Отключить окна фильтрации и озвучивания клавиш",
            "Не выскакивают окна при удержании правого Shift 8 секунд и NumLock 5 секунд.", false, false,
            (@"HKCU\Control Panel\Accessibility\Keyboard Response", "Flags", "122", "126"),
            (@"HKCU\Control Panel\Accessibility\ToggleKeys", "Flags", "58", "62")).In("Клавиатура"));
    }

    // ───────── Звук и уведомления ─────────
    static void SoundNotify(List<Tweak> t)
    {
        const string C = "Звук и уведомления";
        const string Notif = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

        t.Add(R(C, "Звук запуска Windows", "Мелодия при входе в систему (в Windows 10 выключена по умолчанию).",
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\BootAnimation", "DisableStartupSound", 0, 1).In("Звук"));
        t.Add(R(C, "Не приглушать звук во время звонков", "Windows не будет тише играть музыку и игры, когда идёт звонок в Discord/Skype.",
            @"HKCU\Software\Microsoft\Multimedia\Audio", "UserDuckingPreference", 3, null).In("Звук"));
        t.Add(Act(C, "Звук", "Микшер громкости", "Громкость отдельно для каждой программы.", () => Live.Start("sndvol.exe"),
            details: "Запускает sndvol.exe"));
        t.Add(Act(C, "Звук", "Устройства воспроизведения", "Классическое окно звука: устройство по умолчанию, формат, улучшения.",
            () => Live.Start("control.exe", "mmsys.cpl"), details: "Запускает control.exe mmsys.cpl"));

        t.Add(R(C, "Отключить всплывающие уведомления", "Баннеры в правом нижнем углу не показываются (в Центре уведомлений они остаются).",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", 0, 1).In("Уведомления"));
        t.Add(R(C, "Без уведомлений на экране блокировки", "",
            Notif, "NOC_GLOBAL_SETTING_ALLOW_TOASTS_ABOVE_LOCK", 0, null).In("Уведомления"));
        t.Add(R(C, "Без звука уведомлений", "Уведомления приходят беззвучно.",
            Notif, "NOC_GLOBAL_SETTING_ALLOW_NOTIFICATION_SOUND", 0, null).In("Уведомления"));
        t.Add(R(C, "Отключить Центр уведомлений", "Убирает значок и панель Центра уведомлений справа." + Restart,
            @"HKCU\Software\Policies\Microsoft\Windows\Explorer", "DisableNotificationCenter", 1, null).In("Уведомления"));
        t.Add(R(C, "Тише «Безопасность Windows»", "Defender показывает только важные уведомления, без отчётов «проверка завершена».",
            @"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender Security Center\Notifications", "DisableEnhancedNotifications", 1, null).In("Уведомления"));
        t.Add(R(C, "Без «Давайте завершим настройку устройства»", "Экран-напоминание после обновлений больше не появляется.",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, null).In("Уведомления"));
    }

    // ───────── Microsoft Edge (политики) ─────────
    static void Edge(List<Tweak> t)
    {
        const string C = "Приложения", G = "Microsoft Edge";
        void P(string name, string desc, string val, int on) =>
            t.Add(R(C, name, desc + "\n\nВ Edge появится «Управляется организацией» — это нормально для политик.", EdgePol, val, on, null).In(G));
        P("Edge: не запускать вместе с Windows", "Отключает «ускорение запуска» — Edge не висит в памяти после входа.", "StartupBoostEnabled", 0);
        P("Edge: не работать в фоне после закрытия", "Расширения и уведомления не держат Edge запущенным.", "BackgroundModeEnabled", 0);
        P("Edge: убрать боковую панель", "Панель с Copilot, поиском и инструментами справа.", "HubsSidebarEnabled", 0);
        P("Edge: без рекомендаций и рекламы", "Отключает всплывающие рекомендации и продвижение функций.", "ShowRecommendationsEnabled", 0);
        P("Edge: без помощника покупок", "Купоны и сравнение цен на сайтах магазинов.", "EdgeShoppingAssistantEnabled", 0);
        P("Edge: не предлагать стать браузером по умолчанию", "", "DefaultBrowserSettingEnabled", 0);
        P("Edge: без экрана первого запуска", "", "HideFirstRunExperience", 1);
        P("Edge: минимум диагностики", "Edge отправляет только обязательные данные.", "DiagnosticData", 0);
        P("Edge: не использовать историю для персонализации", "", "PersonalizationReportingEnabled", 0);
    }

    // ───────── Ползунки и варианты в существующих разделах ─────────
    static void ValuesInExisting(List<Tweak> t)
    {
        t.Add(RegSlider("Интерфейс", null, "Толщина текстового курсора", "Мигающий курсор в полях ввода. Применяется сразу.",
            Desk, "CaretWidth", 1, 10, 1, "пикс.", 1, asString: false, live: v => Live.Spi(0x2007, 0, (IntPtr)v)));
        t.Add(RegSlider("Интерфейс", null, "Качество обоев (JPEG)", "Windows пережимает обои в JPEG. 100 — без потерь. Переустановите обои, чтобы применилось.",
            Desk, "JPEGImportQuality", 60, 100, 5, "%", 85, asString: false));

        const string P = "Производительность";
        t.Add(PowerSlider(P, "Отключать дисплей через", "SUB_VIDEO", "VIDEOIDLE", "monitor-timeout-ac", 0, 120, 5));
        t.Add(PowerSlider(P, "Переход в сон через", "SUB_SLEEP", "STANDBYIDLE", "standby-timeout-ac", 0, 240, 10));
        t.Add(new Tweak
        {
            Category = P, Name = "Ожидание закрытия программ при выключении",
            Description = "Сколько Windows ждёт зависшую программу при выключении, прежде чем закрыть её. По умолчанию 20 с.",
            Range = (2, 20, 1, "с"),
            Details = $"[{Desk}]\n  WaitToKillAppTimeout = \"<мс>\" (REG_SZ)",
            Get = () => int.TryParse(Reg.Get(Desk, "WaitToKillAppTimeout")?.ToString(), out int ms) ? Math.Clamp(ms / 1000, 2, 20) : 20,
            Set = v => Reg.Set(Desk, "WaitToKillAppTimeout", (v * 1000).ToString())
        });
        t.Add(new Tweak
        {
            Category = P, Name = "Ожидание служб при выключении",
            Description = "Сколько Windows ждёт остановки служб. По умолчанию 5 с. Слишком мало — риск потери данных служб.",
            Range = (2, 20, 1, "с"),
            Details = @"[HKLM\SYSTEM\CurrentControlSet\Control]" + "\n  WaitToKillServiceTimeout = \"<мс>\" (REG_SZ)",
            Get = () => int.TryParse(Reg.Get(@"HKLM\SYSTEM\CurrentControlSet\Control", "WaitToKillServiceTimeout")?.ToString(), out int ms) ? Math.Clamp(ms / 1000, 2, 20) : 5,
            Set = v => Reg.Set(@"HKLM\SYSTEM\CurrentControlSet\Control", "WaitToKillServiceTimeout", (v * 1000).ToString())
        });

        const string U = "Обновления";
        t.Add(DeferSlider(U, "Отложить крупные обновления", "DeferFeatureUpdates", "DeferFeatureUpdatesPeriodInDays", 365, 15));
        t.Add(DeferSlider(U, "Отложить ежемесячные исправления", "DeferQualityUpdates", "DeferQualityUpdatesPeriodInDays", 30, 1));
        const string Ux = @"HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
        t.Add(RegSlider(U, null, "Активные часы: начало", "В это время Windows не перезагружается для установки обновлений. Работает и в Home.",
            Ux, "ActiveHoursStart", 0, 23, 1, "ч", 8, asString: false, fmt: v => $"{v:00}:00"));
        t.Add(RegSlider(U, null, "Активные часы: конец", "Разница с началом — не больше 18 часов.",
            Ux, "ActiveHoursEnd", 0, 23, 1, "ч", 17, asString: false, fmt: v => $"{v:00}:00"));
    }

    // ───────── Фабрики настроек со значением ─────────

    static Tweak RegChoice(string cat, string? group, string name, string desc, string key, string val,
        object?[] values, string[] options, int def, bool restart = false, Action<int>? live = null) => new()
    {
        Category = cat, Group = group, Name = name, Description = desc + (restart ? Restart : ""), Options = options,
        Details = $"[{key}]\n" + string.Join("\n", options.Select((o, i) => $"  {val} = {values[i] ?? "<удалить>"}   — {o}")),
        Get = () =>
        {
            var cur = Reg.Get(key, val)?.ToString();
            int i = Array.FindIndex(values, v => v?.ToString() == cur);
            return i >= 0 ? i : def;
        },
        Set = i =>
        {
            var v = values[Math.Clamp(i, 0, values.Length - 1)];
            if (v == null) Reg.Delete(key, val); else Reg.Set(key, val, v);
            live?.Invoke(i);
        }
    };

    static Tweak RegSlider(string cat, string? group, string name, string desc, string key, string val,
        int min, int max, int step, string unit, int def, bool asString, Action<int>? live = null, Func<int, string>? fmt = null) => new()
    {
        Category = cat, Group = group, Name = name, Description = desc,
        Range = (min, max, step, unit), Format = fmt,
        Details = $"[{key}]\n  {val} = <{min}…{max}>  ({(asString ? "REG_SZ" : "REG_DWORD")}), по умолчанию {def}" +
                  (live != null ? "\n\nПрименяется сразу через SystemParametersInfo." : ""),
        Get = () => int.TryParse(Reg.Get(key, val)?.ToString(), out int v) ? Math.Clamp(v, min, max) : def,
        Set = v =>
        {
            if (asString) Reg.Set(key, val, v.ToString()); else Reg.Set(key, val, v);
            live?.Invoke(v);
        }
    };

    /// <summary>Таймауты электропитания текущей схемы (от сети) через powercfg.</summary>
    static Tweak PowerSlider(string cat, string name, string sub, string setting, string change, int min, int max, int step) => new()
    {
        Category = cat, Name = name,
        Description = "Для текущей схемы электропитания, при питании от сети. 0 — никогда.",
        Range = (min, max, step, "мин"), Format = v => v == 0 ? "никогда" : $"{v} мин",
        Details = $"Применить: powercfg /change {change} <минуты>\nПроверка: powercfg /query SCHEME_CURRENT {sub} {setting}",
        Get = () =>
        {
            var m = Regex.Matches(Shell.Run("powercfg.exe", $"/query SCHEME_CURRENT {sub} {setting}"), @"0x([0-9a-fA-F]{8})");
            if (m.Count < 2) return 0;
            int sec = Convert.ToInt32(m[m.Count - 2].Groups[1].Value, 16); // предпоследнее — «от сети», последнее — «от батареи»
            return Math.Clamp(sec / 60, min, max);
        },
        Set = v => Shell.Run("powercfg.exe", $"/change {change} {v}")
    };

    /// <summary>Отсрочка обновлений (политика, Pro и выше). 0 — не откладывать.</summary>
    static Tweak DeferSlider(string cat, string name, string flag, string days, int max, int step) => new()
    {
        Category = cat, Name = name,
        Description = "Windows Update будет ждать указанное число дней после выхода обновления." + Pro,
        Range = (0, max, step, "дн."), Format = v => v == 0 ? "не откладывать" : $"{v} дн.",
        Details = $"[{WU}]\n  {flag} = 1\n  {days} = <дни>\n0 — удалить оба параметра.",
        Get = () => Equals(Reg.Get(WU, flag), 1) && int.TryParse(Reg.Get(WU, days)?.ToString(), out int d) ? Math.Clamp(d, 0, max) : 0,
        Set = v =>
        {
            if (v == 0) { Reg.Delete(WU, flag); Reg.Delete(WU, days); }
            else { Reg.Set(WU, flag, 1); Reg.Set(WU, days, v); }
        }
    };
}

/// <summary>Мгновенное применение без перезахода: SystemParametersInfo и запуск программ.</summary>
static class Live
{
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint uiParam, IntPtr pvParam, uint winIni);
    [DllImport("user32.dll")] static extern bool SetDoubleClickTime(uint ms);

    /// <summary>SPIF_UPDATEINIFILE | SPIF_SENDCHANGE — сохранить и разослать изменение.</summary>
    public static void Spi(uint action, uint ui, IntPtr pv)
    {
        try { SystemParametersInfo(action, ui, pv, 0x01 | 0x02); } catch { }
    }

    public static void DoubleClick(int ms)
    {
        try { SetDoubleClickTime((uint)ms); } catch { }
    }

    public static void Start(string file, string args = "")
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args) { UseShellExecute = true }); } catch { }
    }
}
