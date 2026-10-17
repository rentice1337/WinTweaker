namespace WinTweaker;

/// <summary>Настройки кругового пульта и игрового оверлея.</summary>
public partial class MainForm
{
    static Segmented Seg(string[] options, int value, Action<int> changed)
    {
        var s = new Segmented(options) { Value = value };
        s.Changed += changed;
        return s;
    }

    void BuildWheelCard(Control cv)
    {
        var card = new OptionsCard("Круговой пульт");
        card.AddCustom("Где открывать", "У курсора или по центру экрана",
            Seg(new[] { "У курсора", "По центру" }, WheelSettings.Centered ? 1 : 0, v => WheelSettings.Centered = v == 1));
        card.AddCustom("Игр во втором кольце", "Сколько обложек показывать в «Играх»",
            Seg(new[] { "6", "8", "10" }, WheelSettings.Games switch { 6 => 0, 8 => 1, _ => 2 }, v => WheelSettings.Games = v switch { 0 => 6, 1 => 8, _ => 10 }));
        card.Add("Подсказка под кольцом", "Одна главная подсказка «прямо сейчас»", () => WheelSettings.Hint, on => WheelSettings.Hint = on);
        var hk = new HotkeyBox(WheelSettings.Hotkey);
        hk.Changed += k =>
        {
            WheelSettings.Hotkey = k;
            var (ok, _) = tray?.ApplyHotkeys() ?? (true, true);
            if (!ok) Toast.Pop(this, $"{WheelSettings.Format(k)} уже занято другой программой — выберите другое сочетание", Tone.Warning);
            else Toast.Pop(this, k == Keys.None ? "Горячая клавиша пульта выключена" : $"Пульт: {WheelSettings.Format(k)}", Tone.Info);
        };
        card.AddCustom("Горячая клавиша", "Клик — и нажмите сочетание. Backspace — выключить", hk);
        var sectors = new FlatBtn("", "Настроить…");
        sectors.Click += (_, _) =>
        {
            using var d = new SectorsDialog();
            if (d.ShowDialog(this) == DialogResult.OK) Toast.Pop(this, "Секторы пульта сохранены");
        };
        card.AddCustom("Секторы", "Какие 8 действий на кольце и в каком порядке", sectors);
        var tryIt = new FlatBtn("", "Открыть пульт");
        tryIt.Click += (_, _) => tray?.ToggleWheel(Cursor.Position);
        card.AddCustom("Попробовать", "Изменения применяются при следующем открытии", tryIt);
        cv.Controls.Add(card);
    }

    void BuildOverlayCards(Control cv)
    {
        var card = new OptionsCard("Игровой оверлей");
        void Reload() => Overlay.Reload();
        card.Add("Показывать сам при игре", "Появляется при запуске игры и исчезает после неё", () => OverlaySettings.Auto, on => OverlaySettings.Auto = on);
        card.AddCustom("Вид", "Капсула — компактно, HUD — без фона, Панель — с графиками",
            Seg(OverlaySettings.Styles, OverlaySettings.Style, v => { OverlaySettings.Style = v; Reload(); }));
        var corner = Seg(new[] { "↖", "↗", "↙", "↘" }, Math.Min(3, OverlaySettings.Corner), v => { OverlaySettings.Corner = v; Reload(); });
        card.AddCustom("Угол", "Или перетащите мышью — кнопка ниже", corner);
        var move = new FlatBtn("\uE7C2", Overlay.Editing ? "Готово" : "Переместить");
        move.Click += (_, _) =>
        {
            bool now = !Overlay.Editing;
            Overlay.Edit(now);
            if (now) Toast.Pop(this, "Перетащите оверлей мышью. Двойной клик по нему или «Готово» — сохранить место", Tone.Info);
            else Toast.Pop(this, "Место оверлея сохранено");
        };
        Action sync = () =>
        {
            if (move.IsDisposed) return;
            move.Text = Overlay.Editing ? "Готово" : "Переместить";
            move.Invalidate();
        };
        Overlay.EditingChanged += sync;
        move.Disposed += (_, _) => Overlay.EditingChanged -= sync;

        card.AddCustom("Своё место", "Оверлей можно поставить в любую точку экрана", move);
        var bg = new Slider(0, 100, 10, v => v == 0 ? "без фона" : $"фон {v}%") { Value = OverlaySettings.Background };
        bg.Changed += v => { OverlaySettings.Background = v; Reload(); };
        card.AddCustom("Прозрачность фона", "0% — только цифры", bg);
        card.Add("Предупреждения", "Цифры желтеют и краснеют: FPS просел, пинг скакнул, видеокарта перегрелась",
            () => OverlaySettings.Warnings, on => { OverlaySettings.Warnings = on; Reload(); });
        card.Add("Тихий режим", "Еле виден, пока всё хорошо, и ярко появляется при просадке FPS или скачке пинга",
            () => OverlaySettings.Quiet, on => { OverlaySettings.Quiet = on; Reload(); });
        card.Add("Линия цели FPS", "На графике — частота монитора: видно, дотягивает ли игра до 144/165",
            () => OverlaySettings.TargetLine, on => { OverlaySettings.TargetLine = on; Reload(); });
        var hk = new HotkeyBox(OverlaySettings.Hotkey);
        hk.Changed += k =>
        {
            OverlaySettings.Hotkey = k;
            var (_, ok) = tray?.ApplyHotkeys() ?? (true, true);
            if (!ok) Toast.Pop(this, $"{WheelSettings.Format(k)} уже занято другой программой — выберите другое сочетание", Tone.Warning);
        };
        card.AddCustom("Горячая клавиша", "Показать / скрыть прямо в игре", hk);
        var show = new FlatBtn("", "Показать / скрыть");
        show.Click += (_, _) => Overlay.Toggle();
        card.AddCustom("Проверить", "Виден поверх игр в оконном и «полноэкранном оконном» режиме", show);
        cv.Controls.Add(card);

        var metrics = new OptionsCard("Что показывать в оверлее");
        foreach (var (key, title) in OverlaySettings.Metrics)
        {
            string k = key;
            string sub = key switch
            {
                "fps" => "Через события Windows, без внедрения в игру (DirectX 9–12)",
                "ft" => "Ровный график — плавная картинка, пики — фризы",
                "low" => "Средний FPS худшего 1% кадров — показатель статтеров",
                "gpu" => "NVIDIA — через драйвер, остальные — счётчики Windows",
                "temp" => "Только видеокарты NVIDIA",
                "ping" => "До цели монитора соединения или 1.1.1.1",
                "session" => "Сколько идёт текущая игра",
                _ => ""
            };
            metrics.Add(title, sub, () => OverlaySettings.On(k), on => { OverlaySettings.Set(k, on); Reload(); });
        }
        cv.Controls.Add(metrics);
    }
}
