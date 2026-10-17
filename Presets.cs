using System.Drawing.Drawing2D;

namespace WinTweaker;

/// <summary>
/// Готовые наборы. Ссылаются на настройки по точному названию из Catalog.cs.
/// Рискованные (⚠) и разовые действия в пресеты не попадают никогда — даже если их сюда добавить.
/// </summary>
static class Presets
{
    public sealed record Preset(string Name, string Glyph, string Description, string[] Tweaks);

    public static readonly Preset[] All =
    {
        new("Рекомендуемый", "\uE735", "Безопасный набор для любого ПК", new[]
        {
            "Показывать расширения файлов",
            "Отключить советы, рекламу и автоустановку приложений",
            "Отключить веб-поиск и Bing в меню «Пуск»",
            "Отключить рекламный идентификатор",
            "Отключить рекламу на экране блокировки",
            "Отключить «Новости и интересы»",
            "Скрыть кнопку «Провести собрание»",
            "Убрать задержку автозагрузки",
            "Отключить окно залипания клавиш (5× Shift)",
            "Отключить запросы отзывов",
            "Поддержка длинных путей (>260 символов)",
            "Отключить SMBv1",
            "Защита от нежелательных программ (PUA)",
        }),
        new("Игровой", "\uE7FC", "Максимум FPS и минимум задержек", new[]
        {
            "Схема питания «Высокая производительность»",
            "Отключить фоновую запись (Game DVR)",
            "Игровой режим",
            "Отключить оптимизацию во весь экран",
            "Отключить ускорение мыши",
            "Аппаратное планирование GPU (HAGS)",
            "Приоритет мультимедиа и игр",
            "Отключить окно залипания клавиш (5× Shift)",
            "Запретить фоновые приложения",
            "Убрать задержку автозагрузки",
        }),
        new("Приватность", "\uE72E", "Меньше данных уходит в Microsoft", new[]
        {
            "Минимальная телеметрия",
            "Отключить рекламный идентификатор",
            "Отключить Кортану",
            "Отключить веб-поиск и Bing в меню «Пуск»",
            "Отключить журнал действий",
            "Отключить облачный буфер обмена",
            "Отключить советы, рекламу и автоустановку приложений",
            "Отключить сбор рукописного и клавиатурного ввода",
            "Отключить запросы отзывов",
            "Отключить персонализацию по диагностическим данным",
            "Отключить рекламу на экране блокировки",
            "Отключить: Телеметрия (DiagTrack)",
            "Отключить: WAP Push (dmwappushservice)",
        }),
        new("Слабый ПК", "\uEC4A", "Разгружает процессор, память и диск", new[]
        {
            "Визуальные эффекты: быстродействие",
            "Отключить прозрачность",
            "Отключить анимацию сворачивания окон",
            "Мгновенное открытие подменю",
            "Запретить фоновые приложения",
            "Отключить «Новости и интересы»",
            "Убрать задержку автозагрузки",
            "Отключить фоновую запись (Game DVR)",
            "Отключить гибернацию",
            "Отключить: Диспетчер скачанных карт",
            "Отключить: Отчёты об ошибках Windows",
            "Отключить: Демо-режим магазина",
            "Отключить: Факс",
        }),
    };

    /// <summary>Настройки пресета, которые реально можно включить (без ⚠ и действий).</summary>
    public static List<Tweak> Resolve(Preset p, IEnumerable<Tweak> tweaks)
    {
        var names = p.Tweaks.ToHashSet();
        return tweaks.Where(t => names.Contains(t.Name) && !t.Risky && !t.IsAction).ToList();
    }
}

/// <summary>Пресеты — широкий ряд из 4 плиток: иконка, название, описание, прогресс.</summary>
class PresetsWidget : Widget
{
    readonly (Presets.Preset P, int On, int Total, int Pending)[] items;
    readonly AnimFloat[] hover;
    readonly AnimFloat press;
    int hot = -1, pressed = -1;
    const int TileH = 132, Gap = 10;
    public event Action<Presets.Preset>? Picked;

    public PresetsWidget((Presets.Preset, int, int, int)[] items)
        : base("Пресеты — отмечают настройки, применяете вы", 28 + 12 + TileH + 12)
    {
        this.items = items;
        Wide = true;
        hover = items.Select(_ => new AnimFloat(0, () => Invalidate(false))).ToArray();
        press = new AnimFloat(0, () => Invalidate(false));
    }

    RectangleF Tile(int i)
    {
        var r = Body;
        int n = items.Length;
        float w = (r.Width - 24 - Gap * (n - 1)) / (float)n;
        return new RectangleF(r.Left + 12 + i * (w + Gap), r.Top + 12, w, TileH);
    }

    int HitTest(Point p)
    {
        for (int i = 0; i < items.Length; i++)
            if (Tile(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h != hot)
        {
            if (hot >= 0) hover[hot].To(0, 240);
            if (h >= 0) hover[h].To(1, 150);
            hot = h;
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (hot >= 0) hover[hot].To(0, 240);
        hot = -1;
        press.To(0, 200);
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        pressed = HitTest(e.Location);
        if (pressed >= 0) press.To(1, 100);
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        press.To(0, 220);
        int h = HitTest(e.Location);
        if (h >= 0 && h == pressed) Picked?.Invoke(items[h].P);
        pressed = -1;
        base.OnMouseUp(e);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        for (int i = 0; i < items.Length; i++)
        {
            var (p, on, total, pending) = items[i];
            var t = Tile(i);
            double h = hover[i].Value;
            bool done = total > 0 && on == total;

            var state = g.Save();
            float s = 1f - (float)(i == pressed ? press.Value * 0.03 : 0);
            float lift = (float)(-3 * h); // при наведении плитка чуть приподнимается
            g.TranslateTransform(t.X + t.Width / 2, t.Y + t.Height / 2 + lift);
            g.ScaleTransform(s, s);
            g.TranslateTransform(-(t.X + t.Width / 2), -(t.Y + t.Height / 2));

            if (h > 0.01) Theme.Glow(g, Theme.Accent, t, 11, 3, 2f, (int)(70 * h * Appear));
            Theme.Fill(g, Color.FromArgb((int)((16 + 14 * h) * Appear), 255, 255, 255), t, 11);
            using (var pen = new Pen(Color.FromArgb((int)((20 + 140 * h) * Appear), Theme.Lerp(Color.White, Theme.Accent, h))))
            using (var path = Theme.Round(t, 11))
                g.DrawPath(pen, path);

            var circle = new RectangleF(t.X + 14, t.Y + 14, 38, 38);
            Theme.Fill(g, Color.FromArgb((int)((48 + 50 * h) * Appear), Theme.Accent), circle, 19);
            TextRenderer.DrawText(g, p.Glyph, Theme.IconBig, Rectangle.Round(circle), C(Theme.Lerp(Theme.Accent, Color.White, 0.3 * h)),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (done)
                TextRenderer.DrawText(g, "", Theme.Icon, new Rectangle((int)t.Right - 34, (int)t.Y + 12, 22, 22), C(Theme.Good),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
            int tx = (int)t.X + 14, tw = (int)t.Width - 28;
            TextRenderer.DrawText(g, p.Name, Theme.Bold, new Rectangle(tx, (int)t.Y + 60, tw, 20), C(Theme.Text), flags);
            TextRenderer.DrawText(g, p.Description, Theme.Tiny, new Rectangle(tx, (int)t.Y + 80, tw, 18), C(Theme.Muted), flags);

            string status = pending > 0 ? $"+{pending} к применению" : done ? "всё включено" : $"включено {on} из {total}";
            var statusColor = pending > 0 ? Theme.Accent : done ? Theme.Good : Theme.Dim;
            TextRenderer.DrawText(g, status, Theme.Tiny, new Rectangle(tx, (int)t.Y + 100, tw, 18), C(statusColor), flags);

            if (total > 0)
            {
                var bar = new RectangleF(t.X + 14, t.Bottom - 9, t.Width - 28, 3);
                Theme.Fill(g, Color.FromArgb((int)(22 * Appear), 255, 255, 255), bar, 1.5f);
                Theme.Fill(g, C(done ? Theme.Good : Theme.Accent), new RectangleF(bar.X, bar.Y, (float)(bar.Width * on / total * Appear), 3), 1.5f);
            }
            g.Restore(state);
        }
    }
}

/// <summary>
/// Профиль одной строкой: «WT1-…». Каждая включённая настройка — 3 байта хеша её названия (FNV-1a),
/// всё в base64url. Скинул другу в мессенджер — он вставил и получил те же отметки.
/// </summary>
static class ProfileCode
{
    const string Prefix = "WT1-";

    static uint Hash(string s)
    {
        uint h = 2166136261;
        foreach (char ch in s)
        {
            h ^= ch;
            h *= 16777619;
        }
        return h & 0xFFFFFF;
    }

    public static string Encode(IEnumerable<Tweak> on)
    {
        var bytes = new List<byte>();
        foreach (var t in on)
        {
            uint h = Hash(t.Name);
            bytes.Add((byte)(h >> 16));
            bytes.Add((byte)(h >> 8));
            bytes.Add((byte)h);
        }
        return Prefix + Convert.ToBase64String(bytes.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>null — строка не похожа на код профиля.</summary>
    public static List<Tweak>? Decode(string? code, IEnumerable<Tweak> all)
    {
        code = code?.Trim();
        if (code == null || !code.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string b64 = code[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch { return null; }
        var wanted = new HashSet<uint>();
        for (int i = 0; i + 2 < bytes.Length; i += 3)
            wanted.Add((uint)(bytes[i] << 16 | bytes[i + 1] << 8 | bytes[i + 2]));
        return all.Where(t => wanted.Contains(Hash(t.Name))).ToList();
    }
}
