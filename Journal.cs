using System.Text.Json;
using Microsoft.Win32;

namespace WinTweaker;

/// <summary>
/// Журнал изменений для точного отката.
/// Во время «Применить» каждая настройка открывает запись (Begin/End); все вызовы Reg.* в это время
/// сохраняют исходное значение параметра (один раз), а настройки со своим снимком (службы, схема питания, DNS)
/// дополнительно сохраняют Snapshot. Сеансы лежат в C:\ProgramData\WinTweaker\history\*.json.
/// </summary>
public static class Journal
{
    public sealed class RegOp
    {
        public string Path { get; set; } = "";
        public string? Name { get; set; }          // null — операция над разделом целиком
        public bool Existed { get; set; }
        public string? Kind { get; set; }
        public string? Str { get; set; }
        public long? Num { get; set; }
        public string[]? Multi { get; set; }
        public string? Bin { get; set; }
        public List<RegOp>? KeyValues { get; set; } // значения удалённого раздела
    }

    public sealed class Item
    {
        public string Category { get; set; } = "";
        public string Name { get; set; } = "";
        public bool WasApplied { get; set; }
        public bool NowApplied { get; set; }
        public bool Irreversible { get; set; }
        public string? Snapshot { get; set; }
        public List<RegOp> Reg { get; set; } = new();
        /// <summary>Для настроек со значением: было/стало.</summary>
        public int? FromValue { get; set; }
        public int? ToValue { get; set; }
        public string? FromText { get; set; }
        public string? ToText { get; set; }
    }

    public sealed class Session
    {
        public string Id { get; set; } = "";
        public DateTime Date { get; set; }
        public bool RolledBack { get; set; }
        public List<Item> Items { get; set; } = new();
    }

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinTweaker", "history");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Folder => Dir;

    static Item? current;

    // ───────── Запись ─────────

    public static Session StartSession() => new() { Id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"), Date = DateTime.Now };

    public static void Begin(Session s, Tweak t, bool wasApplied, bool willApply)
    {
        current = new Item
        {
            Category = t.Category,
            Name = t.Name,
            WasApplied = wasApplied,
            NowApplied = willApply,
            Irreversible = willApply && t.Revert == null
        };
        try { current.Snapshot = t.Snapshot?.Invoke(); } catch { }
        s.Items.Add(current);
    }

    /// <summary>Начало записи для настройки со значением (выбор/ползунок).</summary>
    public static void BeginValue(Session s, Tweak t, int from, int to)
    {
        current = new Item
        {
            Category = t.Category,
            Name = t.Name,
            FromValue = from,
            ToValue = to,
            FromText = t.ValueText(from),
            ToText = t.ValueText(to)
        };
        try { current.Snapshot = t.Snapshot?.Invoke(); } catch { }
        s.Items.Add(current);
    }

    public static void End() => current = null;

    /// <summary>Вызывается из Reg перед изменением значения.</summary>
    internal static void CaptureValue(RegistryKey root, string sub, string fullPath, string name)
    {
        if (current == null || current.Reg.Any(o => o.Path == fullPath && o.Name == name)) return;
        using var k = root.OpenSubKey(sub);
        current.Reg.Add(Read(k, fullPath, name));
    }

    /// <summary>Вызывается из Reg перед созданием или удалением раздела.</summary>
    internal static void CaptureKey(RegistryKey root, string sub, string fullPath)
    {
        if (current == null || current.Reg.Any(o => o.Path == fullPath && o.Name == null)) return;
        using var k = root.OpenSubKey(sub);
        current.Reg.Add(new RegOp
        {
            Path = fullPath,
            Existed = k != null,
            KeyValues = k?.GetValueNames().Select(n => Read(k, fullPath, n)).ToList()
        });
    }

    static RegOp Read(RegistryKey? k, string path, string name)
    {
        var op = new RegOp { Path = path, Name = name };
        if (k == null || !k.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return op;
        var kind = k.GetValueKind(name);
        var v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        op.Existed = true;
        op.Kind = kind.ToString();
        switch (v)
        {
            case int i: op.Num = i; break;
            case long l: op.Num = l; break;
            case string s: op.Str = s; break;
            case string[] m: op.Multi = m; break;
            case byte[] b: op.Bin = Convert.ToBase64String(b); break;
        }
        return op;
    }

    public static void Save(Session s)
    {
        if (s.Items.Count == 0) return;
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, s.Id + ".json"), JsonSerializer.Serialize(s, Json));
    }

    public static List<Session> LoadAll()
    {
        if (!Directory.Exists(Dir)) return new();
        var list = new List<Session>();
        foreach (var f in Directory.GetFiles(Dir, "*.json"))
        {
            try
            {
                var s = JsonSerializer.Deserialize<Session>(File.ReadAllText(f));
                if (s != null) list.Add(s);
            }
            catch { /* повреждённый файл пропускаем */ }
        }
        return list.OrderByDescending(s => s.Date).ToList();
    }

    // ───────── Откат ─────────

    /// <summary>Откатывает сеанс. Возвращает названия того, что вернуть нельзя (удалённые приложения).</summary>
    public static List<string> Rollback(Session s, IReadOnlyList<Tweak> tweaks)
    {
        current = null; // откат сам не журналируется
        var skipped = new List<string>();
        foreach (var item in Enumerable.Reverse(s.Items))
        {
            if (item.Irreversible)
            {
                skipped.Add(item.Name);
                continue;
            }
            var t = tweaks.FirstOrDefault(x => x.Name == item.Name && x.Category == item.Category);

            foreach (var op in Enumerable.Reverse(item.Reg)) RestoreOp(op);

            if (item.Snapshot != null && t?.Restore != null)
                t.Restore(item.Snapshot);
            else if (item.FromValue != null && t?.Set != null)
            {
                // значение: реестр уже восстановлен выше, Set применяет его «вживую» (скорость мыши и т.п.)
                try { t.Set(item.FromValue.Value); } catch { }
            }
            else if (item.Reg.Count == 0 && t != null)
            {
                // изменение было не через реестр (PowerShell, sc, планировщик) — возвращаем прежнее состояние
                if (item.WasApplied) t.Apply(); else t.Revert?.Invoke();
            }
        }
        s.RolledBack = true;
        Save(s);
        return skipped;
    }

    static void RestoreOp(RegOp op)
    {
        try
        {
            var (root, sub) = Reg.SplitPath(op.Path);
            if (op.Name == null)
            {
                if (!op.Existed) { root.DeleteSubKeyTree(sub, false); return; }
                using var key = root.CreateSubKey(sub, true);
                foreach (var v in op.KeyValues ?? new()) WriteValue(key, v);
                return;
            }
            if (!op.Existed)
            {
                using var k = root.OpenSubKey(sub, true);
                k?.DeleteValue(op.Name, false);
                return;
            }
            using var w = root.CreateSubKey(sub, true);
            WriteValue(w, op);
        }
        catch { /* значение могло стать недоступным — продолжаем откат остального */ }
    }

    static void WriteValue(RegistryKey k, RegOp op)
    {
        if (!op.Existed || op.Name == null) return;
        var kind = Enum.Parse<RegistryValueKind>(op.Kind ?? "String");
        object value = kind switch
        {
            RegistryValueKind.DWord => (int)(op.Num ?? 0),
            RegistryValueKind.QWord => op.Num ?? 0L,
            RegistryValueKind.MultiString => op.Multi ?? Array.Empty<string>(),
            RegistryValueKind.Binary => Convert.FromBase64String(op.Bin ?? ""),
            _ => op.Str ?? ""
        };
        k.SetValue(op.Name, value, kind);
    }
}
