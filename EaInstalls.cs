using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinTweaker;

/// <summary>
/// Список игр, установленных через EA app. EA app хранит его в зашифрованном файле
/// %ProgramData%\EA Desktop\&lt;папка&gt;\IS: AES-256-CBC, ключ — SHA3-256 от строки
/// «allUsersGenericId» + «IS» + SHA1(сведения о железе этого ПК), вектор — первые 16 байт SHA3-256
/// от «allUsersGenericIdIS», первые 64 байта файла — служебный хэш. Внутри — JSON с installInfos
/// (baseInstallPath, baseSlug). Формат описан сообществом (проект GameFinder); реализация своя.
/// Файл только читается — ничего не меняется.
/// </summary>
static class EaInstalls
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetVolumeInformationW(string root, StringBuilder? name, int nameSize, out uint serial,
        out uint maxLen, out uint flags, StringBuilder? fs, int fsSize);

    public sealed record Entry(string Dir, string Slug);

    public static List<Entry> Read()
    {
        var result = new List<Entry>();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EA Desktop");
        if (!Directory.Exists(root)) return result;
        var files = Directory.EnumerateFiles(root, "IS", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true }).ToList();
        if (files.Count == 0) return result;

        string hw = HardwareString();
        string hwHash = Convert.ToHexString(SHA1.HashData(Encoding.ASCII.GetBytes(hw))).ToLowerInvariant();
        byte[] key = Sha3(Encoding.ASCII.GetBytes("allUsersGenericIdIS" + hwHash));
        byte[] iv = Sha3(Encoding.ASCII.GetBytes("allUsersGenericIdIS"))[..16];

        foreach (var f in files)
        {
            try
            {
                var data = File.ReadAllBytes(f);
                if (data.Length <= 64) continue;
                using var aes = Aes.Create();
                aes.Key = key;
                var plain = aes.DecryptCbc(data.AsSpan(64), iv, PaddingMode.PKCS7);
                using var doc = JsonDocument.Parse(plain);
                if (!doc.RootElement.TryGetProperty("installInfos", out var infos) || infos.ValueKind != JsonValueKind.Array) continue;
                foreach (var info in infos.EnumerateArray())
                {
                    string dir = info.TryGetProperty("baseInstallPath", out var p) ? p.GetString() ?? "" : "";
                    string slug = info.TryGetProperty("baseSlug", out var s) ? s.GetString() ?? "" : "";
                    dir = dir.Trim().TrimEnd('\\', '/');
                    if (dir != "" && Directory.Exists(dir) && !result.Any(r => r.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase)))
                        result.Add(new Entry(dir, slug));
                }
            }
            catch { /* другой формат или ключ — просто пропускаем, есть запасные способы поиска */ }
        }
        return result;
    }

    /// <summary>«Отпечаток» железа в том же виде, что и у EA app: значения через «;».</summary>
    static string HardwareString()
    {
        // WMI одним вызовом PowerShell; значения не обрезаются — пробелы важны для ключа
        const string sep = "|~|";
        string ps =
            "$b = Get-CimInstance Win32_BaseBoard | Select-Object -First 1; " +
            "$i = Get-CimInstance Win32_BIOS | Select-Object -First 1; " +
            "$v = Get-CimInstance Win32_VideoController | Select-Object -First 1; " +
            "$p = Get-CimInstance Win32_Processor | Select-Object -First 1; " +
            $"[Console]::Out.Write((@($b.Manufacturer, $b.SerialNumber, $i.Manufacturer, $i.SerialNumber, $v.PNPDeviceID, $p.Manufacturer, $p.ProcessorId, $p.Name) -join '{sep}'))";
        var parts = Shell.PS(ps).Split(sep);
        if (parts.Length < 8) throw new InvalidOperationException("WMI не ответил");
        string vol = GetVolumeInformationW(@"C:\", null, 0, out uint serial, out _, out _, null, 0) ? serial.ToString("X") : "";
        var sb = new StringBuilder();
        foreach (var x in new[] { parts[0], parts[1], parts[2], parts[3], vol, parts[4], parts[5], parts[6], parts[7] })
            sb.Append(x).Append(';');
        return sb.ToString();
    }

    // ───────── SHA3-256 (Keccak-f[1600]) — в Windows 10 встроенного нет ─────────

    static byte[] Sha3(byte[] data)
    {
        try { if (SHA3_256.IsSupported) return SHA3_256.HashData(data); } catch { }
        const int rate = 136;
        var st = new ulong[25];
        int off = 0;
        while (data.Length - off >= rate)
        {
            Absorb(st, data, off, rate);
            Keccak(st);
            off += rate;
        }
        var last = new byte[rate];
        Array.Copy(data, off, last, 0, data.Length - off);
        last[data.Length - off] ^= 0x06;
        last[rate - 1] ^= 0x80;
        Absorb(st, last, 0, rate);
        Keccak(st);
        var hash = new byte[32];
        for (int i = 0; i < 4; i++) BitConverter.TryWriteBytes(hash.AsSpan(i * 8), st[i]);
        return hash;
    }

    static void Absorb(ulong[] st, byte[] b, int off, int len)
    {
        for (int i = 0; i < len / 8; i++) st[i] ^= BitConverter.ToUInt64(b, off + i * 8);
    }

    static readonly ulong[] RC =
    {
        0x0000000000000001, 0x0000000000008082, 0x800000000000808A, 0x8000000080008000, 0x000000000000808B, 0x0000000080000001,
        0x8000000080008081, 0x8000000000008009, 0x000000000000008A, 0x0000000000000088, 0x0000000080008009, 0x000000008000000A,
        0x000000008000808B, 0x800000000000008B, 0x8000000000008089, 0x8000000000008003, 0x8000000000008002, 0x8000000000000080,
        0x000000000000800A, 0x800000008000000A, 0x8000000080008081, 0x8000000000008080, 0x0000000080000001, 0x8000000080008008,
    };

    static readonly int[] Rot = { 1, 3, 6, 10, 15, 21, 28, 36, 45, 55, 2, 14, 27, 41, 56, 8, 25, 43, 62, 18, 39, 61, 20, 44 };
    static readonly int[] Pi = { 10, 7, 11, 17, 18, 3, 5, 16, 8, 21, 24, 4, 15, 23, 19, 13, 12, 2, 20, 14, 22, 9, 6, 1 };

    static void Keccak(ulong[] a)
    {
        var c = new ulong[5];
        for (int round = 0; round < 24; round++)
        {
            for (int x = 0; x < 5; x++) c[x] = a[x] ^ a[x + 5] ^ a[x + 10] ^ a[x + 15] ^ a[x + 20];
            for (int x = 0; x < 5; x++)
            {
                ulong d = c[(x + 4) % 5] ^ BitOperations.RotateLeft(c[(x + 1) % 5], 1);
                for (int y = 0; y < 25; y += 5) a[y + x] ^= d;
            }
            ulong t = a[1];
            for (int i = 0; i < 24; i++)
            {
                int j = Pi[i];
                ulong tmp = a[j];
                a[j] = BitOperations.RotateLeft(t, Rot[i]);
                t = tmp;
            }
            for (int y = 0; y < 25; y += 5)
            {
                ulong a0 = a[y], a1 = a[y + 1], a2 = a[y + 2], a3 = a[y + 3], a4 = a[y + 4];
                a[y] = a0 ^ (~a1 & a2);
                a[y + 1] = a1 ^ (~a2 & a3);
                a[y + 2] = a2 ^ (~a3 & a4);
                a[y + 3] = a3 ^ (~a4 & a0);
                a[y + 4] = a4 ^ (~a0 & a1);
            }
            a[0] ^= RC[round];
        }
    }
}
