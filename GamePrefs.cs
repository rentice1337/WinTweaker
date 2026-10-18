using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;

namespace WinTweaker;

/// <summary>
/// Правки списка игр: скрытые, свои названия и обложки, порядок, закреплённые
/// и лаунчер по умолчанию для игры, установленной в нескольких местах.
/// Игра опознаётся по ключу из названия — одна и та же игра из Steam, EA app и добавленная вручную имеет один ключ.
/// Хранится в %AppData%\WinTweaker\gameprefs.json.
/// </summary>
static class GamePrefs
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinTweaker");
    static readonly string FileName = Path.Combine(Dir, "gameprefs.json");
    static readonly string CustomDir = Path.Combine(Dir, "covers", "custom");

    sealed class Data
    {
        public List<string> Hidden { get; set; } = new();
        public List<string> Pinned { get; set; } = new();
        public List<string> Order { get; set; } = new();
        public Dictionary<string, string> Names { get; set; } = new();
        public Dictionary<string, string> Covers { get; set; } = new();
        public Dictionary<string, string> Launcher { get; set; } = new();
    }

    static Data d = Load();
    static readonly Dictionary<string, Image?> covers = new();

    /// <summary>Что-то поменялось — списки игр нужно перерисовать.</summary>
    public static event Action? Changed;

    static Data Load()
    {
        try { return JsonSerializer.Deserialize<Data>(File.ReadAllText(FileName)) ?? new(); }
        catch { return new(); }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FileName, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        Changed?.Invoke();
    }

    /// <summary>Ключ игры: название без регистра, пробелов и знаков («Counter-Strike: GO» = «counterstrikego»).</summary>
    public static string KeyOf(string title) =>
        new string(title.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()) is { Length: > 0 } k ? k : title.ToLowerInvariant();

    // ───── скрытие и закрепление ─────

    public static bool IsHidden(string key) => d.Hidden.Contains(key);
    public static bool IsPinned(string key) => d.Pinned.Contains(key);

    public static void SetHidden(string key, bool on)
    {
        d.Hidden.Remove(key);
        if (on) { d.Hidden.Add(key); d.Pinned.Remove(key); }
        Save();
    }

    public static void SetPinned(string key, bool on)
    {
        d.Pinned.Remove(key);
        if (on) { d.Pinned.Add(key); d.Hidden.Remove(key); }
        Save();
    }

    // ───── порядок ─────

    public static int OrderOf(string key)
    {
        int i = d.Order.IndexOf(key);
        return i < 0 ? int.MaxValue : i;
    }

    /// <summary>Запомнить порядок и закреплённые после перетаскивания.</summary>
    public static void SetOrder(IEnumerable<string> keys, IEnumerable<string> pinned)
    {
        var list = keys.ToList();
        // игры, которых сейчас нет в списке (скрытые, удалённые), сохраняют место в хвосте
        d.Order = list.Concat(d.Order.Where(k => !list.Contains(k))).ToList();
        d.Pinned = pinned.ToList();
        Save();
    }

    public static void ResetOrder()
    {
        d.Order.Clear();
        d.Pinned.Clear();
        Save();
    }

    // ───── название ─────

    public static string? NameOf(string key) => d.Names.TryGetValue(key, out var n) ? n : null;

    public static void SetName(string key, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) d.Names.Remove(key);
        else d.Names[key] = name.Trim();
        Save();
    }

    // ───── лаунчер ─────

    public static string? LauncherOf(string key) => d.Launcher.TryGetValue(key, out var s) ? s : null;

    public static void SetLauncher(string key, string? source)
    {
        if (source == null) d.Launcher.Remove(key);
        else d.Launcher[key] = source;
        Save();
    }

    // ───── своя обложка ─────

    public static bool HasCover(string key) => d.Covers.ContainsKey(key);

    public static Image? CoverOf(string key)
    {
        if (!d.Covers.TryGetValue(key, out var file)) return null;
        if (covers.TryGetValue(key, out var img)) return img;
        img = GameLibrary.LoadCover(Path.Combine(CustomDir, file));
        covers[key] = img;
        return img;
    }

    /// <summary>Взять картинку с компьютера: обрезается под плитку и сохраняется у себя (оригинал можно удалить).</summary>
    public static void SetCover(string key, string sourceFile)
    {
        var img = GameLibrary.LoadCover(sourceFile) ?? throw new InvalidOperationException("Не удалось открыть картинку");
        Directory.CreateDirectory(CustomDir);
        string name = key + "-" + DateTime.Now.Ticks.ToString("x") + ".png";
        img.Save(Path.Combine(CustomDir, name), ImageFormat.Png);
        if (d.Covers.TryGetValue(key, out var old)) try { File.Delete(Path.Combine(CustomDir, old)); } catch { }
        d.Covers[key] = name;
        covers[key] = img;
        Save();
    }

    public static void ResetCover(string key)
    {
        if (d.Covers.Remove(key, out var old)) try { File.Delete(Path.Combine(CustomDir, old)); } catch { }
        covers.Remove(key);
        Save();
    }
}

/// <summary>
/// Готовые картинки игр под точный размер на экране: обложка со сглаженными углами и круглая «монета».
/// Масштабирование — один раз и в высоком качестве, дальше рисуется пиксель в пиксель: без мыла, лесенки и лагов.
/// </summary>
static class GameArt
{
    static readonly Dictionary<(Image?, Image?, int, int, int), Bitmap> cache = new();

    static Bitmap Remember((Image?, Image?, int, int, int) key, Func<Bitmap> make)
    {
        if (cache.TryGetValue(key, out var b)) return b;
        if (cache.Count > 300)
        {
            foreach (var v in cache.Values) v.Dispose();
            cache.Clear();
        }
        return cache[key] = make();
    }

    /// <summary>Нарисовать готовую картинку пиксель в пиксель (без пересчёта по DPI и без размытия), с прозрачностью.</summary>
    public static void Blit(Graphics g, Bitmap bmp, int x, int y, double alpha = 1)
    {
        if (alpha <= 0.003) return;
        var st = g.Save();
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.None;
        var dest = new Rectangle(x, y, bmp.Width, bmp.Height);
        if (alpha >= 0.997) g.DrawImage(bmp, dest, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel);
        else
        {
            using var ia = new ImageAttributes();
            ia.SetColorMatrix(new ColorMatrix { Matrix33 = (float)alpha });
            g.DrawImage(bmp, dest, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
        }
        g.Restore(st);
    }

    /// <summary>Скруглённый прямоугольник; top — скругляются только верхние углы.</summary>
    public static GraphicsPath Shape(RectangleF r, float rad, bool topOnly)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        if (rad <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        if (topOnly)
        {
            p.AddLine(r.Right, r.Y + rad, r.Right, r.Bottom);
            p.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
        }
        else
        {
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        }
        p.CloseFigure();
        return p;
    }

    /// <summary>Картинка, заполняющая w×h с обрезкой по центру, в высоком качестве.</summary>
    static Bitmap Fill(Image src, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        float s = Math.Max((float)w / src.Width, (float)h / src.Height);
        float sw = w / s, sh = h / s;
        using var ia = new ImageAttributes();
        ia.SetWrapMode(WrapMode.TileFlipXY); // без полупрозрачной каймы по краям
        g.DrawImage(src, new Rectangle(0, 0, w, h), (src.Width - sw) / 2, (src.Height - sh) / 2, sw, sh, GraphicsUnit.Pixel, ia);
        return bmp;
    }

    /// <summary>Своя «обложка» для игры без картинки: градиент акцента и крупный значок exe.</summary>
    static Bitmap Placeholder(InstalledGame game, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        using (var lg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Theme.Lerp(Theme.Card, Theme.Accent, 0.35), Theme.Lerp(Theme.Bg, Theme.Accent, 0.08), 35f))
            g.FillRectangle(lg, 0, 0, w, h);
        int s = Math.Clamp(Math.Min(w, h) / 2, 24, 64);
        if (game.ShownIcon is { } icon)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(icon, new Rectangle((w - s) / 2, (h - s) / 2, s, s));
        }
        else
        {
            using var f = new Font("Segoe MDL2 Assets", s * 0.45f, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, "", f, new Rectangle(0, 0, w, h), Color.FromArgb(200, 255, 255, 255),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        return bmp;
    }

    static Bitmap Clip(Bitmap content, GraphicsPath shape, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var tb = new TextureBrush(content, WrapMode.Clamp);
        g.FillPath(tb, shape);
        return bmp;
    }

    /// <summary>Обложка игры w×h со сглаженными углами (topOnly — скруглены только верхние).</summary>
    public static Bitmap Cover(InstalledGame game, int w, int h, int rad, bool topOnly)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        var src = game.ShownCover;
        return Remember((src, src == null ? game.ShownIcon : null, w, h, topOnly ? rad : -rad - 1), () =>
        {
            using var content = src != null ? Fill(src, w, h) : Placeholder(game, w, h);
            using var shape = Shape(new RectangleF(0, 0, w, h), rad, topOnly);
            return Clip(content, shape, w, h);
        });
    }

    /// <summary>Круглая «монета» игры диаметром d: центр обложки или значок.</summary>
    public static Bitmap Coin(InstalledGame game, int d)
    {
        d = Math.Max(2, d);
        var src = game.ShownCover;
        return Remember((src, src == null ? game.ShownIcon : null, d, d, int.MinValue), () =>
        {
            Bitmap content;
            if (src != null)
            {
                // квадрат из центра обложки
                int side = Math.Min(src.Width, src.Height);
                using var sq = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(sq))
                    g.DrawImage(src, new Rectangle(0, 0, side, side), new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side), GraphicsUnit.Pixel);
                content = Fill(sq, d, d);
            }
            else content = Placeholder(game, d, d);
            using (content)
            using (var shape = new GraphicsPath())
            {
                shape.AddEllipse(0.5f, 0.5f, d - 1, d - 1);
                return Clip(content, shape, d, d);
            }
        });
    }
}
