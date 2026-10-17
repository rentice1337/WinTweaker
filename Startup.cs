using System.Diagnostics;
using Microsoft.Win32;

namespace WinTweaker;

/// <summary>
/// Менеджер автозагрузки в стиле диспетчера задач:
/// записи Run (HKCU/HKLM/WOW6432Node) и папки «Автозагрузка» отключаются через ключи StartupApproved
/// (сама запись остаётся, её видно и в диспетчере задач); сторонние задания планировщика при входе
/// и загрузке включаются и выключаются через Enable/Disable-ScheduledTask. Системные \Microsoft\* не трогаем.
/// </summary>
static class StartupManager
{
    public const string Cat = "Автозагрузка";
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<Tweak> Load()
    {
        var list = new List<Tweak>();

        var regSources = new (RegistryKey Hive, string Key, string ApprovedSub, string Label)[]
        {
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Run", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
        };
        foreach (var (hive, key, sub, label) in regSources)
        {
            try
            {
                using var k = hive.OpenSubKey(key);
                if (k == null) continue;
                foreach (var name in k.GetValueNames().Where(n => n != ""))
                {
                    string cmd = k.GetValue(name)?.ToString() ?? "";
                    list.Add(Make(name, cmd, ExeFromCommand(cmd), "Реестр", "Реестр: " + label, hive, sub, name));
                }
            }
            catch { }
        }

        var folders = new (string Path, RegistryKey Hive)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine),
        };
        foreach (var (folder, hive) in folders)
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var f in Directory.GetFiles(folder))
                {
                    string file = Path.GetFileName(f);
                    if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    string? target = f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? LnkTarget(f) : f;
                    list.Add(Make(Path.GetFileNameWithoutExtension(f), f, target ?? f, "Папка «Автозагрузка»",
                        "Папка: " + folder, hive, "StartupFolder", file));
                }
            }
            catch { }
        }

        try { list.AddRange(Tasks()); } catch { }
        return list;
    }

    // ───────── Реестр и папки: StartupApproved ─────────

    static Tweak Make(string name, string command, string? exe, string group, string source,
        RegistryKey hive, string approvedSub, string valueName)
    {
        string key = Approved + approvedSub;
        return Entry(name, exe, group, $"{source}\n\nКоманда: {command}",
            details: $"Как в диспетчере задач — запись не удаляется, а помечается:\n  [{(hive == Registry.CurrentUser ? "HKCU" : "HKLM")}\\{key}]\n" +
                     $"    {valueName} = 02 00 00 … (включено) / 03 00 00 … (выключено)",
            isOn: () => IsEnabled(hive, key, valueName),
            on: () => SetEnabled(hive, key, valueName, true),
            off: () => SetEnabled(hive, key, valueName, false));
    }

    /// <summary>Нет значения или первый байт чётный (02/06) — включено; нечётный (03/07) — выключено.</summary>
    static bool IsEnabled(RegistryKey hive, string key, string value)
    {
        try
        {
            using var k = hive.OpenSubKey(key);
            return k?.GetValue(value) is not byte[] v || v.Length == 0 || (v[0] & 1) == 0;
        }
        catch { return true; }
    }

    static void SetEnabled(RegistryKey hive, string key, string value, bool enabled)
    {
        var data = new byte[12];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4); // время отключения, как пишет диспетчер задач
        Reg.Set((hive == Registry.CurrentUser ? "HKCU\\" : "HKLM\\") + key, value, data); // через Reg — попадает в журнал отката
    }

    // ───────── Планировщик ─────────

    static IEnumerable<Tweak> Tasks()
    {
        const string ps =
            "Get-ScheduledTask | Where-Object { $_.TaskPath -notlike '\\Microsoft\\*' -and $_.TaskPath -notlike '\\WinTweaker\\*' -and " +
            "@($_.Triggers | Where-Object { $_.CimClass.CimClassName -in 'MSFT_TaskLogonTrigger','MSFT_TaskBootTrigger' }).Count -gt 0 } | " +
            "ForEach-Object { $a = @($_.Actions)[0]; $_.TaskPath + '|' + $_.TaskName + '|' + $_.State + '|' + $a.Execute + '|' + $a.Arguments }";

        foreach (var line in Shell.PS(ps).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split('|');
            if (p.Length < 4) continue;
            string path = p[0], name = p[1], state = p[2];
            string exe = Environment.ExpandEnvironmentVariables(p[3].Trim().Trim('"'));
            string args = p.Length > 4 ? p[4] : "";
            string target = $"-TaskPath '{Esc(path)}' -TaskName '{Esc(name)}'";

            yield return Entry(name, exe, "Планировщик заданий",
                $"Задание: {path}{name}\nЗапускается при входе в систему или загрузке.\n\nКоманда: {exe} {args}".TrimEnd(),
                details: $"PowerShell:\n  Disable-ScheduledTask {target}\nОтменить:\n  Enable-ScheduledTask {target}",
                isOn: () => state != "Disabled",
                on: () => Shell.PS($"Enable-ScheduledTask {target} | Out-Null"),
                off: () => Shell.PS($"Disable-ScheduledTask {target} | Out-Null"));
        }
    }

    static string Esc(string s) => s.Replace("'", "''");

    // ───────── Общее ─────────

    static Tweak Entry(string name, string? exe, string group, string desc, Func<bool> isOn, Action on, Action off, string? details = null)
    {
        bool exists = exe != null && File.Exists(exe);
        return new Tweak
        {
            Category = Cat, Group = group, Name = name, Description = desc,
            Details = details,
            Subtitle = exists ? Publisher(exe!) : null,
            Icon = exists ? IconOf(exe!) : null,
            OpenFolder = exists ? () => Process.Start("explorer.exe", $"/select,\"{exe}\"") : null,
            IsApplied = isOn, Apply = on, Revert = off
        };
    }

    static string? ExeFromCommand(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : null;
        }
        int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? cmd[..(i + 4)] : cmd.Split(' ')[0];
    }

    static string? LnkTarget(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            string target = shell.CreateShortcut(lnk).TargetPath;
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch { return null; }
    }

    static string? Publisher(string exe)
    {
        try
        {
            var c = FileVersionInfo.GetVersionInfo(exe).CompanyName?.Trim();
            return string.IsNullOrEmpty(c) ? null : c;
        }
        catch { return null; }
    }

    static Image? IconOf(string exe)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exe);
            if (icon == null) return null;
            using var big = icon.ToBitmap();
            var bmp = new Bitmap(20, 20);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(big, 0, 0, 20, 20);
            return bmp;
        }
        catch { return null; }
    }
}
