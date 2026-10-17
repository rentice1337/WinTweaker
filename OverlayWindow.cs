using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WinTweaker;

/// <summary>Управление оверлеем: показ/скрытие, автопоказ в игре, перемещение мышью.</summary>
static class Overlay
{
    static OverlayWindow? window;
    static bool autoShown;
    public static bool Visible => window is { IsDisposed: false };
    public static bool Editing => window is { IsDisposed: false, Editing: true };

    public static void Toggle()
    {
        if (Visible) Hide(); else Show();
    }

    public static void Show()
    {
        if (Visible) return;
        window = new OverlayWindow();
        window.FormClosed += (_, _) => window = null;
        window.Show();
    }

    public static void Hide()
    {
        autoShown = false;
        bool wasEditing = Editing;
        window?.Close();
        window = null;
        if (wasEditing) RaiseEditingChanged();
    }

    /// <summary>Пересобрать после смены настроек (если показан).</summary>
    public static void Reload()
    {
        if (!Visible) return;
        bool edit = Editing;
        Hide();
        Show();
        if (edit) window?.SetEditing(true);
    }

    /// <summary>Режим перемещения: оверлей можно взять мышью; двойной клик — готово.</summary>
    public static void Edit(bool on)
    {
        if (on && !Visible) Show();
        if (on) window?.SetEditing(true);
        else window?.Finish();
    }

    /// <summary>Режим перемещения включили или закончили (в том числе двойным кликом по оверлею).</summary>
    public static event Action? EditingChanged;
    internal static void RaiseEditingChanged() => EditingChanged?.Invoke();

    public static void GameStarted()
    {
        if (OverlaySettings.Auto && !Visible) { Show(); autoShown = true; }
    }

    public static void GameEnded()
    {
        if (autoShown && !Editing) Hide();
    }
}

/// <summary>
/// Окно оверлея: поверх всех окон, прозрачное для мыши, без фокуса. Видно поверх игр в оконном и «полноэкранном
/// оконном» режимах (в эксклюзивном полноэкранном Windows не показывает поверх игры другие окна).
/// Три стиля: неоновая капсула, тактический HUD и панель-дашборд. Обновление 10 раз в секунду.
/// </summary>
sealed class OverlayWindow : LayeredForm
{
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFOEX info);
    delegate bool MonitorProc(IntPtr mon, IntPtr dc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorProc proc, IntPtr data);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int Size;
        public RECT Monitor, Work;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20;
    const int M = 10; // поле под свечение вокруг подложки

    readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    readonly int style = OverlaySettings.Style, bgAlpha = (int)(OverlaySettings.Background * 2.4);
    readonly bool warn = OverlaySettings.Warnings, quiet = OverlaySettings.Quiet, targetLine = OverlaySettings.TargetLine;
    readonly Dictionary<string, bool> on = OverlaySettings.Metrics.ToDictionary(m => m.Key, m => OverlaySettings.On(m.Key));
    static readonly Font Huge = new("Segoe UI Semibold", 26f, GraphicsUnit.Pixel), Big = new("Segoe UI Semibold", 22f, GraphicsUnit.Pixel),
        Mid = new("Segoe UI Semibold", 14f, GraphicsUnit.Pixel), Small = new("Segoe UI", 11.5f, GraphicsUnit.Pixel),
        Tiny = new("Segoe UI Semibold", 10f, GraphicsUnit.Pixel);
    static readonly Bitmap measureBmp = new(1, 1);
    static readonly Graphics measure = Graphics.FromImage(measureBmp);
    readonly Queue<double> fpsHist = new(), pingHist = new();
    double alpha = 255;
    int tick, targetHz;
    public bool Editing { get; private set; }

    bool UsesFps => on["fps"] || on["ft"] || on["low"];
    bool UsesGpu => on["gpu"] || on["temp"];

    public OverlayWindow()
    {
        Size = new Size(1, 1);
        timer.Tick += (_, _) => Frame();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TRANSPARENT | 0x08000000 /* NOACTIVATE */;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (UsesFps) FpsMeter.Acquire();
        if (UsesGpu) GpuMeter.Acquire();
        Frame();
        timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Dispose();
        if (UsesFps) FpsMeter.Release();
        if (UsesGpu) GpuMeter.Release();
        base.OnFormClosed(e);
    }

    // ───── положение ─────
    // Единственный источник правды — поля pos и sz. Окно получает их одним вызовом UpdateLayeredWindow вместе с картинкой.
    // Положение окна обратно не читаем (никаких Location/Bounds/Screen из WinForms): из-за пересчётов масштаба экрана
    // прочитанное значение может отличаться от записанного на пару пикселей, и окно ползло бы при каждом обновлении.

    Point pos;
    Size sz;
    bool placed;
    Point grab;
    bool dragging;
    IntPtr lastMon;

    readonly record struct Mon(Rectangle Bounds, string Device);

    static Mon Info(IntPtr h)
    {
        var mi = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
        if (h == IntPtr.Zero || !GetMonitorInfoW(h, ref mi)) return Primary();
        return new Mon(Rectangle.FromLTRB(mi.Monitor.Left, mi.Monitor.Top, mi.Monitor.Right, mi.Monitor.Bottom), mi.Device ?? "");
    }

    static Mon Primary()
    {
        var h = MonitorFromPoint(new POINT(), 1 /* PRIMARY */);
        var mi = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfoW(h, ref mi)
            ? new Mon(Rectangle.FromLTRB(mi.Monitor.Left, mi.Monitor.Top, mi.Monitor.Right, mi.Monitor.Bottom), mi.Device ?? "")
            : new Mon(new Rectangle(0, 0, 1920, 1080), "");
    }

    static Mon At(Point p) => Info(MonitorFromPoint(new POINT { X = p.X, Y = p.Y }, 2 /* NEAREST */));

    static Mon? ByDevice(string device)
    {
        if (device == "") return null;
        Mon? found = null;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            var m = Info(h);
            if (string.Equals(m.Device, device, StringComparison.OrdinalIgnoreCase)) { found = m; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Монитор для оверлея. Своё место — тот монитор, где его поставили (если он подключён). Угол — монитор окна
    /// на переднем плане (игры); если на переднем плане сам WinTweak — тот, где оверлей был, чтобы не прыгать.
    /// </summary>
    Mon TargetMonitor()
    {
        if (OverlaySettings.Corner == 4 && ByDevice(OverlaySettings.Monitor) is { } saved) return saved;
        var fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out uint pid);
        if (fg != IntPtr.Zero && pid != Environment.ProcessId) lastMon = MonitorFromWindow(fg, 2);
        return lastMon != IntPtr.Zero ? Info(lastMon) : Primary();
    }

    /// <summary>Поставить прямоугольник оверлея целиком внутрь монитора.</summary>
    Point Fit(Point p, Rectangle area) => new(
        Math.Clamp(p.X, area.Left, Math.Max(area.Left, area.Right - sz.Width)),
        Math.Clamp(p.Y, area.Top, Math.Max(area.Top, area.Bottom - sz.Height)));

    Point Home(Rectangle b)
    {
        const int pad = 6;
        int corner = OverlaySettings.Corner;
        if (corner == 4)
        {
            var (fx, fy) = OverlaySettings.Place;
            return new Point(b.Left + (int)Math.Round(fx * Math.Max(0, b.Width - sz.Width)),
                             b.Top + (int)Math.Round(fy * Math.Max(0, b.Height - sz.Height)));
        }
        return new Point(corner is 0 or 2 ? b.Left + pad : b.Right - sz.Width - pad,
                         corner is 0 or 1 ? b.Top + pad : b.Bottom - sz.Height - pad);
    }

    // ───── перемещение мышью ─────
    // Тащим сами, без системного перетаскивания за «заголовок»: Windows не прилепляет и не разворачивает окно.

    public void SetEditing(bool value)
    {
        if (Editing == value || IsDisposed) return;
        Editing = value;
        StopDrag();
        int ex = GetWindowLong(Handle, GWL_EXSTYLE);
        SetWindowLong(Handle, GWL_EXSTYLE, value ? ex & ~WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT);
        Cursor = value ? Cursors.SizeAll : Cursors.Default;
        Frame();
        Overlay.RaiseEditingChanged();
    }

    void StopDrag()
    {
        dragging = false;
        if (Capture) Capture = false;
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84, WM_MOUSEACTIVATE = 0x21, WM_SYSCOMMAND = 0x112;
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)3; return; } // MA_NOACTIVATE: клик не забирает фокус у игры
        if (m.Msg == WM_NCHITTEST && Editing) { m.Result = (IntPtr)1; return; } // HTCLIENT — мышь обрабатываем сами
        if (m.Msg == WM_SYSCOMMAND) return; // никаких «развернуть», «свернуть», «переместить» от системы
        base.WndProc(ref m);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Editing || e.Button != MouseButtons.Left) return;
        if (e.Clicks >= 2) { Finish(); return; }
        GetCursorPos(out var c);
        grab = new Point(c.X - pos.X, c.Y - pos.Y);
        dragging = true;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging) return;
        GetCursorPos(out var c);
        var cur = new Point(c.X, c.Y);
        pos = Fit(new Point(cur.X - grab.X, cur.Y - grab.Y), At(cur).Bounds);
        Present(pos, sz);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!dragging) return;
        StopDrag();
        SavePlace();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (dragging && !Capture) { dragging = false; SavePlace(); }
    }

    /// <summary>Закончить перемещение (двойной клик или кнопка «Готово»).</summary>
    public void Finish()
    {
        StopDrag();
        if (Editing) SavePlace();
        SetEditing(false);
    }

    void SavePlace()
    {
        var mon = At(new Point(pos.X + sz.Width / 2, pos.Y + sz.Height / 2));
        var b = mon.Bounds;
        double fx = b.Width > sz.Width ? (double)(pos.X - b.Left) / (b.Width - sz.Width) : 0;
        double fy = b.Height > sz.Height ? (double)(pos.Y - b.Top) / (b.Height - sz.Height) : 0;
        OverlaySettings.Place = (Math.Clamp(fx, 0, 1), Math.Clamp(fy, 0, 1));
        OverlaySettings.Monitor = mon.Device;
        OverlaySettings.Corner = 4;
        Log($"сохранено: {pos} на {mon.Device} {b}, доли {fx:0.###}/{fy:0.###}");
    }

    // ───── журнал для разбора, если оверлей всё-таки окажется не там ─────

    static int logged;
    Point lastLogged = new(int.MinValue, int.MinValue);

    static void Log(string line)
    {
        if (logged++ > 200) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinTweaker");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "overlay.log"), $"{DateTime.Now:HH:mm:ss} {line}\r\n");
        }
        catch { }
    }

    // ───── данные кадра ─────

    enum Sev { Ok, Warn, Bad }

    sealed class Model
    {
        public FpsMeter.Reading? Fps;
        public Snap S = Snap.Empty;
        public int? Ping, Gpu, Temp;
        public bool Loss;
        public TimeSpan? Session;
        public Sev FpsSev, PingSev, TempSev, CpuSev, RamSev;
        public Sev Worst => new[] { FpsSev, PingSev, TempSev, CpuSev, RamSev }.Max();
    }

    static Color Col(Sev s, Color ok) => s switch { Sev.Bad => Theme.Bad, Sev.Warn => Theme.Warn, _ => ok };

    Model Build()
    {
        var m = new Model { S = Sampler.Current, Fps = FpsMeter.Read(), Gpu = GpuMeter.Load, Temp = GpuMeter.Temp };
        if (GameMode.Monitor is { } mon) { m.Ping = mon.Last; m.Loss = mon.Last == null; }
        else m.Ping = m.S.PingMs;
        if (GameWatcher.Current is { } run) m.Session = DateTime.Now - run.Since;

        if (tick % 5 == 0)
        {
            if (m.Fps is { } f) Push(fpsHist, f.Fps, 60);
            if (m.Ping is int p) Push(pingHist, p, 60);
        }
        if (warn)
        {
            if (m.Fps is { } f && fpsHist.Count >= 10)
            {
                double avg = fpsHist.Average();
                m.FpsSev = f.Fps < avg * 0.6 ? Sev.Bad : f.Fps < avg * 0.8 ? Sev.Warn : Sev.Ok;
            }
            double pavg = pingHist.Count > 5 ? pingHist.Average() : m.Ping ?? 0;
            m.PingSev = m.Loss ? Sev.Bad : m.Ping is int pp ? (pp > pavg + 40 || pp > 150 ? Sev.Bad : pp > pavg + 15 || pp > 90 ? Sev.Warn : Sev.Ok) : Sev.Ok;
            m.TempSev = m.Temp is int t ? (t >= 90 ? Sev.Bad : t >= 83 ? Sev.Warn : Sev.Ok) : Sev.Ok;
            m.CpuSev = m.S.Cpu >= 97 ? Sev.Warn : Sev.Ok;
            m.RamSev = m.S.Ram >= 95 ? Sev.Bad : m.S.Ram >= 88 ? Sev.Warn : Sev.Ok;
        }
        return m;
    }

    static void Push(Queue<double> q, double v, int max)
    {
        q.Enqueue(v);
        while (q.Count > max) q.Dequeue();
    }

    /// <summary>Чей FPS: игра из слежения, иначе окно на переднем плане.</summary>
    static int TargetPid()
    {
        if (GameWatcher.Current is { } run) return run.Pid;
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return pid == Environment.ProcessId ? FpsMeter.Pid : (int)pid;
    }

    Model? model;

    void Frame()
    {
        if (IsDisposed) return;
        tick++;
        FpsMeter.Pid = TargetPid();
        model = Build();

        var mon = Editing ? At(new Point(pos.X + sz.Width / 2, pos.Y + sz.Height / 2)) : TargetMonitor();
        if (tick % 50 == 1)
        {
            try { targetHz = Displays.All().FirstOrDefault(d => d.Device == mon.Device)?.Hz ?? 0; } catch { targetHz = 0; }
        }

        // размер под содержимое; ширину не дёргаем на каждую цифру — растём сразу, сжимаемся только заметно
        var want = Measure(model);
        int w = sz.Width;
        if (want.Width > w || want.Width < w - 24 || !placed) w = (want.Width + 3) / 4 * 4;
        sz = new Size(w, want.Height);

        // тихий режим: еле видно, пока всё хорошо; ярко — при просадке или скачке
        double target = Editing || !quiet || model.Worst != Sev.Ok ? 255 : 95;
        alpha += (target - alpha) * (target > alpha ? 0.6 : 0.15);
        Alpha = (byte)Math.Clamp(alpha, 0, 255);

        if (dragging) { } // положение задаёт мышь
        else if (Editing) pos = Fit(pos, mon.Bounds); // вырос — не вылезаем за край
        else pos = Fit(Home(mon.Bounds), mon.Bounds);
        placed = true;

        if (!Editing && (Math.Abs(pos.X - lastLogged.X) > 40 || Math.Abs(pos.Y - lastLogged.Y) > 40))
        {
            Log($"место {pos}, размер {sz}, монитор {mon.Device} {mon.Bounds}, угол {OverlaySettings.Corner}");
            lastLogged = pos;
        }

        // поверх всех — подтверждаем раз в 2 с (игры в оконном режиме любят перекрывать)
        if (tick % 20 == 1) SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 /* NOSIZE */ | 0x0002 /* NOMOVE */ | 0x0010 /* NOACTIVATE */);
        Present(pos, sz);
    }

    // ───── элементы ─────

    List<(string Label, string Value, Sev Sev)> Chips(Model m)
    {
        var list = new List<(string, string, Sev)>();
        if (on["cpu"]) list.Add(("CPU", $"{m.S.Cpu:0}%", m.CpuSev));
        if (on["gpu"]) list.Add(("GPU", m.Gpu is int g ? $"{g}%" : "—", Sev.Ok));
        if (on["temp"] && m.Temp is int t) list.Add(("", $"{t}°", m.TempSev));
        if (on["ping"]) list.Add(("", m.Loss ? "потеря" : m.Ping is int p ? $"{p} мс" : "пинг —", m.PingSev));
        if (on["ram"]) list.Add(("ОЗУ", $"{m.S.Ram:0}%", m.RamSev));
        if (on["session"] && m.Session is TimeSpan s) list.Add(("", Clock(s), Sev.Ok));
        if (on["clock"]) list.Add(("", DateTime.Now.ToString("HH:mm"), Sev.Ok));
        return list;
    }

    static string Clock(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    static float W(string s, Font f) => s == "" ? 0 : measure.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic).Width;

    string FpsText(Model m) => m.Fps is { } f ? $"{f.Fps:0}" : "—";

    Size Measure(Model m)
    {
        switch (style)
        {
            case 0:
            {
                float w = 18;
                if (on["fps"] || on["low"]) w += Math.Max(W(FpsText(m), Huge) + 4, 62) + 16;
                if (on["ft"]) w += 104;
                var (rowW, rows) = ChipRows(Chips(m));
                w += rowW + 18;
                return new Size((int)w + M * 2, 58 + M * 2);
            }
            case 1:
            {
                float w = HudWidth(m);
                return new Size((int)w + 20 + M * 2, (on["ft"] ? 46 : 28) + M * 2);
            }
            default:
            {
                int h = 14;
                if (on["fps"] || on["low"]) h += 36;
                if (on["ft"]) h += 60;
                int bars = (on["cpu"] ? 1 : 0) + (on["gpu"] || on["temp"] ? 1 : 0) + (on["ram"] ? 1 : 0);
                h += bars * 20;
                if (on["ping"]) h += 26;
                if (on["session"] || on["clock"]) h += 18;
                return new Size(262 + M * 2, h + 10 + M * 2);
            }
        }
    }

    static (float Width, List<List<(string Label, string Value, Sev Sev)>> Rows) ChipRows(List<(string Label, string Value, Sev Sev)> chips)
    {
        var rows = new List<List<(string, string, Sev)>> { new(), new() };
        int half = (chips.Count + 1) / 2;
        for (int i = 0; i < chips.Count; i++) rows[i < half ? 0 : 1].Add(chips[i]);
        float ChipW((string Label, string Value, Sev) c) => 14 + W(c.Label, Small) + (c.Label != "" ? 4 : 0) + W(c.Value, Mid) + 14;
        float w = rows.Max(r => r.Sum(ChipW));
        return (w, rows);
    }

    float HudWidth(Model m) => HudItems(m).Sum(i => W(i.Text, Mid) + 22);

    List<(string Text, Color Color)> HudItems(Model m)
    {
        var list = new List<(string, Color)>();
        if (on["fps"]) list.Add(($"{FpsText(m)} FPS", Col(m.FpsSev, Color.White)));
        if (on["ft"] && m.Fps is { } a) list.Add(($"{a.FrameMs:0.0} мс", Color.White));
        if (on["low"] && m.Fps is { } b) list.Add(($"1% {b.Low1:0}", Color.FromArgb(200, 205, 215)));
        foreach (var (label, value, sev) in Chips(m)) list.Add(((label != "" ? label + " " : "") + value, Col(sev, Color.White)));
        return list;
    }

    // ───── рисование ─────

    protected override void Draw(Graphics g)
    {
        var m = model ?? Build();
        g.TranslateTransform(M, M);
        var rect = new RectangleF(0.5f, 0.5f, sz.Width - M * 2 - 1, sz.Height - M * 2 - 1);
        if (Editing)
        {
            // полностью прозрачные пиксели пропускают мышь насквозь — даём, за что взяться
            using var grip = Theme.Round(RectangleF.Inflate(rect, 4, 4), 12);
            using var fill = new SolidBrush(Color.FromArgb(70, Theme.Accent));
            g.FillPath(fill, grip);
        }
        switch (style)
        {
            case 0: Capsule(g, m, rect); break;
            case 1: Hud(g, m, rect); break;
            default: Panel(g, m, rect); break;
        }
        if (Editing)
        {
            using var pen = new Pen(Theme.Accent, 1.5f) { DashStyle = DashStyle.Dash };
            using var path = Theme.Round(RectangleF.Inflate(rect, 4, 4), 12);
            g.DrawPath(pen, path);
            Txt(g, "тащите мышью · двойной клик — готово", Tiny, Theme.AccentHi, new RectangleF(rect.X, rect.Bottom - 2, rect.Width, 12), StringAlignment.Center);
        }
    }

    void Plate(Graphics g, RectangleF r, float rad, bool glow)
    {
        if (bgAlpha <= 0) return;
        if (glow) Theme.Glow(g, Theme.Accent, r, rad, 3, 2.5f, Math.Min(60, bgAlpha / 3));
        using var path = Theme.Round(r, rad);
        using (var b = new LinearGradientBrush(r, Color.FromArgb(bgAlpha, 20, 21, 30), Color.FromArgb(bgAlpha, 9, 10, 15), 90f)) g.FillPath(b, path);
        using var pen = new Pen(Color.FromArgb(Math.Min(255, bgAlpha / 4 + 24), 255, 255, 255));
        g.DrawPath(pen, path);
    }

    /// <summary>График времени кадра и пунктир цели (частота монитора).</summary>
    void FrameGraph(Graphics g, RectangleF area, FpsMeter.Reading? fps, float width, bool label)
    {
        if (fps is not { Recent.Length: > 2 } f)
        {
            Txt(g, FpsMeter.Failed ? FpsMeter.Error : "нет кадров DirectX", Tiny, Theme.Muted, area, StringAlignment.Near);
            return;
        }
        double targetMs = targetHz > 0 ? 1000.0 / targetHz : 0;
        double max = Math.Max(Math.Max(20, targetMs * 2.2), f.Recent.Max() * 1.1);
        var pts = new PointF[f.Recent.Length];
        for (int i = 0; i < f.Recent.Length; i++)
            pts[i] = new PointF(area.X + area.Width * i / (f.Recent.Length - 1), area.Bottom - (float)(f.Recent[i] / max * area.Height));
        using (var path = new GraphicsPath())
        {
            path.AddLines(pts);
            path.AddLine(pts[^1], new PointF(pts[^1].X, area.Bottom));
            path.AddLine(new PointF(pts[^1].X, area.Bottom), new PointF(pts[0].X, area.Bottom));
            using var fill = new LinearGradientBrush(new RectangleF(area.X, area.Y - 1, area.Width, area.Height + 2), Color.FromArgb(70, Theme.Accent), Color.FromArgb(0, Theme.Accent), 90f);
            g.FillPath(fill, path);
        }
        using (var pen = new Pen(Theme.AccentHi, width) { LineJoin = LineJoin.Round }) g.DrawLines(pen, pts);
        if (targetLine && targetMs > 0)
        {
            float ty = area.Bottom - (float)(targetMs / max * area.Height);
            using var dash = new Pen(Color.FromArgb(150, Theme.Good), 1f) { DashStyle = DashStyle.Dash };
            g.DrawLine(dash, area.X, ty, area.Right, ty);
            if (label) Txt(g, targetHz.ToString(), Tiny, Theme.Good, new RectangleF(area.Right - 30, ty - 13, 30, 12), StringAlignment.Far);
        }
    }

    void Capsule(Graphics g, Model m, RectangleF r)
    {
        Plate(g, r, r.Height / 2, glow: m.Worst == Sev.Ok);
        float x = r.X + 18;
        if (on["fps"] || on["low"])
        {
            string fps = FpsText(m);
            var col = Col(m.FpsSev, targetHz > 0 && m.Fps is { } ff && ff.Fps >= targetHz * 0.95 ? Theme.Good : Color.White);
            if (on["fps"]) Txt(g, fps, Huge, col, new RectangleF(x - 2, r.Y + 4, 120, 32), StringAlignment.Near);
            string sub = "FPS" + (on["low"] && m.Fps is { } f ? $" · 1% {f.Low1:0}" : "");
            Txt(g, sub, Tiny, Theme.Muted, new RectangleF(x, r.Y + 35, 120, 14), StringAlignment.Near);
            x += Math.Max(W(fps, Huge) + 4, 62) + 16;
        }
        if (on["ft"])
        {
            FrameGraph(g, new RectangleF(x, r.Y + 13, 90, 30), m.Fps, 1.3f, false);
            x += 104;
        }
        var (_, rows) = ChipRows(Chips(m));
        for (int ri = 0; ri < rows.Count; ri++)
        {
            float cx = x, cy = r.Y + 8 + ri * 21;
            foreach (var (label, value, sev) in rows[ri])
            {
                var dot = Col(sev, Theme.Accent);
                using (var b = new SolidBrush(dot)) g.FillEllipse(b, cx, cy + 6, 6, 6);
                cx += 12;
                if (label != "")
                {
                    Txt(g, label, Small, Theme.Muted, new RectangleF(cx, cy, 60, 18), StringAlignment.Near);
                    cx += W(label, Small) + 4;
                }
                Txt(g, value, Mid, Col(sev, Color.White), new RectangleF(cx, cy - 1, 90, 19), StringAlignment.Near);
                cx += W(value, Mid) + 16;
            }
        }
    }

    void Hud(Graphics g, Model m, RectangleF r)
    {
        if (bgAlpha > 0)
        {
            using var path = Theme.Round(r, 8);
            using var b = new SolidBrush(Color.FromArgb(bgAlpha / 2, 6, 7, 10));
            g.FillPath(b, path);
        }
        float x = r.X + 10, y = r.Y + 4;
        var items = HudItems(m);
        for (int i = 0; i < items.Count; i++)
        {
            var (text, col) = items[i];
            Shadow(g, text, Mid, col, new RectangleF(x, y, W(text, Mid) + 10, 20));
            x += W(text, Mid) + 10;
            if (i < items.Count - 1)
            {
                using var pen = new Pen(Color.FromArgb(110, 255, 255, 255));
                g.DrawLine(pen, x + 1, y + 4, x + 1, y + 16);
                x += 12;
            }
        }
        if (on["ft"]) FrameGraph(g, new RectangleF(r.X + 10, y + 24, r.Width - 20, 16), m.Fps, 1.2f, false);
    }

    static void Shadow(Graphics g, string s, Font f, Color c, RectangleF r)
    {
        Txt(g, s, f, Color.FromArgb(170, 0, 0, 0), new RectangleF(r.X + 1, r.Y + 1, r.Width, r.Height), StringAlignment.Near);
        Txt(g, s, f, c, r, StringAlignment.Near);
    }

    void Panel(Graphics g, Model m, RectangleF r)
    {
        Plate(g, r, 14, glow: m.Worst == Sev.Ok);
        using (var top = new SolidBrush(Color.FromArgb(220, Theme.Accent))) g.FillRectangle(top, r.X + 18, r.Y + 0.5f, 34, 2);
        float pad = 14, y = r.Y + 10, w = r.Width - pad * 2, x = r.X + pad;
        if (on["fps"] || on["low"])
        {
            if (on["fps"])
            {
                string fps = FpsText(m);
                Txt(g, fps, Big, Col(m.FpsSev, Color.White), new RectangleF(x - 1, y - 2, 120, 30), StringAlignment.Near);
                Txt(g, "FPS", Tiny, Theme.Muted, new RectangleF(x + W(fps, Big) + 6, y + 11, 40, 14), StringAlignment.Near);
            }
            var right = new List<string>();
            if (on["low"] && m.Fps is { } lo) right.Add($"1% low {lo.Low1:0}");
            if (targetHz > 0) right.Add($"цель {targetHz}");
            Txt(g, string.Join("  ·  ", right), Small, Theme.Muted, new RectangleF(x, y + 8, w, 16), StringAlignment.Far);
            y += 36;
        }
        if (on["ft"])
        {
            var area = new RectangleF(x, y + 4, w, 44);
            using (var track = new SolidBrush(Color.FromArgb(14, 255, 255, 255))) g.FillRectangle(track, area);
            FrameGraph(g, area, m.Fps, 1.4f, true);
            if (m.Fps is { } f) Txt(g, $"{f.FrameMs:0.0} мс", Tiny, Color.White, new RectangleF(area.X + 4, area.Y + 2, 80, 12), StringAlignment.Near);
            y += 60;
        }
        void Bar(string label, double value, string text, Sev sev)
        {
            Txt(g, label, Small, Theme.Muted, new RectangleF(x, y, 40, 16), StringAlignment.Near);
            var track = new RectangleF(x + 40, y + 6, w - 40 - 70, 5);
            Theme.Fill(g, Color.FromArgb(26, 255, 255, 255), track, 2.5f);
            Theme.Fill(g, Col(sev, Theme.Accent), new RectangleF(track.X, track.Y, (float)(track.Width * Math.Clamp(value / 100, 0.02, 1)), 5), 2.5f);
            Txt(g, text, Small, Col(sev, Color.White), new RectangleF(x + w - 68, y, 68, 16), StringAlignment.Far);
            y += 20;
        }
        if (on["cpu"]) Bar("CPU", m.S.Cpu, $"{m.S.Cpu:0}%", m.CpuSev);
        if (on["gpu"] || on["temp"])
        {
            string t = (on["gpu"] && m.Gpu is int gl ? $"{gl}%" : "") + (on["temp"] && m.Temp is int tc ? $" {tc}°" : "");
            Bar("GPU", m.Gpu ?? 0, t.Trim() == "" ? "—" : t.Trim(), m.TempSev);
        }
        if (on["ram"]) Bar("ОЗУ", m.S.Ram, $"{m.S.Ram:0}%", m.RamSev);
        if (on["ping"])
        {
            var area = new RectangleF(x + 40, y + 2, w - 40 - 70, 16);
            Txt(g, "Пинг", Small, Theme.Muted, new RectangleF(x, y + 2, 40, 16), StringAlignment.Near);
            var v = pingHist.ToArray();
            if (v.Length >= 2)
            {
                double max = Math.Max(30, v.Max() * 1.2);
                var pts = v.Select((p, i) => new PointF(area.X + area.Width * i / (v.Length - 1), area.Bottom - (float)(p / max * area.Height))).ToArray();
                using var pen = new Pen(Col(m.PingSev, Color.FromArgb(120, 170, 255)), 1.2f) { LineJoin = LineJoin.Round };
                g.DrawLines(pen, pts);
            }
            double jit = v.Length > 2 ? GameNet.Jitter(v) : 0;
            string pt = m.Loss ? "потеря" : m.Ping is int p ? $"{p} ±{jit:0}" : "—";
            Txt(g, pt, Small, Col(m.PingSev, Color.White), new RectangleF(x + w - 68, y + 2, 68, 16), StringAlignment.Far);
            y += 26;
        }
        if (on["session"] || on["clock"])
        {
            string left = on["session"] && m.Session is TimeSpan s ? "в игре " + Clock(s) : "";
            string right = on["clock"] ? DateTime.Now.ToString("HH:mm") : "";
            Txt(g, left, Tiny, Theme.Muted, new RectangleF(x, y, w, 14), StringAlignment.Near);
            Txt(g, right, Tiny, Theme.Muted, new RectangleF(x, y, w, 14), StringAlignment.Far);
        }
    }
}
