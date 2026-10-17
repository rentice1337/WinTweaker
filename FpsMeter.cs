using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinTweaker;

/// <summary>
/// Счётчик кадров без внедрения в игру — как PresentMon: своя сессия трассировки Windows (ETW) в реальном
/// времени ловит события показа кадров от провайдеров Microsoft-Windows-DXGI (Present_Start, DirectX 10–12)
/// и Microsoft-Windows-D3D9 (Present_Start, DirectX 9). В процесс игры ничего не загружается.
/// Работает напрямую через advapi32 (StartTrace / EnableTraceEx2 / OpenTrace / ProcessTrace), без сторонних библиотек.
/// Игры на Vulkan/OpenGL без DXGI эти события не пишут.
/// </summary>
static class FpsMeter
{
    // ───── ETW ─────
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint StartTraceW(out ulong handle, string name, IntPtr props);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint ControlTraceW(ulong handle, string? name, IntPtr props, uint code);
    [DllImport("advapi32.dll")]
    static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level, ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ulong OpenTraceW(IntPtr logfile);
    [DllImport("advapi32.dll")] static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] static extern uint CloseTrace(ulong handle);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void RecordCallback(IntPtr record);

    const string SessionName = "WinTweak-FPS";
    const int PropsSize = 120, NameBytes = 1024;
    static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    static readonly RecordCallback callback = OnRecord; // держим ссылку — иначе сборщик мусора уберёт делегат
    static readonly double qpcToMs = 1000.0 / Stopwatch.Frequency;

    static ulong session, trace;
    static Thread? thread;
    static readonly object gate = new();
    static readonly List<double> times = new();   // отметки показа кадров (мс), последние ~12 с
    static volatile int pid;
    static int users;
    static double sumFt, sumCount;
    static readonly int[] hist = new int[2001];      // время кадра за сессию, корзины по 0,1 мс (до 200 мс)

    public static bool Failed { get; private set; }
    public static string Error { get; private set; } = "";

    /// <summary>Чей FPS считать.</summary>
    public static int Pid
    {
        get => pid;
        set
        {
            if (pid == value) return;
            lock (gate) { pid = value; times.Clear(); }
        }
    }

    /// <summary>Начать (счётчик ссылок: оверлей и статистика могут работать одновременно).</summary>
    public static void Acquire()
    {
        if (Interlocked.Increment(ref users) > 1 && thread != null) return;
        if (thread != null) return;
        Failed = false;
        Error = "";
        thread = new Thread(Run) { IsBackground = true, Name = "WinTweak FPS" };
        thread.Start();
    }

    public static void Release()
    {
        if (Interlocked.Decrement(ref users) > 0) return;
        users = 0;
        Stop();
        lock (gate) times.Clear();
    }

    static IntPtr Props()
    {
        var p = Marshal.AllocHGlobal(PropsSize + NameBytes);
        for (int i = 0; i < PropsSize + NameBytes; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, PropsSize + NameBytes);  // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);                     // Wnode.ClientContext: время — QueryPerformanceCounter
        Marshal.WriteInt32(p, 44, 0x00020000);            // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 48, 64);                    // BufferSize, КБ
        Marshal.WriteInt32(p, 52, 4);                     // MinimumBuffers
        Marshal.WriteInt32(p, 56, 32);                    // MaximumBuffers
        Marshal.WriteInt32(p, 64, 0x00000100);            // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
        Marshal.WriteInt32(p, 68, 1);                     // FlushTimer, с
        Marshal.WriteInt32(p, 116, PropsSize);            // LoggerNameOffset
        return p;
    }

    static void Stop()
    {
        var p = Props();
        try { ControlTraceW(0, SessionName, p, 1 /* EVENT_TRACE_CONTROL_STOP */); }
        finally { Marshal.FreeHGlobal(p); }
        if (trace != 0) { CloseTrace(trace); trace = 0; }
        session = 0;
        thread = null;
    }

    static void Fail(string what, uint code)
    {
        Failed = true;
        Error = code == 5 ? (Elevation.IsAdmin ? "Windows запретила трассировку (код 5)" : "нет прав администратора") : $"{what} (код {code})";
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinTweaker");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "fps.log"), $"{DateTime.Now:s} {what}: {code}\r\n");
        }
        catch { }
    }

    static void Run()
    {
        // сессия могла остаться от прошлого запуска (сбой) — останавливаем её
        var p = Props();
        try
        {
            ControlTraceW(0, SessionName, p, 1);
            Marshal.FreeHGlobal(p);
            p = Props();
            uint r = StartTraceW(out session, SessionName, p);
            if (r != 0) { Fail("StartTrace", r); return; }
        }
        finally { Marshal.FreeHGlobal(p); }

        var g1 = Dxgi;
        var g2 = D3D9;
        uint e1 = EnableTraceEx2(session, ref g1, 1 /* ENABLE */, 5 /* VERBOSE */, ulong.MaxValue, 0, 0, IntPtr.Zero);
        uint e2 = EnableTraceEx2(session, ref g2, 1, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);
        if (e1 != 0 && e2 != 0) { Fail("EnableTraceEx2", e1); Stop(); return; }

        // EVENT_TRACE_LOGFILEW (x64, 448 байт): LoggerName @8, ProcessTraceMode @28, EventRecordCallback @424
        var log = Marshal.AllocHGlobal(448);
        var name = Marshal.StringToHGlobalUni(SessionName);
        try
        {
            for (int i = 0; i < 448; i++) Marshal.WriteByte(log, i, 0);
            Marshal.WriteIntPtr(log, 8, name);
            Marshal.WriteInt32(log, 28, 0x00000100 | 0x10000000); // REAL_TIME | EVENT_RECORD
            Marshal.WriteIntPtr(log, 424, Marshal.GetFunctionPointerForDelegate(callback));
            trace = OpenTraceW(log);
            if (trace == ulong.MaxValue || trace == 0) { Fail("OpenTrace", (uint)Marshal.GetLastWin32Error()); trace = 0; Stop(); return; }
            uint r = ProcessTrace(new[] { trace }, 1, IntPtr.Zero, IntPtr.Zero); // блокирует до остановки сессии
            if (r != 0 && r != 1223 /* отменено */ && users > 0) Fail("ProcessTrace", r);
        }
        catch (Exception ex) { Failed = true; Error = ex.Message; }
        finally
        {
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(log);
        }
    }

    /// <summary>EVENT_RECORD: ProcessId @12, TimeStamp @16, ProviderId @24, EventDescriptor.Id @40.</summary>
    static void OnRecord(IntPtr rec)
    {
        try
        {
            int p = Marshal.ReadInt32(rec, 12);
            if (p != pid || p == 0) return;
            int id = (ushort)Marshal.ReadInt16(rec, 40);
            if (id != 42 && id != 1) return;
            var provider = Marshal.PtrToStructure<Guid>(rec + 24);
            if (!((provider == Dxgi && id == 42) || (provider == D3D9 && id == 1))) return;
            double t = Marshal.ReadInt64(rec, 16) * qpcToMs;
            lock (gate)
            {
                if (times.Count > 0)
                {
                    double ft = t - times[^1];
                    if (ft > 0 && ft < 1000)
                    {
                        sumFt += ft; sumCount++;
                        hist[Math.Min(2000, (int)(ft * 10))]++;
                    }
                }
                times.Add(t);
                int cut = 0;
                while (cut < times.Count && t - times[cut] > 12000) cut++;
                if (cut > 0) times.RemoveRange(0, cut);
            }
        }
        catch { }
    }

    public readonly record struct Reading(double Fps, double FrameMs, double Low1, double[] Recent);

    /// <summary>FPS за последнюю секунду, среднее время кадра, 1% low за ~10 с и последние времена кадров для графика.</summary>
    public static Reading? Read()
    {
        double[] t;
        lock (gate) t = times.ToArray();
        if (t.Length < 3) return null;
        // кадров давно не было (игра свернута) — не показываем старое
        double nowMs = Stopwatch.GetTimestamp() * qpcToMs;
        if (nowMs - t[^1] > 2000) return null;
        double last = t[^1];
        int n1 = t.Count(x => last - x <= 1000);
        var ft = new double[t.Length - 1];
        for (int i = 1; i < t.Length; i++) ft[i - 1] = t[i] - t[i - 1];
        var lastSec = ft.Skip(Math.Max(0, ft.Length - n1)).ToArray();
        double avg = lastSec.Length > 0 ? lastSec.Average() : ft.Average();
        var sorted = ft.OrderByDescending(x => x).ToArray();
        int k = Math.Max(1, sorted.Length / 100);
        double low = 1000 / sorted.Take(k).Average();
        var recent = ft.Skip(Math.Max(0, ft.Length - 240)).ToArray();
        return new Reading(1000 / avg, avg, low, recent);
    }

    /// <summary>Средний FPS с момента последнего сброса (для игровой статистики).</summary>
    public static double? SessionAverage(bool reset) => SessionStats(reset).Avg;

    /// <summary>Средний FPS и 1% low за сессию (с момента сброса).</summary>
    public static (double? Avg, double? Low1) SessionStats(bool reset)
    {
        lock (gate)
        {
            (double?, double?) r = (null, null);
            if (sumCount > 30)
            {
                // 1% худших кадров: идём по гистограмме с самых долгих
                long need = Math.Max(1, (long)(sumCount / 100)), got = 0;
                double sum = 0;
                for (int i = hist.Length - 1; i >= 0 && got < need; i--)
                {
                    long take = Math.Min(hist[i], need - got);
                    sum += take * (i + 0.5) / 10.0;
                    got += take;
                }
                r = (1000 / (sumFt / sumCount), got > 0 ? 1000 / (sum / got) : null);
            }
            if (reset) { sumFt = 0; sumCount = 0; Array.Clear(hist); }
            return r;
        }
    }
}
