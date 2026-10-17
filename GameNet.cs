using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace WinTweaker;

/// <summary>
/// Сетевые инструменты для игр — только измерения, без изменения маршрутов:
///  • пинг до регионов: ретрансляторы Steam Datagram Relay (через них играют CS2, Dota 2, Deadlock;
///    список берётся у Valve — ISteamApps/GetSDRConfig) и регионы AWS (на нём, например, Fortnite);
///  • трассировка с задержкой и потерями на каждом узле;
///  • монитор соединения на время игры: отдельно до роутера и до интернета — видно, где проблема.
/// </summary>
static class GameNet
{
    public sealed record Region(string Code, string Name, string Host, bool Tcp);

    public sealed class Result
    {
        public required Region Region { get; init; }
        public double? Ms { get; set; }   // лучшая задержка
        public double Jitter { get; set; }
        public int Lost { get; set; }
        public int Sent { get; set; }
    }

    static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinTweaker");
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // ───────── Регионы ─────────

    static readonly Dictionary<string, string> Cities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ams"] = "Амстердам", ["ams4"] = "Амстердам", ["fra"] = "Франкфурт", ["fsn"] = "Фалькенштайн", ["hel"] = "Хельсинки",
        ["lhr"] = "Лондон", ["lux"] = "Люксембург", ["mad"] = "Мадрид", ["par"] = "Париж", ["sto"] = "Стокгольм",
        ["sto2"] = "Стокгольм (2)", ["vie"] = "Вена", ["waw"] = "Варшава", ["ist"] = "Стамбул", ["dxb"] = "Дубай",
        ["bom"] = "Мумбаи", ["bom2"] = "Мумбаи (2)", ["maa"] = "Ченнаи", ["maa2"] = "Ченнаи (2)", ["sgp"] = "Сингапур", ["hkg"] = "Гонконг",
        ["tyo"] = "Токио", ["tyo1"] = "Токио", ["seo"] = "Сеул", ["syd"] = "Сидней", ["gru"] = "Сан-Паулу", ["scl"] = "Сантьяго",
        ["lim"] = "Лима", ["eze"] = "Буэнос-Айрес", ["jnb"] = "Йоханнесбург", ["iad"] = "Вашингтон", ["atl"] = "Атланта",
        ["ord"] = "Чикаго", ["dfw"] = "Даллас", ["lax"] = "Лос-Анджелес", ["sea"] = "Сиэтл", ["mia"] = "Майами", ["tbs"] = "Тбилиси",
    };

    public static readonly Region[] Aws =
    {
        new("eu-central-1", "Франкфурт", "dynamodb.eu-central-1.amazonaws.com", true),
        new("eu-north-1", "Стокгольм", "dynamodb.eu-north-1.amazonaws.com", true),
        new("eu-west-1", "Ирландия", "dynamodb.eu-west-1.amazonaws.com", true),
        new("eu-west-2", "Лондон", "dynamodb.eu-west-2.amazonaws.com", true),
        new("eu-west-3", "Париж", "dynamodb.eu-west-3.amazonaws.com", true),
        new("eu-south-1", "Милан", "dynamodb.eu-south-1.amazonaws.com", true),
        new("eu-central-2", "Цюрих", "dynamodb.eu-central-2.amazonaws.com", true),
        new("me-central-1", "ОАЭ", "dynamodb.me-central-1.amazonaws.com", true),
        new("il-central-1", "Тель-Авив", "dynamodb.il-central-1.amazonaws.com", true),
        new("ap-south-1", "Мумбаи", "dynamodb.ap-south-1.amazonaws.com", true),
        new("ap-southeast-1", "Сингапур", "dynamodb.ap-southeast-1.amazonaws.com", true),
        new("ap-northeast-1", "Токио", "dynamodb.ap-northeast-1.amazonaws.com", true),
        new("us-east-1", "Вирджиния (США)", "dynamodb.us-east-1.amazonaws.com", true),
        new("us-west-2", "Орегон (США)", "dynamodb.us-west-2.amazonaws.com", true),
    };

    /// <summary>Ретрансляторы Valve по данным Steam (кэш на сутки, при сбое — прошлый кэш).</summary>
    public static async Task<List<Region>> ValveAsync()
    {
        var file = Path.Combine(Data, "sdr.json");
        string? json = null;
        if (File.Exists(file) && DateTime.Now - File.GetLastWriteTime(file) < TimeSpan.FromDays(1))
            json = await File.ReadAllTextAsync(file);
        if (json == null)
        {
            try
            {
                json = await http.GetStringAsync("https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid=730");
                Directory.CreateDirectory(Data);
                await File.WriteAllTextAsync(file, json);
            }
            catch
            {
                if (File.Exists(file)) json = await File.ReadAllTextAsync(file);
            }
        }
        var list = new List<Region>();
        if (json == null) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("pops", out var pops)) return list;
            foreach (var pop in pops.EnumerateObject())
            {
                if (!pop.Value.TryGetProperty("relays", out var relays) || relays.ValueKind != JsonValueKind.Array) continue;
                string? ip = relays.EnumerateArray()
                    .Select(r => r.TryGetProperty("ipv4", out var v) ? v.GetString() : null)
                    .FirstOrDefault(v => !string.IsNullOrEmpty(v));
                if (ip == null) continue;
                string desc = pop.Value.TryGetProperty("desc", out var d) ? d.GetString() ?? pop.Name : pop.Name;
                string name = Cities.TryGetValue(pop.Name, out var ru) ? ru : desc;
                list.Add(new Region(pop.Name, name, ip, false));
            }
        }
        catch { }
        return list;
    }

    // ───────── Измерение ─────────

    public static async Task<List<Result>> MeasureAsync(IReadOnlyList<Region> regions, int count, Action<int, int>? progress = null)
    {
        var results = regions.Select(r => new Result { Region = r }).ToList();
        int done = 0;
        using var gate = new SemaphoreSlim(12);
        await Task.WhenAll(results.Select(async res =>
        {
            await gate.WaitAsync();
            try { await Measure(res, count); }
            finally
            {
                gate.Release();
                progress?.Invoke(Interlocked.Increment(ref done), results.Count);
            }
        }));
        return results.OrderBy(r => r.Ms ?? double.MaxValue).ToList();
    }

    static async Task Measure(Result res, int count)
    {
        var times = new List<double>();
        IPAddress? ip = null;
        try { ip = await Resolve(res.Region.Host); } catch { }
        for (int i = 0; i < count; i++)
        {
            res.Sent++;
            double? ms = ip == null ? null : res.Region.Tcp ? await TcpPing(ip, 443) : await IcmpPing(ip, 1200);
            if (ms is double v) times.Add(v); else res.Lost++;
        }
        if (times.Count > 0)
        {
            res.Ms = times.Min();
            res.Jitter = Jitter(times);
        }
    }

    public static async Task<IPAddress?> Resolve(string host)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var all = await Dns.GetHostAddressesAsync(host);
        return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.FirstOrDefault();
    }

    public static async Task<double?> IcmpPing(IPAddress ip, int timeout)
    {
        try
        {
            using var p = new Ping();
            var r = await p.SendPingAsync(ip, timeout);
            return r.Status == IPStatus.Success ? Math.Max(0.5, r.RoundtripTime) : null;
        }
        catch { return null; }
    }

    /// <summary>Время установки TCP-соединения — для серверов, которые не отвечают на ICMP.</summary>
    public static async Task<double?> TcpPing(IPAddress ip, int port)
    {
        try
        {
            using var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var cts = new CancellationTokenSource(1500);
            var sw = Stopwatch.StartNew();
            await s.ConnectAsync(ip, port, cts.Token);
            return sw.Elapsed.TotalMilliseconds;
        }
        catch { return null; }
    }

    /// <summary>Джиттер — средний разброс между соседними замерами (как в RFC 3550, упрощённо).</summary>
    public static double Jitter(IReadOnlyList<double> v)
    {
        if (v.Count < 2) return 0;
        double sum = 0;
        for (int i = 1; i < v.Count; i++) sum += Math.Abs(v[i] - v[i - 1]);
        return sum / (v.Count - 1);
    }

    public static IPAddress? Gateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        }
        catch { return null; }
    }

    /// <summary>Подключение по Wi-Fi (у Wi-Fi чаще скачки пинга).</summary>
    public static string? WifiName()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                                     && n.GetIPProperties().GatewayAddresses.Count > 0)?.Name;
        }
        catch { return null; }
    }

    // ───────── Трассировка ─────────

    public sealed class Hop
    {
        public int Ttl { get; init; }
        public string? Ip { get; set; }
        public string? Host { get; set; }
        public double? Ms { get; set; }
        public int Lost { get; set; }
        public int Sent { get; set; }
        public bool Final { get; set; }
    }

    /// <summary>Трассировка: по 3 пакета на каждый узел (TTL 1…30). onHop вызывается из фонового потока.</summary>
    public static async Task TraceAsync(string target, Action<Hop> onHop, CancellationToken ct)
    {
        var ip = await Resolve(target) ?? throw new InvalidOperationException("Не удалось найти адрес " + target);
        var buf = new byte[32];
        for (int ttl = 1; ttl <= 30 && !ct.IsCancellationRequested; ttl++)
        {
            int t = ttl;
            var replies = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Probe(ip, t, buf)));
            var ok = replies.Where(r => r.Status is IPStatus.Success or IPStatus.TtlExpired && r.Address != null).ToList();
            var hop = new Hop
            {
                Ttl = ttl, Sent = 3, Lost = 3 - ok.Count,
                Ip = ok.FirstOrDefault().Address?.ToString(),
                Ms = ok.Count > 0 ? ok.Min(r => r.Ms) : null,
                Final = ok.Any(r => r.Status == IPStatus.Success)
            };
            onHop(hop);
            if (hop.Ip != null) _ = ReverseAsync(hop, onHop);
            if (hop.Final) break;
        }
    }

    static async Task<(IPStatus Status, IPAddress? Address, double Ms)> Probe(IPAddress ip, int ttl, byte[] buf)
    {
        using var p = new Ping();
        var sw = Stopwatch.StartNew();
        try
        {
            var r = await p.SendPingAsync(ip, 1200, buf, new PingOptions(ttl, true));
            double ms = r.RoundtripTime > 0 ? r.RoundtripTime : sw.Elapsed.TotalMilliseconds;
            return (r.Status, r.Address, ms);
        }
        catch { return (IPStatus.Unknown, null, 0); }
    }

    static async Task ReverseAsync(Hop hop, Action<Hop> onHop)
    {
        try
        {
            var t = Dns.GetHostEntryAsync(hop.Ip!);
            if (await Task.WhenAny(t, Task.Delay(2000)) == t && t.Result.HostName != hop.Ip)
            {
                hop.Host = t.Result.HostName;
                onHop(hop);
            }
        }
        catch { }
    }
}

/// <summary>Отчёт о соединении за игровую сессию.</summary>
sealed class MatchReport
{
    public string Game { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Target { get; set; } = "";
    public double Avg { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double P95 { get; set; }
    public double Jitter { get; set; }
    public double Loss { get; set; }
    public int Spikes { get; set; }
    public double GwAvg { get; set; }
    public double GwJitter { get; set; }
    public double GwLoss { get; set; }
    public bool Wifi { get; set; }
    /// <summary>Пинг по секундам (-1 — потеря), прорежено до 600 точек.</summary>
    public int[] Series { get; set; } = Array.Empty<int>();

    public TimeSpan Duration => End - Start;

    /// <summary>Где проблема: дома (Wi-Fi/роутер) или дальше (провайдер, магистраль, сервер).</summary>
    public (string Text, Tone Tone) Verdict()
    {
        if (GwLoss > 1 || GwJitter > 12)
            return (Wifi ? "Нестабилен Wi-Fi: потери и скачки уже до роутера. Помогут кабель, 5 ГГц или ближе к роутеру"
                         : "Скачки уже до роутера: проверьте кабель и нагрузку на домашнюю сеть", Tone.Warning);
        if (Loss > 1 || Jitter > 12 || Spikes > Math.Max(3, Duration.TotalMinutes / 3))
            return ("Дома всё ровно — скачки дальше: у провайдера или на пути к серверу", Tone.Warning);
        return ("Соединение стабильное", Tone.Success);
    }
}

/// <summary>Монитор соединения: раз в секунду пинг до цели и до роутера.</summary>
sealed class ConnMonitor : IDisposable
{
    readonly IPAddress? target, gateway;
    readonly string targetName;
    readonly List<int> ms = new(), gw = new();
    readonly System.Threading.Timer timer;
    readonly DateTime start = DateTime.Now;
    readonly bool wifi = GameNet.WifiName() != null;
    int busy;

    /// <summary>Последний замер до цели (null — потеря или ещё нет данных).</summary>
    public int? Last { get; private set; }
    public string TargetName => targetName;

    public ConnMonitor(string targetHost)
    {
        targetName = targetHost;
        try { target = GameNet.Resolve(targetHost).GetAwaiter().GetResult(); } catch { }
        gateway = GameNet.Gateway();
        timer = new System.Threading.Timer(_ => Tick(), null, 500, 1000);
    }

    async void Tick()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        try
        {
            var a = target == null ? Task.FromResult<double?>(null) : GameNet.IcmpPing(target, 900);
            var b = gateway == null ? Task.FromResult<double?>(null) : GameNet.IcmpPing(gateway, 900);
            await Task.WhenAll(a, b);
            lock (ms)
            {
                ms.Add(a.Result is double x ? (int)Math.Round(x) : -1);
                Last = a.Result is double l ? (int)Math.Round(l) : null;
                if (gateway != null) gw.Add(b.Result is double y ? (int)Math.Round(y) : -1);
            }
        }
        catch { }
        finally { Volatile.Write(ref busy, 0); }
    }

    public MatchReport Stop(string game)
    {
        timer.Dispose();
        int[] s, g;
        lock (ms) { s = ms.ToArray(); g = gw.ToArray(); }
        var ok = s.Where(v => v >= 0).Select(v => (double)v).ToList();
        var gok = g.Where(v => v >= 0).Select(v => (double)v).ToList();
        var r = new MatchReport
        {
            Game = game, Start = start, End = DateTime.Now, Target = targetName, Wifi = wifi,
            Loss = s.Length == 0 ? 0 : 100.0 * (s.Length - ok.Count) / s.Length,
            GwLoss = g.Length == 0 ? 0 : 100.0 * (g.Length - gok.Count) / g.Length,
        };
        if (ok.Count > 0)
        {
            r.Avg = ok.Average();
            r.Min = ok.Min();
            r.Max = ok.Max();
            var sorted = ok.OrderBy(v => v).ToList();
            r.P95 = sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(sorted.Count * 0.95))];
            r.Jitter = GameNet.Jitter(ok);
            r.Spikes = ok.Count(v => v > r.Avg + Math.Max(40, r.Avg));
        }
        if (gok.Count > 0)
        {
            r.GwAvg = gok.Average();
            r.GwJitter = GameNet.Jitter(gok);
        }
        // прореживание: максимум из окна, чтобы скачки не терялись
        int step = Math.Max(1, (int)Math.Ceiling(s.Length / 600.0));
        r.Series = Enumerable.Range(0, (s.Length + step - 1) / step)
            .Select(i => s.Skip(i * step).Take(step).Any(v => v < 0) ? -1 : s.Skip(i * step).Take(step).Max()).ToArray();
        return r;
    }

    public void Dispose() => timer.Dispose();
}

/// <summary>История отчётов о соединении (%AppData%\WinTweaker\matches.json, последние 30).</summary>
static class Matches
{
    static readonly string File_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinTweaker", "matches.json");

    public static List<MatchReport> Load()
    {
        try { return JsonSerializer.Deserialize<List<MatchReport>>(File.ReadAllText(File_)) ?? new(); }
        catch { return new(); }
    }

    public static void Add(MatchReport r)
    {
        var list = Load();
        list.Insert(0, r);
        if (list.Count > 30) list.RemoveRange(30, list.Count - 30);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, JsonSerializer.Serialize(list));
        }
        catch { }
    }
}
