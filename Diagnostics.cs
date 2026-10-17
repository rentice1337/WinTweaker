using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace WinTweaker;

/// <summary>Данные из журналов Windows: время загрузки и аварийные завершения.</summary>
static class Diag
{
    public sealed record Boot(DateTime When, int Ms);
    public sealed record Crash(DateTime When, string Kind, uint Code, string Dump);

    /// <summary>
    /// Событие 100 журнала Diagnostics-Performance: полное время загрузки до готового рабочего стола.
    /// Пишется только при полной загрузке (с включённым быстрым запуском — после «Перезагрузки»).
    /// </summary>
    public static List<Boot> Boots()
    {
        const string ps =
            "Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Diagnostics-Performance/Operational';Id=100} -MaxEvents 30 -ErrorAction SilentlyContinue | " +
            "ForEach-Object { $x=[xml]$_.ToXml(); $b=($x.Event.EventData.Data | Where-Object { $_.Name -eq 'BootTime' }).'#text'; " +
            "$_.TimeCreated.ToString('o') + '|' + $b }";
        var list = new List<Boot>();
        foreach (var line in Lines(Shell.PS(ps)))
        {
            var p = line.Split('|');
            if (p.Length == 2 && DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
                && int.TryParse(p[1], out int ms) && ms > 0 && ms < 30 * 60 * 1000)
                list.Add(new Boot(d.ToLocalTime(), ms));
        }
        return list.OrderBy(b => b.When).ToList();
    }

    /// <summary>BSOD (1001, BugCheck) и аварийные выключения (41, Kernel-Power) за полгода.</summary>
    public static List<Crash> Crashes()
    {
        const string ps =
            "Get-WinEvent -FilterHashtable @{LogName='System';Id=41,1001;StartTime=(Get-Date).AddDays(-180)} -ErrorAction SilentlyContinue | " +
            "ForEach-Object { $code=''; $dump=''; $kind=''; " +
            "if ($_.Id -eq 41) { $x=[xml]$_.ToXml(); $c=($x.Event.EventData.Data | Where-Object { $_.Name -eq 'BugcheckCode' }).'#text'; " +
            "  if ($c -and $c -ne '0') { $code=[string]$c } ; $kind='power' } " +
            "else { if ($_.ProviderName -notmatch 'WER-SystemErrorReporting|BugCheck') { return }; $m=$_.Message; " +
            "  if ($m -match '0x[0-9a-fA-F]{8}') { $code=$matches[0] }; if ($m -match '[A-Za-z]:\\\\[^\\s]+\\.dmp') { $dump=$matches[0] }; $kind='bsod' } " +
            "$_.TimeCreated.ToString('o') + '|' + $kind + '|' + $code + '|' + $dump }";
        var raw = new List<Crash>();
        foreach (var line in Lines(Shell.PS(ps)))
        {
            var p = line.Split('|');
            if (p.Length < 4 || !DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)) continue;
            raw.Add(new Crash(d.ToLocalTime(), p[1], ParseCode(p[2]), p[3].TrimEnd('.', ',')));
        }
        // Kernel-Power 41 с кодом ошибки дублирует BSOD-запись рядом по времени — оставляем BSOD
        var bsods = raw.Where(c => c.Kind == "bsod").ToList();
        var result = raw.Where(c => c.Kind == "bsod" ||
            !bsods.Any(b => Math.Abs((b.When - c.When).TotalMinutes) < 20)).ToList();
        return result.OrderByDescending(c => c.When).ToList();
    }

    static uint ParseCode(string s)
    {
        s = s.Trim();
        if (s == "") return 0;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : 0;
        return uint.TryParse(s, out var v) ? v : 0;
    }

    static IEnumerable<string> Lines(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Самые частые коды BSOD — по-человечески.</summary>
    static readonly Dictionary<uint, (string Name, string What, string Fix)> Codes = new()
    {
        [0x0A] = ("IRQL_NOT_LESS_OR_EQUAL", "Драйвер обратился к памяти, к которой в этот момент нельзя обращаться.", "Обновите драйверы сети, звука и чипсета; удалите сторонний антивирус; проверьте ОЗУ (mdsched)."),
        [0x19] = ("BAD_POOL_HEADER", "Повреждена служебная область памяти ядра — почти всегда виноват драйвер.", "Обновите драйверы, вспомните, что ставили недавно; проверьте ОЗУ."),
        [0x1A] = ("MEMORY_MANAGEMENT", "Ошибка управления памятью.", "Отключите разгон/XMP и проверьте ОЗУ (mdsched или MemTest86); обновите BIOS."),
        [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", "Драйвер вызвал необработанное исключение.", "Обновите или откатите недавно установленные драйверы."),
        [0x24] = ("NTFS_FILE_SYSTEM", "Ошибка файловой системы NTFS.", "Проверьте диск: chkdsk C: /f и состояние SSD/HDD (CrystalDiskInfo)."),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", "Исключение в системной службе — часто видеодрайвер или антивирус.", "Переустановите драйвер видеокарты начисто (DDU); удалите сторонний антивирус."),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", "Обращение к несуществующей памяти.", "Проверьте ОЗУ; обновите драйверы; отключите сторонний антивирус."),
        [0x7A] = ("KERNEL_DATA_INPAGE_ERROR", "Не удалось прочитать данные с диска в память.", "Проверьте кабель/разъём диска, здоровье SSD/HDD и файл подкачки."),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "Системный поток драйвера упал с ошибкой.", "Обновите драйверы (особенно видео); если началось после обновления — откатите его."),
        [0x7F] = ("UNEXPECTED_KERNEL_MODE_TRAP", "Аппаратное прерывание, с которым ядро не справилось.", "Часто железо: перегрев, разгон, ОЗУ. Сбросьте разгон, проверьте температуры."),
        [0x9C] = ("MACHINE_CHECK_EXCEPTION", "Процессор сообщил об аппаратной ошибке.", "Проверьте охлаждение, питание и разгон CPU."),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", "Драйвер не справился со сном или пробуждением.", "Обновите драйверы (Wi-Fi, USB, видео); отключите быстрый запуск."),
        [0xBE] = ("ATTEMPTED_WRITE_TO_READONLY_MEMORY", "Драйвер попытался записать в память только для чтения.", "Обновите или удалите недавно установленный драйвер."),
        [0xC2] = ("BAD_POOL_CALLER", "Драйвер неправильно работает с памятью ядра.", "Обновите драйверы; удалите сторонний антивирус и утилиты-«ускорители»."),
        [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "Драйвер обратился к недопустимой памяти — чаще всего сетевой.", "Обновите драйвер сетевой карты/Wi-Fi с сайта производителя."),
        [0xEA] = ("THREAD_STUCK_IN_DEVICE_DRIVER", "Драйвер видеокарты завис в бесконечном цикле.", "Переустановите драйвер видеокарты; проверьте температуру и питание GPU."),
        [0xEF] = ("CRITICAL_PROCESS_DIED", "Завершился критически важный процесс Windows.", "Восстановите системные файлы (раздел «Обслуживание») и проверьте диск."),
        [0xF4] = ("CRITICAL_OBJECT_TERMINATION", "Упал критический системный процесс — часто из-за диска.", "Проверьте SATA-кабель и здоровье диска; восстановите системные файлы."),
        [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", "Ядро процессора перестало отвечать.", "Сбросьте разгон/андервольт, обновите BIOS, проверьте температуру CPU."),
        [0x116] = ("VIDEO_TDR_FAILURE", "Видеодрайвер завис и не смог перезапуститься.", "Чистая переустановка драйвера GPU (DDU); снимите разгон видеокарты; проверьте блок питания."),
        [0x117] = ("VIDEO_TDR_TIMEOUT_DETECTED", "Видеокарта перестала отвечать.", "Обновите драйвер GPU, проверьте температуру и разгон."),
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", "Неисправимая аппаратная ошибка (процессор, память, шина).", "Сбросьте разгон и XMP, проверьте температуры и блок питания, обновите BIOS."),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", "Драйвер слишком долго не отдавал процессор.", "Обновите прошивку SSD и драйвер контроллера (SATA AHCI/NVMe), драйверы чипсета."),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", "Ядро обнаружило повреждение своих структур.", "Обновите драйверы, проверьте ОЗУ, восстановите системные файлы."),
        [0x13A] = ("KERNEL_MODE_HEAP_CORRUPTION", "Драйвер повредил память ядра.", "Обновите драйверы, особенно видео и периферии."),
        [0x144] = ("BUGCODE_USB3_DRIVER", "Ошибка драйвера USB 3.", "Обновите драйвер чипсета/USB; попробуйте другой порт или хаб."),
        [0x154] = ("UNEXPECTED_STORE_EXCEPTION", "Ошибка подсистемы хранения памяти — часто диск.", "Обновите прошивку SSD, проверьте здоровье диска; отключите сторонний антивирус."),
    };

    public static (string Name, string What, string Fix) Explain(Crash c)
    {
        if (c.Kind == "power" && c.Code == 0)
            return ("Аварийное выключение",
                "Компьютер выключился, не завершив работу: пропало питание, его выключили кнопкой при зависании, либо сработала защита от перегрева или перегрузки БП.",
                "Если это не вы держали кнопку — проверьте температуры под нагрузкой и блок питания.");
        if (Codes.TryGetValue(c.Code, out var e)) return e;
        return ($"Код 0x{c.Code:X8}", "Редкий код ошибки.", $"Поищите в интернете «0x{c.Code:X8}» вместе с названием вашего ПК или видеокарты.");
    }
}

/// <summary>Тест DNS: сырые UDP-запросы, медиана задержки по нескольким доменам.</summary>
static class DnsBench
{
    public sealed record Server(string Name, string Ip, string? Tweak);
    public sealed record Result(Server Server, double Ms, int Ok, int Total);

    public static readonly Server[] Servers =
    {
        new("Cloudflare", "1.1.1.1", "Cloudflare 1.1.1.1"),
        new("Google", "8.8.8.8", "Google 8.8.8.8"),
        new("Quad9", "9.9.9.9", "Quad9"),
        new("AdGuard", "94.140.14.14", "AdGuard DNS"),
        new("Яндекс", "77.88.8.8", "Яндекс DNS"),
        new("Comss", "83.220.169.155", "Comss DNS"),
        new("Xbox DNS", "176.99.11.77", "Xbox DNS"),
    };

    static readonly string[] Domains = { "google.com", "youtube.com", "vk.com", "microsoft.com", "github.com" };

    /// <summary>Тестирует серверы из списка и текущий DNS провайдера (если он другой).</summary>
    public static async Task<List<Result>> RunAsync(Action<int, int>? progress = null)
    {
        var servers = Servers.ToList();
        string? cur = Regex.Matches(DnsCache.Current, @"\b(\d{1,3}\.){3}\d{1,3}\b").Select(m => m.Value).FirstOrDefault();
        if (cur != null && servers.All(s => s.Ip != cur)) servers.Insert(0, new Server("Текущий", cur, null));

        int done = 0;
        var tasks = servers.Select(async s =>
        {
            var r = await Measure(s);
            progress?.Invoke(Interlocked.Increment(ref done), servers.Count);
            return r;
        });
        var results = await Task.WhenAll(tasks);
        return results.OrderBy(r => r.Ok == 0 ? double.MaxValue : r.Ms).ToList();
    }

    static async Task<Result> Measure(Server s)
    {
        if (!IPAddress.TryParse(s.Ip, out var ip)) return new Result(s, 0, 0, 0);
        foreach (var d in Domains) await Ask(ip, d); // прогрев: домены попадают в кэш сервера
        var times = new List<double>();
        int total = 0;
        for (int round = 0; round < 2; round++)
            foreach (var d in Domains)
            {
                total++;
                var t = await Ask(ip, d);
                if (t != null) times.Add(t.Value);
            }
        if (times.Count == 0) return new Result(s, 0, 0, total);
        times.Sort();
        return new Result(s, times[times.Count / 2], times.Count, total);
    }

    static async Task<double?> Ask(IPAddress ip, string domain)
    {
        try
        {
            using var udp = new UdpClient(ip.AddressFamily);
            ushort id = (ushort)Random.Shared.Next(1, 65535);
            var q = Query(domain, id);
            using var cts = new CancellationTokenSource(1500);
            var sw = Stopwatch.StartNew();
            await udp.SendAsync(q, new IPEndPoint(ip, 53), cts.Token);
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token);
                var b = r.Buffer;
                if (b.Length >= 12 && (b[0] << 8 | b[1]) == id && (b[2] & 0x80) != 0)
                    return sw.Elapsed.TotalMilliseconds;
            }
        }
        catch { return null; }
    }

    /// <summary>DNS-запрос типа A: заголовок (RD=1, один вопрос) + имя метками + тип/класс.</summary>
    static byte[] Query(string domain, ushort id)
    {
        var b = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in domain.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            b.Add((byte)bytes.Length);
            b.AddRange(bytes);
        }
        b.AddRange(new byte[] { 0, 0, 1, 0, 1 });
        return b.ToArray();
    }
}

// ═══════════════════════════════════════════════════════════════
//  Виджеты «Диагностики»
// ═══════════════════════════════════════════════════════════════

/// <summary>Время загрузки Windows по последним запускам; отмечено, когда начали работать твики.</summary>
class BootWidget : Widget
{
    List<Diag.Boot>? boots;
    DateTime? firstSession;
    int hover = -1;
    const int ChartH = 150;

    public BootWidget() : base("Время загрузки Windows", 28 + 70 + ChartH + 30) { Wide = true; }

    public void SetData(List<Diag.Boot> data, DateTime? first)
    {
        boots = data;
        firstSession = first;
        Invalidate(false);
    }

    RectangleF Chart => new(Body.Left + 22, Body.Top + 70, Body.Width - 44, ChartH);

    int BarAt(Point p)
    {
        if (boots == null || boots.Count == 0 || !Chart.Contains(p)) return -1;
        float step = Chart.Width / boots.Count;
        return Math.Clamp((int)((p.X - Chart.Left) / step), 0, boots.Count - 1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = BarAt(e.Location);
        if (h != hover) { hover = h; Invalidate(false); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(false); base.OnMouseLeave(e); }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
        if (boots == null)
        {
            TextRenderer.DrawText(g, "Читаю журнал событий" + new string('.', (int)(Motion.Now / 400) % 4), Theme.Body,
                new Rectangle(r.Left + 22, r.Top + 20, r.Width - 44, 24), C(Theme.Muted), flags);
            return;
        }
        if (boots.Count == 0)
        {
            TextRenderer.DrawText(g, "Записей о загрузке нет", Theme.Semi, new Rectangle(r.Left + 22, r.Top + 18, r.Width - 44, 22), C(Theme.Text), flags);
            TextRenderer.DrawText(g, "Windows пишет время только при полной загрузке. С включённым быстрым запуском — после «Перезагрузки», а не «Завершения работы».",
                Theme.Tiny, new Rectangle(r.Left + 22, r.Top + 42, r.Width - 44, 36), C(Theme.Muted), TextFormatFlags.WordBreak);
            return;
        }

        var before = firstSession == null ? boots : boots.Where(b => b.When < firstSession).ToList();
        var after = firstSession == null ? new List<Diag.Boot>() : boots.Where(b => b.When >= firstSession).ToList();
        string Sec(double ms) => (ms / 1000).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " с";

        // сводка
        if (before.Count > 0 && after.Count > 0)
        {
            double a = before.Average(b => b.Ms), z = after.Average(b => b.Ms);
            double delta = (z - a) / a * 100;
            TextRenderer.DrawText(g, $"{Sec(a)}  →  {Sec(z)}", Theme.F(17f, FontStyle.Bold), new Point(r.Left + 18, r.Top + 12), C(Theme.Text));
            var col = delta <= 0 ? Theme.Good : Theme.Warn;
            TextRenderer.DrawText(g, $"{(delta <= 0 ? "быстрее" : "медленнее")} на {Math.Abs(delta):0}% после WinTweaker · среднее до и после первого «Применить»",
                Theme.Tiny, new Rectangle(r.Left + 22, r.Top + 44, r.Width - 44, 18), C(col), flags);
        }
        else
        {
            double avg = boots.Average(b => b.Ms);
            TextRenderer.DrawText(g, Sec(avg), Theme.F(17f, FontStyle.Bold), new Point(r.Left + 18, r.Top + 12), C(Theme.Text));
            TextRenderer.DrawText(g, firstSession == null
                    ? $"в среднем за {boots.Count} загрузок · после первого «Применить» здесь появится сравнение «до/после»"
                    : $"в среднем · загрузок после WinTweaker ещё не было — перезагрузите ПК",
                Theme.Tiny, new Rectangle(r.Left + 22, r.Top + 44, r.Width - 44, 18), C(Theme.Muted), flags);
        }

        // столбики
        var chart = Chart;
        double max = boots.Max(b => b.Ms) * 1.1;
        float step = chart.Width / boots.Count, bw = Math.Max(4, step * 0.62f);
        using (var axis = new Pen(Color.FromArgb((int)(24 * Appear), 255, 255, 255)))
            g.DrawLine(axis, chart.Left, chart.Bottom, chart.Right, chart.Bottom);
        bool markDrawn = false;
        for (int i = 0; i < boots.Count; i++)
        {
            var b = boots[i];
            bool isAfter = firstSession != null && b.When >= firstSession;
            float hgt = (float)(b.Ms / max * chart.Height * Appear);
            float x = chart.Left + i * step + (step - bw) / 2;
            var bar = new RectangleF(x, chart.Bottom - hgt, bw, hgt);

            if (isAfter && !markDrawn)
            {
                markDrawn = true;
                float mx = chart.Left + i * step;
                using var dash = new Pen(Color.FromArgb((int)(140 * Appear), Theme.Accent), 1f) { DashStyle = DashStyle.Dash };
                g.DrawLine(dash, mx, chart.Top - 6, mx, chart.Bottom);
                TextRenderer.DrawText(g, "WinTweaker", Theme.Small, new Point((int)mx + 4, (int)chart.Top - 10), C(Theme.Accent));
            }
            var col = isAfter ? Theme.Accent : Color.FromArgb(150, 160, 175);
            if (i == hover) Theme.Glow(g, col, bar, 3, 3, 2f, (int)(90 * Appear));
            using (var grad = new LinearGradientBrush(RectangleF.Inflate(bar, 0, 1), Color.FromArgb((int)((i == hover ? 255 : 210) * Appear), col),
                       Color.FromArgb((int)(70 * Appear), col), 90f))
            using (var path = Theme.Round(bar, Math.Min(4, bw / 2)))
                g.FillPath(grad, path);
        }

        if (hover >= 0 && hover < boots.Count)
        {
            var b = boots[hover];
            string tip = $"{b.When:d MMM, HH:mm} · {Sec(b.Ms)}";
            var sz = TextRenderer.MeasureText(tip, Theme.Tiny);
            float cx = chart.Left + hover * step + step / 2;
            var box = new RectangleF(Math.Clamp(cx - sz.Width / 2f - 8, r.Left + 4, r.Right - sz.Width - 20), chart.Bottom + 6, sz.Width + 16, 20);
            Theme.Fill(g, Color.FromArgb(230, Theme.Hover), box, 6);
            TextRenderer.DrawText(g, tip, Theme.Tiny, Rectangle.Round(box), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else
        {
            TextRenderer.DrawText(g, $"{boots[0].When:d MMM}", Theme.Tiny, new Point((int)chart.Left, (int)chart.Bottom + 6), C(Theme.Dim));
            var last = $"{boots[^1].When:d MMM}";
            var lw = TextRenderer.MeasureText(last, Theme.Tiny).Width;
            TextRenderer.DrawText(g, last, Theme.Tiny, new Point((int)chart.Right - lw, (int)chart.Bottom + 6), C(Theme.Dim));
        }
    }
}

/// <summary>Сбои за полгода: BSOD и аварийные выключения, клик — объяснение и что делать.</summary>
class CrashWidget : Widget
{
    List<Diag.Crash>? crashes;
    int hover = -1;
    const int RowH = 48, MaxRows = 7;
    public event Action<Diag.Crash>? CrashClicked;

    public CrashWidget() : base("Сбои за полгода", 28 + 12 + RowH * 2 + 8) { }

    public void SetData(List<Diag.Crash> data)
    {
        crashes = data;
        Height = HeadH + 12 + Math.Max(2, Math.Min(MaxRows, data.Count) + (data.Count > MaxRows ? 1 : 0)) * RowH + 8;
        Invalidate(false);
    }

    int RowAt(Point p)
    {
        if (crashes == null) return -1;
        int i = (p.Y - HeadH - 12) / RowH;
        return p.Y > HeadH + 12 && i >= 0 && i < Math.Min(MaxRows, crashes.Count) ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(false); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(false); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int h = RowAt(e.Location);
        if (h >= 0 && crashes != null) CrashClicked?.Invoke(crashes[h]);
        base.OnMouseClick(e);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        if (crashes == null)
        {
            TextRenderer.DrawText(g, "Читаю системный журнал" + new string('.', (int)(Motion.Now / 400) % 4), Theme.Body,
                new Rectangle(r.Left + 18, r.Top + 12, r.Width - 36, 30), C(Theme.Muted), flags);
            return;
        }
        if (crashes.Count == 0)
        {
            var icon = new RectangleF(r.Left + 18, r.Top + 22, 40, 40);
            Theme.Fill(g, Color.FromArgb((int)(50 * Appear), Theme.Good), icon, 20);
            TextRenderer.DrawText(g, "", Theme.IconBig, Rectangle.Round(icon), C(Theme.Good), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "Сбоев не было", Theme.Semi, new Rectangle(r.Left + 72, r.Top + 22, r.Width - 90, 20), C(Theme.Text), flags);
            TextRenderer.DrawText(g, "Ни одного BSOD и аварийного выключения за 180 дней", Theme.Tiny, new Rectangle(r.Left + 72, r.Top + 42, r.Width - 90, 18), C(Theme.Muted), flags);
            return;
        }
        int n = Math.Min(MaxRows, crashes.Count);
        for (int i = 0; i < n; i++)
        {
            var c = crashes[i];
            var (name, _, _) = Diag.Explain(c);
            int y = r.Top + 12 + i * RowH;
            if (i == hover) Theme.Fill(g, Color.FromArgb(20, 255, 255, 255), new RectangleF(r.Left + 8, y, r.Width - 16, RowH - 6), 9);
            var col = c.Kind == "bsod" || c.Code != 0 ? Theme.Bad : Theme.Warn;
            var icon = new RectangleF(r.Left + 18, y + 6, 30, 30);
            Theme.Fill(g, Color.FromArgb((int)(50 * Appear), col), icon, 15);
            TextRenderer.DrawText(g, c.Kind == "bsod" || c.Code != 0 ? "" : "", Theme.Icon, Rectangle.Round(icon), C(col),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, name, Theme.Semi, new Rectangle(r.Left + 60, y + 3, r.Width - 100, 20), C(Theme.Text), flags);
            string sub = $"{c.When:d MMMM, HH:mm}" + (c.Code != 0 ? $" · 0x{c.Code:X8}" : "");
            TextRenderer.DrawText(g, sub, Theme.Tiny, new Rectangle(r.Left + 60, y + 22, r.Width - 100, 18), C(Theme.Muted), flags);
            TextRenderer.DrawText(g, "", Theme.Icon, new Rectangle(r.Right - 36, y, 24, RowH - 6), C(i == hover ? Theme.Accent : Theme.Dim),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        if (crashes.Count > MaxRows)
            TextRenderer.DrawText(g, $"и ещё {crashes.Count - MaxRows} — см. «Просмотр событий»", Theme.Tiny,
                new Rectangle(r.Left + 60, r.Top + 12 + n * RowH, r.Width - 80, RowH - 10), C(Theme.Dim), flags);
    }
}

/// <summary>Тест скорости DNS: столбики задержки, лучший подсвечен.</summary>
class DnsWidget : Widget
{
    List<DnsBench.Result>? results;
    string? currentIp;
    int done, total;
    public bool Running { get; private set; }
    public readonly FlatBtn Run;
    public readonly FlatBtn UseBest;
    const int RowH = 30;

    public DnsWidget() : base("Скорость DNS", 28 + 12 + (DnsBench.Servers.Length + 1) * RowH + 64)
    {
        Run = new FlatBtn("", "Запустить тест") { Primary = true };
        UseBest = new FlatBtn("", "Включить лучший") { Visible = false };
        Controls.Add(Run);
        Controls.Add(UseBest);
    }

    public DnsBench.Result? Best => results?.FirstOrDefault(r => r.Ok > 0 && r.Server.Tweak != null);

    public void Begin()
    {
        Running = true;
        results = null;
        done = 0;
        UseBest.Visible = false;
        Invalidate(false);
    }

    public void Progress(int d, int t)
    {
        done = d;
        total = t;
        Invalidate(false);
    }

    public void SetData(List<DnsBench.Result> data, string? current)
    {
        Running = false;
        results = data;
        currentIp = current;
        var best = Best;
        UseBest.Visible = best != null;
        Invalidate(true);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Run is null || UseBest is null) return;
        Run.Location = new Point(10 - 8 + 8, Height - Run.Height - 6);
        UseBest.Location = new Point(Width - UseBest.Width - 14, Height - UseBest.Height - 14);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        if (results == null)
        {
            string text = Running
                ? $"Проверяю {done} из {Math.Max(total, 1)}… каждый сервер — 15 запросов"
                : "Отправит по 15 DNS-запросов каждому серверу и покажет, какой отвечает быстрее именно у вашего провайдера.";
            TextRenderer.DrawText(g, text, Theme.Body, new Rectangle(r.Left + 18, r.Top + 14, r.Width - 36, 60), C(Theme.Muted), TextFormatFlags.WordBreak);
            if (Running && total > 0)
            {
                var bar = new RectangleF(r.Left + 18, r.Top + 82, r.Width - 36, 4);
                Theme.Fill(g, Color.FromArgb(22, 255, 255, 255), bar, 2);
                Theme.Fill(g, Theme.Accent, new RectangleF(bar.X, bar.Y, bar.Width * done / total, 4), 2);
            }
            return;
        }
        var ok = results.Where(x => x.Ok > 0).ToList();
        double max = ok.Count == 0 ? 1 : ok.Max(x => x.Ms) * 1.15;
        for (int i = 0; i < results.Count; i++)
        {
            var res = results[i];
            int y = r.Top + 12 + i * RowH;
            bool best = res == Best;
            bool cur = res.Server.Ip == currentIp || res.Server.Name == "Текущий";
            var nameCol = best ? Theme.Accent : Theme.Text;
            TextRenderer.DrawText(g, res.Server.Name + (cur ? " ·  сейчас" : ""), best ? Theme.Semi : Theme.Body,
                new Rectangle(r.Left + 18, y, 120, RowH - 4), C(nameCol), flags);
            float bx = r.Left + 142, bw = r.Width - 142 - 84;
            var track = new RectangleF(bx, y + 11, bw, 5);
            Theme.Fill(g, Color.FromArgb((int)(18 * Appear), 255, 255, 255), track, 2.5f);
            string label;
            if (res.Ok == 0) label = "нет ответа";
            else
            {
                var fill = new RectangleF(bx, y + 11, (float)(bw * res.Ms / max * Appear), 5);
                var col = best ? Theme.Accent : Color.FromArgb(150, 160, 175);
                if (best) Theme.Glow(g, col, fill, 2.5f, 2, 1.5f, (int)(80 * Appear));
                Theme.Fill(g, C(col), fill, 2.5f);
                label = $"{res.Ms:0} мс" + (res.Ok < res.Total ? $"  ({res.Total - res.Ok} потерь)" : "");
            }
            TextRenderer.DrawText(g, label, Theme.Tiny, new Rectangle((int)(bx + bw + 8), y, 80, RowH - 4),
                C(res.Ok == 0 ? Theme.Bad : best ? Theme.Accent : Theme.Muted), flags);
        }
    }
}
