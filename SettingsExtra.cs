namespace WinTweaker;

/// <summary>Настройки строки поиска.</summary>
public partial class MainForm
{
    static Segmented Seg(string[] options, int value, Action<int> changed)
    {
        var s = new Segmented(options) { Value = value };
        s.Changed += changed;
        return s;
    }

    void BuildSearchCard(Control cv)
    {
        var card = new OptionsCard("Строка поиска");
        var hk = new HotkeyBox(SearchHotkey.Hotkey);
        hk.Changed += k =>
        {
            SearchHotkey.Hotkey = k;
            bool ok = tray?.ApplyHotkeys() ?? true;
            if (!ok) Toast.Pop(this, $"{SearchHotkey.Format(k)} уже занято другой программой — выберите другое сочетание", Tone.Warning);
            else Toast.Pop(this, k == Keys.None ? "Горячая клавиша поиска выключена" : $"Поиск: {SearchHotkey.Format(k)}", Tone.Info);
        };
        card.AddCustom("Горячая клавиша", "Открывает строку поиска из любого места. Клик — и нажмите сочетание, Backspace — выключить", hk);
        cv.Controls.Add(card);
    }
}
