using System.Diagnostics;

namespace WinTweaker;

/// <summary>
/// Раздел «Обход блокировок»: DNS для обхода геоблокировок, шифрование DNS,
/// проверка доступности сайтов и ссылки на инструменты обхода DPI.
/// Адреса DNS-сервисов могут меняться — проверяйте на их сайтах.
/// </summary>
public static class RussiaNetwork
{
    const string C = "Обход блокировок";
    const string DnsNote = "\n\nВключайте только один DNS. Выключение вернёт DNS провайдера.";

    public static IEnumerable<Tweak> All()
    {
        // ── DNS против геоблокировки (сервисы сами блокируют РФ) ──
        const string G1 = "DNS: геоблок (ИИ, Spotify и др.)";
        yield return Dns(G1, "Comss DNS",
            "Адреса актуальны на момент написания — сверяйте на comss.ru. Открывает сервисы, которые сами не пускают пользователей из России: ChatGPT, Gemini, Copilot, Spotify, некоторые игры. " +
            "От блокировок РКН (YouTube, Discord) НЕ помогает.",
            "83.220.169.155", "212.109.195.93");

        // ── Обычные публичные DNS ──
        const string G2 = "DNS: публичные";
        yield return Dns(G2, "Cloudflare 1.1.1.1", "Быстрый и приватный DNS. Провайдер не сможет подменять ответы, если включено шифрование DNS.", "1.1.1.1", "1.0.0.1");
        yield return Dns(G2, "Google 8.8.8.8", "Надёжный DNS от Google.", "8.8.8.8", "8.8.4.4");
        yield return Dns(G2, "Quad9", "DNS с блокировкой вредоносных сайтов.", "9.9.9.9", "149.112.112.112");
        yield return Dns(G2, "AdGuard DNS", "Блокирует рекламу и трекеры на уровне DNS.", "94.140.14.14", "94.140.15.15");
        yield return Dns(G2, "Яндекс DNS", "Серверы в России — обычно самый низкий пинг у российских провайдеров. Базовый режим, без фильтрации.", "77.88.8.8", "77.88.8.1");

        // ── Защита и проверка ──
        const string G3 = "Защита и проверка";
        // В стабильной Windows 10 системный DoH не поддерживается (появился в Windows 11),
        // поэтому включаем его там, где он работает: в браузерах через их политики.
        const string Ch = @"HKLM\SOFTWARE\Policies\Google\Chrome";
        const string Ed = @"HKLM\SOFTWARE\Policies\Microsoft\Edge";
        const string Ff = @"HKLM\SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS";
        const string Doh = "https://cloudflare-dns.com/dns-query";
        yield return new Tweak
        {
            Category = C, Group = G3, Name = "Шифрование DNS в браузерах (DoH)",
            Details = "Политики браузеров:\n  [HKLM\\SOFTWARE\\Policies\\Google\\Chrome] и [...\\Microsoft\\Edge]\n    DnsOverHttpsMode = \"secure\"\n    DnsOverHttpsTemplates = \"https://cloudflare-dns.com/dns-query\"\n" +
                      "  [HKLM\\SOFTWARE\\Policies\\Mozilla\\Firefox\\DNSOverHTTPS]\n    Enabled = 1, ProviderURL = …\n\nОтменить — удалить эти параметры.",
            Description = "Chrome, Edge и Firefox будут получать адреса сайтов через зашифрованный DNS Cloudflare — провайдер не сможет подменить ответ " +
                          "(частый способ блокировки). В браузере появится «Управляется организацией» — это нормально. Перезапустите браузер.\n\n" +
                          "Не совмещайте с Comss DNS: браузер будет обращаться мимо него.",
            IsApplied = () => Equals(Reg.Get(Ch, "DnsOverHttpsMode"), "secure") && Equals(Reg.Get(Ed, "DnsOverHttpsMode"), "secure"),
            Apply = () =>
            {
                foreach (var key in new[] { Ch, Ed })
                {
                    Reg.Set(key, "DnsOverHttpsMode", "secure");
                    Reg.Set(key, "DnsOverHttpsTemplates", Doh);
                }
                Reg.Set(Ff, "Enabled", 1);
                Reg.Set(Ff, "ProviderURL", Doh);
            },
            Revert = () =>
            {
                foreach (var key in new[] { Ch, Ed })
                {
                    Reg.Delete(key, "DnsOverHttpsMode");
                    Reg.Delete(key, "DnsOverHttpsTemplates");
                }
                Reg.Delete(Ff, "Enabled");
                Reg.Delete(Ff, "ProviderURL");
            }
        };
        yield return Catalog.Act(C, G3, "Проверить доступность сайтов",
            "Проверяет YouTube, Discord, Instagram, X, Telegram, ChatGPT и показывает, что открывается.",
            CheckSites);

        // ── Инструменты обхода DPI (скачиваются вручную) ──
        const string G4 = "Инструменты обхода DPI";
        yield return Catalog.Act(C, G4, "zapret — страница загрузки",
            "Самый мощный обход замедления YouTube и блокировок Discord. Сборка для Windows: zapret-win-bundle. " +
            "Скачивается и запускается вручную, программа только откроет страницу.",
            () => Open("https://github.com/bol-van/zapret-win-bundle"));
        yield return Catalog.Act(C, G4, "GoodbyeDPI — страница загрузки",
            "Простой обход DPI. Скачайте релиз и запустите один из .cmd-файлов (например, для Russia).",
            () => Open("https://github.com/ValdikSS/GoodbyeDPI/releases"));
    }

    static Tweak Dns(string group, string name, string desc, string p1, string p2) => new()
    {
        Category = C, Group = group, Name = name, Description = desc + $"\n\nСерверы: {p1}, {p2}." + DnsNote,
        Details = $"PowerShell, для всех подключённых адаптеров:\n  Set-DnsClientServerAddress -ServerAddresses ('{p1}','{p2}')\n  ipconfig /flushdns\n\n" +
                  "Отменить: вернуть DNS, которые были на каждом адаптере до применения (снимок в журнале), иначе — DNS от провайдера (DHCP).",
        IsApplied = () => DnsCache.Current.Contains(p1),
        Snapshot = DnsSnapshot,
        Restore = DnsRestore,
        Apply = () =>
        {
            Shell.PS($"Get-NetAdapter | Where-Object Status -eq 'Up' | Set-DnsClientServerAddress -ServerAddresses ('{p1}','{p2}')");
            Shell.Run("ipconfig.exe", "/flushdns");
            DnsCache.Reset();
        },
        Revert = () =>
        {
            Shell.PS("Get-NetAdapter | Set-DnsClientServerAddress -ResetServerAddresses");
            Shell.Run("ipconfig.exe", "/flushdns");
            DnsCache.Reset();
        }
    };

    /// <summary>
    /// DNS-сервисы, которые убраны из списка, потому что перестали работать (Xbox DNS — блокировки, октябрь 2026).
    /// У кого такой DNS остался включён, программа предлагает вернуть DNS провайдера.
    /// </summary>
    public static readonly (string Name, string[] Ips)[] Retired = { ("Xbox DNS", new[] { "176.99.11.77", "80.78.247.254" }) };

    /// <summary>Название выведенного из списка DNS, если он сейчас стоит на каком-либо адаптере.</summary>
    public static string? RetiredInUse()
    {
        var cur = DnsCache.Current;
        return Retired.FirstOrDefault(r => r.Ips.Any(ip => cur.Contains(ip))).Name;
    }

    public const string Page = C;

    /// <summary>Убранный из списка DNS — только для отката старых сеансов из «Истории».</summary>
    public static Tweak? RetiredTweak(string name)
    {
        var r = Retired.FirstOrDefault(x => x.Name == name);
        return r.Name == null ? null : Dns("", r.Name, "", r.Ips[0], r.Ips[1]);
    }

    /// <summary>Вернуть DNS от провайдера (DHCP) на всех адаптерах.</summary>
    public static void ResetDns()
    {
        Shell.PS("Get-NetAdapter | Set-DnsClientServerAddress -ResetServerAddresses");
        Shell.Run("ipconfig.exe", "/flushdns");
        DnsCache.Reset();
    }

    const string Ifaces = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    /// <summary>Снимок DNS: {GUID адаптера}=серверы (пусто — DNS от DHCP/провайдера).</summary>
    static string DnsSnapshot()
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Ifaces);
        if (k == null) return "";
        return string.Join(";", k.GetSubKeyNames().Select(g =>
        {
            using var s = k.OpenSubKey(g);
            return g + "=" + (s?.GetValue("NameServer")?.ToString() ?? "");
        }));
    }

    static void DnsRestore(string snap)
    {
        foreach (var part in snap.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            string guid = kv[0], servers = kv.Length > 1 ? kv[1].Replace(' ', ',') : "";
            string target = $"$a = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object InterfaceGuid -eq '{guid}'; if ($a) {{ ";
            Shell.PS(servers == ""
                ? target + "Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ResetServerAddresses }"
                : target + "Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses ('" +
                  string.Join("','", servers.Split(',', StringSplitOptions.RemoveEmptyEntries)) + "') }");
        }
        Shell.Run("ipconfig.exe", "/flushdns");
        DnsCache.Reset();
    }

    static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    static void CheckSites()
    {
        // Вывод на латинице, чтобы не ломалась кодировка консоли
        string ps = "$h='youtube.com','discord.com','instagram.com','x.com','telegram.org','chatgpt.com'; " +
                    "foreach($x in $h){ try { $null = Invoke-WebRequest ('https://'+$x) -Method Head -TimeoutSec 6 -UseBasicParsing; $x+'|OK' } " +
                    "catch { $x+'|FAIL' } }";
        var lines = Shell.PS(ps).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|'))
            .Where(p => p.Length == 2)
            .Select(p => (p[1] == "OK" ? "✔  " : "✖  ") + p[0] + (p[1] == "OK" ? " — доступен" : " — недоступен"));
        Dialog.InfoFromAnyThread("Доступность сайтов", string.Join("\n", lines) +
                        "\n\nЕсли YouTube/Discord недоступны — нужен zapret или GoodbyeDPI.\nЕсли недоступен ChatGPT — включите Comss DNS.");
    }
}
