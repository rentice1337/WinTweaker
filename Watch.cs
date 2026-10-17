using System.Diagnostics;

namespace WinTweaker;

/// <summary>
/// «Сторож настроек». Крупные обновления Windows нередко возвращают политики, службы и удалённые приложения.
/// По журналу «Истории» восстанавливаем, что именно включал пользователь, и сравниваем с текущим состоянием.
/// Работает в окне программы (баннер на «Главной») и без окна — заданием планировщика при входе в Windows.
/// </summary>
static class Watch
{
    public const string TaskName = @"WinTweaker\Watch";

    public static string Key(Tweak t) => t.Category + "\u0001" + t.Name;

    /// <summary>Настройки, которые по журналу сейчас должны быть включены (откаченные сеансы не учитываются).</summary>
    public static HashSet<string> AppliedByUs()
    {
        var state = new Dictionary<string, bool>();
        foreach (var s in Journal.LoadAll().OrderBy(s => s.Date))
        {
            if (s.RolledBack) continue;
            foreach (var i in s.Items) state[i.Category + "\u0001" + i.Name] = i.NowApplied;
        }
        return state.Where(kv => kv.Value).Select(kv => kv.Key).ToHashSet();
    }

    /// <summary>Что Windows откатила: включали через WinTweaker, а сейчас выключено.</summary>
    public static List<Tweak> Reverted(IEnumerable<Tweak> tweaks, Func<Tweak, bool> isOn)
    {
        var set = AppliedByUs();
        if (set.Count == 0) return new();
        return tweaks.Where(t => !t.IsAction && set.Contains(Key(t)) && !isOn(t)).ToList();
    }

    // ───────── Задание планировщика ─────────

    public static bool Installed() => Shell.RunCode("schtasks.exe", $"/Query /TN \"{TaskName}\"") == 0;

    public static void Install()
    {
        string exe = Environment.ProcessPath ?? Application.ExecutablePath;
        int code = Shell.RunCode("schtasks.exe",
            $"/Create /F /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /DELAY 0001:00 /TR \"\\\"{exe}\\\" --watch\"");
        if (code != 0) throw new InvalidOperationException("Не удалось создать задание планировщика (код " + code + ").");
    }

    public static void Uninstall() => Shell.RunCode("schtasks.exe", $"/Delete /F /TN \"{TaskName}\"");

    // ───────── Режим без окна: WinTweaker.exe --watch ─────────

    public static void RunHeadless()
    {
        var set = AppliedByUs();
        if (set.Count == 0) return;
        var reverted = Catalog.All()
            .Where(t => !t.IsAction && set.Contains(Key(t)))
            .Where(t => { try { return !t.IsApplied(); } catch { return false; } })
            .ToList();
        if (reverted.Count == 0) return;

        using var icon = new NotifyIcon { Visible = true, Text = "WinTweaker" };
        try { icon.Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); }
        catch { icon.Icon = SystemIcons.Warning; }

        void Open()
        {
            try { Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? Application.ExecutablePath) { UseShellExecute = true }); }
            catch { }
            Application.ExitThread();
        }
        icon.BalloonTipClicked += (_, _) => Open();
        icon.Click += (_, _) => Open();

        string names = string.Join(", ", reverted.Take(3).Select(t => t.Name)) + (reverted.Count > 3 ? " и др." : "");
        icon.ShowBalloonTip(20000, $"Windows откатила {reverted.Count} {WatchBanner.Plural(reverted.Count)}",
            names + "\nНажмите, чтобы вернуть в WinTweaker.", ToolTipIcon.Warning);

        var timer = new System.Windows.Forms.Timer { Interval = 30000 };
        timer.Tick += (_, _) => Application.ExitThread();
        timer.Start();
        Application.Run();
        icon.Visible = false;
    }
}
