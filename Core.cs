using System.Diagnostics;
using Microsoft.Win32;

namespace WinTweaker;

/// <summary>Одна настройка: умеет проверить своё состояние, применить и (если возможно) отменить.</summary>
public class Tweak
{
    public required string Category { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public bool Risky { get; init; }
    public bool NeedsRestart { get; init; }
    /// <summary>Заголовок карточки внутри категории (необязательно).</summary>
    public string? Group { get; set; }
    /// <summary>Разовое действие (кнопка «Запуск» вместо переключателя).</summary>
    public bool IsAction { get; init; }
    /// <summary>Для записей автозагрузки: иконка, подпись (издатель) и «Открыть папку».</summary>
    public Image? Icon { get; init; }
    public string? Subtitle { get; init; }
    public Action? OpenFolder { get; init; }
    public Func<bool> IsApplied { get; init; } = () => false;
    public Action Apply { get; init; } = () => { };
    public Action? Revert { get; init; }
    /// <summary>Для точного отката того, что меняется не через Reg (службы, схема питания, DNS).</summary>
    public Func<string>? Snapshot { get; init; }
    /// <summary>«Что именно меняется»: ключи реестра и команды — показывается до применения.</summary>
    public string? Details { get; init; }
    public Action<string>? Restore { get; init; }

    // ───── Настройки со значением: выбор из вариантов или ползунок ─────
    /// <summary>Варианты выбора (сегменты). Значение — индекс варианта.</summary>
    public string[]? Options { get; init; }
    /// <summary>Ползунок: диапазон, шаг и единица измерения.</summary>
    public (int Min, int Max, int Step, string Unit)? Range { get; init; }
    /// <summary>Подпись значения ползунка (например, «0 = никогда»).</summary>
    public Func<int, string>? Format { get; init; }
    public Func<int>? Get { get; init; }
    public Action<int>? Set { get; init; }

    public bool IsValue => Get != null && Set != null;
    public bool IsToggle => !IsAction && !IsValue;

    public string ValueText(int v) =>
        Options != null ? (v >= 0 && v < Options.Length ? Options[v] : "—")
        : Format != null ? Format(v)
        : Range != null ? $"{v} {Range.Value.Unit}".Trim() : v.ToString();
}

static class TweakExt
{
    /// <summary>Положить настройку в карточку с заголовком group.</summary>
    public static Tweak In(this Tweak t, string group)
    {
        t.Group = group;
        return t;
    }
}

/// <summary>Работа с реестром по путям вида "HKLM\SOFTWARE\...".</summary>
public static class Reg
{
    static (RegistryKey root, string sub) Split(string path) => SplitPath(path);

    public static (RegistryKey root, string sub) SplitPath(string path)
    {
        int i = path.IndexOf('\\');
        string hive = path[..i], sub = path[(i + 1)..];
        RegistryKey root = hive switch
        {
            "HKLM" => Registry.LocalMachine,
            "HKCU" => Registry.CurrentUser,
            "HKCR" => Registry.ClassesRoot,
            "HKU" => Registry.Users,
            _ => throw new ArgumentException("Неизвестный раздел: " + hive)
        };
        return (root, sub);
    }

    public static object? Get(string path, string name)
    {
        var (r, s) = Split(path);
        using var k = r.OpenSubKey(s);
        return k?.GetValue(name);
    }

    public static void Set(string path, string name, object value)
    {
        var (r, s) = Split(path);
        Journal.CaptureValue(r, s, path, name);
        using var k = r.CreateSubKey(s, true);
        var kind = value switch
        {
            string => RegistryValueKind.String,
            byte[] => RegistryValueKind.Binary,
            _ => RegistryValueKind.DWord
        };
        k.SetValue(name, value, kind);
    }

    public static void Delete(string path, string name)
    {
        var (r, s) = Split(path);
        Journal.CaptureValue(r, s, path, name);
        using var k = r.OpenSubKey(s, true);
        k?.DeleteValue(name, false);
    }

    public static bool KeyExists(string path)
    {
        var (r, s) = Split(path);
        using var k = r.OpenSubKey(s);
        return k != null;
    }

    public static void CreateKey(string path)
    {
        var (r, s) = Split(path);
        Journal.CaptureKey(r, s, path);
        r.CreateSubKey(s, true)?.Dispose();
    }

    public static void DeleteKey(string path)
    {
        var (r, s) = Split(path);
        Journal.CaptureKey(r, s, path);
        r.DeleteSubKeyTree(s, false);
    }
}

/// <summary>Запуск внешних команд без окна.</summary>
public static class Shell
{
    public static string Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        _ = err.Result;
        return output;
    }

    /// <summary>Запуск с кодом возврата (вывод не нужен).</summary>
    public static int RunCode(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    public static string PS(string script) =>
        Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script.Replace("\"", "\\\"") + "\"");
}

/// <summary>Кэш установленных UWP-приложений, чтобы не вызывать PowerShell для каждой строки.</summary>
public static class AppxCache
{
    static HashSet<string>? names;

    public static void Reset() => names = null;

    public static bool Has(string pattern)
    {
        names ??= Shell.PS("Get-AppxPackage | ForEach-Object { $_.Name }")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Any(n => n.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Кэш текущих DNS-серверов.</summary>
public static class DnsCache
{
    static string? cur;
    public static void Reset() => cur = null;
    public static string Current => cur ??= Shell.PS("Get-DnsClientServerAddress -AddressFamily IPv4 | ForEach-Object { $_.ServerAddresses }");
}
