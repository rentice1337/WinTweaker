namespace WinTweaker;

/// <summary>
/// Каталог настроек для Windows 10 22H2 (сборка 19045).
/// Ревизия: убраны устаревшие пункты (Wi-Fi Sense, «Люди»), политики вместо легко откатываемых
/// пользовательских ключей, службы через SCM (sc config), пометки о редакциях Home/Pro.
///
/// R — один параметр реестра, MX — несколько, S — службы, A — UWP-приложение, Act — разовое действие.
/// off = null → параметр удаляется (возврат к поведению по умолчанию).
/// </summary>
public static partial class Catalog
{
    const string Adv = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    const string ExpU = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer";
    const string CDM = @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    const string Pers = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    const string Search = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Search";
    const string WU = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    const string SysPol = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    const string PolSys = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System";
    const string DC = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection";

    const string Pro = "\n\nРаботает в Pro, Enterprise и Education (в Home политика игнорируется).";
    const string Ent = "\n\nРаботает только в Enterprise и Education.";
    const string Relogin = "\n\nВступит в силу после выхода из системы.";

    public static List<Tweak> All()
    {
        var t = new List<Tweak>();
        Privacy(t);
        Interface(t);
        Explorer(t);
        Performance(t);
        Security(t);
        Apps(t);
        Services(t);
        Updates(t);
        Games(t);
        Maintenance(t);
        Network(t);
        t.AddRange(RussiaNetwork.All());
        More(t);
        return t;
    }

    // ───────── Конфиденциальность ─────────
    static void Privacy(List<Tweak> t)
    {
        const string C = "Конфиденциальность";
        t.Add(MX(C, "Минимальная телеметрия",
            "Устанавливает минимальный уровень диагностических данных и скрывает уведомления о нём. " +
            "В Home/Pro минимум — «Обязательные данные» (уровень 1): значение 0 Windows сама поднимет до 1. " +
            "Для полного отключения отправки используйте вместе со службой DiagTrack (раздел «Службы»).",
            false, false,
            (DC, "AllowTelemetry", 0, null),
            (DC, "DoNotShowFeedbackNotifications", 1, null)));
        t.Add(MX(C, "Отключить рекламный идентификатор", "Приложения не получат ID для персонализированной рекламы.", false, false,
            (@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1),
            (@"HKLM\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1, null)));
        t.Add(R(C, "Отключить Кортану", "Политика AllowCortana. Само приложение можно удалить в разделе «Приложения».",
            @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0, null));
        t.Add(MX(C, "Отключить веб-поиск и Bing в меню «Пуск»",
            "Поиск ищет только на компьютере. Для Windows 10 2004+ надёжно работает политика DisableSearchBoxSuggestions.",
            false, false,
            (@"HKCU\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null),
            (Search, "BingSearchEnabled", 0, null)));
        t.Add(MX(C, "Отключить журнал действий", "Windows не собирает и не отправляет историю запуска приложений и открытия файлов.", false, false,
            (PolSys, "EnableActivityFeed", 0, null),
            (PolSys, "PublishUserActivities", 0, null),
            (PolSys, "UploadUserActivities", 0, null)));
        t.Add(MX(C, "Отключить облачный буфер обмена", "Буфер обмена не синхронизируется между устройствами.", false, false,
            (PolSys, "AllowCrossDeviceClipboard", 0, null)));
        t.Add(R(C, "Запретить доступ к местоположению",
            "Запрещает доступ к геолокации для всех приложений на этом ПК.",
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", "Deny", "Allow"));
        t.Add(MX(C, "Отключить советы, рекламу и автоустановку приложений",
            "Убирает «предложения» в Пуске и Параметрах, экран приветствия после обновлений и тихую установку Candy Crush и т.п.",
            false, false,
            (CDM, "SubscribedContent-310093Enabled", 0, 1), (CDM, "SubscribedContent-338388Enabled", 0, 1),
            (CDM, "SubscribedContent-338389Enabled", 0, 1), (CDM, "SubscribedContent-353694Enabled", 0, 1),
            (CDM, "SubscribedContent-353696Enabled", 0, 1), (CDM, "SystemPaneSuggestionsEnabled", 0, 1),
            (CDM, "SilentInstalledAppsEnabled", 0, 1), (CDM, "SoftLandingEnabled", 0, 1),
            (CDM, "OemPreInstalledAppsEnabled", 0, 1), (CDM, "PreInstalledAppsEnabled", 0, 1)));
        t.Add(MX(C, "Отключить сбор рукописного и клавиатурного ввода",
            "Windows не отправляет образцы набора текста и рукописного ввода для «улучшения распознавания».", false, false,
            (@"HKCU\Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1, 0),
            (@"HKCU\Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1, 0)));
        t.Add(R(C, "Отключить запросы отзывов", "Windows перестанет спрашивать мнение о системе.",
            @"HKCU\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, null));
        t.Add(R(C, "Отключить персонализацию по диагностическим данным", "Microsoft не будет подбирать советы и рекламу на основе ваших данных.",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1));
        t.Add(R(C, "Отключить рекламу на экране блокировки", "Убирает «интересные факты» и рекламу Windows Spotlight.",
            CDM, "RotatingLockScreenOverlayEnabled", 0, 1));
    }

    // ───────── Интерфейс ─────────
    static void Interface(List<Tweak> t)
    {
        const string C = "Интерфейс";
        t.Add(R(C, "Тёмная тема приложений", "", Pers, "AppsUseLightTheme", 0, 1));
        t.Add(R(C, "Тёмная тема Windows (панель задач, Пуск)", "", Pers, "SystemUsesLightTheme", 0, 1));
        t.Add(R(C, "Отключить прозрачность", "Немного снижает нагрузку на видеокарту.", Pers, "EnableTransparency", 0, 1));
        t.Add(R(C, "Скрыть кнопку «Представление задач»", "Win+Tab продолжит работать.", Adv, "ShowTaskViewButton", 0, 1));
        t.Add(R(C, "Отключить «Новости и интересы»",
            "Виджет погоды и новостей на панели задач. Через политику — Windows не включит его обратно после обновления.",
            @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Feeds", "EnableFeeds", 0, null));
        t.Add(MX(C, "Скрыть кнопку «Провести собрание»", "Значок Skype Meet Now в трее.", false, false,
            (@"HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "HideSCAMeetNow", 1, null),
            (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "HideSCAMeetNow", 1, null)));
        t.Add(R(C, "Показывать все значки в трее", "Значки не прячутся под стрелку.", ExpU, "EnableAutoTray", 0, 1));
        t.Add(R(C, "Маленькие значки на панели задач", "", Adv, "TaskbarSmallIcons", 1, 0));
        t.Add(R(C, "Показывать секунды в часах", "", Adv, "ShowSecondsInSystemClock", 1, 0));
        t.Add(R(C, "Отключить Aero Shake", "Окна не сворачиваются при «встряхивании» заголовка.", Adv, "DisallowShaking", 1, null));
        t.Add(R(C, "Отключить экран блокировки", "Сразу показывается окно входа." + Ent,
            @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoLockScreen", 1, null));
        t.Add(R(C, "Отключить анимацию сворачивания окон", Relogin, @"HKCU\Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", "1"));
        t.Add(R(C, "Мгновенное открытие подменю", "Убирает задержку 400 мс при наведении на пункты меню." + Relogin,
            @"HKCU\Control Panel\Desktop", "MenuShowDelay", "0", "400"));
        t.Add(R(C, "Отключить окно залипания клавиш (5× Shift)", "Важно для игр: окно больше не выскакивает.",
            @"HKCU\Control Panel\Accessibility\StickyKeys", "Flags", "506", "510"));
        t.Add(R(C, "Включать NumLock при загрузке", "Для экрана входа.",
            @"HKU\.DEFAULT\Control Panel\Keyboard", "InitialKeyboardIndicators", "2", "0"));
    }

    // ───────── Проводник ─────────
    static void Explorer(List<Tweak> t)
    {
        const string C = "Проводник";
        t.Add(R(C, "Показывать расширения файлов", "Важно для безопасности: видно, что «фото.jpg.exe» — программа.", Adv, "HideFileExt", 0, 1));
        t.Add(R(C, "Показывать скрытые файлы", "", Adv, "Hidden", 1, 2));
        t.Add(R(C, "Показывать системные файлы", "Защищённые файлы ОС (desktop.ini и т.п.). Не удаляйте их.", Adv, "ShowSuperHidden", 1, 0, risky: true));
        t.Add(R(C, "Открывать «Этот компьютер» вместо «Быстрого доступа»", "", Adv, "LaunchTo", 1, 2));
        t.Add(MX(C, "Не показывать недавние файлы и папки", "В «Быстром доступе».", false, false,
            (ExpU, "ShowRecent", 0, 1), (ExpU, "ShowFrequent", 0, 1)));
        t.Add(R(C, "Полный путь в заголовке окна", "", ExpU + @"\CabinetState", "FullPath", 1, 0));
        t.Add(KeyRemove(C, "Убрать папку «Объёмные объекты»", "Из раздела «Этот компьютер» (64- и 32-битные окна выбора файлов).",
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}",
            @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\{0DB7E03F-FC29-4DC6-9020-FF41B59E513A}"));
        t.Add(R(C, "Убрать OneDrive из панели навигации", "Только скрывает значок, OneDrive продолжает работать.",
            @"HKCU\Software\Classes\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}", "System.IsPinnedToNameSpaceTree", 0, 1));
        t.Add(R(C, "Поддержка длинных путей (>260 символов)", "Для программ, которые это поддерживают (git, npm, .NET).",
            @"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 1, 0));
    }

    // ───────── Производительность ─────────
    static void Performance(List<Tweak> t)
    {
        const string C = "Производительность";
        const string HP = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
        const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
        t.Add(new Tweak
        {
            Category = C, Name = "Схема питания «Высокая производительность»",
            Details = "Применить:\n  powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c\n  (если схемы нет — powercfg -duplicatescheme e9a42b02-… из «Максимальной производительности»)\n\nОтменить: вернуть схему, активную до применения (запоминается в журнале).",
            Description = "Процессор не снижает частоту. Если схемы нет (ноутбуки с Modern Standby), она создаётся из шаблона «Максимальная производительность». На батарее садится быстрее.",
            IsApplied = () => Shell.Run("powercfg", "/getactivescheme").Contains(HP, StringComparison.OrdinalIgnoreCase),
            Apply = () =>
            {
                if (!Shell.Run("powercfg", "/list").Contains(HP, StringComparison.OrdinalIgnoreCase))
                    Shell.Run("powercfg", $"-duplicatescheme {Ultimate} {HP}");
                Shell.Run("powercfg", $"/setactive {HP}");
            },
            Revert = () => Shell.Run("powercfg", $"/setactive {Balanced}"),
            Snapshot = () => System.Text.RegularExpressions.Regex.Match(Shell.Run("powercfg", "/getactivescheme"),
                @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Value,
            Restore = guid => { if (guid != "") Shell.Run("powercfg", $"/setactive {guid}"); }
        });
        t.Add(new Tweak
        {
            Category = C, Name = "Отключить гибернацию",
            Details = "Применить: powercfg /hibernate off  — удаляет C:\\hiberfil.sys\nОтменить: powercfg /hibernate on",
            Description = "Удаляет hiberfil.sys (освобождает место размером ~40% ОЗУ). Заодно отключает быстрый запуск.",
            IsApplied = () => Equals(Reg.Get(@"HKLM\SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled"), 0),
            Apply = () => Shell.Run("powercfg", "/hibernate off"),
            Revert = () => Shell.Run("powercfg", "/hibernate on")
        });
        t.Add(R(C, "Отключить быстрый запуск",
            "Выключение становится полным: решает проблемы с драйверами, обновлениями и двойной загрузкой. Загрузка чуть дольше.",
            @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, 1));
        t.Add(R(C, "Убрать задержку автозагрузки", "Программы из автозагрузки стартуют сразу, без 10-секундной паузы.",
            ExpU + @"\Serialize", "StartupDelayInMSec", 0, null));
        t.Add(MX(C, "Запретить фоновые приложения", "UWP-приложения (Почта, Погода и т.п.) не работают в фоне.", false, false,
            (@"HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1, 0),
            (Search, "BackgroundAppGlobalToggle", 0, 1)));

        byte[] fxBest = { 0x90, 0x12, 0x03, 0x80, 0x10, 0x00, 0x00, 0x00 };
        byte[] fxDefault = { 0x9E, 0x1E, 0x07, 0x80, 0x12, 0x00, 0x00, 0x00 };
        const string VE = ExpU + @"\VisualEffects";
        t.Add(new Tweak
        {
            Category = C, Name = "Визуальные эффекты: быстродействие",
            Details = "[HKCU\\...\\Explorer\\VisualEffects]\n  VisualFXSetting = 3 (особые)\n[HKCU\\Control Panel\\Desktop]\n  UserPreferencesMask = 90 12 03 80 10 00 00 00\n  FontSmoothing = \"2\"\n\nОтменить: VisualFXSetting = 0, маска по умолчанию.",
            Description = "Отключает тени, анимации и плавное перетаскивание, оставляя сглаживание шрифтов и эскизы." + Relogin,
            IsApplied = () => Equals(Reg.Get(VE, "VisualFXSetting"), 3) && Reg.Get(@"HKCU\Control Panel\Desktop", "UserPreferencesMask") is byte[] m && m.SequenceEqual(fxBest),
            Apply = () =>
            {
                Reg.Set(VE, "VisualFXSetting", 3); // 3 = «Особые эффекты»
                Reg.Set(@"HKCU\Control Panel\Desktop", "UserPreferencesMask", fxBest);
                Reg.Set(@"HKCU\Control Panel\Desktop", "FontSmoothing", "2");
            },
            Revert = () =>
            {
                Reg.Set(VE, "VisualFXSetting", 0);
                Reg.Set(@"HKCU\Control Panel\Desktop", "UserPreferencesMask", fxDefault);
            }
        });
        t.Add(MX(C, "Приоритет мультимедиа и игр", "Снимает ограничение сети для мультимедиа и отдаёт больше CPU активным играм/звуку.", false, true,
            (@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", -1, 10),
            (@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 10, 20)));
        t.Add(R(C, "Аппаратное планирование GPU (HAGS)",
            "Снижает задержки в играх. Нужны Windows 10 2004+, GeForce GTX 10xx / Radeon RX 5000 и новее и свежий драйвер.",
            @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, 1, restart: true));
    }

    // ───────── Безопасность ─────────
    static void Security(List<Tweak> t)
    {
        const string C = "Безопасность";
        t.Add(new Tweak
        {
            Category = C, Name = "Отключить SMBv1",
            Details = "PowerShell:\n  Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol -NoRestart\nОтменить:\n  Enable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol -All -NoRestart",
            Description = "Устаревший протокол, через который распространялся WannaCry. Нужен только очень старым NAS и принтерам.",
            NeedsRestart = true,
            IsApplied = () => Shell.PS("(Get-WindowsOptionalFeature -Online -FeatureName SMB1Protocol).State").Trim() is "Disabled" or "DisablePending",
            Apply = () => Shell.PS("Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol -NoRestart | Out-Null"),
            Revert = () => Shell.PS("Enable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol -All -NoRestart | Out-Null")
        });
        t.Add(new Tweak
        {
            Category = C, Name = "Защита от нежелательных программ (PUA)",
            Details = "PowerShell:\n  Set-MpPreference -PUAProtection Enabled\nОтменить:\n  Set-MpPreference -PUAProtection Disabled",
            Description = "Microsoft Defender начнёт блокировать «рекламное» ПО: тулбары, майнеры и установщики с довеском. Не работает, если стоит другой антивирус.",
            IsApplied = () => Shell.PS("(Get-MpPreference).PUAProtection").Trim() == "1",
            Apply = () => Shell.PS("Set-MpPreference -PUAProtection Enabled"),
            Revert = () => Shell.PS("Set-MpPreference -PUAProtection Disabled")
        });
        t.Add(R(C, "Отключить LLMNR", "Устаревший способ поиска имён в сети, которым пользуются для перехвата паролей в локальных сетях.",
            @"HKLM\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient", "EnableMulticast", 0, null));
        t.Add(R(C, "Подробные сообщения при входе и выключении", "Видно, на каком этапе «висит» загрузка.", SysPol, "VerboseStatus", 1, null));
        t.Add(R(C, "Не затемнять экран при запросе UAC",
            "Запрос UAC остаётся, но без «безопасного рабочего стола». Снижает защиту от вредоносных программ, имитирующих нажатия.",
            SysPol, "PromptOnSecureDesktop", 0, 1, risky: true));
        t.Add(R(C, "Отключить SmartScreen для программ", "Опасно: исчезнет предупреждение при запуске подозрительных скачанных файлов.",
            PolSys, "EnableSmartScreen", 0, null, risky: true));
        t.Add(new Tweak
        {
            Category = C, Name = "Папка «Все настройки» (God Mode) на рабочем столе",
            Details = "Создаёт на рабочем столе папку\n  GodMode.{ED7BA470-8E54-465E-825C-99712043E01C}\nОтменить — удалить папку.",
            Description = "Папка со всеми ~200 настройками панели управления в одном списке.",
            IsApplied = () => Directory.Exists(GodModePath),
            Apply = () => Directory.CreateDirectory(GodModePath),
            Revert = () => { if (Directory.Exists(GodModePath)) Directory.Delete(GodModePath); }
        });
    }

    static string GodModePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "GodMode.{ED7BA470-8E54-465E-825C-99712043E01C}");

    // ───────── Приложения ─────────
    static void Apps(List<Tweak> t)
    {
        t.Add(A("Кортана", "Microsoft.549981C3F5F10"));
        t.Add(A("Погода", "Microsoft.BingWeather"));
        t.Add(A("Новости", "Microsoft.BingNews"));
        t.Add(A("Техническая поддержка", "Microsoft.GetHelp"));
        t.Add(A("Советы", "Microsoft.Getstarted"));
        t.Add(A("Office (реклама подписки)", "Microsoft.MicrosoftOfficeHub"));
        t.Add(A("OneNote для Windows 10", "Microsoft.Office.OneNote"));
        t.Add(A("Solitaire Collection", "Microsoft.MicrosoftSolitaireCollection"));
        t.Add(A("Люди", "Microsoft.People"));
        t.Add(A("Skype", "Microsoft.SkypeApp"));
        t.Add(A("Центр отзывов", "Microsoft.WindowsFeedbackHub"));
        t.Add(A("Карты", "Microsoft.WindowsMaps"));
        t.Add(A("Groove Музыка", "Microsoft.ZuneMusic"));
        t.Add(A("Кино и ТВ", "Microsoft.ZuneVideo"));
        t.Add(A("Связь с телефоном", "Microsoft.YourPhone"));
        t.Add(A("Средство 3D-просмотра", "Microsoft.Microsoft3DViewer"));
        t.Add(A("Paint 3D", "Microsoft.MSPaint"));
        t.Add(A("Портал смешанной реальности", "Microsoft.MixedReality.Portal"));
        t.Add(A("Candy Crush и игры King", "king.com"));
        t.Add(R("Приложения", "Запретить синхронизацию OneDrive",
            "Политика DisableFileSyncNGSC: OneDrive не запускается и не синхронизирует файлы. Сами файлы в папке OneDrive не удаляются.",
            @"HKLM\SOFTWARE\Policies\Microsoft\Windows\OneDrive", "DisableFileSyncNGSC", 1, null));
        t.Add(Act("Приложения", "Действия", "Удалить OneDrive полностью",
            "Завершает OneDrive и запускает штатный деинсталлятор OneDriveSetup.exe /uninstall. Файлы в папке OneDrive остаются на диске. " +
            "Вернуть: скачать установщик с сайта Microsoft.",
            () =>
            {
                Shell.Run("taskkill.exe", "/f /im OneDrive.exe");
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                foreach (var p in new[] { Path.Combine(win, "SysWOW64", "OneDriveSetup.exe"), Path.Combine(win, "System32", "OneDriveSetup.exe") })
                    if (File.Exists(p)) { Shell.Run(p, "/uninstall"); break; }
            }, risky: true));
    }

    // ───────── Службы ─────────
    static void Services(List<Tweak> t)
    {
        t.Add(S("Телеметрия (DiagTrack)", "«Функциональные возможности для подключённых пользователей и телеметрия» — основная служба отправки данных.", "auto", false, "DiagTrack"));
        t.Add(S("WAP Push (dmwappushservice)", "Маршрутизация push-сообщений, используется телеметрией.", "demand", false, "dmwappushservice"));
        t.Add(S("Отчёты об ошибках Windows", "Windows не будет собирать и отправлять отчёты о сбоях.", "demand", false, "WerSvc"));
        t.Add(S("Диспетчер скачанных карт", "Нужен только приложению «Карты».", "delayed-auto", false, "MapsBroker"));
        t.Add(S("Служба геолокации", "Без неё не работают определение местоположения и автосмена часового пояса.", "demand", true, "lfsvc"));
        t.Add(S("Службы Xbox", "Не отключайте, если играете через Xbox App / Game Pass или используете геймпад Xbox по беспроводу.",
            "demand", false, "XblAuthManager", "XblGameSave", "XboxNetApiSvc", "XboxGipSvc"));
        t.Add(S("Факс", "", "demand", false, "Fax"));
        t.Add(S("Демо-режим магазина", "", "demand", false, "RetailDemo"));
        t.Add(S("SysMain (Superfetch)", "Предзагрузка программ в ОЗУ. Отключать имеет смысл на HDD при постоянной 100% загрузке диска; на SSD вреда от службы нет.",
            "auto", true, "SysMain"));
        t.Add(S("Индексатор поиска (WSearch)", "Поиск по файлам и в Outlook станет заметно медленнее.", "delayed-auto", true, "WSearch"));
        t.Add(S("Диспетчер печати", "Только если нет принтера и не нужна «Печать в PDF».", "auto", true, "Spooler"));
    }

    // ───────── Обновления ─────────
    static void Updates(List<Tweak> t)
    {
        const string C = "Обновления";
        t.Add(MX(C, "Остаться на Windows 10 22H2",
            "Фиксирует версию: Windows Update не предложит переход на Windows 11. Обновления безопасности для 22H2 продолжают приходить.",
            false, false,
            (WU, "TargetReleaseVersion", 1, null),
            (WU, "ProductVersion", "Windows 10", null),
            (WU, "TargetReleaseVersionInfo", "22H2", null)));
        t.Add(R(C, "Не обновлять драйверы через Windows Update", "Драйверы ставите сами — Windows не заменит их на старые «совместимые»." + Pro,
            WU, "ExcludeWUDriversInQualityUpdate", 1, null));
        t.Add(MX(C, "Спрашивать перед загрузкой обновлений", "Windows только уведомляет о наличии обновлений." + Pro, false, false,
            (WU + @"\AU", "NoAutoUpdate", 0, null),
            (WU + @"\AU", "AUOptions", 2, null)));
        t.Add(R(C, "Не перезагружать, пока я в системе", "Перезагрузка для установки — только когда вы её запустите." + Pro,
            WU + @"\AU", "NoAutoRebootWithLoggedOnUsers", 1, null));
        t.Add(R(C, "Не раздавать обновления другим ПК", "Отключает P2P-оптимизацию доставки (экономит исходящий трафик).",
            @"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, null));
        t.Add(R(C, "Полностью отключить автообновление", "Опасно: исправления безопасности перестанут приходить автоматически." + Pro,
            WU + @"\AU", "NoAutoUpdate", 1, null, risky: true));
    }

    // ───────── Игры ─────────
    static void Games(List<Tweak> t)
    {
        const string C = "Игры";
        t.Add(MX(C, "Отключить фоновую запись (Game DVR)", "Запись последних минут игры в фоне ест FPS. Игровая панель Win+G останется.", false, false,
            (@"HKCU\System\GameConfigStore", "GameDVR_Enabled", 0, 1),
            (@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1),
            (@"HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, null)));
        t.Add(R(C, "Игровой режим", "Windows отдаёт приоритет игре и не ставит обновления во время неё. Включён по умолчанию в 22H2.",
            @"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, 0));
        t.Add(MX(C, "Отключить оптимизацию во весь экран", "Настоящий эксклюзивный полноэкранный режим в старых DirectX 9–11 играх. Может снизить задержку ввода.", false, false,
            (@"HKCU\System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2, 0),
            (@"HKCU\System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 1, 0)));
        t.Add(MX(C, "Отключить ускорение мыши", "Флажок «Повышенная точность установки указателя». Движение мыши становится линейным." + Relogin, false, false,
            (@"HKCU\Control Panel\Mouse", "MouseSpeed", "0", "1"),
            (@"HKCU\Control Panel\Mouse", "MouseThreshold1", "0", "6"),
            (@"HKCU\Control Panel\Mouse", "MouseThreshold2", "0", "10")));
        GameNetwork(t);
    }

    // ───── Сеть для игр ─────
    const string TcpIf = @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    /// <summary>Интерфейсы TCP/IP, у которых есть адрес (по DHCP или вручную) — то есть реально используемые.</summary>
    static List<string> ActiveInterfaces()
    {
        var list = new List<string>();
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(TcpIf[5..]);
            if (root == null) return list;
            foreach (var id in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(id);
                string dhcp = k?.GetValue("DhcpIPAddress")?.ToString() ?? "";
                string ip = (k?.GetValue("IPAddress") as string[])?.FirstOrDefault() ?? "";
                bool has(string a) => a != "" && a != "0.0.0.0";
                if (has(dhcp) || has(ip)) list.Add(TcpIf + "\\" + id);
            }
        }
        catch { }
        return list;
    }

    static void GameNetwork(List<Tweak> t)
    {
        const string C = "Игры", G = "Сеть для игр";
        t.Add(new Tweak
        {
            Category = C, Group = G, Name = "Не усыплять сетевую карту",
            Description = "Windows не выключает сетевой адаптер для экономии энергии — меньше обрывов и скачков пинга после простоя. " +
                          "Заодно выключается пробуждение по сети (Wake-on-LAN). При применении сеть переподключится на пару секунд.",
            Details = "PowerShell (физические адаптеры):\n  Get-NetAdapter -Physical | Disable-NetAdapterPowerManagement\nОтменить:\n  Get-NetAdapter -Physical | Enable-NetAdapterPowerManagement",
            IsApplied = () =>
            {
                var r = Shell.PS("$p = Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Get-NetAdapterPowerManagement -ErrorAction SilentlyContinue; " +
                                 "if (-not $p) { 'none' } elseif (@($p | Where-Object { $_.AllowComputerToTurnOffDevice -eq 'Enabled' }).Count -gt 0) { 'on' } else { 'off' }");
                return r.Trim() == "off";
            },
            Apply = () => Shell.PS("Get-NetAdapter -Physical | Disable-NetAdapterPowerManagement -ErrorAction SilentlyContinue"),
            Revert = () => Shell.PS("Get-NetAdapter -Physical | Enable-NetAdapterPowerManagement -ErrorAction SilentlyContinue")
        });
        t.Add(new Tweak
        {
            Category = C, Group = G, Name = "Отключить алгоритм Нейгла (TCP без задержки)",
            NeedsRestart = true,
            Description = "Мелкие TCP-пакеты уходят сразу, без накопления. Влияет только на TCP: часть MMO, старые онлайн-игры, лаунчеры и голосовые чаты. " +
                          "Большинство современных шутеров работают по UDP — им это не поможет и не навредит.",
            Details = "Для каждого сетевого интерфейса с IP-адресом:\n  [" + TcpIf + "\\{GUID}]\n    TcpAckFrequency = 1  (REG_DWORD)\n    TCPNoDelay = 1  (REG_DWORD)\n\n" +
                      "Отменить: удалить оба параметра (поведение по умолчанию).",
            IsApplied = () =>
            {
                var ifs = ActiveInterfaces();
                return ifs.Count > 0 && ifs.All(k => Reg.Get(k, "TcpAckFrequency")?.ToString() == "1" && Reg.Get(k, "TCPNoDelay")?.ToString() == "1");
            },
            Apply = () =>
            {
                foreach (var k in ActiveInterfaces()) { Reg.Set(k, "TcpAckFrequency", 1); Reg.Set(k, "TCPNoDelay", 1); }
            },
            Revert = () =>
            {
                foreach (var k in ActiveInterfaces()) { Reg.Delete(k, "TcpAckFrequency"); Reg.Delete(k, "TCPNoDelay"); }
            }
        });
    }

    // ───────── Обслуживание (разовые действия) ─────────
    static void Maintenance(List<Tweak> t)
    {
        const string C = "Обслуживание";
        t.Add(Act(C, "Очистка", "Удалить временные файлы", "Чистит %TEMP% и C:\\Windows\\Temp. Занятые файлы пропускаются.",
            () => Shell.PS(@"Get-ChildItem -Path $env:TEMP, (Join-Path $env:SystemRoot 'Temp') -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue")));
        t.Add(Act(C, "Очистка", "Очистить кэш Windows Update",
            "Останавливает службы обновления, очищает SoftwareDistribution\\Download и запускает их снова. Помогает при «застрявших» обновлениях.",
            () =>
            {
                Shell.Run("net.exe", "stop wuauserv");
                Shell.Run("net.exe", "stop bits");
                Shell.PS(@"Remove-Item (Join-Path $env:SystemRoot 'SoftwareDistribution\Download\*') -Recurse -Force -ErrorAction SilentlyContinue");
                Shell.Run("net.exe", "start bits");
                Shell.Run("net.exe", "start wuauserv");
            }));
        t.Add(Act(C, "Очистка", "Сжать хранилище компонентов (WinSxS)",
            "DISM /StartComponentCleanup: удаляет старые версии системных компонентов после обновлений. Занимает 5–15 минут.",
            () => Shell.Run("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup")));
        t.Add(Act(C, "Восстановление", "Восстановить системные файлы",
            "Сначала DISM /RestoreHealth (нужен интернет), затем sfc /scannow. Занимает 15–40 минут. Журнал: C:\\Windows\\Logs\\CBS\\CBS.log.",
            () =>
            {
                Shell.Run("dism.exe", "/Online /Cleanup-Image /RestoreHealth");
                Shell.Run("sfc.exe", "/scannow");
            }));
        t.Add(Act(C, "Восстановление", "Перезапустить Проводник", "Помогает, если зависла панель задач или не применились настройки интерфейса.",
            RestartExplorer));
        t.Add(new Tweak
        {
            Category = C, Group = "WinTweaker", Name = "Сторож настроек при входе в Windows",
            Description = "При каждом входе в систему WinTweaker тихо проверяет, не откатила ли Windows ваши настройки (обычно после крупных обновлений), " +
                          "и показывает уведомление — по клику всё возвращается. Сторож запускается по пути, где лежит exe: если переместите файл, включите заново.",
            Details = $"Задание планировщика «{Watch.TaskName}»:\n  schtasks /Create /TN \"{Watch.TaskName}\" /SC ONLOGON /RL HIGHEST /DELAY 0001:00\n" +
                      "           /TR \"<путь к WinTweaker.exe> --watch\"\nОтменить:\n  schtasks /Delete /TN \"" + Watch.TaskName + "\" /F",
            IsApplied = Watch.Installed,
            Apply = Watch.Install,
            Revert = Watch.Uninstall
        });
        t.Add(Act(C, "Программы", "Обновить все программы (winget)",
            "winget upgrade --all: тихо обновляет установленные программы. Нужен «Установщик приложений» из Microsoft Store.",
            () => Shell.Run("cmd.exe", "/c winget upgrade --all --silent --accept-package-agreements --accept-source-agreements")));
    }

    // ───────── Сеть ─────────
    static void Network(List<Tweak> t)
    {
        const string C = "Сеть";
        t.Add(R(C, "Предпочитать IPv4 вместо IPv6",
            "Официальный способ Microsoft (DisabledComponents = 0x20). Помогает, если у провайдера кривой IPv6 и сайты открываются с задержкой.",
            @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 0x20, null, restart: true));
        t.Add(Act(C, "Действия", "Очистить кэш DNS", "ipconfig /flushdns — если сайт «переехал», а открывается старый адрес.",
            () => Shell.Run("ipconfig.exe", "/flushdns")));
        t.Add(Act(C, "Действия", "Сбросить сетевые настройки",
            "netsh winsock reset и netsh int ip reset. Помогает при «интернет есть, но ничего не открывается». " +
            "Сбросит статические IP-адреса. Нужна перезагрузка.",
            () => { Shell.Run("netsh.exe", "winsock reset"); Shell.Run("netsh.exe", "int ip reset"); }, risky: true));
    }

    public static void RestartExplorer() =>
        Shell.Run("cmd.exe", "/c taskkill /f /im explorer.exe & start explorer.exe");

    // ───────── Фабрики ─────────

    static Tweak R(string cat, string name, string desc, string key, string val, object on, object? off,
        bool risky = false, bool restart = false) =>
        MX(cat, name, desc, risky, restart, (key, val, on, off));

    static Tweak MX(string cat, string name, string desc, bool risky, bool restart,
        params (string key, string val, object on, object? off)[] items) => new()
    {
        Category = cat, Name = name, Description = desc.TrimStart('\n'), Risky = risky, NeedsRestart = restart,
        Details = RegDetails(items),
        IsApplied = () => items.All(i => Reg.Get(i.key, i.val)?.ToString() == i.on.ToString()),
        Apply = () => { foreach (var i in items) Reg.Set(i.key, i.val, i.on); },
        Revert = () =>
        {
            foreach (var i in items)
                if (i.off == null) Reg.Delete(i.key, i.val); else Reg.Set(i.key, i.val, i.off);
        }
    };

    /// <summary>Текст «что меняется» для настроек из параметров реестра.</summary>
    static string RegDetails((string key, string val, object on, object? off)[] items)
    {
        static string Fmt(object v) => v switch
        {
            string str => $"\"{str}\"  (REG_SZ)",
            byte[] b => string.Join(" ", b.Select(x => x.ToString("X2"))) + "  (REG_BINARY)",
            int i => $"{i}  (REG_DWORD, 0x{i:X})",
            _ => v.ToString() ?? ""
        };
        var sb = new System.Text.StringBuilder("Применить:\n");
        foreach (var g in items.GroupBy(i => i.key))
        {
            sb.Append("  [").Append(g.Key).Append("]\n");
            foreach (var i in g) sb.Append("    ").Append(i.val).Append(" = ").Append(Fmt(i.on)).Append('\n');
        }
        sb.Append("\nОтменить:\n");
        foreach (var i in items)
            sb.Append("    ").Append(i.val).Append(i.off == null ? " → удалить параметр (поведение по умолчанию)" : " = " + Fmt(i.off)).Append('\n');
        sb.Append("\nПри откате из «Истории» возвращаются точные прежние значения.");
        return sb.ToString();
    }

    static string SvcKey(string s) => @"HKLM\SYSTEM\CurrentControlSet\Services\" + s;

    /// <summary>Отключение служб через диспетчер служб (sc config), а не прямой правкой реестра.</summary>
    static Tweak S(string name, string desc, string startMode, bool risky, params string[] services) => new()
    {
        Category = "Службы", Name = "Отключить: " + name, Risky = risky,
        Details = "Применить:\n" + string.Join("\n", services.Select(s => $"  sc stop {s}\n  sc config {s} start= disabled")) +
                  "\n\nОтменить:\n" + string.Join("\n", services.Select(s => $"  sc config {s} start= <прежний тип запуска>")) +
                  "\n\nПрежний тип запуска запоминается в журнале и возвращается при откате.",
        Description = (desc == "" ? "" : desc + "\n\n") + "Службы: " + string.Join(", ", services) + ".",
        IsApplied = () => services.Where(s => Reg.KeyExists(SvcKey(s))).All(s => Equals(Reg.Get(SvcKey(s), "Start"), 4)),
        // снимок: служба=Start,DelayedAutostart — откат вернёт ровно прежний тип запуска
        Snapshot = () => string.Join(";", services.Where(s => Reg.KeyExists(SvcKey(s)))
            .Select(s => $"{s}={Reg.Get(SvcKey(s), "Start")},{Reg.Get(SvcKey(s), "DelayedAutostart") ?? 0}")),
        Restore = snap =>
        {
            foreach (var part in snap.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                var v = kv[1].Split(',');
                string mode = (v[0], v[1]) switch
                {
                    ("2", "1") => "delayed-auto",
                    ("2", _) => "auto",
                    ("3", _) => "demand",
                    ("4", _) => "disabled",
                    _ => ""
                };
                if (mode == "") continue;
                Shell.Run("sc.exe", $"config {kv[0]} start= {mode}");
                if (mode is "auto" or "delayed-auto") Shell.Run("sc.exe", $"start {kv[0]}");
            }
        },
        Apply = () =>
        {
            foreach (var s in services.Where(s => Reg.KeyExists(SvcKey(s))))
            {
                Shell.Run("sc.exe", $"stop {s}");
                Shell.Run("sc.exe", $"config {s} start= disabled");
            }
        },
        Revert = () =>
        {
            foreach (var s in services.Where(s => Reg.KeyExists(SvcKey(s))))
            {
                Shell.Run("sc.exe", $"config {s} start= {startMode}");
                if (startMode != "demand") Shell.Run("sc.exe", $"start {s}");
            }
        }
    };

    static Tweak A(string name, string package) => new()
    {
        Category = "Приложения", Group = "Удаление встроенных приложений", Name = name,
        Details = $"PowerShell:\n  Get-AppxPackage -AllUsers '*{package}*' | Remove-AppxPackage -AllUsers\n" +
                  $"  Get-AppxProvisionedPackage -Online | Where DisplayName -like '*{package}*' | Remove-AppxProvisionedPackage -Online\n\n" +
                  "Отменить программой нельзя — приложение возвращается через Microsoft Store.",
        Description = $"Удаляет {package} у всех пользователей и из образа для новых пользователей. Вернуть можно из Microsoft Store.",
        IsApplied = () => !AppxCache.Has(package),
        Apply = () =>
        {
            Shell.PS($"Get-AppxPackage -AllUsers '*{package}*' | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue; " +
                     $"Get-AppxProvisionedPackage -Online | Where-Object DisplayName -like '*{package}*' | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Out-Null");
            AppxCache.Reset();
        },
        Revert = null
    };

    public static Tweak Act(string cat, string? group, string name, string desc, Action run, bool risky = false, string? details = null) => new()
    {
        Category = cat, Group = group, Name = name, Description = desc, Risky = risky, IsAction = true,
        Details = details ?? "Разовое действие, переключателя нет.\n\n" + desc,
        IsApplied = () => false, Apply = run
    };

    static Tweak KeyRemove(string cat, string name, string desc, params string[] keys) => new()
    {
        Category = cat, Name = name, Description = desc,
        Details = "Применить — удалить разделы реестра:\n" + string.Join("\n", keys.Select(k => "  " + k)) + "\n\nОтменить — создать их снова.",
        IsApplied = () => keys.All(k => !Reg.KeyExists(k)),
        Apply = () => { foreach (var k in keys) Reg.DeleteKey(k); },
        Revert = () => { foreach (var k in keys) Reg.CreateKey(k); }
    };
}
