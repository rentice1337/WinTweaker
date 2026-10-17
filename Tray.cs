using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace WinTweaker;

/// <summary>
/// Значок в трее: в нём живой % занятой памяти (цвет — по нагрузке), подсказка с CPU/ОЗУ.
/// Левый клик — стеклянная панель, двойной — открыть окно.
/// </summary>
sealed class Tray : IDisposable
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    readonly NotifyIcon icon = new() { Text = "WinTweak", Visible = true };
    readonly MainForm main;
    TrayPanel? panel;
    IntPtr lastHandle;
    int lastValue = -1;
    Color lastAccent;
    DateTime panelClosed;

    public Tray(MainForm main)
    {
        this.main = main;
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) TogglePanel();
            if (e.Button == MouseButtons.Right) TogglePanel();
        };
        icon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) main.ShowFromTray(); };
        Sampler.Updated += Update;
        Update();
    }

    public void Balloon(string title, string text) => icon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info);

    void TogglePanel()
    {
        if (panel is { IsDisposed: false, Visible: true }) { panel.CloseAnimated(); return; }
        // клик по значку при открытой панели сначала закрывает её (Deactivate) — не открываем заново сразу же
        if ((DateTime.Now - panelClosed).TotalMilliseconds < 250) return;
        panel = new TrayPanel(main);
        panel.FormClosed += (_, _) => panelClosed = DateTime.Now;
        panel.Show();
        panel.Activate();
    }

    void Update()
    {
        int v = (int)Math.Round(Sampler.Ram);
        icon.Text = $"WinTweak — ОЗУ {v}% · CPU {Sampler.Cpu:0}%";
        if (v == lastValue && lastAccent == Theme.Accent) return;
        lastValue = v;
        lastAccent = Theme.Accent;
        var h = Draw(v);
        icon.Icon = Icon.FromHandle(h);
        if (lastHandle != IntPtr.Zero) DestroyIcon(lastHandle);
        lastHandle = h;
    }

    /// <summary>Плитка цвета нагрузки с числом: до 70% — акцент, до 85% — жёлтый, выше — красный.</summary>
    static IntPtr Draw(int value)
    {
        int s = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            var col = value >= 85 ? Theme.Bad : value >= 70 ? Theme.Warn : Theme.Accent;
            var r = new RectangleF(0, 0, s - 0.5f, s - 0.5f);
            using (var path = Theme.Round(r, s * 0.24f))
            using (var b = new LinearGradientBrush(r, Theme.Lerp(col, Color.White, 0.15), Theme.Lerp(col, Color.Black, 0.2), 90f))
                g.FillPath(b, path);
            // уровень «налит» снизу светлой полосой
            float lvl = s * Math.Clamp(value, 0, 100) / 100f;
            using (var clip = Theme.Round(r, s * 0.24f))
            {
                g.SetClip(clip);
                using var fill = new SolidBrush(Color.FromArgb(55, 255, 255, 255));
                g.FillRectangle(fill, 0, s - lvl, s, lvl);
                g.ResetClip();
            }
            string text = value >= 100 ? "99" : value.ToString();
            using var font = new Font("Segoe UI", s * (text.Length > 1 ? 0.5f : 0.6f), FontStyle.Bold, GraphicsUnit.Pixel);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 0.5f, s, s), sf);
        }
        return bmp.GetHicon();
    }

    public void Dispose()
    {
        Sampler.Updated -= Update;
        panel?.Close();
        icon.Visible = false;
        icon.Dispose();
        if (lastHandle != IntPtr.Zero) DestroyIcon(lastHandle);
    }
}

/// <summary>Стеклянная панель над треем: живые CPU/ОЗУ/сеть, виджеты, «Открыть» и «Выход».</summary>
sealed class TrayPanel : RoundForm
{
    readonly MainForm main;
    readonly Toggle widgets = new();
    readonly FlatBtn open = new("", "Открыть");
    readonly FlatBtn exit = new("", "Выход") { Danger = true };
    bool closing;
    int baseTop;
    const int W = 320, H = 300;

    public TrayPanel(MainForm main)
    {
        this.main = main;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Opacity = 0;
        Size = new Size(W, H);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        Location = new Point(area.Right - W - 12, area.Bottom - H - 12);

        widgets.Checked = Widgets.Master;
        widgets.Location = new Point(W - widgets.Width - 18, 196);
        widgets.CheckedChanged += (_, _) => Widgets.Master = widgets.Checked;
        Controls.Add(widgets);

        open.Location = new Point(16, H - 50);
        open.Click += (_, _) => { CloseAnimated(); main.ShowFromTray(); };
        Controls.Add(open);
        exit.Location = new Point(W - exit.Width - 16, H - 50);
        exit.Click += (_, _) => main.ExitApp();
        Controls.Add(exit);

        Deactivate += (_, _) => CloseAnimated();
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) CloseAnimated(); };
        Sampler.Updated += Repaint;
        FormClosed += (_, _) => Sampler.Updated -= Repaint;
    }

    void Repaint() { if (!IsDisposed) Invalidate(false); }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        baseTop = Top;
        Motion.Run(220, p => { if (!IsDisposed) { Opacity = p; Top = baseTop + (int)Math.Round(14 * (1 - p)); } });
    }

    public void CloseAnimated()
    {
        if (closing || IsDisposed) return;
        closing = true;
        Motion.Run(130, p => { if (!IsDisposed) Opacity = 1 - p; }, Motion.InOut, 0, () => { if (!IsDisposed) Close(); });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Logo.Tile(g, new RectangleF(18, 18, 30, 30));
        TextRenderer.DrawText(g, "WinTweak", Theme.F(11.5f, FontStyle.Bold), new Point(56, 16), Theme.Text);
        TextRenderer.DrawText(g, $"работает {SysInfo.Uptime()}", Theme.Tiny, new Point(57, 36), Theme.Muted);

        Meter(g, 18, 66, "Процессор", Sampler.Cpu, $"{Sampler.Cpu:0}%", Sampler.CpuHist, Theme.Accent);
        Meter(g, 18, 118, "Память", Sampler.Ram, $"{Sampler.Ram:0}% · {Sampler.RamUsedGb:0.0} ГБ", Sampler.RamHist, Color.FromArgb(120, 170, 255));

        TextRenderer.DrawText(g, $"↓ {Sampler.Speed(Sampler.DownBps)}   ↑ {Sampler.Speed(Sampler.UpBps)}", Theme.Tiny, new Point(20, 168), Theme.Muted);
        if (Sampler.PingMs is int p)
        {
            var s = $"пинг {p} мс";
            var w = TextRenderer.MeasureText(s, Theme.Tiny).Width;
            TextRenderer.DrawText(g, s, Theme.Tiny, new Point(W - 20 - w, 168), p < 40 ? Theme.Good : p < 100 ? Theme.Warn : Theme.Bad);
        }

        using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 16, 188, W - 16, 188);
        TextRenderer.DrawText(g, "Виджеты на рабочем столе", Theme.Semi, new Point(18, 198), Theme.Text);
        TextRenderer.DrawText(g, "Какие именно — в «Настройках»", Theme.Tiny, new Point(19, 218), Theme.Muted);
        using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 16, H - 62, W - 16, H - 62);
    }

    void Meter(Graphics g, int x, int y, string label, double v, string text, double[] hist, Color c)
    {
        TextRenderer.DrawText(g, label, Theme.Tiny, new Point(x, y), Theme.Muted);
        var tw = TextRenderer.MeasureText(text, Theme.Semi).Width;
        TextRenderer.DrawText(g, text, Theme.Semi, new Point(W - 18 - tw, y - 3), Theme.Text);
        var area = new RectangleF(x, y + 18, W - x * 2, 26);
        var pts = hist.ToArray();
        if (pts.Length < 2) return;
        float step = area.Width / (Sampler.History - 1), x0 = area.Right - (pts.Length - 1) * step;
        var line = pts.Select((val, i) => new PointF(x0 + i * step, area.Bottom - (float)(Math.Max(4, val) / 100 * area.Height))).ToArray();
        using (var path = new GraphicsPath())
        {
            path.AddLines(line);
            path.AddLine(line[^1], new PointF(line[^1].X, area.Bottom));
            path.AddLine(new PointF(line[^1].X, area.Bottom), new PointF(line[0].X, area.Bottom));
            using var fill = new LinearGradientBrush(new RectangleF(area.X, area.Y - 1, area.Width, area.Height + 2), Color.FromArgb(80, c), Color.FromArgb(0, c), 90f);
            g.FillPath(fill, path);
        }
        using var pen = new Pen(c, 1.6f) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, line);
    }
}

/// <summary>Автозапуск вместе с Windows: задание планировщика (без запроса UAC при каждом входе).</summary>
static class Autostart
{
    public const string Task = @"WinTweaker\Autostart";

    public static bool Installed() => Shell.RunCode("schtasks.exe", $"/Query /TN \"{Task}\"") == 0;

    public static void Install()
    {
        string exe = Environment.ProcessPath ?? Application.ExecutablePath;
        int code = Shell.RunCode("schtasks.exe", $"/Create /F /TN \"{Task}\" /SC ONLOGON /RL HIGHEST /DELAY 0000:20 /TR \"\\\"{exe}\\\" --tray\"");
        if (code != 0) throw new InvalidOperationException("Не удалось создать задание автозапуска (код " + code + ").");
    }

    public static void Uninstall() => Shell.RunCode("schtasks.exe", $"/Delete /F /TN \"{Task}\"");
}
