using System.Text.RegularExpressions;
using Gokko.Common;

namespace Gokko.Server.Services;

/// <summary>
/// 進路の判断（在線共有モード・フリーモード・列車扱い＋集中扱い）。
/// 0.5秒ごとに <see cref="Tick"/> を呼び、参加者ごとの「ゲームに開通させておく進路」「取り消す進路」「この先の駅の状態」を作る。
///
/// ・方向は列番の偶数・奇数（奇数＝下り）、種別は列番の末尾（なし普通・C準急・D区急・B急行・K快急・A特急）
/// ・ひとつ手前の駅（すべての駅で数える）を先頭が通過したら、その駅の番線を確定し、進路を要求する
/// ・出発進路は、前方の防護区間に他の列車がいなければ開通。単線区間は、さらにその区間に列車がいないことと、相手の駅で対向の出発進路が出ていないこと
/// ・場内・出発とも、進路の軌道回路に他の列車がいる、または他の列車の進路と重なるときは待たせる（手前の駅を通過した順）
/// ・待避: 後ろの「自分より強い種別」に追いつかれる見込みなら待避の番線に入れ（最大3本待つ）、待つ列車ごとに通過・追いつかれない見込み・許容の遅れで外していく
/// ・発車順（集中扱い）: 待避駅の通過・発車の順番を指定できる。先の列車が抜けるまで出発させず、抜かれる列車は待避の番線に入れる
/// ・在線共有モードの列車だけを扱う（特発のみ共有の列車は、位置を進路・信号・ダイヤグラムに使わない）
/// ・取消: ふだんはゲームの自動解除。列車が駅を抜けたのに残っている進路だけ、取り消しを指示する
/// </summary>
public sealed partial class RouteEngine
{
    private static readonly TimeSpan LeftoverDelay = TimeSpan.FromSeconds(20);

    /// <summary>要求した進路がこの時間たっても開通しなければ、ゲームに残っている「要求していない進路」を消す。</summary>
    private static readonly TimeSpan StuckDelay = TimeSpan.FromSeconds(5);

    /// <summary>「残っていた進路を取消」を記録に出したもの（同じ進路を何度も記録しないため）。</summary>
    private readonly HashSet<(string Conn, string Interlock, string Route)> _orphanLogged = new();

    /// <summary>「接近しているため取り消しません」を記録に出したもの（同じことを何度も記録しないため）。</summary>
    private readonly HashSet<(string Conn, string Station, bool Departure)> _holdLogged = new();

    /// <summary>
    /// 列番の読み替え（接続 → ゲームから届く列番・サーバーで使う列番）。途中駅で列番が変わる列車に使う。
    /// </summary>
    private readonly Dictionary<string, (string From, string To)> _renumber = new();

    /// <summary>単線区間の方向てこ（区間 → 向き・鎖錠されているか）。</summary>
    private readonly Dictionary<(string A, string B), (Dir Dir, bool Locked)> _levers = new();

    private readonly RailData _data;
    private readonly RunTimeModel _model;
    private readonly CentralControl _central;
    private const string PanelWait = "駅扱い（信号制御盤で扱い中）";
    private readonly PlayerRegistry _players;
    private readonly IncidentManager _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, TrainRun> _runs = new();
    private long _seq;

    /// <summary>催物用臨停設定（司令用管理画面）。自動運転の列車・スタフ・ダイヤグラムも、これを見る。</summary>
    public EventStops EventStops { get; }

    public RouteEngine(RailData data, RunTimeModel model, CentralControl central, PlayerRegistry players, IncidentManager log, GameClock clock, ConnectionTable connections,
        CrewRoster crew, EventStops eventStops)
    {
        _connections = connections;
        _crew = crew;
        _clock = clock;
        _data = data;
        _model = model;
        _central = central;
        _players = players;
        _log = log;
        _approach = new ApproachTrigger(data);
        central.Changed += _ => Interlocked.Increment(ref _centralVersion); // 運転順序を作り直す目安（RouteEngine.OrderPlan.cs）
        EventStops = eventStops;
        eventStops.Changed += _ => Interlocked.Increment(ref _centralVersion);
        central.AutoRouteRequests = () =>
        {
            lock (_lock)
                return _runs.Values.SelectMany(r => r.Requests)
                    .Where(q => !q.Released && q.CancelledUtc == null && !q.Seen)
                    .Select(q => (q.Def.Interlock, q.Def.Name)).Distinct().ToList();
        };
        central.AutoHeldRoutes = () =>
        {
            var now = DateTime.UtcNow;
            lock (_lock)
                return _runs.Values.SelectMany(r => r.Requests)
                    .Where(q => !q.Released && q.CancelledUtc == null && !q.Entered && q.SeenUtc is { } seen
                                && _data.ClearTimes.TryGetValue($"{q.Def.Interlock}/{q.Def.Name}", out var sec) && now - seen < TimeSpan.FromSeconds(sec))
                    .Select(q => (q.Def.Interlock, q.Def.Name)).Distinct().ToList();
        };
    }

    private readonly ApproachTrigger _approach;
    private readonly ConnectionTable _connections;
    private readonly CrewRoster _crew; // 司令員が出勤しているか（いなければ、詰まった接続待ちを自動で切る）
    private readonly GameClock _clock;

    // =========================================================================================
    // ダイヤ運転（ロープレ時刻とダイヤの時刻）
    // =========================================================================================

    private static readonly TimeZoneInfo Japan = FindJapan();

    private static TimeZoneInfo FindJapan()
    {
        foreach (var id in new[] { "Asia/Tokyo", "Tokyo Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        return TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");
    }

    private bool DiaMode => _central.DiaMode;

    /// <summary>
    /// 今のロープレ時刻（運転日の秒。ロープレ時刻がなければ日本時間の今）。ダイヤの「24:15」のような日またぎに合わせ、4時前は 24時以降として数える。
    /// </summary>
    private double NowSec()
    {
        var t = _clock.Now ?? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Japan).TimeOfDay;
        var sec = t.TotalSeconds;
        return sec < 4 * 3600 ? sec + 86400 : sec;
    }

    /// <summary>
    /// ダイヤの時刻（運転日の秒）まで、あと何秒か（過ぎていれば負）。±12時間に収める: 臨時列車の 0時〜4時の時刻（"00:30:00"）や、
    /// 終夜運転で4時をまたいで待つ列車（3時台に、4時20分の発車を待つ）も、すぐ先の時刻として数える。
    /// </summary>
    private double Until(double sec)
    {
        var d = sec - NowSec();
        while (d > 12 * 3600) d -= 86400;
        while (d < -12 * 3600) d += 86400;
        return d;
    }

    /// <summary>"HH:mm:ss"（24時以降も可）→ 運転日の秒。</summary>
    private static double? ParseDiaTime(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var p = text.Split(':');
        if (p.Length < 2 || !int.TryParse(p[0], out var hh) || !int.TryParse(p[1], out var mm)) return null;
        var ss = p.Length > 2 && int.TryParse(p[2], out var x) ? x : 0;
        return hh * 3600 + mm * 60 + ss;
    }

    private static string FormatDiaTime(double sec)
    {
        var t = (int)Math.Round(sec);
        return $"{t / 3600 % 24:00}:{t / 60 % 60:00}:{t % 60:00}";
    }

    /// <summary>ダイヤのその駅の時刻（運転日の秒）。depart なら発（通過）時刻、なければ着。arrive なら着時刻、なければ発。</summary>
    private double? DiaSec(TrainRun run, string stationId, bool depart)
    {
        var stop = run.Dia?.Stops.FirstOrDefault(x => x.StationId == stationId);
        if (stop == null) return null;
        var arr = StopArr(run.Dia!, stop); // 催物用臨停設定の臨時停車は、通過時刻の少し前に着く
        return depart ? ParseDiaTime(stop.Dep) ?? ParseDiaTime(arr) : ParseDiaTime(arr) ?? ParseDiaTime(stop.Dep);
    }

    /// <summary>ダイヤの着時刻。催物用臨停設定で臨時停車になる駅（時刻表では通過）は、通過時刻 − 停車時間。</summary>
    private string? StopArr(DiaTrain dia, DiaStop stop) =>
        stop.Arr ?? (stop.Stop == "通" && EventStops.Times(dia, dia.Stops.IndexOf(stop)) is { Arr: { } a } ? a : null);

    /// <summary>
    /// 入換で始まる列車の入換時刻（列番 → 運転日の秒）。前の列車のスタフの「折返|881C 08:42入換」から。
    /// 後運用を差し替えたら作り直す。
    /// </summary>
    private Dictionary<string, double> DiaShuntTimes
    {
        get
        {
            var ver = _data.Turns.Version;
            if (_diaShuntTimes == null || _diaShuntVersion != ver)
            {
                _diaShuntTimes = _data.Dia.Values
                    .Select(d => _data.Turns.TurnOf(d))
                    .Where(t => t is { Kind: "折返", How: "入換", Number: not null, Time: not null })
                    .Select(t => (t!.Number!, T: ParseDiaTime(t.Time)))
                    .Where(x => x.T != null)
                    .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.First().T!.Value);
                _diaShuntVersion = ver;
            }
            return _diaShuntTimes;
        }
    }
    private Dictionary<string, double>? _diaShuntTimes;
    private int _diaShuntVersion;

    /// <summary>入換で始まる列車の、前の列車の「終点のあと」（列番 → 折返）。留置線から出るときの線（Out）・待つ列車（After）を見る。</summary>
    private Dictionary<string, DiaTurn> DiaShuntTurns
    {
        get
        {
            var ver = _data.Turns.Version;
            if (_diaShuntTurns == null || _diaShuntTurnsVersion != ver)
            {
                _diaShuntTurns = _data.Dia.Values
                    .Select(d => _data.Turns.TurnOf(d))
                    .OfType<DiaTurn>()
                    .Where(t => t is { Kind: "折返", How: "入換", Number: not null })
                    .GroupBy(t => t.Number!).ToDictionary(g => g.Key, g => g.First());
                _diaShuntTurnsVersion = ver;
            }
            return _diaShuntTurns;
        }
    }
    private Dictionary<string, DiaTurn>? _diaShuntTurns;
    private int _diaShuntTurnsVersion;

    /// <summary>
    /// ダイヤ運転中の、留置線から出る入換: 前の列車の折返に「この列車が通り過ぎてから」（After。大道寺 5番線から出る 2250 は 2121A）があれば、
    /// その列車が入換の進路の範囲を抜けるまで待つ（通り過ぎたら、あとは見ない）。その列車が走っていなければ、ダイヤでこの駅に来る時刻まで待つ。
    /// </summary>
    private string? AfterHold(TrainRun run, StationRun st, IEnumerable<RouteDef> path)
    {
        if (!DiaMode || run.Dia == null || st.AfterPassed || _central.IsForced(run.Train, st.St.Id)) return null;
        if (!DiaShuntTurns.TryGetValue(run.Dia.Number, out var turn) || turn.After is not { } after) return null;
        var circuits = Circuits(path).ToHashSet();
        var other = _runs.Values.FirstOrDefault(o => o != run && o.Dia?.Number == after);
        bool passed;
        if (other != null)
        {
            var os = other.Stations.FirstOrDefault(x => x.St.Id == st.St.Id);
            passed = os == null || os.Done || os.HereUtc != null && !other.Player.TrackCircuits.Any(circuits.Contains);
        }
        else
        {
            var stop = _data.DiaOf(after)?.Stops.FirstOrDefault(x => x.StationId == st.St.Id);
            // 日をまたぐ時刻（0時〜4時の臨時列車・終夜運転）でも正しく比べる（Until）
            passed = ParseDiaTime(stop?.Arr ?? stop?.Dep) is not { } due || Until(due) <= 0;
        }
        if (passed)
        {
            st.AfterPassed = true;
            return null;
        }
        return $"{after} の通過待ち（{after} が通ってから入換）";
    }

    /// <summary>留置線へ入れ終わった列車（2051 が大道寺 5番線に入った）へのお知らせ。次の列番に変えると、留置線から出る入換を扱う。</summary>
    private string? StabledMessage(TrainRun run)
    {
        if (run.Stations.FirstOrDefault(s => !s.IsStart && s.ShuntThen == GroupState.Requested && s.ShuntThenPath is { Count: > 0 }) is not { } st) return null;
        var dest = st.ShuntThenPath![^1].Circuits.LastOrDefault();
        if (dest == null || !run.Player.TrackCircuits.Contains(dest)) return null;
        var turn = run.Dia != null ? _data.Turns.TurnOf(run.Dia) : null;
        var where = st.ShuntOutChoice is { } i && ShuntOutOptions(run, st) is var outs && i < outs.Count ? outs[i].Label : "留置線";
        return turn?.Number is { } next
            ? $"{st.St.Name} {where}に留置しました。{next} に列番を変えると、{(turn.Time is { } tm ? $"{tm} ごろ" : "")}番線への入換を扱います"
            : $"{st.St.Name} {where}に留置しました";
    }

    /// <summary>
    /// 入換の2つ目の進路（途中の線で向きを変えてから開ける。大道寺 X線 → 5番線、5番線 → X線 → 2番線 の X線 → 2番線）。
    /// 列車が途中の線（at の回路）に入りきって止まったら、進路の範囲が空いていれば開ける。
    /// </summary>
    private void RequestThen(TrainRun run, StationRun st, List<RouteDef> path, string at, bool departure, Dictionary<string, TrainRun> occupied, DateTime now)
    {
        var p = run.Player;
        var inMid = p.TrackCircuits.Count > 0 && p.TrackCircuits.All(c => c == at) && p.Speed < StopSpeedKmh;
        var wait = !inMid ? "途中の線に入って止まるまで"
            : PanelControlled(run, st) ? PanelWait
            : InterlockConflict(run, path, occupied);
        if (wait != null)
        {
            if (inMid) ShuntWaitLog(now, run, st, "入換（向きを変えて）", st.ShuntThenWait, wait);
            (st.ShuntThen, st.ShuntThenWait) = (GroupState.Waiting, wait);
            return;
        }

        RouteLog(now, run, st, "入換（向きを変えて）", path);
        foreach (var r in Enumerable.Reverse(path))
            run.Requests.Add(new RouteReq { Def = r, Id = ++_seq, Station = st, Departure = departure, RequestedUtc = now });
        (st.ShuntThen, st.ShuntThenWait) = (GroupState.Requested, "");
        _log.Note($"入換: {run.Train} {st.St.Name} 向きを変えて {string.Join("・", path.Select(x => x.Name))}");
    }

    /// <summary>
    /// ダイヤ運転中の始発の入換（検車区・引上線からホームへ）: スタフに入換時刻があれば、その30秒前まで開けない（遅れていれば、空きしだい）。
    /// 引き上げてすぐ入れ換えて、ホームを早くからふさがないように。時刻が書かれていなければ待たない。
    /// </summary>
    private string? DiaShuntHold(TrainRun run, StationRun st)
    {
        if (!DiaMode || run.Dia == null || _central.OpenAt(run.Train, st.St.Id) != null) return null;
        if (!DiaShuntTimes.TryGetValue(run.Dia.Number, out var t)) return null;
        return Until(t) > DepartLeadSeconds ? $"入換時刻待ち（{FormatDiaTime(t)} 入換。{DepartLeadSeconds}秒前に開通）" : null;
    }

    /// <summary>
    /// ダイヤ運転中の始発の入換: 入換で入る番線を、ダイヤでそれより先に使う列車（その番線に着いて出ていく列車）が、まだ駅を出ていなければ待つ。
    /// 先の列車が遅れていても、入換の列車が先に番線をふさがないように（ふさぐと、先の列車が場内に入れず、出発順序の待ち合いで止まる）。
    /// プレイヤーの列車も自動運転の列車も同じに扱う。比べるのは、自分の入換時刻（なければ発車時刻）と、相手のこの駅の発車時刻（なければ着時刻）。
    /// </summary>
    private string? DiaShuntTrackHold(TrainRun run, StationRun st, int to)
    {
        if (!DiaMode || run.Dia == null) return null;
        var mine = DiaShuntTimes.TryGetValue(run.Dia.Number, out var t) ? t : DiaSec(run, st.St.Id, depart: true);
        if (mine == null) return null;
        var first = new List<string>();
        foreach (var other in _runs.Values.Where(o => o != run && o.Dia != null))
        {
            if (other.Stations.FirstOrDefault(s => s.St.Id == st.St.Id) is not { Done: false } os || IsCutOff(other, os)) continue;
            if ((os.Track ?? other.Dia!.Stops.FirstOrDefault(x => x.StationId == st.St.Id)?.Track) != to) continue;
            // 同じ時刻も先（入換時刻は分まで。大道寺の 1781C は 17:05 入換で、4番線を 1603A が 17:05:00 に出てから入る）
            if (DiaSec(other, st.St.Id, depart: true) is { } theirs && theirs <= mine) first.Add(other.Train);
        }

        return first.Count > 0 ? $"番線を先に使う列車待ち（{string.Join("・", first)} が {to}番線を出てから入換）" : null;
    }

    /// <summary>出発も場内と一緒に、出発・場内の順に要求する駅（ダイヤ運転中。浜園）。出発が開けなくても場内は開ける。</summary>
    private bool DepartBeforeArrive(StationRun st) => DiaMode && _data.DepartBeforeArrive.Contains(st.St.Id);

    /// <summary>
    /// ダイヤ運転中の発車時刻: 停車列車・始発は、ダイヤの発車時刻の30秒前（踏切防護のある駅は40秒前。DepartLead）まで出発を開けない（遅れていれば、前が空きしだい）。
    /// 通過列車と、出発も場内と一緒に開ける駅（浜園）と、着から発車まで2分以内の停車（ShortDwell）は時刻で待たない。スジずらし・発車時刻の手打ちがあれば、そちら（DepartTimeHold）を使う
    /// </summary>
    private string? DiaDepartHold(TrainRun run, StationRun st)
    {
        if (!DiaMode || run.Dia == null || _central.OpenAt(run.Train, st.St.Id) != null) return null;
        if (!st.IsStart && st.StopType == "通過" && !st.DepartStop) return null;
        // 浜園に入ってくる列車は、出発も場内と一緒に開けるので時刻で待たない。浜園から始発する列車（回1094 など）は時刻で待つ
        if (DepartBeforeArrive(st) && !st.IsStart) return null;
        if (DiaSec(run, st.St.Id, depart: true) is not { } dep) return null;
        if (ShortDwell(run, st)) return null;
        var lead = DepartLead(st);
        return Until(dep) > lead ? $"発車時刻待ち（{FormatDiaTime(dep)} 発。{lead}秒前に開通）" : null;
    }

    /// <summary>短い停車の上限（秒）。着から発車までがこれ以内なら、発車時刻を待たずに場内と一緒に出発も開ける。</summary>
    private const int ShortDwellSeconds = 120;

    /// <summary>
    /// ダイヤ運転中、ダイヤの着時刻から発車時刻（司令が手で決めた発車時刻があれば、そちら）までが2分以内か。
    /// そうなら、発車時刻の30秒前（DepartLead）を待たずに、場内と一緒に出発も開ける。出発停止の番線（種別が変わる列車を含む）と始発は除く。
    /// </summary>
    private bool ShortDwell(TrainRun run, StationRun st)
    {
        if (!DiaMode || run.Dia == null || st.IsStart || st.DepartStop) return false;
        if (ParseDiaTime(run.Dia.Stops.FirstOrDefault(x => x.StationId == st.St.Id)?.Arr) is not { } arr) return false;
        double? dep = _central.OpenAt(run.Train, st.St.Id) is { } t
            ? NowSec() + (t - DateTime.UtcNow).TotalSeconds * _clock.Speed
            : DiaSec(run, st.St.Id, depart: true);
        if (dep is not { } d) return false;
        var dwell = d - arr;
        while (dwell > 43200) dwell -= 86400;
        while (dwell < -43200) dwell += 86400;
        return dwell <= ShortDwellSeconds;
    }

    /// <summary>
    /// この駅で列番が変わり、種別も変わる（ダイヤの「次の列番」。581 → 581C など）。どの駅でも出発停止で入れ、
    /// 止まって次の列番に読み替えてから、次の列番（その駅の始発）として出発を開ける。
    /// </summary>
    private bool TypeChangesAt(TrainRun run, StationRun st) =>
        !st.IsStart && run.Dia?.Next is { } n && n.StationId == st.St.Id && TrainInfo(n.Number) is { } ni
        && (_data.DiaOf(n.Number)?.Type ?? ni.Type) != run.Type;

    /// <summary>
    /// ダイヤ運転中: この駅から出る始発列車のダイヤの発車が、この列車の着（通過）より前で、その出発の進路と場内の進路が重なるなら、始発列車が先
    /// （江ノ原検車区13番線から出る回送より先に、12番線に入る列車が場内を取ってしまわないように）。
    /// </summary>
    private string? DiaStartFirstHold(TrainRun run, StationRun st)
    {
        if (!DiaMode || run.Dia == null || st.ArrivePath.Count == 0) return null;
        if (DiaSec(run, st.St.Id, depart: false) is not { } mine) return null;
        // 単線区間をこちらへ向かっている列車（区間にいる・手前の駅で区間へ出る出発をもう開けた）は待たせない。
        // 始発列車はその区間へ出られない（対向列車を待つ）ので、待たせると互いに待って詰まる
        // （三郷: 赤山町を出た上りが、三郷始発の下りの発車待ちで場内に入れず、下りは単線区間の上りを待っていた）
        if (run.Stations.TakeWhile(s => s != st).LastOrDefault() is { } prev && _data.IsSingleTrack(prev.St.Id, st.St.Id))
        {
            var (lo, hi) = (Math.Min(prev.Zone, st.Zone), Math.Max(prev.Zone, st.Zone));
            if (prev.Depart == GroupState.Requested || prev.Done || run.Player.Zones.Any(z => z > lo && z < hi)) return null;
        }
        var path = Circuits(st.ArrivePath).ToHashSet();
        foreach (var other in _runs.Values.Where(o => o != run && o.Dia != null && o.HasPosition))
        {
            var os = other.Stations.FirstOrDefault(s => s.St.Id == st.St.Id && s.IsStart && !s.Done);
            if (os == null || os.DepartPath.Count == 0) continue;
            if (DiaSec(other, st.St.Id, depart: true) is { } theirs && theirs < mine && Circuits(os.DepartPath).Any(path.Contains))
                return $"始発列車の発車待ち（{other.Train} が先）";
        }
        return null;
    }

    /// <summary>
    /// ダイヤ運転中の駅の出発順序（手で決めていなければ、ダイヤグラムで調整したダイヤの発の順。RouteEngine.OrderPlan.cs）。
    /// 手前の駅で順序を入れ替えた・◆をずらした列車は、この先の駅でも同じように後ろになる。ダイヤにない列車は入れない（待たない・待たせない）。
    /// </summary>
    private IReadOnlyList<string>? EffectiveOrder(string stationId, Dir dir)
    {
        if (_central.Order(stationId, dir) is { } manual) return manual;
        if (!DiaMode) return null;
        if (_orderPlan is { } op) return op.Depart.TryGetValue((stationId, dir), out var planned) && planned.Count > 0 ? planned : null;
        var list = _runs.Values.Where(r => r.Dir == dir && r.Dia != null && r.Stations.Any(s => s.St.Id == stationId && !s.Done))
            .Select(r => (r.Train, T: DiaSec(r, stationId, depart: true))).Where(x => x.T != null).OrderBy(x => x.T).Select(x => x.Train).ToList();
        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// ダイヤ運転中の単線区間の走行順（手で決めていなければ、区間に入る駅の、ダイヤグラムで調整したダイヤの発の順。RouteEngine.OrderPlan.cs）。
    /// 手前の駅で順序を入れ替えた・◆をずらした列車は、この先の単線区間でも同じように後ろになる。もう区間に入った列車は先頭
    /// </summary>
    private IReadOnlyList<string>? EffectiveSectionOrder(string a, string b)
    {
        if (_central.SectionOrder(a, b) is { } manual) return manual;
        if (!DiaMode) return null;
        var list = new List<(string Train, double T)>();
        foreach (var r in _runs.Values.Where(r => r.Dia != null))
        {
            var entry = r.Stations.FirstOrDefault(s => (s.St.Id == a || s.St.Id == b) && NextControlStation(r, s) is { } nx && (nx == a || nx == b) && nx != s.St.Id);
            if (entry == null || SectionCleared(r, a, b)) continue;
            if (entry.Done) { list.Add((r.Train, double.MinValue)); continue; } // 区間の中
            var t = _orderPlan?.Times.GetValueOrDefault((r.Train, entry.St.Id))?.Dep ?? DiaDaySec(r, entry.St.Id, depart: true);
            if (t != null) list.Add((r.Train, t.Value));
        }

        return list.Count > 0 ? list.OrderBy(x => x.T).Select(x => x.Train).ToList() : null;
    }

    /// <summary>
    /// その駅の場内（通過なら出発も）を取り始める位置（信号機）。設定で切ってあれば・計算できなければ null。
    /// 第2場内もまとめて取る駅（firstHome の withSecond）は、第1場内の取り始め（第1・第2場内と、通過なら出発も、ここでまとめて取る）。
    /// </summary>
    private ApproachTrigger.Result? ApproachOf(TrainRun run, StationRun st)
    {
        if (!_data.ApproachTriggerEnabled || st.IsStart) return null;
        if (HomesTogether(run, st) && FirstHomeOf(run, st) is { } fh) return fh;
        return _approach.For(st.St.Id, run.Dir, st.St.Routes[run.Dir], st.Track, IsPassingRoute(run, st), _data.ApproachTriggerMargin,
            firstHomeOpen: _data.ApproachFirstHome.ContainsKey((st.St.Id, run.Dir)));
    }

    private bool IsPassingRoute(TrainRun run, StationRun st) =>
        st.StopType == "通過" && !st.DepartStop && !st.Terminal && !IsTurnbackTrack(run, st);

    /// <summary>第1場内を先に取る駅（approachTrigger.firstHome）で、第1場内だけを取り始める位置。それ以外の駅は null。</summary>
    private ApproachTrigger.Result? FirstHomeOf(TrainRun run, StationRun st, int? track = null)
    {
        if (!_data.ApproachTriggerEnabled || st.IsStart || !_data.ApproachFirstHome.TryGetValue((st.St.Id, run.Dir), out var trigger)) return null;
        return _approach.FirstHome(st.St.Id, run.Dir, st.St.Routes[run.Dir], track ?? st.Track, IsPassingRoute(run, st), trigger);
    }

    /// <summary>第1場内を先に取る駅で、第2場内も第1場内と同じ位置でまとめて取るか（approachTrigger.firstHome の withSecond）。</summary>
    private bool HomesTogether(TrainRun run, StationRun st) => _data.ApproachFirstHomeWithSecond.Contains((st.St.Id, run.Dir));

    /// <summary>番線を決める前（どの番線でも）に、第1場内の取り始めを過ぎたか。過ぎたら番線を確定する。</summary>
    private bool PassedFirstHomeAnyTrack(TrainRun run, StationRun st) =>
        _data.ApproachFirstHome.ContainsKey((st.St.Id, run.Dir))
        && st.St.Routes[run.Dir].Arrive.Keys.Any(t => FirstHomeOf(run, st, t) is { } fh && PassedApproach(run, st, fh));

    /// <summary>
    /// 第1場内を先に取る駅で、第1場内の取り始めを過ぎ、第2場内の取り始め待ちだけで止まっているなら、第1場内の進路だけ先に開ける。
    /// 第2場内は、ふだんどおり場内の組として、第1場内が開いている前提の位置で開ける（開いている第1場内は送り直さない）。
    /// </summary>
    private void RequestFirstHome(TrainRun run, StationRun st, Dictionary<string, TrainRun> occupied, string? otherWait, DateTime now)
    {
        if (otherWait != null || FirstHomeOf(run, st) is not { } fh || !PassedApproach(run, st, fh)) return;
        var outer = st.ArrivePath.Where(d => d.Signal == fh.Home).ToList();
        if (outer.Count == 0 || outer.Count == st.ArrivePath.Count) return;
        var live = run.Requests.Where(r => r.Station == st && !r.Released && r.CancelledUtc == null).Select(r => r.Def).ToHashSet();
        if (outer.All(live.Contains)) return;
        if (Conflict(run, st, outer, occupied, departure: false) != null) return;
        foreach (var r in Enumerable.Reverse(outer).Where(r => !live.Contains(r)))
            run.Requests.Add(new RouteReq { Def = r, Id = ++_seq, Station = st, Departure = false, RequestedUtc = now });
        RouteLog(now, run, st, "第1場内", outer);
    }

    /// <summary>駅・向き・番線ごとの、進路を取り始める位置の一覧（確かめる用）。margin は限界点からの余裕（閉塞の数）。</summary>
    public List<object> ApproachTable(int margin)
    {
        var list = new List<object>();
        foreach (var st in _data.ControlStations)
        foreach (var dir in new[] { Dir.Down, Dir.Up })
        {
            var routes = st.Routes[dir];
            var tracks = routes.Arrive.Keys.OrderBy(t => t).Select(t => (int?)t).ToList();
            if (routes.Through.Count > 0) tracks.Add(null);
            foreach (var track in tracks)
            foreach (var passing in track == null ? new[] { true } : routes.Depart.ContainsKey(track.Value) ? new[] { false, true } : new[] { false })
            {
                var first = _data.ApproachFirstHome.GetValueOrDefault((st.Id, dir));
                var r = _approach.For(st.Id, dir, routes, track, passing, margin, firstHomeOpen: first != null);
                var fh = first != null ? _approach.FirstHome(st.Id, dir, routes, track, passing, first) : null;
                // 第2場内もまとめて取る駅は、場内（通過なら出発も）を第1場内の取り始めでまとめて取る
                var together = first != null && _data.ApproachFirstHomeWithSecond.Contains((st.Id, dir));
                list.Add(new
                {
                    station = st.Name, direction = dir.Label(), track, kind = passing ? "通過" : "停車",
                    home = r?.Home, changed = r?.Changed, limits = r?.Limits, triggers = together && fh != null ? fh.Triggers : r?.Triggers,
                    firstHomeTriggers = fh?.Triggers, // 第1場内を先に取る位置（firstHome の駅。withSecond でなければ triggers はそのあとの第2場内）
                    withSecond = together ? true : (bool?)null
                });
            }
        }

        return list;
    }

    /// <summary>列車の先頭が、取り始める信号機を過ぎたか（その信号機から場内信号機までの回路にいる、または駅に着いている）。</summary>
    private static bool PassedApproach(TrainRun run, StationRun st, ApproachTrigger.Result a) =>
        (run.Front - st.Zone) * run.Dir.Step() >= 0 || run.Player.TrackCircuits.Any(a.Near.Contains);

    /// <summary>
    /// 進路を取り始める位置（ApproachTrigger）をまだ過ぎていなければ、場内を要求しない（通過列車は出発も、場内を待つので一緒に待つ）。
    /// 現示が変わるところを運転士に見せない、いちばん遅い位置で取る。
    /// </summary>
    /// <summary>特発臨（列番, 駅ID → その駅で特発臨の列番に変わるのを待っているか）。Program.cs で TokuhatsuRinji につなぐ。</summary>
    public Func<string, string, bool>? RinjiPendingAt { get; set; }

    /// <summary>
    /// その駅で、次の列番に変わるのを待っている（途中折返しで打ち切った駅・特発臨の発駅）。
    /// 待っている間は、引上げの入換などを自動で開けない。
    /// </summary>
    private bool WaitsRenumberAt(TrainRun run, StationRun st) =>
        run.Dia != null && _data.Turns.DirectOf(run.Dia.Number) is { Midway: true } mw && mw.Station == st.St.Id
        || RinjiPendingAt?.Invoke(run.Train, st.St.Id) == true
        || run.GameTrain.Length > 0 && RinjiPendingAt?.Invoke(run.GameTrain, st.St.Id) == true;

    private string? ApproachHold(TrainRun run, StationRun st) =>
        ApproachOf(run, st) is { } a && !PassedApproach(run, st, a) ? $"取り始め待ち（{string.Join("・", a.Triggers)} を過ぎたら）" : null;

    // =========================================================================================
    // 列車・駅ごとの状態
    // =========================================================================================

    private enum GroupState
    {
        None,
        Waiting,
        Requested
    }

    private sealed class RouteReq
    {
        public required RouteDef Def { get; init; }
        public required long Id { get; init; }
        public required StationRun Station { get; init; }
        public required bool Departure { get; init; }
        public DateTime RequestedUtc { get; init; } = DateTime.UtcNow;
        public bool Seen { get; set; }
        /// <summary>ゲームで開通を確かめた時刻（現示時素に使う）。</summary>
        public DateTime? SeenUtc { get; set; }
        public bool Released { get; set; }

        /// <summary>列車がこの進路の軌道回路に入ったことがある。</summary>
        public bool Entered { get; set; }

        /// <summary>番線の変更などで取り消した。ゲームにあれば取り消しを指示し、なくなったら解除済みにする。</summary>
        public DateTime? CancelledUtc { get; set; }

        /// <summary>管理画面の手動進路（進路名を直接指定したもの）。</summary>
        public bool Manual { get; init; }
    }

    /// <summary>何駅手前に着いたら開通させるか（すべての駅で数える。2 = 2つ手前の駅に着いたら）。</summary>
    private const int TriggerStationsAhead = 2;

    /// <summary>何駅手前に着いたら開通させるか。単線区間を通って入る駅は1つ手前（遠くから単線区間の駅をふさがない）、それ以外は2つ手前。</summary>
    private int TriggerAhead(TrainRun run, StationRun st) =>
        run.Stations.TakeWhile(s => s != st).LastOrDefault() is { } prev && _data.IsSingleTrack(prev.St.Id, st.St.Id) ? 1 : TriggerStationsAhead;

    /// <summary>ダイヤグラムの◆で指定した開通時刻を使うか。フリーモードでは時刻に依らず開通させるので使わない（ダイヤモードで使う）。</summary>
    public static bool UseOpenTime => false;

    private sealed class StationRun
    {
        /// <summary>手前の駅での打ち切りで扱わなくなったことを、進路の記録に書いた。</summary>
        public bool CutOffLogged { get; set; }

        /// <summary>出発停止の番線に入る（場内進路が解除されるまで出発を開通させない。停車種別は「停車」で送る）。</summary>
        public bool DepartStop { get; set; }

        /// <summary>待避線（待避駅の待避の番線）に着いた時刻。最低の停車時分を数える。</summary>
        public DateTime? LoopArrivedUtc { get; set; }

        /// <summary>列車が番線に入りきった時刻（番線ごとの「入ってから何秒後に出発を開通」を数える）。</summary>
        public DateTime? InTrackUtc { get; set; }

        /// <summary>番線に入りきって、ゲームの速度が 0km/h になった（止まった）時刻。出発・入換の開通の合図に使う。</summary>
        public DateTime? StoppedUtc { get; set; }

        /// <summary>先頭がこの駅に着いた時刻（始発は認識した時刻）。単線区間で、同時に待ち合ったときの順番に使う。</summary>
        public DateTime? HereUtc { get; set; }

        /// <summary>単線区間で、先に通すと決めた対向列車（最大2本。決めたら、その列車が来るまで待つ）。</summary>
        public List<string> OpposingWaits { get; } = new();

        /// <summary>この駅で先に通すと決めた対向列車の延べ本数（最大2本）。</summary>
        public int WaitedTotal { get; set; }

        /// <summary>場内・出発の待ちが、進路の競合（在線・重なり・順番待ち）によるものか。順番待ちの相手にするのはこれだけ。</summary>
        public bool ArriveBlocked { get; set; }
        public bool DepartBlocked { get; set; }

        /// <summary>駅扱い（強制進路）。連動の確認だけで開通させる（抑止・待ちを無視）。</summary>
        public bool ForceArrive { get; set; }
        public bool ForceDepart { get; set; }
        public bool ForceShunt { get; set; }

        /// <summary>抑止をかけたが、列車が接近していて取り消さなかった進路（"場内"・"出発"・"場内出発"。なければ空）。</summary>
        public string HoldKept { get; set; } = "";

        /// <summary>駅扱いで開通させた進路がある（後続に譲るための自動の取消をしない）。</summary>
        public bool ManualUsed { get; set; }

        public required StationDef St { get; init; }
        public int Zone { get; init; }
        public string StopType { get; set; } = "通過";
        public bool Terminal { get; set; }

        /// <summary>ダイヤ・ゲームどおりの停車種別と終着（臨時停車・臨時通過・打ち切りを外したときに戻す）。</summary>
        public string BaseStopType { get; set; } = "通過";
        public bool BaseTerminal { get; set; }

        /// <summary>運転士の「打ち切り・入換」の選択で、この駅を終着にしているか。</summary>
        public bool DriverEnd { get; set; }
        public bool IsStart { get; set; }
        public int? Track { get; set; }
        public string TrackSource { get; set; } = "";
        public bool Locked { get; set; }
        public bool Triggered { get; set; }
        public DateTime TriggeredUtc { get; set; }
        public bool Done { get; set; }
        public DateTime DoneUtc { get; set; }
        public bool DepartureRequested { get; set; }
        public List<RouteDef> ArrivePath { get; set; } = new();
        public List<RouteDef> DepartPath { get; set; } = new();
        public GroupState Arrive { get; set; }
        public GroupState Depart { get; set; }
        public string ArriveWait { get; set; } = "";
        public string DepartWait { get; set; } = "";
        /// <summary>始発で、引上線などの入換が必要な番線にいるなら、その番線。</summary>
        public string? ShuntFrom { get; set; }
        /// <summary>始発で、出発できる番線に一度でもいたか。いたら、本線の回路（大道寺の X線）からの入換元とはもうみなさない。</summary>
        public bool WasOnDepartTrack { get; set; }
        public bool ShuntRequested { get; set; }
        public GroupState Shunt { get; set; }
        public string ShuntWait { get; set; } = "";

        /// <summary>連結（ダイヤの注記に「併」のある駅）: 番線に止まって待っている増結車。いなければ null。</summary>
        public TrainRun? CouplePartner { get; set; }
        /// <summary>連結: まだ番線に入りきっていない増結車（回730・連777 など）の列番。いなければ null。</summary>
        public string? CoupleWaiting { get; set; }
        /// <summary>連結: あとで開ける、番線へ入る入換信号機の進路（大道寺4番は 28L、新野崎4番は 22L）。進路を取ったときに決める。</summary>
        public RouteDef? CoupleSecond { get; set; }
        /// <summary>連結: 番線へ入る入換信号機がない（番線へは入れない）。</summary>
        public bool CoupleNoShunt { get; set; }
        /// <summary>連結: 番線の手前の回路（大道寺 44T・45T、新野崎 34T）。列車がここに入って止まってから、番線へ入る入換信号機を開ける。</summary>
        public string? CoupleStopAt { get; set; }
        public bool CoupleSecondRequested { get; set; }
        /// <summary>連結: 駅扱いの「誘導開通」。止まるのを待たずに、番線の場内も開ける。</summary>
        public bool CoupleGuide { get; set; }
        /// <summary>連結: 列車が止まった時刻（番線の場内の手前・番線の中）。完全に止まったことを確かめるのに使う。</summary>
        public DateTime? CoupleStoppedUtc { get; set; }
        public bool Coupled { get; set; }
        /// <summary>解放: 切り離した（「解」の列車を出した）。止まった時刻は完全停止の確認に使う。</summary>
        public bool Split { get; set; }
        public DateTime? SplitStoppedUtc { get; set; }

        /// <summary>終着の列車の入換（引上げ）: 要求された入換（その番線の shuntOut の何番目か）。要求がなければ null。</summary>
        public int? ShuntOutChoice { get; set; }
        public GroupState ShuntOut { get; set; }
        public string ShuntOutWait { get; set; } = "";
        public bool ForceShuntOut { get; set; }

        /// <summary>
        /// 引き上げた線（大道寺の X線）にいる終着の列車への、駅扱いの入換（その線から To 番線へ。TrackRoutes.json の shunt）。
        /// 要求がなければ null。開通させたら null に戻す。
        /// </summary>
        public ShuntDef? ManualShuntFrom { get; set; }
        public string ManualShuntFromWait { get; set; } = "";

        /// <summary>入換で経由する線路（"E線" など。直接なら null）。</summary>
        public string? ShuntVia { get; set; }

        /// <summary>
        /// 入換の2つ目の進路（途中の線で向きを変えてから開ける。大道寺 3番線 → X線 → 5番線 の 31L5）と、向きを変える線の回路（TH65_XT）。
        /// 引上げ（終着）でも、始発の入換でも使う。なければ null。
        /// </summary>
        public List<RouteDef>? ShuntThenPath { get; set; }
        public string? ShuntThenAt { get; set; }
        public GroupState ShuntThen { get; set; }
        public string ShuntThenWait { get; set; } = "";
        /// <summary>留置線から出る前に待つ列車（前の列車の折返の After）が、もう通り過ぎたか。</summary>
        public bool AfterPassed { get; set; }
        /// <summary>自動の待避で待っている列車（強い種別の後続列車。最大 MaxPassingWaits 本）。</summary>
        public List<Partner> Partners { get; } = new();

        public bool Passing => Partners.Count > 0;

        /// <summary>発車順（集中扱い）で、先の列車を待っているところ。</summary>
        public bool OrderWaiting { get; set; }

        public List<int> Options { get; set; } = new();
    }

    private sealed class Partner
    {
        public required string Conn { get; init; }
        public required string Train { get; init; }
        public DateTime ExpectedPassUtc { get; init; }
    }

    private sealed class TrainRun
    {
        public required string ConnId { get; set; }
        public required string Train { get; init; }

        /// <summary>
        /// ゲームの列番（同じ列番の列車がほかにいて、サーバーでは Train に接尾辞 X・Y・Z を付けて呼び分けているときの、元の列番）。
        /// ゲームへ送る進路の列番はこちら（そのプレイヤーのゲームには、その列車しかいないため）
        /// </summary>
        public string GameTrain { get; init; } = "";
        public required string Type { get; init; }
        /// <summary>今の列番の種別記号から決めた種別（列車走行位置・発車案内に出す）。時刻表と種別が違う列番で走っているときに使う。</summary>
        public required string NumberKind { get; init; }
        public required Dir Dir { get; init; }

        /// <summary>ダイヤにある列車なら、その列車（番線・停車・終着・列選の基本）。なければ種別ごとの所定で作る。</summary>
        public DiaTrain? Dia { get; set; }

        /// <summary>出発信号機の列選に出す文字（ダイヤの種別表記から）。空ならクライアントがゲームの種別から作る。</summary>
        public string Indicator { get; init; } = "";

        public PlayerInfo Player { get; set; } = null!;
        public bool HasPosition { get; set; }
        public int Front { get; set; }
        public int Rear { get; set; }
        public double Fraction { get; set; } = 0.5;
        /// <summary>Fraction を停車駅の距離から出せたか（出せなければ 0.5 のまま）。</summary>
        public bool FractionKnown { get; set; }
        /// <summary>先頭が今のゾーンに来た時刻（初めて位置が分かったときは、その時刻）。同じゾーンにいる2本の前後を決めるのに使う。</summary>
        public DateTime? ZoneSinceUtc { get; set; }
        /// <summary>先頭が手前のゾーンから今のゾーンへ進んだのを見たか（false なら、初めて位置が分かったときから、このゾーンにいる）。</summary>
        public bool ZoneEntered { get; set; }
        public bool Initialized { get; set; }

        /// <summary>駅の一覧を作った・決め直したときのゲームの停車駅の目安（ダイヤにない列車で、変わったら決め直す）。</summary>
        public string StopsKey { get; set; } = "";
        public List<StationRun> Stations { get; } = new();
        public List<RouteReq> Requests { get; } = new();
        public Dictionary<string, string> Results { get; } = new();

        /// <summary>ゲームの返事（Results）が届いた時刻。</summary>
        public Dictionary<string, DateTime> ResultAt { get; } = new();
        public string? Message { get; set; }

        /// <summary>走った実績（ダイヤグラム用）。先頭のゾーンが変わるたびに記録し、30分より古いものは捨てる。</summary>
        public List<(DateTime Utc, double Y, string? Row)> History { get; } = new();

        /// <summary>踏んだ軌道回路と、最後に踏んでいた時刻（在線位置の画面が、通ってきた枝で在線を塗るため。1分より古いものは捨てる）。</summary>
        public Dictionary<string, DateTime> SeenCircuits { get; } = new();

        /// <summary>
        /// 入換で入った駅間の回路（大道寺の XT・44T など、本線や分岐の回路で駅間のゾーンに入っているもの）→ その入換の駅のゾーン。
        /// ここにいる間は、駅を出たとはみなさない（XT へ入換しただけで、先の藤江の進路を取り始めないように）。
        /// </summary>
        public Dictionary<string, int> ShuntCircuits { get; } = new();

        /// <summary>始発の入換元（大道寺の X線など）にいるかを、列車を見つけたときに一度だけ確かめたか。</summary>
        public bool ShuntStartChecked { get; set; }

        /// <summary>駅ごとの実際の着時刻・発時刻（ロープレ時刻の運転日の秒）。着は停車なら止まった時、通過なら駅に入った時。発は出発信号機を踏んだ時。</summary>
        public Dictionary<string, double> ActualArr { get; } = new();
        public Dictionary<string, double> ActualDep { get; } = new();

        /// <summary>遅れ（秒）。出発信号機を踏んだときに、ダイヤの発車（通過）時刻との差で決める。ダイヤにない列車・まだ分からなければ null。</summary>
        public double? Delay { get; set; }
    }

    // =========================================================================================
    // 外から使うもの
    // =========================================================================================

    public sealed record Output(
        List<RouteCommand> Routes,
        List<RouteKey> Deletes,
        List<StationPlanView> Plan,
        bool CanRequestDeparture,
        string DepartureLabel,
        string? Message,
        List<RouteKey>? ConductorDeletes = null,
        string? PlanDetail = null);

    private readonly Dictionary<string, Output> _outputs = new();

    public Output? GetOutput(string connId)
    {
        lock (_lock) return _outputs.GetValueOrDefault(connId);
    }

    /// <summary>出発要求ボタン。駅に止まっている自列車の、その駅の出発進路を要求する。</summary>
    public string RequestDeparture(string connId)
    {
        lock (_lock)
        {
            if (!_runs.TryGetValue(connId, out var run)) return "在線共有モードの列車として認識されていません（列車番号・位置を確認してください）";
            if (DiaMode && run.Dia != null) return "ダイヤ運転中は、出発要求・入換要求はできません（時刻になると自動で開通します）";

            // 終着の列車の入換（引上げ）: 番線に入りきっていて、その番線から出る入換があれば、それを要求する
            if (ShuntOutStation(run) is { } end)
            {
                end.ShuntOutChoice = DefaultShuntOut(run, end);
                return $"{end.St.Name}: {end.Track}番線から{ShuntOutOptions(run, end)[end.ShuntOutChoice.Value].Label}への入換進路を要求しました";
            }

            // フリー運転で待避線に止まっている列車: 待避線からの出発進路を要求する（待避が終わっていなければ、終わってから開通）
            if (run.Stations.FirstOrDefault(s => LoopRequestPending(run, s)) is { } loop)
            {
                loop.DepartureRequested = true;
                return $"{loop.St.Name}: {loop.Track}番線（待避線）からの出発進路を要求しました" + (loop.Passing ? "（待避が終わってから開通します）" : "");
            }

            var st = run.Stations.FirstOrDefault(s => s.IsStart && !s.Done);
            if (st == null) return "駅に止まっている状態ではありません";
            if (st.ShuntFrom != null)
            {
                if (st.Track == null) return $"{st.St.Name}: 入換で入れる番線がありません";
                st.ShuntRequested = true;
                return $"{st.St.Name}: {st.ShuntFrom}から{st.Track}番線への入換進路を要求しました";
            }

            if (st.Terminal || st.DepartPath.Count == 0 && !ResolvePaths(run, st)) return $"{st.St.Name}: この列車はここから先へ出る進路がありません";
            st.DepartureRequested = true;
            return $"{st.St.Name}: 出発進路を要求しました";
        }
    }

    /// <summary>
    /// その駅で運転を終える列車（終着、または行き止まりの番線に入る）が、今いる番線から出られる入換（引上げ）の選択肢。
    /// 同じ行き先の候補（経由の違い）は、最初のものを代表にして1つにまとめる。実際の進路は <see cref="ShuntOutPath"/> で選ぶ。
    /// </summary>
    private static List<ShuntOutDef> ShuntOutOptions(TrainRun run, StationRun st) =>
        // 途中で列番が変わる駅（1178 は新野崎から 回1178）は、ダイヤの上では終着でも、その番線からそのまま出るので引上げはない
        (st.Terminal || IsTurnbackTrack(run, st)) && run.Dia?.Next?.StationId != st.St.Id && st.Track is { } t
            ? st.St.Routes[run.Dir].ShuntOuts.Where(o => o.From == t).GroupBy(o => o.Label).Select(g => g.First()).ToList()
            : new List<ShuntOutDef>();

    /// <summary>
    /// 入換（引上げ）の既定の選択肢。増結車になる列車（折り返しの列番が「連」で始まる。回730 → 連777）と、
    /// 切り離された列車（「解」で始まる。解2076C → 回2131）は、E線へ（E線で折り返して番線へ入る）。
    /// ダイヤの折返に引き上げる線（Via）が書かれていればその線。ほかは最初の選択肢。
    /// </summary>
    private int DefaultShuntOut(TrainRun run, StationRun st)
    {
        // ダイヤの折返に引き上げる線が書かれていれば、その線（大道寺の 951 → 1050 は X線）
        if (run.Dia != null && _data.Turns.TurnOf(run.Dia) is { Via: { Length: > 0 } via })
        {
            var i = ShuntOutOptions(run, st).FindIndex(o => o.Label == via);
            if (i >= 0) return i;
        }
        if (run.Train.StartsWith("解") || run.Dia != null && _data.Turns.TurnOf(run.Dia) is { Number: { } n } && n.StartsWith("連"))
        {
            var i = ShuntOutOptions(run, st).FindIndex(o => o.Label == "E線");
            if (i >= 0) return i;
        }
        return 0;
    }

    /// <summary>入換（引上げ）の選択肢の、実際に使う候補: 書いた順に、RequireFree の回路に他の列車がいないもの（なければ最後の候補）。</summary>
    private static ShuntOutDef ShuntOutPath(TrainRun run, StationRun st, ShuntOutDef option, Dictionary<string, TrainRun> occupied)
    {
        var alts = st.St.Routes[run.Dir].ShuntOuts.Where(o => o.From == option.From && o.Label == option.Label).ToList();
        return alts.FirstOrDefault(a => a.RequireFree.All(c => !occupied.TryGetValue(c, out var o) || o == run)) ?? alts[^1];
    }

    /// <summary>入換（引上げ）を要求できる駅: 列車が番線に入りきっていて、まだ入換を要求していない。なければ null。</summary>
    private static StationRun? ShuntOutStation(TrainRun run) =>
        run.Stations.FirstOrDefault(s => !s.Done && s.ShuntOutChoice == null && ShuntOutOptions(run, s).Count > 0
                                         && FullyInTrack(run, s));

    /// <summary>
    /// 取り消した進路の信号機。ゲームから進路が消えるまでのあいだ、すぐに停止を出させる
    /// （進路の取消はゲーム側で時間がかかったり、断られたりすることがあるため）。
    /// </summary>
    public List<string> StopSignals(string connId)
    {
        lock (_lock)
        {
            if (!_runs.TryGetValue(connId, out var run)) return new List<string>();
            return run.Requests.Where(r => !r.Released && r.CancelledUtc != null && r.Def.Signal.Length > 0)
                .Select(r => r.Def.Signal).Distinct().ToList();
        }
    }

    /// <summary>クライアントから届いた SetRoute の結果（"新野崎連動装置_10LD is OK" など）。</summary>
    public void ReportResult(string connId, string message)
    {
        lock (_lock)
        {
            if (!_runs.TryGetValue(connId, out var run)) return;
            var m = Regex.Match(message ?? "", @"^(.+?)_(\S+) is (.+)$");
            if (!m.Success) return;
            var key = $"{m.Groups[1].Value}/{m.Groups[2].Value}";
            var reply = m.Groups[3].Value;
            // ゲームが断った進路は記録に残す（サーバーが送らなかったのか、ゲームが断ったのかを、あとから区別できるように）
            // 返事は、ほかの列車の進路を写したもの（OtherRoutes）の分も届く。どちらか分かるように印を付け、
            // 鎖錠・競合の相手が分かるように、そのときゲームにあった同じ連動装置の進路も残す
            if (reply != "OK" && !reply.Contains("同一進路") && run.Results.GetValueOrDefault(key) != reply)
            {
                var (il, name) = (m.Groups[1].Value, m.Groups[2].Value);
                var mirrored = !run.Requests.Any(r => !r.Released && r.Def.Interlock == il && r.Def.Name == name)
                               && _mirroredTo.TryGetValue(connId, out var sent) && sent.Contains((il, name));
                var inGame = run.Player.GameRoutes.Where(g => g.Interlock == il).Select(g => g.Route).Order().ToList();
                RouteLogLine(DateTime.UtcNow, $"ゲームが進路を断った: {run.Train} {il} {name}{(mirrored ? "（ほかの列車の進路を写したもの）" : "")}「{reply}」"
                                              + $"（在線 {string.Join(" ", run.Player.TrackCircuits)}／ゲームの進路 {(inGame.Count > 0 ? string.Join(" ", inGame) : "なし")}）");
            }
            run.Results[key] = reply;
            run.ResultAt[key] = DateTime.UtcNow;
        }
    }

    // =========================================================================================
    // 毎周期の処理
    // =========================================================================================

    public void Tick()
    {
        var now = DateTime.UtcNow;
        var all = _players.Snapshot();
        var snapshot = all
            .Where(p => p.Mode == ShareMode.Shared && p.InGame && p.TrainCrewConnected && p.TrainNumber.Length > 0 && p.CoupledInto == null)
            .ToList();

        lock (_lock)
        {
            // 継続乗務: 運転画面を出て在線を残したら、その列車（進路・駅の状態）を残した在線へ移す。
            // 同じ番線から次のシナリオを始めたら、残した在線は消え、新しいシナリオの列番の列車として作り直す
            foreach (var g in snapshot.Where(p => p.ConnectionId.StartsWith(PlayerRegistry.GhostPrefix)))
            {
                var owner = g.ConnectionId[PlayerRegistry.GhostPrefix.Length..g.ConnectionId.LastIndexOf('/')];
                // 入換で動かしている（ゲームの中だけで走っている）持ち主は、いないものとする
                if (!_runs.ContainsKey(g.ConnectionId) && snapshot.All(p => p.ConnectionId != owner || p.ShuntMove == ShuntMoveState.Moving) && _runs.TryGetValue(owner, out var r0)
                    && r0.Player.TrainNumber == g.TrainNumber)
                    MoveRun(owner, g.ConnectionId, "継続乗務の在線へ移す", now);
            }

            // 引き継ぎ: 継続乗務の在線を、ほかの列車（自動運転の列車）が同じ列番で引き継いだら、その列車（進路・駅の状態）を移す
            foreach (var p in snapshot.Where(p => p.TakesOver != null && !_runs.ContainsKey(p.ConnectionId)))
            {
                var from = p.TakesOver!;
                if (snapshot.All(q => q.ConnectionId != from) && _runs.TryGetValue(from, out var r1) && r1.Player.TrainNumber == p.TrainNumber)
                    MoveRun(from, p.ConnectionId, "継続乗務の在線を自動運転の列車が引き継いだ", now);
            }

            // 列車の入れ替わり
            foreach (var id in _runs.Keys.Where(id => snapshot.All(p => p.ConnectionId != id)).ToList())
            {
                RunLog(now, _runs[id], "扱いを外しました（参加者の一覧からいなくなった）");
                ForgetArrived(_runs[id], now);
                _runs.Remove(id);
            }
            foreach (var id in _mirroredTo.Keys.Where(id => !_runs.ContainsKey(id)).ToList()) _mirroredTo.Remove(id);
            _mirrorLeftoverLogged.RemoveWhere(x => !_runs.ContainsKey(x.Conn));
            // 列番の読み替え（途中駅で列番が変わる列車）は、運転画面を出た・ゲームの列番が変わったら忘れる
            foreach (var id in _renumber.Keys.Where(id => snapshot.All(p => p.ConnectionId != id || p.TrainNumber != _renumber[id].From)).ToList())
                _renumber.Remove(id);
            _appear.Clear();
            _shuntMoving.Clear();
            foreach (var id in _assigned.Keys.Where(id => snapshot.All(p => p.ConnectionId != id)).ToList()) _assigned.Remove(id);
            foreach (var p in snapshot)
            {
                // 入換で動かしている列車（ゲームの中だけで X線へ走っている）: サーバーの列車にしない。
                // この人のゲームにだけ、走るのに要る進路（藤江13L、E線は大道寺32LE）を送る（BuildOutputs）
                if (p.ShuntMove == ShuntMoveState.Moving && PlayerRegistry.ShuntSpotOf(p.ShuntSpot) is { } shuntSpot)
                {
                    if (_runs.Remove(p.ConnectionId, out var was)) RunLog(now, was, "扱いを外しました（入換で、ゲームの中だけで動かす）");
                    _assigned.Remove(p.ConnectionId);
                    _shuntMoving[p.ConnectionId] = (shuntSpot, p.AppearReason ?? "");
                    continue;
                }

                // サーバーで使う列番: 読み替えていれば、読み替えた先（ゲームから届く列番は元のまま）。
                // 同じ列番の列車がほかにいれば、2本目以降に接尾辞 X・Y・Z を付けて呼び分ける
                var requested = _renumber.TryGetValue(p.ConnectionId, out var rn) ? rn.To : p.TrainNumber;
                var number = UniqueNumber(p.ConnectionId, requested);
                if (_runs.TryGetValue(p.ConnectionId, out var existing) && existing.Train == number)
                {
                    existing.Player = p;
                    continue;
                }

                // 出現待ち: 継続乗務の開始番線が違う、または他の列車がいる・他の列車の進路が開通している軌道回路に現れた列車は、
                // その回路が空く（先の列車が抜ける）まで、サーバーの列車として扱わない（進路を作らず、在線位置にも出さない）
                if (existing != null)
                {
                    RunLog(now, existing, $"扱いを外しました（列番が {number} になったので、その列番の列車として作り直す）");
                    ForgetArrived(existing, now);
                }
                _runs.Remove(p.ConnectionId);
                if (p.AppearReason != null)
                {
                    _appear[p.ConnectionId] = (number, p.AppearReason);
                    continue;
                }

                if (AppearBlocker(p) is { } blocker)
                {
                    _appear[p.ConnectionId] = (number, $"{blocker.Train} がいる・{blocker.Train} の進路がある軌道回路に現れました（{blocker.Train} が抜けたら始まります）");
                    if (_appearLogged.Add((p.ConnectionId, number)))
                        RouteLogLine(now, $"出現待ち: {number}（{string.Join(" ", p.TrackCircuits)}）に {blocker.Train} の在線・進路（{string.Join(" ", blocker.Requests.Where(q => !q.Released && !q.Entered && q.CancelledUtc == null).Select(q => q.Def.Name))}）");
                    continue;
                }

                if (TrainInfo(number) is not { } info) continue;
                var dia = _data.DiaOf(number) ?? _data.DiaOf(requested); // 接尾辞を付けた列車は、元の列番のダイヤで走る
                NoteSeen(number);
                _runs[p.ConnectionId] = new TrainRun
                {
                    ConnId = p.ConnectionId, Train = number, GameTrain = requested, NumberKind = info.Type, Type = dia?.Type ?? info.Type, Dir = info.Dir, Player = p,
                    Dia = dia, Indicator = dia != null ? IndicatorOf(dia.TypeLabel) : ""
                };
            }

            // 臨時停車で時刻を変えた・戻した列車は、新しい時刻の表を読む（停車駅の一覧はそのまま。時刻だけ変わる）
            var diaNow = _data.Dia;
            foreach (var run in _runs.Values)
                if (run.Dia is { } rd && diaNow.TryGetValue(rd.Number, out var cur) && !ReferenceEquals(cur, rd))
                    run.Dia = cur;

            foreach (var run in _runs.Values) UpdatePosition(run);

            // 連結: 増結車（ユーザーが運転している 連777 など）のいる軌道回路に親の列車（777）が入ったら、増結車の在線を消す
            _couplingDone.Clear();
            foreach (var p in all.Where(p => p.CoupledInto != null)) _couplingDone[p.ConnectionId] = p.CoupledInto!;
            CoupleUserCars(now);

            // 他の列車がいる軌道回路（回路 → 列番）
            var occupied = new Dictionary<string, TrainRun>();
            foreach (var run in _runs.Values)
            foreach (var c in run.Player.TrackCircuits)
                occupied[c] = run;

            foreach (var run in _runs.Values.Where(r => r.HasPosition))
            {
                if (!run.Initialized) Initialize(run);
                else RefreshStops(run, now);
                UpdateStations(run, now);

                // 途中駅で列番が変わる列車（ダイヤの「次の列番」）: その駅の番線に入りきって止まったら、次の列番に読み替える。
                // 次の周期から、その列番の列車（その駅の始発）として、その列番のダイヤで進路を作る
                if (run.Dia?.Next is { } next && run.Player.Speed < 1
                    && run.Stations.FirstOrDefault(s => s.St.Id == next.StationId && !s.Done) is { } ns && FullyInTrack(run, ns)
                    && TrainInfo(next.Number) != null)
                {
                    _renumber[run.ConnId] = (run.Player.TrainNumber, next.Number);
                    _log.Note($"列番の読み替え: {run.Train} → {next.Number}（{ns.St.Name}に到着）");
                }
                // 駅を抜けた列車は、その駅の出発順序と、手前の単線区間の走行順から外す
                for (var i = 0; i < run.Stations.Count; i++)
                {
                    if (!run.Stations[i].Done) continue;
                    _central.RemoveFromOrder(run.Stations[i].St.Id, run.Train);
                    if (i > 0) _central.RemoveFromSectionOrder(run.Stations[i - 1].St.Id, run.Stations[i].St.Id, run.Train);
                }
            }

            SyncOrders();
            RefreshOrderPlan(now);

            // 進路の要求（手前の駅を通過した順）
            var pending = _runs.Values
                .SelectMany(r => r.Stations.Where(s => !s.Done).Select(s => (Run: r, St: s)))
                // 急に現れた列車（始発）は、現れた時刻の順に並べる。すでに走っている列車の方が先
                .OrderBy(x => x.St.IsStart ? x.St.HereUtc ?? now : x.St.TriggeredUtc)
                .ToList();
            foreach (var (run, st) in pending) Process(run, st, occupied, now);

            // これから進路を取る順番（取れずに待っているもの。処理した順）
            _queue = pending.Where(x => !x.St.Done).SelectMany(x => new[]
                {
                    (x.Run, x.St, Kind: x.St.Track == null ? "通過" : "場内", State: x.St.Arrive, Wait: x.St.ArriveWait),
                    (x.Run, x.St, Kind: "出発", State: x.St.Depart, Wait: x.St.DepartWait)
                })
                .Where(q => q.State == GroupState.Waiting)
                .Select(q => (object)new { train = q.Run.Train, station = q.St.St.Name, kind = q.Kind, wait = q.Wait }).ToList();
            UpdateLevers();

            BuildOutputs(now);
        }
    }

    /// <summary>
    /// 単線区間の方向てこ（区間ごとの向き）。その区間に列車がいる、または区間へ出る出発進路を要求している列車がいれば、
    /// その列車の向きに鎖錠する（Locked）。誰も使っていなければ、最後の向きのまま、鎖錠なし。
    /// 鎖錠されている間、反対向きの出発は開通させない（SingleTrackHold が、区間の対向列車・相手の駅の対向の出発進路で待たせる）。
    /// </summary>
    /// <summary>
    /// 出発信号の進路が出ていく単線区間（SingleTrackSections の組）と、その向き。単線区間へ出ない進路・出発信号でない進路なら null。
    /// 向きは進路の種別（下り出発・上り出発）から決める。手動進路構成で出した出発信号も、これで方向てこ・対向の判断に入る
    /// </summary>
    private ((string, string) Section, Dir Dir)? DepartSection(RouteDef def, Dir fallback)
    {
        if (!def.IsDepartureSignal) return null;
        var dir = def.Category.StartsWith("上り") ? Dir.Up : def.Category.StartsWith("下り") ? Dir.Down : fallback;
        var z = _data.Line.ZoneOfStation(def.StationId);
        if (z < 0) return null;
        foreach (var sec in _data.SingleTrackSections)
        {
            var other = sec.Item1 == def.StationId ? sec.Item2 : sec.Item2 == def.StationId ? sec.Item1 : null;
            if (other == null) continue;
            if ((_data.Line.ZoneOfStation(other) - z) * dir.Step() > 0) return (sec, dir);
        }

        return null;
    }

    private void UpdateLevers()
    {
        var panel = _central.PanelSectionDirs();
        var panelRoutes = _central.PanelRoutes();
        foreach (var (a, b) in _data.SingleTrackSections)
        {
            var (za, zb) = (_data.Line.ZoneOfStation(a), _data.Line.ZoneOfStation(b));
            var (lo, hi) = (Math.Min(za, zb), Math.Max(za, zb));
            Dir? use = null;
            // 信号制御盤の方向てこで駅扱いにしている区間: 区間に列車がいなければ、てこの向きにする（区間方向が変わる）。
            // 列車がいる間はその列車の向きのまま。出発進路の要求では向きを変えない（反対向きの出発は方向てこが止める）
            Dir? fixedDir = panel.TryGetValue((a, b), out var pd) || panel.TryGetValue((b, a), out pd) ? pd : null;
            // 区間の中にいる列車を先に見る。出発を要求している列車（向かいの駅で待っている列車など）より先に決めないと、
            // 列車を見る順番しだいで、区間を走っている列車と逆向きに倒れ、その列車が閉塞信号機の停止で動けなくなる
            var inSection = _runs.Values.FirstOrDefault(r => r.HasPosition && r.Player.Zones.Any(z => z > lo && z < hi));
            if (inSection != null) use = inSection.Dir;
            else if (fixedDir == null)
                foreach (var run in _runs.Values.Where(r => r.HasPosition))
                {
                    // 区間へ出る出発進路を要求している（自動・駅扱い・手動進路構成のどれでも）
                    var leaving = run.Requests.Where(r => !r.Released && r.CancelledUtc == null)
                        .Select(r => DepartSection(r.Def, run.Dir)).FirstOrDefault(ds => ds is { } d && d.Section == (a, b));
                    if (leaving is not { } go) continue;
                    use = go.Dir;
                    break;
                }

            // 信号制御盤で取った出発進路（集中のまま盤で取ったもの）も、自動の出発と同じく区間の向きを倒す。
            // そうしないと、区間の向きが逆のままで、ゲームの出発信号が方向てこで停止に押さえられる
            if (use == null && fixedDir == null)
                foreach (var rc in panelRoutes)
                    if (_data.Routes.TryGetValue((rc.Interlocking, rc.RouteName), out var pdef) && DepartSection(pdef, Dir.Up) is { } pds && pds.Section == (a, b))
                    { use = pds.Dir; break; }

            if (use is { } dir) _levers[(a, b)] = (dir, true);
            else if (fixedDir is { } f) _levers[(a, b)] = (f, true);
            else if (_levers.TryGetValue((a, b), out var cur)) _levers[(a, b)] = (cur.Dir, false);
            if (_levers.TryGetValue((a, b), out var lvNow)) _central.SetSectionDir((a, b), lvNow.Dir);
        }
    }

    private List<object> _queue = new();

    /// <summary>出現待ちの列車（接続ID → 列番・理由）。毎周期作り直す。</summary>
    private readonly Dictionary<string, (string Train, string Reason)> _appear = new();

    /// <summary>入換で動かしている列車（ゲームの中だけで走っている。接続ID → 場所・お知らせ）。</summary>
    private readonly Dictionary<string, (ShuntSpot Spot, string Message)> _shuntMoving = new();

    /// <summary>入換で動かしている列車へ送る進路の指示の番号（同じ進路は同じ番号のまま送る）。</summary>
    private readonly Dictionary<(string Conn, string Interlock, string Route), long> _shuntIds = new();
    private readonly HashSet<(string Conn, string Train)> _appearLogged = new();

    /// <summary>出現待ちの列車（管理画面・ダイヤグラムの設定画面用）。</summary>
    public List<object> AppearView()
    {
        lock (_lock) return _appear.Values.Select(a => (object)new { train = a.Train, reason = a.Reason }).ToList();
    }

    /// <summary>
    /// 現れた列車（まだサーバーの列車になっていない）の踏んでいる軌道回路に、他の列車がいる、
    /// または他の列車がまだ入っていない（これから通る）開通中・要求中の進路がかかっていれば、その列車。
    /// </summary>
    private TrainRun? AppearBlocker(PlayerInfo p)
    {
        var mine = p.TrackCircuits.ToHashSet();
        if (mine.Count == 0) return null;
        foreach (var r in _runs.Values)
        {
            if (r.ConnId == p.ConnectionId) continue;
            // 切り離した車両（解874・そこから折り返した 回933）は、元の列車（874・975）と同じ番線に現れる
            if ((SplitOriginOf(RailData.NormalizeTrain(p.TrainNumber)) != null) != (SplitOriginOf(r.Train) != null)) continue;
            if (r.Player.TrackCircuits.Any(mine.Contains)) return r;
            // 進路に入ったあとも、列車がまだ通っていない回路（番線に入りきるまでの番線など）にいれば待つ。入った進路を見ないと、
            // 遅れて館浜に入ってくる列車が場内に入ったとたん、その番線に出した自動運転の列車が始まって出発進路を要求し、入ってくる列車が番線に入れない
            if (r.Requests.Any(q => !q.Released && q.CancelledUtc == null && LiveCircuits(r, q).Any(mine.Contains))) return r;
        }

        return null;
    }

    /// <summary>列車が要求している・開いている進路の、まだ使う軌道回路（自動運転の列車を出す番線を選ぶため。ゲームにまだ開いていない進路も含む）。</summary>
    public HashSet<string> ReservedCircuits()
    {
        lock (_lock)
            return _runs.Values.SelectMany(r => r.Requests.Where(q => !q.Released && q.CancelledUtc == null).SelectMany(q => LiveCircuits(r, q))).ToHashSet();
    }

    /// <summary>列車（進路・駅の状態）を別の接続IDへ移す（継続乗務）。</summary>
    private void MoveRun(string from, string to, string why, DateTime now)
    {
        if (!_runs.Remove(from, out var run)) return;
        RunLog(now, run, $"扱いを移しました（{why}）");
        run.ConnId = to;
        _runs[to] = run;
        if (_renumber.Remove(from, out var rn)) _renumber[to] = rn;
        if (_assigned.Remove(from, out var asg)) _assigned[to] = asg;
    }

    /// <summary>接続ごとに割り振ったサーバーの列番（ゲームの列番 → サーバーの列番）。ゲームの列番が変わるまで同じものを使う。</summary>
    private readonly Dictionary<string, (string Game, string Server)> _assigned = new();

    /// <summary>
    /// 同じ列番の列車がほかにいるとき、2本目以降のサーバーの列番を決める（列車番号の付け方の「接尾辞」: 2本目 X、3本目 Y、4本目 Z）。
    /// 例: 3643K が4本 → 3643K・3643KX・3643KY・3643KZ。先に走っていた列車が元の列番のまま。
    /// ダイヤに別の列車としてある列番（1683CX など）と、ほかの接続が使っている列番は使わない。全部ふさがっていれば「#2」などを付ける。
    /// 進路・集中扱いの指示・順序はサーバーの列番で別々に扱い、ゲームへ送る進路の列番は元の列番のまま。
    /// </summary>
    private string UniqueNumber(string conn, string requested)
    {
        if (_assigned.TryGetValue(conn, out var a) && a.Game == requested) return a.Server;
        var used = _runs.Values.Where(r => r.ConnId != conn).Select(r => r.Train)
            .Concat(_assigned.Where(kv => kv.Key != conn).Select(kv => kv.Value.Server)).ToHashSet();
        var server = requested;
        if (used.Contains(requested))
        {
            var stem = Regex.Replace(requested, "[XYZ]$", "");
            server = new[] { "X", "Y", "Z" }.Select(x => stem + x)
                         .FirstOrDefault(c => c != requested && !used.Contains(c) && _data.DiaOf(c) == null)
                     ?? Enumerable.Range(2, 98).Select(i => $"{requested}#{i}").First(c => !used.Contains(c));
            _log.Note($"同じ列番: {requested} がすでに走っているため、サーバーでは {server} として扱います");
        }

        _assigned[conn] = (requested, server);
        return server;
    }
    private readonly List<string> _routeLog = new();

    /// <summary>これから進路を取る順番（取れずに待っている場内・出発。処理する順）。</summary>
    public List<object> QueueView() { lock (_lock) return _queue.ToList(); }

    /// <summary>進路を要求した記録（新しい順）。</summary>
    public List<string> RouteLogView() { lock (_lock) return Enumerable.Reverse(_routeLog).Take(300).ToList(); }

    /// <summary>進路を要求したことを記録する（画面用に直近500件。あとから調べられるよう、日ごとのファイル logs/routes-yyyyMMdd.log にも書く）。</summary>
    private void RouteLog(DateTime now, TrainRun run, StationRun st, string kind, List<RouteDef> path) =>
        RouteLogLine(now, $"{run.Train} {st.St.Name} {kind} {string.Join("→", Enumerable.Reverse(path).Select(r => r.Name))}");

    /// <summary>
    /// 入換（始発の入換・終着の引上げ）を待っている理由が変わったら、進路の記録に残す。入換は駅の着発と違って、待ちが長くなっても画面にしか出ないため。
    /// 同じ理由が続くあいだは書かない。
    /// </summary>
    private void ShuntWaitLog(DateTime now, TrainRun run, StationRun st, string kind, string before, string wait)
    {
        if (wait == before) return;
        RouteLogLine(now, $"{run.Train} {st.St.Name} {kind}待ち {wait}");
    }

    private void RouteLogLine(DateTime now, string text)
    {
        var line = $"{_clock.TextAt(now)} {text}"; // 画面の記録はロープレ時刻（ファイルには実際の日時も書く）
        _routeLog.Add(line);
        if (_routeLog.Count > 500) _routeLog.RemoveAt(0);
        // 書けなくても進路の判断は続ける（DailyLog は例外を出さない）
        var local = now.ToLocalTime();
        _routeFile.Append(now, $"{local:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}",
            () => $"{local:yyyy-MM-dd HH:mm:ss} この日の進路の記録が {_routeFile.MaxMb}MB を超えたので、ここで書くのをやめます{Environment.NewLine}");
    }

    /// <summary>進路の記録のファイル（logs/routes-yyyyMMdd.log）。ふだんは1日 300〜400KB。何かが繰り返し書き続けても、1日 20MB で止める。</summary>
    private readonly DailyLog _routeFile = new("routes", 20L * 1024 * 1024);

    /// <summary>
    /// 単線区間の走行順と、その区間へ出る駅の出発順序をそろえる（どちらかを変えたら、もう片方も変わる）。
    /// 新しく変えた方に合わせる。そろえるのは「その駅からその区間へ出る列車」どうしの並びだけ。
    /// </summary>
    private void SyncOrders()
    {
        foreach (var (a, b) in _data.SingleTrackSections)
        {
            if (_central.SectionOrder(a, b) is not { } sec) continue;
            foreach (var x in new[] { a, b })
            {
                var other = x == a ? b : a;
                var deps = sec.Where(t => RunByTrain(t) is { } r && r.Stations.FirstOrDefault(s => s.St.Id == x && !s.Done) is { } s
                                          && !IsCutOff(r, s) && !s.Terminal && NextControlStation(r, s) == other).ToList();
                if (deps.Count == 0) continue;
                var dir = RunByTrain(deps[0])!.Dir; // x から other へ出る列車は、みな同じ向き
                var order = _central.Order(x, dir)?.ToList();
                var secStamp = _central.SectionStamp(a, b);
                var stStamp = _central.OrderStamp(x, dir);
                if (order == null || secStamp > stStamp)
                {
                    var merged = Reorder(order ?? new List<string>(), deps);
                    if (order == null || !merged.SequenceEqual(order)) { _central.SyncOrder(x, dir, merged, secStamp); _orderSyncVersion++; }
                }
                else if (stStamp > secStamp)
                {
                    var inStation = order.Where(deps.Contains).ToList();
                    if (inStation.Count < 2) continue;
                    var merged = Reorder(sec.ToList(), inStation);
                    if (!merged.SequenceEqual(sec)) { _central.SyncSectionOrder(a, b, merged, stStamp); _orderSyncVersion++; }
                }
            }
        }
    }

    /// <summary>list の中で order の列車がいる位置はそのままに、その並びを order の順にする（list にない列車は末尾に足す）。</summary>
    private static List<string> Reorder(List<string> list, List<string> order)
    {
        var res = list.ToList();
        res.AddRange(order.Where(t => !res.Contains(t)));
        var idx = res.Select((t, i) => (t, i)).Where(p => order.Contains(p.t)).Select(p => p.i).ToList();
        for (var k = 0; k < idx.Count; k++) res[idx[k]] = order[k];
        return res;
    }

    /// <summary>単線区間の閉塞信号機（とその中継信号機）: どの区間の、どちら向きの信号機か。信号のデータの方向てこから作る。</summary>
    private List<((string A, string B) Section, Dir Dir, string Name)>? _leverSignals;

    private List<((string A, string B) Section, Dir Dir, string Name)> LeverSignals()
    {
        if (_leverSignals != null) return _leverSignals;
        var list = new List<((string A, string B) Section, Dir Dir, string Name)>();
        foreach (var sig in _data.Signals.Values.Where(s => s.DirectionRoutes.Count > 0 && s.Direction != null))
        {
            // 方向てこの名前（TH65_14F など）の駅ID から、単線区間を引く
            var ids = sig.DirectionRoutes.Select(n => n.Contains('_') ? n[..n.IndexOf('_')] : n).Distinct().ToList();
            foreach (var sec in _data.SingleTrackSections.Where(s => ids.Contains(s.Item1) && ids.Contains(s.Item2)))
                list.Add((sec, sig.Direction!.Value, sig.Name));
        }

        // 中継信号機は、主の信号機と同じ扱い
        foreach (var rep in _data.Signals.Values.Where(s => s.IsRepeater))
        foreach (var main in list.Where(x => rep.Next.Contains(x.Name)).ToList())
            if (list.All(x => x.Name != rep.Name)) list.Add((main.Section, main.Dir, rep.Name));
        return _leverSignals = list;
    }

    /// <summary>
    /// 方向てこによって停止に固定する閉塞信号機。単線区間の閉塞信号機は、方向てこがその信号機の向きのときだけ現示を出す。
    /// 逆向きのとき・まだ一度も向きが決まっていないときは、停止にする（ゲームの閉塞信号機は方向てこを見ないので、
    /// 固定しないと、対向列車が区間にいるのに進行を出してしまう）。全員のゲームに同じものを送る。
    /// </summary>
    /// <summary>
    /// 現示時素のある進路（RouteRules.json の clearTimes。踏切がすぐ先にある出発信号機など）の信号機で、
    /// ゲームで開通してからまだその秒数がたっていないもの。全員のゲームで停止にしておく。
    /// </summary>
    public List<string> ClearTimeStopSignals()
    {
        // 信号制御盤の進路で、現示時素（盤の駅データの clearTime）の間のもの。時素は盤が数えている
        var panel = _central.PanelHeldRoutes()
            .Select(h => _data.Routes.TryGetValue(h, out var def) ? def.Signal : "")
            .Where(s => s.Length > 0).ToList();
        if (_data.ClearTimes.Count == 0) return panel.Distinct().ToList();
        var now = DateTime.UtcNow;
        lock (_lock)
            return _runs.Values.SelectMany(run => run.Requests)
                .Where(r => !r.Released && r.CancelledUtc == null && !r.Entered && r.SeenUtc is { } seen && r.Def.Signal.Length > 0
                            && _data.ClearTimes.TryGetValue($"{r.Def.Interlock}/{r.Def.Name}", out var sec) && now - seen < TimeSpan.FromSeconds(sec))
                .Select(r => r.Def.Signal).Concat(panel).Distinct().ToList();
    }

    public List<string> LeverStopSignals()
    {
        lock (_lock)
            return LeverSignals().Where(x => !_levers.TryGetValue(x.Section, out var lv) || lv.Dir != x.Dir).Select(x => x.Name).ToList();
    }

    /// <summary>走っている列車の遅れ（秒）。走っていない・まだ分からなければ null。</summary>
    public double? DelayOf(string train)
    {
        lock (_lock) return RunByTrain(train)?.Delay;
    }

    /// <summary>列車の向き（走っていればその列車、いなければ列番から）。分からなければ null。</summary>
    public Dir? DirOfTrain(string train)
    {
        lock (_lock) return RunByTrain(train)?.Dir ?? TrainInfo(train)?.Dir;
    }

    /// <summary>
    /// 折返が「入換」でも、着いた番線から引上げの入換（shuntOut）がなく、次の列番の始発の入換がその番線から出るなら、
    /// 着いた番線でそのまま列番を変える（江ノ原信号場の10番線に下りで着いて、次の列番が 10→13 の入換で出区する列車など）。
    /// 入換は、次の列番の始発の入換として、入換時刻に合わせて開ける。
    /// </summary>
    public bool TurnsInPlace(DiaTrain dia)
    {
        if (_data.Turns.TurnOf(dia) is not { Kind: "折返", How: "入換", Number: { Length: > 0 } next } || _data.DiaOf(next) is not { } nd) return false;
        var end = dia.Stops.LastOrDefault();
        if (end?.StationId is not { } sid || end.Track is not { } track || !_data.Stations.TryGetValue(sid, out var st)) return false;
        if (nd.Stops.FirstOrDefault() is not { } first || first.StationId != sid) return false;
        if (TrainInfo(dia.Number)?.Dir is not { } dir || TrainInfo(nd.Number)?.Dir is not { } nextDir) return false;
        if (st.Routes[dir].ShuntOuts.Any(o => o.From == track)) return false;
        return st.Routes[nextDir].Shunts.Any(s => s.From == track && (first.Track is not { } to || s.To == to));
    }

    /// <summary>
    /// 列番から種別と向きを読む（列車番号の付け方: 接頭辞 + 始発時刻2桁 + 運番下2桁 + 社局 + 種別 + 接尾辞）。
    /// ・呼び分けの「#2」など、接尾辞（X・Y・Z）は除く
    /// ・向き: 数字の最後の桁（奇数＝下り、偶数＝上り）
    /// ・種別: 接頭辞が「回」（定期回送）「臨回」（臨時の回送）「試」（試運転）なら回送。それ以外（「臨」を含む）は、数字のあとの文字の最後（社局 T・S の後ろ）
    ///   A＝特急、B＝急行、C＝準急、D＝区急、K＝快急、なし（社局 T・S だけも）＝普通
    /// </summary>
    internal static (string Type, Dir Dir)? TrainInfo(string train)
    {
        var body = Regex.Replace(train.Trim(), @"#\d+$", "");
        body = Regex.Replace(body, "[XYZ]$", "", RegexOptions.IgnoreCase);
        var digits = Regex.Match(body, @"(\d+)(?!.*\d)");
        if (!digits.Success) return null;
        var dir = int.Parse(digits.Groups[1].Value[^1..]) % 2 == 1 ? Dir.Down : Dir.Up;
        string type;
        if (body.StartsWith("回") || body.StartsWith("臨回") || body.StartsWith("試") || body.StartsWith("連") || body.StartsWith("解")) type = "回送"; // 臨回1352（特発臨の回送）・連777（増結車）・解2076C（切り離した車両）
        else if (body.Length == 0) type = "普通";
        else
            type = char.ToUpperInvariant(body[^1]) switch
            {
                'A' => "特急", 'B' => "急行", 'C' => "準急", 'D' => "区急", 'K' => "快急", _ => "普通"
            };
        return (type, dir);
    }

    private static string RunType(TrainRun run) => run.Type == "回送" ? "普通" : run.Type;

    /// <summary>ダイヤの種別表記 → 出発信号機の列選の文字。</summary>
    private static string IndicatorOf(string label)
    {
        var name = label.Replace("臨時", "");
        if (Regex.IsMatch(name, @"^C特\d+$") && int.Parse(name[2..]) > 4) return "C特4";
        return name switch
        {
            "回送" or "A特" or "B特" or "C特1" or "C特2" or "C特3" or "C特4" or "D特" or "快速急行" or "急行" or "区急" or "準急" or "普通" => name,
            "試運転" => "回送",
            "C特" => "C特4",
            "特急" => "D特",
            "区間急行" => "区急",
            "快急" => "快速急行",
            _ => ""
        };
    }

    /// <summary>列車番号の確定（クライアントの「確定」ボタン）。ダイヤにある列車か。</summary>
    public TrainCheckResult CheckTrain(string train)
    {
        var name = RailData.NormalizeTrain(train);
        if (name.Length == 0) return new TrainCheckResult { Message = "列車番号が空です" };
        if (TrainInfo(name) == null) return new TrainCheckResult { Message = $"{name}: 列車番号の形が違います（数字が入っていません）" };
        if (_data.DiaOf(name) is { } dia)
            return new TrainCheckResult { Found = true, Message = $"{name}（{dia.TypeLabel}）: ダイヤの進路で構成します" };
        return new TrainCheckResult { Message = $"{name}: 進路情報がありません。種別進路で構成します" };
    }

    private void UpdatePosition(TrainRun run)
    {
        var seenAt = DateTime.UtcNow;
        foreach (var c in run.Player.TrackCircuits) run.SeenCircuits[c] = seenAt;
        foreach (var c in run.SeenCircuits.Where(kv => (seenAt - kv.Value).TotalSeconds > 60).Select(kv => kv.Key).ToList()) run.SeenCircuits.Remove(c);
        var zones = PositionZones(run);
        run.HasPosition = zones.Count > 0;
        if (!run.HasPosition) return;
        var prevFront = run.Front;
        run.Front = run.Dir == Dir.Down ? zones.Max() : zones.Min();
        // 先頭がゾーンに来た時刻（同じゾーンの2本の前後に使う。IsAheadOf）
        if (run.ZoneSinceUtc == null || run.Front != prevFront)
        {
            run.ZoneEntered = run.ZoneSinceUtc != null && (run.Front - prevFront) * run.Dir.Step() > 0;
            run.ZoneSinceUtc = seenAt;
        }
        run.Rear = run.Dir == Dir.Down ? zones.Min() : zones.Max();

        // 駅間のどこにいるか（停車駅の距離から）。分からなければ 0.5
        run.Fraction = 0.5;
        run.FractionKnown = false;
        if (run.Front % 2 == 1)
        {
            var a = _data.Line.StationAtZone(run.Front - run.Dir.Step());
            var b = _data.Line.StationAtZone(run.Front + run.Dir.Step());
            var da = DistanceOf(run.Player, a);
            var db = DistanceOf(run.Player, b);
            if (da is { } x && db is { } y && y > x)
                (run.Fraction, run.FractionKnown) = (Math.Clamp((run.Player.TraveledDistance - x) / (y - x), 0, 1), true);
        }

        // ダイヤグラム用の実績（駅のゾーンを目盛りにした位置）
        var now = DateTime.UtcNow;
        var pos = DiagramY(run);
        var row = SidingRow(run);
        if (run.History.Count == 0 || Math.Abs(run.History[^1].Y - pos) > 0.05 || run.History[^1].Row != row || (now - run.History[^1].Utc).TotalSeconds > 20)
            run.History.Add((now, pos, row));
        run.History.RemoveAll(h => (now - h.Utc).TotalMinutes > 30);
    }

    /// <summary>
    /// 列車の先頭・最後尾を決めるゾーン。入換の進路（入換信号機）で駅間の回路に入っている間は、その回路を入換の駅のゾーンとして数える。
    /// 大道寺の XT のように、入換の行き先が本線上（駅間のゾーン）にあると、そこへ入換しただけで駅を出たことになり、
    /// 先の駅（藤江）の進路を取り始めてしまうため。出発・場内などの列車の進路でその回路に入ったときは、ふつうに駅間とする。
    /// 入換の進路は、サーバーが要求したもの（駅扱い・手動進路）と、ゲームで開いているもの（ゲームで直接取ったもの）の両方を見る。
    /// 一度入換で入った回路は、入換の進路がゲームで解除されても、その回路群をすべて抜ける（駅に戻る）まで駅のものとする。
    /// </summary>
    private List<int> PositionZones(TrainRun run)
    {
        var p = run.Player;
        if (p.TrackCircuits.Count == 0) return p.Zones;

        // 列車を見つけたとき、ダイヤの始発駅の入換元（駅間のゾーンにある回路。大道寺の X線）にいれば、その回路と、
        // そこから番線への入換の進路の回路を、始発駅のものとして数える。引上げで X線に入ってから列番を変えた列車（951 → 1050）は、
        // 引上げの進路が前の列番のものなので、そのままでは大道寺を出たことになってしまうため
        if (!run.ShuntStartChecked)
        {
            run.ShuntStartChecked = true;
            if (run.Dia?.Stops.FirstOrDefault()?.StationId is { } sid && _data.Stations.TryGetValue(sid, out var sst)
                && _data.Line.ZoneOfStation(sid) is var sz && sz >= 0)
                foreach (var sh in sst.Routes[run.Dir].Shunts.Where(s => s.FromCircuit != null && p.TrackCircuits.Contains(s.FromCircuit)))
                foreach (var c in sh.Routes.Concat(sh.ThenRoutes).SelectMany(r => r.Circuits).Prepend(sh.FromCircuit!))
                    if (_data.Line.ZoneOfCircuit(c) is { } z && Math.Abs(z - sz) == 1)
                        run.ShuntCircuits[c] = sz;
        }

        var live = run.Requests.Where(r => !r.Released && r.CancelledUtc == null).Select(r => r.Def)
            .Concat(p.GameRoutes.Select(k => _data.Routes.GetValueOrDefault(k)).OfType<RouteDef>())
            .Where(d => d.Circuits.Any(p.TrackCircuits.Contains))
            .Distinct().ToList();

        // 入換の回路群をすべて抜けたら（駅に戻った・先へ行った）忘れる。列車の進路（出発・場内など）の回路は、入換ではない
        if (!run.ShuntCircuits.Keys.Any(p.TrackCircuits.Contains)) run.ShuntCircuits.Clear();
        foreach (var d in live.Where(d => !d.Kind.Contains("入換")))
        foreach (var c in d.Circuits)
            run.ShuntCircuits.Remove(c);

        foreach (var d in live.Where(d => d.Kind.Contains("入換")))
        {
            var stZone = _data.Line.ZoneOfStation(d.StationId);
            if (stZone < 0) continue;
            // 入換の進路の、駅のすぐ隣の駅間にある回路（まだ踏んでいない先の回路も、まとめて入れる）
            foreach (var c in d.Circuits)
                if (_data.Line.ZoneOfCircuit(c) is { } z && Math.Abs(z - stZone) == 1)
                    run.ShuntCircuits[c] = stZone;
        }

        if (run.ShuntCircuits.Count == 0 || !run.ShuntCircuits.Keys.Any(p.TrackCircuits.Contains)) return p.Zones;
        return p.TrackCircuits
            .Select(c => run.ShuntCircuits.TryGetValue(c, out var sz) ? sz : _data.Line.ZoneOfCircuit(c))
            .OfType<int>().Distinct().OrderBy(z => z).ToList();
    }

    /// <summary>ダイヤグラムの縦の位置（駅＝偶数のゾーン。駅間は、進んだ割合で按分）。</summary>
    /// <summary>
    /// ダイヤグラムの縦の位置。踏んでいる回路がすべて1つの駅の構内の回路（駅IDで始まる回路）なら、その駅にいるものとする
    /// （大道寺の 44T のような分岐の回路は駅間のゾーンに入っているが、入換で通るときに線が駅の外へ跳ねないように）。それ以外は PlotY。
    /// </summary>
    private double DiagramY(TrainRun run)
    {
        var circuits = run.Player.TrackCircuits;
        if (circuits.Count > 0 && circuits[0].IndexOf('_') is var i and > 0)
        {
            var sid = circuits[0][..i];
            var zone = _data.Line.ZoneOfStation(sid);
            if (zone >= 0 && circuits.All(c => c.StartsWith(sid + "_", StringComparison.Ordinal))) return zone;
        }

        return PlotY(run);
    }

    /// <summary>
    /// ダイヤグラムの段: 駅で、入換線など（Stations.json の diagramRows で軌道回路を書いた段）にいれば、その段の名前。
    /// 大道寺の XT のように段の回路が本線上（駅間のゾーン）にあるときは、入換でそこへ入った列車だけをその段に描く。
    /// 発着・通過で踏んだだけの列車まで段に入れると、線が X線へ跳ねてから番線へ戻ってしまう
    /// </summary>
    private string? SidingRow(TrainRun run)
    {
        if (DiagramY(run) is var y && y % 2 != 0 || _data.Line.StationAtZone((int)y) is not { } sid || !_data.Stations.TryGetValue(sid, out var st)) return null;
        return st.DiagramRows.FirstOrDefault(r => r.Circuit != null && run.Player.TrackCircuits.Contains(r.Circuit)
            && (_data.Line.ZoneOfCircuit(r.Circuit) is not { } z || z % 2 == 0 || run.ShuntCircuits.ContainsKey(r.Circuit))).Label;
    }

    private static double PlotY(TrainRun run)
    {
        if (run.Front % 2 == 0) return run.Front;
        var from = run.Front - run.Dir.Step();
        return from + 2 * run.Dir.Step() * run.Fraction;
    }

    private float? DistanceOf(PlayerInfo p, string? stationId)
    {
        if (stationId == null) return null;
        var name = _data.Line.StationName(stationId);
        return p.Stops.FirstOrDefault(s => s.Name == name)?.Distance;
    }

    private static string StopTypeLabel(string? gameStopType) => gameStopType switch
    {
        "StopForPassenger" => "停車",
        "StopForOperation" => "運転停車",
        _ => "通過"
    };

    /// <summary>位置が分かった最初の周期に、この先の駅の一覧を作る。</summary>
    private void Initialize(TrainRun run)
    {
        run.Initialized = true;
        run.StopsKey = StopsKeyOf(run.Player);
        var step = run.Dir.Step();
        var (terminalId, terminalZone) = TerminalOf(run);

        var stations = _data.ControlStations.ToList();
        if (run.Dir == Dir.Up) stations.Reverse();
        foreach (var st in stations)
        {
            var zone = _data.Line.ZoneOfStation(st.Id);
            if (zone < 0) continue;
            // 終着駅より先は扱わない
            if (terminalZone >= 0 && (zone - terminalZone) * step > 0) continue;
            run.Stations.Add(NewStationRun(run, st, zone, terminalId));
        }
        RunLog(DateTime.UtcNow, run, $"扱いを始めました（{StationsText(run)}）");
    }

    /// <summary>
    /// 列車の扱い（サーバーの列車と、その駅の一覧）の移り変わりを、進路の記録に残す。
    /// 扱いを始めた・作り直した・外した・駅の扱いを終えたときに書く。自動進路構成がある駅の進路をもう取らなくなったとき、
    /// どこで扱いが外れたかを、あとから追えるように。いる場所（区間と軌道回路）も書く。
    /// </summary>
    private void RunLog(DateTime now, TrainRun run, string what)
    {
        var where = _data.Line.DescribeZones(run.Player.Zones) ?? "位置不明";
        RouteLogLine(now, $"{run.Train} {what}（いる場所: {where} {string.Join(" ", run.Player.TrackCircuits)}）");
    }

    /// <summary>駅の一覧（扱いを終えた駅・始発・終着の印つき）。</summary>
    private static string StationsText(TrainRun run) =>
        run.Stations.Count == 0 ? "扱う駅なし"
            : string.Join(" ", run.Stations.Select(s => s.St.Name + (s.Done ? "［済］" : "") + (s.IsStart ? "［始発］" : "") + (s.Terminal ? "［終着］" : "")));

    /// <summary>
    /// 終着駅: ダイヤにあればダイヤ（江原検 → 江ノ原信号場 など、ゲームの停車駅にない入庫も分かる）、なければゲームの停車駅の最後。
    /// もう後ろ（今いる位置より手前）なら使わない。分からなければ (null, -1)（線区の端まで）。
    /// </summary>
    private (string? Id, int Zone) TerminalOf(TrainRun run)
    {
        var stops = run.Player.Stops;
        var terminalId = run.Dia != null ? run.Dia.TerminalId
            : stops.Count > 0 ? _data.Line.StationIdByName(stops[^1].Name) : null;
        var terminalZone = terminalId != null ? _data.Line.ZoneOfStation(terminalId) : -1;
        if (terminalZone < 0) return (null, -1);
        if ((terminalZone - run.Front) * run.Dir.Step() < 0 && !run.Player.Zones.Contains(terminalZone)) return (null, -1);
        return (terminalId, terminalZone);
    }

    /// <summary>ゲームの停車駅が変わったかを見る目安（駅の数と最後の駅）。</summary>
    private static string StopsKeyOf(PlayerInfo p) => p.Stops.Count == 0 ? "" : $"{p.Stops.Count}:{p.Stops[^1].Name}";

    private StationRun NewStationRun(TrainRun run, StationDef st, int zone, string? terminalId)
    {
        var step = run.Dir.Step();
        var sr = new StationRun
        {
            St = st,
            Zone = zone,
            // 停車・通過: ダイヤにあればダイヤ（手入力の列番がシナリオと違っても、その列番のとおりに）、なければゲーム
            StopType = run.Dia != null
                ? run.Dia.Stations.TryGetValue(st.Id, out var ds) ? ds.StopType : "通過"
                : StopTypeLabel(run.Player.Stops.FirstOrDefault(s => s.Name == st.Name)?.StopType),
            Terminal = st.Id == terminalId
        };

        if ((zone - run.Rear) * step < 0 && (zone - run.Front) * step < 0)
        {
            sr.Done = true; // もう過ぎた駅
            sr.DoneUtc = DateTime.UtcNow;
        }
        else if (run.Player.Zones.Contains(zone) || (zone - run.Front) * step <= 0)
        {
            sr.IsStart = true; // 今いる駅（出発は要求ボタンで）
            sr.StopType = sr.StopType == "通過" ? "停車" : sr.StopType;
        }

        sr.BaseStopType = sr.StopType;
        sr.BaseTerminal = sr.Terminal;
        return sr;
    }

    /// <summary>
    /// ダイヤにない列車は、終着と停車・通過をゲームの停車駅から決めるが、最初の周期にまだ停車駅が届いていないことがある
    /// （届く前に位置が分かった、継続乗務・自動運転からの引き継ぎで後から変わる など）。変わったら決め直す。毎周期は目安を比べるだけ。
    /// 空になったときは決め直さない（一時的に空で届いても、線区の端までに戻さない）。
    /// 終着より先の駅は一覧から外し（扱わない）、終着までで一覧にない駅は足す。停車・通過と終着は Base だけ変え、
    /// 実際の切り替え（進路の取り消し・作り直し）は ApplyStopOverride に任せる。
    /// </summary>
    private void RefreshStops(TrainRun run, DateTime now)
    {
        if (run.Dia != null) return;
        var key = StopsKeyOf(run.Player);
        if (key == run.StopsKey || key == "") return;
        run.StopsKey = key;

        var step = run.Dir.Step();
        var (terminalId, terminalZone) = TerminalOf(run);
        bool Beyond(int zone) => terminalZone >= 0 && (zone - terminalZone) * step > 0;

        // 終着より先の駅を外す（列車がもう入っている駅は外さない）
        var removed = new List<string>();
        for (var i = run.Stations.Count - 1; i >= 0; i--)
        {
            var st = run.Stations[i];
            if (st.Done || st.IsStart || !Beyond(st.Zone)) continue;
            if (run.Requests.Any(r => r.Station == st && r.Entered && !r.Released)) continue;
            foreach (var req in run.Requests.Where(r => r.Station == st && !r.Released && r.CancelledUtc == null)) req.CancelledUtc = now;
            _central.RemoveFromOrder(st.St.Id, run.Train);
            if (i > 0) _central.RemoveFromSectionOrder(run.Stations[i - 1].St.Id, st.St.Id, run.Train);
            run.Stations.RemoveAt(i);
            removed.Add(st.St.Name);
        }

        // 終着までで、一覧にない駅を足す（前に終着をもっと手前で決めていたとき）
        var added = new List<string>();
        foreach (var sd in _data.ControlStations)
        {
            var zone = _data.Line.ZoneOfStation(sd.Id);
            if (zone < 0 || Beyond(zone) || (zone - run.Front) * step < 0 || run.Stations.Any(s => s.St.Id == sd.Id)) continue;
            var sr = NewStationRun(run, sd, zone, terminalId);
            if (sr.Done) continue;
            var at = run.Stations.FindIndex(s => (s.Zone - zone) * step > 0);
            run.Stations.Insert(at < 0 ? run.Stations.Count : at, sr);
            added.Add(sd.Name);
        }

        // 残った駅の停車・通過と終着（Base）を、ゲームの停車駅に合わせる
        foreach (var st in run.Stations.Where(s => !s.Done))
        {
            var stop = StopTypeLabel(run.Player.Stops.FirstOrDefault(s => s.Name == st.St.Name)?.StopType);
            if (st.IsStart && stop == "通過") stop = "停車";
            st.BaseStopType = stop;
            st.BaseTerminal = st.St.Id == terminalId;
        }

        _log.Note($"停車駅の更新: {run.Train} 終着 {(terminalId != null ? _data.Line.StationName(terminalId) : "なし")}"
                  + (removed.Count > 0 ? $"（外した駅: {string.Join("・", removed)}）" : "")
                  + (added.Count > 0 ? $"（足した駅: {string.Join("・", added)}）" : ""));
        if (removed.Count > 0 || added.Count > 0)
            RunLog(now, run, "駅の一覧を作り直しました（ゲームの停車駅が変わった"
                             + (removed.Count > 0 ? $"。外した駅: {string.Join("・", removed)}" : "")
                             + (added.Count > 0 ? $"。足した駅: {string.Join("・", added)}" : "") + $"）（{StationsText(run)}）");
    }

    /// <summary>
    /// 駅の停車の変更: 列車別設定の臨時停車・臨時通過・打ち切り（"停車"・"通過"・"打ち切り"）。
    /// 列車別設定がなければ、催物用臨停設定で臨時停車になる駅は "停車"。どちらもなければ null。
    /// </summary>
    private string? StopChange(string train, DiaTrain? dia, string stationId) =>
        _central.StopOverride(train, stationId) ?? (dia != null && EventStops.Applies(dia, stationId) ? "停車" : null);

    /// <summary>
    /// 列車別設定の臨時停車・臨時通過・打ち切り（サーバーの扱いだけ。ゲームの停車駅は変わらない）。催物用臨停設定の臨時停車も入る。
    /// 変わったら、まだ列車が入っていなければ、その駅の進路を取り消して作り直す。入っていれば変えない。
    /// </summary>
    private void ApplyStopOverride(TrainRun run, StationRun st, DateTime now)
    {
        var ov = _central.StopOverride(run.Train, st.St.Id);
        // 運転士の「打ち切り・入換」は、列車別設定の打ち切りと同じに扱う（列車別設定があれば、そちらが先）
        var driverEnd = ov == null && DriverEndChosen(run, st);
        if (driverEnd) ov = "打ち切り";
        // 催物用臨停設定の臨時停車は、列車別設定・運転士の打ち切りがないときだけ
        var byEvent = ov == null && run.Dia != null && EventStops.Applies(run.Dia, st.St.Id);
        if (byEvent) ov = "停車";
        try
        {
            ApplyStopOverrideCore(run, st, ov, driverEnd, now, byEvent);
        }
        finally
        {
            st.DriverEnd = driverEnd && st.Terminal && !st.BaseTerminal;
        }
    }

    /// <summary>
    /// 運転士が、この駅で打ち切って入換（引上げ）をする番線を選んだか（運転画面の「◯番線（打ち切り・入換）」）。
    /// 終着でない駅でも、ダイヤにない列番でも、引上げのある番線なら入換できるように。ダイヤ運転中のダイヤの列車は使わない。
    /// 番線を確定したあとは、その番線のときだけ（集中扱いなどで別の番線になったら打ち切らない）。
    /// </summary>
    private bool DriverEndChosen(TrainRun run, StationRun st) =>
        !(DiaMode && run.Dia != null) && !st.BaseTerminal
        && run.Player.EndChoices.TryGetValue(st.St.Id, out var t)
        && run.Player.TrackChoices.TryGetValue(st.St.Id, out var c) && c == t
        && st.St.Routes[run.Dir].ShuntOuts.Any(o => o.From == t)
        && (st.Track == t || !st.Locked && !st.IsStart);

    /// <summary>運転画面に「◯番線（打ち切り・入換）」として出す番線: 選べる番線のうち、そこから引上げの入換がある番線。</summary>
    private List<int> EndOptions(TrainRun run, StationRun st) =>
        DiaMode && run.Dia != null || st.BaseTerminal
            ? new List<int>()
            : st.Options.Where(t => st.St.Routes[run.Dir].ShuntOuts.Any(o => o.From == t) && (!st.IsStart || st.Track == t)).ToList();

    private void ApplyStopOverrideCore(TrainRun run, StationRun st, string? ov, bool driverEnd, DateTime now, bool byEvent = false)
    {
        var stop = ov switch { "停車" or "打ち切り" => "停車", "通過" => "通過", _ => st.BaseStopType };
        if (st.IsStart && stop == "通過") stop = "停車";
        var terminal = ov == "打ち切り" || st.BaseTerminal;
        if (stop == st.StopType && terminal == st.Terminal) return;
        // 停車・通過が変わると番線も変わりうるので、場内から作り直す。終着だけが変わるなら、出発だけ作り直す
        // 場内にもう入っていれば、停車・通過は変えない（打ち切りの終着だけ反映する）
        if (stop != st.StopType && run.Requests.Any(r => r.Station == st && r.Entered && !r.Released && !r.Departure)) stop = st.StopType;
        var stopChanged = stop != st.StopType;
        if (!stopChanged && terminal == st.Terminal) return;
        if (run.Requests.Any(r => r.Station == st && r.Entered && !r.Released && (stopChanged || r.Departure))) return;
        foreach (var req in run.Requests.Where(r => r.Station == st && !r.Released && r.CancelledUtc == null && (stopChanged || r.Departure)))
            req.CancelledUtc = now;
        st.StopType = stop;
        st.Terminal = terminal;
        (st.Depart, st.DepartWait) = (GroupState.None, "");
        if (stopChanged)
        {
            st.Triggered = false;
            st.Locked = false;
            st.Partners.Clear();
            (st.Arrive, st.ArriveWait) = (GroupState.None, "");
            // Clear() は使わない（ResolvePaths で駅の進路の表のリストそのものを指しているので、消すとその番線の進路がどの列車でも取れなくなる）
            st.ArrivePath = new List<RouteDef>();
            st.DepartPath = new List<RouteDef>();
        }
        else ResolvePaths(run, st);
        _log.Note($"{(driverEnd || st.DriverEnd ? "運転士の打ち切り・入換" : byEvent ? "催物用臨停設定" : "列車別設定")}: {run.Train} {st.St.Name} {(ov ?? "指定解除")}（{stop}{(terminal ? "・終着" : "")}）");
    }

    /// <summary>
    /// 列車が駅を抜けた・終着に着いて走り終えたとき、その駅の指示（番線・抑止・開通指示・発車時刻。CentralControl.ForgetAt）を消す。
    /// 列車が現れたときにもう過ぎていた駅（作ったときから「済」の駅）の指示は消さない（手前からやり直す列車のために残す）。
    /// </summary>
    private void ForgetOrders(TrainRun run, StationRun st, DateTime now, string why)
    {
        if (_central.ForgetAt(run.Train, st.St.Id))
            RouteLogLine(now, $"{run.Train} {st.St.Name} の指示（番線・抑止・開通指示・発車時刻）を消しました（{why}）");
    }

    /// <summary>扱いを外す列車が、終着の番線に入りきっていれば（走り終えた）、終着の駅の指示を消す。途中でいなくなった列車の指示は残す。</summary>
    private void ForgetArrived(TrainRun run, DateTime now)
    {
        if (run.Stations.FirstOrDefault(s => !s.Done && s.Terminal && s.InTrackUtc != null) is { } end)
            ForgetOrders(run, end, now, "終着に着いて走り終えた");
    }

    private void UpdateStations(TrainRun run, DateTime now)
    {
        var step = run.Dir.Step();
        foreach (var st in run.Stations.Where(s => !s.Done))
        {
            // 最後尾が駅を抜けたら終わり
            // ただし、この駅で終わる列車（終着・行き止まりの番線）が、まだこの駅の場内の進路の回路にいるなら抜けていない
            // （回路の区間分けがずれていると、短い列車が場内の途中の回路だけを踏んだときに抜けたとみなされ、引上げが出なくなる）。
            // 駅間に入れてあるその駅の回路（大道寺の X線）へ引き上げただけのときも、まだ終わらない（X線から番線への入換を扱うため）
            if ((run.Rear - st.Zone) * step > 0
                && !((st.Terminal || IsTurnbackTrack(run, st))
                     && (st.ArrivePath.SelectMany(r => r.Circuits).Any(run.Player.TrackCircuits.Contains)
                         || run.Player.TrackCircuits.Count > 0 && run.Player.TrackCircuits.All(c => c.StartsWith(st.St.Id + "_", StringComparison.Ordinal)))))
            {
                st.Done = true;
                st.DoneUtc = now;
                ForgetOrders(run, st, now, "駅を抜けた");
                foreach (var req in run.Requests.Where(r => r.Station == st && !r.Seen)) req.Released = true;
                // 通過駅は数が多いので書かない（止まる駅・始発・終着だけ）
                if (st.IsStart || st.Terminal || st.StopType != "通過")
                    RunLog(now, run, $"{st.St.Name} の扱いを終えました（最後尾が駅を抜けたと判断）");
                continue;
            }

            ApplyStopOverride(run, st, now);

            // 手前の駅で行き止まりの番線に入る・打ち切りの指定がある（打ち切り）なら、この駅は扱わない。開通済みの進路があれば取り消す
            if (IsCutOff(run, st))
            {
                foreach (var req in run.Requests.Where(r => r.Station == st && !r.Released && r.CancelledUtc == null)) req.CancelledUtc = now;
                st.Triggered = false;
                st.Locked = false;
                st.Partners.Clear();
                (st.Arrive, st.ArriveWait) = (GroupState.None, "");
                (st.Depart, st.DepartWait) = (GroupState.None, "");
                if (!st.CutOffLogged)
                {
                    st.CutOffLogged = true;
                    RunLog(now, run, $"{st.St.Name} を扱わなくなりました（手前の駅で打ち切り）");
                }
                continue;
            }
            if (st.CutOffLogged)
            {
                st.CutOffLogged = false;
                RunLog(now, run, $"{st.St.Name} をまた扱います（打ち切りが外れた）");
            }

            if (_central.TakeManual(run.Train, st.St.Id) is { } manual) ApplyManual(run, st, manual, now);

            // ダイヤ運転中のダイヤの列車: 入換（引上線などからの始発・終着の引上げ）は、止まったら自動で要求する（運転士は要求できない）
            if (DiaMode && run.Dia != null && run.Player.Speed < StopSpeedKmh)
            {
                if (st.IsStart && st.ShuntFrom != null && !st.ShuntRequested) st.ShuntRequested = true;
                // 引上げは、スタフの折返が「入換」のときだけ（「発」なら、その番線から次の列番でそのまま出る）
                // 始発でも、その番線の回路にいるときだけ（つなぎ直しなどで列車を作り直したとき、もう引上線にいるのに、引上げの進路を取り直さないように）
                // 増結車（折返が「併結」）は、番線で連結を待つので引き上げない
                // 途中の駅で打ち切って、そこで次の列番（途中折返し・特発臨）に変わるのを待っている列車は引き上げない
                // （終点の後運用が「入換」でも、この駅で入換の進路を開けると、列番の差し替えを待つ間に別の信号機が開いてしまう）
                if (st.ShuntOutChoice == null && ShuntOutOptions(run, st).Count > 0 && FullyInTrack(run, st)
                    && _data.Turns.TurnOf(run.Dia) is not ({ Kind: "折返", How: not "入換" } or { Kind: "併結" }) && !run.Train.StartsWith("連")
                    && !WaitsRenumberAt(run, st))
                    st.ShuntOutChoice = DefaultShuntOut(run, st);
                // 引上げを選んだあとで、この駅で次の列番に変わることになった（特発臨を立てた・途中折返しにした）: まだ要求していなければやめる
                else if (st.ShuntOutChoice != null && st.ShuntOut != GroupState.Requested && WaitsRenumberAt(run, st))
                    (st.ShuntOutChoice, st.ShuntOut, st.ShuntOutWait) = (null, GroupState.None, "");
            }
            ApplyHold(run, st, now);

            // 駅に着いた時刻（先頭が構内に入った。始発は認識した時刻）
            if (st.HereUtc == null && (st.IsStart || (run.Front - st.Zone) * step >= 0))
            {
                st.HereUtc = now;
                if (!st.IsStart && st.StopType == "通過" && !st.DepartStop) run.ActualArr.TryAdd(st.St.Id, NowSec()); // 通過: 駅に入った時刻
            }

            // 始発の入換（駅扱い・手動進路を含む）でホームに入りきったら、入換は終わり。番線の確定と駅扱いの印を外し、
            // そこから先はふつうの始発として自動で扱う（駅扱いで確定したままだと、入換元にいる扱いが続き、出発の進路を自動で扱わない）
            if (st.IsStart && st.ShuntFrom != null && st.Track is { } shTo && st.St.TrackCircuit.TryGetValue(shTo, out var shTc)
                && run.Player.TrackCircuits.Contains(shTc)
                && run.Requests.Where(r => r.Station == st && !r.Departure && r.CancelledUtc == null).SelectMany(r => r.Def.Circuits)
                    .Where(c => c != shTc).All(c => !run.Player.TrackCircuits.Contains(c)))
            {
                (st.ShuntFrom, st.ShuntRequested, st.Shunt, st.ShuntWait, st.ForceShunt, st.ShuntVia) = (null, false, GroupState.None, "", false, null);
                (st.ShuntThenPath, st.ShuntThenAt, st.ShuntThen, st.ShuntThenWait) = (null, null, GroupState.None, "");
                (st.Locked, st.ManualUsed) = (false, false);
                _log.Note($"入換終わり: {run.Train} {st.St.Name} {shTo}番線に入りました（ここから先は自動で進路を扱います）");
            }

            if (!st.Locked) ChooseTrack(run, st);
            else if (CanRechoose(run, st))
            {
                // 単線区間の駅は、場内を開通させるまで番線を選び直す（対向列車が先に入った番線を避けて、空いている方へ）
                var old = st.Track;
                ChooseTrack(run, st);
                if (old != st.Track)
                {
                    ResolvePaths(run, st);
                    _log.Note($"番線変更: {run.Train} {st.St.Name} {old?.ToString() ?? "-"}番線 → {st.Track?.ToString() ?? "-"}番線（{st.TrackSource}）");
                }
            }

            // 待避線に着いた時刻（番線の回路に入った時点）
            if (st.LoopArrivedUtc == null && IsLoopTrack(run, st) && FullyInTrack(run, st))
                st.LoopArrivedUtc = now;
            if (st.InTrackUtc == null && FullyInTrack(run, st)) st.InTrackUtc = now;
            if (st.StoppedUtc == null && FullyInTrack(run, st) && run.Player.Speed < StopSpeedKmh)
            {
                st.StoppedUtc = now;
                if (!st.IsStart) run.ActualArr.TryAdd(st.St.Id, NowSec()); // 停車: 止まった時刻
            }

            // 2つ手前の駅（すべての駅）に先頭が着いたか（構内に入ったか）。始発のすぐ先などで、もう過ぎていればすぐ
            if (!st.Triggered && !st.IsStart)
            {
                var triggerZone = st.Zone - 2 * TriggerAhead(run, st) * step;
                // 取り始める信号機（ApproachTrigger）を先に過ぎたら、そこで番線を確定する（駅が近く、2つ手前の駅より手前になる所）
                if ((run.Front - triggerZone) * step >= 0 || ApproachOf(run, st) is { } ap && PassedApproach(run, st, ap)
                    || PassedFirstHomeAnyTrack(run, st))
                {
                    st.Triggered = true;
                    st.TriggeredUtc = now;
                    ChooseTrack(run, st);
                    DecidePassing(run, st, now);
                    st.Locked = true;
                    ResolvePaths(run, st);
                }
            }
        }

        // 集中扱いで、確定後に番線が変えられた駅（ダイヤグラム・管理画面から）
        foreach (var st in run.Stations.Where(s => !s.Done && s.Locked))
            ApplyTrackChange(run, st, now);

        // ゲームの進路一覧と突き合わせる（開通の確認・ゲームの自動解除・取り消した進路）
        foreach (var req in run.Requests.Where(r => !r.Released))
        {
            var inGame = run.Player.GameRoutes.Contains((req.Def.Interlock, req.Def.Name));

            // 列車がその進路に入り、通り抜けたら、その進路は使い終わり（ゲームの進路の一覧で確認できなかった進路も、ここで片付ける。
            // 残したままだと、使い終わった進路が、ほかの列車の進路と「重なる」と判定されて開通をふさぐ）
            if (req.Def.Circuits.Any(run.Player.TrackCircuits.Contains))
            {
                // 出発信号機（通過の進路なら、その進路）を踏んだ時刻を、その駅の発時刻とし、ダイヤとの差を遅れにする
                if (!req.Entered && (req.Def.IsDepartureSignal || req.Station.Track == null) && run.ActualDep.TryAdd(req.Station.St.Id, NowSec())
                    && DiaSec(run, req.Station.St.Id, depart: true) is { } plan)
                    run.Delay = run.ActualDep[req.Station.St.Id] - plan;
                req.Entered = true;
            }
            else if (req.Entered && req.CancelledUtc == null)
            {
                req.Released = true;
                continue;
            }

            if (req.CancelledUtc is { } c)
            {
                // 取り消した進路は、ゲームからなくなって（送信と行き違いにならないよう）5秒たてば解除済み
                if (!inGame && now - c > TimeSpan.FromSeconds(5)) req.Released = true;
                continue;
            }

            // ほかの列車の進路として、この列車のゲームに写した同じ進路が残っているだけなら、まだ開通とはしない。
            // 写した進路は列選・停車の扱いがその列車のもの（準急の出発信号機に C特1 が出る）なので、BuildOutputs で消させ、
            // ゲームから消えたら、この列車の進路として送り直す
            if (!req.Seen && IsMirrorLeftover(run, req)) continue;

            if (inGame) { req.Seen = true; req.SeenUtc ??= now; }
            else if (req.Seen) req.Released = true;
        }

        run.Requests.RemoveAll(r => r.Released && (now - r.RequestedUtc).TotalMinutes > 5);
    }

    /// <summary>番線の決め方: 集中扱いの指定 ＞ 待避 ＞ ユーザーの選択 ＞ 所定。</summary>
    private void ChooseTrack(TrainRun run, StationRun st)
    {
        var routes = st.St.Routes[run.Dir];

        // 始発の入換を要求したあとは、入換元と番線を選び直さない。列車が入換元の回路（新野崎の 23RT など）を出ると、
        // 入換元にいるとはみなされなくなり、入換の途中でふつうの始発として出発の進路を取り始めてしまうため。
        // 入換元の印は、ホームに入りきったとき（UpdateStations の「入換終わり」）に外す
        if (st.IsStart && st.ShuntFrom != null && st.Shunt == GroupState.Requested) return;

        // 始発で、入換の元の番線（引上線など）にいるか。入換が終わってホームに入れば、ふつうの始発に戻る
        st.ShuntFrom = null;
        // 出発できる番線にいたことがあれば覚えておく。大道寺の X線は本線の回路なので、1番線から出発して X線に入った列車を、
        // X線からの入換元と取り違えないように
        if (st.IsStart && routes.Depart.Keys.Any(k => st.St.TrackCircuit.TryGetValue(k, out var dc) && run.Player.TrackCircuits.Contains(dc)))
            st.WasOnDepartTrack = true;
        if (st.IsStart)
            foreach (var sh in routes.Shunts)
            {
                if (sh.FromCircuit != null && st.WasOnDepartTrack) continue;
                var fc = sh.FromCircuit ?? (sh.From is { } f && st.St.TrackCircuit.TryGetValue(f, out var c) ? c : null);
                // 入換の行き先の番線にもういれば、入換はいらない（江ノ原信号場の13番線の長い列車が、一直線の10番線側にはみ出していても、
                // 10番線にいるとはみなさない）
                if (st.St.TrackCircuit.TryGetValue(sh.To, out var toCircuit) && run.Player.TrackCircuits.Contains(toCircuit)) continue;
                if (fc != null && run.Player.TrackCircuits.Contains(fc))
                {
                    st.ShuntFrom = sh.FromLabel;
                    break;
                }
            }

        var options = new List<int>();
        if (st.ShuntFrom is { } from)
        {
            options.AddRange(routes.Shunts.Where(s => s.FromLabel == from && (st.Terminal || routes.Depart.ContainsKey(s.To))).Select(s => s.To).Distinct().OrderBy(t => t));
        }
        else
        {
            foreach (var t in routes.Arrive.Keys.Union(routes.Depart.Keys).OrderBy(t => t))
            {
                var canArrive = st.IsStart || routes.Arrive.ContainsKey(t);
                var canDepart = st.Terminal || routes.Depart.ContainsKey(t) || routes.Turnback.Contains(t); // 行き止まりは打ち切りとして選べる
                if (canArrive && canDepart) options.Add(t);
            }
        }

        st.Options = options;
        var throughOk = routes.Through.Count > 0 && !st.Terminal && !st.IsStart;

        if (_central.TrackOverride(run.Train, st.St.Id) is { } ct && options.Contains(ct))
        {
            (st.Track, st.TrackSource) = (ct, "集中");
            return;
        }

        if (OrderLoopTrack(run, st) is { } ot && options.Contains(ot))
        {
            (st.Track, st.TrackSource) = (ot, "待避");
            return;
        }

        if (st.Passing && st.Track is { } pt && options.Contains(pt))
        {
            st.TrackSource = "待避";
            return;
        }

        // 始発駅: 列車が実際にいる番線を使う
        if (st.IsStart)
        {
            foreach (var t in options)
                if (st.St.TrackCircuit.TryGetValue(t, out var tc) && run.Player.TrackCircuits.Contains(tc))
                {
                    (st.Track, st.TrackSource) = (t, "在線");
                    return;
                }
        }

        // 運転士が選んだ番線。ダイヤ運転中のダイヤの列車は使わない（列車からは進路に触れない）
        if (!(DiaMode && run.Dia != null) && run.Player.TrackChoices.TryGetValue(st.St.Id, out var chosen) && options.Contains(chosen))
        {
            (st.Track, st.TrackSource) = (chosen, "選択");
            return;
        }

        // ダイヤの番線（手入力の列番、またはシナリオの列番のダイヤ）。単線区間の駅で、ほかの列車が使っていれば空いている番線へ
        if (run.Dia != null && run.Dia.Stations.TryGetValue(st.St.Id, out var ds) && ds.Track is { } dt && options.Contains(dt))
        {
            (st.Track, st.TrackSource) = (dt, "ダイヤ");
            // ダイヤ運転中は、ダイヤの番線を勝手に変えない（ふさがっていれば空くまで待つ。交換・順序はダイヤどおりに流すので、ずらすと行き違えなくなる）
            if (!DiaMode && st.CouplePartner == null && st.CoupleWaiting == null) AvoidTakenTrack(run, st, options); // 連結する駅は、増結車のいる番線のまま
            return;
        }

        // 通過できる駅（江ノ原信号場）は、止まらない列車なら番線を使わない
        if (throughOk)
        {
            (st.Track, st.TrackSource) = (null, "通過");
            return;
        }

        int? def = null;
        if (st.St.DefaultTracks.TryGetValue(run.Dir, out var rules))
        {
            var keys = new List<string>();
            if (st.Terminal) keys.AddRange([$"終着:{run.Type}", "終着"]);
            if (st.IsStart) keys.AddRange([$"始発:{run.Type}", "始発"]);
            keys.AddRange([run.Type, "*"]);
            foreach (var k in keys)
                if (rules.TryGetValue(k, out var t) && options.Contains(t)) { def = t; break; }
        }

        // 決まりがなければ、そのまま通れる番線の最初、それもなければ選べる番線の最初
        if (def == null)
        {
            var through = routes.ThroughTracks.Where(options.Contains).ToList();
            def = through.Count > 0 ? through[0] : options.Count > 0 ? options[0] : null;
        }

        (st.Track, st.TrackSource) = (def, "所定");
        AvoidTakenTrack(run, st, options);
    }

    /// <summary>
    /// 単線区間の駅で、決めた番線をほかの列車（上下とも）が使う・いるなら、空いている別の番線にする
    /// （単線の駅は2線あるので、片方がふさがっていてももう片方に入れる。行き違い・続行のため）。
    /// </summary>
    private void AvoidTakenTrack(TrainRun run, StationRun st, List<int> options)
    {
        if (st.Track is not { } cur || st.IsStart || !_data.TouchesSingleTrack(st.St.Id)) return;
        var routes = st.St.Routes[run.Dir];

        bool Taken(int t) =>
            _runs.Values.Any(o => o != run && o.Stations.Any(s => s.St.Id == st.St.Id && !s.Done && !IsCutOff(o, s) && s.Locked && s.Track == t))
            || st.St.TrackCircuit.TryGetValue(t, out var tc) && _runs.Values.Any(o => o != run && o.Player.TrackCircuits.Contains(tc));

        if (!Taken(cur)) return;
        // 代わりの番線: 通り抜ける列車は通れる番線（行き止まりは不可）、終着なら着ける番線
        var candidates = st.Terminal ? options : routes.ThroughTracks.Where(options.Contains).ToList();
        if (candidates.FirstOrDefault(t => t != cur && !Taken(t)) is var alt && alt != 0)
            (st.Track, st.TrackSource) = (alt, "空き番線");
    }

    /// <summary>番線の確定後も、選び直してよいか（単線区間の駅で、まだ進路を何も要求していない。指定・待避・駅扱いは除く）。</summary>
    private bool CanRechoose(TrainRun run, StationRun st) =>
        !st.IsStart && _data.TouchesSingleTrack(st.St.Id)
        && st.Arrive != GroupState.Requested && st.Depart != GroupState.Requested
        && !st.Passing && !st.ForceArrive && !st.ForceDepart
        && st.TrackSource is "ダイヤ" or "所定" or "空き番線"
        && !(DiaMode && st.TrackSource == "ダイヤ")
        && _central.TrackOverride(run.Train, st.St.Id) == null
        && !run.Requests.Any(r => r.Station == st && !r.Released && r.CancelledUtc == null);

    /// <summary>
    /// 確定後の番線の変更（集中扱い）。進路をまだ要求していなければそのまま変える。
    /// 要求済みでも、列車がまだその進路に入っていなければ、今の進路を取り消して新しい番線で開通し直す（急な番線変更）。
    /// </summary>
    private void ApplyTrackChange(TrainRun run, StationRun st, DateTime now)
    {
        var want = _central.TrackOverride(run.Train, st.St.Id);
        var source = "集中";
        if (want == null && OrderLoopTrack(run, st) is { } ot) (want, source) = (ot, "待避");
        if (want == null || want == st.Track || !st.Options.Contains(want.Value)) return;
        if (st.IsStart && st.ShuntFrom != null) return; // 入換の番線は、入換要求の前に ChooseTrack で決まる

        var active = run.Requests.Where(r => r.Station == st && !r.Released && r.CancelledUtc == null).ToList();
        if (active.Any(r => r.Def.Circuits.Any(run.Player.TrackCircuits.Contains)))
            return; // もう進路に入っている

        foreach (var r in active) r.CancelledUtc = now;
        var old = st.Track;
        st.Track = want;
        st.LoopArrivedUtc = null;
        st.InTrackUtc = null;
        st.StoppedUtc = null;
        st.TrackSource = source;
        st.Partners.Clear();
        (st.Arrive, st.ArriveWait) = (GroupState.None, "");
        (st.Depart, st.DepartWait) = (GroupState.None, "");
        ResolvePaths(run, st);
        _log.Note($"番線変更: {run.Train} {st.St.Name} {old?.ToString() ?? "-"}番線 → {want}番線" + (active.Count > 0 ? "（開通済みの進路を取り消して取り直し）" : ""));
    }

    /// <summary>
    /// 抑止（場内・出発）: その進路を開通させない。すでに開通している（要求している）進路は、
    /// 運転士の目の前で信号が急に変わらない範囲なら取り消す。列車がもう接近していれば、取り消さずに残す。
    /// 駅扱い・手動進路で開通させた進路は、抑止では取り消さない。
    /// </summary>
    private void ApplyHold(TrainRun run, StationRun st, DateTime now)
    {
        st.HoldKept = "";
        var hold = _central.Hold(run.Train, st.St.Id);
        if (hold == HoldKind.None || st.ManualUsed) return;
        foreach (var departure in new[] { false, true })
        {
            if ((hold & (departure ? HoldKind.Depart : HoldKind.Arrive)) == 0) continue;
            var label = departure ? "出発" : "場内";
            var active = run.Requests
                .Where(r => r.Station == st && r.Departure == departure && !r.Manual && !r.Released && r.CancelledUtc == null).ToList();
            if (active.Count == 0) continue;
            var names = string.Join("・", active.Select(r => r.Def.Name));
            if (active.Any(r => !CanCancelQuietly(run, st, r.Def)))
            {
                st.HoldKept += label;
                if (_holdLogged.Add((run.ConnId, st.St.Id, departure)))
                    _log.Note($"{label}抑止: {run.Train} {st.St.Name} の {names} は、列車が接近しているため取り消しません");
                continue;
            }

            foreach (var r in active) r.CancelledUtc = now;
            if (departure) (st.Depart, st.DepartWait) = (GroupState.None, "");
            else
            {
                (st.Arrive, st.ArriveWait) = (GroupState.None, "");
                if (st.IsStart && st.ShuntFrom != null) st.Shunt = GroupState.None;
            }

            _holdLogged.Remove((run.ConnId, st.St.Id, departure));
            _log.Note($"{label}抑止: {run.Train} {st.St.Name} の {names} を取消（列車はまだ接近していない）");
        }
    }

    /// <summary>
    /// その進路を、運転士に急な信号の変化を見せずに取り消せるか。
    /// 列車が進路の中にいる、または接近鎖錠の区間にいれば、取り消せない。
    /// 接近鎖錠の区間が分からない進路は、列車が手前の駅より先に来ていたら取り消せないものとする。
    /// </summary>
    private static bool CanCancelQuietly(TrainRun run, StationRun st, RouteDef def)
    {
        var on = run.Player.TrackCircuits;
        if (def.Circuits.Any(on.Contains)) return false;
        if (def.Approach.Count > 0) return !def.Approach.Any(on.Contains);
        return (st.Zone - run.Front) * run.Dir.Step() >= 3;
    }

    // ---- 駅扱い（強制進路） ----

    /// <summary>駅扱いのボタン（その列車・駅で選べる進路）。action は CentralControl.RequestManual に渡す値。</summary>
    private List<(string Action, string Label)> ManualOptions(TrainRun run, StationRun st)
    {
        var routes = st.St.Routes[run.Dir];
        var list = new List<(string, string)>();
        if (st.IsStart && st.ShuntFrom is { } from)
        {
            foreach (var to in routes.Shunts.Where(s => s.FromLabel == from).Select(s => s.To).Distinct().OrderBy(t => t))
                list.Add(($"shunt:{to}", $"入換 {from}→{to}番線"));
            return list;
        }

        if (!st.IsStart)
        {
            foreach (var t in routes.Arrive.Keys.OrderBy(t => t)) list.Add(($"arrive:{t}", $"本線→{t}番線"));
            if (routes.Through.Count > 0) list.Add(("through", "本線通過"));
            // 連結: 番線に「連」＋この列車の列番の列車がいるときだけ
            foreach (var t in routes.Arrive.Where(a => a.Value.Count >= 2 && ParallelShuntOf(_data, a.Value[^1]) != null).Select(a => a.Key).OrderBy(t => t))
                if (GuidePartner(run, st, t) != null) list.Add(($"guide:{t}", $"誘導開通（本線→{t}番線）"));
        }

        if (!st.Terminal)
            foreach (var t in routes.Depart.Keys.OrderBy(t => t))
                if (!st.IsStart || st.Options.Contains(t) || st.Track == t)
                    list.Add(($"depart:{t}", $"{t}番線→本線"));

        // 終着の列車の入換（引上げ）
        var outs = ShuntOutOptions(run, st);
        for (var i = 0; i < outs.Count; i++) list.Add(($"shuntout:{i}", $"入換をする（{st.Track}番線→{outs[i].Label}）"));

        // 引き上げた線（大道寺の X線）にいる終着の列車: その線から番線への入換
        foreach (var sh in ShuntsFromHere(run, st))
            list.Add(($"shuntfrom:{sh.To}", $"入換 {sh.FromLabel}→{sh.To}番線"));
        return list;
    }

    /// <summary>
    /// 終着の列車が今いる線（引上げ先の X線など）から出る入換（上り・下りの shunt。行き先の番線ごとに最初のもの）。
    /// 列車がその線にいなければ空。始発の入換（ShuntFrom。始発の列車が引上線などにいる）は、ManualOptions の始発の入換で出すので除く。
    /// </summary>
    private static List<ShuntDef> ShuntsFromHere(TrainRun run, StationRun st)
    {
        if (st.IsStart && st.ShuntFrom != null || !(st.Terminal || IsTurnbackTrack(run, st)) || run.Player.TrackCircuits.Count == 0) return [];
        return st.St.Routes.Values.SelectMany(r => r.Shunts)
            .Where(s => s.FromCircuit != null && s.Then is not { Count: > 0 } && s.Via == null && run.Player.TrackCircuits.Contains(s.FromCircuit))
            .GroupBy(s => s.To).Select(g => g.First()).OrderBy(s => s.To).ToList();
    }

    /// <summary>
    /// 駅扱いの指示を適用する。番線を変え（開通済みでまだ入っていない進路は取り消す）、その進路を
    /// 「連動の確認だけで開通させる」印を付ける（抑止・待避・単線の交換・順番待ちなどは無視）。
    /// </summary>
    private void ApplyManual(TrainRun run, StationRun st, string action, DateTime now)
    {
        var routes = st.St.Routes[run.Dir];
        var kind = action.Split(':')[0];
        int? track = action.Contains(':') && int.TryParse(action.Split(':')[1], out var t) ? t : null;

        void UseTrack(int? want)
        {
            if (want == st.Track) { st.TrackSource = "駅扱い"; return; }
            var active = run.Requests.Where(r => r.Station == st && !r.Released && r.CancelledUtc == null).ToList();
            foreach (var r in active.Where(r => !r.Def.Circuits.Any(run.Player.TrackCircuits.Contains))) r.CancelledUtc = now;
            (st.Track, st.TrackSource) = (want, "駅扱い");
            st.Partners.Clear();
            st.LoopArrivedUtc = null;
            if (st.Arrive != GroupState.Requested || active.Any(r => !r.Departure && r.CancelledUtc != null)) (st.Arrive, st.ArriveWait) = (GroupState.None, "");
            if (st.Depart != GroupState.Requested || active.Any(r => r.Departure && r.CancelledUtc != null)) (st.Depart, st.DepartWait) = (GroupState.None, "");
            ResolvePaths(run, st);
        }

        switch (kind)
        {
            case "arrive" when track is { } a && routes.Arrive.ContainsKey(a) && !st.IsStart:
                _central.SetTrack(run.Train, st.St.Id, a);
                UseTrack(a);
                (st.ForceArrive, st.CoupleGuide) = (true, false);
                break;
            case "through" when routes.Through.Count > 0 && !st.IsStart:
                _central.SetTrack(run.Train, st.St.Id, null);
                UseTrack(null);
                (st.ForceArrive, st.CoupleGuide) = (true, false);
                break;
            // 誘導開通（連結）: 番線にいる列車の列番から「連」を除いたものが、この列車の列番のときだけ
            case "guide" when track is { } g && routes.Arrive.TryGetValue(g, out var gp) && gp.Count >= 2 && ParallelShuntOf(_data, gp[^1]) != null && !st.IsStart && GuidePartner(run, st, g) is { } gpart:
                _central.SetTrack(run.Train, st.St.Id, g);
                UseTrack(g);
                (st.ForceArrive, st.CoupleGuide) = (true, true);
                _log.Note($"連結: {run.Train} {st.St.Name} 駅扱いの誘導開通（{g}番線の {gpart.Train} に連結）");
                break;
            case "depart" when track is { } d && routes.Depart.ContainsKey(d) && !st.Terminal:
                _central.SetTrack(run.Train, st.St.Id, d);
                UseTrack(d);
                st.ForceDepart = true;
                if (st.IsStart) st.DepartureRequested = true;
                break;
            case "shunt" when track is { } to && st.IsStart && st.ShuntFrom != null:
                _central.SetTrack(run.Train, st.St.Id, to);
                (st.Track, st.TrackSource) = (to, "駅扱い");
                st.ShuntRequested = true;
                st.ForceShunt = true;
                break;
            case "shuntout" when track is { } idx && idx >= 0 && idx < ShuntOutOptions(run, st).Count:
                // 入換をする: 連動の確認だけで開通させる（列車が番線に入りきる前でも要求できる）
                (st.ShuntOutChoice, st.ShuntOut, st.ForceShuntOut) = (idx, GroupState.None, true);
                return;
            case "shuntfrom" when track is { } shTo && ShuntsFromHere(run, st).FirstOrDefault(s => s.To == shTo) is { } fromHere:
                // 引き上げた線から番線への入換: 連動の確認だけで開通させる（Process）
                (st.ManualShuntFrom, st.ManualShuntFromWait) = (fromHere, "");
                return;
            default:
                _log.Note($"駅扱い: {run.Train} {st.St.Name} の「{action}」は、この列車では使えません");
                return;
        }

        st.Locked = true;
        if (!st.Triggered) (st.Triggered, st.TriggeredUtc) = (true, now);
        ResolvePaths(run, st);
    }

    /// <summary>
    /// 管理画面の手動進路（管理者用）: 進路名を直接指定して、その列車のゲームに開通・取消させる。連動の確認もしない。
    /// 列車がもう通り過ぎた駅の進路は扱えない。
    /// </summary>
    public string ManualRoute(string train, string interlock, string routeName, bool set)
    {
        lock (_lock)
        {
            var run = _runs.Values.FirstOrDefault(r => r.Train == train);
            if (run == null) return $"{train} は在線共有モードで走っていません";
            if (!_data.Routes.TryGetValue((interlock, routeName), out var def)) return $"{interlock} に進路 {routeName} はありません";
            var now = DateTime.UtcNow;
            var active = run.Requests.Where(r => r.Def == def && !r.Released && r.CancelledUtc == null).ToList();
            var st = run.Stations.FirstOrDefault(s => s.St.Id == def.StationId && !s.Done) ?? run.Stations.FirstOrDefault(s => !s.Done);
            if (set)
            {
                if (active.Count > 0) return $"{train}: {routeName} はもう要求しています";
                if (st == null) return $"{train}: 扱える駅がありません";
                if (DepartSection(def, run.Dir) is { } ds)
                {
                    var secName = $"{_data.Line.StationName(ds.Section.Item1)}〜{_data.Line.StationName(ds.Section.Item2)}";
                    if (_levers.TryGetValue(ds.Section, out var lv) && lv.Locked && lv.Dir != ds.Dir)
                        return $"{train}: {routeName} は開通させません（方向てこが{lv.Dir.Label()}向きに鎖錠中: {secName}）";
                    var (za, zb) = (_data.Line.ZoneOfStation(ds.Section.Item1), _data.Line.ZoneOfStation(ds.Section.Item2));
                    // 同じ向きの列車がいても続行できる（逆向きの列車がいるときだけ断る）
                    if (_runs.Values.FirstOrDefault(o => o != run && o.Dir != ds.Dir && o.Player.Zones.Any(z => z > Math.Min(za, zb) && z < Math.Max(za, zb))) is { } inSec)
                        return $"{train}: {routeName} は開通させません（単線区間 {secName} に {inSec.Train} がいます）";
                    _levers[ds.Section] = (ds.Dir, true); // すぐに倒す（次の周期を待たずに、対向の出発を止める）
                }

                foreach (var old in run.Requests.Where(o => !o.Released && o.CancelledUtc != null && o.Def == def)) old.Released = true;
                run.Requests.Add(new RouteReq { Def = def, Id = ++_seq, Station = st, Departure = def.IsDepartureSignal, RequestedUtc = now, Manual = true });
                if (st.St.Id == def.StationId) st.ManualUsed = true;
                _log.Note($"手動進路: {train} {interlock} {routeName} を開通");
                return $"{train}: {routeName} を開通させます";
            }

            foreach (var r in active) r.CancelledUtc = now;
            if (active.Count == 0)
            {
                if (!run.Player.GameRoutes.Contains((interlock, routeName))) return $"{train}: {routeName} はゲームにありません";
                if (st == null) return $"{train}: 扱える駅がありません";
                run.Requests.Add(new RouteReq { Def = def, Id = ++_seq, Station = st, Departure = def.IsDepartureSignal, RequestedUtc = now, Manual = true, Seen = true, CancelledUtc = now });
            }

            // 取り消した進路の駅は、自動で開き直さないように抑止にする
            if (st != null && st.St.Id == def.StationId)
            {
                _central.SetHold(train, def.StationId, true, def.IsDepartureSignal ? HoldKind.Depart : HoldKind.Arrive);
                if (active.Any(r => !r.Departure)) (st.Arrive, st.ArriveWait) = (GroupState.None, "");
                if (active.Any(r => r.Departure)) (st.Depart, st.DepartWait) = (GroupState.None, "");
            }

            _log.Note($"手動進路: {train} {interlock} {routeName} を取消");
            return $"{train}: {routeName} を取り消します";
        }
    }

    /// <summary>番線から、送る進路の並びを決める。進路がなければ false。</summary>
    private bool ResolvePaths(TrainRun run, StationRun st)
    {
        var routes = st.St.Routes[run.Dir];
        st.ArrivePath = new List<RouteDef>();
        st.DepartPath = new List<RouteDef>();
        if (st.Track is { } t)
        {
            if (!st.IsStart && routes.Arrive.TryGetValue(t, out var a)) st.ArrivePath = a;
            if (!st.Terminal && routes.Depart.TryGetValue(t, out var d)) st.DepartPath = d;
        }
        else if (routes.Through.Count > 0 && !st.IsStart && !st.Terminal)
        {
            st.ArrivePath = routes.Through; // 通過（江ノ原信号場）は、場内と同じ扱いで1回で送る
        }

        st.DepartStop = !st.IsStart && st.Track is { } dt && routes.DepartStop.Contains(dt)
                        // "停車" の番線は、通過列車（待避しないもの）なら出発停止にしない（場内・出発を一緒に開ける）
                        && !(routes.DepartStopStoppingOnly.Contains(dt) && st.StopType == "通過" && !st.Passing && st.TrackSource != "待避" && !st.Terminal)
                        // 種別が変わる列車は、どの駅・番線でも出発停止で入れる
                        || !st.IsStart && !st.Terminal && st.DepartPath.Count > 0 && TypeChangesAt(run, st);

        return st.ArrivePath.Count > 0 || st.DepartPath.Count > 0;
    }

    // =========================================================================================
    // 待避
    // =========================================================================================

    /// <summary>その列車がこの駅をもう抜けた（または先頭が駅より先にいて、最後尾も抜けた）。</summary>
    private static bool HasPassed(TrainRun t, StationRun st)
    {
        var ts = t.Stations.FirstOrDefault(s => s.St.Id == st.St.Id);
        return ts is { Done: true } || (t.HasPosition && (t.Rear - st.Zone) * t.Dir.Step() > 0);
    }

    private TrainRun? RunByTrain(string train) => _runs.Values.FirstOrDefault(r => r.Train == train);

    /// <summary>待避線を、ほかの列車（同じ方向）が待避に使っている・使う予定か。</summary>
    private TrainRun? LoopUser(TrainRun run, StationRun st, int loop) =>
        _runs.Values.FirstOrDefault(o => o != run && o.Dir == run.Dir
                                         && o.Stations.Any(s => s.St.Id == st.St.Id && !s.Done && s.Locked && s.Track == loop
                                                                && (s.Passing || s.TrackSource == "待避")));

    /// <summary>
    /// 自動の待避。番線の確定時に、後ろから来る「自分より強い種別」の列車を、この駅を通る見込みの早い順に見ていき、
    /// 次の駅までに追いつかれる見込みの列車を待つ（待つたびに自分の発車見込みを遅らせて判断を続ける。最大 MaxPassingWaits 本）。
    /// 集中扱いの番線指定・発車順があれば、自動では判断しない。その駅を通過する列車は、自動では待避させない。
    /// </summary>
    private void DecidePassing(TrainRun run, StationRun st, DateTime now)
    {
        if (!_data.PassingStations.TryGetValue(st.St.Id, out var ps) || st.Terminal) return;
        // 自動の待避は、その駅に停車する列車だけ（通過列車を待避のために止めない。通過列車は、次の停車する待避駅で待つ）
        if (st.StopType == "通過" && !st.IsStart) return;
        if (_central.TrackOverride(run.Train, st.St.Id) != null) return;
        if (_central.Order(st.St.Id, run.Dir)?.Contains(run.Train) == true) return;
        if (DiaMode && run.Dia != null) return; // ダイヤ運転中は、ダイヤの番線・順序どおり（勝手に待避させない）
        var loop = ps.TrackFor(run.Dir);
        if (!st.Options.Contains(loop) || LoopUser(run, st, loop) != null) return;
        var nextStation = _data.Line.StationAtZone(st.Zone + 2 * run.Dir.Step());
        if (nextStation == null) return;

        var type = RunType(run);
        var runTime = _model.SecondsBetween(type, run.Dir, st.St.Id, nextStation);
        if (runTime == null) return;
        var arrive = st.IsStart ? 0 : _model.SecondsTo(type, run.Dir, run.Front, run.Fraction, st.St.Id) ?? 0;
        var depart = arrive + (st.StopType == "通過" && !st.IsStart ? 0 : _data.DefaultDwellSeconds);
        var rank = _data.RankOf(run.Type);

        var candidates = _runs.Values
            .Where(o => o != run && o.HasPosition && o.Dir == run.Dir && _data.RankOf(o.Type) > rank)
            .Where(o => IsAheadOf(run, o)) // 後ろにいる列車だけ
            .Select(o => (Run: o, St: o.Stations.FirstOrDefault(s => s.St.Id == st.St.Id && !s.Done && !s.Terminal)))
            .Where(x => x.St != null)
            .Select(x =>
            {
                var at = _model.SecondsTo(RunType(x.Run), x.Run.Dir, x.Run.Front, x.Run.Fraction, st.St.Id);
                var pass = at + (x.St!.StopType == "通過" ? 0 : _data.DefaultDwellSeconds);
                return (x.Run, Pass: pass);
            })
            .Where(x => x.Pass != null)
            .OrderBy(x => x.Pass)
            .ToList();

        var waits = new List<(TrainRun Run, double Pass, double Next)>();
        foreach (var (other, pass) in candidates)
        {
            if (waits.Count >= _data.MaxPassingWaits) break;
            var theirNext = _model.SecondsTo(RunType(other), other.Dir, other.Front, other.Fraction, nextStation);
            if (theirNext == null || theirNext >= depart + runTime + _data.PassingMarginSeconds) continue;
            waits.Add((other, pass!.Value, theirNext.Value));
            depart = Math.Max(depart, pass.Value); // 通過を待ってから発車する
            depart = Math.Max(depart, arrive + _data.MinLoopDwellSeconds); // 待避線では最低の停車時分を止まる
        }

        if (waits.Count == 0) return;
        foreach (var w in waits)
            st.Partners.Add(new Partner { Conn = w.Run.ConnId, Train = w.Run.Train, ExpectedPassUtc = now.AddSeconds(w.Pass) });
        st.Track = loop;
        st.TrackSource = "待避";
        _log.Note($"待避: {run.Train} は {st.St.Name} {loop}番線で " + string.Join("、", waits.Select(w => $"{w.Run.Train}（通過見込み {w.Pass:0}秒後）"))
                  + $" を待避。発車見込み {depart:0}秒後");
    }

    /// <summary>待避中なら、まだ発車させない理由。発車させてよければ null。待つ列車ごとに、通過・追いつかれない見込み・許容の遅れで外していく。</summary>
    private string? PassingHold(TrainRun run, StationRun st, DateTime now)
    {
        if (!st.Passing) return null;
        var nextStation = _data.Line.StationAtZone(st.Zone + 2 * run.Dir.Step());
        // 自分が次の駅に着く見込み: まだ駅に着いていなければ、着くまで＋停車時分を足す（待避線では必ず止まる）
        double? runTime = nextStation != null ? _model.SecondsBetween(RunType(run), run.Dir, st.St.Id, nextStation) : null;
        var atStation = run.Player.Zones.Contains(st.Zone) && (run.Front - st.Zone) * run.Dir.Step() <= 0;
        // 発車できる見込み（秒後）。まだ駅に着いていなければ、着くまで＋待避線の最低の停車時分。着いていれば、その残り
        var loopLeft = st.LoopArrivedUtc is { } la ? Math.Max(0, _data.MinLoopDwellSeconds - (now - la).TotalSeconds) : _data.MinLoopDwellSeconds;
        var depart = atStation ? loopLeft : (_model.SecondsTo(RunType(run), run.Dir, run.Front, run.Fraction, st.St.Id) ?? 0) + Math.Max(_data.DefaultDwellSeconds, _data.MinLoopDwellSeconds);

        // 通過の早い順に見る。先に待つ列車が残っていれば、その通過までは発車できないので、それを踏まえて判断する
        foreach (var p in st.Partners.OrderBy(p => p.ExpectedPassUtc).ToList())
        {
            string? why = null;
            if (!_runs.TryGetValue(p.Conn, out var partner) || partner.Train != p.Train) why = "いなくなった";
            else if (HasPassed(partner, st)) why = "通過";
            else if (now > p.ExpectedPassUtc.AddSeconds(_data.PassingToleranceSeconds)) why = $"見込みより {_data.PassingToleranceSeconds}秒以上遅れた";
            else if (runTime != null && nextStation != null
                     && _model.SecondsTo(RunType(partner), partner.Dir, partner.Front, partner.Fraction, nextStation) is { } theirs
                     && theirs >= depart + runTime + _data.PassingMarginSeconds)
                why = "今発車しても追いつかれない見込み";
            if (why == null)
            {
                // 残す列車: その通過を待ってから発車する
                var ps = partner!.Stations.FirstOrDefault(s => s.St.Id == st.St.Id);
                if (_model.SecondsTo(RunType(partner), partner.Dir, partner.Front, partner.Fraction, st.St.Id) is { } pass)
                    depart = Math.Max(depart, pass + (ps is { StopType: not "通過" } ? _data.DefaultDwellSeconds : 0));
                continue;
            }
            st.Partners.Remove(p);
            _log.Note($"待避: {run.Train} {st.St.Name} … {p.Train} {why}" + (st.Partners.Count == 0 ? "。待避終了" : ""));
        }

        return st.Passing ? $"待避中（{string.Join("・", st.Partners.Select(p => p.Train))} の通過待ち）" : null;
    }

    // ---- 発車順（集中扱い） ----

    /// <summary>出発順序で先の列車（同じ方向）が、まだ後ろ（この列車より手前）にいて駅を抜けていなければ、待避の番線（待避駅だけ）。</summary>
    private int? OrderLoopTrack(TrainRun run, StationRun st)
    {
        if (st.Terminal || st.IsStart) return null;
        if (_central.Order(st.St.Id, run.Dir) is not { } order) return null;
        var idx = order.ToList().IndexOf(run.Train);
        if (idx <= 0) return null;
        var routes = st.St.Routes[run.Dir];
        foreach (var name in order.Take(idx))
        {
            if (RunByTrain(name) is not { HasPosition: true } p || p.Dir != run.Dir || HasPassed(p, st)
                || !IsAheadOf(run, p)) continue;
            // 待避駅: 待避の番線
            if (_data.PassingStations.TryGetValue(st.St.Id, out var ps)) return ps.TrackFor(run.Dir);
            // ほかの駅: 追い越す列車が使う番線と別の、そのまま通れる番線（なければ追い越せないので、番線は変えない）
            var theirs = p.Stations.FirstOrDefault(s => s.St.Id == st.St.Id && !s.Done)?.Track;
            var alt = st.Options.Where(t => t != theirs && routes.Arrive.ContainsKey(t) && routes.Depart.ContainsKey(t)).ToList();
            if (alt.Count > 0) return st.Track is { } cur && alt.Contains(cur) ? cur : alt[0];
        }

        return null;
    }

    /// <summary>その列車がこの駅でこれから使う進路（場内はまだ入っていなければ、出発・通過）。番線が決まっていなければ、その時点の番線で。</summary>
    private static List<RouteDef> PendingPaths(TrainRun p, StationRun ps)
    {
        var routes = ps.St.Routes[p.Dir];
        var list = new List<RouteDef>();
        if (ps.Track is { } t)
        {
            if (!ps.IsStart && ps.Arrive != GroupState.Requested && routes.Arrive.TryGetValue(t, out var a)) list.AddRange(a);
            if (!ps.Terminal && routes.Depart.TryGetValue(t, out var d)) list.AddRange(d);
        }
        else if (!ps.IsStart && !ps.Terminal && ps.Arrive != GroupState.Requested) list.AddRange(routes.Through);
        return list;
    }

    /// <summary>
    /// 駅の出発順序。順序で先の列車が、まだ駅を抜けていなくて、自分の出発の進路と競合する（回路が重なる）なら待つ。
    /// 競合しない進路（別の番線の場内・反対方向の出発など）は待たない（1番線に先の列車が入れば、2番線の場内・出発は開けられる）。
    /// 同じ方向で先の列車が後ろにいるのに、自分が待避の番線にいない（追い越させられない）ときは待たない。
    /// </summary>
    private string? OrderHold(TrainRun run, StationRun st, List<RouteDef> path)
    {
        if (EffectiveOrder(st.St.Id, run.Dir) is not { } order) return null;
        var idx = order.ToList().IndexOf(run.Train);
        if (idx <= 0) return null;
        var mine = Circuits(path).ToHashSet();
        var waiting = new List<string>();
        foreach (var name in order.Take(idx))
        {
            if (RunByTrain(name) is not { HasPosition: true } p || HasPassed(p, st)) continue;
            if ((_central.Hold(name, st.St.Id) & HoldKind.Depart) != 0) continue; // 出発抑止中の列車は待たない（ほかの列車を先に出す）
            if (p.Stations.FirstOrDefault(s => s.St.Id == st.St.Id && !s.Done) is not { } ps || IsCutOff(p, ps)) continue; // その駅を通らない
            if (SameTrackBehind(run, st, p)) continue; // 同じ番線の後ろの列車は追い越させられない
            // 後ろを走る列車は、ダイヤ・司令員が決めた追い越しでなければ待たない（遅れで順序が入れ替わっていても、前の列車が先。
            // 後ろの列車は AheadHold で前の列車を待つので、ここで待つとお互いに動けなくなる）
            if (p.Dir == run.Dir && IsAheadOf(run, p) && !MayOvertake(p, run, st)) continue;
            if (!Circuits(PendingPaths(p, ps)).Any(mine.Contains)) continue; // 競合しない
            waiting.Add(name);
        }

        return waiting.Count > 0 ? $"出発順序待ち（{string.Join("・", waiting)} が先）" : null;
    }

    /// <summary>
    /// ダイヤ運転中に出発進路を出すとき、ダイヤの発の順で先の列車がまだその駅を通っていなければ、記録（routes の記録ファイル）に残す。
    /// 先に出した訳を後から確かめるため: 進路が競合するのに待たなかった先の列車と、運転順序の計画の順・発を書く（待たなくてよい列車は OrderHold と同じ判定で除く）。
    /// </summary>
    private void NoteDepartAhead(TrainRun run, StationRun st, List<RouteDef> path, DateTime now)
    {
        // 前を走る同じ向きの列車が、まだこの駅を出ていないのに出発進路を出すとき（追い越し）は、訳と一緒に必ず記録に残す
        var passed = _runs.Values.Where(o => IsAheadOf(o, run) && o.Stations.FirstOrDefault(s => s.St.Id == st.St.Id) is { Done: false } os && !IsCutOff(o, os)
                                             && !os.Terminal && !IsTurnbackTrack(o, os)).ToList();
        if (passed.Count > 0)
        {
            var why = st.ForceDepart ? "駅扱い"
                : string.Join("・", passed.Select(o =>
                    $"{o.Train}: " + (MayOvertake(run, o, st) ? (DiaOvertakes(run, o, st.St.Id) ? "ダイヤの追い越し" : "司令員の順序")
                        : WaitingForRequest(o, o.Stations.First(s => s.St.Id == st.St.Id)) ? "出発要求前"
                        : (_central.Hold(o.Train, st.St.Id) & HoldKind.Depart) != 0 ? "出発抑止中"
                        : !DiaMode && _data.PassingStations.ContainsKey(st.St.Id) ? "自動の待避" : "そのほか")));
            RouteLogLine(now, $"追い越し: {run.Train} {st.St.Name} が、前を走る {string.Join("・", passed.Select(o => o.Train))} より先に出発（{why}）");
        }

        if (!DiaMode || run.Dia == null || DiaSec(run, st.St.Id, depart: true) is not { } myDia) return;
        var mine = Circuits(path).ToHashSet();
        var ahead = new List<string>();
        foreach (var p in _runs.Values)
        {
            if (p == run || p.Dir != run.Dir || p.Dia == null || !p.HasPosition || HasPassed(p, st)) continue;
            if (p.Stations.FirstOrDefault(s => s.St.Id == st.St.Id && !s.Done) is not { } ps || IsCutOff(p, ps)) continue;
            if (DiaSec(p, st.St.Id, depart: true) is not { } pd || pd >= myDia) continue;
            // 待たなくてよい先の列車（出発抑止中・同じ番線の後ろ・進路が競合しない）は書かない。待つはずの列車を待たなかったときだけ
            if ((_central.Hold(p.Train, st.St.Id) & HoldKind.Depart) != 0 || SameTrackBehind(run, st, p) || !Circuits(PendingPaths(p, ps)).Any(mine.Contains)) continue;
            if (IsAheadOf(run, p) && !MayOvertake(p, run, st)) continue; // 後ろを走る列車（遅れて順序が入れ替わった）は待たない
            ahead.Add($"{p.Train}（ダイヤ {FormatDiaTime(pd)}）");
        }
        if (ahead.Count == 0) return;

        var order = EffectiveOrder(st.St.Id, run.Dir);
        var planText = order == null ? "なし"
            : string.Join("・", order.Select(t => _orderPlan?.Times.TryGetValue((t, st.St.Id), out var ot) == true ? $"{t} {FormatDiaTime(ot.Dep)}" : t));
        var how = st.ForceDepart ? "駅扱い" : _central.IsForced(run.Train, st.St.Id) ? "開通指示" : _central.Order(st.St.Id, run.Dir) != null ? "手で決めた順" : "自動";
        RouteLogLine(now, $"順序の確認: {run.Train} {st.St.Name}（ダイヤ {FormatDiaTime(myDia)}）を、ダイヤで先の {string.Join("、", ahead)}より先に出発（{how}）。"
                          + $"出発順序: {planText}");
    }

    /// <summary>出発順序の画面に出す注意（競合する先の列車・追い越せない並び）。</summary>
    private string OrderWarning(TrainRun run, StationRun st, IReadOnlyList<string> order)
    {
        var idx = order.ToList().IndexOf(run.Train);
        if (idx <= 0) return "";
        var mine = Circuits(PendingPaths(run, st)).ToHashSet();
        var notes = new List<string>();
        foreach (var name in order.Take(idx))
        {
            if (RunByTrain(name) is not { HasPosition: true } p || HasPassed(p, st)) continue;
            if (p.Stations.FirstOrDefault(s => s.St.Id == st.St.Id && !s.Done) is not { } ps || IsCutOff(p, ps)) continue;
            if (p.Dir == run.Dir && IsAheadOf(run, p) && ps.Track == st.Track)
                notes.Add($"{name} と同じ番線のため追い越せません（番線を変えてください）");
            else if (Circuits(PendingPaths(p, ps)).Any(mine.Contains))
                notes.Add($"{name} と進路が競合（{name} が先）");
        }

        return string.Join("、", notes);
    }

    // =========================================================================================
    // 進路の要求
    // =========================================================================================

    /// <summary>待避駅の待避の番線（津崎・新野崎の下り4・上り1）に入るか。始発・終着は除く。</summary>
    private bool IsLoopTrack(TrainRun run, StationRun st) =>
        !st.Terminal && !st.IsStart && st.Track is { } t && _data.PassingStations.TryGetValue(st.St.Id, out var ps) && ps.TrackFor(run.Dir) == t;

    /// <summary>この駅での停車時分の見込み（秒）。待避線は最低の停車時分、通過は 0。</summary>
    private int DwellSeconds(TrainRun run, StationRun st) =>
        IsLoopTrack(run, st) ? Math.Max(_data.DefaultDwellSeconds, _data.MinLoopDwellSeconds)
        : st.StopType == "通過" && !st.IsStart && !st.DepartStop ? 0 : _data.DefaultDwellSeconds;

    /// <summary>待避線に着いてから、最低の停車時分がたっていなければ待つ理由。</summary>
    /// <summary>止まったとみなす速度（km/h 未満）。</summary>
    private const float StopSpeedKmh = 0.5f;

    /// <summary>
    /// 番線に入りきって 0km/h になる（止まる）まで開通させない。止まってから afterStopSeconds（と番線ごとの秒数）待つ。
    /// 秒数で待つと運転のしかたで長さが変わるので、ゲームの速度で止まったことを確かめる。
    /// </summary>
    private string? StopHold(StationRun st, DateTime now, string what, int extraSeconds = 0)
    {
        if (st.StoppedUtc is not { } at) return $"{what}（止まったら開通）";
        var sec = Math.Max(extraSeconds, _data.AfterStopSeconds);
        var left = sec - (now - at).TotalSeconds;
        return left > 0 ? $"{what}（止まってから{sec}秒・あと {Math.Ceiling(left):0}秒）" : null;
    }

    /// <summary>
    /// フリー運転で待避線に入った列車（自動の待避・運転士の選択のどちらも）は、止まったあと、出発要求ボタンで出発させる。
    /// ダイヤ運転中は（ダイヤにない列車も）、これまでどおり自動。
    /// </summary>
    private bool NeedsLoopDepartRequest(TrainRun run, StationRun st) =>
        !DiaMode && IsLoopTrack(run, st) && !st.DepartureRequested;

    /// <summary>待避線に止まっていて、出発要求を待っているか（ボタンを押せる）。</summary>
    private bool LoopRequestPending(TrainRun run, StationRun st) =>
        !st.Done && st.StoppedUtc != null && NeedsLoopDepartRequest(run, st);

    /// <summary>出発要求をしないまま止まっている列車か（始発の要求前・待避線の要求前）。単線の交換・続行で、この列車は待たない。</summary>
    private bool WaitingForRequest(TrainRun run, StationRun st) =>
        st.IsStart && !st.DepartureRequested && !st.ShuntRequested || LoopRequestPending(run, st);

    /// <summary>待避線に入った列車は、止まるまで出発を開通させない（すぐに次の信号を要求しないように）。</summary>
    private string? LoopDwellHold(TrainRun run, StationRun st, DateTime now) =>
        IsLoopTrack(run, st) ? StopHold(st, now, "待避線で停車") : null;

    /// <summary>ダイヤグラムのスジずらしで決めた発車時刻の30秒前（踏切防護のある駅は40秒前）まで、出発を開通させない。</summary>
    private string? DepartTimeHold(TrainRun run, StationRun st, DateTime now)
    {
        var lead = DepartLead(st);
        if (_central.OpenAt(run.Train, st.St.Id) is not { } t || now >= t.AddSeconds(-lead)) return null;
        if (ShortDwell(run, st)) return null; // 司令が着から2分以内に繰り上げた: 場内と一緒に出発も開ける
        return $"発車時刻待ち（{_clock.TextAt(t)} 発。{lead}秒前に開通）";
    }

    /// <summary>スジずらしで決めた発車時刻の、何秒前に出発の進路を要求するか。</summary>
    private const int DepartLeadSeconds = 30;

    /// <summary>
    /// 出発の進路を、発車時刻の何秒前に要求するか。踏切防護（現示時素。RouteRules.json の clearTimes）のある駅は、
    /// 要求してから信号が開くまでその秒数かかるので、発車時刻の20秒前には信号が開いているように早める
    /// （時素20秒なら40秒前。2026-11-19 ユーザーの決まり）
    /// </summary>
    private int DepartLead(StationRun st)
    {
        var prefix = st.St.Interlock + "/";
        var clear = _data.ClearTimes.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
        return clear > 0 ? Math.Max(DepartLeadSeconds, ClearOpenBeforeSeconds + clear) : DepartLeadSeconds;
    }

    /// <summary>踏切防護のある駅で、発車時刻の何秒前までに信号を開けておくか。</summary>
    private const int ClearOpenBeforeSeconds = 20;

    /// <summary>
    /// 番線に入りきって止まる（0km/h）まで、出発を開通させない。番線ごとの指定（大道寺の上り1番線など）がある番線は必ず、
    /// ない番線は、出発が開通しないまま入りきった（停止で入った）ときだけ。指定の秒数は「止まってから」の秒数。始発の列車には使わない。
    /// 番線ごとの指定は停車する列車だけにかける（通過列車は止めない）。
    /// </summary>
    private string? TrackDelayHold(TrainRun run, StationRun st, DateTime now)
    {
        if (st.IsStart || st.Track is not { } t) return null;
        var configured = st.St.DepartDelay.TryGetValue((run.Dir, t), out var conf) && !(IsPassingRoute(run, st) && !st.Passing);
        if (!configured && st.InTrackUtc == null) return null; // 入りきる前に開通させるのはかまわない
        return StopHold(st, now, "停車", configured ? conf : 0);
    }

    /// <summary>行き止まり（折り返し）の番線に入るか。入ると、この駅で打ち切り。</summary>
    private static bool IsTurnbackTrack(TrainRun run, StationRun st) =>
        !st.Terminal && st.Track is { } t && st.St.Routes[run.Dir] is var routes && routes.Turnback.Contains(t) && !routes.Depart.ContainsKey(t);

    /// <summary>手前の駅で、行き止まりの番線に入って打ち切る（この駅には来ない）か。</summary>
    private static bool IsCutOff(TrainRun run, StationRun st)
    {
        foreach (var s in run.Stations)
        {
            if (s == st) return false;
            if (!s.Done && (IsTurnbackTrack(run, s) || s.Terminal)) return true;
        }
        return false;
    }

    /// <summary>盤で扱っている駅（管理画面で駅扱い、かつ信号制御盤の解放の鍵が R）か。自動では進路（入換を含む）を取らない。手動（駅扱いの強制進路）だけ通す</summary>
    private bool PanelControlled(TrainRun run, StationRun st) => _central.IsPanelControlled(st.St.Id) && !_central.IsForced(run.Train, st.St.Id);

    private void Process(TrainRun run, StationRun st, Dictionary<string, TrainRun> occupied, DateTime now)
    {
        if (IsCutOff(run, st)) return;
        if (st.Done || !st.Locked && !st.IsStart && !_central.IsForced(run.Train, st.St.Id)) return;
        UpdateCoupling(run, st, occupied, now);
        UpdateSplit(run, st, now);

        // 終着の列車の入換（引上げ）: 要求があれば、番線に入りきってから入換進路を開通させる
        if (st.ShuntOutChoice is { } outIndex && st.ShuntOut != GroupState.Requested)
        {
            var outOptions = ShuntOutOptions(run, st);
            if (outIndex >= outOptions.Count) st.ShuntOutChoice = null;
            else
            {
                var outDef = ShuntOutPath(run, st, outOptions[outIndex], occupied);
                var outPath = outDef.Routes;
                var outWait = !st.IsStart && !FullyInTrack(run, st) ? "列車が番線に入るまで"
                    : !st.IsStart && StopHold(st, now, "停車") is { } stopWait ? stopWait
                    : st.ForceShuntOut ? InterlockConflict(run, outPath, occupied)
                    : PanelControlled(run, st) ? PanelWait
                    : (_central.Hold(run.Train, st.St.Id) & HoldKind.Depart) != 0 ? "出発抑止（集中扱い）"
                    : SplitOrderHold(run, st) ?? Conflict(run, st, outPath, occupied, departure: true)
                      // 留置線へ入れる引上げ（X線で向きを変えて 5番線へ）は、留置線までの進路の範囲も空いてから引き上げる（X線で止まったままにしない）
                      ?? (outDef.ThenRoutes.Count > 0 ? InterlockConflict(run, outDef.ThenRoutes, occupied) : null);
                if (outWait != null)
                {
                    ShuntWaitLog(now, run, st, "引上げ", st.ShuntOutWait, outWait);
                    (st.ShuntOut, st.ShuntOutWait) = (GroupState.Waiting, outWait);
                }
                else
                {
                    RouteLog(now, run, st, st.ForceShuntOut ? "引上げ（駅扱い）" : "引上げ", outPath);
                    foreach (var outRoute in Enumerable.Reverse(outPath))
                        run.Requests.Add(new RouteReq { Def = outRoute, Id = ++_seq, Station = st, Departure = true, RequestedUtc = now });
                    (st.ShuntOut, st.ShuntOutWait, st.ForceShuntOut) = (GroupState.Requested, "", false);
                    (st.ShuntThenPath, st.ShuntThenAt, st.ShuntThen, st.ShuntThenWait) = outDef.ThenRoutes.Count > 0
                        ? (outDef.ThenRoutes, outPath[^1].Circuits.LastOrDefault(), GroupState.None, "") : (null, null, GroupState.None, "");
                    _log.Note($"入換: {run.Train} {st.St.Name} {st.Track}番線→{outOptions[outIndex].Label}{(outDef.Via != null ? $"（{outDef.Via}経由）" : "")}（{string.Join("・", outPath.Select(x => x.Name))}）");
                }
            }
        }

        // 引上げの2つ目の進路（X線で向きを変えて 5番線へ）
        if (!st.IsStart && st.ShuntOut == GroupState.Requested && st.ShuntThenPath is { Count: > 0 } outThen && st.ShuntThenAt is { } outAt
            && st.ShuntThen != GroupState.Requested)
            RequestThen(run, st, outThen, outAt, departure: true, occupied, now);

        // 駅扱いの、引き上げた線（X線）から番線への入換。列車がその線を出ていれば取りやめる
        if (st.ManualShuntFrom is { } msf)
        {
            if (msf.FromCircuit is not { } mfc || !run.Player.TrackCircuits.Contains(mfc)) st.ManualShuntFrom = null;
            else if (InterlockConflict(run, msf.Routes, occupied) is { } msfWait)
            {
                ShuntWaitLog(now, run, st, "入換（駅扱い）", st.ManualShuntFromWait, msfWait);
                st.ManualShuntFromWait = msfWait;
            }
            else
            {
                RouteLog(now, run, st, "入換（駅扱い）", msf.Routes);
                foreach (var r in Enumerable.Reverse(msf.Routes))
                    run.Requests.Add(new RouteReq { Def = r, Id = ++_seq, Station = st, Departure = true, RequestedUtc = now });
                _log.Note($"入換: {run.Train} {st.St.Name} {msf.FromLabel}→{msf.To}番線（駅扱い。{string.Join("・", msf.Routes.Select(x => x.Name))}）");
                (st.ManualShuntFrom, st.ManualShuntFromWait) = (null, "");
            }
        }

        // 前を走る同じ方向の列車がこの駅をまだ通っていないのに、後ろの自分が先に進路を取っていたら、譲る（まだ進路に入っていなければ取り消す）
        // 駅扱い（強制進路）で開通させた進路は譲らない
        if (!st.IsStart && !st.ManualUsed && (st.Arrive == GroupState.Requested || st.Depart == GroupState.Requested)
            && AheadHold(run, st, departure: st.Depart == GroupState.Requested) is { } aheadWhy)
        {
            var active = run.Requests.Where(r => r.Station == st && !r.Released && r.CancelledUtc == null).ToList();
            if (!active.Any(r => r.Def.Circuits.Any(run.Player.TrackCircuits.Contains)))
            {
                foreach (var r in active) r.CancelledUtc = now;
                (st.Arrive, st.ArriveWait) = (GroupState.None, "");
                (st.Depart, st.DepartWait) = (GroupState.None, "");
                _log.Note($"進路を譲る: {run.Train} {st.St.Name} … {aheadWhy}（{string.Join("・", active.Select(r => r.Def.Name))} を取消）");
            }
        }

        // 始発駅で集中扱いの「開通指示」があれば、入換要求・出発要求の代わりにする（司令役が列車を出発させる）
        if (st.IsStart && _central.IsForced(run.Train, st.St.Id))
        {
            if (st.ShuntFrom != null) st.ShuntRequested = true;
            else st.DepartureRequested = true;
        }
        // 待避線でも、集中扱いの「開通指示」は出発要求の代わりにする
        if (!st.IsStart && _central.IsForced(run.Train, st.St.Id) && IsLoopTrack(run, st)) st.DepartureRequested = true;

        // 始発の入換（引上線などからホームへ）: 入換要求があったら、入換進路を要求する
        if (st.IsStart && st.ShuntFrom is { } shuntFrom)
        {
            // 2つ目の進路（5番線 → X線 と来て、X線で向きを変えて 2番線へ）
            if (st.Shunt == GroupState.Requested && st.ShuntThenPath is { Count: > 0 } then && st.ShuntThenAt is { } thenAt && st.ShuntThen != GroupState.Requested)
            {
                RequestThen(run, st, then, thenAt, departure: false, occupied, now);
                return;
            }
            if (!st.ShuntRequested || st.Shunt == GroupState.Requested || st.Track is not { } to) return;

            // 同じ元・同じ番線への入換が複数あれば、書いた順に優先。RequireFree の回路に他の列車がいれば次の候補。
            // 前の列車の折返に、出るときに通る線（Out。5番線から Y線か X線か）が書かれていれば、その線を通るものだけ
            var cands = st.St.Routes[run.Dir].Shunts.Where(s => s.FromLabel == shuntFrom && s.To == to).ToList();
            if (run.Dia != null && DiaShuntTurns.GetValueOrDefault(run.Dia.Number)?.Out is { } outVia && cands.Any(s => s.Via == outVia))
                cands = cands.Where(s => s.Via == outVia).ToList();
            var choice = cands.FirstOrDefault(s => s.RequireFree.All(c => !occupied.TryGetValue(c, out var o) || o == run));
            var path = choice?.Routes;
            if (path == null || path.Count == 0)
            {
                ShuntWaitLog(now, run, st, "入換", st.ShuntWait, "入換に使う線路がふさがっています");
                (st.Shunt, st.ShuntWait) = (GroupState.Waiting, "入換に使う線路がふさがっています");
                return;
            }

            st.ShuntVia = choice!.Via;
            path = CoupleShuntPath(st, path);
            var wait = st.ForceShunt ? InterlockConflict(run, path, occupied, st.CouplePartner)
                : PanelControlled(run, st) ? PanelWait
                : _central.IsHeld(run.Train, st.St.Id) ? "抑止（集中扱い）"
                : TimeWait(run, st, now) ?? DiaShuntHold(run, st) ?? AfterHold(run, st, path) ?? DiaShuntTrackHold(run, st, to)
                  ?? (st.CoupleWaiting is { } cw ? $"増結車（{cw}）が番線に入るまで" : null)
                  ?? (st.CouplePartner is { } cpart ? InterlockConflict(run, path, occupied, cpart) : Conflict(run, st, path, occupied, departure: false));
            if (wait != null)
            {
                ShuntWaitLog(now, run, st, "入換", st.ShuntWait, wait);
                (st.Shunt, st.ShuntWait) = (GroupState.Waiting, wait);
                return;
            }

            RouteLog(now, run, st, st.ForceShunt ? "入換（駅扱い）" : "入換", path);
            foreach (var r in Enumerable.Reverse(path))
                run.Requests.Add(new RouteReq { Def = r, Id = ++_seq, Station = st, Departure = false, RequestedUtc = now });
            (st.Shunt, st.ShuntWait) = (GroupState.Requested, "");
            (st.ShuntThenPath, st.ShuntThenAt, st.ShuntThen, st.ShuntThenWait) = choice.ThenRoutes.Count > 0
                ? (choice.ThenRoutes, path[^1].Circuits.LastOrDefault(), GroupState.None, "") : (null, null, GroupState.None, "");
            if (st.ForceShunt) (st.ForceShunt, st.ManualUsed) = (false, true);
            return;
        }

        // 始発駅: 出発要求があったら、その時点の番線で確定する
        if (st.IsStart && !st.Locked)
        {
            if (DiaMode && run.Dia != null && !st.Terminal && st.ShuntFrom == null) st.DepartureRequested = true; // ダイヤ運転中は要求なしで（時刻で開ける）
            if (!st.DepartureRequested && !_central.IsForced(run.Train, st.St.Id))
            {
                if (!st.Terminal && st.Track != null) (st.Depart, st.DepartWait) = (GroupState.Waiting, "出発要求待ち");
                return;
            }

            st.Locked = true;
            ResolvePaths(run, st);
        }
        if (_central.IsForced(run.Train, st.St.Id) && !st.Locked)
        {
            st.Locked = true;
            if (!st.Triggered) { st.Triggered = true; st.TriggeredUtc = now; }
            ResolvePaths(run, st);
        }

        if (st.ArrivePath.Count == 0 && st.DepartPath.Count == 0 && st.Locked) ResolvePaths(run, st);

        var hold = _central.Hold(run.Train, st.St.Id);
        var arriveHeld = (hold & HoldKind.Arrive) != 0;
        var departHeld = (hold & HoldKind.Depart) != 0;
        var central = _central.IsCentral(st.St.Id) && !_central.IsForced(run.Train, st.St.Id);
        // 盤で扱っている駅（管理画面で駅扱い、かつ信号制御盤の解放の鍵が R）は、自動では進路を取らない。手動（駅扱いの強制進路）だけ通す
        var panel = PanelControlled(run, st);
        var timeWait = TimeWait(run, st, now);

        // 場内（通過を含む）を開通できるか。先に判定だけして、送るのは出発のあと
        string? arriveWait = null, approachWait = null;
        var needArrive = st.ArrivePath.Count > 0 && st.Arrive != GroupState.Requested && !st.IsStart;
        st.ArriveBlocked = false;
        if (needArrive)
        {
            string? conflict = null;
            if (st.ForceArrive) arriveWait = conflict = InterlockConflict(run, CoupleArrivePath(st), occupied, st.CouplePartner); // 駅扱い: 連動の確認だけ
            else
                arriveWait = panel ? PanelWait
                    : arriveHeld ? "場内抑止（集中扱い）"
                    : central ? "集中扱い（開通指示待ち）"
                    : timeWait ?? SingleEntryHold(run, st) ?? PrevDepartHold(run, st) ?? (approachWait = ApproachHold(run, st)) ?? (st.Track == null ? DepartTimeHold(run, st, now) : null)
                      ?? DiaStartFirstHold(run, st)
                      ?? (conflict = Conflict(run, st, CoupleArrivePath(st), occupied, departure: false));
            if (arriveWait == null && !st.ForceArrive && st.CoupleWaiting is { } cw) arriveWait = $"増結車（{cw}）が番線に入るまで";
            st.ArriveBlocked = arriveWait != null && ReferenceEquals(arriveWait, conflict);
            // 第2場内の取り始め待ちだけなら、第1場内を先に開ける駅では第1場内だけ開ける
            if (approachWait != null && ReferenceEquals(arriveWait, approachWait)) RequestFirstHome(run, st, occupied, DiaStartFirstHold(run, st), now);
            if (arriveWait != null && !st.ForceArrive && st.TrackSource == "待避" && st.Track is { } lt && LoopUser(run, st, lt) is { } lu)
                arriveWait = $"待避線ふさがり（{lu.Train}）";
        }

        // 出発順序待ちの判断は、下の出発の判断でも使う（同じ判断を2回しない）
        var orderHold = st.DepartPath.Count > 0 && st.Depart != GroupState.Requested ? OrderHold(run, st, st.DepartPath) : null;
        st.OrderWaiting = orderHold != null;

        // 出発（場内より先に送る）。場内を開通できない（先行列車が番線にいるなど）うちは、出発も要求しない
        st.DepartBlocked = false;
        if (st.DepartPath.Count > 0 && st.Depart != GroupState.Requested)
        {
            string? wait, conflict = null;
            if (st.ForceDepart) wait = conflict = InterlockConflict(run, st.DepartPath, occupied); // 駅扱い: 連動の確認だけ
            else if (panel) wait = PanelWait;
            else if (departHeld) wait = "出発抑止（集中扱い）";
            else if (central) wait = "集中扱い（開通指示待ち）";
            else if (timeWait != null) wait = timeWait;
            else if (st.IsStart && !st.DepartureRequested) wait = "出発要求待ち";
            else if (DepartBeforeArrive(st) && !st.IsStart && ApproachHold(run, st) is { } aw) wait = aw;
            else if (needArrive && arriveWait != null && !DepartBeforeArrive(st)) wait = $"場内待ち（{arriveWait}）";
            else if (st.CouplePartner != null || st.CoupleWaiting != null) wait = "連結待ち";
            else if (needArrive && LeadsToSingleTrack(run, st)) wait = "単線区間のため、場内の開通後に判断";
            else if (orderHold is { } ow) wait = ow;
            else wait = PassingHold(run, st, now)
                        ?? DepartStopHold(run, st, needArrive)
                        ?? LoopDwellHold(run, st, now)
                        ?? (NeedsLoopDepartRequest(run, st) ? "出発要求待ち" : null)
                        ?? SingleTrackHold(run, st)
                        ?? TrackDelayHold(run, st, now)
                        ?? DepartTimeHold(run, st, now)
                        ?? DiaDepartHold(run, st)
                        ?? ConnectionHold(run, st)
                        ?? SplitOrderHold(run, st)
                        ?? (conflict = Conflict(run, st, st.DepartPath, occupied, departure: true));
            st.DepartBlocked = wait != null && ReferenceEquals(wait, conflict);

            if (wait == null)
            {
                NoteDepartAhead(run, st, st.DepartPath, now);
                Request(run, st, st.DepartPath, departure: true, now);
                if (st.ForceDepart) (st.ForceDepart, st.ManualUsed) = (false, true);
            }
            else (st.Depart, st.DepartWait) = (GroupState.Waiting, wait);
        }

        if (needArrive)
        {
            if (arriveWait == null)
            {
                Request(run, st, CoupleArrivePath(st), departure: false, now);
                if (st.ForceArrive) (st.ForceArrive, st.ManualUsed) = (false, true);
            }
            else (st.Arrive, st.ArriveWait) = (GroupState.Waiting, arriveWait);
        }
    }

    // =========================================================================================
    // 連結・解放（大道寺の「併B 前2増」など）
    // ・増結車は、列番が「連」＋この列車の列番の列車（連777 → 777）と、ダイヤでその列番に折り返す列車（回730 → 連777）。
    //   増結車が止まる駅（ダイヤの終着、ダイヤがなければ今いる駅）で連結する
    // ・増結車が番線に入って止まるまで、場内は取らない
    // ・増結車が番線にいれば、番線の手前までの進路だけ取る（本線から 大道寺 13LC・13LD、新野崎 10LC・10LD。
    //   入換で入る始発の列車は 大道寺 E線から 29LC・29LD、Y線から 32LC・32LD、新野崎 5番線から 25LC・25LD）
    // ・番線へ入る進路は、いつも入換信号機（大道寺 27L・28L、新野崎 21L・22L）。番線の場内（11L・12L・4L・5L）は開けない。
    //   列車が番線の手前の回路（大道寺 44T・45T、新野崎 34T）に入って完全に止まってから開ける。入換信号機がない番線へは入れない
    // ・増結車の在線は、この列車の進路・信号では見ない
    // ・駅扱いの「誘導開通」: 番線にいる列車の列番から「連」を除いたもの（連1283C → 1283C）が入ろうとしている列車のときだけ選べる。
    //   止まるのを待たずに、番線の場内まで開ける（早引き）
    // ・連結するまで出発は取らない。連結したら（増結車が消えたら）ふつうに出発
    // =========================================================================================

    private static readonly TimeSpan CoupleStopCheck = TimeSpan.FromSeconds(3);  // 完全に止まったとみなすまで
    private static readonly TimeSpan CoupleDoneStop = TimeSpan.FromSeconds(30);  // ユーザーの列車: 番線の中でこれだけ止まっていたら連結した
    private readonly List<(string Partner, string Train)> _coupled = new();

    /// <summary>この列車に連結する増結車の列番（連777 と、連777 に折り返す 回730）。</summary>
    private HashSet<string> CouplingPartnerNumbers(string train)
    {
        var first = "連" + train;
        var set = new HashSet<string> { first };
        if (TurnPredecessors().TryGetValue(first, out var preds)) set.UnionWith(preds);
        return set;
    }

    // 折返のつなぎの逆引き（後運用の列番 → そこへ折り返す列車の列番）。駅・列車ごとに全ダイヤを調べ直さないよう、
    // ダイヤ（臨時列車の足し外し）か後運用の差し替えが変わったときだけ作り直す
    private (Dictionary<string, DiaTrain>? Dia, int Version, Dictionary<string, List<string>> Map) _turnPreds = (null, -1, new());

    private Dictionary<string, List<string>> TurnPredecessors()
    {
        var dia = _data.Dia;
        var version = _data.Turns.Version;
        var cache = _turnPreds;
        if (ReferenceEquals(cache.Dia, dia) && cache.Version == version) return cache.Map;
        var map = new Dictionary<string, List<string>>();
        foreach (var d in dia.Values)
            if (_data.Turns.TurnOf(d) is { Kind: "折返", Number: { } n })
            {
                if (!map.TryGetValue(n, out var list)) map[n] = list = new List<string>(1);
                list.Add(d.Number);
            }
        _turnPreds = (dia, version, map);
        return map;
    }

    /// <summary>増結車が止まる駅: ダイヤの終着。ダイヤがなければ（手入力の 連777 など）今いる駅。</summary>
    private static string? PartnerStation(TrainRun r) =>
        r.Dia?.TerminalId ?? r.Stations.FirstOrDefault(s => !s.Done)?.St.Id;

    /// <summary>番線の場内と同じ所から同じ回路へ入る入換信号機の進路（大道寺 11L → 27L、12L → 28L、新野崎 4L → 21L、5L → 22L）。</summary>
    internal static RouteDef? ParallelShuntOf(RailData data, RouteDef home) =>
        data.Routes.Values.FirstOrDefault(r => r.Interlock == home.Interlock && r.Name != home.Name && r.Kind.Contains("入換")
                                               && r.From == home.From && r.To == home.To && r.Circuits.SequenceEqual(home.Circuits));

    /// <summary>
    /// その駅・向き・番線で、増結車が待つ番線に列車が入って連結できるか（場内が2つ以上あり、番線の場内と同じ所へ入る入換信号機がある）。
    /// 運転課の臨時列車の確認に使う
    /// </summary>
    internal static bool CanCoupleAt(RailData data, string stationId, Dir dir, int track) =>
        data.Stations.TryGetValue(stationId, out var sd) && sd.RouteControl && sd.Routes[dir].Arrive.TryGetValue(track, out var path)
        && path.Count >= 2 && ParallelShuntOf(data, path[^1]) != null;

    /// <summary>
    /// 場内で取る進路。増結車が番線にいれば、番線の場内（最後の進路）を除き、あとで開けるのは同じ所へ入る入換信号機にする。
    /// 入換信号機がなければ、あとで開ける進路はない（番線へは入れない）。
    /// </summary>
    private List<RouteDef> CoupleArrivePath(StationRun st)
    {
        // 増結車がいない（いなくなった）: 前に決めた後回しの進路は忘れる（まだ進路を取る前にだけ呼ばれる）
        if (st.CouplePartner == null || st.ArrivePath.Count < 2) { (st.CoupleSecond, st.CoupleNoShunt, st.CoupleStopAt) = (null, false, null); return st.ArrivePath; }
        var first = st.ArrivePath.Take(st.ArrivePath.Count - 1).ToList();
        st.CoupleSecond = ParallelShuntOf(_data, st.ArrivePath[^1]);
        st.CoupleNoShunt = st.CoupleSecond == null;
        st.CoupleStopAt = CoupleStopCircuit(first, st.ArrivePath[^1]);
        return first;
    }

    /// <summary>連結: 番線の手前までの進路（場内なら第一場内、始発の入換なら番線へ入る進路の手前まで）を取ったか。</summary>
    private static bool FirstStageRequested(StationRun st) =>
        st.IsStart ? st.Shunt == GroupState.Requested : st.Arrive == GroupState.Requested;

    /// <summary>始発の入換で取る進路。増結車が番線にいれば、最後の進路（番線へ入る 27L・28L・21L・22L）を除く（あとで開ける）。</summary>
    private List<RouteDef> CoupleShuntPath(StationRun st, List<RouteDef> path)
    {
        if (st.CouplePartner == null || path.Count < 2) { (st.CoupleSecond, st.CoupleNoShunt, st.CoupleStopAt) = (null, false, null); return path; }
        var last = path[^1];
        var first = path.Take(path.Count - 1).ToList();
        st.CoupleSecond = last.Kind.Contains("入換") ? last : null;
        st.CoupleNoShunt = st.CoupleSecond == null;
        st.CoupleStopAt = CoupleStopCircuit(first, last);
        return first;
    }

    /// <summary>
    /// 番線の手前の回路: 先に取った進路の回路のうち、番線へ入る進路の回路を除いた最後のもの（大道寺 44T・45T、新野崎 34T）。
    /// 列車がここに入って止まってから、番線へ入る進路を開ける（第一場内の手前で止まっていたのを、止まったとみなさないように）。
    /// </summary>
    private static string? CoupleStopCircuit(List<RouteDef> first, RouteDef last) =>
        first.SelectMany(r => r.Circuits).Where(c => !last.Circuits.Contains(c)).LastOrDefault();

    /// <summary>駅扱いの「誘導開通」の相手: その番線にいる、列番が「連」＋この列車の列番の列車。いなければ null。</summary>
    private TrainRun? GuidePartner(TrainRun run, StationRun st, int track) =>
        st.St.TrackCircuit.TryGetValue(track, out var tc)
            ? _runs.Values.FirstOrDefault(r => r != run && r.Train == "連" + run.Train && r.Player.TrackCircuits.Contains(tc))
            : null;

    private void UpdateCoupling(TrainRun run, StationRun st, Dictionary<string, TrainRun> occupied, DateTime now)
    {
        st.CouplePartner = null;
        st.CoupleWaiting = null;
        // 始発は、入換で番線に入る列車だけ（番線に入りきると入換の印は消えるが、番線へ入る進路を後回しにした列車は続ける）
        if (st.Coupled || st.IsStart && st.ShuntFrom == null && st.CoupleSecond == null && !st.CoupleNoShunt || st.Track is not { } track || !st.St.TrackCircuit.TryGetValue(track, out var tc)) return;
        var numbers = CouplingPartnerNumbers(run.Train);
        var partners = _runs.Values.Where(r => r != run && numbers.Contains(r.Train) && PartnerStation(r) == st.St.Id).ToList();
        if (partners.Count == 0 && st.CoupleSecond == null) return;
        var partner = partners.FirstOrDefault(r => r.Player.TrackCircuits.Count > 0 && r.Player.TrackCircuits.All(c => c == tc) && r.Player.Speed < StopSpeedKmh);
        if (partner == null)
        {
            // 増結車がまだ番線にいない（入換の途中など）。自分がもう番線に入っていれば待たない
            if (partners.Count > 0 && !run.Player.TrackCircuits.Contains(tc)) st.CoupleWaiting = partners[0].Train;
            // 場内を取ったあと増結車がいなくなった（消えた）: 番線がもう空いていれば、ふつうに番線へ入る進路を開ける
            // （番線にまだ何かいるうちは開けない）
            if (partners.Count == 0 && (st.CoupleSecond != null || st.CoupleNoShunt) && !st.CoupleSecondRequested && FirstStageRequested(st)
                && !occupied.ContainsKey(tc)
                && (st.IsStart ? st.CoupleSecond : st.ArrivePath.Count > 0 ? st.ArrivePath[^1] : null) is { } rest)
            {
                run.Requests.Add(new RouteReq { Def = rest, Id = ++_seq, Station = st, Departure = false, RequestedUtc = now });
                st.CoupleSecondRequested = true;
                _log.Note($"連結: {run.Train} {st.St.Name} 増結車がいなくなり番線が空いたので、番線へ入る進路 {rest.Name} を開けます");
            }
            return;
        }
        st.CouplePartner = partner;

        // 入換信号機のない番線: 番線へ入る進路は開けない
        if (st.CoupleNoShunt && !st.CoupleSecondRequested)
        {
            if (FirstStageRequested(st)) st.ArriveWait = "連結: この番線へ入る入換信号機がないので、番線へは入れません";
            return;
        }

        // 番線へ入る入換信号機: 先の進路を取ったあと、列車が番線の手前の回路に入って完全に止まってから開ける（駅扱いの誘導開通なら、止まるのを待たない）
        if (st.CoupleSecond is { } second && !st.CoupleSecondRequested)
        {
            if (!FirstStageRequested(st)) return;
            if (!st.CoupleGuide)
            {
                var stopAt = st.CoupleStopAt;
                var before = stopAt != null && run.Player.TrackCircuits.Contains(stopAt) && !run.Player.TrackCircuits.Any(second.Circuits.Contains);
                if (before && run.Player.Speed < StopSpeedKmh) st.CoupleStoppedUtc ??= now;
                else st.CoupleStoppedUtc = null;
                if (st.CoupleStoppedUtc is not { } stopped || now - stopped < CoupleStopCheck) return;
            }
            if (InterlockConflict(run, [second], occupied, partner) is { } why)
            {
                st.ArriveWait = $"連結の入換信号機待ち（{why}）";
                return;
            }
            run.Requests.Add(new RouteReq { Def = second, Id = ++_seq, Station = st, Departure = false, RequestedUtc = now });
            (st.CoupleSecondRequested, st.CoupleStoppedUtc) = (true, null);
            _log.Note(st.CoupleGuide
                ? $"連結: {run.Train} {st.St.Name} 駅扱いの誘導開通で、番線へ入る進路 {second.Name} を開けます（増結車 {partner.Train}）"
                : $"連結: {run.Train} {st.St.Name} 止まったのを確かめて、番線へ入る進路 {second.Name} を開けます（増結車 {partner.Train}）");
            return;
        }

        // ユーザーの列車: 番線に入りきって、しばらく止まっていたら連結したとする（自動運転列車は AiTrains が決める）
        if (st.CoupleSecondRequested && !PlayerRegistry.IsVirtual(run.ConnId)
            && run.Player.TrackCircuits.Count > 0 && run.Player.TrackCircuits.All(c => c == tc))
        {
            if (run.Player.Speed < StopSpeedKmh) st.CoupleStoppedUtc ??= now;
            else st.CoupleStoppedUtc = null;
            if (st.CoupleStoppedUtc is { } s1 && now - s1 >= CoupleDoneStop)
            {
                (st.Coupled, st.CouplePartner) = (true, null);
                _coupled.Add((partner.Train, run.Train));
                _log.Note($"連結: {run.Train} に {partner.Train} を連結しました（{st.St.Name} {track}番線）");
            }
        }
    }

    /// <summary>連結した増結車（接続ID → 親の列番）。毎周期作り直す。運転画面にその旨を出す。</summary>
    private readonly Dictionary<string, string> _couplingDone = new();

    /// <summary>
    /// ユーザーが運転している増結車（連777 と、連777 に折り返す 回730）のいる軌道回路に、親の列車（777）が入ったら、その時点で連結したとする。
    /// 増結車の在線を消して（PlayerRegistry.Couple）、扱いを外す。その回路には親の列番が出る。
    /// 増結車が自動運転の列車なら、これまでどおり AiTrains が連結位置で消す
    /// </summary>
    private void CoupleUserCars(DateTime now)
    {
        foreach (var parent in _runs.Values.Where(r => r.HasPosition).ToList())
        {
            var numbers = CouplingPartnerNumbers(parent.Train);
            foreach (var child in _runs.Values.Where(r => r != parent && numbers.Contains(r.Train) && !PlayerRegistry.IsVirtual(r.ConnId)).ToList())
            {
                if (child.Player.TrackCircuits.Count == 0 || !parent.Player.TrackCircuits.Any(child.Player.TrackCircuits.Contains)) continue;
                if (!_players.Couple(child.ConnId, parent.Train)) continue;
                foreach (var st in parent.Stations.Where(s => !s.Done && s.CouplePartner == child)) (st.Coupled, st.CouplePartner, st.CoupleWaiting) = (true, null, null);
                RunLog(now, child, $"扱いを外しました（{parent.Train} に連結した）");
                _runs.Remove(child.ConnId);
                _couplingDone[child.ConnId] = parent.Train;
                _log.Note($"連結: {parent.Train} が {child.Train} のいる軌道回路に入ったので、{child.Train} の在線を消しました");
            }
        }
    }

    /// <summary>連結: 列車（接続ID）→ 駅ID → その番線で止まって待っている増結車の列番。自動運転列車が停止位置を決めるのに使う。</summary>
    public Dictionary<string, Dictionary<string, string>> CouplePartners()
    {
        lock (_lock)
            return _runs.Values
                .Select(r => (r.ConnId, Map: r.Stations.Where(s => !s.Done && s.CouplePartner != null).ToDictionary(s => s.St.Id, s => s.CouplePartner!.Train)))
                .Where(x => x.Map.Count > 0)
                .ToDictionary(x => x.ConnId, x => x.Map);
    }

    /// <summary>信号の上限で見ない軌道回路: 連結する列車には、番線で待っている増結車の在線を見せない（止まってから入換信号機で入るため）。</summary>
    public List<string> CouplingIgnored(string connId)
    {
        lock (_lock)
            return _runs.TryGetValue(connId, out var run)
                ? run.Stations.Where(s => !s.Done && s.CouplePartner != null).SelectMany(s => s.CouplePartner!.Player.TrackCircuits).ToList()
                : new List<string>();
    }

    // ---- 解放 ----
    // ・ダイヤに「解」＋この列車の列番の列車があれば、その最初の駅で切り離す（解2076C は大道寺2番線、解874 は大道寺3番線）
    // ・ユーザーの列車は、番線に入りきって完全に止まったら切り離す。切り離した車両は自動運転列車として同じ番線に出す（AiTrains）
    // ・自動運転列車は、AiTrains が駅に着いたときに切り離す
    // ・元の列車がその駅から先へ進む（2076C は日野森へ）なら、切り離した車両は元の列車が番線から出たあと、その跡地に出す
    //   （同じ回路に2本重ねると、在線の表示などで元の列車の位置に切り離した車両の列番が出てしまう）。
    //   元の列車がその駅で終わって折り返す（874 → 975）なら、切り離した車両が先に出るので、すぐ出す

    private readonly List<(string Child, string Parent, string Station, int Track)> _splits = new();
    private readonly List<((string Child, string Parent, string Station, int Track) Split, string Conn, string Circuit)> _splitsAfterLeave = new();

    private void UpdateSplit(TrainRun run, StationRun st, DateTime now)
    {
        // 自動運転列車は、着いたときに AiTrains が切り離す（すぐ折り返して列番が変わることがあるので）
        if (PlayerRegistry.IsVirtual(run.ConnId)) return;
        if (st.Split || st.IsStart || st.Track is not { } track || !st.St.TrackCircuit.TryGetValue(track, out var tc)) return;
        if (_data.DiaOf("解" + run.Train) is not { } child || child.Stops.FirstOrDefault(s => s.StationId != null)?.StationId != st.St.Id) return;
        var inTrack = run.Player.TrackCircuits.Count > 0 && run.Player.TrackCircuits.All(c => c == tc);
        if (inTrack && run.Player.Speed < StopSpeedKmh) st.SplitStoppedUtc ??= now;
        else st.SplitStoppedUtc = null;
        if (st.SplitStoppedUtc is not { } s0 || now - s0 < CoupleStopCheck) return;
        st.Split = true;
        var split = (child.Number, run.Train, st.St.Name, track);
        if (st.Terminal) _splits.Add(split);
        else _splitsAfterLeave.Add((split, run.ConnId, tc));
        _log.Note($"解放: {run.Train} から {child.Number} を切り離します（{st.St.Name} {track}番線" + (st.Terminal ? "）" : "。元の列車が出てから出します）"));
    }

    /// <summary>切り離した車両の側の列車なら、その「解」のダイヤ（解874 自身、または 解874 から折り返した 回933）。元の列車の側なら null。</summary>
    private DiaTrain? SplitOrigin(TrainRun r) => SplitOriginOf(r.Train);

    private DiaTrain? SplitOriginOf(string train)
    {
        if (train.StartsWith("解")) return _data.DiaOf(train);
        return SplitSuccessors().GetValueOrDefault(train);
    }

    // 「解」の列車の後運用の逆引き（後運用の列番 → その「解」のダイヤ）。番線で待つ列車ごとに毎周期、全ダイヤを調べ直さないよう、
    // ダイヤ（臨時列車の足し外し）か後運用の差し替えが変わったときだけ作り直す（TurnPredecessors と同じ）
    private (Dictionary<string, DiaTrain>? Dia, int Version, Dictionary<string, DiaTrain> Map) _splitSuccessors = (null, -1, new());

    private Dictionary<string, DiaTrain> SplitSuccessors()
    {
        var dia = _data.Dia;
        var version = _data.Turns.Version;
        var cache = _splitSuccessors;
        if (ReferenceEquals(cache.Dia, dia) && cache.Version == version) return cache.Map;
        var map = new Dictionary<string, DiaTrain>();
        foreach (var d in dia.Values)
            if (d.Number.StartsWith("解") && _data.Turns.TurnOf(d) is { Number: { } n })
                map.TryAdd(n, d); // 同じ後運用の「解」が2本あれば、ダイヤに書かれた順で先のもの
        _splitSuccessors = (dia, version, map);
        return map;
    }

    /// <summary>
    /// 切り離したあと、同じ番線にいる2本の出る順番。切り離した車両は、元の列車が着いた向きで後ろ側にいる。
    /// その向きに出るなら元の列車の側が先（2076C が出てから、解2076C が E線へ）、反対向きに出るなら切り離した車両の側が先
    /// （874 から折り返した 975 は、解874 から折り返した 回933 が出てから）。
    /// </summary>
    private string? SplitOrderHold(TrainRun run, StationRun st)
    {
        if (st.Track is not { } track || !st.St.TrackCircuit.TryGetValue(track, out var tc) || !run.Player.TrackCircuits.Contains(tc)) return null;
        var mine = SplitOrigin(run);
        foreach (var o in _runs.Values.Where(o => o != run && o.Player.TrackCircuits.Contains(tc)))
        {
            var theirs = SplitOrigin(o);
            if (mine == null && theirs != null && TrainInfo(theirs.Number)?.Dir is { } arrived && run.Dir != arrived)
                return $"切り離した車両（{o.Train}）が先";
            if (mine != null && theirs == null && TrainInfo(mine.Number)?.Dir is { } arrived2 && run.Dir == arrived2)
                return $"元の列車（{o.Train}）が先";
        }
        return null;
    }

    /// <summary>切り離した組（切り離した車両の列番, 元の列車の列番, 駅名, 番線）。取り出すと消える。</summary>
    public List<(string Child, string Parent, string Station, int Track)> TakeSplits()
    {
        lock (_lock)
        {
            // 元の列車が番線から出た（または運転をやめた）ものを、出す側へ回す
            foreach (var w in _splitsAfterLeave.Where(w => !_runs.TryGetValue(w.Conn, out var r) || !r.Player.TrackCircuits.Contains(w.Circuit)).ToList())
            {
                _splitsAfterLeave.Remove(w);
                _splits.Add(w.Split);
            }
            var list = _splits.ToList();
            _splits.Clear();
            return list;
        }
    }

    /// <summary>ユーザーの列車が連結を終えた組（増結車の列番, 列車の列番）。取り出すと消える。</summary>
    public List<(string Partner, string Train)> TakeCoupled()
    {
        lock (_lock)
        {
            var list = _coupled.ToList();
            _coupled.Clear();
            return list;
        }
    }

    /// <summary>この駅からの出発が、単線区間へ出るか。</summary>
    private bool LeadsToSingleTrack(TrainRun run, StationRun st) => NextControlStation(run, st) is { } next && _data.IsSingleTrack(st.St.Id, next);

    private string? NextControlStation(TrainRun run, StationRun st)
    {
        var step = run.Dir.Step();
        for (var z = st.Zone + 2 * step; z >= 0 && _data.Line.StationAtZone(z) is { } id; z += 2 * step)
            if (_data.Stations.ContainsKey(id)) return id;
        return null;
    }

    /// <summary>
    /// 連動の確認（駅扱いの強制進路用）: 進路の回路に他の列車がいない、他の列車の進路と重ならない。
    /// サーバーの抑止・待避・単線の交換・順番待ちなどは見ない。
    /// </summary>
    private string? InterlockConflict(TrainRun run, List<RouteDef> path, Dictionary<string, TrainRun> occupied, TrainRun? ignore = null)
    {
        // ignore: 見ない列車（連結で、番線で待っている増結車。その在線と、まだ残っている入換の進路）
        var circuits = Circuits(path).ToHashSet();
        foreach (var c in circuits)
            if (occupied.TryGetValue(c, out var o) && o != run && o != ignore) return $"在線（{o.Train}）";
        foreach (var other in _runs.Values.Where(o => o != run && o != ignore))
            if (other.Requests.Any(r => StillHeld(other, r) && LiveCircuits(other, r).Any(circuits.Contains)))
                return $"進路が重なる（{other.Train}）";
        return null;
    }

    /// <summary>
    /// 他の列車の進路が、まだその列車のものとして残っているか。取り消した進路も、その列車のゲームに残っている間は残っているとみなす
    /// （接近の区間にいて消せない進路。プレイヤーの列車ならゲームの連動が重なる進路を断るが、にぎやかしの列車はサーバーの判断だけで開けるので、
    /// ここで見ないと、取り消したつもりの進路と重なる進路を前の列車に開けてしまい、2本が同じ回路へ進む）。
    /// </summary>
    private static bool StillHeld(TrainRun owner, RouteReq r) =>
        !r.Released && (r.CancelledUtc == null || owner.Player.GameRoutes.Contains((r.Def.Interlock, r.Def.Name)));

    /// <summary>
    /// 他の列車の進路のうち、まだ使う回路。列車がその進路に入っていれば、列車より後ろの回路は使い終わっている
    /// （1番線に入りきった列車の場内進路が、2番線への場内とぶつかって見えないように）。回路はおおむね進路の順に並んでいる。
    /// </summary>
    private static IEnumerable<string> LiveCircuits(TrainRun owner, RouteReq r)
    {
        var c = r.Def.Circuits;

        // 番線に入る進路で、列車が番線に入りきっていれば、まだ使うのは番線の回路だけ。
        // 回路の並びが進路の順になっていない進路がある（水越の 12R は「21イT・12RT・21ロT」で、21ロT は 12RT より後ろに並ぶが、
        // 列車は通らない）。並びのまま見ると、1番線に停まっている列車の 12R が、2番線から出る 13L と重なって見えて、出られなくなる
        if (!r.Departure && r.Station.Track is { } t && r.Station.St.TrackCircuit.TryGetValue(t, out var platform)
            && c.Contains(platform) && FullyInTrack(owner, r.Station, c))
            return [platform];

        // 行き先が軌道回路の進路（大道寺の 22RX・30R など）も同じ。行き先の回路に入りきっていれば、その回路だけ。
        if (DestCircuitOf(r.Def) is { } dest && owner.Player.TrackCircuits.Contains(dest)
            && c.Where(x => x != dest).All(x => !owner.Player.TrackCircuits.Contains(x)))
            return [dest];

        for (var i = 0; i < c.Count; i++)
            if (owner.Player.TrackCircuits.Contains(c[i])) return c.Skip(i);
        return c;
    }

    /// <summary>進路の行き先が軌道回路の名前なら、その回路（データには半角の「ﾛ」もあるので、全角にそろえて比べる）。</summary>
    private static string? DestCircuitOf(RouteDef def)
    {
        var to = def.To.Normalize(System.Text.NormalizationForm.FormKC);
        if (to.Length == 0) return null;
        return def.Circuits.FirstOrDefault(c => c == to || c.EndsWith("_" + to, StringComparison.Ordinal));
    }

    /// <summary>
    /// 出発停止: 出発停止の番線に入る場内進路、または始発の入換進路が、まだ解除されていなければ（列車がその番線に入りきっていなければ）待つ理由。
    /// 連動で鎖錠されているので、同時に送ってもゲームに競合で断られる。解除はゲームの自動解除（列車が進路を抜けたとき）。
    /// </summary>
    private string? DepartStopHold(TrainRun run, StationRun st, bool arrivalNotYetRequested)
    {
        if (TypeChangesAt(run, st)) return "出発停止（種別が変わる。止まって列番が変わってから出発）";
        if (st.DepartStop && arrivalNotYetRequested) return "出発停止（場内の開通待ち）";
        if (!st.DepartStop && !st.IsStart) return null;
        var pending = run.Requests.Where(r => r.Station == st && !r.Departure && !r.Released && r.CancelledUtc == null).ToList();
        if (pending.Count == 0) return null;

        // ゲームは鎖錠を見ない。場内が残っていても、番線に入りきっていれば進める
        return FullyInTrack(run, st, pending.SelectMany(r => r.Def.Circuits)) ? null : "出発停止（列車が番線に入るまで）";
    }

    /// <summary>
    /// 列車が番線に入りきったか: 番線の回路に入り、それより手前の回路（場内・入換の進路の回路）をすべて抜けた。
    /// 手前の回路を渡さなければ、その駅の場内の進路の回路で判断する。
    /// </summary>
    private static bool FullyInTrack(TrainRun run, StationRun st, IEnumerable<string>? before = null)
    {
        if (st.Track is not { } t || !st.St.TrackCircuit.TryGetValue(t, out var platform)) return false;
        if (!run.Player.TrackCircuits.Contains(platform)) return false;
        return (before ?? st.ArrivePath.SelectMany(r => r.Circuits)).Where(c => c != platform).All(c => !run.Player.TrackCircuits.Contains(c));
    }

    /// <summary>ダイヤグラムで開通時刻が指定されていて、まだその時刻になっていなければ、待つ理由。開通指示があれば待たない。フリーモードでは使わない。</summary>
    private string? TimeWait(TrainRun run, StationRun st, DateTime now)
    {
        if (!UseOpenTime || _central.IsForced(run.Train, st.St.Id)) return null;
        return _central.OpenAt(run.Train, st.St.Id) is { } t && now < t ? $"開通時刻待ち（{_clock.TextAt(t)}）" : null;
    }

    private void Request(TrainRun run, StationRun st, List<RouteDef> path, bool departure, DateTime now)
    {
        // 同じ進路を取り消した記録が残っていれば片付ける（残すと「消す」指示が、開き直した進路まで消してしまう）
        foreach (var old in run.Requests.Where(o => !o.Released && o.CancelledUtc != null && path.Contains(o.Def)))
            old.Released = true;

        // 遠い方から送る（信号機が、先の進路の開通を見てから現示を出せるように）。先に開けた第1場内（RequestFirstHome）は送り直さない
        var live = run.Requests.Where(o => o.Station == st && !o.Released && o.CancelledUtc == null).Select(o => o.Def).ToHashSet();
        foreach (var r in Enumerable.Reverse(path).Where(r => !live.Contains(r)))
            run.Requests.Add(new RouteReq { Def = r, Id = ++_seq, Station = st, Departure = departure, RequestedUtc = now });
        RouteLog(now, run, st, departure ? "出発" : st.Track == null ? "通過" : "場内", path);
        if (departure) (st.Depart, st.DepartWait) = (GroupState.Requested, "");
        else (st.Arrive, st.ArriveWait) = (GroupState.Requested, "");
    }

    private IEnumerable<string> Circuits(IEnumerable<RouteDef> path) => path.SelectMany(r => r.Circuits);

    /// <summary>進路の範囲に他の列車がいる・他の列車の進路と重なる・先に手前の駅を通過した列車が待っている・前方に列車がいる。</summary>
    private string? Conflict(TrainRun run, StationRun st, List<RouteDef> path, Dictionary<string, TrainRun> occupied, bool departure)
    {
        var circuits = Circuits(path).ToHashSet();
        foreach (var c in circuits)
            if (occupied.TryGetValue(c, out var o) && o != run) return $"在線（{o.Train}）";

        if (AheadHold(run, st, departure) is { } ahead) return ahead;

        foreach (var other in _runs.Values.Where(o => o != run))
        {
            if (other.Requests.Any(r => StillHeld(other, r) && LiveCircuits(other, r).Any(circuits.Contains)))
                return $"進路が重なる（{other.Train}）";

            // 同じ方向で後ろにいる列車は、順番待ちの相手にしない（前の列車が先。AheadHold で後ろの列車が待つ）
            if (IsAheadOf(run, other)) continue;

            // 先に開通の処理を始めて、同じ範囲を待っている列車。進路の競合（在線・重なり）で待っているものだけ
            // （単線の交換・待避・抑止・出発順序・出発要求待ちなどで自分から待っている列車を待つと、お互いに待ち合って止まる）
            foreach (var os in other.Stations.Where(s => s.St.Id == st.St.Id && !s.Done && s.Triggered && s.TriggeredUtc < st.TriggeredUtc))
            {
                // 相手が自分（の在線・進路）を待っているなら、自分が先（待ち合わない）
                var mention = $"（{run.Train}）";
                var theirs = (os.Arrive == GroupState.Waiting && os.ArriveBlocked && !os.ArriveWait.Contains(mention) ? os.ArrivePath : new List<RouteDef>())
                    .Concat(os.Depart == GroupState.Waiting && os.DepartBlocked && !os.DepartWait.Contains(mention) ? os.DepartPath : new List<RouteDef>());
                if (Circuits(theirs).Any(circuits.Contains)) return $"順番待ち（{other.Train}）";
            }
        }

        if (departure && path.Count > 0)
        {
            // 前方の防護区間（出発信号機から次の閉塞信号機まで）。同じ駅のほかの進路だけに入っている回路（もう一方の番線）は見ない
            var last = path[^1];
            if (_data.Signals.TryGetValue(last.Signal, out var sig))
                foreach (var c in sig.StopProtectsFor(k => k == (last.Interlock, last.Name)))
                    if (occupied.TryGetValue(c, out var o) && o != run) return $"前方在線（{o.Train}）";
        }

        return null;
    }

    /// <summary>
    /// 単線区間の先の駅 next で自分が入る番線を、対向列車がふさいでいるか、ふさぎに来ているか。
    /// ふさいでいる: 対向列車がその番線にいる（まだその駅を出ていない）。ふさぎに来ている: 対向列車が反対側の駅間を next に向かって走っている、
    /// または反対側の隣の駅で next へ向かう出発進路がもう出ている。対向列車は、自分が入ろうとしている区間を通らないと出られないので、待つ。
    /// </summary>
    private (TrainRun Run, string Why)? NextTrackBlocker(TrainRun run, string next)
    {
        if (run.Stations.FirstOrDefault(s => s.St.Id == next && !s.Done) is not { Track: { } track }) return null;
        var nextZone = _data.Line.ZoneOfStation(next);
        var nextName = _data.Line.StationName(next);
        foreach (var o in _runs.Values.Where(o => o != run && o.HasPosition && o.Dir != run.Dir))
        {
            var rest = o.Stations.Where(s => !s.Done).ToList();
            var i = rest.FindIndex(s => s.St.Id == next);
            if (i < 0 || rest[i].Track != track || IsCutOff(o, rest[i])) continue;
            var oStep = o.Dir.Step();
            if ((o.Front - nextZone) * oStep >= 0) return (o, $"{nextName}{track}番線に対向列車（{o.Train}）");
            if (i == 0 && o.Front == nextZone - oStep || i == 1 && rest[0].Depart == GroupState.Requested)
                return (o, $"{nextName}{track}番線に対向列車（{o.Train}）が向かっている");
        }
        return null;
    }

    /// <summary>
    /// 単線区間の出発（交換の判断）。駅間ごとに判断する。
    /// ・出発を決めるのは、列車がその駅に着いているか、その手前の駅間まで来てから（何駅も先の単線区間を先取りしない）。
    ///   これで、大道寺と水越に対向する列車がいれば藤江で、藤江と高見沢にいれば水越で行き違う
    /// ・次の駅までの区間に対向列車がいる、または相手の駅で対向列車の出発進路が出ていれば待つ。同じ方向の列車は続行してよい（間隔は信号と前方在線で守る）
    /// ・走行順（集中扱い）が決まっていれば、その順で流す
    /// ・なければダイヤグラムの見込みで、自分が相手の駅に着くより先に相手の駅に着く対向列車を、早い順に最大2本まで先に通す
    ///   （例: 1040 に対して 1051・903A・1041 が来るなら、903A まで待って出る）。待ち合いは、着くのが早い方が待つ（デッドロックしない）
    /// ・単線区間の駅は2線あるので、対向列車はもう片方の番線に入れる（AvoidTakenTrack）
    /// ・相手の駅で自分が入る番線を対向列車がふさいでいる・ふさぎに来ているなら、走行順で先でも待つ（NextTrackBlocker）
    /// </summary>
    private string? SingleTrackHold(TrainRun run, StationRun st)
    {
        var step = run.Dir.Step();
        var next = NextControlStation(run, st);

        if (next == null || !_data.IsSingleTrack(st.St.Id, next)) return null;
        var nextName = _data.Line.StationName(next);

        // まだこの駅の手前の駅間まで来ていなければ、交換の判断をしない（場内が停止なら、判断は直前でよい）
        var here = (run.Front - st.Zone) * step >= 0;            // 駅に着いている（構内か、その先）
        var approaching = run.Front == st.Zone - step;             // 手前の駅間
        // 進路を取り始める位置（ApproachTrigger）が入っていれば、そこを過ぎたら判断する
        // （通過列車の場内・出発をまとめて開けられるように。水越の上りが、藤江を通過する前に出発まで開くように）
        var approachPassed = ApproachOf(run, st) is { } ap && PassedApproach(run, st, ap);
        if (!st.IsStart && !here && !approaching && !approachPassed) return "交換の判断待ち（駅に近づくまで）";

        var nextZone = _data.Line.ZoneOfStation(next);
        var (lo, hi) = (Math.Min(st.Zone, nextZone), Math.Max(st.Zone, nextZone));

        // ---- 必ず待つ: 区間に対向列車がいる、相手の駅で対向列車の出発進路が出ている ----
        foreach (var other in _runs.Values.Where(o => o != run && o.HasPosition && o.Dir != run.Dir))
        {
            if (other.Player.Zones.Any(z => z > lo && z < hi)) return $"単線区間に対向列車（{other.Train}）";
            // 相手がもう区間を通り終えて（こちらの駅に入りきって）いれば、相手の出発進路の記録が残っていても待たない
            if (other.Requests.Any(r => !r.Released && r.CancelledUtc == null && r.Departure && r.Station.St.Id == next)
                && !SectionCleared(other, st.St.Id, next))
                return $"対向列車（{other.Train}）が{nextName}を出発";
        }

        var section = _data.SingleTrackSections.FirstOrDefault(s => s.Item1 == st.St.Id && s.Item2 == next || s.Item2 == st.St.Id && s.Item1 == next);
        // 区間へ逆向きに出る出発進路が出ている（手動進路構成で出したものを含む。進路の種別から向きを決める）
        foreach (var other in _runs.Values.Where(o => o != run && o.HasPosition))
            if (other.Requests.Any(r => !r.Released && r.CancelledUtc == null && DepartSection(r.Def, other.Dir) is { } ds && ds.Section == section && ds.Dir != run.Dir)
                && !SectionCleared(other, st.St.Id, next))
                return $"対向列車（{other.Train}）の出発進路が{st.St.Name}〜{nextName}へ出ています";

        // ---- 方向てこ: 区間が逆向きに鎖錠されている間は、こちら向きの出発を開通させない ----
        if (_levers.TryGetValue(section, out var lever) && lever.Locked && lever.Dir != run.Dir)
            return $"方向てこが{lever.Dir.Label()}向き（{st.St.Name}〜{nextName}）";

        // ---- 相手の駅の番線: 自分が入る番線を対向列車がふさいでいる・ふさぎに来るなら待つ（走行順で先でも） ----
        // 対向列車は、この区間を通らないとその番線から出られない。入っていくと、お互いに動けなくなる
        if (NextTrackBlocker(run, next) is { } blocker) return blocker.Why;

        // ---- 出現前の自動運転列車: 相手の駅にこれから出す対向列車が、ダイヤでこの列車より先に区間に入るなら待つ ----
        // 出す前は走っていないので走行順に入らない。先に区間へ出ると、出てきた列車が相手の駅で対向列車を待つことになる
        // （三郷始発の下りが、赤山町で早めに出発を開けた上りを三郷で5分待った）。走行順を手で決めていれば、そちらに従う。
        // 抑止中・時刻を過ぎても出せていない列車は待たない（出てこないかもしれない）
        if (DiaMode && _central.SectionOrder(st.St.Id, next) == null && DiaDaySec(run, st.St.Id, depart: true) is { } myDep)
            foreach (var p in PendingTrains())
            {
                if (p.Station != next || p.Held || p.Late || TrainInfo(p.Number) is not { } pi || pi.Dir == run.Dir) continue;
                var due = p.Due.TotalSeconds < 4 * 3600 ? p.Due.TotalSeconds + 86400 : p.Due.TotalSeconds;
                if (due < myDep) return $"出現前の対向列車（{p.Number}）が{nextName}から先";
            }

        // ---- 走行順（集中扱い）: 区間ごとに決めた順で流す ----
        // ダイヤ運転中（手で決めていなければ）は、いまサーバー上で走っているダイヤの列車だけで、区間に入るダイヤの時刻の順に並べる。
        // 交換の相手が走っていれば（参加していれば）その列車を待つ。走っていなければ待たない（上の「必ず待つ」で、区間と相手の駅の出発に
        // 列車がいないことは確かめてある。フリー運転と同じく、隣の駅まで進めるなら進路を取る）
        var order = EffectiveSectionOrder(st.St.Id, next); // ダイヤ運転中は、手で決めていなければダイヤの時刻順
        if (order != null && order.Contains(run.Train))
        {
            // 逆向きの列車は、区間を通り終えるまで待つ。同じ向きの列車は、この駅を出て区間に入れば続けて出てよい
            // （単線区間は続行運転ができる。間隔は閉塞信号機と前方在線で守る）
            // 同じ番線で後ろにいる同じ向きの列車も待たない（追い越せないので、順が先でも実際には後から出る。待つとお互いに動けなくなる）
            var before = order.TakeWhile(t => t != run.Train)
                .Where(t => RunByTrain(t) is { } p && !SectionCleared(p, st.St.Id, next) && !(p.Dir == run.Dir && EnteredSection(p, st))
                            && !SameTrackBehind(run, st, p)
                            // 後ろを走る同じ向きの列車は、ダイヤ・司令員が決めた追い越しでなければ待たない（後ろの列車は AheadHold で自分を待つ）
                            && !(p.Dir == run.Dir && IsAheadOf(run, p) && !MayOvertake(p, run, st))
                            // 先の対向列車が、この駅で自分のいる番線に入れずに待っているなら、自分が先（待つとお互いに動けない）
                            && !(p.Dir != run.Dir && NextTrackBlocker(p, st.St.Id)?.Run == run))
                .ToList();
            return before.Count > 0 ? $"走行順待ち（{st.St.Name}〜{nextName}: {string.Join("・", before)} が先）" : null;
        }

        // ---- 対向列車を先に通すか（ダイヤグラムの見込みで）。最大2本 ----
        // 自分が相手の駅に着くより、対向列車が相手の駅を出る見込みの方が十分早い（相手を駅で待たせることになる）なら、先に通す。
        // ほぼ同時なら相手の駅で行き違う（大道寺と水越の列車は藤江で）。お互いに相手を待つことになるとき（待ち合い）は、
        // 着くのが遅い方（走ってくる方）を先に通す。先に通すと決めたら、その列車が来るまで待つ（判断を変えない）
        var type = RunType(run);
        var runTime = _model.SecondsBetween(type, run.Dir, st.St.Id, next) ?? 180;
        double mineReady = here || st.IsStart ? 0 : _model.SecondsTo(type, run.Dir, run.Front, run.Fraction, st.St.Id) ?? 0;
        var mineArrNext = mineReady + (here || st.IsStart ? 0 : DwellSeconds(run, st)) + runTime; // 自分が相手の駅に着く見込み
        st.OpposingWaits.RemoveAll(t => RunByTrain(t) is not { } p || !ComesThrough(p, next, st.St.Id) || SectionCleared(p, st.St.Id, next));

        var candidates = _runs.Values
            .Where(o => o != run && o.HasPosition && o.Dir != run.Dir && ComesThrough(o, next, st.St.Id))
            .Where(o => !(order?.Contains(o.Train) ?? false))
            // 出発要求をしていない始発列車（止まったまま）は待たない
            .Where(o => o.Stations.FirstOrDefault(s => s.St.Id == next) is not { } ns || !WaitingForRequest(o, ns))
            .Select(o => (Run: o, Ready: ReadyAt(o, next)))
            .Where(x => x.Ready != null)
            .OrderBy(x => x.Ready)
            .ToList();
        foreach (var (o, ready) in candidates)
        {
            if (st.OpposingWaits.Contains(o.Train)) return $"対向列車（{o.Train}）を先に通す（{nextName}から）";
            if (st.OpposingWaits.Count + WaitedDone(st) >= MaxOpposingWaits) break;

            // 相手が自分を先に通すと決めているなら、待たない
            if (o.Stations.FirstOrDefault(s => s.St.Id == next && !s.Done) is { } os && os.OpposingWaits.Contains(run.Train)) continue;
            if (_central.IsHeld(o.Train, next)) continue; // 相手が抑止されていれば待たない

            // 相手が相手の駅を出られる見込み（着く＋停車時分）。自分がそこに着くより十分早ければ、相手を待たせることになる
            var theirDwell = o.Stations.FirstOrDefault(s => s.St.Id == next) is { } ons && ready!.Value > 0 ? DwellSeconds(o, ons) : 0;
            var theirLeaveNext = ready!.Value + theirDwell;
            if (theirLeaveNext + MeetToleranceSeconds >= mineArrNext) continue; // ほぼ同時か、自分が先に着く: 相手の駅で行き違う

            // 待ち合い: 相手から見ても、自分を待つことになる（自分がこの駅を出るのが、相手がこの駅に着くより十分早い）なら、着くのが早い方が待つ
            var theirRun = _model.SecondsBetween(RunType(o), o.Dir, next, st.St.Id) ?? 180;
            var mutual = mineReady + MeetToleranceSeconds < theirLeaveNext + theirRun;
            if (mutual && (ready.Value < mineReady || ready.Value == mineReady && string.CompareOrdinal(o.Train, run.Train) > 0)) continue;

            st.OpposingWaits.Add(o.Train);
            st.WaitedTotal++;
            _log.Note($"単線: {run.Train} は {st.St.Name} で対向の {o.Train} を先に通す（{nextName}に 自分が着く {mineArrNext:0}秒後・相手が出る {theirLeaveNext:0}秒後）");
            return $"対向列車（{o.Train}）を先に通す（{nextName}から）";
        }

        return null;
    }

    private const int MaxOpposingWaits = 2;

    /// <summary>行き違いで、相手を相手の駅で待たせてよい時間（秒）。これより長く待たせるなら、こちらが待つ。</summary>
    private const double MeetToleranceSeconds = 60;

    /// <summary>
    /// 単線区間を通って入る駅の場内は、手前の駅からその区間への出発が決まってから（行き違いの判断の前に、相手の駅の場内を取って
    /// 相手の出発をふさがないように）。駅を出たあと（区間の中）なら待たない。
    /// </summary>
    private string? SingleEntryHold(TrainRun run, StationRun st)
    {
        if (st.IsStart) return null;
        var prev = run.Stations.TakeWhile(s => s != st).LastOrDefault();
        if (prev == null || prev.Done || !_data.IsSingleTrack(prev.St.Id, st.St.Id)) return null;
        if ((run.Front - prev.Zone) * run.Dir.Step() > 0) return null; // もう手前の駅を出た
        if (prev.Depart == GroupState.Requested || prev.Terminal) return null;
        if (prev.Locked && prev.DepartPath.Count == 0) return null; // 手前の駅から出る進路がない（扱わない）
        return $"{prev.St.Name}の出発待ち（単線区間）";
    }

    /// <summary>
    /// 手前の駅（進路を扱う駅）の出発が、まだ開通していなければ待つ理由。出発抑止中や、出発が引けない（ほかの列車の進路と競合する・
    /// 待避中・単線の交換待ちなど）列車の、その先の駅の進路は取らない（取ると、ほかの列車の進路を無駄にふさぐため）。
    /// </summary>
    private string? PrevDepartHold(TrainRun run, StationRun st)
    {
        if (st.IsStart) return null;
        var prev = run.Stations.TakeWhile(s => s != st).LastOrDefault(s => !s.Done);
        if (prev == null || IsCutOff(run, prev) || prev.Terminal) return null;
        if ((run.Front - prev.Zone) * run.Dir.Step() > 0) return null; // もう手前の駅を出た
        // 始発の駅: 入換（引上線などからホーム）が終わり、出発進路を要求するまで、先の駅の進路は取らない
        if (prev.IsStart && prev.ShuntFrom != null) return $"{prev.St.Name}の入換待ち";
        if (prev.IsStart && prev.Depart != GroupState.Requested && !IsTurnbackTrack(run, prev)) return $"{prev.St.Name}の出発待ち";
        if (prev.Track == null) return prev.Arrive == GroupState.Requested ? null : $"{prev.St.Name}の通過待ち";
        if (prev.DepartPath.Count == 0) return null;
        return prev.Depart == GroupState.Requested ? null : $"{prev.St.Name}の出発待ち";
    }

    /// <summary>先に通し終わった対向列車の数（OpposingWaits から外したもの）。</summary>
    private static int WaitedDone(StationRun st) => st.WaitedTotal - st.OpposingWaits.Count;

    /// <summary>その列車が、駅 from から駅 to へ（区間を通って）これから走るか（from で打ち切り・終着しない）。</summary>
    private static bool ComesThrough(TrainRun o, string from, string to)
    {
        var f = o.Stations.FirstOrDefault(s => s.St.Id == from);
        var t = o.Stations.FirstOrDefault(s => s.St.Id == to);
        if (f == null || t == null || t.Done || IsCutOff(o, t)) return false;
        if (f.Terminal || IsTurnbackTrack(o, f)) return false;
        return !f.Done || (o.Front - f.Zone) * o.Dir.Step() >= 0; // from を出た後（区間内）も含む
    }

    /// <summary>その列車が駅に着く（出発できるようになる）までの見込み（秒）。駅にいれば 0。もう過ぎていれば null。</summary>
    private double? ReadyAt(TrainRun o, string station)
    {
        var z = _data.Line.ZoneOfStation(station);
        var d = (o.Front - z) * o.Dir.Step();
        if (d > 0) return null;
        if (d == 0 || o.Player.Zones.Contains(z)) return 0;
        return _model.SecondsTo(RunType(o), o.Dir, o.Front, o.Fraction, station);
    }

    /// <summary>
    /// p が run と同じ向きで後ろを走っていて、駅 st で run と同じ番線に入るか。同じ番線では追い越せないので、
    /// 出発順序・走行順で p が先になっていても、run が先に出る（p を待つと、p は番線が空かず、お互いに動けなくなる）。
    /// </summary>
    private static bool SameTrackBehind(TrainRun run, StationRun st, TrainRun p) =>
        p.Dir == run.Dir && IsAheadOf(run, p)
        && st.Track is { } t && p.Stations.FirstOrDefault(s => s.St.Id == st.St.Id && !s.Done) is { } ps && ps.Track == t;

    /// <summary>
    /// 同じ向きの列車 o が、run より前を走っているか。先頭のゾーンで比べ、同じ駅の構内（同じゾーン）にいれば、先にその駅に着いた方が前
    /// （駒野の上りで、1番線に止まっている先行列車と、2番線に入ってきた後続列車は、どちらも駒野のゾーンにいる。
    /// ゾーンだけで比べると前後が分からず、後続列車が先行列車より先に出発進路を取っていた）。同じ駅間にいれば、駅間のどこにいるかで比べる。
    /// </summary>
    private static bool IsAheadOf(TrainRun o, TrainRun run)
    {
        if (o == run || o.Dir != run.Dir || !o.HasPosition || !run.HasPosition) return false;
        var d = (o.Front - run.Front) * run.Dir.Step();
        if (d != 0) return d > 0;
        // 同じ駅間: 駅間のどこにいるかが両方分かれば、それで比べる
        if (o.Front % 2 == 1 && o.FractionKnown && run.FractionKnown && Math.Abs(o.Fraction - run.Fraction) > 0.02) return o.Fraction > run.Fraction;
        // 同じゾーン: 先にそのゾーンに来た方が前（複線・駅の中では、同じ向きの列車は後ろから来る）
        if (o.ZoneSinceUtc is not { } os || run.ZoneSinceUtc is not { } rs) return false;
        if (run.ZoneEntered) return os < rs;  // 自分が後ろから入ってきたとき、もういた列車は前
        return false;                          // 自分が先にいた（または、どちらも初めからいて前後が分からない）
    }

    /// <summary>
    /// 後ろを走る run が、前を走る other を駅 st で追い越してよいか。追い越してよいのは、次のときだけ:
    /// ・ダイヤで、その駅で追い越すことになっている（other がその駅に着いてから、run がその駅を出る・通る。other はそのあとに出る）
    /// ・司令員が手で決めた出発順序・単線の走行順で run が先、または other の発車（◆）をずらして run が先になった
    /// （開通指示は前もって出せるので、追い越しの許しにはしない。先に出したければ、出発順序を決めるか先行列車を出発抑止にする）
    /// それ以外は、遅れで順番が入れ替わっていても、前を走る列車が先（後ろの列車に先の進路を渡さない）。
    /// </summary>
    private bool MayOvertake(TrainRun run, TrainRun other, StationRun st)
    {
        var id = st.St.Id;
        if (_central.Order(id, run.Dir) is { } manual)
        {
            var list = manual.ToList();
            var (ri, oi) = (list.IndexOf(run.Train), list.IndexOf(other.Train));
            if (ri >= 0 && oi >= 0 && ri < oi) return true;
        }
        // 司令員が手で決めた単線区間の走行順（この駅から入る区間）で、run が先
        if (NextControlStation(run, st) is { } next && _data.IsSingleTrack(id, next) && _central.SectionOrder(id, next) is { } sec)
        {
            var (ri, oi) = (sec.ToList().IndexOf(run.Train), sec.ToList().IndexOf(other.Train));
            if (ri >= 0 && oi >= 0 && ri < oi) return true;
        }
        // 司令員がダイヤグラムで other の発車（◆）をずらし、運転順序で run が先になった
        if (_central.OpenAt(other.Train, id) != null && EffectiveOrder(id, run.Dir) is { } plan)
        {
            var list = plan.ToList();
            var (ri, oi) = (list.IndexOf(run.Train), list.IndexOf(other.Train));
            if (ri >= 0 && oi >= 0 && ri < oi) return true;
        }
        return DiaOvertakes(run, other, id);
    }

    /// <summary>ダイヤで、run が駅 stationId で other を追い越すことになっているか（other が着いたあとに run が出て、other はそのあとに出る）。</summary>
    private bool DiaOvertakes(TrainRun run, TrainRun other, string stationId)
    {
        if (!DiaMode || run.Dia == null || other.Dia == null) return false;
        if (DiaDaySec(other, stationId, depart: false) is not { } oa || DiaDaySec(other, stationId, depart: true) is not { } od
            || DiaDaySec(run, stationId, depart: true) is not { } rd) return false;
        return oa <= rd && rd < od;
    }

    /// <summary>同じ向きの列車が、駅 st を出て、その先の駅間に入ったか（先頭が駅より先にある）。</summary>
    private static bool EnteredSection(TrainRun p, StationRun st) => p.HasPosition && (p.Front - st.Zone) * p.Dir.Step() > 0;

    /// <summary>その列車が、駅 a〜b の単線区間を通り終えたか（もう通らない列車も含む）。</summary>
    private bool SectionCleared(TrainRun p, string a, string b)
    {
        var za = _data.Line.ZoneOfStation(a);
        var zb = _data.Line.ZoneOfStation(b);
        var (lo, hi) = (Math.Min(za, zb), Math.Max(za, zb));
        if (p.Player.Zones.Any(z => z > lo && z < hi)) return false; // 区間内
        var exit = p.Dir == Dir.Down ? hi : lo;
        // 区間の出口の駅まで来ていれば通り終えた。区間をこれから通る（入口の駅より手前）なら、まだ
        if ((p.Front - exit) * p.Dir.Step() >= 0) return true;
        var entry = p.Dir == Dir.Down ? lo : hi;
        var entryId = _data.Line.StationAtZone(entry);
        var exitId = _data.Line.StationAtZone(exit);
        return entryId == null || exitId == null || !ComesThrough(p, entryId, exitId);
    }

    /// <summary>
    /// 同じ方向で前を走る列車が、まだこの駅を通っていなければ待つ（追い越しは待避駅で、別の番線に入るときだけ）。
    /// 江ノ原信号場のように1線しかない所で、後ろの列車に先に進路を渡さないため。出発要求前の始発列車は待たない。
    /// 場内は、前の列車と別の番線に入るならよい（単線区間の駅は2線ある）。
    /// </summary>
    private string? AheadHold(TrainRun run, StationRun st, bool departure)
    {
        // 前を走る列車は IsAheadOf で決める（同じ駅の構内に2本いても、先に着いた方が前。ゾーンだけで比べると、構内に入った後続列車が
        // 先行列車を見なくなり、先に進路を取っていた。11/27 駒野）
        foreach (var other in _runs.Values.Where(o => IsAheadOf(o, run)))
        {
            if (other.Stations.FirstOrDefault(s => s.St.Id == st.St.Id) is not { Done: false } os || IsCutOff(other, os)) continue;
            // この駅で終わる列車は抜けないので、番線に入りきったあとは待たない（番線のふさがりは在線・進路の重なりで判断）。
            // それまでは、別の番線に入るのでも前の列車が先。後ろの列車が先に取ると、前の列車は場内に入れず、後ろの列車も前の列車がいて進めない
            // （駒野から出た回送が、前を走る列車より先に館浜の場内を取る。江ノ原信号場で、検車区へ入る列車より先に後ろの通過列車が本線を取る）
            if (os.Terminal || IsTurnbackTrack(other, os))
            {
                if (os.IsStart || os.InTrackUtc != null) continue;
                return $"先行列車（{other.Train}）が先";
            }
            if (WaitingForRequest(other, os)) continue; // 出発要求をしないまま止まっている列車（始発・待避線）は追い越してよい
            // ダイヤ運転中: ダイヤでこちらが先に出る（通る）始発列車は待たない（江ノ原検車区から出る列車より先に、本線の列車を通す）
            if (os.IsStart && DiaMode && DiaSec(other, st.St.Id, depart: true) is { } od && DiaSec(run, st.St.Id, depart: true) is { } rd && rd < od) continue;
            var otherTrack = os.Track != null && st.Track != null && os.Track != st.Track;
            // 追い越してよいのは出発（通過）だけ。場内は、追い越すときでも先行列車が先に取る（下の「別の番線に入るだけ」の決まり）。
            // 追い越しを理由に場内まで許すと、駅の手前を走っている先行列車より先に、共通の第1場内（駒野の上り 1RE）を取ってしまう
            // （11/26 19:30 駒野の上り: 館浜を先に出た 1980C より先に、後ろの 1908A が 5RB→1RE と 8R を取った）
            if (departure && otherTrack)
            {
                // 出発抑止中で、別の番線にいる先行列車は追い越してよい（駒野1番線で抑止中の列車を残して、2番線の列車を先に出す）
                if ((_central.Hold(other.Train, st.St.Id) & HoldKind.Depart) != 0) continue;
                // ダイヤで追い越すことになっている・司令員が順序を決めたなら、別の番線から先に出てよい
                if (MayOvertake(run, other, st)) continue;
                // 待避駅で別の番線（自動の待避。ダイヤ運転中は、ダイヤ・司令員が決めた追い越しだけ。遅れで順番を入れ替えない）
                if (!DiaMode && _data.PassingStations.ContainsKey(st.St.Id)) continue;
            }
            // 別の番線に入るだけならよい。ただし、前の列車がまだ場内を取っていなくて、場内の進路が重なるなら、前の列車が先
            // （続行する2本が同じ駅の別の番線に入るとき、後ろの列車が先に場内を取ると、前の列車の場内がふさがるため）
            if (!departure && os.Track != null && st.Track != null && os.Track != st.Track
                && (os.Arrive == GroupState.Requested || os.IsStart || !Circuits(PendingPaths(other, os)).Any(Circuits(st.ArrivePath).ToHashSet().Contains))) continue;
            return $"先行列車（{other.Train}）が先";
        }

        return null;
    }

    // =========================================================================================
    // 出力
    // =========================================================================================

    private void BuildOutputs(DateTime now)
    {
        _outputs.Clear();
        foreach (var run in _runs.Values)
        {
            var routes = run.Requests.Where(r => !r.Released && r.CancelledUtc == null).OrderBy(r => r.Id)
                .Select(r => new RouteCommand
                {
                    Id = r.Id, Interlocking = r.Def.Interlock, RouteName = r.Def.Name,
                    Indicator = r.Def.IsDepartureSignal ? run.Indicator : r.Def.Indicator, // 出発は列選（ダイヤの種別表記。なければクライアントがゲームの種別から）
                    UseTrainType = r.Def.IsDepartureSignal,
                    TrainNumber = run.GameTrain.Length > 0 ? run.GameTrain : run.Train, // ゲームには元の列番で送る
                    StopType = r.Station.DepartStop && r.Station.StopType == "通過" ? "停車" : r.Station.StopType // 出発停止の番線では止める
                }).ToList();

            // 駅を抜けたのに残っている進路と、番線の変更で取り消した進路は、ゲームから消す
            var deletes = run.Requests
                .Where(r => !r.Released && (r.CancelledUtc != null || r.Seen && r.Station.Done && now - r.Station.DoneUtc > LeftoverDelay))
                .Where(r => run.Player.GameRoutes.Contains((r.Def.Interlock, r.Def.Name)))
                .Select(r => new RouteKey { Interlocking = r.Def.Interlock, RouteName = r.Def.Name })
                .ToList();

            // 前の列車の進路を写したものが、この列車のゲームに残ったまま、同じ進路がこの列車に来たときは、写したものをいったん消す
            // （前の列車の進路が解除された周期に、そのまま後ろの列車に同じ進路が来る。残すと前の列車の列選・停車の扱いのまま開通する）。
            // 消えたら、この列車の列選で設定し直す（routes から外すのは、下の RemoveAll）
            foreach (var r in run.Requests.Where(r => !r.Released && r.CancelledUtc == null && !r.Seen && IsMirrorLeftover(run, r)))
            {
                deletes.Add(new RouteKey { Interlocking = r.Def.Interlock, RouteName = r.Def.Name });
                if (_mirrorLeftoverLogged.Add((run.ConnId, r.Id)))
                    RouteLogLine(now, $"写した進路を取り直し: {run.Train} {r.Def.Interlock} {r.Def.Name}（ほかの列車の進路として写したものが残っていたため、消してからこの列車の進路として設定する）");
            }

            // 要求した進路が5秒たっても開通しないとき、同じ連動装置に「サーバーが要求していない進路」がゲームに残っていれば消す
            // （列番の変更・つなぎ直し・サーバーの再起動の前に開通させた進路が残っていると、新しい進路が競合で開通しないため）。
            // 列車がもう入っている進路は消さない
            var wanted = run.Requests.Where(r => !r.Released).Select(r => (r.Def.Interlock, r.Def.Name)).ToHashSet();

            // 入換で動かしたあと、残した在線につなぎ直した列車: ゲームの中だけで走るのに使った進路（藤江13L・大道寺32LE）が残っていれば消す。
            // 列車が入っている進路なので、下の「残っていた進路」では消えない。残すと、そこから出る入換の進路が競合で開通しない
            if (run.Player.ShuntCleanupUntilUtc > now && PlayerRegistry.ShuntSpotOf(run.Player.ShuntSpot) is { } cleanup)
                foreach (var k in cleanup.Routes.Where(k => run.Player.GameRoutes.Contains(k) && !wanted.Contains(k)))
                    if (!deletes.Any(d => d.Interlocking == k.Interlock && d.RouteName == k.Route))
                        deletes.Add(new RouteKey { Interlocking = k.Interlock, RouteName = k.Route });
            var stuck = run.Requests.Where(r => !r.Released && r.CancelledUtc == null && !r.Seen && now - r.RequestedUtc > StuckDelay)
                .Select(r => r.Def.Interlock).ToHashSet();
            foreach (var g in run.Player.GameRoutes.Where(g => stuck.Contains(g.Interlock) && !wanted.Contains(g)))
            {
                if (_data.Routes.TryGetValue(g, out var def) && def.Circuits.Any(run.Player.TrackCircuits.Contains)) continue;
                if (_mirror.Any(m => m.Conn != run.ConnId && m.Route.Interlocking == g.Interlock && m.Route.RouteName == g.Route)) continue; // ほかの列車の進路（クライアントが写したもの）は消さない
                if (_central.PanelRoutes().Any(p => p.Interlocking == g.Interlock && p.RouteName == g.Route)) continue; // 信号制御盤の進路も消さない
                deletes.Add(new RouteKey { Interlocking = g.Interlock, RouteName = g.Route });
                if (_orphanLogged.Add((run.ConnId, g.Interlock, g.Route)))
                    _log.Note($"残っていた進路を取消: {run.Train} {g.Interlock} {g.Route}（要求していない進路が、新しい進路の開通をふさいでいたため）");
            }

            routes.RemoveAll(r => deletes.Any(d => d.Interlocking == r.Interlocking && d.RouteName == r.RouteName));

            var plan = run.Stations.Where(s => !s.Done).Select(s => new StationPlanView
            {
                StationId = s.St.Id,
                Name = s.St.Name,
                StopType = s.StopType,
                Track = s.Track,
                TrackOptions = s.Options,
                EndOptions = EndOptions(run, s),
                TurnbackTracks = s.BaseTerminal ? new List<int>() : s.Options.Where(t => s.St.Routes[run.Dir] is var r && r.Turnback.Contains(t) && !r.Depart.ContainsKey(t)).ToList(),
                EndHere = s.DriverEnd,
                CanPass = s.St.Routes[run.Dir].Through.Count > 0 && !s.Terminal && !s.IsStart,
                TrackSource = s.TrackSource,
                Locked = s.Locked,
                Status = ClientStatus(run, s) // 運転士には3段階だけ（詳しい状態は配信にのせない）
            }).ToList();
            // 詳しい状態（進路待ちの理由など）は、自動運転の列車だけが「止まっている理由」に使う。この先の最初の駅の分だけ作る
            var planDetail = run.ConnId.StartsWith(PlayerRegistry.AiPrefix) && run.Stations.FirstOrDefault(s => !s.Done) is { } ds
                ? $"{ds.St.Name}: {Status(run, ds)}" : null;

            var start = run.Stations.FirstOrDefault(s => s.IsStart && !s.Done);
            var shunting = start?.ShuntFrom != null;
            var canDepart = start != null && !start.Terminal && (shunting ? !start.ShuntRequested : !start.DepartureRequested) && !(DiaMode && run.Dia != null);
            var label = shunting ? "入換要求" : "出発要求";
            // 終着の列車: 番線に入りきっていて、引上げの入換があれば「入換要求」
            if (ShuntOutStation(run) != null && !(DiaMode && run.Dia != null)) (canDepart, label) = (true, "入換要求");
            // フリー運転で待避線に止まっている: 「出発要求」
            else if (run.Stations.Any(s => LoopRequestPending(run, s))) (canDepart, label) = (true, "出発要求");
            var message = !run.HasPosition ? "位置が分かりません（運転画面で列車を動かすと分かります）"
                : run.GameTrain.Length > 0 && run.Train != run.GameTrain ? $"同じ列番の列車が走っているため、サーバーでは {run.Train} として扱います"
                : StabledMessage(run);
            // 車掌へ送る取り消し: 番線の変更などで取り消した進路だけ。
            // 「駅を抜けたのに残っている進路」などは運転士の位置で決めたものなので送らない（2台の位置はずれるので、
            // 後ろを走る車掌がまだ使う進路を消して、運転士が抜けたあとの赤い信号に車掌を引っかけてしまうため）
            var conductorDeletes = run.Requests.Where(r => !r.Released && r.CancelledUtc != null)
                .Select(r => new RouteKey { Interlocking = r.Def.Interlock, RouteName = r.Def.Name }).ToList();
            _outputs[run.ConnId] = new Output(routes, deletes, plan, canDepart, label, message, conductorDeletes, planDetail);
        }

        foreach (var (conn, a) in _appear)
            _outputs[conn] = new Output(new(), new(), new(), false, "出発要求", $"出現待ち: {a.Reason}");

        // 入換で動かしている列車: この人のゲームにだけ、走るのに要る進路を送る（ほかの人のゲームには写さない。ほかの列車の在線は見ない）
        foreach (var key in _shuntIds.Keys.Where(k => !_shuntMoving.ContainsKey(k.Conn)).ToList()) _shuntIds.Remove(key);
        foreach (var (conn, m) in _shuntMoving)
        {
            var routes = new List<RouteCommand>();
            foreach (var k in m.Spot.Routes)
            {
                if (!_data.Routes.TryGetValue(k, out var def)) continue;
                if (!_shuntIds.TryGetValue((conn, k.Interlock, k.Route), out var id)) _shuntIds[(conn, k.Interlock, k.Route)] = id = ++_seq;
                routes.Add(new RouteCommand
                {
                    Id = id, Interlocking = def.Interlock, RouteName = def.Name,
                    Indicator = def.IsDepartureSignal ? "" : def.Indicator, UseTrainType = def.IsDepartureSignal, StopType = "停車"
                });
            }

            _outputs[conn] = new Output(routes, new(), new(), false, "出発要求", m.Message);
        }
        foreach (var (conn, parent) in _couplingDone)
            _outputs[conn] = new Output(new(), new(), new(), false, "出発要求", $"{parent} に連結しました（在線は {parent} として出します）");

        // ほかの参加者のゲームにも設定する進路: その列車のゲームで開通を確かめ、列車がまだ入っていないもの
        // （入ったら外す。本物の信号も、列車が過ぎると停止に戻る。その先は在線による停止の上限で守る）
        _mirror.Clear();
        foreach (var run in _runs.Values)
        foreach (var r in run.Requests.Where(r => r.Seen && !r.Entered && !r.Released && r.CancelledUtc == null))
            _mirror.Add((run.ConnId, r.Station.Zone, run.Dir, r.Def, new RouteCommand
            {
                Id = r.Id, Interlocking = r.Def.Interlock, RouteName = r.Def.Name,
                Indicator = r.Def.IsDepartureSignal ? "" : r.Def.Indicator, // 出発の列選は、受け取る列車の種別にする（OtherRoutes）
                UseTrainType = r.Def.IsDepartureSignal,
                TrainNumber = run.GameTrain.Length > 0 ? run.GameTrain : run.Train,
                StopType = r.Station.StopType
            }));
    }

    private readonly List<(string Conn, int Zone, Dir Dir, RouteDef Def, RouteCommand Route)> _mirror = new();

    /// <summary>ほかの列車の進路を送る範囲（区間の番号の差。駅は2つおきなので、4 で前後2駅）。</summary>
    private const int MirrorZones = 4;

    /// <summary>その参加者のゲームにも設定する、ほかの列車の進路（自分の列車の前後2駅の分。自分の列車の進路と同じものは除く）。</summary>
    public List<RouteCommand> OtherRoutes(string connId)
    {
        lock (_lock)
        {
            // 自分の列車の位置が分からなければ送らない。見えるのは近くの信号機だけなので、前後2駅の進路だけ送る（回線を軽くするため）
            if (!_runs.TryGetValue(connId, out var run) || !run.HasPosition) return new List<RouteCommand>();
            var mine = run.Requests.Where(r => !r.Released).Select(r => (r.Def.Interlock, r.Def.Name)).ToHashSet();
            var list = _mirror.Where(m => m.Conn != connId && Math.Abs(m.Zone - run.Front) <= MirrorZones
                                      && !mine.Contains((m.Route.Interlocking, m.Route.RouteName))
                                      && !WillApproach(run, m.Zone, m.Dir, m.Def))
                // 出発信号機の列選は、受け取る列車の種別の文字にする。写した進路はゲームには受け取る人の列車名で設定するので、
                // ほかの列車の種別（C特1 など）を入れると、運転台の列選の表示がそれを自分の列車のものとして出してしまうため
                // （他列車は非表示なので、ほかの列車の信号機の列選は見た目だけ。通過の進路は、ゲームが「通」と出す）
                .Select(m => m.Route.UseTrainType ? new RouteCommand
                {
                    Id = m.Route.Id, Interlocking = m.Route.Interlocking, RouteName = m.Route.RouteName, Indicator = run.Indicator,
                    UseTrainType = true, TrainNumber = m.Route.TrainNumber, StopType = m.Route.StopType
                } : m.Route).ToList();

            // 信号制御盤で開通させている進路も、全員のゲームに写す（盤の信号が進行の間だけ。列車が進路に入ったら一覧から外れ、クライアントが取り消す）
            foreach (var p in _central.PanelRoutes())
            {
                if (mine.Contains((p.Interlocking, p.RouteName)) || list.Any(r => r.Interlocking == p.Interlocking && r.RouteName == p.RouteName)) continue;
                list.Add(p.UseTrainType ? new RouteCommand
                {
                    Id = p.Id, Interlocking = p.Interlocking, RouteName = p.RouteName, Indicator = run.Indicator,
                    UseTrainType = true, TrainNumber = p.TrainNumber, StopType = p.StopType
                } : p);
            }

            // 写した進路の記録: 今回送ったもの＋前に送って、まだそのゲームに残っているもの
            var sent = _mirroredTo.TryGetValue(connId, out var old) ? old.Where(run.Player.GameRoutes.Contains).ToHashSet() : new();
            foreach (var r in list) sent.Add((r.Interlocking, r.RouteName));
            _mirroredTo[connId] = sent;
            return list;
        }
    }

    /// <summary>
    /// 受け取る列車が、その進路の接近鎖錠の区間にいる、またはこれから通るか（写さない進路）。
    /// 同じ向きで後ろを走る列車（後続）に、先行列車の場内・通過の進路を写すと、先行列車が進路に入って写すのをやめたとき、
    /// 後続のゲームでは後続が接近鎖錠の区間にいるので、DeleteRoute2 で取り消せずに残る。残った進路が後続自身の進路と競合して、
    /// サーバーが要求した進路がゲームで開通しない（江ノ原: 先行が 1RB で12番線に入ったあと、後続の本線 1RC が取れなかった）。
    /// 出発の進路は接近鎖錠の区間が出る番線なので、別の番線に着く後続には写してよい（待避列車の出発が見える）。
    /// </summary>
    private static bool WillApproach(TrainRun run, int zone, Dir ownerDir, RouteDef def)
    {
        if (def.Approach.Any(run.Player.TrackCircuits.Contains)) return true;
        return ownerDir == run.Dir && !def.IsDepartureSignal && (zone - run.Front) * run.Dir.Step() > 0;
    }

    /// <summary>参加者ごとの、そのゲームに写させた（ほかの列車の）進路。ゲームから消えるまで覚えておく。</summary>
    private readonly Dictionary<string, HashSet<(string Interlock, string Route)>> _mirroredTo = new();
    private readonly HashSet<(string Conn, long Id)> _mirrorLeftoverLogged = new();

    /// <summary>
    /// この列車の進路が、この列車のゲームでは「ほかの列車の進路として写したもの」のまま残っているか
    /// （同じ進路名でゲームにあっても、列選・停車の扱いがほかの列車のもの）。列車がもう進路に入っていれば、消さないので false。
    /// </summary>
    private bool IsMirrorLeftover(TrainRun run, RouteReq req)
    {
        var key = (req.Def.Interlock, req.Def.Name);
        return !req.Entered && _mirroredTo.TryGetValue(run.ConnId, out var sent) && sent.Contains(key)
               && run.Player.GameRoutes.Contains(key) && !req.Def.Circuits.Any(run.Player.TrackCircuits.Contains);
    }

    /// <summary>
    /// クライアント（運転士）に出す状態。運転中は目の前の信号機しか見えないので、進路の中身（待ちの理由・抑止・順番など）は伏せ、
    /// 「出発要求待ち」（引上線からなら「入換要求待ち」）「進路確定」「進路未確定」の3段階だけにする。
    /// 進路確定: その駅でその列車に要る進路（場内、終着でなければ出発。通過なら両方）が全部開通した。管理画面には Status を出す
    /// </summary>
    private string ClientStatus(TrainRun run, StationRun s)
    {
        bool Opened(GroupState g, List<RouteDef> path)
        {
            if (path.Count == 0) return true;
            if (g != GroupState.Requested) return false;
            var reqs = run.Requests.Where(r => r.Station == s && path.Contains(r.Def) && !r.Released && r.CancelledUtc == null).ToList();
            return reqs.Count > 0 && reqs.All(r => r.Seen || Acknowledged(run, r));
        }

        if (IsCutOff(run, s)) return "進路未確定";
        if (s.IsStart && s.ShuntFrom != null)
            return !s.ShuntRequested ? "入換要求待ち"
                : s.Shunt == GroupState.Requested && run.Requests.Where(r => r.Station == s && !r.Departure && !r.Released).All(r => r.Seen || Acknowledged(run, r)) ? "進路確定" : "進路未確定";
        if (ShuntOutOptions(run, s).Count > 0 && FullyInTrack(run, s))
            return s.ShuntOutChoice == null ? "入換要求待ち" : s.ShuntOut == GroupState.Requested ? "進路確定" : "進路未確定";
        if (s.IsStart && !s.Locked) return s.Terminal ? "進路確定" : "出発要求待ち";
        if (!s.Locked) return "進路未確定";
        if (LoopRequestPending(run, s)) return "出発要求待ち";
        var needDepart = !s.Terminal && !IsTurnbackTrack(run, s);
        var arriveOk = s.IsStart || Opened(s.Arrive, s.ArrivePath);
        var departOk = !needDepart || Opened(s.Depart, s.DepartPath);
        return arriveOk && departOk ? "進路確定" : "進路未確定";
    }

    private string Status(TrainRun run, StationRun s)
    {
        string Group(GroupState g, string wait, List<RouteDef> path, string label)
        {
            if (path.Count == 0) return "";
            if (g == GroupState.Waiting) return $"{label}待ち: {wait}";
            if (g != GroupState.Requested) return "";
            var reqs = run.Requests.Where(r => r.Station == s && path.Contains(r.Def)).ToList();
            if (reqs.Count > 0 && reqs.All(r => r.Seen || Acknowledged(run, r))) return $"{label}開通";
            var failed = path.Select(p => run.Results.GetValueOrDefault($"{p.Interlock}/{p.Name}")).FirstOrDefault(x => x != null && x != "OK");
            return failed != null ? $"{label}: {failed}（再試行中）" : $"{label}要求中";
        }

        if (IsCutOff(run, s)) return "扱わない（手前の駅で打ち切り）";
        if (!s.Locked && !s.IsStart) return IsTurnbackTrack(run, s) ? "未確定（この駅で打ち切り）" : "未確定";
        if (s.IsStart && s.ShuntFrom != null)
            return !s.ShuntRequested ? $"入換要求待ち（{s.ShuntFrom}→{s.Track}番線）"
                : s.Shunt == GroupState.Waiting ? $"入換待ち: {s.ShuntWait}"
                : (run.Requests.Any(r => r.Station == s && !r.Departure && r.Seen) ? "入換開通" : "入換要求中")
                  + (s.ShuntVia != null ? $"（{s.ShuntVia}経由）" : "");
        if (s.IsStart && !s.Locked)
            return !s.Terminal ? "出発要求待ち"
                : s.ShuntOutChoice == null ? "終着"
                : "終着／" + (s.ShuntOut == GroupState.Waiting ? $"入換待ち: {s.ShuntOutWait}" : s.ShuntOut == GroupState.Requested ? "入換（引上げ）要求済み" : "入換要求");
        var parts = new[]
        {
            Group(s.Depart, s.DepartWait, s.DepartPath, "出発"),
            Group(s.Arrive, s.ArriveWait, s.ArrivePath, s.Track == null ? "通過" : "場内")
        }.Where(x => x.Length > 0).ToList();
        if (s.HoldKept.Length > 0) parts.Add($"{s.HoldKept}抑止は列車が接近中のため取り消さず");
        if (s.ShuntOutChoice != null)
            parts.Add(s.ShuntOut == GroupState.Waiting ? $"入換待ち: {s.ShuntOutWait}" : s.ShuntOut == GroupState.Requested ? "入換（引上げ）要求済み" : "入換要求");
        if (s.Terminal) parts.Add("終着");
        else if (IsTurnbackTrack(run, s)) parts.Add("打ち切り（折り返し）");
        return parts.Count > 0 ? string.Join("／", parts) : "待機";
    }

    // =========================================================================================
    // 管理画面
    // =========================================================================================

    /// <summary>
    /// ダイヤグラム用のデータ。列車ごとに、走った実績・この先の見込み（標準運転時分）・駅ごとの開通の目印を返す。
    /// 縦の位置はゾーン（駅＝偶数）、時刻は UTC。
    /// </summary>
    public object DiagramView()
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            // 在線を共有している列車だけ（特発のみ共有の列車は _runs に入らないが、念のためここでも絞る）
            var runs = _runs.Values.Where(r => r.HasPosition && r.Player.Mode == ShareMode.Shared).OrderBy(r => r.Train).ToList();

            // 走行順は、この画面を作る間は変わらないので、区間ごとに1回だけ出す
            var sectionOrders = new Dictionary<(string, string), IReadOnlyList<string>?>();
            // 1回目: スジずらし（発車時刻）だけを入れた見込み。2回目から、走行順・出発順序の先の列車を待つ時間と、
            // 先の列車に詰まる分（駅間で追いつかない・同じ番線は先の列車が出てから）を足す。先の列車が詰まれば後ろの列車も詰まるので、変わらなくなるまで（4回まで）
            var basePlans = runs.ToDictionary(r => r, r => Plan(r, now, z => SlideDepart(r, z)));
            var plans = basePlans;
            for (var pass = 0; pass < 4; pass++)
            {
                var prev = plans;
                basePlans = prev;
                plans = runs.ToDictionary(r => r, r => Plan(r, now, z => MinDepart(r, z), DiaMode ? z => MinArrive(prev, r, z) : null));
                if (runs.All(r => SameTimes(prev[r], plans[r]))) break;
            }
            IReadOnlyList<string>? SectionOrderOf(string a, string b) =>
                sectionOrders.TryGetValue((a, b), out var o) ? o : sectionOrders[(a, b)] = EffectiveSectionOrder(a, b);
            DateTime? MinDepart(TrainRun r, int z)
            {
                DateTime? m = SlideDepart(r, z);
                void Max(DateTime? t) { if (t != null && (m == null || t > m)) m = t; }
                if (_data.Line.StationAtZone(z) is not { } sid || r.Stations.FirstOrDefault(s => s.St.Id == sid && !s.Done) is not { } st) return m;

                // 単線区間の走行順: 先の列車が区間を通り終えるまで（同じ向きなら先の列車がこの駅を出たあと、逆向きなら先の列車がこの駅に着いたあと）。
                // 手で決めていなければ、運転と同じ自動の走行順（EffectiveSectionOrder）で。単線区間に同じ時刻に2本いる線にしない
                if (NextControlStation(r, st) is { } nx && _data.IsSingleTrack(sid, nx) && SectionOrderOf(sid, nx) is { } so && so.Contains(r.Train))
                    foreach (var name in so.TakeWhile(t => t != r.Train))
                        if (RunByTrain(name) is { } p && basePlans.TryGetValue(p, out var pp) && !SectionCleared(p, sid, nx) && !SameTrackBehind(r, st, p)
                            && !(p.Dir == r.Dir && IsAheadOf(r, p) && !MayOvertake(p, r, st)))
                            Max(p.Dir == r.Dir ? pp.DepAt.TryGetValue(z, out var d1) ? d1 : null : pp.ArrAt.TryGetValue(z, out var a1) ? a1 : null);

                // 駅の出発順序: 同じ向きの先の列車がこの駅を出たあと
                if (EffectiveOrder(sid, r.Dir) is { } order && order.Contains(r.Train))
                    foreach (var name in order.TakeWhile(t => t != r.Train))
                        if (RunByTrain(name) is { } p && p.Dir == r.Dir && (_central.Hold(name, sid) & HoldKind.Depart) == 0 && !SameTrackBehind(r, st, p)
                            && !(IsAheadOf(r, p) && !MayOvertake(p, r, st))
                            && basePlans.TryGetValue(p, out var pp) && pp.DepAt.TryGetValue(z, out var d2))
                            Max(d2);
                return m;
            }

            return runs.Select(r =>
            {
                var step = r.Dir.Step();
                var type = RunType(r);
                var plan = plans[r];
                var forecast = plan.Points;
                var arrAt = plan.ArrAt;

                // 駅の目印（◆）: 進路を扱う駅ごとに、その駅を通過する（停車駅は着く）予想時刻に置く（見込みの線の上）。
                // 開通の時刻（開通済みならその時刻、まだなら2つ手前の駅に着く見込み）は、説明に出す
                var markers = r.Stations.Where(s => !s.Done && !IsCutOff(r, s)).Select(s =>
                {
                    var openAt = UseOpenTime ? _central.OpenAt(r.Train, s.St.Id) : null;
                    var started = s.Arrive == GroupState.Requested || s.Depart == GroupState.Requested || s.Shunt == GroupState.Requested;

                    // 通過・到着の予想。今いる駅（始発・停車中）は今
                    var here = s.IsStart || r.Front == s.Zone;
                    DateTime? time = here ? now : arrAt.TryGetValue(s.Zone, out var at) ? at : null; // 待避線の延長を足した見込み
                    var arriveAt = time; // 着（通過）の見込み。停車駅は、着の◆と発の◆に分けて描く
                    DateTime? leaveAt = plan.DepAt.TryGetValue(s.Zone, out var lv) ? lv : null;
                    // スジずらしで発車時刻を決めた駅は、◆を発車時刻に置く（ずらした位置に残る）
                    var departAt = _central.OpenAt(r.Train, s.St.Id);
                    if (departAt != null && plan.DepAt.TryGetValue(s.Zone, out var dt)) time = dt;
                    var stops = s.StopType != "通過" || s.DepartStop || s.Passing || IsTurnbackTrack(r, s) || s.Terminal;
                    var source = here ? "在線" : stops ? "到着予想" : "通過予想";

                    // 開通の時刻
                    DateTime? open = openAt;
                    var openSource = openAt != null ? "指定" : "見込み";
                    if (started) (open, openSource) = (s.TriggeredUtc != default ? s.TriggeredUtc : open, "開通済み");
                    else if (openAt == null)
                    {
                        if (s.Triggered) (open, openSource) = (s.TriggeredUtc, "到着済み");
                        else if (_data.Line.StationAtZone(s.Zone - 2 * TriggerAhead(r, s) * step) is { } prev)
                            open = _model.SecondsTo(type, r.Dir, r.Front, r.Fraction, prev) is { } sec ? now.AddSeconds(sec) : null;
                    }

                    // 番線を変えられるか: 選べる番線が2つ以上あり、列車がまだその駅の進路に入っていない
                    // 入換を出したあとの始発駅は、番線を変えられない
                    var entered = r.Requests.Any(q => q.Station == s && !q.Released && q.CancelledUtc == null
                                                      && q.Def.Circuits.Any(r.Player.TrackCircuits.Contains))
                                  || (s.IsStart && s.ShuntFrom != null && s.Shunt == GroupState.Requested);
                    return new
                    {
                        stationId = s.St.Id,
                        name = s.St.Name,
                        passingStation = _data.PassingStations.ContainsKey(s.St.Id),
                        y = (double)s.Zone,
                        t = time,
                        arriveAt,
                        leaveAt,
                        stops,
                        source,
                        openAt = open,
                        openSource,
                        editable = !started,
                        // ◆を横にずらすと、その駅の発車時刻を決められる（出発の進路をまだ取っていない、その駅で終わらない列車）
                        draggable = !s.Terminal && !IsTurnbackTrack(r, s) && s.Depart != GroupState.Requested,
                        departAt,
                        held = _central.IsHeld(r.Train, s.St.Id),
                        holdArrive = (_central.Hold(r.Train, s.St.Id) & HoldKind.Arrive) != 0,
                        holdDepart = (_central.Hold(r.Train, s.St.Id) & HoldKind.Depart) != 0,
                        // 臨時停車・臨時通過・打ち切りの指定（◆の窓で打ち切りを選べる）
                        stopOverride = _central.StopOverride(r.Train, s.St.Id),
                        terminal = s.Terminal,
                        stopType = s.StopType, // 停車・通過（臨時停車・臨時通過の指定を入れたもの）
                        manual = ManualOptions(r, s).Select(o => new { action = o.Action, label = o.Label }).ToList(),
                        singleNext = NextControlStation(r, s) is { } nx && _data.IsSingleTrack(s.St.Id, nx) ? nx : null,
                        status = Status(r, s),
                        track = s.Track,
                        trackSource = s.TrackSource,
                        options = s.Options.ToList(), // 写す（JSON にするのはロックの外）
                        changeable = s.Options.Count > 1 && !entered
                    };
                }).Where(m => m.t != null).ToList();

                return new
                {
                    train = r.Train,
                    type = r.Type,
                    direction = r.Dir.Label(),
                    history = r.History.Select(h => new { t = h.Utc, y = h.Y, row = h.Row }).ToList(), // lock の中で写す（JSON にするのは lock の外。Tick が書き足すと「Collection was modified」になる）
                    forecast,
                    // 駅ごとの番線（通った駅も含む。ダイヤグラムの番線の段に線を引くため）
                    tracks = r.Stations.Where(s => s.Track != null).Select(s => new { y = s.Zone, track = s.Track }).ToList(),
                    markers,
                    // 後運用（ダイヤの列車だけ。◆の窓で差し替える）
                    unyo = r.Dia != null ? _data.Turns.UnyoOf(r.Dia) : null,
                    turn = DiaMode && r.Dia != null ? _data.Turns.View(r.Dia) : null
                };
            }).Cast<object>().Concat(PendingDiagram(now, runs, plans)).ToList();
        }
    }

    /// <summary>出現前の列車（自動運転の列車として、これから始発駅に出すもの）。AiTrains が作る。</summary>
    public Func<IReadOnlyList<AiTrains.PendingTrain>> PendingTrains { get; set; } = () => [];

    /// <summary>
    /// ダイヤグラムに出す、出現前の列車（ダイヤ運転中だけ）。線はダイヤの時刻（◆で決めた発車時刻・遅れの分だけずらす。単線区間では走っている対向列車を待つ）。
    /// ◆は、始発駅と、その先の進路を扱う駅。
    /// ◆の窓で、出る前から発車時刻・抑止・番線を入れておける（CentralControl は列番で覚えるので、出たあともそのまま効く）。
    /// _lock の中で呼ぶ（PendingTrains はロックを取らずに読める）
    /// </summary>
    private IEnumerable<object> PendingDiagram(DateTime now, List<TrainRun> runs, Dictionary<TrainRun, PlanResult> plans)
    {
        if (!DiaMode) yield break;
        var nowSec = NowSec();
        var speed = ClockSpeed;
        DateTime UtcOf(double daySec) => now.AddSeconds((daySec - nowSec) / speed);
        foreach (var p in PendingTrains())
        {
            if (runs.Any(r => r.Train == p.Number) || _data.DiaOf(p.Number) is not { } dia || TrainInfo(p.Number) is not { } info) continue;
            if (!_data.Stations.TryGetValue(p.Station, out var st)) continue;
            var from = dia.Stops.FindIndex(x => x.StationId == p.Station);
            if (from < 0 || DaySec(dia.Stops[from].Dep ?? dia.Stops[from].Arr) is not { } diaDep) continue;
            var due = p.Due.TotalSeconds < 4 * 3600 ? p.Due.TotalSeconds + 86400 : p.Due.TotalSeconds;
            var leave = Math.Max(due, nowSec); // 時刻を過ぎても出せていなければ、今から
            var shift = leave - diaDep;

            // 線: 始発駅の発車から、ダイヤの時刻をずらして。
            // 単線区間へ出る駅では、反対向きの走っている列車が区間にいる・この列車より先に区間に入る見込みなら、
            // その列車がこの駅に着くまで待つ（走っている列車の線と、単線区間の中で交差させない）
            var forecast = new List<object>();
            var tracks = new List<object>();
            var stationMarks = new List<object>(); // 始発駅の先の、進路を扱う駅の◆
            for (var i = from; i < dia.Stops.Count; i++)
            {
                var s = dia.Stops[i];
                if (s.StationId == null || _data.Line.ZoneOfStation(s.StationId) is var z && z < 0) continue;
                double? arrSec = i == from ? null : DaySec(s.Arr) + shift;
                var depBase = i == from ? leave : DaySec(s.Dep) + shift;
                // 遅れていれば、停車時間を最小（標準の停車時分）まで縮めて取り戻す（走っている列車の見込みと同じ。ダイヤより早くは出ない）
                if (i != from && DaySec(s.Dep) is { } diaD && arrSec is { } ar && DaySec(s.Arr) is { } diaA)
                {
                    depBase = Math.Max(diaD, ar + Math.Min(Math.Max(0, diaD - diaA), _data.DefaultDwellSeconds));
                    shift = depBase.Value - diaD;
                }
                var depSec = depBase;
                if (depSec != null && SingleTrackWait(dia, i, z, info.Dir, depSec.Value, shift, plans, runs, UtcOf, speed) is { } wait && wait > 0)
                {
                    shift += wait;
                    depSec += wait;
                }
                if (i == from) forecast.Add(new { t = UtcOf(leave), y = (double)z });
                else if (arrSec is { } arr) forecast.Add(new { t = UtcOf(arr), y = (double)z });
                if (depSec is { } dep && dep != (i == from ? leave : arrSec)) forecast.Add(new { t = UtcOf(dep), y = (double)z });
                var track = i == from ? p.Track : _central.TrackOverride(p.Number, s.StationId) ?? s.Track;
                if (track != null) tracks.Add(new { y = z, track });

                // ◆: 始発駅の先の、進路を扱う駅にも置く（出る前から番線・発車時刻・抑止を入れておける。列番と駅で覚えるので、出たあとも効く）
                if (i == from || !_data.Stations.TryGetValue(s.StationId, out var ms) || !ms.RouteControl || s.Stop == "運停") continue;
                var mTime = arrSec ?? depBase;
                if (mTime == null) continue;
                var terminal = s.StationId == dia.TerminalId;
                var mNext = dia.Stops.Skip(i + 1).Select(x => x.StationId)
                    .FirstOrDefault(id => id != null && _data.Stations.TryGetValue(id, out var nd) && nd.RouteControl);
                var mOptions = ms.Routes[info.Dir].Arrive.Keys.Concat(ms.Routes[info.Dir].Depart.Keys).Distinct().Order().ToList();
                var mHold = _central.Hold(p.Number, s.StationId);
                var mOverride = _central.TrackOverride(p.Number, s.StationId);
                stationMarks.Add(new
                {
                    stationId = s.StationId,
                    name = ms.Name,
                    passingStation = _data.PassingStations.ContainsKey(s.StationId),
                    y = (double)z,
                    t = (DateTime?)UtcOf(mTime.Value),
                    arriveAt = (DateTime?)UtcOf(mTime.Value),
                    leaveAt = depSec is { } ld ? (DateTime?)UtcOf(ld) : null,
                    stops = s.Stop == "停" || terminal,
                    source = s.Stop == "停" || terminal ? "出現前・到着予想" : "出現前・通過予想",
                    openAt = (DateTime?)null,
                    openSource = "見込み",
                    editable = true,
                    draggable = !terminal,
                    departAt = _central.OpenAt(p.Number, s.StationId),
                    held = mHold != HoldKind.None,
                    holdArrive = (mHold & HoldKind.Arrive) != 0,
                    holdDepart = (mHold & HoldKind.Depart) != 0,
                    stopOverride = _central.StopOverride(p.Number, s.StationId),
                    terminal,
                    stopType = s.Stop == "停" ? "停車" : "通過",
                    manual = new List<object>(),
                    singleNext = mNext != null && _data.IsSingleTrack(s.StationId, mNext) ? mNext : null,
                    status = $"出現前（{st.Name}に出す前の列車）",
                    track = mOverride ?? s.Track,
                    trackSource = mOverride != null ? "集中" : "ダイヤ",
                    options = mOptions,
                    changeable = mOptions.Count > 1
                });
            }
            if (forecast.Count < 2) continue;

            string? next = dia.Stops.Skip(from + 1).Select(x => x.StationId)
                .FirstOrDefault(id => id != null && _data.Stations.TryGetValue(id, out var nd) && nd.RouteControl);
            var options = st.Routes[info.Dir].Depart.Keys.Order().ToList();
            var status = p.Held ? "出現前（抑止中なので出しません。外すと出します）"
                : p.Late ? "出現前（時刻を過ぎましたが、番線があかないので出せていません）"
                : $"出現前（{_clock.TextAt(UtcOf(leave).AddSeconds(-120 / speed))}ごろ出します）";
            var hold = _central.Hold(p.Number, p.Station);
            var marker = new
            {
                stationId = p.Station,
                name = st.Name,
                passingStation = _data.PassingStations.ContainsKey(p.Station),
                y = (double)_data.Line.ZoneOfStation(p.Station),
                t = (DateTime?)UtcOf(leave),
                arriveAt = (DateTime?)UtcOf(leave),
                leaveAt = (DateTime?)UtcOf(leave),
                stops = false, // 始発駅は発の◆だけ
                source = "出現前・発車",
                openAt = (DateTime?)null,
                openSource = "見込み",
                editable = true,
                draggable = true,
                departAt = _central.OpenAt(p.Number, p.Station),
                held = hold != HoldKind.None,
                holdArrive = (hold & HoldKind.Arrive) != 0,
                holdDepart = (hold & HoldKind.Depart) != 0,
                stopOverride = (string?)null,
                terminal = false,
                stopType = dia.Stops[from].Stop == "停" ? "停車" : "通過",
                manual = new List<object>(),
                singleNext = next != null && _data.IsSingleTrack(p.Station, next) ? next : null,
                status,
                track = p.Track,
                trackSource = p.TrackFixed ? "集中" : "ダイヤ",
                options,
                changeable = options.Count > 1
            };
            yield return new
            {
                train = p.Number,
                type = info.Type,
                direction = info.Dir.Label(),
                pending = true,
                history = new List<object>(),
                forecast,
                tracks,
                markers = new List<object> { marker }.Concat(stationMarks).ToList(),
                unyo = _data.Turns.UnyoOf(dia),
                turn = _data.Turns.View(dia)
            };
        }
    }

    /// <summary>
    /// 出現前の列車が、ダイヤの i 番目の駅（ゾーン z）から単線区間へ出るとき、反対向きの走っている列車を待つ秒（ロープレ時刻）。待たなくてよければ null。
    /// 反対向きの列車が区間にいる・この列車がその先の駅に着くより前に区間へ入る見込みで、この駅に着くのがこの列車の発車より後なら、着くまで待つ。
    /// depSec はこの駅の発車（ロープレ時刻の秒）、shift はダイヤからのずれ（その先の駅の着に足す）
    /// </summary>
    private double? SingleTrackWait(DiaTrain dia, int i, int z, Dir dir, double depSec, double shift, Dictionary<TrainRun, PlanResult> plans,
        List<TrainRun> runs, Func<double, DateTime> utcOf, double speed)
    {
        var sid = dia.Stops[i].StationId!;
        if (!_data.Stations.TryGetValue(sid, out var a) || !a.RouteControl) return null;
        var j = dia.Stops.FindIndex(i + 1, x => x.StationId != null && _data.Stations.TryGetValue(x.StationId, out var b) && b.RouteControl);
        if (j < 0 || !_data.IsSingleTrack(sid, dia.Stops[j].StationId!)) return null;
        var zb = _data.Line.ZoneOfStation(dia.Stops[j].StationId!);
        if (DaySec(dia.Stops[j].Arr ?? dia.Stops[j].Dep) is not { } arrB) return null;
        var (lo, hi) = (Math.Min(z, zb), Math.Max(z, zb));

        double wait = 0;
        for (var guard = 0; guard < 10; guard++)
        {
            var dep = utcOf(depSec + wait);
            var arrive = utcOf(arrB + shift + wait);
            DateTime? until = null;
            foreach (var r in runs.Where(r => r.Dir != dir && r.HasPosition))
            {
                if (!plans.TryGetValue(r, out var pl) || !pl.ArrAt.TryGetValue(z, out var rArr) || rArr <= dep) continue;
                // 区間に入るのが、この列車が先の駅に着くより前か（もう区間にいる・先の駅を出る見込みが早い）
                var inSection = r.Player.Zones.Any(x => x > lo && x < hi);
                var enters = inSection || (pl.DepAt.TryGetValue(zb, out var rDep) && rDep < arrive);
                if (enters && (until == null || rArr > until)) until = rArr;
            }
            if (until == null) break;
            wait += (until.Value - dep).TotalSeconds * speed;
        }
        return wait > 0 ? wait : null;
    }

    private sealed record PlanResult(List<object> Points, Dictionary<int, DateTime> ArrAt, Dictionary<int, DateTime> DepAt);

    private static bool SameTimes(PlanResult a, PlanResult b) =>
        a.ArrAt.Count == b.ArrAt.Count && a.DepAt.Count == b.DepAt.Count
        && a.ArrAt.All(kv => b.ArrAt.TryGetValue(kv.Key, out var v) && v == kv.Value)
        && a.DepAt.All(kv => b.DepAt.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>
    /// 見込みの線の終わり: 行き止まりの番線に入る駅・終着（列車別設定の打ち切りを含む）のゾーン。もう抜けた扱い（Done）の駅も含める。
    /// 大道寺の Y 線・新野崎の5番線などへの入換では、分岐の回路が駅間のゾーンにあるので、最後尾がそこに入ると駅を抜けた扱いになる。
    /// そのときも、終着より先へは線を延ばさない
    /// </summary>
    private static int? PlanEnd(TrainRun r) => r.Stations.FirstOrDefault(s => IsTurnbackTrack(r, s) || s.Terminal)?.Zone;

    /// <summary>スジずらしで決めた発車時刻（そのゾーンが進路を扱う駅なら）。</summary>
    private DateTime? SlideDepart(TrainRun r, int zone) =>
        _data.Line.StationAtZone(zone) is { } sid ? _central.OpenAt(r.Train, sid) : null;

    /// <summary>
    /// 列車の見込み（ダイヤグラムの点線）。今の位置から終着駅（なければ路線の端）まで、標準運転時分で各駅に着く・出る時刻を出す。
    /// 待避線では最低の停車時分まで止め、minDepart（スジずらし・走行順・出発順序で決まる、その駅を出られる最も早い時刻）より前には出ない。
    /// 延びた分は、その先の駅にも足す。停車は水平の線で描く。
    /// </summary>
    private PlanResult Plan(TrainRun r, DateTime now, Func<int, DateTime?> minDepart, Func<int, DateTime?>? minArrive = null)
    {
        // ダイヤ運転中のダイヤの列車: この先はダイヤどおりの線（遅れていればその分ずらす）。過去は実績（history）のまま
        if (DiaMode && r.Dia != null && DiaPlan(r, now, minDepart, minArrive) is { } dp) return dp;

        var step = r.Dir.Step();
        var type = RunType(r);
        var terminal = r.Player.Stops.Count > 0 ? _data.Line.StationIdByName(r.Player.Stops[^1].Name) : null;
        var terminalZone = terminal != null ? _data.Line.ZoneOfStation(terminal) : -1;
        // 行き止まりの番線に入る駅・終着（列車別設定の打ち切りを含む）があれば、線はその駅で切る
        if (PlanEnd(r) is { } cut) terminalZone = cut;

        var points = new List<object> { new { t = now, y = DiagramY(r), row = SidingRow(r) } };
        var arrAt = new Dictionary<int, DateTime>();
        var depAt = new Dictionary<int, DateTime>();
        double extra = 0;
        if (r.Front % 2 == 0 && r.Stations.FirstOrDefault(s => s.Zone == r.Front && !s.Done) is { } curSt)
        {
            // 今いる駅: 待避線の最低の停車時分の残り（入りきる前なら全部）と、決められた発車時刻
            var leave = now;
            if (IsLoopTrack(r, curSt))
            {
                var left = curSt.LoopArrivedUtc is { } la ? _data.MinLoopDwellSeconds - (now - la).TotalSeconds : _data.MinLoopDwellSeconds;
                if (left > 0) leave = now.AddSeconds(left);
            }

            if (minDepart(r.Front) is { } md && md > leave) leave = md;
            arrAt[r.Front] = now;
            depAt[r.Front] = leave;
            if (leave > now)
            {
                extra += (leave - now).TotalSeconds;
                points.Add(new { t = leave, y = (double)r.Front });
            }
        }

        for (var z = (r.Front % 2 == 0 ? r.Front : r.Front + step) + (r.Front % 2 == 0 ? 2 * step : 0);
             _data.Line.StationAtZone(z) is { } sid; z += 2 * step)
        {
            if (terminalZone >= 0 && (z - terminalZone) * step > 0) break; // 終着より先（入換で終着の先の分岐まで出ていても、線は延ばさない）
            if (_model.SecondsTo(type, r.Dir, r.Front, r.Fraction, sid) is not { } sec) { if (z == terminalZone) break; continue; }
            var arr = now.AddSeconds(sec + extra);
            arrAt[z] = arr;
            points.Add(new { t = arr, y = (double)z });
            if (z == terminalZone) break;

            // この駅の停車時分: 標準運転時分の値。待避線なら最低の停車時分まで延ばし、決められた発車時刻より前には出ない
            var modelDwell = _model.DwellAt(type, r.Dir, sid);
            var dwell = modelDwell;
            if (r.Stations.FirstOrDefault(s => s.Zone == z && !s.Done) is { } zs && IsLoopTrack(r, zs))
                dwell = Math.Max(modelDwell, Math.Max(_data.DefaultDwellSeconds, _data.MinLoopDwellSeconds));
            var dep = arr.AddSeconds(dwell);
            if (minDepart(z) is { } md2 && md2 > dep) dep = md2;
            depAt[z] = dep;
            extra += (dep - arr).TotalSeconds - modelDwell;
            if (dep > arr) points.Add(new { t = dep, y = (double)z });
        }

        AddSidingWait(r, points, now, depAt.TryGetValue(r.Front, out var sidingLeave) ? sidingLeave : null);
        return new PlanResult(points, arrAt, depAt);
    }

    /// <summary>
    /// ダイヤ運転中の見込み: この先の駅を、ダイヤの着・発の時刻に、いまの遅れの分だけ遅らせて引く。
    /// 遅れ = 今いる駅ならダイヤの発車時刻からの遅れ（まだ発車時刻前なら0）、駅間なら次の駅に着く見込み（標準運転時分）とダイヤの着の差（早着は0）。
    /// 時刻はロープレ時刻なので、ダイヤグラムの横軸（実時間）に直す（時計を早回ししていれば、その速さで割る）。
    /// minDepart（スジずらし・走行順・出発順序）でダイヤより遅く出る駅は、その時刻に出し、延びた分をこの先の駅にも足す
    /// </summary>
    private PlanResult? DiaPlan(TrainRun r, DateTime now, Func<int, DateTime?> minDepart, Func<int, DateTime?>? minArrive)
    {
        var step = r.Dir.Step();
        var nowSec = NowSec();
        var speed = _clock.Now != null && _clock.Speed > 0 ? _clock.Speed : 1;
        DateTime Utc(double sec) => now.AddSeconds((sec - nowSec) / speed);

        // 線の終わり: ダイヤで線区の最後の駅（打ち切り・行き止まりの番線があればそこ）
        var lastDia = r.Dia!.Stops.LastOrDefault(s => s.StationId != null && _data.Line.ZoneOfStation(s.StationId) >= 0);
        if (lastDia == null) return null;
        var terminalZone = _data.Line.ZoneOfStation(lastDia.StationId!);
        if (PlanEnd(r) is { } cut) terminalZone = cut;

        double? Arr(string sid) => DiaSec(r, sid, depart: false);
        double? Dep(string sid) => DiaSec(r, sid, depart: true);

        // 遅れ
        double shift = 0;
        var atStation = r.Front % 2 == 0 && _data.Line.StationAtZone(r.Front) is { } curSid && r.Dia.Stops.Any(s => s.StationId == curSid);
        if (atStation)
        {
            if (Dep(_data.Line.StationAtZone(r.Front)!) is { } d) shift = Math.Max(0, nowSec - d);
        }
        else
        {
            for (var z = r.Front + step; _data.Line.StationAtZone(z) is { } sid || z % 2 != 0; z += step)
            {
                if (_data.Line.StationAtZone(z) is not { } s2 || Arr(s2) is not { } a) continue;
                if (_model.SecondsTo(RunType(r), r.Dir, r.Front, r.Fraction, s2) is { } sec) shift = Math.Max(0, nowSec + sec - a);
                break;
            }
        }

        var points = new List<object> { new { t = now, y = DiagramY(r), row = SidingRow(r) } };
        var arrAt = new Dictionary<int, DateTime>();
        var depAt = new Dictionary<int, DateTime>();
        var first = r.Front % 2 == 0 ? r.Front : r.Front + step;
        for (var z = first; _data.Line.StationAtZone(z) is { } sid; z += 2 * step)
        {
            if ((z - terminalZone) * step > 0) break; // 終着より先
            if (Arr(sid) is not { } a || Dep(sid) is not { } d) { if (z == terminalZone) break; continue; }
            var here = z == r.Front && atStation;
            var arr = here ? now : Utc(a + shift);
            if (arr < now) arr = now;
            // 先の列車に詰まる（駅間で追いつく・同じ番線がまだふさがっている）なら、その分だけ遅く着く（線の傾きが寝る）。延びた分は、この先の駅にも足す
            if ((z != r.Front || !atStation) && minArrive?.Invoke(z) is { } ma && ma > arr)
            {
                shift += (ma - arr).TotalSeconds * speed;
                arr = ma;
            }
            // 遅れていれば、停車時間を最小（標準の停車時分 DefaultDwellSeconds。ダイヤの停車時間より長くはしない）まで縮めて、最速で出る。
            // 縮めた分だけ遅れを取り戻す（24分着・26分発に2分遅れて着けば、26分着・26分20秒発）。ダイヤより早くは出ない。今いる駅は、今から出られる
            var minDwell = Math.Min(Math.Max(0, d - a), _data.DefaultDwellSeconds);
            var depSec = here ? Math.Max(d, nowSec) : Math.Max(d, a + shift + minDwell);
            shift = Math.Max(0, depSec - d);
            var dep = Utc(depSec);
            if (dep < arr) dep = arr;
            arrAt[z] = arr;
            if (z != r.Front || !atStation) points.Add(new { t = arr, y = (double)z });
            if (z == terminalZone) break;
            // スジずらし（◆を横にずらした発車時刻）・走行順・出発順序で、ダイヤより遅く出るなら、その時刻に出る。延びた分は、この先の駅にも足す
            if (minDepart(z) is { } md && md > dep)
            {
                shift += (md - dep).TotalSeconds * speed;
                dep = md;
            }
            depAt[z] = dep;
            if (dep > arr) points.Add(new { t = dep, y = (double)z });
        }

        AddSidingWait(r, points, now, depAt.TryGetValue(r.Front, out var sidingLeave) ? sidingLeave : null);
        return points.Count > 1 ? new PlanResult(points, arrAt, depAt) : null;
    }

    /// <summary>
    /// 引上線・入換線（新野崎の5番線、大道寺のY線など）にいる始発の列車の見込み: スタフの入換時刻まではその段にいて、
    /// 入換にかかる秒数（shuntSeconds、なければ 75 秒）で出る番線へ移る。入れないと、引上線にいる間ずっと出る番線にいることになり、
    /// その番線を使う列車と番線の競合に見えてしまう。入換時刻が書かれていなければ、発車の入換の秒数前まで引上線にいるものとする
    /// </summary>
    private void AddSidingWait(TrainRun r, List<object> points, DateTime now, DateTime? leave)
    {
        if (r.Front % 2 != 0 || SidingRow(r) is not { } row || _data.Line.StationAtZone(r.Front) is not { } sid
            || !_data.Stations.TryGetValue(sid, out var st)) return;
        // ここから出る列車だけ（終着で引上線に入った列車は、そのまま引上線にいる）
        if (r.Dia?.Stops.FirstOrDefault(s => s.StationId == sid) is not { Dep: not null } diaStop) return;
        var track = r.Stations.FirstOrDefault(s => s.Zone == r.Front)?.Track ?? diaStop.Track;
        var speed = _clock.Now != null && _clock.Speed > 0 ? _clock.Speed : 1;
        var move = (track != null && st.ShuntSeconds.TryGetValue($"{row}>{track}", out var sec) ? sec : 75) / speed;
        DateTime? start = DiaShuntTimes.TryGetValue(r.Dia.Number, out var t) ? now.AddSeconds((t - NowSec()) / speed)
            : leave?.AddSeconds(-move);
        if (start == null) return;
        var s0 = start.Value < now ? now : start.Value;
        if (leave is { } l && s0.AddSeconds(move) > l) s0 = l.AddSeconds(-move) < now ? now : l.AddSeconds(-move);
        var y = (double)r.Front;
        points.Insert(1, new { t = s0, y, row = (string?)row });
        points.Insert(2, new { t = s0.AddSeconds(move), y, row = (string?)null });
    }

    /// <summary>
    /// 駅ごとの出発順序（ダイヤグラム・管理画面の編集用）。その駅をこれから通る列車（上下とも）を、順序の指定があればその順、
    /// なければ発車（通過）の見込みの順に並べる。番線を変えられるか・競合の注意も付ける。
    /// </summary>
    public object OrdersView()
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            var list = new List<object>();
            var plannedAll = PlannedTrains();
            var ranks = ForecastRanks(plannedAll);
            foreach (var station in _data.ControlStations)
            {
                // 出発順序は上り・下りで別々（片方だけ自動に戻せる）
                var orders = new Dictionary<Dir, List<string>>
                {
                    [Dir.Down] = _central.Order(station.Id, Dir.Down)?.ToList() ?? new List<string>(),
                    [Dir.Up] = _central.Order(station.Id, Dir.Up)?.ToList() ?? new List<string>()
                };
                // 自動のときの並び（ダイヤ運転中は、ダイヤグラムで調整したダイヤの発の順）
                var auto = new Dictionary<Dir, List<string>>
                {
                    [Dir.Down] = (DiaMode ? _orderPlan?.Depart.GetValueOrDefault((station.Id, Dir.Down)) : null) ?? new List<string>(),
                    [Dir.Up] = (DiaMode ? _orderPlan?.Depart.GetValueOrDefault((station.Id, Dir.Up)) : null) ?? new List<string>()
                };
                var here = _runs.Values
                    .Where(r => r.HasPosition)
                    .Select(r => (Run: r, St: r.Stations.FirstOrDefault(s => s.St.Id == station.Id && !s.Done)))
                    .Where(x => x.St != null && !IsCutOff(x.Run, x.St))
                    .ToList();

                // 表示の並び（在線位置の画面の出発・到着の枠）: RouteEngine.Forecast.cs（ForecastRanks）
                var down = ranks.GetValueOrDefault((station.Id, Dir.Down));
                var upr = ranks.GetValueOrDefault((station.Id, Dir.Up));
                ForecastRank? RankOf(Dir d) => d == Dir.Down ? down : upr;

                var trains = here
                    .Select(x =>
                    {
                        double? sec = x.St!.IsStart ? 0 : _model.SecondsTo(RunType(x.Run), x.Run.Dir, x.Run.Front, x.Run.Fraction, station.Id);
                        var arriveSec = sec;
                        if (sec != null) sec += DwellSeconds(x.Run, x.St); // 待避線なら最低の停車時分
                        var order = orders[x.Run.Dir];
                        var entered = x.Run.Requests.Any(q => q.Station == x.St && !q.Released && q.CancelledUtc == null
                                                              && q.Def.Circuits.Any(x.Run.Player.TrackCircuits.Contains));
                        return new
                        {
                            train = x.Run.Train,
                            type = x.Run.Type,
                            direction = x.Run.Dir.Label(),
                            stop = x.St.StopType,
                            eta = sec is { } s ? now.AddSeconds(s) : (DateTime?)null,
                            index = order.IndexOf(x.Run.Train),
                            autoIndex = auto[x.Run.Dir].IndexOf(x.Run.Train),
                            track = x.St.Track,
                            trackSource = x.St.TrackSource,
                            options = x.St.Options.ToList(), // 写す（JSON にするのはロックの外）
                            changeable = x.St.Options.Count > 1 && !entered && !(x.St.IsStart && x.St.ShuntFrom != null && x.St.Shunt == GroupState.Requested),
                            warning = order.Count > 0 ? OrderWarning(x.Run, x.St, order) : "",
                            // 指定した発車時刻（ダイヤグラムのスジずらし・出発順序の手打ち）。この30秒前まで出発を開通させない
                            departAt = _central.OpenAt(x.Run.Train, x.St.St.Id),
                            departable = !x.St.Terminal && !IsTurnbackTrack(x.Run, x.St) && x.St.Depart != GroupState.Requested,
                            // その駅で運転を終える列車（終着・行き止まりの番線）。出発順序には出さず、到着順序にだけ出す
                            ends = x.St.Terminal || IsTurnbackTrack(x.Run, x.St),
                            // もうこの駅に着いている（始発・停車中・通過中）。到着順序には出さない
                            arrived = x.St.IsStart || (x.Run.Front - x.St.Zone) * x.Run.Dir.Step() >= 0,
                            // この駅に着く（通過する）見込みの時刻。到着順序はこの順
                            arrive = arriveSec is { } a ? now.AddSeconds(a) : (DateTime?)null,
                            // 出発抑止中（順序に入っていても、ほかの列車を先に出す）
                            held = (_central.Hold(x.Run.Train, station.Id) & HoldKind.Depart) != 0,
                            // 在線位置の画面の並び（出発・到着）。小さいほど先
                            departRank = RankOf(x.Run.Dir)?.Depart.GetValueOrDefault(x.Run.Train, int.MaxValue) ?? int.MaxValue,
                            arriveRank = RankOf(x.Run.Dir)?.Arrive.GetValueOrDefault(x.Run.Train, int.MaxValue) ?? int.MaxValue,
                            status = Status(x.Run, x.St)
                        };
                    })
                    .OrderBy(x => x.index < 0 ? int.MaxValue : x.index)
                    .ThenBy(x => x.held)
                    .ThenBy(x => x.autoIndex < 0 ? int.MaxValue : x.autoIndex)
                    .ThenBy(x => x.eta ?? DateTime.MaxValue)
                    .ToList();
                list.Add(new
                {
                    station = station.Id,
                    name = station.Name,
                    loopTrack = _data.PassingStations.TryGetValue(station.Id, out var ps) ? $"下り{ps.DownTrack}・上り{ps.UpTrack}番線" : null,
                    // 指定中: 順序の列車が1本でもいま走っている（退出した列車だけの古い指定は数えない）。上り・下り別にも出す
                    fixedOrder = trains.Any(t => t.index >= 0),
                    fixedUp = trains.Any(t => t.index >= 0 && t.direction == "上り"),
                    fixedDown = trains.Any(t => t.index >= 0 && t.direction == "下り"),
                    trains,
                    // ダイヤ運転中の、まだ走っていない後運用の列車（在線位置の画面の到着・出発の表だけに出す。出発順序の編集には出さない）
                    planned = plannedAll.Where(p => PlannedPasses(p, station.Id) && !trains.Any(t => t.train == p.Train)).Select(p => new
                    {
                        train = p.Train,
                        type = p.Dia.Type,
                        direction = p.Dir.Label(),
                        from = p.From?.Train,
                        planned = true,
                        ends = p.Dia.TerminalId == station.Id,
                        arrived = p.StartId == station.Id,
                        departRank = RankOf(p.Dir)?.Depart.GetValueOrDefault(p.Train, int.MaxValue) ?? int.MaxValue,
                        arriveRank = RankOf(p.Dir)?.Arrive.GetValueOrDefault(p.Train, int.MaxValue) ?? int.MaxValue
                    }).ToList()
                });
            }

            return list;
        }
    }

    /// <summary>
    /// 単線区間（駅間）ごとの走行順（編集用）。これからその区間を通る列車（上下とも）を、走行順の指定があればその順、
    /// なければ区間に入る見込みの順に並べる。
    /// </summary>
    public object SectionsView()
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            var list = new List<object>();
            foreach (var (a, b) in _data.SingleTrackSections)
            {
                var order = _central.SectionOrder(a, b)?.ToList() ?? new List<string>();
                // 自動のときの並び（ダイヤ運転中は、ダイヤグラムで調整したダイヤの、区間に入る発の順。在線位置の画面の走行順の表もこの順）
                var auto = order.Count == 0 ? EffectiveSectionOrder(a, b)?.ToList() ?? new List<string>() : new List<string>();
                var trains = _runs.Values.Where(r => r.HasPosition && !SectionCleared(r, a, b))
                    .Select(r =>
                    {
                        var entry = r.Dir == Dir.Down
                            ? (_data.Line.ZoneOfStation(a) < _data.Line.ZoneOfStation(b) ? a : b)
                            : (_data.Line.ZoneOfStation(a) < _data.Line.ZoneOfStation(b) ? b : a);
                        var es = r.Stations.FirstOrDefault(s => s.St.Id == entry);
                        double? sec = es == null || es.Done ? 0 : es.IsStart ? 0 : _model.SecondsTo(RunType(r), r.Dir, r.Front, r.Fraction, entry);
                        if (sec != null && es is { Done: false }) sec += DwellSeconds(r, es);
                        return new
                        {
                            train = r.Train,
                            type = r.Type,
                            direction = r.Dir.Label(),
                            from = _data.Line.StationName(entry),
                            eta = sec is { } s ? now.AddSeconds(s) : (DateTime?)null,
                            index = order.IndexOf(r.Train),
                            autoIndex = auto.IndexOf(r.Train),
                            status = es is { Done: false } ? Status(r, es) : "区間内"
                        };
                    })
                    .OrderBy(x => x.index < 0 ? int.MaxValue : x.index)
                    .ThenBy(x => x.autoIndex < 0 ? int.MaxValue : x.autoIndex)
                    .ThenBy(x => x.eta ?? DateTime.MaxValue)
                    .ToList();
                list.Add(new
                {
                    a, b,
                    name = $"{_data.Line.StationName(a)}〜{_data.Line.StationName(b)}",
                    fixedOrder = trains.Any(t => t.index >= 0),
                    trains
                });
            }

            return list;
        }
    }

    /// <summary>列車走行位置に出す列車: 本線・旅客ホーム側の回路に少しでもいる。編成全体が側線・留置線に入ったら出さない。</summary>
    private bool VisibleOnPassengerMap(TrainRun run) =>
        run.HasPosition && _data.Line.HasPassengerCircuit(run.Player.TrackCircuits);

    /// <summary>
    /// 列車走行位置（passenger.html）で列車を押したとき: 行き先・編成（車両ごとの形式）と、お客様が乗り降りする駅の定刻着・到着見込み。
    /// 信号場は出さない。打ち切りの駅で終える。在線していない列車は null。
    /// </summary>
    public object? PassengerTrain(string train)
    {
        lock (_lock)
        {
            var run = _runs.Values.FirstOrDefault(r => VisibleOnPassengerMap(r) && r.Train == train && r.Type != "回送" && r.NumberKind != "回送"); // 回送・試運転はお客様に出さない
            if (run == null) return null;
            var rows = new List<object>();
            var dest = "";
            if (run.Dia is { } dia)
            {
                // 通り過ぎた最後の駅（発車の実績があるか、最後尾がその駅より先）
                var lastPassed = dia.Stops.FindLastIndex(stop =>
                {
                    if (stop.StationId is not { } id) return false;
                    var zone = _data.Line.ZoneOfStation(id);
                    return run.ActualDep.ContainsKey(id) || zone >= 0 && (run.Rear - zone) * run.Dir.Step() > 0;
                });
                for (var i = 0; i < dia.Stops.Count; i++)
                {
                    var s = dia.Stops[i];
                    var change = s.StationId is { } sid ? StopChange(train, dia, sid) : null;
                    var end = change == "打ち切り";
                    var passengerStop = change == "停車" || (change != "通過" && s.Stop is not ("通" or "運停"));
                    if ((end || passengerStop) && !(s.StationId?.EndsWith("S") == true || s.Name.Contains("信号場") || s.Name is "江原信" or "珠川信"))
                    {
                        dest = s.Name;
                        var scheduled = StopArr(dia, s) ?? s.Dep; // 着時刻のない駅（始発・発時刻だけの駅）は発時刻。催物用臨停設定の臨時停車は、通過時刻 − 停車時間
                        var arrived = s.StationId != null && run.ActualArr.ContainsKey(s.StationId);
                        var passed = i <= lastPassed;
                        // 到着見込み: 遅れがあれば定刻に足す。なければ、次の駅だけ走行時間から見込む
                        var scheduledSeconds = ParseDiaTime(scheduled);
                        double? expectedSeconds = !passed && !arrived && run.Delay is { } delay && scheduledSeconds is { } sec
                            ? sec + Math.Max(0, delay) : null;
                        if (expectedSeconds == null && !passed && !arrived && scheduled != null
                            && _clock.Now is { } now && s.StationId is { } nextId
                            && (_data.Line.ZoneOfStation(nextId) - run.Front) * run.Dir.Step() > 0
                            && _data.Line.ZoneOfStation(nextId) >= 0
                            && _model.SecondsTo(RunType(run), run.Dir, run.Front, run.Fraction, nextId) is { } remaining)
                        {
                            // 4時前は、時刻表と同じく前の日の24時以降として数える（定刻の 24:35 と比べられるように）
                            var nowSeconds = now.TotalSeconds < 4 * 3600 ? now.TotalSeconds + 86400 : now.TotalSeconds;
                            expectedSeconds = nowSeconds + remaining;
                        }
                        var expected = expectedSeconds is { } eta ? FormatDiaTime(eta) : null;
                        // 到着見込みが定刻より後の分（画面で赤く出す）
                        var isLate = expectedSeconds is { } estimate && scheduledSeconds is { } plan
                            && Math.Floor(Math.Round(estimate) / 60) > Math.Floor(plan / 60);
                        rows.Add(new { name = s.Name, scheduled, expected, isLate });
                    }
                    if (end) break;
                }
                // 行き先は時刻表の行き先。司令が途中で打ち切りにしたときだけ、打ち切りの駅
                if (!dia.Stops.Any(s => s.StationId is { } sid && _central.StopOverride(train, sid) == "打ち切り") && dia.Dest.Length > 0) dest = dia.Dest;
            }
            return new { train, dest, cars = run.Player.CarModels, averageOccupancy = run.Player.AverageOccupancy, stops = rows };
        }
    }

    /// <summary>発車案内の1本。EventDia があれば、時刻表では通過の駅（その時刻表の EventIndex 番目）: 催物用臨停設定で臨時停車になるときだけ出す。</summary>
    private sealed record PassengerDeparture(string Train, string Kind, string Dest, string Time, int? Track, Dir Direction, double Seconds, DiaTrain? EventDia = null, int EventIndex = -1);
    private ILookup<string, PassengerDeparture>? _departuresByStation; // 時刻表が変わったとき（臨時列車の適用・取消）だけ作り直す
    private object? _departuresDia; // _departuresByStation を作ったときのダイヤの表

    private static readonly string[] PassengerKindOrder = ["特急", "快急", "急行", "区急", "準急", "普通"];
    private ILookup<string, string>? _passengerKinds; // 所定の時刻表から一度だけ作る

    /// <summary>
    /// 駅に「普段」止まる種別（駅 → 種別）: その駅を走るその種別の列車のうち、1割以上が止まるもの。
    /// 浜園の特急のように、臨時停車のときだけ止まるものは入らない（発車案内では「臨時停車」と出す）。
    /// </summary>
    private ILookup<string, string> PassengerKinds() => _passengerKinds ??= _data.BaseDia.Values.Where(t => PassengerKindOrder.Contains(t.Type))
        .SelectMany(t => t.Stops.Where(s => s.StationId != null).Select(s => (Id: s.StationId!, Kind: t.Type, Stop: s.Stop == "停")))
        .GroupBy(x => (x.Id, x.Kind))
        .Where(g => g.Count(x => x.Stop) * 10 >= g.Count())
        .ToLookup(g => g.Key.Id, g => g.Key.Kind);

    /// <summary>時刻表の、駅ごとのお客様が乗れる発車（回送を除く。終着の駅は含めない）。時刻の順。</summary>
    private ILookup<string, PassengerDeparture> BuildPassengerDepartures()
    {
        var entries = new List<(string Station, PassengerDeparture Train)>();
        foreach (var dia in _data.Dia.Values)
        {
            if (dia.Type == "回送" || TrainInfo(dia.Number) is not { } info) continue;
            var dest = dia.Stops.LastOrDefault(s => s.Stop == "停");
            if (dest == null) continue;
            foreach (var stop in dia.Stops)
            {
                // 最後の停車駅は終着なので出さない。ただし発時刻があり、行き先がその先（路線の外）なら、そこからも発車する
                if (stop == dest && (stop.Dep == null || dia.Dest.Length == 0 || stop.Name == dia.Dest)) break;
                // 通過の駅は、催物用臨停設定で臨時停車になりうる種別だけ（出すかは、案内のときに設定を見て決める。発は通過時刻）
                if (stop.Stop == "通" && stop.StationId != null && EventStops.KindOrder.Contains(dia.Type) && (stop.Dep ?? stop.Arr) is { } passAt && ParseDiaTime(passAt) is { } passSec)
                {
                    entries.Add((stop.StationId, new PassengerDeparture(dia.Number, dia.Type, dia.Dest.Length > 0 ? dia.Dest : dest.Name, passAt, stop.Track, info.Dir, passSec, dia, dia.Stops.IndexOf(stop))));
                    continue;
                }
                if (stop.Stop != "停" || stop.StationId == null || stop.Dep == null || ParseDiaTime(stop.Dep) is not { } sec) continue;
                entries.Add((stop.StationId, new PassengerDeparture(dia.Number, dia.Type, dia.Dest.Length > 0 ? dia.Dest : dest.Name, stop.Dep, stop.Track, info.Dir, sec)));
            }
        }
        return entries.OrderBy(e => e.Train.Seconds).ToLookup(e => e.Station, e => e.Train);
    }

    /// <summary>
    /// 列車走行位置で駅を押したときの発車案内: 方面ごとに、これから発車する4本（定刻発・種別・行き先・のりば・運転整理中か）。
    /// まだ出ていない列車は時刻表の時刻から、走っている列車はまだその駅を出ていなければ入れる（通過・打ち切りの指示がある列車は除く）。
    /// 出発順序の指示があれば、その順に並べ替える。
    /// </summary>
    public object PassengerDepartures(string id)
    {
        lock (_lock)
        {
            if (_departuresByStation == null || !ReferenceEquals(_departuresDia, _data.Dia))
            {
                _departuresDia = _data.Dia;
                _departuresByStation = BuildPassengerDepartures();
            }
            var now = NowSec();
            var advisories = _central.PassengerAdvisoryTrains();
            var zone = _data.Line.ZoneOfStation(id);
            var running = _runs.Values.Where(r => r.HasPosition && r.Dia != null).ToLookup(r => r.Dia!.Number);
            var candidates = new List<PassengerDeparture>();
            foreach (var dep in _departuresByStation[id].Where(d => d.EventDia == null || EventStops.Applies(d.EventDia, d.EventIndex)))
            {
                var runs = running[dep.Train];
                if (!runs.Any())
                {
                    if (dep.Seconds >= now) candidates.Add(dep);
                    continue;
                }
                foreach (var run in runs)
                {
                    if (run.ActualDep.ContainsKey(id) || (run.Rear - zone) * run.Dir.Step() > 0) continue;
                    var station = run.Stations.FirstOrDefault(s => s.St.Id == id);
                    if (station != null && (station.Done || IsCutOff(run, station))) continue;
                    if (_central.StopOverride(run.Train, id) is "通過" or "打ち切り") continue;
                    var adjusted = _orderPlan?.Times.GetValueOrDefault((run.Train, id))?.Dep ?? dep.Seconds;
                    if (run.NumberKind != "回送") // 今の列番が回送の列車は出さない
                        candidates.Add(dep with { Train = run.Train, Kind = run.NumberKind, Seconds = adjusted });
                }
            }

            PassengerDeparture[] Next(Dir dir)
            {
                var rows = candidates.Where(d => d.Direction == dir).OrderBy(d => d.Seconds).ToArray();
                var order = EffectiveOrder(id, dir);
                if (order is { Count: > 0 })
                {
                    // 順序の指示に入っていない列車（まだ走っていないものなど）は、時刻の位置に残す
                    var ranks = order.Select((name, i) => (name, i)).ToLookup(x => x.name, x => x.i);
                    var ordered = new Queue<PassengerDeparture>(rows.Where(d => ranks.Contains(d.Train)).OrderBy(d => ranks[d.Train].First()));
                    for (var i = 0; i < rows.Length; i++)
                        if (ranks.Contains(rows[i].Train)) rows[i] = ordered.Dequeue();
                }
                return rows.Take(4).ToArray();
            }

            return new
            {
                time = FormatDiaTime(now),
                boards = new[] { Dir.Up, Dir.Down }.Select(dir => new
                {
                    direction = dir.Label(),
                    trains = Next(dir).Select(d => new
                    {
                        train = d.Train, kind = d.Kind, dest = d.Dest, time = d.Time, track = d.Track,
                        // その種別がこの駅に普段は止まらない（浜園の特急など）: 臨時停車
                        specialStop = d.EventDia != null || PassengerKindOrder.Contains(d.Kind) && !PassengerKinds()[id].Contains(d.Kind),
                        advisory = advisories.Contains(d.Train)
                    }).ToArray()
                }).ToArray()
            };
        }
    }

    /// <summary>
    /// 列車走行位置（passenger.html）用。駅（信号場を除く。路線の順）と、その駅に止まる種別、在線している列車の位置（ゾーン）・種別・行き先・遅れ。
    /// </summary>
    public object PassengerPositions()
    {
        lock (_lock)
        {
            var line = _data.Line;
            var kinds = PassengerKinds();
            var stations = line.StationIds.Where(id => !line.StationName(id).Contains("信号場"))
                .Select(id => new
                {
                    id, name = line.StationName(id), zone = line.ZoneOfStation(id),
                    kinds = PassengerKindOrder.Where(kind => kinds[id].Contains(kind)).ToArray()
                }).ToArray();
            var advisories = _central.PassengerAdvisoryTrains();
            var trains = _runs.Values.Where(r => VisibleOnPassengerMap(r) && r.Type != "回送" && r.NumberKind != "回送").Select(r => new // 回送・試運転はお客様に出さない
            {
                // 種別は今の列番の記号から。時刻表と同じ種別なら時刻表の書き方（通勤特急 など）
                train = r.Train, advisory = advisories.Contains(r.Train), kind = r.NumberKind,
                type = r.Dia is { } dia && dia.Type == r.NumberKind ? dia.TypeLabel : r.NumberKind, direction = r.Dir.Label(), zone = r.Front,
                destination = r.Dia is { } d ? (d.Dest.Length > 0 ? d.Dest : d.Stops.LastOrDefault()?.Name) : null, // 時刻表の行き先
                delay = r.Delay is { } seconds && seconds >= 60 ? (int)(seconds / 60) : 0
            }).ToArray();
            return new { stations, trains, updated = DateTimeOffset.UtcNow, gameTime = _clock.NowText }; // 最終更新はゲームの時刻（時計がなければ updated）
        }
    }

    /// <summary>
    /// 在線位置の画面（tid.html）用。列車が踏んでいる軌道回路と、開通させている進路のうち、まだ使う軌道回路を返す。
    /// </summary>
    public object TidView()
    {
        lock (_lock)
        {
            var runs = _runs.Values.Where(r => r.HasPosition).ToList();
            return new
            {
                trains = runs.Select(r => new
                {
                    train = r.Train,
                    type = r.Type,
                    direction = r.Dir.Label(),
                    ai = r.ConnId.StartsWith(PlayerRegistry.AiPrefix), // にぎやかしの列車（札の右上に * を付ける）
                    circuits = r.Player.TrackCircuits.ToList(),
                    // 少し前（1分以内）に踏んでいた回路。在線をどの枝で塗るかの手がかり（通ってきた側の枝を選ぶ）
                    recent = r.SeenCircuits.Keys.Where(c => !r.Player.TrackCircuits.Contains(c)).ToList(),
                    // 遅れ（分）。1分以上のときだけ
                    delay = r.Delay is { } dl && dl >= 60 ? (int)(dl / 60) : (int?)null
                }).ToList(),
                routes = runs.SelectMany(r => r.Requests
                    .Where(q => !q.Released && q.CancelledUtc == null)
                    .Select(q => new
                    {
                        train = r.Train,
                        station = q.Station.St.Id,
                        route = q.Def.Name,
                        // 開通を確認できたか: ゲームの進路の一覧に出ている（一覧から消えたら解除済みで、ここには出ない）。
                        // ゲームが受け付けた返事（OK・同一進路）だけでは、まだ要求中とする（信号制御盤の白い灯と同じ条件。2026-11-19 ユーザーの決まり）
                        open = q.Seen,
                        reply = r.Results.GetValueOrDefault($"{q.Def.Interlock}/{q.Def.Name}") ?? "",
                        circuits = LiveCircuits(r, q).ToList(),
                        // 進路の回路全部。列車が入って circuits が鎖錠中の回路だけになっても、在線位置の画面が通る側を引けるように
                        all = q.Def.Circuits,
                        // 接近鎖錠の区間（出発なら出る番線の回路）。在線位置の画面が、進路の線をどちらの枝へ引くかの手がかりにする
                        approach = q.Def.Approach,
                        // 行き先の番線の回路（「3番線」へ入る進路なら、その番線の回路）。新野崎の 10LC・10LD のように
                        // 回路の並びが同じで行き先の番線だけ違う進路を、在線位置の画面が正しい枝へ引くための手がかり
                        dest = DestCircuit(q.Def)
                    })).ToList(),
                // 単線区間の方向てこ: direction は "下り" / "上り"（まだ一度も使われていなければ null）。locked は、いま使われているか
                levers = _data.SingleTrackSections.Select(s => new
                {
                    a = s.Item1,
                    b = s.Item2,
                    direction = _levers.TryGetValue(s, out var lv) ? lv.Dir.Label() : null,
                    locked = _levers.TryGetValue(s, out var lk) && lk.Locked
                }).ToList()
            };
        }
    }

    /// <summary>進路の行き先が「N番線」なら、その番線の軌道回路（Stations の番線の回路）。それ以外は null。</summary>
    private string? DestCircuit(RouteDef def)
    {
        var m = System.Text.RegularExpressions.Regex.Match(def.To, @"^(\d+)番線$");
        return m.Success && _data.Stations.TryGetValue(def.StationId, out var st)
               && st.TrackCircuit.TryGetValue(int.Parse(m.Groups[1].Value), out var c) ? c : null;
    }

    /// <summary>
    /// 列車の時刻表（在線位置の画面で、列番を押したときに出す）。全駅（進路を扱わない駅も含む）の着・発（通過）時刻。
    /// ダイヤにある列車はダイヤの時刻。ない列車は、ロープレ時刻（なければ今の時刻）に始発駅を発車したとして、標準運転時分で見込む。
    /// </summary>
    /// <summary>行路の項目の、乗る区間とダイヤの時刻（列車の途中から・途中までなら、その駅）。</summary>
    private (string? From, string? Dep, string? To, string? Arr) DutySpan(DutyItem x)
    {
        if (x.Kind == "休憩" || _data.DiaOf(x.Train) is not { } td) return (x.From, null, x.To, null);
        var inLine = td.Stops.Where(s => s.StationId != null && _data.Line.ZoneOfStation(s.StationId) >= 0).ToList();
        if (inLine.Count == 0) inLine = td.Stops;
        var a = x.From != null ? td.Stops.FirstOrDefault(s => s.Name == x.From) ?? inLine[0] : inLine[0];
        var b = x.To != null ? td.Stops.LastOrDefault(s => s.Name == x.To) ?? inLine[^1] : inLine[^1];
        return (a.Name, a.Dep ?? a.Arr, b.Name, b.Arr ?? b.Dep);
    }

    /// <summary>
    /// 行路の表示用データ: 項目（区間・時刻）、ハンドルタイム（乗務・入換・便乗の時間の合計）、始まり・終わりの時刻。
    /// current の列車の項目に印を付ける。
    /// </summary>
    public object DutyView(Duty d, string? current)
    {
        double handle = 0;
        string? first = null, last = null;
        var items = d.Items.Select(x =>
        {
            var sp = DutySpan(x);
            if (x.Kind != "休憩")
            {
                if (ParseDiaTime(sp.Dep) is { } t1 && ParseDiaTime(sp.Arr) is { } t2 && t2 > t1) handle += t2 - t1;
                first ??= sp.Dep;
                last = sp.Arr ?? last;
            }

            return new { kind = x.Kind, train = x.Train, from = sp.From, dep = sp.Dep, to = sp.To, arr = sp.Arr, note = x.Note, current = current != null && x.Train == current };
        }).ToList();
        return new { id = d.Id, items, handleMinutes = (int)Math.Round(handle / 60), start = first, end = last };
    }

    /// <summary>
    /// 行路のうち、いま乗る列車: 走っている列車があればそれ。なければロープレ時刻（なければ今）でまだ終わっていない最初の列車。
    /// すべて終わっていれば最後の列車。乗る列車がなければ null。
    /// </summary>
    public string? CurrentTrainOf(IEnumerable<Duty> duties)
    {
        lock (_lock) return CurrentTrainLocked(duties, RunningNumbers());
    }

    /// <summary>行路ごとの、いま乗る列車（行路の一覧用。<see cref="CurrentTrainOf"/> を行路1本ずつ呼ぶのと同じ答えを、1回のロックで出す）。</summary>
    public List<string?> CurrentTrainsOf(IReadOnlyList<Duty> duties)
    {
        lock (_lock)
        {
            var running = RunningNumbers();
            return duties.Select(d => CurrentTrainLocked([d], running)).ToList();
        }
    }

    /// <summary>走っている列車の列番（サーバーの列番と、ゲームの列番）。_lock の中で呼ぶ。</summary>
    private HashSet<string> RunningNumbers()
    {
        var set = new HashSet<string>();
        foreach (var r in _runs.Values)
        {
            if (!r.HasPosition) continue;
            set.Add(r.Train);
            set.Add(RailData.NormalizeTrain(r.GameTrain));
        }
        return set;
    }

    private string? CurrentTrainLocked(IEnumerable<Duty> duties, HashSet<string> runningNumbers)
    {
        var items = duties.SelectMany(d => d.Items).Where(x => x.Kind != "休憩" && _data.DiaOf(x.Train) != null).ToList();
        if (items.Count == 0) return null;
        var running = items.FirstOrDefault(x => runningNumbers.Contains(x.Train));
        if (running != null) return running.Train;
        var now = NowSec();
        var ordered = items.Select(x => (x, Arr: ParseDiaTime(DutySpan(x).Arr), Dep: ParseDiaTime(DutySpan(x).Dep)))
            .OrderBy(t => t.Dep ?? double.MaxValue).ToList();
        return (ordered.FirstOrDefault(t => t.Arr is { } a && a >= now).x ?? ordered[^1].x).Train;
    }

    /// <summary>
    /// 走っている列車の、時刻表の列番。同じ列番が2本走ってサーバーでは 1985CX と呼んでいる列車は、時刻表の 1985C を返す。
    /// 走っていない・時刻表にない列車は、受け取った列番のまま。
    /// </summary>
    public string DiaNumberOf(string train)
    {
        var key = RailData.NormalizeTrain(train);
        lock (_lock) return RunByTrain(key)?.Dia?.Number ?? key;
    }

    /// <summary>その列車が今走っているか（勤怠管理の画面用）。</summary>
    public bool IsRunning(string train)
    {
        lock (_lock)
            return _runs.Values.Any(r => r.HasPosition && (r.Train == train || RailData.NormalizeTrain(r.GameTrain) == train));
    }

    /// <summary>
    /// 乗務タブレット用のデータ。スタフと同じ並び（ダイヤの全駅。時刻・番線・記事）に、実際の着発・通過済みか・進路の状態（運転士に見せる3段階）、
    /// 次の駅、遅れ、終点のあと（折返・列番変更）、受け持つ行路（Data/Duties.csv）を付ける。ダイヤにない列車は null。
    /// </summary>
    public object? TabletView(string train, DutyRoster roster, List<Duty>? crewDuties = null, string? crew = null)
    {
        lock (_lock)
        {
            var key = RailData.NormalizeTrain(train);
            var run = _runs.Values.FirstOrDefault(r => r.Train == key) ?? _runs.Values.FirstOrDefault(r => RailData.NormalizeTrain(r.GameTrain) == key);
            var dia = run?.Dia ?? _data.DiaOf(key);
            if (dia == null) return null;

            // どこまで通ったか（実際に出た駅、または列車がもう先にいる駅）
            var stepD = run?.Dir.Step() ?? 0;
            var lastPassed = -1;
            if (run is { HasPosition: true })
                for (var i = 0; i < dia.Stops.Count; i++)
                {
                    if (dia.Stops[i].StationId is not { } sid) continue;
                    var z = _data.Line.ZoneOfStation(sid);
                    if (run.ActualDep.ContainsKey(sid) || z >= 0 && (run.Front - z) * stepD > 0 && (run.Rear - z) * stepD > 0) lastPassed = i;
                }

            var nextIndex = -1;
            var stops = dia.Stops.Select((s, i) =>
            {
                var control = s.StationId != null && _data.Stations.TryGetValue(s.StationId, out var sd) && sd.RouteControl;
                var st = run != null && s.StationId != null ? run.Stations.FirstOrDefault(x => x.St.Id == s.StationId && !x.Done) : null;
                var passed = i <= lastPassed;
                if (!passed && nextIndex < 0 && run is { HasPosition: true } && s.StationId != null && _data.Line.ZoneOfStation(s.StationId) >= 0) nextIndex = i;
                // 臨時停車（催物用臨停設定・列車別設定）: 時刻表で通過の駅を「臨停」にし、通過時刻を発車時刻として書く（通過時刻がなければ空）
                var special = s.Stop == "通" && s.StationId != null && StopChange(run?.Train ?? dia.Number, dia, s.StationId) == "停車";
                return new
                {
                    name = s.Name,
                    control,
                    stop = special ? "臨停" : s.Stop,
                    arr = s.Arr, // 臨時停車用に時刻を変えた列車は、臨時停車の駅にも着が書いてある
                    dep = special ? s.Dep ?? s.Arr : s.Dep,
                    track = s.Track ?? (s.StationId != null && dia.Stations.TryGetValue(s.StationId, out var ds) ? ds.Track : null),
                    note = s.Note ?? "",
                    actArr = s.StationId != null && run != null && run.ActualArr.TryGetValue(s.StationId, out var aa) ? FormatDiaTime(aa) : null,
                    actDep = s.StationId != null && run != null && run.ActualDep.TryGetValue(s.StationId, out var ad) ? FormatDiaTime(ad) : null,
                    passed,
                    route = st != null && control ? ClientStatus(run!, st) : null
                };
            }).ToList();

            // 行路: ログインした乗務員の行路（その列車を含むもの）、なければその列車を受け持つ行路（自分の行路にない列車のスタフは「見るだけ」）。
            // どちらもなければ乗務員の最初の行路。項目をダイヤの時刻つきで並べる
            var d = (crewDuties ?? new List<Duty>()).FirstOrDefault(x => x.Items.Any(i => i.Train == dia.Number))
                    ?? roster.DutyOfTrain(dia.Number) ?? crewDuties?.FirstOrDefault();
            var viewOnly = crewDuties is { Count: > 0 } && !crewDuties.Any(x => x.Items.Any(i => i.Train == dia.Number));
            object? duty = d == null ? null : DutyView(d, dia.Number);

            return new
            {
                train = dia.Number,
                type = dia.Type,
                label = dia.TypeLabel,
                dest = dia.Stops.Count > 0 ? dia.Stops[^1].Name : "",
                unyo = _data.Turns.UnyoOf(dia),
                running = run is { HasPosition: true },
                delay = run?.Delay is { } dl && dl >= 60 ? (int)(dl / 60) : (int?)null,
                gameTime = FormatDiaTime(NowSec()),
                next = nextIndex,
                renumber = dia.Next is { } nx ? new { number = nx.Number, station = _data.Line.StationName(nx.StationId) } : null,
                // 後運用（差し替えていれば changed、時刻表の後運用が original）
                turn = _data.Turns.TurnOf(dia) is { } tn
                    ? new { kind = tn.Kind, number = tn.Number, time = tn.Time, how = tn.How, via = tn.Via, inPlace =TurnsInPlace(dia) ? dia.Stops[^1].Track : null, changed = _data.Turns.IsChanged(dia.Number), original = dia.Turn?.Number }
                    : null,
                stops,
                duty,
                crew,
                viewOnly
            };
        }
    }

    /// <summary>
    /// 駅の接近案内（乗務タブレットの tablet-arrivals.html）。上り・下りごとに、駅へ向かってくる列車と駅にいる列車を、到着する順に並べた「段」の一覧。
    /// 段は 1 列車の「着」と「発」。待避などで、あとから来る列車を先に出す列車は、「着」の段と「発」の段に分け、「発」の段は先に出す列車の段のあとに置く。
    /// 時刻はダイヤの時刻。arr・dep は "HH:mm:ss"、"split"（ほかの段で出す）、"none"（時刻がない。通過の着・始発の着・終着の発・ダイヤにない列車）。
    /// 順は、駅にいる列車が先、あとは駅からの近さ（watch.Distance。回路の数）の順。監視の範囲（watch.Range）にいない列車は出さない。maxTrains 列車まで。
    /// ダイヤ運転中は、この駅で折り返して次になる列車（後運用）を、折り返す前の列車が範囲に入ったら、折り返す前から「発」の段（状態は「折返」）で出す。
    /// 先に出す順は、予告の並び（ForecastRanks の出発の順。司令員が決めた出発順序・抑止を含む）。
    /// </summary>
    public object ArrivalBoard(ArrivalBoards.StationWatch watch, int maxTrains)
    {
        lock (_lock)
        {
            var zone = _data.Line.ZoneOfStation(watch.Id);
            var planned = PlannedTrains();
            var ranks = ForecastRanks(planned);

            // 駅からの近さ（駅にいれば 0）。この駅へ向かっていない・範囲にいなければ null
            (StationRun St, int Dist, bool Here)? Near(TrainRun r)
            {
                var st = r.Stations.FirstOrDefault(s => s.St.Id == watch.Id && !s.Done);
                if (!r.HasPosition || st == null || r.ActualDep.ContainsKey(watch.Id) || IsCutOff(r, st)) return null;
                if (InSiding(r, st, zone)) return null; // 留置線・引上線にいる列車（お客様には出さない）
                var here = r.Player.Zones.Contains(zone) || (zone - r.Front) * r.Dir.Step() <= 0;
                var dist = here ? 0 : r.Player.TrackCircuits.Where(watch.Range.Contains).Select(c => watch.Distance.TryGetValue(c, out var d) ? d : int.MaxValue).DefaultIfEmpty(-1).Min();
                return dist < 0 ? null : (st, dist, here);
            }

            var boards = new List<object>();
            foreach (var dir in new[] { Dir.Down, Dir.Up })
            {
                // Run が null の候補は後運用の列車（P）。近さは折り返す前の列車のもの
                var cands = new List<(TrainRun? Run, StationRun? St, PlannedTrain? P, int Dist, double T)>();
                foreach (var r in _runs.Values.Where(r => r.HasPosition && r.Dir == dir))
                    if (Near(r) is { } n) cands.Add((r, n.St, null, n.Dist, DiaSec(r, watch.Id, depart: n.Here) ?? double.MaxValue));
                foreach (var p in planned.Where(p => p.Dir == dir && p.StartId == watch.Id))
                    if (p.From != null && Near(p.From) is { } n) cands.Add((null, null, p, n.Dist, PlannedDiaSec(p, watch.Id, depart: true) ?? double.MaxValue));

                // 後運用の列車は、折り返す前の列車より前に出さない
                cands = cands.OrderBy(c => c.Dist).ThenBy(c => c.P != null ? 1 : 0).ThenBy(c => c.T).ToList();
                // 先に出す順（手で決めた順・ダイヤ運転中だけ。ダイヤ運転でなく手で決めていなければ、待たない）
                var order = EffectiveOrder(watch.Id, dir) == null && !DiaMode ? null
                    : ranks.GetValueOrDefault((watch.Id, dir))?.Depart.OrderBy(x => x.Value).Select(x => x.Key).ToList();
                var stages = new List<ArrivalStage>();
                var trains = new List<string>();
                var emitted = new HashSet<string>();
                var deferred = new List<(HashSet<string> After, ArrivalStage Stage)>();

                ArrivalStage Stage(TrainRun r, StationRun st, string? arr, string? dep, string kind) => new(
                    r.Train,
                    r.Dia?.Number ?? r.Train,
                    r.Type,
                    r.Dia?.TypeLabel ?? r.Type,
                    r.Dia is { Stops.Count: > 0 } d ? d.Stops[^1].Name : "",
                    st.Track ?? (r.Dia != null && r.Dia.Stations.TryGetValue(watch.Id, out var ds) ? ds.Track : null),
                    arr ?? "none",
                    dep ?? "none",
                    kind,
                    st.IsStart ? "始発" : r.ActualArr.ContainsKey(watch.Id) ? (st.StopType == "通過" && !st.DepartStop ? "通過中" : "到着") : "接近",
                    r.Delay is { } dl && dl >= 60 ? (int)(dl / 60) : null);

                void Flush()
                {
                    foreach (var x in deferred.Where(x => x.After.IsSubsetOf(emitted)).ToList())
                    {
                        stages.Add(x.Stage);
                        deferred.Remove(x);
                    }
                }

                foreach (var c in cands)
                {
                    if (c.P is { } pl)
                    {
                        // 後運用の列車: 出発の順で前の列車（範囲にいるもの）のあとに「発」の段を置く。
                        // 折り返す前の列車は（向きが同じなら）その段のあと。ふつうは逆向きなので、反対の向きの表の終着の段になる
                        trains.Add(pl.Train);
                        var stop0 = pl.Dia.Stops.FirstOrDefault(x => x.StationId == watch.Id);
                        var afterP = new HashSet<string>();
                        if (pl.From is { } pf && pf.Dir == dir) afterP.Add(pf.Train);
                        if (order != null)
                            foreach (var t in order.TakeWhile(t => t != pl.Train))
                                if (cands.Any(o => (o.Run?.Train ?? o.P?.Train) == t)) afterP.Add(t);
                        var stageP = new ArrivalStage(
                            pl.Train,
                            pl.Dia.Number,
                            TrainInfo(pl.Train)?.Type ?? pl.Dia.Type,
                            pl.Dia.TypeLabel,
                            pl.Dia.Stops.Count > 0 ? pl.Dia.Stops[^1].Name : "",
                            _central.TrackOverride(pl.Train, watch.Id) ?? stop0?.Track ?? (pl.Dia.Stations.TryGetValue(watch.Id, out var pds) ? pds.Track : null),
                            "none",
                            stop0?.Dep ?? stop0?.Arr ?? "none",
                            "発",
                            "折返",
                            null);
                        if (afterP.IsSubsetOf(emitted)) stages.Add(stageP);
                        else deferred.Add((afterP, stageP));
                        emitted.Add(pl.Train);
                        Flush();
                        continue;
                    }

                    var (r, st) = (c.Run!, c.St!);
                    var stop = r.Dia?.Stops.FirstOrDefault(x => x.StationId == watch.Id);
                    var arrT = stop?.Arr ?? (st.StopType != "通過" ? stop?.Dep : null);
                    var depT = stop?.Dep ?? stop?.Arr;
                    trains.Add(r.Train);
                    if (st.Terminal) stages.Add(Stage(r, st, arrT ?? depT, null, "終着"));
                    else if (st.StopType == "通過" && !st.DepartStop) stages.Add(Stage(r, st, null, depT, "通過"));
                    else
                    {
                        // この列車より先に出す列車（待避の相手・発車順で前の列車）で、まだこの駅を出ていないもの。
                        // まだ範囲にいない待避の相手も待つ（その「発」の段は最後に置く）
                        var after = st.Partners.Select(p => p.Train)
                            .Where(t => t != r.Train && (cands.Any(o => o.Run?.Train == t) || !PassedStation(t, watch.Id)))
                            .ToHashSet();
                        if (order != null)
                            foreach (var t in order.TakeWhile(t => t != r.Train))
                                if (cands.Any(o => (o.Run?.Train ?? o.P?.Train) == t && (o.Dist > c.Dist || o.Dist == 0 && c.Dist == 0))) after.Add(t);
                        var arrived = st.IsStart || r.ActualArr.ContainsKey(watch.Id);
                        if (after.Count == 0) stages.Add(Stage(r, st, st.IsStart ? null : arrT, depT, st.IsStart ? "発" : "着発"));
                        else
                        {
                            if (!arrived) stages.Add(Stage(r, st, arrT, "split", "着"));
                            deferred.Add((after, Stage(r, st, st.IsStart ? null : "split", depT, "発")));
                        }
                    }

                    emitted.Add(r.Train);
                    Flush();
                }

                stages.AddRange(deferred.Select(x => x.Stage));
                // 直近 maxTrains 列車まで（同じ列車の「着」と「発」の段は 1 列車と数える）
                var shown = trains.Take(maxTrains).ToHashSet();
                boards.Add(new { direction = dir.Label(), stages = stages.Where(s => shown.Contains(s.Run)).ToList() });
            }

            return new { station = watch.Id, name = watch.Name, gameTime = FormatDiaTime(NowSec()), boards };
        }
    }

    /// <summary>
    /// その駅で始まる・終わる列車が、留置線・引上線（新野崎5番線、大道寺5番線・X線など。着発の番線でない所）にいるか。
    /// 着発の番線（ホーム）や場内の進路の回路にいれば、いない。始発の入換を待っている・終着の引上げでホームを出た列車は、駅の外の回路（X線）にいても、いる。
    /// </summary>
    private static bool InSiding(TrainRun run, StationRun st, int zone)
    {
        if (!st.Terminal && !st.IsStart && !IsTurnbackTrack(run, st)) return false;
        var mine = run.Player.TrackCircuits;
        if (mine.Count == 0) return false;
        var platforms = new[] { Dir.Down, Dir.Up }.SelectMany(d => st.St.Routes[d].Arrive.Keys.Concat(st.St.Routes[d].Depart.Keys))
            .Select(k => st.St.TrackCircuit.GetValueOrDefault(k)).OfType<string>();
        if (platforms.Any(mine.Contains) || st.ArrivePath.SelectMany(r => r.Circuits).Any(mine.Contains)) return false;
        return st.ShuntOut == GroupState.Requested || st.IsStart && st.ShuntFrom != null || run.Player.Zones.Contains(zone);
    }

    /// <summary>その列車が、もうその駅を出た（通った）か。走っていない列車も出たとみなす。</summary>
    private bool PassedStation(string train, string stationId) =>
        _runs.Values.FirstOrDefault(r => r.Train == train) is not { } r || r.ActualDep.ContainsKey(stationId) || r.Stations.All(s => s.St.Id != stationId || s.Done);

    /// <summary>接近案内の 1 段。arr・dep は "HH:mm:ss"・"split"・"none"。stage は "着発"・"着"・"発"・"通過"・"終着"。status は "接近"・"到着"・"通過中"・"始発"・"折返"（折り返して次になる、まだ走っていない列車）。</summary>
    public sealed record ArrivalStage(
        [property: System.Text.Json.Serialization.JsonIgnore] string Run,
        string Train, string Kind, string Type, string Dest, int? Track, string Arr, string Dep, string Stage, string Status, int? Delay);

    public object? Timetable(string train, TimeSpan? clock)
    {
        lock (_lock)
        {
            var key = RailData.NormalizeTrain(train);
            var run = _runs.Values.FirstOrDefault(r => r.Train == key);
            var dia = run?.Dia ?? _data.DiaOf(key);
            bool Control(string? id) => id != null && _data.Stations.TryGetValue(id, out var s) && s.RouteControl;

            // 列車別設定の臨時停車・臨時通過・打ち切りと、催物用臨停設定の臨時停車。stop は「臨停」「臨通」「打切」になる
            string? Override(string? id) => id != null ? StopChange(key, dia, id) : null;

            if (dia != null && dia.Stops.Count > 0)
            {
                var rows = new List<(string Name, string? Sid, string Stop, string? Arr, string? Dep)>();
                foreach (var s in dia.Stops)
                {
                    rows.Add(Override(s.StationId) switch
                    {
                        "停車" when s.Stop == "通" => (s.Name, s.StationId, "臨停", s.Arr ?? s.Dep, s.Dep ?? s.Arr),
                        "通過" when s.Stop != "通" => (s.Name, s.StationId, "臨通", null, s.Dep ?? s.Arr),
                        "打ち切り" => (s.Name, s.StationId, "打切", s.Arr ?? s.Dep, null),
                        _ => (s.Name, s.StationId, s.Stop, s.Arr, s.Dep)
                    });
                    if (Override(s.StationId) == "打ち切り") break; // 打ち切った先の駅は出さない
                }

                // 計画（ダイヤの時刻）と実（着いた・出た駅は実際の時刻、これからの駅は計画に今の遅れを足した見込み）。
                // 着いた・出た駅は、計画の欄を「-」にする
                var stepD = run?.Dir.Step() ?? 0;
                var lastPassed = -1; // ここまでの駅はもう出た（通過した）
                if (run is { HasPosition: true })
                    for (var i = 0; i < rows.Count; i++)
                    {
                        if (rows[i].Sid is not { } sid) continue;
                        var z = _data.Line.ZoneOfStation(sid);
                        if (run.ActualDep.ContainsKey(sid) || z >= 0 && (run.Front - z) * stepD > 0 && (run.Rear - z) * stepD > 0) lastPassed = i;
                    }

                var delay = Math.Max(0, run?.Delay ?? 0);
                string? Shift(string? t) => run != null && ParseDiaTime(t) is { } v ? FormatDiaTime(v + delay) : null;
                // 番線: 所定（ダイヤ）と、今決まっている番線（走っている列車はその駅で決めた番線、なければ集中扱いの番線変更）
                int? PlanTrack(int i) => dia.Stops[i].Track ?? (dia.Stops[i].StationId is { } psid && dia.Stations.TryGetValue(psid, out var pds) ? pds.Track : null);
                int? NowTrack(string? sid)
                {
                    if (sid == null) return null;
                    if (run?.Stations.LastOrDefault(x => x.St.Id == sid) is { Track: { } rt }) return rt;
                    return _central.TrackOverride(key, sid);
                }
                var stops = rows.Select((r, i) =>
                {
                    var planTrack = PlanTrack(i);
                    var nowTrack = NowTrack(r.Sid);
                    var arrDone = i <= lastPassed || r.Sid != null && run != null && run.ActualArr.ContainsKey(r.Sid);
                    var depDone = i <= lastPassed || r.Sid != null && run != null && run.ActualDep.ContainsKey(r.Sid);
                    var actArr = r.Sid != null && run != null && run.ActualArr.TryGetValue(r.Sid, out var aa) ? FormatDiaTime(aa) : Shift(r.Arr);
                    var actDep = r.Sid != null && run != null && run.ActualDep.TryGetValue(r.Sid, out var ad) ? FormatDiaTime(ad) : Shift(r.Dep);
                    return (object)new
                    {
                        name = r.Name, control = Control(r.Sid), stop = r.Stop,
                        planArr = r.Arr == null ? null : arrDone ? "-" : r.Arr,
                        actArr = r.Arr == null ? null : actArr,
                        planDep = r.Dep == null ? null : depDone ? "-" : r.Dep,
                        actDep = r.Dep == null ? null : actDep,
                        planTrack,
                        // 所定と違う番線になったときだけ入れる（所定がなければ、決まった番線をそのまま）
                        track = nowTrack is { } nt && nt != planTrack ? nt : (int?)null
                    };
                }).ToList();

                return new { train = key, label = dia.TypeLabel, source = "ダイヤ", delay = run?.Delay is { } dl && dl >= 60 ? (int)(dl / 60) : (int?)null, stops };
            }

            if (run == null || !run.HasPosition) return null;

            // ダイヤにない列車: 始発駅（ゲームの停車駅の最初、なければ今いる所）から、終着（ゲームの停車駅の最後、なければ路線の端）まで
            var type = RunType(run);
            var step = run.Dir.Step();
            var ids = _data.Line.StationIds;
            int ZoneOfName(string name) => _data.Line.StationIdByName(name) is { } id ? _data.Line.ZoneOfStation(id) : -1;
            var stopZones = run.Player.Stops.Select(s => (Zone: ZoneOfName(s.Name), s.StopType)).Where(x => x.Zone >= 0).ToList();
            var startZone = stopZones.Count > 0 ? stopZones[0].Zone : run.Front;
            var endZone = run.Dia?.TerminalId is { } tid ? _data.Line.ZoneOfStation(tid)
                : stopZones.Count > 0 ? stopZones[^1].Zone
                : step > 0 ? ids.Max(_data.Line.ZoneOfStation) : ids.Min(_data.Line.ZoneOfStation);
            // 打ち切りの駅があれば、そこを終着にする
            foreach (var id in ids.OrderBy(i => _data.Line.ZoneOfStation(i) * step))
            {
                var z0 = _data.Line.ZoneOfStation(id);
                if ((z0 - startZone) * step <= 0 || (z0 - endZone) * step > 0) continue;
                if (Override(id) == "打ち切り") { endZone = z0; break; }
            }

            var start = clock ?? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Japan).TimeOfDay;
            string Fmt(double sec) => TimeSpan.FromTicks((start + TimeSpan.FromSeconds(sec)).Ticks % TimeSpan.TicksPerDay).ToString(@"hh\:mm\:ss");

            var list = new List<object>();
            foreach (var id in ids.OrderBy(i => _data.Line.ZoneOfStation(i) * step))
            {
                var z = _data.Line.ZoneOfStation(id);
                if ((z - startZone) * step < 0 || (z - endZone) * step > 0) continue;
                var stopInfo = stopZones.FirstOrDefault(x => x.Zone == z);
                var stops = stopInfo.StopType is "StopForPassenger" or "StopForOperation" || z == startZone || z == endZone;
                var mark = stopInfo.StopType == "StopForOperation" ? "運停" : stops ? "停" : "通";
                switch (Override(id))
                {
                    case "停車" when !stops: (stops, mark) = (true, "臨停"); break;
                    case "通過" when stops && z != startZone && z != endZone: (stops, mark) = (false, "臨通"); break;
                    case "打ち切り": mark = "打切"; break;
                }
                if (z == startZone)
                {
                    list.Add(new { name = _data.Line.StationName(id), control = Control(id), stop = mark, planArr = (string?)null, actArr = (string?)null, planDep = (string?)Fmt(0), actDep = (string?)null });
                    continue;
                }

                var sec = _model.SecondsTo(type, run.Dir, startZone, 0, id);
                if (sec == null) continue;
                var dwell = stops && z != endZone ? Math.Max(_model.DwellAt(type, run.Dir, id), _data.DefaultDwellSeconds) : 0;
                list.Add(new
                {
                    name = _data.Line.StationName(id), control = Control(id), stop = mark,
                    planArr = stops ? Fmt(sec.Value) : null,
                    actArr = (string?)null,
                    planDep = z == endZone ? null : Fmt(sec.Value + dwell),
                    actDep = (string?)null
                });
            }

            return new { train = key, label = run.Type, source = clock != null ? "標準運転時分（ロープレ時刻から）" : "標準運転時分（今の時刻から）", stops = list };
        }
    }

    /// <summary>
    /// 要求のあとで、ゲームがその進路を受け付けた返事をしたか（OK・同一進路への進入）。表示のためだけに使う。
    /// ゲームの進路の一覧に出るまでのつなぎなので、要求から StuckDelay を過ぎても一覧に出なければ、開通とはしない
    /// （OK と返しても、ゲームに残った競合する進路で開通しないことがある。そのまま開通と出すと、サーバーでは開通、ゲームでは停止に見える）。
    /// </summary>
    private static bool Acknowledged(TrainRun run, RouteReq q)
    {
        var key = $"{q.Def.Interlock}/{q.Def.Name}";
        return DateTime.UtcNow - q.RequestedUtc <= StuckDelay
               && run.ResultAt.TryGetValue(key, out var at) && at >= q.RequestedUtc
               && run.Results.TryGetValue(key, out var reply) && (reply == "OK" || reply.Contains("同一進路"));
    }

    public object AdminView()
    {
        lock (_lock)
        {
            return _runs.Values.OrderBy(r => r.Train).Select(r => new
            {
                train = r.Train,
                type = r.Type,
                direction = r.Dir.Label(),
                dia = r.Dia != null,
                unyo = r.Dia != null ? _data.Turns.UnyoOf(r.Dia) : null,
                turn = DiaMode && r.Dia != null ? _data.Turns.View(r.Dia) : null, // 後運用（差し替えの候補つき）
                position = r.HasPosition ? _data.Line.DescribeZones(r.Player.Zones) : "不明",
                // ゲームに今ある進路（手動進路の取消用）
                gameRoutes = r.Player.GameRoutes.Select(g => new { interlock = g.Item1, route = g.Item2 }),
                stations = r.Stations.Where(s => !s.Done).Select(s => new
                {
                    stationId = s.St.Id,
                    name = s.St.Name,
                    stopType = s.StopType,
                    stopOverride = _central.StopOverride(r.Train, s.St.Id),
                    track = s.Track,
                    options = s.Options,
                    source = s.TrackSource,
                    locked = s.Locked,
                    held = _central.IsHeld(r.Train, s.St.Id),
                    holdArrive = (_central.Hold(r.Train, s.St.Id) & HoldKind.Arrive) != 0,
                    holdDepart = (_central.Hold(r.Train, s.St.Id) & HoldKind.Depart) != 0,
                    manual = ManualOptions(r, s).Select(o => new { action = o.Action, label = o.Label }).ToList(),
                    forced = _central.IsForced(r.Train, s.St.Id),
                    status = Status(r, s)
                })
            }).ToList();
        }
    }
}
