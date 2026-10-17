namespace WinTweaker;

/// <summary>Настройки кругового пульта.</summary>
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
        var pickGames = new FlatBtn("\uE7FC", "Выбрать…");
        pickGames.Click += (_, _) =>
        {
            using var d = new WheelGamesDialog();
            if (d.ShowDialog(this) == DialogResult.OK)
                Toast.Pop(this, GamePrefs.Wheel == null ? "На кольце — первые игры из «Моих игр»" : $"На кольце: {GamePrefs.Wheel.Count} игр", Tone.Success);
        };
        card.AddCustom("Игры на кольце", "Какие игры и в каком порядке — или автоматически первые из «Моих игр»", pickGames);
        card.Add("Подсказка под кольцом", "Одна главная подсказка «прямо сейчас»", () => WheelSettings.Hint, on => WheelSettings.Hint = on);
        var hk = new HotkeyBox(WheelSettings.Hotkey);
        hk.Changed += k =>
        {
            WheelSettings.Hotkey = k;
            bool ok = tray?.ApplyHotkeys() ?? true;
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
}
