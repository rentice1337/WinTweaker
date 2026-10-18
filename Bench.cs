using System.Diagnostics;
using System.Text;

namespace WinTweaker;

/// <summary>
/// Замер плавности интерфейса (скрытый режим «--bench=путь», только для проверки сборки, пользователю не виден):
/// окно само переключает разделы и вкладки, прокручивает страницу и водит «курсор» над карточками,
/// а интервалы между кадрами анимаций пишутся в файл.
/// </summary>
public partial class MainForm
{
    public static string? BenchOut;

    static string Stats(string name, List<double> v)
    {
        if (v.Count < 3) return $"{name}: мало данных ({v.Count})";
        var s = v.OrderBy(x => x).ToList();
        double med = s[s.Count / 2], p90 = s[(int)(s.Count * 0.9)], avg = s.Average();
        int slow = s.Count(x => x > 33.4);
        return $"{name}: кадров {s.Count}, медиана {med:0.0} мс ({1000 / med:0} FPS), p90 {p90:0.0} мс ({1000 / p90:0} FPS), " +
               $"среднее {avg:0.0} мс, медленнее 30 FPS: {100.0 * slow / s.Count:0}%, худший {s[^1]:0} мс";
    }

    static List<double> Gaps(List<double> ticks)
    {
        var g = new List<double>();
        for (int i = 1; i < ticks.Count; i++) if (ticks[i] - ticks[i - 1] < 1000) g.Add(ticks[i] - ticks[i - 1]);
        return g;
    }

    async Task WaitIdle(int maxMs)
    {
        var sw = Stopwatch.StartNew();
        await Task.Delay(30);
        while (Motion.Busy && sw.ElapsedMilliseconds < maxMs) await Task.Delay(15);
    }

    async Task RunBench()
    {
        var sb = new StringBuilder();
        try
        {
            var scr = Screen.FromControl(this).Bounds;
            WindowState = FormWindowState.Normal;
            Bounds = new Rectangle(scr.X, scr.Y, Math.Min(1600, scr.Width), Math.Min(1000, scr.Height));
            sb.AppendLine($"экран {scr.Width}x{scr.Height}, окно {Width}x{Height}, ядер {Environment.ProcessorCount}");
            var sw = Stopwatch.StartNew();
            while (!loaded && sw.Elapsed.TotalSeconds < 180) await Task.Delay(100);
            sb.AppendLine($"проверка настроек при запуске: {sw.ElapsedMilliseconds} мс");
            await Task.Delay(1500);

            // 1) переключение разделов и вкладок
            var builds = new List<double>();
            var ticks = new List<double>();
            var pages = AllPages.Select(p => p.Cat).Where(c => c != selected).ToList();
            for (int round = 0; round < 2; round++)
                foreach (var cat in pages)
                {
                    Motion.Record = new List<double>();
                    var t = Stopwatch.StartNew();
                    SelectCategory(cat);
                    builds.Add(t.Elapsed.TotalMilliseconds);
                    await WaitIdle(2500);
                    ticks.AddRange(Gaps(Motion.Record));
                    Motion.Record = null;
                    await Task.Delay(150);
                }
            var bs = builds.OrderBy(x => x).ToList();
            sb.AppendLine($"построение страницы (до первого кадра): медиана {bs[bs.Count / 2]:0} мс, p90 {bs[(int)(bs.Count * 0.9)]:0} мс, худшее {bs[^1]:0} мс");
            sb.AppendLine(Stats("анимация перехода", ticks));

            // 2) прокрутка длинной страницы
            SelectCategory(Programs.Cat);
            await WaitIdle(3000);
            await Task.Delay(300);
            Motion.Record = new List<double>();
            for (int i = 0; i < 30; i++) { view.Wheel(-120); await Task.Delay(45); }
            for (int i = 0; i < 30; i++) { view.Wheel(120); await Task.Delay(45); }
            await WaitIdle(2000);
            sb.AppendLine(Stats("прокрутка", Gaps(Motion.Record)));
            Motion.Record = null;

            // 3) подсветка рамок: курсор ходит над карточками (каждый шаг рисуется сразу)
            var area = view.RectangleToScreen(view.ClientRectangle);
            var moves = new List<double>();
            for (int i = 0; i < 240; i++)
            {
                var p = new Point(area.Left + 40 + (i * 23) % Math.Max(1, area.Width - 80), area.Top + 40 + (i * 37) % Math.Max(1, area.Height - 80));
                var t = Stopwatch.StartNew();
                Spotlight.Move(p);
                moves.Add(t.Elapsed.TotalMilliseconds);
                if (i % 20 == 0) await Task.Delay(1);
            }
            var ms = moves.OrderBy(x => x).ToList();
            sb.AppendLine($"подсветка рамок за шаг курсора: медиана {ms[ms.Count / 2]:0.0} мс, p90 {ms[(int)(ms.Count * 0.9)]:0.0} мс");
        }
        catch (Exception ex) { sb.AppendLine("ошибка замера: " + ex); }
        try { File.WriteAllText(BenchOut!, sb.ToString()); } catch { }
        Environment.Exit(0);
    }
}
