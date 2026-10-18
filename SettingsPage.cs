using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json;

namespace WinTweaker;

/// <summary>Страница «Настройки»: оформление, применение, свои наборы, профиль, о программе.</summary>
public partial class MainForm
{
    const string Repo = "rentice1337/WinTweaker";

    void BuildSettings()
    {
        var cv = view.Canvas;
        cv.Controls.Add(new Header(SettingsPage, "оформление, поведение программы, наборы и профиль"));

        // ── Оформление ──
        var look = new OptionsCard("Оформление");
        var picker = new AccentPicker(Array.FindIndex(Theme.Accents, a => a.Color == Theme.Accent));
        picker.Picked += ChangeAccent;
        look.AddCustom("Цвет акцента", "Кнопки, переключатели, свечение и логотип", picker);
        look.Add("Подсветка под курсором", "Рамки карточек подсвечиваются акцентом рядом с курсором",
            () => AppSettings.Get("spot", "1") == "1", on => { AppSettings.Set("spot", on ? "1" : "0"); ApplyUiMode(rebuild: false); });
        look.Add("Анимации", "Плавные переходы и появление карточек. Выключите на слабом ПК",
            () => AppSettings.Get("anim", "1") == "1", on => { AppSettings.Set("anim", on ? "1" : "0"); ApplyUiMode(rebuild: false); });
        cv.Controls.Add(look);

        // ── Применение ──
        var apply = new OptionsCard("Применение");
        apply.Add("Точка восстановления", "Перед рискованными изменениями: «для опытных», службы, удаление приложений (то же, что переключатель сверху)",
            () => restoreToggle.Checked, on => { restoreToggle.Checked = on; AppSettings.Set("restore", on ? "1" : "0"); });
        apply.Add("Предлагать перезапуск Проводника", "После изменений панели задач, меню и Проводника",
            () => AppSettings.Get("askExplorer", "1") == "1", on => AppSettings.Set("askExplorer", on ? "1" : "0"));
        apply.Add("Сторож настроек при входе в Windows", "Проверять, не откатила ли Windows ваши настройки, и уведомлять",
            () => watchOn, async on =>
            {
                bool ok = await RunBusy(on ? "Включаю сторожа…" : "Выключаю сторожа…", on ? () => Watch.Install() : () => Watch.Uninstall());
                watchOn = ok ? on : !on;
                if (ok) Toast.Pop(this, on ? "Сторож включён — проверка при каждом входе" : "Сторож выключен");
            });
        apply.Add("Проверять обновления WinTweaker", "При запуске — по релизам на GitHub",
            () => AppSettings.Get("updates", "1") == "1", on => AppSettings.Set("updates", on ? "1" : "0"));
        cv.Controls.Add(apply);

        // ── Трей ──
        var tray = new OptionsCard("Трей");
        tray.Add("Закрывать в трей", "Крестик прячет окно в трей, выход — из панели значка в трее",
            () => AppSettings.Get("tray", "1") == "1", on => AppSettings.Set("tray", on ? "1" : "0"));
        tray.Add("Запуск вместе с Windows", "Сразу в трей, без окна и без запроса UAC (задание планировщика)",
            () => autostartOn, async on =>
            {
                bool ok = await RunBusy(on ? "Включаю автозапуск…" : "Выключаю автозапуск…", on ? () => Autostart.Install() : () => Autostart.Uninstall());
                autostartOn = ok ? on : !on;
            });
        cv.Controls.Add(tray);
        BuildWheelCard(cv);

        // ── Свои наборы ──
        var sets = new SetsWidget(Sets.Load(), s => Shareable.Count(t => s.Tweaks.Contains(t.Name)));
        sets.SaveClicked += SaveSet;
        sets.MarkClicked += MarkSet;
        sets.DeleteClicked += DeleteSet;
        cv.Controls.Add(sets);

        // ── Профиль ──
        var copy = new ActionTile("", "Скопировать код", "Строка WT1-… для друга");
        copy.Click += (_, _) => CopyCode();
        var paste = new ActionTile("", "Вставить код", "Из буфера обмена");
        paste.Click += (_, _) => PasteCode();
        var save = new ActionTile("", "Сохранить в файл", "Список отмеченных настроек");
        save.Click += (_, _) => SaveProfile();
        var load = new ActionTile("", "Загрузить из файла", "Отметить настройки из .txt");
        load.Click += (_, _) => LoadProfile();
        cv.Controls.Add(new ActionsWidget("Профиль", copy, paste, save, load));

        // ── О программе ──
        var about = new AboutWidget();
        about.Github.Click += (_, _) => Live.Start($"https://github.com/{Repo}");
        about.Check.Click += async (_, _) => await CheckUpdates(manual: true);
        cv.Controls.Add(about);
    }

    bool watchOn, autostartOn;

    /// <summary>Применить настройки эффектов (подсветка, анимации).</summary>
    void ApplyUiMode(bool rebuild = true)
    {
        Motion.UserOff = AppSettings.Get("anim", "1") == "0";
        Spotlight.Enabled = AppSettings.Get("spot", "1") == "1";
        if (rebuild) Glass.Rebuild();
        Invalidate(true);
    }

    void ApplyAppSettings()
    {
        ApplyUiMode(rebuild: false);
        restoreToggle.Checked = AppSettings.Get("restore", "1") == "1";
        restoreToggle.CheckedChanged += (_, _) => AppSettings.Set("restore", restoreToggle.Checked ? "1" : "0");
    }

    // ───────── Свои наборы ─────────

    void SaveSet()
    {
        var on = Shareable.Where(t => desired.GetValueOrDefault(t)).Select(t => t.Name).ToList();
        if (on.Count == 0) { Toast.Pop(this, "Нечего сохранять — отметьте настройки", Tone.Info); return; }
        string? name = Prompt.Ask(this, "Новый набор", $"Сохранить {on.Count} отмеченных настроек как набор. Как его назвать?", "Мой набор");
        if (string.IsNullOrWhiteSpace(name)) return;
        var sets = Sets.Load();
        sets.RemoveAll(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        sets.Add(new Sets.Set { Name = name.Trim(), Created = DateTime.Now, Tweaks = on });
        Sets.Save(sets);
        Build();
        Toast.Pop(this, $"Набор «{name.Trim()}» сохранён: {on.Count}");
    }

    void MarkSet(Sets.Set set)
    {
        int marked = 0;
        foreach (var t in Shareable.Where(t => set.Tweaks.Contains(t.Name)))
            if (!desired.GetValueOrDefault(t)) { desired[t] = true; marked++; }
        UpdatePending();
        Build();
        Toast.Pop(this, marked == 0 ? $"«{set.Name}»: всё уже отмечено" : $"«{set.Name}»: отмечено {marked} — нажмите «Применить»",
            marked == 0 ? Tone.Info : Tone.Success);
    }

    void DeleteSet(Sets.Set set)
    {
        if (!Dialog.Confirm(this, "Удалить набор?", $"«{set.Name}» — {set.Tweaks.Count} настроек. Сами настройки не изменятся.", "Удалить", "Отмена", Tone.Warning)) return;
        var sets = Sets.Load();
        sets.RemoveAll(s => s.Name == set.Name);
        Sets.Save(sets);
        Build();
    }

    // ───────── Обновления ─────────

    async Task CheckUpdates(bool manual)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WinTweaker");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            string url = doc.RootElement.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || !Version.TryParse(Logo.Version, out var mine)) return;
            // «2.0» и «2.0.0» — одна версия: сравниваем по трём числам, недостающее считаем нулём
            static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
            if (Norm(latest) > Norm(mine))
            {
                if (Dialog.Confirm(this, $"Доступна версия {tag}", $"У вас v{Logo.Version}. Открыть страницу загрузки?", "Открыть", "Позже"))
                    Live.Start(url);
            }
            else if (manual) Toast.Pop(this, $"У вас последняя версия — v{Logo.Version}", Tone.Info);
        }
        catch (Exception ex)
        {
            if (manual) Dialog.Info(this, "Не удалось проверить обновления", ex.Message, Tone.Warning);
        }
    }
}

/// <summary>Карточка с параметрами программы: заголовок, пояснение и переключатель (меняется сразу).</summary>
class OptionsCard : Surface
{
    const int RowH = 54;

    public OptionsCard(string title) : base(title) { }

    public void Add(string title, string sub, Func<bool> get, Action<bool> set)
    {
        var tg = new Toggle { Checked = get() };
        tg.CheckedChanged += (_, _) => set(tg.Checked);
        AddCustom(title, sub, tg);
    }

    public void AddCustom(string title, string sub, Control control)
    {
        Controls.Add(new OptionRow(title, sub, control));
        Height = HeadH + 8 + Controls.Count * RowH + 8;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int y = HeadH + 8;
        foreach (Control c in Controls)
        {
            c.SetBounds(6, y, Width - 12, RowH);
            y += RowH;
        }
    }
}

class OptionRow : Control
{
    readonly string sub;
    readonly Control control;

    public OptionRow(string title, string sub, Control control)
    {
        Text = title;
        this.sub = sub;
        this.control = control;
        SetStyle(Theme.Smooth, true);
        Controls.Add(control);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (control is null) return;
        control.Location = new Point(Width - control.Width - 8, (Height - control.Height) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Glass.Back(g, this);
        int w = Width - (control?.Width ?? 0) - 36;
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
        TextRenderer.DrawText(g, Text, Theme.Semi, new Rectangle(12, 8, w, 20), Theme.Fade(this, Theme.Text), flags);
        TextRenderer.DrawText(g, sub, Theme.Tiny, new Rectangle(12, 28, w, 18), Theme.Fade(this, Theme.Muted), flags);
    }
}

/// <summary>Выбор акцента: крупные круги со свечением, выбранный — с кольцом и галочкой.</summary>
class AccentPicker : Control
{
    readonly AnimFloat[] grow;
    int selected, hover = -1;
    const int D = 24, Gap = 12, X0 = 6;
    public event Action<int>? Picked;

    public AccentPicker(int selected)
    {
        this.selected = Math.Max(0, selected);
        SetStyle(Theme.Smooth, true);
        grow = Theme.Accents.Select(_ => new AnimFloat(0, () => Invalidate())).ToArray();
        Size = new Size(X0 * 2 + Theme.Accents.Length * (D + Gap) - Gap, 40);
        Cursor = Cursors.Hand;
    }

    int Hit(Point p)
    {
        int i = (p.X - X0 + Gap / 2) / (D + Gap);
        return i >= 0 && i < grow.Length ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = Hit(e.Location);
        if (h != hover)
        {
            if (hover >= 0) grow[hover].To(0, 200);
            if (h >= 0) grow[h].To(1, 150);
            hover = h;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (hover >= 0) grow[hover].To(0, 200);
        hover = -1;
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int h = Hit(e.Location);
        if (h >= 0 && h != selected)
        {
            selected = h;
            Invalidate();
            Picked?.Invoke(h);
        }
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Glass.Back(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 0; i < grow.Length; i++)
        {
            float cx = X0 + i * (D + Gap) + D / 2f, cy = Height / 2f;
            float r = (float)(D / 2f + 2 * grow[i].Value);
            var c = Theme.Accents[i].Color;
            var circle = new RectangleF(cx - r, cy - r, r * 2, r * 2);
            Theme.Glow(g, c, circle, r, 3, 1.6f, (int)(60 + 60 * grow[i].Value));
            using (var b = new SolidBrush(c)) g.FillEllipse(b, circle);
            if (i == selected)
            {
                using var pen = new Pen(Color.White, 2f);
                g.DrawEllipse(pen, cx - r - 4, cy - r - 4, (r + 4) * 2, (r + 4) * 2);
                TextRenderer.DrawText(g, "", Theme.Icon, Rectangle.Round(circle), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }
}

/// <summary>Свои наборы: список с кнопками «Отметить» и «Удалить», сверху — «Сохранить текущие».</summary>
class SetsWidget : Widget
{
    readonly List<Sets.Set> sets;
    public event Action? SaveClicked;
    public event Action<Sets.Set>? MarkClicked;
    public event Action<Sets.Set>? DeleteClicked;
    const int RowH = 50;

    public SetsWidget(List<Sets.Set> sets, Func<Sets.Set, int> available)
        : base("Мои наборы", 28 + 12 + 60 + 10 + Math.Max(1, sets.Count) * RowH + 8)
    {
        this.sets = sets;
        var add = new ActionTile("", "Сохранить текущие отметки как набор", "Потом включаются одним кликом — здесь или через Ctrl+K");
        add.Click += (_, _) => SaveClicked?.Invoke();
        Controls.Add(add);
        foreach (var s in sets)
        {
            var mark = new FlatBtn("", "Отметить");
            mark.Click += (_, _) => MarkClicked?.Invoke(s);
            var del = new FlatBtn("", "") { Danger = true };
            del.Click += (_, _) => DeleteClicked?.Invoke(s);
            mark.Tag = s;
            del.Tag = s;
            Controls.Add(mark);
            Controls.Add(del);
        }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (sets is null) return;
        int i = -1;
        foreach (Control c in Controls)
        {
            if (c is ActionTile) { c.SetBounds(12, HeadH + 12, Width - 24, 60); continue; }
            if (c is FlatBtn b && b.Text != "") i++;
            int y = HeadH + 12 + 70 + i * RowH + (RowH - 32) / 2 - 4;
            if (c is FlatBtn fb && fb.Text == "") c.Location = new Point(Width - c.Width - 16, y);
            else c.Location = new Point(Width - 16 - 38 - 6 - c.Width, y);
        }
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
        int top = r.Top + 12 + 70;
        if (sets.Count == 0)
        {
            TextRenderer.DrawText(g, "Наборов пока нет. Отметьте нужные настройки в разделах и сохраните их здесь.", Theme.Tiny,
                new Rectangle(r.Left + 18, top + 8, r.Width - 36, 32), C(Theme.Muted), TextFormatFlags.WordBreak);
            return;
        }
        for (int i = 0; i < sets.Count; i++)
        {
            var s = sets[i];
            int y = top + i * RowH;
            var icon = new RectangleF(r.Left + 16, y + 6, 32, 32);
            Theme.Fill(g, Color.FromArgb((int)(50 * Appear), Theme.Accent), icon, 16);
            TextRenderer.DrawText(g, "", Theme.Icon, Rectangle.Round(icon), C(Theme.Accent), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            int w = r.Width - 64 - 170;
            TextRenderer.DrawText(g, s.Name, Theme.Semi, new Rectangle(r.Left + 58, y + 4, w, 20), C(Theme.Text), flags);
            TextRenderer.DrawText(g, $"{s.Tweaks.Count} настроек · {s.Created:d MMMM}", Theme.Tiny, new Rectangle(r.Left + 58, y + 24, w, 18), C(Theme.Muted), flags);
        }
    }
}

/// <summary>О программе: логотип, версия, ссылки.</summary>
class AboutWidget : Widget
{
    public readonly FlatBtn Github = new("", "GitHub");
    public readonly FlatBtn Check = new("", "Проверить обновления");

    public AboutWidget() : base("О программе", 28 + 150)
    {
        Controls.Add(Github);
        Controls.Add(Check);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Github is null || Check is null) return;
        Github.Location = new Point(18, Height - Github.Height - 16);
        Check.Location = new Point(Github.Right + 8, Github.Top);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        Logo.Tile(g, new RectangleF(r.Left + 18, r.Top + 18, 48, 48));
        TextRenderer.DrawText(g, "WinTweak", Theme.F(15f, FontStyle.Bold), new Point(r.Left + 78, r.Top + 16), C(Theme.Text));
        TextRenderer.DrawText(g, $"Версия {Logo.Version} · {Logo.Copyright}", Theme.Tiny, new Point(r.Left + 80, r.Top + 44), C(Theme.Muted));
        TextRenderer.DrawText(g, "Каждое изменение записывается в «Историю» и откатывается в один клик.", Theme.Tiny,
            new Rectangle(r.Left + 18, r.Top + 76, r.Width - 36, 18), C(Theme.Dim), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}
