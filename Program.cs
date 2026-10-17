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

        // колесо мыши — тому, кто под курсором (своя прокрутка без полос)
        Application.AddMessageFilter(new WheelFilter());
        Application.Run(new MainForm());
    }
}
