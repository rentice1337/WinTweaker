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
        new("Рекомендуемый", "", "Безопасный набор для любого ПК", new[]
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
        new("Игровой", "", "Максимум FPS и минимум задержек", new[]
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
        new("Приватность", "", "Меньше данных уходит в Microsoft", new[]
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
        new("Слабый ПК", "", "Разгружает процессор, память и диск", new[]
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

/// <summary>Плитки пресетов 2×2: подсветка и «нажатие» при наведении, счётчик включённых.</summary>
class PresetsWidget : Widget
{
    readonly (Presets.Preset P, int On, int Total, int Pending)[] items;
    readonly AnimFloat[] hover;
    readonly AnimFloat press;
    int hot = -1, pressed = -1;
    const int TileH = 88, Gap = 10;
    public event Action<Presets.Preset>? Picked;

    public PresetsWidget((Presets.Preset, int, int, int)[] items)
        : base("Пресеты", Head + 12 + 2 * TileH + Gap + 12)
    {
        this.items = items;
        hover = items.Select(_ => new AnimFloat(0, () => Invalidate())).ToArray();
        press = new AnimFloat(0, () => Invalidate());
    }

    RectangleF Tile(int i)
    {
        var r = Body;
        float w = (r.Width - 24 - Gap) / 2f;
        int col = i % 2, row = i / 2;
        return new RectangleF(r.Left + 12 + col * (w + Gap), r.Top + 12 + row * (TileH + Gap), w, TileH);
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
            if (hot >= 0) hover[hot].To(0, 220);
            if (h >= 0) hover[h].To(1, 150);
            hot = h;
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (hot >= 0) hover[hot].To(0, 220);
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

            var state = g.Save();
            float s = 1f - (float)(i == pressed ? press.Value * 0.03 : 0);
            g.TranslateTransform(t.X + t.Width / 2, t.Y + t.Height / 2);
            g.ScaleTransform(s, s);
            g.TranslateTransform(-(t.X + t.Width / 2), -(t.Y + t.Height / 2));

            Theme.Fill(g, C(Theme.Lerp(Theme.Hover, Theme.Line, h)), t, 9);
            bool done = total > 0 && on == total;

            // иконка в круге
            var circle = new RectangleF(t.X + 14, t.Y + 14, 34, 34);
            using (var b = new SolidBrush(C(Theme.Lerp(Theme.Hover, Theme.Accent, 0.22 + 0.2 * h))))
                g.FillEllipse(b, circle);
            TextRenderer.DrawText(g, p.Glyph, Theme.Icon, Rectangle.Round(circle), C(Theme.Accent),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
            int tx = (int)t.X + 58, tw = (int)t.Width - 70;
            TextRenderer.DrawText(g, p.Name, Theme.Bold, new Rectangle(tx, (int)t.Y + 13, tw, 20), C(Theme.Text), flags);
            TextRenderer.DrawText(g, p.Description, Theme.F(8.5f), new Rectangle(tx, (int)t.Y + 33, tw, 18), C(Theme.Muted), flags);

            string status = pending > 0 ? $"+{pending} к применению"
                          : done ? "✓ всё включено"
                          : $"включено {on} из {total}";
            var statusColor = pending > 0 ? Theme.Accent : done ? Theme.Text : Theme.Dim;
            TextRenderer.DrawText(g, status, Theme.F(8.25f), new Rectangle(tx, (int)t.Y + 58, tw, 18), C(statusColor), flags);

            // мини-прогресс по нижнему краю плитки
            if (total > 0)
            {
                var bar = new RectangleF(t.X + 14, t.Bottom - 7, t.Width - 28, 2);
                Theme.Fill(g, C(Theme.Off), bar, 1);
                Theme.Fill(g, C(Theme.Accent), new RectangleF(bar.X, bar.Y, (float)(bar.Width * on / total * Appear), 2), 1);
            }
            g.Restore(state);
        }
    }
}
