namespace WinTweaker;

/// <summary>Что значок в трее и строка поиска могут попросить у главного окна.</summary>
public partial class MainForm
{
    public int PendingCount => busy ? 0 : Pending();
    public int RevertedCount => reverted.Count;
    public IReadOnlyList<Tweak> AllTweaks => tweaks;

    /// <summary>Разделы бокового меню (название и значок).</summary>
    public static IEnumerable<(string Cat, string Glyph)> Pages => AllPages;
    public const string GamesPageName = GamesHub;

    public void OpenPage(string cat)
    {
        ShowFromTray();
        SelectCategory(cat);
    }

    public void OpenTweak(Tweak t)
    {
        ShowFromTray();
        GoTo(t);
    }

    /// <summary>Открыть окно «Изменения к применению».</summary>
    public void ReviewPending()
    {
        ShowFromTray();
        if (!busy && Pending() > 0) _ = ApplyAsync();
    }

    public void FixRevertedFromTray()
    {
        ShowFromTray();
        _ = FixReverted();
    }

    internal void OpenReport(MatchReport r)
    {
        ShowFromTray();
        using var d = new ReportDialog(r);
        d.ShowDialog(this);
    }
}
