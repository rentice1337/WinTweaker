using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WinTweaker;

/// <summary>
/// Значок в трее: логотип WinTweak с точкой состояния (зелёная — всё в порядке, жёлтая — есть совет,
/// красная — проблема, цвет акцента — идёт игровой режим). Левый клик и Ctrl+Alt+W — круговой пульт,
/// правый клик — короткое меню.
/// </summary>
sealed class Tray : IDisposable
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    readonly NotifyIcon icon = new() { Text = "WinTweak", Visible = true };
    readonly MainForm main;
    readonly HotkeyWindow hotkey;
    TrayWheel? wheel;
    DateTime wheelClosed;
    IntPtr lastHandle;
    Color lastDot, lastAccent;
    int tick;

    public static Hints.Hint Current { get; private set; } = Hints.Fallback;
    public bool HotkeyOk => hotkey.Ok;

    public Tray(MainForm main)
    {
        this.main = main;
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleWheel(Cursor.Position); };

        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), Font = Theme.Body };
        menu.Items.Add("Открыть WinTweak", null, (_, _) => main.ShowFromTray());
        menu.Items.Add("Пульт   (Ctrl+Alt+W)", null, (_, _) => ToggleWheel(Cursor.Position));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => main.ExitApp());
        icon.ContextMenuStrip = menu;

        hotkey = new HotkeyWindow(() => ToggleWheel(Cursor.Position));
        SetHotkey(AppSettings.Get("hotkey", "1") == "1");

        Sampler.Updated += Update;
        GameMode.Changed += Refresh;
        Update();
    }

    /// <summary>Включить/выключить глобальную горячую клавишу Ctrl+Alt+W. Возвращает, удалось ли её занять.</summary>
    public bool SetHotkey(bool on) => on ? hotkey.Register() : hotkey.Unregister();

    public void Balloon(string title, string text) => icon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info);

    public void ToggleWheel(Point at)
    {
        if (wheel is { IsDisposed: false }) { wheel.CloseAnimated(); return; }
        // клик по значку при открытом пульте сначала закрывает его (потеря фокуса) — не открываем заново
        if ((DateTime.Now - wheelClosed).TotalMilliseconds < 250) return;
        wheel = new TrayWheel(main, at);
        wheel.FormClosed += (_, _) => { wheelClosed = DateTime.Now; wheel = null; Refresh(); };
        wheel.Show();
        wheel.Activate();
    }

    void Refresh()
    {
        tick = 0;
        Update();
    }

    void Update()
    {
        var s = Sampler.Current;
        string ping = s.PingMs is int p ? $" · пинг {p} мс" : "";
        string mode = GameMode.Active ? "\nИгровой режим: " + GameMode.Reason : "";
        var text = $"WinTweak — ОЗУ {s.Ram:0}% · CPU {s.Cpu:0}%{ping}{mode}";
        icon.Text = text.Length > 127 ? text[..127] : text;

        // подсказка и цвет точки — раз в 5 секунд (и сразу после событий)
        if (tick++ % 5 == 0)
        {
            try { Current = Hints.Compute(main); } catch { Current = Hints.Fallback; }
        }
        var dot = GameMode.Active ? Theme.Accent : Hints.Tint(Current.Level);
        if (dot == lastDot && lastAccent == Theme.Accent && lastHandle != IntPtr.Zero) return;
        lastDot = dot;
        lastAccent = Theme.Accent;
        var h = Draw(dot);
        icon.Icon = Icon.FromHandle(h);
        if (lastHandle != IntPtr.Zero) DestroyIcon(lastHandle);
        lastHandle = h;
    }

    /// <summary>Плитка логотипа и точка состояния в правом нижнем углу (с тёмной обводкой, чтобы читалась на любой панели).</summary>
    static IntPtr Draw(Color dot)
    {
        int s = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            Logo.Tile(g, new RectangleF(0, 0, s * 0.86f, s * 0.86f));
            float d = s * 0.46f;
            var r = new RectangleF(s - d - 0.5f, s - d - 0.5f, d, d);
            using (var ring = new SolidBrush(Color.FromArgb(255, 12, 13, 18))) g.FillEllipse(ring, RectangleF.Inflate(r, s * 0.07f, s * 0.07f));
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, r);
        }
        return bmp.GetHicon();
    }

    public void Dispose()
    {
        Sampler.Updated -= Update;
        GameMode.Changed -= Refresh;
        wheel?.Close();
        hotkey.Unregister();
        hotkey.DestroyHandle();
        icon.Visible = false;
        icon.Dispose();
        if (lastHandle != IntPtr.Zero) DestroyIcon(lastHandle);
    }
}

/// <summary>Невидимое окно для глобальной горячей клавиши Ctrl+Alt+W.</summary>
sealed class HotkeyWindow : NativeWindow
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    const int Id = 0x5754;
    readonly Action pressed;
    public bool Ok { get; private set; }

    public HotkeyWindow(Action pressed)
    {
        this.pressed = pressed;
        CreateHandle(new CreateParams());
    }

    public bool Register()
    {
        if (Ok) return true;
        // MOD_ALT | MOD_CONTROL | MOD_NOREPEAT
        Ok = RegisterHotKey(Handle, Id, 0x0001 | 0x0002 | 0x4000, (uint)Keys.W);
        return Ok;
    }

    public bool Unregister()
    {
        if (Ok) UnregisterHotKey(Handle, Id);
        Ok = false;
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 /* WM_HOTKEY */ && (int)m.WParam == Id) pressed();
        base.WndProc(ref m);
    }
}

/// <summary>Одна главная подсказка «прямо сейчас» — по текущему состоянию ПК.</summary>
static class Hints
{
    public sealed record Hint(string Text, Readiness.Level Level, string Glyph, Action<MainForm>? Act = null);

    public static readonly Hint Fallback = new("Всё в порядке", Readiness.Level.Good, "");

    static List<MatchReport>? matches;
    static DateTime matchesAt;

    public static Color Tint(Readiness.Level l) => l switch
    {
        Readiness.Level.Bad => Theme.Bad,
        Readiness.Level.Warn => Theme.Warn,
        Readiness.Level.Info => System.Drawing.Color.FromArgb(120, 170, 255),
        _ => Theme.Good
    };

    public static Hint Compute(MainForm main)
    {
        var s = Sampler.Current;
        if (GameWatcher.Current is { } run)
        {
            string ping = GameMode.Monitor?.Last is int p ? $" · пинг {p} мс" : "";
            return new($"{run.Name} · {GameUI.Duration(DateTime.Now - run.Since)}{ping}", Readiness.Level.Info, "",
                m => m.OpenPage(MainForm.GamesPageName));
        }
        if (main.RevertedCount is int rv and > 0)
            return new($"Windows откатила {rv} {WatchBanner.Plural(rv)} — вернуть", Readiness.Level.Bad, "", m => m.FixRevertedFromTray());
        if (main.PendingCount is int pc and > 0)
            return new($"Изменений ждут «Применить»: {pc}", Readiness.Level.Warn, "", m => m.ReviewPending());

        double down = s.DownHist.TakeLast(5).DefaultIfEmpty(0).Average();
        if (down > 1024 * 1024)
            return new($"Что-то качает {Sampler.Speed(down)} — перед игрой лучше закрыть", Readiness.Level.Warn, "",
                m => m.OpenPage(MainForm.GamesPageName));
        if (s.Ram >= 85)
        {
            var top = s.Top.FirstOrDefault();
            string who = top.Name != null ? $" — больше всех {top.Name} ({GameUI.Bytes(top.Bytes)})" : "";
            return new($"Память занята на {s.Ram:0}%{who}", Readiness.Level.Warn, "", m => m.OpenPage(MainForm.GamesPageName));
        }
        double cpu = s.CpuHist.TakeLast(5).DefaultIfEmpty(0).Average();
        if (cpu > 50)
            return new($"Процессор занят на {cpu:0}% — что-то работает в фоне", Readiness.Level.Warn, "",
                _ => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); } catch { } });
        if (Reg.KeyExists(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            return new("Обновление Windows ждёт перезагрузки", Readiness.Level.Warn, "");
        if (SysInfo.UptimeDays > 7)
            return new($"ПК не перезагружался {SysInfo.UptimeDays:0} дн.", Readiness.Level.Info, "");

        if (matches == null || (DateTime.Now - matchesAt).TotalSeconds > 30) { matches = Matches.Load(); matchesAt = DateTime.Now; }
        var last = matches.FirstOrDefault();
        if (last != null && (DateTime.Now - last.End).TotalHours < 12)
        {
            var (_, tone) = last.Verdict();
            return new($"{last.Game}: {last.Avg:0} мс ±{last.Jitter:0}" + (tone == Tone.Success ? " — стабильно" : " — были скачки"),
                tone == Tone.Success ? Readiness.Level.Good : Readiness.Level.Warn, "", m => m.OpenReport(last));
        }
        return new($"Всё в порядке · работает {SysInfo.Uptime()}", Readiness.Level.Good, "");
    }
}

/// <summary>Автозапуск вместе с Windows: задание планировщика (без запроса UAC при каждом входе).</summary>
static class Autostart
{
    public const string Task = @"WinTweaker\Autostart";

    public static bool Installed() => Shell.RunCode("schtasks.exe", $"/Query /TN \"{Task}\"") == 0;

    public static void Install()
    {
        string exe = Environment.ProcessPath ?? Application.ExecutablePath;
        int code = Shell.RunCode("schtasks.exe", $"/Create /F /TN \"{Task}\" /SC ONLOGON /RL HIGHEST /DELAY 0000:20 /TR \"\\\"{exe}\\\" --tray\"");
        if (code != 0) throw new InvalidOperationException("Не удалось создать задание автозапуска (код " + code + ").");
    }

    public static void Uninstall() => Shell.RunCode("schtasks.exe", $"/Delete /F /TN \"{Task}\"");
}
