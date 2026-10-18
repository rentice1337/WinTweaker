namespace WinTweaker;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // программа работает только с правами администратора (реестр HKLM, службы, трассировка кадров для FPS)
        if (!Elevation.IsAdmin)
        {
            if (Elevation.Relaunch(args)) return;
            MessageBox.Show("WinTweak нужны права администратора: без них нельзя менять системные настройки, службы и считать FPS.\n\n" +
                            "Запустите программу снова и подтвердите запрос Windows.", "WinTweak", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Задание «Сторожа» при входе в Windows: проверить откаты и показать уведомление, без окна
        if (args.Any(a => a.Equals("--watch", StringComparison.OrdinalIgnoreCase)))
        {
            Watch.RunHeadless();
            return;
        }

        // замер плавности интерфейса для проверки сборки (не для пользователей)
        if (args.FirstOrDefault(a => a.StartsWith("--bench=", StringComparison.OrdinalIgnoreCase)) is string b)
        {
            MainForm.BenchOut = b["--bench=".Length..];
            AppSettings.Set("lite", "0");
        }

        // одна копия: повторный запуск показывает уже работающую (например, из трея)
        using var single = new Mutex(true, "WinTweak.Single", out bool first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting("WinTweak.Show").Set(); } catch { }
            return;
        }

        bool hidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        var form = new MainForm(hidden);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, "WinTweak.Show");
        new Thread(() =>
        {
            while (show.WaitOne())
            {
                try { form.BeginInvoke(form.ShowFromTray); }
                catch { return; }
            }
        }) { IsBackground = true }.Start();

        // колесо мыши — тому, кто под курсором (своя прокрутка без полос)
        Application.AddMessageFilter(new WheelFilter());
        Application.Run(form);
    }
}

/// <summary>Права администратора: проверка и перезапуск с запросом UAC.</summary>
static class Elevation
{
    public static bool IsAdmin
    {
        get
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>Перезапустить себя от администратора. false — пользователь отказался.</summary>
    public static bool Relaunch(string[] args)
    {
        try
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            var psi = new System.Diagnostics.ProcessStartInfo(exe, string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)))
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch { return false; }
    }
}
