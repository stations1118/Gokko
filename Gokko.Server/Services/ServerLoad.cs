using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Gokko.Server.Services;

/// <summary>
/// サーバーの負荷を測る（管理画面の「サーバーの負荷」と logs/load-yyyyMMdd.log）。
///
/// 5秒ごとに、このプロセスの CPU（全コアで 100%）・メモリ、マシン全体のメモリとロードアベレージ（Linux だけ）、
/// 配信1回（進路の処理と全員への状態の配信）にかかった時間、参加者とにぎやかしの列車の数を測る。
/// 配信は、そのうち進路の処理（Tick）と送り終わるのを待った時間も分けて測る。画面の API（HTTP）とハブの呼び出し（SignalR）は、
/// 種類ごとの回数と時間を数える（どこが重いかを見るため。直近1分の多い順を画面に、1分ごとに上位12（配信の内訳の5つが入っても、画面の API が押し出されないように）をファイルに）。
/// 直近1時間ぶんを画面用に持ち、1分ごとにまとめた1行（平均と最大）をファイルに書く。1行 300 バイトほどで、1日で 400KB くらい。
/// </summary>
public sealed class ServerLoad(PlayerRegistry players, ILogger<ServerLoad>? logger = null) : BackgroundService
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
    private const int HistoryCount = 720; // 5秒 × 720 = 1時間
    private const int WorkHistoryCount = 12; // 5秒 × 12 = 1分
    private const long MaxBytesPerDay = 10L * 1024 * 1024;

    /// <summary>
    /// 1分の平均がこれを超えたら、サーバーの記録（journal）に警告を出す（ふだんは配信 10〜20ms・進路の処理 3〜6ms）。
    /// 負荷の記録（logs/load-*.log）を見に行かないと、重くなっていることに気づけなかったため。
    /// </summary>
    private const double SlowBroadcastMs = 100, SlowTickMs = 30;

    public sealed record Sample(
        DateTime Utc, double CpuPercent, double ProcessMb, double GcHeapMb, int Threads,
        double? MachineTotalMb, double? MachineAvailableMb, double? LoadAverage1,
        double BroadcastAvgMs, double BroadcastMaxMs, int Broadcasts, int Players, int AiTrains,
        double TickAvgMs, double SendAvgMs, double WorkMs, int WorkCount);

    /// <summary>画面の API・ハブの呼び出し1種類の、ある間の回数と時間（ミリ秒）。</summary>
    public sealed record Work(string Name, int Count, double TotalMs, double MaxMs);

    private sealed class WorkSum
    {
        public int Count;
        public double TotalMs, MaxMs;
    }

    private readonly object _lock = new();
    private readonly List<Sample> _history = new();
    private readonly List<Sample> _minute = new();
    private double _bcSumMs, _bcMaxMs, _tickSumMs, _sendSumMs;
    private int _bcCount;
    private Dictionary<string, WorkSum> _work = new();
    private readonly Queue<Dictionary<string, WorkSum>> _workHistory = new(); // 画面用の直近1分
    private readonly List<Dictionary<string, WorkSum>> _workMinute = new(); // ファイルに書く1分ぶん
    private readonly DailyLog _file = new("load", MaxBytesPerDay,
        "# 時刻 CPU%(平均/最大) プロセスMB(平均/最大) GCヒープMB マシンの空きMB/全体MB ロードアベレージ 配信ms(平均/最大) 参加者 にぎやかし" +
        " 進路の処理ms(平均) 送信待ちms(平均) API・ハブ(回数/合計ms) 上位(名前=回数/合計ms)" + Environment.NewLine);

    /// <summary>配信1回にかかった時間（StateBroadcaster から呼ぶ）。tickMs はそのうち進路の処理、sendMs は送り終わるのを待った時間</summary>
    public void RecordBroadcast(double ms, double tickMs = 0, double sendMs = 0)
    {
        lock (_lock)
        {
            _bcSumMs += ms;
            _tickSumMs += tickMs;
            _sendSumMs += sendMs;
            _bcCount++;
            if (ms > _bcMaxMs) _bcMaxMs = ms;
        }
    }

    /// <summary>画面の API・ハブの呼び出し1回にかかった時間（name は "GET /api/..." や "GameHub.Report"）</summary>
    public void RecordWork(string name, double ms)
    {
        lock (_lock)
        {
            if (!_work.TryGetValue(name, out var w)) _work[name] = w = new WorkSum();
            w.Count++;
            w.TotalMs += ms;
            if (ms > w.MaxMs) w.MaxMs = ms;
        }
    }

    /// <summary>いくつかの間の合計を、時間の多い順に count 件。</summary>
    private static List<Work> Top(IEnumerable<Dictionary<string, WorkSum>> parts, int count) =>
        parts.SelectMany(d => d)
            .GroupBy(kv => kv.Key)
            .Select(g => new Work(g.Key, g.Sum(kv => kv.Value.Count), Math.Round(g.Sum(kv => kv.Value.TotalMs), 1), Math.Round(g.Max(kv => kv.Value.MaxMs), 1)))
            .OrderByDescending(w => w.TotalMs)
            .Take(count)
            .ToList();

    public object View()
    {
        lock (_lock)
            return new
            {
                cores = Environment.ProcessorCount,
                intervalSeconds = (int)SampleInterval.TotalSeconds,
                current = _history.Count > 0 ? _history[^1] : null,
                history = _history.ToList(),
                work = Top(_workHistory, 20), // 直近1分の、画面の API・ハブの呼び出し（時間の多い順）
            };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var proc = Process.GetCurrentProcess();
        var lastCpu = proc.TotalProcessorTime;
        var lastWall = Stopwatch.GetTimestamp();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(SampleInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                proc.Refresh();
                var cpu = proc.TotalProcessorTime;
                var wall = Stopwatch.GetTimestamp();
                var wallSec = Stopwatch.GetElapsedTime(lastWall, wall).TotalSeconds;
                var cpuPercent = wallSec > 0 ? (cpu - lastCpu).TotalSeconds / wallSec / Environment.ProcessorCount * 100 : 0;
                lastCpu = cpu;
                lastWall = wall;

                var (total, avail) = MachineMemory();
                var list = players.Snapshot();
                double bcAvg, bcMax, tickAvg, sendAvg;
                int bcCount;
                Dictionary<string, WorkSum> work;
                lock (_lock)
                {
                    bcAvg = _bcCount > 0 ? _bcSumMs / _bcCount : 0;
                    bcMax = _bcMaxMs;
                    bcCount = _bcCount;
                    tickAvg = _bcCount > 0 ? _tickSumMs / _bcCount : 0;
                    sendAvg = _bcCount > 0 ? _sendSumMs / _bcCount : 0;
                    _bcSumMs = _bcMaxMs = _tickSumMs = _sendSumMs = 0;
                    _bcCount = 0;
                    work = _work;
                    _work = new();
                }

                var s = new Sample(DateTime.UtcNow, Math.Round(cpuPercent, 1), Mb(proc.WorkingSet64), Mb(GC.GetTotalMemory(false)), proc.Threads.Count,
                    total, avail, LoadAverage(), Math.Round(bcAvg, 2), Math.Round(bcMax, 2), bcCount,
                    list.Count(p => !PlayerRegistry.IsVirtual(p.ConnectionId)), list.Count(p => p.ConnectionId.StartsWith(PlayerRegistry.AiPrefix)),
                    Math.Round(tickAvg, 2), Math.Round(sendAvg, 2), Math.Round(work.Values.Sum(w => w.TotalMs), 1), work.Values.Sum(w => w.Count));

                List<Sample>? minute = null;
                List<Dictionary<string, WorkSum>>? workMinute = null;
                lock (_lock)
                {
                    _history.Add(s);
                    if (_history.Count > HistoryCount) _history.RemoveAt(0);
                    _workHistory.Enqueue(work);
                    while (_workHistory.Count > WorkHistoryCount) _workHistory.Dequeue();
                    if (_minute.Count > 0 && _minute[0].Utc.ToLocalTime().Minute != s.Utc.ToLocalTime().Minute)
                    {
                        minute = _minute.ToList();
                        _minute.Clear();
                        workMinute = _workMinute.ToList();
                        _workMinute.Clear();
                    }
                    _minute.Add(s);
                    _workMinute.Add(work);
                }
                if (minute != null) Write(minute, workMinute!);
            }
            catch
            {
                // 測れなくてもサーバーは止めない
            }
        }
    }

    private static double Mb(long bytes) => Math.Round(bytes / 1024.0 / 1024.0, 1);

    /// <summary>マシン全体のメモリ（MB）。Linux は /proc/meminfo、ほかは .NET が知っている全体の量だけ。</summary>
    private static (double? Total, double? Available) MachineMemory()
    {
        try
        {
            if (File.Exists("/proc/meminfo"))
            {
                double? total = null, avail = null;
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:")) total = Kb(line);
                    else if (line.StartsWith("MemAvailable:")) avail = Kb(line);
                    if (total != null && avail != null) break;
                }
                return (total, avail);
            }
            var info = GC.GetGCMemoryInfo();
            return (Mb(info.TotalAvailableMemoryBytes), null);
        }
        catch { return (null, null); }

        static double? Kb(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var kb) ? Math.Round(kb / 1024, 1) : null;
        }
    }

    /// <summary>1分のロードアベレージ（Linux の /proc/loadavg。ないときは null）</summary>
    private static double? LoadAverage()
    {
        try
        {
            if (!File.Exists("/proc/loadavg")) return null;
            var first = File.ReadAllText("/proc/loadavg").Split(' ')[0];
            return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        catch { return null; }
    }

    /// <summary>1分ぶんをまとめて1行書く（平均/最大）</summary>
    private void Write(List<Sample> m, List<Dictionary<string, WorkSum>> work)
    {
        try
        {
            var at = m[0].Utc.ToLocalTime();
            var last = m[^1];
            var bcCount = m.Sum(x => x.Broadcasts);
            var bcAvg = bcCount > 0 ? m.Sum(x => x.BroadcastAvgMs * x.Broadcasts) / bcCount : 0;
            double PerBroadcast(Func<Sample, double> f) => bcCount > 0 ? m.Sum(x => f(x) * x.Broadcasts) / bcCount : 0;
            var top = string.Join(";", Top(work, 12).Select(w => string.Create(CultureInfo.InvariantCulture, $"{w.Name.Replace(' ', '_')}={w.Count}/{w.TotalMs:0}")));
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{at:yyyy-MM-dd HH:mm} cpu {m.Average(x => x.CpuPercent):0.0}/{m.Max(x => x.CpuPercent):0.0} " +
                $"mem {m.Average(x => x.ProcessMb):0}/{m.Max(x => x.ProcessMb):0} gc {last.GcHeapMb:0} " +
                $"machine {Num(m.Min(x => x.MachineAvailableMb))}/{Num(last.MachineTotalMb)} load {Num(last.LoadAverage1, "0.00")} " +
                $"bc {bcAvg:0.0}/{m.Max(x => x.BroadcastMaxMs):0.0} players {m.Max(x => x.Players)} ai {m.Max(x => x.AiTrains)} " +
                $"tick {PerBroadcast(x => x.TickAvgMs):0.0} send {PerBroadcast(x => x.SendAvgMs):0.0} work {m.Sum(x => x.WorkCount)}/{m.Sum(x => x.WorkMs):0} " +
                $"top {top}{Environment.NewLine}");
            // 重い1分は、サーバーの記録にも警告を出す（時間を使っている上位3つつき）
            var tickAvg = PerBroadcast(x => x.TickAvgMs);
            if (bcAvg >= SlowBroadcastMs || tickAvg >= SlowTickMs)
                logger?.LogWarning("配信が遅れています: 配信1回 平均 {Broadcast:0}ms（最大 {Max:0}ms）・進路の処理 平均 {Tick:0}ms。時間を使っているもの: {Top}",
                    bcAvg, m.Max(x => x.BroadcastMaxMs), tickAvg,
                    string.Join("、", Top(work, 3).Select(w => $"{w.Name} {w.Count}回 計{w.TotalMs:0}ms")));
            _file.Append(m[0].Utc, line,
                () => $"{at:yyyy-MM-dd HH:mm} この日の負荷の記録が {_file.MaxMb}MB を超えたので、ここで書くのをやめます{Environment.NewLine}");
        }
        catch
        {
            // 書けなくても測るのは続ける
        }

        static string Num(double? v, string f = "0") => v is { } x ? x.ToString(f, CultureInfo.InvariantCulture) : "-";
    }
}
