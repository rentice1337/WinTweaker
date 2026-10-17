namespace WinTweaker;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Задание «Сторожа» при входе в Windows: проверить откаты и показать уведомление, без окна
        if (args.Any(a => a.Equals("--watch", StringComparison.OrdinalIgnoreCase)))
        {
            Watch.RunHeadless();
            return;
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
