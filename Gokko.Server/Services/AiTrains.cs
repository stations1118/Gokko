using System.Globalization;
using System.Text;
using System.Text.Json;
using Gokko.Common;

namespace Gokko.Server.Services;

/// <summary>
/// にぎやかしの列車（サーバーだけで走る列車）。ゲームを持たない参加者として <see cref="PlayerRegistry"/> に載せる。
///
/// ・進路は、プレイヤーの列車と同じく <see cref="RouteEngine"/> が決める。決まった進路は、すぐ開通したものとして扱う
///   （本物のゲームの代わり。開通の判断は RouteEngine が在線・ほかの進路を見て済ませている）。開けた進路は、ほかのプレイヤーのゲームにも写る
/// ・在線は、ほかのプレイヤーへの信号（SignalCalculator の R）・TID・信号制御盤に、ふつうの列車として出る
/// ・自分の信号は、プレイヤーに配るのと同じもの（<see cref="StateBroadcaster"/> の ClientState）を受け取る。
///   閉塞信号機は R でなければ進む。駅の信号機は、自分の進路が開いていて R でなければ進む
/// ・走る道筋は、信号機の「次の信号機」（Signals.json の next）と進路の軌道回路（Interlockings.json）をつないで作る
/// ・距離は Data/AiTrains.json（ゲームの自動運転で走ったときの信号機の位置・番線ごとの停止位置・軌道回路の長さの見込み）。
///   ない所は、軌道回路の長さを決め打ちにする
/// ・速さは、次の停車駅の時刻に間に合うように決める（残り距離 ÷ 残り時分 × 1.1）。上限は <see cref="LimitCap"/>（区間の最高・線路の制限・分岐 40km/h）
///
/// 決まり（2026-11-20 ユーザー。使うのは基本的にダイヤ運転モード）
/// ・自動進路構成（RouteEngine）とは切り離す。自動進路構成は、列車番号ごとに決められたタイミングで決められたように進路・信号を出すだけで、
///   にぎやかしのために変えない。にぎやかしは、時刻・司令員の指示・信号に従って運転するだけ（番線・順序・抑止は、信号と進路に表れる）
/// ・自分で番線を選ぶのは出すときだけ。ダイヤ運転モードではダイヤの番線にだけ出し、空いていなければ待つ。時刻を過ぎた駅には出さない（順序を崩さない）
/// ・ユーザーの列車（出勤した行路の列車・入力された列番）は出さない。ユーザーがその列番で出てきたら、途中駅からでも消してユーザーに運転させる
/// </summary>
public sealed class AiTrains : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);
    private const double Accel = 0.65;          // m/s²（約 2.3km/h/s）
    private const double Decel = 0.70;          // m/s²（約 2.5km/h/s）
    private const double ProtectionDecel = 1.1; // m/s²（防護受信時、約4 km/h/s）
    private const double SignalMargin = 8;      // R の信号機の何 m 手前で止まるか
    private const double CarLength = 20;
    private const double DefaultStationCircuit = 150;
    private const double DefaultLineCircuit = 450;
    private const int MinDwellSeconds = 15;
    private const double ShuntSpeed = 25 / 3.6; // 入換の速さ

    // ---- 三郷（ゲームの線区の新大路方の端。上りは1番線、下りは2番線） ----
    /// <summary>上りがここに入ったら、ExitSeconds（ロープレ時刻の秒）で消す: 13RT（1番線の先、新大路方の出口）。</summary>
    private static readonly HashSet<string> ExitCircuits = new() { "TH57_13RT" };
    private const double ExitSeconds = 20;
    /// <summary>三郷を通過する下り（新大路方から入ってくる）は、通過時刻の EntryLeadSeconds 前に 2番線に出す。</summary>
    private const string EntryStation = "TH57";
    private const double EntryLeadSeconds = 20;
    private static readonly TimeSpan TerminalLinger = TimeSpan.FromSeconds(60);
    /// <summary>ダイヤ運転中、継続乗務の在線がこれだけ残ったら、自動運転の列車として引き継ぐ。</summary>
    private static readonly TimeSpan GhostTakeOverAfter = TimeSpan.FromMinutes(3);

    private readonly RailData _rail;
    private readonly PlayerRegistry _players;
    private readonly GameClock _clock;
    private readonly RouteEngine _routes;
    private readonly CentralControl _central;
    private readonly IncidentManager _log;
    private readonly CrewRoster _crew;
    private readonly DutyRosters _duties;
    private readonly CrewRelay _relay;
    private readonly ProtectionRadio _protectionRadio;
    private readonly DispatcherProtection _dispatcherProtection;
    private readonly ILogger<AiTrains>? _logger;

    private readonly Dictionary<string, HashSet<string>> _adj;
    private readonly Dictionary<string, List<RouteDef>> _routesBySignal;
    private readonly List<Dictionary<string, double>> _signalRuns = new();
    private readonly List<List<(double Pos, double Kmh)>> _limitRuns = new(); // _signalRuns と同じ並び。制限速度が変わった地点
    private readonly List<Dictionary<string, int>> _trackRuns = new(); // _signalRuns と同じ並び。その走りで止まった駅の番線
    private readonly Dictionary<(string, string), double?> _chainCache = new(); // ChainDistance の答え
    private Dictionary<string, List<(string To, double D)>>? _chainEdges; // ChainDistance の道（記録の隣どうしの信号機）
    private readonly List<StopSpec> _stops = new();
    private readonly Dictionary<string, double> _circuitLengths = new();
    private readonly UnyoCars _unyoCars; // 運行番号ごとの両数（Data/UnyoCars.csv。ない運行は6両）
    private readonly StopMarkers _markers; // 停止位置目標（両数に合った停目に止める。Data/StopMarkers.*.csv）
    private Dictionary<string, Dictionary<string, string>> _couplePartners = new(); // 連結: 列車ID → 駅ID → 番線で待つ増結車（RouteEngine から周期ごとに写す）
    private static readonly TimeSpan CoupleStopB = TimeSpan.FromSeconds(5); // 連結: 停B で止まっている時間
    private readonly List<(string Child, int Cars, string Station, int Track, string Parent)> _pendingSplits = new(); // 解放: これから出す車両
    private readonly List<((string Child, int Cars, string Station, int Track, string Parent) Split, string ParentId, string Circuit)> _splitsAfterLeave = new(); // 解放: 元の列車が番線から出るのを待つ車両

    private readonly object _lock = new();
    private readonly Dictionary<string, Train> _trains = new();
    private int _seq;

    // ---- 自動運転列車アリ/ナシ（アリなら、ダイヤの列車を時刻に合わせて自動で出す） ----
    private bool _enabled;
    private readonly HashSet<string> _errorLogged = new(); // 例外を記録した列車（同じ列車で何度も書かない）
    private readonly HashSet<string> _spawned = new(); // 自動で出した・続けて走った列番（同じ列車を二度出さない）
    private HashSet<string> _userTrains = new();       // ユーザーの列車（出勤した行路の列車・入力した列番）
    private HashSet<string> _userInGame = new();       // 運転画面に出ているユーザーの列番
    private DateTime _lastSpawnUtc;
    private TimeSpan? _lastGame;
    /// <summary>
    /// 途中の駅から出してよい列車の目安のロープレ時刻（自動運転列車をアリにした・ロープレ時刻を飛ばしたときの時刻）。
    /// このとき、もう始発駅を出ているはずの列車だけ、このとき走っているはずの所から出す。それ以外の列車は始発駅にだけ出す
    /// </summary>
    private TimeSpan? _catchUpAt;
    private bool _catchUpNext = true; // 次に AutoSpawn をまわすときに _catchUpAt を決める
    /// <summary>始発駅で出せないまま、発車時刻をこれだけ過ぎたら出すのをやめる。</summary>
    private static readonly TimeSpan SpawnLateLimit = TimeSpan.FromMinutes(10);
    /// <summary>出現前の列車として、ダイヤグラムに出す先の長さ（始発駅の発車時刻まで）。</summary>
    private static readonly TimeSpan PendingHorizon = TimeSpan.FromMinutes(60);
    private readonly Dictionary<string, TimeSpan> _heldSeen = new(); // 出現前に抑止されていた列番 → 最後に抑止を見たロープレ時刻
    private volatile IReadOnlyList<PendingTrain> _pending = [];

    /// <summary>
    /// 出現前の列車（これから始発駅に自動で出す列車）。Station・Track は出す駅と番線、Due は出すときの発車時刻（ロープレ時刻。
    /// ダイヤグラムの◆で発車時刻を決めていれば、その時刻）、Held は抑止中で出さない、Late は時刻を過ぎても出せていない
    /// </summary>
    public sealed record PendingTrain(string Number, string Station, int? Track, bool TrackFixed, TimeSpan Due, bool Held, bool Late);

    /// <summary>出現前の列車（AutoSpawn が2秒ごとに作り直す。ロックを取らずに読める）。</summary>
    public IReadOnlyList<PendingTrain> Pending => _pending;

    /// <summary>実際の時刻（UTC）を、そのときのロープレ時刻にする（now は今のロープレ時刻）。</summary>
    private TimeSpan GameAt(DateTime utc, TimeSpan now) =>
        now + TimeSpan.FromTicks((long)((utc - DateTime.UtcNow).Ticks * (_clock.Speed > 0 ? _clock.Speed : 1)));
    private readonly HashSet<string> _takeOverLogged = new(); // 引き継げなかったことを記録した継続乗務の在線（同じものを何度も書かない）
    private const long StatusLogMaxBytesPerDay = 20L * 1024 * 1024; // 状態の記録の1日の上限
    private readonly DailyLog _statusFile = new("ai", StatusLogMaxBytesPerDay);

    public AiTrains(string dataDir, RailData rail, PlayerRegistry players, GameClock clock, RouteEngine routes, CentralControl central,
        IncidentManager log, CrewRoster crew, DutyRosters duties, CrewRelay relay, ProtectionRadio protectionRadio,
        DispatcherProtection dispatcherProtection, StopMarkers markers, ILogger<AiTrains>? logger = null)
    {
        _crew = crew;
        _duties = duties;
        _relay = relay;
        _protectionRadio = protectionRadio;
        _dispatcherProtection = dispatcherProtection;
        _markers = markers;
        _rail = rail;
        _players = players;
        _clock = clock;
        _routes = routes;
        _central = central;
        _log = log;
        _logger = logger;
        _adj = LoadAdjacency(Path.Combine(dataDir, "TrackCircuits.json"));
        _routesBySignal = rail.Routes.Values.Where(r => r.Signal.Length > 0).GroupBy(r => r.Signal).ToDictionary(g => g.Key, g => g.ToList());
        LoadDistances(Path.Combine(dataDir, "AiTrains.json"));
        _unyoCars = UnyoCars.Load(dataDir, rail.BaseDia.Values.Select(d => d.Unyo).OfType<string>().ToHashSet());
        foreach (var p in _unyoCars.Problems) { _logger?.LogWarning("両数の表: {Problem}", p); _log.Note($"両数の表: {p}"); }
        players.VirtualTrains = Snapshot;
        routes.PendingTrains = () => _pending;
        players.GhostDeleted += OnGhostDeleted;
    }

    /// <summary>特発臨（司令員がその場で立てる臨時列車）。元の列車が発駅に止まったら、特発臨の列番に変える。</summary>
    public TokuhatsuRinji? Rinji { get; set; }

    /// <summary>走っている自動運転の列車の両数（特発臨に引き継ぐ）。走っていなければ null。</summary>
    public int? CarsNow(string number)
    {
        var key = RailData.NormalizeTrain(number);
        lock (_lock) return _trains.Values.FirstOrDefault(t => t.Number == key)?.Cars;
    }

    // =========================================================================================
    // 出す・消す・一覧（管理画面から）
    // =========================================================================================

    public sealed record View(string Id, string Train, int Cars, string Where, double SpeedKmh, string Status, bool Auto);

    /// <summary>自動運転列車アリか（ダイヤの列車を時刻に合わせて自動で出す）。</summary>
    public bool Enabled
    {
        get { lock (_lock) return _enabled; }
    }

    /// <summary>自動運転列車のアリ/ナシ。ナシにしたら、走っている列車も全部消す。</summary>
    public void SetEnabled(bool on)
    {
        lock (_lock)
        {
            if (_enabled == on) return;
            _enabled = on;
            _spawned.Clear();
            _catchUpNext = on;
            if (!on) _trains.Clear();
        }

        _log.Note(on ? "にぎやかし: 自動運転列車アリにしました（ダイヤの列車を時刻に合わせて出します）" : "にぎやかし: 自動運転列車ナシにしました（走っていた列車は消しました）");
    }

    /// <summary>試運転の運行番号ごとの列車（列番・最初の駅と発車時刻・最後の駅と着時刻）。司令用管理画面などのプルダウンの項目。</summary>
    public sealed record TrialView(string Unyo, List<string> Trains, string From, string? Dep, string To, string? Arr);

    public List<TrialView> Trials()
    {
        var list = new List<TrialView>();
        foreach (var unyo in _rail.TrialUnyos())
        {
            var trains = _rail.Dia.Values.Where(d => RailData.IsTrial(d) && d.Unyo == unyo && d.Stops.Count > 0)
                .OrderBy(d => DayTime(d.Stops[0].Dep ?? d.Stops[0].Arr) ?? TimeSpan.MaxValue).ToList();
            if (trains.Count == 0) continue;
            var (first, last) = (trains[0].Stops[0], trains[^1].Stops[^1]);
            list.Add(new TrialView(unyo, trains.Select(d => d.Number).ToList(), first.Name, Hm(first.Dep ?? first.Arr), last.Name, Hm(last.Arr ?? last.Dep)));
        }
        return list;

        static TimeSpan? DayTime(string? hms) => Time(hms) is { } t && t < TimeSpan.FromHours(4) ? t + TimeSpan.FromDays(1) : Time(hms);
        static string? Hm(string? hms) => hms is { Length: >= 5 } ? hms[..5] : hms;
    }

    /// <summary>手で出せる列車の候補（列番・種別・出す駅と発車時刻・行き先・両数・自動で出る予定か）。</summary>
    public sealed record CandidateView(string Train, string Type, string Station, string Dep, string Dest, int Cars, bool Pending);

    /// <summary>
    /// 手で出せる列車の候補: 走っていないダイヤの列車で、これから発車する駅（手で出すときに列番だけで選ばれる駅）の時刻が
    /// 今から PendingHorizon までのもの。発車の早い順。ユーザーの列車・走っている列車が続けて走る列番・特発臨・走らせない試運転は入れない。
    /// </summary>
    public List<CandidateView> Candidates()
    {
        var list = new List<(TimeSpan Dep, CandidateView View)>();
        if (_clock.Now is not { } now) return [];
        var users = new HashSet<string>(_userTrains);
        users.UnionWith(_userInGame);
        HashSet<string> running, covered, pending;
        lock (_lock)
        {
            running = _trains.Values.Select(t => t.Number).ToHashSet();
            covered = new HashSet<string>();
            foreach (var t in _trains.Values) CoverChain(t.Dia, covered);
            pending = _pending.Select(p => p.Number).ToHashSet();
        }

        foreach (var dia in _rail.Dia.Values)
        {
            var number = RailData.NormalizeTrain(dia.Number);
            if (running.Contains(number) || users.Contains(number) || covered.Contains(number) || number.StartsWith("解")) continue;
            if (Rinji?.IsConversion(number) == true || _rail.IsIdleTrial(dia)) continue; // 列番を変えて走る特発臨は出さない（出庫の特発臨は出す）
            if (_routes.DirOfTrain(number) is not { } dir) continue;
            // 出す駅は Add で駅を指定しなかったときと同じ選び方
            var stops = dia.Stops.Where(s => s.StationId != null && _rail.Stations.TryGetValue(s.StationId, out var sd) && sd.RouteControl).ToList();
            var start = stops.Take(Math.Max(0, stops.Count - 1))
                .FirstOrDefault(s => Time(s.Dep ?? s.Arr) is { } t && t >= now && !PassesWithoutTrack(s, dir));
            if (start == null || Time(start.Dep ?? start.Arr) is not { } dep || dep > now + PendingHorizon) continue;
            var hms = start.Dep ?? start.Arr ?? "";
            list.Add((dep, new CandidateView(number, dia.TypeLabel, start.Name, hms.Length >= 5 ? hms[..5] : hms,
                dia.Dest.Length > 0 ? dia.Dest : dia.Stops[^1].Name, CarsOf(number), pending.Contains(number))));
        }

        return list.OrderBy(c => c.Dep).ThenBy(c => c.View.Train, StringComparer.Ordinal).Select(c => c.View).ToList();
    }

    /// <summary>走っている列車 d が、このあと続けて・折り返して走る列番（と切り離す車両）を covered に足す。</summary>
    private void CoverChain(DiaTrain? d, HashSet<string> covered)
    {
        for (var k = 0; k < 10 && d != null; k++)
        {
            if (_rail.DiaOf("解" + d.Number) is { } split && covered.Add(split.Number)) CoverChain(split, covered);
            var nextNo = d.Next?.Number ?? (_rail.Turns.TurnOf(d) is { Kind: "折返", Number: { Length: > 0 } tn } ? tn : null);
            if (nextNo == null) break;
            covered.Add(RailData.NormalizeTrain(nextNo));
            d = _rail.DiaOf(nextNo);
        }
    }

    /// <summary>
    /// 試運転を走らせる運行番号を決める（空なら1本も出さない）。外した運行番号の試運転が自動運転で走っていれば消す。
    /// ユーザーが運転している試運転はそのまま（ユーザーの列車は自動運転で出さない、の決まりは変えない）。
    /// </summary>
    public string SetTrials(IEnumerable<string> unyos)
    {
        _rail.SetTrialsOn(unyos);
        var on = _rail.TrialsOn.Order(Gokko.Common.Unyo.Comparer).ToList();
        List<string> removed = new();
        lock (_lock)
        {
            foreach (var t in _trains.Values.Where(t => RailData.IsTrial(t.Dia) && _rail.IsIdleTrial(t.Dia)).ToList())
            {
                _trains.Remove(t.Id);
                removed.Add(t.Shown ?? t.Number);
            }
            // 選び直したら、時刻の範囲の中ならもう一度出せるように
            _spawned.RemoveWhere(n => n.StartsWith("試"));
        }

        var message = on.Count == 0 ? "試運転: なし（1本も出しません）" : $"試運転: 運行 {string.Join("・", on)} を走らせます";
        if (removed.Count > 0) message += $"（走っていた {string.Join("・", removed)} は消しました）";
        _log.Note(message);
        return message;
    }

    public List<View> List()
    {
        lock (_lock)
            return _trains.Values.OrderBy(t => t.Seq).Select(t => new View(t.Id, t.Shown ?? t.Number, t.Cars,
                _rail.Line.DescribeZones(t.Info.Zones) ?? string.Join(" ", t.Info.TrackCircuits), Math.Round(t.Speed * 3.6, 1), t.Status, t.Auto)).ToList();
    }

    /// <summary>ダイヤの列車を、これから発車する最初の駅（時刻が来ていない、進路を扱う駅）に出す。</summary>
    public (bool Ok, string Message) Add(string number, int cars, string? stationName) => Add(number, cars, stationName, false);

    private (bool Ok, string Message) Add(string number, int cars, string? stationName, bool auto, int? forceTrack = null, PlayerInfo? takesOver = null)
    {
        number = RailData.NormalizeTrain(number);
        if (!auto && UserTrains(_players.SnapshotWithoutVirtualTrains()).Contains(number)) return (false, $"{number} はユーザーの列車です（出勤した行路の列車・入力された列番）");
        if (_rail.DiaOf(number) is not { } dia) return (false, $"{number}: ダイヤにない列車です");
        if (!auto && takesOver == null && _rail.IsIdleTrial(dia)) return (false, $"{number} は走らせない試運転です（試運転のプルダウンで運行 {dia.Unyo} を選んでください）");
        var dir = _routes.DirOfTrain(number);
        if (dir == null) return (false, $"{number}: 列車番号の形が違います");
        cars = Math.Clamp(cars <= 0 ? CarsOf(number) : cars, 1, 10);
        var now = _clock.Now;
        // 自動進路構成が要求している進路の回路（ゲームにはまだ開いていない進路も。RouteEngine のロックを取るので、自分のロックの外で集める）。
        // 引き継ぐ在線・切り離した車両は、元の列車の進路の上に出るので見ない
        var reserved = takesOver == null && !number.StartsWith("解") ? _routes.ReservedCircuits() : new HashSet<string>();

        lock (_lock)
        {
            if (_trains.Values.Any(t => t.Number == number)) return (false, $"{number} はもう走っています");
            if (_players.Snapshot().Any(p => RailData.NormalizeTrain(p.TrainNumber) == number && !p.ConnectionId.StartsWith(PlayerRegistry.AiPrefix)))
                return (false, $"{number} はプレイヤーが運転しています");

            // 出す駅: 指定があればその駅、なければ時刻がまだ来ていない最初の駅（進路を扱う駅で、番線が分かる所）
            var stops = dia.Stops.Where(s => s.StationId != null && _rail.Stations.TryGetValue(s.StationId, out var sd) && sd.RouteControl).ToList();
            DiaStop? start = null;
            foreach (var s in stops)
            {
                if (stationName is { Length: > 0 } && s.Name != stationName) continue;
                if (stationName is not { Length: > 0 } && now is { } n && Time(s.Dep ?? s.Arr) is { } t && t < n) continue;
                if (s == stops.LastOrDefault() && stationName is not { Length: > 0 }) continue; // 終着には出さない
                if (stationName is not { Length: > 0 } && PassesWithoutTrack(s, dir.Value)) continue;
                start = s;
                break;
            }

            if (start == null) return (false, stationName is { Length: > 0 } ? $"{number} は {stationName} を通りません" : $"{number}: これから発車する駅がありません（ロープレ時刻を確かめてください）");
            var st = _rail.Stations[start.StationId!];
            // 番線: ダイヤの番線。ほかの列車がいる・ほかの列車の進路がかかっていれば、同じ向きに出られるほかの番線
            // （ほかの列車の進路の上に出すと、その列車も自分も動けなくなる。RouteEngine も「出現待ち」にする）
            // 切り離した車両（解2076C）は、元の列車（2076C）がいる番線に出す
            var others = _players.Snapshot().Where(p => !(number.StartsWith("解") && RailData.NormalizeTrain(p.TrainNumber) == number[1..])).ToList();
            var occupied = others.SelectMany(p => p.TrackCircuits).ToHashSet();
            foreach (var p in others)
            foreach (var key in p.GameRoutes)
                if (_rail.Routes.TryGetValue(key, out var rd)) occupied.UnionWith(rd.Circuits);
            // 遅れて入ってくる列車の場内のように、要求したがまだゲームに開いていない進路の番線にも出さない
            // （出すと出現待ちになり、入ってくる列車が番線に入れない）
            occupied.UnionWith(reserved);
            var options = new List<int>();
            if (forceTrack is { } ft) options.Add(ft);
            else if (start.Track is { } dt) options.Add(dt);
            // ダイヤ運転モードでは、ダイヤの番線だけ（番線を変えない。空くまで待つ）。番線の指定があれば、その番線だけ
            if (forceTrack == null && (!_central.DiaMode || options.Count == 0)) options.AddRange(st.Routes[dir.Value].Depart.Keys.Order().Where(k => !options.Contains(k)));
            if (options.Count == 0) options.AddRange(st.Routes[dir.Value].Arrive.Keys.Order());
            var track = options.FirstOrDefault(k => st.TrackCircuit.TryGetValue(k, out var c) && !occupied.Contains(c), -1);
            if (track < 0) return (false, $"{st.Name}: 出せる番線がありません（ほかの列車・進路があります）");
            if (!TrackFitsDir(st, dir.Value, track)) return (false, $"{st.Name} {track}番線は{dir.Value.Label()}の列車が使わない番線です");
            var platform = st.TrackCircuit[track];

            var train = new Train
            {
                Id = $"{PlayerRegistry.AiPrefix}{++_seq}", Seq = _seq, Number = number, Dia = dia, Dir = dir.Value, Cars = cars,
                Length = cars * CarLength, StartStation = st.Name, Auto = auto
            };
            // 停車・通過の一覧（出す駅から先。出す駅は、通過の駅でも止まって発車時刻を待つ）
            var from = dia.Stops.IndexOf(start);
            foreach (var s in dia.Stops.Skip(from))
            {
                if (s.StationId == null || !InArea(s.StationId)) continue;
                train.Plan.Add(s == start ? new PlanStop(s.Name, s.StationId, true, s.Track, Time(s.Arr), Time(s.Dep), dia, s) : PlanOf(dia, s));
            }

            // 最初の道筋: 出す番線の軌道回路。停止位置は、番線ごとの表（出発信号機の手前）から
            // 表の駅名はダイヤの駅名（江原検など、駅の設定の名前と違う所がある）。なければ駅の設定の名前
            var stopSpec = FindStop(start.Name, dir.Value, track) ?? FindStop(st.Name, dir.Value, track);
            // 両数に合った停目に出す（表の位置は6両停目）
            var before = (stopSpec?.Before ?? 20) + MarkerOffset(train, start.Name, dir.Value, track, train.Plan.Count <= 1);
            var len = Math.Max(LengthOf(platform), train.Length + before + 10);
            train.Path.Add(new Seg(null, null, 0, len, [new SegCircuit(platform, 0, len)]));
            train.Head = len - before;
            train.StartHead = train.Head;
            train.StartTrack = track;
            train.Info = new PlayerInfo
            {
                ConnectionId = train.Id, Mode = ShareMode.Shared, InGame = true, TrainCrewConnected = true, TrainNumber = number,
                TrainNumberTyped = true, JoinedUtc = DateTime.UtcNow, LastReportUtc = DateTime.UtcNow, TakesOver = takesOver?.ConnectionId,
                // 引き継いだ在線の進路（ゲームに開通している進路）も引き継ぐ。空のまま始めると、RouteEngine が「ゲームで解除された」と見て、その進路を捨てる
                GameRoutes = takesOver != null ? new HashSet<(string, string)>(takesOver.GameRoutes) : new HashSet<(string, string)>(),
                Stops = train.Plan.Select(p => new StopInfo { Name = p.Name, StopType = p.Stops ? "StopForPassenger" : "Passing" }).ToList()
            };
            train.Status = $"{st.Name} {track}番線で発車待ち";
            UpdateInfo(train);
            _trains[train.Id] = train;
            _spawned.Add(number);
            _log.Note($"にぎやかし: {number}（{cars}両）を {st.Name} {track}番線に出しました" + (takesOver != null ? "（継続乗務の在線を引き継ぎ）" : auto ? "（自動）" : ""));
            return (true, $"{number} を {st.Name} {track}番線に出しました");
        }
    }

    public bool Remove(string id)
    {
        Train? t;
        lock (_lock)
            if (!_trains.Remove(id, out t)) return false;
        _log.Note($"にぎやかし: {t.Number} を消しました");
        return true;
    }

    /// <summary>ダイヤから外した列番（臨時列車の取消）の列車を消す。また出せるように、出した印も外す。</summary>
    public void RemoveNumbers(IReadOnlyCollection<string> numbers)
    {
        var set = numbers.Select(RailData.NormalizeTrain).ToHashSet();
        List<string> gone;
        lock (_lock)
        {
            gone = _trains.Where(x => set.Contains(x.Value.Number)).Select(x => x.Key).ToList();
            foreach (var id in gone) _trains.Remove(id);
            _spawned.ExceptWith(set);
        }
        if (gone.Count > 0) _log.Note($"にぎやかし: 臨時列車の取消で {gone.Count}本を消しました");
    }

    public void RemoveAll()
    {
        lock (_lock) _trains.Clear();
    }

    private IEnumerable<PlayerInfo> Snapshot()
    {
        lock (_lock) return _trains.Values.Select(t => t.Info.Clone()).ToList();
    }

    // =========================================================================================
    // サーバーからの「配信」（プレイヤーのクライアントが受け取るものと同じ）
    // =========================================================================================

    public void Receive(string id, ClientState state, string? planDetail)
    {
        // 信号制御盤で開通させている進路（プレイヤーには OtherRoutes で写して届く。自動運転列車は OtherRoutes を受け取らないので、ここで入れる）。
        // 盤のロックを取るので、自分のロックの外で読む
        var panel = _central.PanelRoutes().Select(r => (r.Interlocking, r.RouteName)).ToHashSet();
        lock (_lock)
        {
            if (!_trains.TryGetValue(id, out var t)) return;
            t.Received = true;
            t.RedSignals = state.Signals.Where(s => s.Phase == "R").Select(s => s.Name).ToHashSet();
            foreach (var r in state.Routes) t.Info.GameRoutes.Add((r.Interlocking, r.RouteName));
            SyncPanelRoutes(t, panel);
            foreach (var r in state.DeleteRoutes)
            {
                // 入ってしまった進路は消さない（ゲームの接近鎖錠の代わり）。まだ入っていなければ、道筋もそこまでに縮める
                var i = t.Path.FindIndex(s => s.Route is { } d && d.Interlock == r.Interlocking && d.Name == r.RouteName);
                if (i >= 0 && t.Path[i].Start <= t.Head) continue;
                // 接近の区間にいる進路も消さない（ゲームなら接近鎖錠で断られる）。駒野の第二出発2L は、手前の 6LT が駒野〜館浜の区間の回路なので、
                // 最後尾が 9LCT を出ると自動進路構成は駒野を抜けたとして消しにくるが、列車はまだ 6LT を走っていて、これから 2L に入る
                // 駅で停車中なら消す（ホームで出発を待つ列車は、出発の進路の接近の区間にいる。自動進路構成が先行列車に譲らせた進路を
                // 持ち続けると、先行列車がその進路を取れない）
                if (i >= 0 && !t.Dwelling && t.Path[i].Route is { Approach.Count: > 0 } ap && t.Info.TrackCircuits.Any(ap.Approach.Contains)) continue;
                if (i > 0) t.Path.RemoveRange(i, t.Path.Count - i);                t.Info.GameRoutes.Remove((r.Interlocking, r.RouteName));
            }
            t.CanRequestDeparture = state.CanRequestDeparture;
            t.RouteMessage = state.RouteMessage;
            // この先の駅の状態（進路待ちの理由など。プレイヤーには出さない詳しい状態）。止まっているときの理由に出す
            t.PlanDetail = planDetail;
        }
    }

    /// <summary>
    /// 信号制御盤の進路を、自動運転列車の「自分のゲームの進路」に写す。盤の一覧から外れたもの（盤で取り消した・ほかの列車が入った）は消す。
    /// 列車がもう入った進路は消さない（盤の一覧からは、列車が入ると外れる）。
    /// </summary>
    private static void SyncPanelRoutes(Train t, HashSet<(string, string)> panel)
    {
        foreach (var key in panel)
            if (t.Info.GameRoutes.Add(key)) t.PanelRoutes.Add(key);
        foreach (var key in t.PanelRoutes.Where(k => !panel.Contains(k)).ToList())
        {
            t.PanelRoutes.Remove(key);
            var i = t.Path.FindIndex(s => s.Route is { } d && (d.Interlock, d.Name) == key);
            if (i >= 0 && t.Path[i].Start <= t.Head) continue;
            if (i > 0) t.Path.RemoveRange(i, t.Path.Count - i);
            t.Info.GameRoutes.Remove(key);
        }
    }

    // =========================================================================================
    // 周期の処理
    // =========================================================================================

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        HashSet<(int First, int Last)> activeProtection = new();
        var last = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var now = DateTime.UtcNow;
            var dt = Math.Clamp((now - last).TotalSeconds, 0, 1);
            last = now;
            try
            {
                // ユーザーの列車（RouteEngine などのロックの中から呼ばないように、自分のロックの外で集める）
                // 自動運転の列車（自分の列車）は要らないので、複製しない
                var snapshot = _players.SnapshotWithoutVirtualTrains();
                var users = UserTrains(snapshot);
                var inGame = snapshot.Where(p => !PlayerRegistry.IsVirtual(p.ConnectionId) && p.InGame && p.CountedTrainNumber.Length > 0)
                    .Select(p => RailData.NormalizeTrain(p.CountedTrainNumber)).ToHashSet(); // 入換で動かしている人は、残した在線の列番

                // 防護無線（運転士の発報・司令の区間防護）の範囲。自分のロックの外で集める
                activeProtection.Clear();
                _protectionRadio.CopyActiveRangesTo(activeProtection);
                _dispatcherProtection.CopyActiveRangesTo(activeProtection);

                List<string> departures = new();
                List<string>? statusLines = null;
                var couplePartners = _routes.CouplePartners();
                var coupled = _routes.TakeCoupled();
                var splits = _routes.TakeSplits();
                List<(string Child, int Cars, string Station, int Track, string Parent)> splitSpawns = new();
                lock (_lock)
                {
                    _userTrains = users;
                    _userInGame = inGame;
                    _couplePartners = couplePartners;
                    // ユーザーの列車が連結を終えたら、番線で待っていた増結車（自動運転）を消す
                    foreach (var (partner, train) in coupled)
                        foreach (var p in _trains.Values.Where(x => x.Number == partner).ToList())
                        {
                            _trains.Remove(p.Id);
                            _log.Note($"にぎやかし: {partner} は {train} に連結したので消しました");
                        }
                    // 解放: 元の列車（自動運転）の両数を減らし、切り離した車両を同じ番線に出す（ユーザーが運転するなら出さない）
                    foreach (var (child, parent, station, track) in splits)
                    {
                        var childCars = CarsOf(child);
                        foreach (var p in _trains.Values.Where(x => x.Number == parent))
                        {
                            p.Cars = p.Dia.Cars != null ? Math.Max(1, p.Cars - childCars) : Math.Max(CarsOf(parent), p.Cars - childCars);
                            p.Length = p.Cars * CarLength;
                        }
                        if (users.Contains(RailData.NormalizeTrain(child)) || _trains.Values.Any(x => x.Number == child)) continue;
                        splitSpawns.Add((child, childCars, station, track, parent));
                    }
                    foreach (var t in _trains.Values.ToList())
                    {
                        if (!_trains.ContainsKey(t.Id)) continue; // この周期の中で消えた（連結した増結車）
                        // ユーザーがその列番で出てきたら、自動運転の列車は消して、ユーザーに運転してもらう（途中駅からでも）
                        // 上りが三郷の 13RT（新大路方の出口）に入ったら、20秒で消す（その先はゲームの線区の外）
                        var protectedStop = ShouldStopForProtection(t, activeProtection);
                        if (protectedStop) t.ExitUtc = null;
                        else if (t.Dir == Dir.Up && t.ExitUtc == null && t.Info.TrackCircuits.Any(ExitCircuits.Contains))
                            t.ExitUtc = now + TimeSpan.FromSeconds(ExitSeconds / Math.Max(0.1, _clock.Speed));
                        if (t.ExitUtc is { } ex && now >= ex)
                        {
                            _trains.Remove(t.Id);
                            _log.Note($"にぎやかし: {t.Number} は三郷から線区の外へ出たので消しました");
                            continue;
                        }

                        if (inGame.Contains(t.Number))
                        {
                            _trains.Remove(t.Id);
                            _log.Note($"にぎやかし: {t.Number} はユーザーが運転を始めたので消しました");
                            continue;
                        }

                        // 1本の列車で例外が出ても、ほかの列車は動かし続ける（周期ごと止まると、後ろに並ぶ列車がすべて動かなくなる）
                        try
                        {
                            Step(t, dt * _clock.Speed, now, protectedStop);
                        }
                        catch (Exception stepError)
                        {
                            if (_errorLogged.Add(t.Id)) _logger?.LogError(stepError, "にぎやかしの列車 {Train} でエラー", t.Number);
                            continue;
                        }
                        if (t.Status != t.LoggedStatus)
                        {
                            t.LoggedStatus = t.Status;
                            var where = _rail.Line.DescribeZones(t.Info.Zones) ?? string.Join(" ", t.Info.TrackCircuits);
                            (statusLines ??= new()).Add($"{t.Number} {t.Status}（{where}）");
                        }
                        if (t.WantDeparture) { departures.Add(t.Id); t.WantDeparture = false; }
                        if (!protectedStop && t.FinishedUtc is { } f && now - f > TerminalLinger)
                        {
                            _trains.Remove(t.Id);
                            _log.Note($"にぎやかし: {t.Number} は {t.Status}。在線を消しました");
                        }
                    }
                }

                if (statusLines != null) StatusLog(now, statusLines);

                // 切り離した車両を出す（Add はほかの参加者の在線を見るので、自分のロックの外で）
                lock (_lock)
                {
                    // 元の列車が番線から出た（消えた）ら、跡地に出す
                    foreach (var w in _splitsAfterLeave.Where(w => !_trains.TryGetValue(w.ParentId, out var pt) || !pt.Info.TrackCircuits.Contains(w.Circuit)).ToList())
                    {
                        _splitsAfterLeave.Remove(w);
                        _pendingSplits.Add(w.Split);
                    }
                    splitSpawns.AddRange(_pendingSplits);
                    _pendingSplits.Clear();
                }
                foreach (var (child, cars, station, track, parent) in splitSpawns)
                {
                    var (ok, msg) = Add(child, cars, station, true, track);
                    _log.Note(ok ? $"にぎやかし: {parent} から {child}（{cars}両）を切り離しました（{station} {track}番線）" : $"にぎやかし: {child} を出せませんでした（{msg}）");
                }

                // 出発要求（RouteEngine のロックの中から呼ばないように、外で）
                foreach (var id in departures) _routes.RequestDeparture(id);

                // 自動運転列車アリ: 時刻が近づいたダイヤの列車を出す（2秒ごと）
                if (now - _lastSpawnUtc > TimeSpan.FromSeconds(2))
                {
                    _lastSpawnUtc = now;
                    AutoSpawn(users, snapshot);
                    TakeOverStaleGhosts();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "にぎやかしの列車でエラー");
            }
        }
    }

    /// <summary>
    /// 自動運転の列車の状態（画面の「状態」と同じ文）が変わったら、日ごとのファイル logs/ai-yyyyMMdd.log に1行ずつ書く。
    /// 在線の記録では回路しかわからず、止まっている理由（進路待ち・信号の手前・連結待ちなど）があとから追えないため。
    /// 1行に実際の日時・ロープレ時刻・列番・状態・いる場所を書く。1日のファイルが上限を超えたら、その日はもう書かない。
    /// </summary>
    private void StatusLog(DateTime now, List<string> lines)
    {
        // 書けなくても列車は動かし続ける（DailyLog は例外を出さない）
        var local = now.ToLocalTime();
        var game = _clock.TextAt(now);
        var sb = new StringBuilder();
        foreach (var l in lines) sb.Append($"{local:yyyy-MM-dd HH:mm:ss} {game} {l}{Environment.NewLine}");
        _statusFile.Append(now, sb.ToString(),
            () => $"{local:yyyy-MM-dd HH:mm:ss} {game} この日の状態の記録が {_statusFile.MaxMb}MB を超えたので、ここで書くのをやめます{Environment.NewLine}");
    }

    private static bool ShouldStopForProtection(Train t, HashSet<(int First, int Last)> active)
    {
        // 制動中に範囲を抜けても、その発報が解除されるまで停車を保つ。
        if (t.ReceivedRanges.Count > 0) t.ReceivedRanges.IntersectWith(active);
        foreach (var range in active)
        {
            foreach (var zone in t.Info.Zones)
            {
                if (zone < range.First || zone > range.Last) continue;
                t.ReceivedRanges.Add(range);
                break;
            }
        }
        return t.ReceivedRanges.Count > 0 || t.BrakingForProtection;
    }

    private void Step(Train t, double dt, DateTime utcNow, bool protectedStop)
    {
        if (!_clock.IsRunning) dt = 0; // ロープレ時刻が止まっている間は止まる
        // ダイヤの表が差し替わったら（臨時停車の時刻・臨時列車の適用で「終点のあと」を差し替えた列車など）、新しい表の列車を読む。
        // 進路の判断（RouteEngine）は毎周期読み直しているので、こちらだけ古い「終点のあと」で折り返さないようにする
        if (_rail.Dia.TryGetValue(t.Dia.Number, out var curDia) && !ReferenceEquals(curDia, t.Dia)) t.Dia = curDia;
        Extend(t);
        if (!t.Shunting) RefreshEventStops(t);
        if (!t.Shunting) ApplyCut(t);
        var nowGame = _clock.Now;

        // 次に止まる駅
        var next = t.Shunting ? null : t.Plan.Skip(t.PlanIndex).FirstOrDefault(p => p.Stops);
        double? stopX = next != null ? StopX(t, next) : null;
        var target = t.PathEnd - (t.Blocked ? SignalMargin : 2);
        if (stopX is { } sx && sx < target + 0.01) target = sx;

        if (protectedStop)
        {
            // 発車・折返しを進めず、走行中なら現在の進路内で停止する。
            var brakingRoom = Math.Max(0, target - t.Head);
            var brakeTime = Math.Min(dt, t.Speed / ProtectionDecel);
            var brakingDistance = (t.Speed - 0.5 * ProtectionDecel * brakeTime) * brakeTime;
            t.Head += Math.Min(brakingRoom, brakingDistance);
            t.Speed = brakingDistance >= brakingRoom || dt >= t.Speed / ProtectionDecel ? 0 : t.Speed - ProtectionDecel * dt;
            t.BrakingForProtection = t.Speed > 0;
            t.Status = t.BrakingForProtection ? "防護無線受信・制動中" : "防護無線により停車中";
            Release(t);
            UpdateInfo(t);
            return;
        }

        // 特発臨: 発駅の番線に止まっていれば、特発臨の列番に変わる
        if (t.Speed == 0 && !t.Shunting && ToRinji(t, utcNow))
        {
            UpdateInfo(t);
            return;
        }

        // 番線への入換が出発と反対向きだった（大道寺の X線 → 1番線）: 番線に入って止まったら、向きを変える
        if (!t.Dwelling && t.FromShunt && t.ShuntMoved && t.Speed == 0 && t.PlanIndex == 0 && next != null && ReverseInPlatform(t, next, utcNow))
        {
            UpdateInfo(t);
            return;
        }
        // 留置線から番線への入換で、途中の線（大道寺の X線・Y線）まで来た: 向きを変える
        if (!t.Dwelling && t.FromShunt && t.ShuntMoved && t.Speed == 0 && t.PlanIndex == 0 && next != null && MidShuntReverse(t, next))
        {
            UpdateInfo(t);
            return;
        }

        // 止まっている（駅で）
        if (t.Dwelling)
        {
            // 終着で、ダイヤの「次の列番」（大道寺で 1651 → 回1651 など）があれば、その列番の停車駅を続ける。
            // 進路は RouteEngine が列番を読み替えて作る（番線に入りきって止まったら）
            if (!t.Plan.Skip(t.DwellIndex + 1).Any()) ChainNext(t);
            var stopNow = t.Plan[t.DwellIndex];
            var depAt = stopNow.Dep ?? stopNow.Arr;
            var ready = (utcNow - t.ArrivedUtc).TotalSeconds * _clock.Speed >= MinDwellSeconds && (depAt == null || nowGame == null || IsLater(nowGame.Value, depAt.Value));
            var terminal = !t.Plan.Skip(t.DwellIndex + 1).Any();
            if (terminal)
            {
                // ダイヤではまだ先へ行く（線区の外へ出ていく）: 出発の進路が開いたら出ていく（三郷は 13RT に入って20秒で消える）。
                // 進路が開かないまま発車時刻から60秒たったら消す
                if (t.Dia.Stops.LastOrDefault()?.StationId != stopNow.StationId && t.CutStation != stopNow.StationId)
                {
                    if (ready && !t.DepartureAsked && !_central.DiaMode) { t.WantDeparture = true; t.DepartureAsked = true; }
                    if (ready && t.PathEnd - (t.Blocked ? SignalMargin : 2) > t.Head + 5)
                    {
                        t.Dwelling = false;
                        t.PlanIndex = t.DwellIndex + 1;
                        t.Status = $"{stopNow.Name} から線区の外へ";
                        UpdateInfo(t);
                        return;
                    }

                    if (ready && (depAt == null || nowGame == null || Wrap(nowGame.Value - depAt.Value) > TimeSpan.FromSeconds(60))) t.FinishedUtc ??= utcNow - TerminalLinger;
                    t.Status = ready ? $"{stopNow.Name} で出発の進路待ち（この先は線区の外）" : $"{stopNow.Name} に停車中（{(depAt is { } d0 ? d0.ToString(@"hh\:mm\:ss") : "")}発。この先は線区の外）";
                    UpdateInfo(t);
                    return;
                }

                // 折返が「入換」: 自動進路構成が開ける入換（引上げ）の進路で引上線へ入り、そこで折り返しの列番になる
                if (t.CutStation != stopNow.StationId && _rail.Turns.TurnOf(t.Dia) is { Kind: "折返", How: "入換", Number: { Length: > 0 } sn } && _rail.DiaOf(sn) != null)
                {
                    if (IsUserTrain(sn)) { HandOver(t, sn); return; }

                    // 着いた番線から引上げの入換がなく、次の列番の始発の入換がその番線から出る（江ノ原信号場10番線など）:
                    // 引き上げずに、この番線で列番を変える。入換は次の列番の始発の入換として、自動進路構成が入換時刻に開ける
                    if (_routes.TurnsInPlace(t.Dia))
                    {
                        if (!ready)
                        {
                            t.Status = $"{stopNow.Name} {stopNow.Track}番線に到着（{sn} に列番を変えます）";
                            UpdateInfo(t);
                            return;
                        }
                        t.ShuntStation = stopNow;
                        ShuntReverse(t, inPlace: true);
                        UpdateInfo(t);
                        return;
                    }

                    if (!t.DepartureAsked && !_central.DiaMode) { t.WantDeparture = true; t.DepartureAsked = true; } // 入換要求（ダイヤ運転中は自動）
                    // 道筋の先に入換（引上げ）の進路が入ってから動く。停止位置が出発信号機より手前の番線（新野崎1番線）では、
                    // 出発信号機までの残りを引上げと取り違え、番線の上で折り返しの列番になっていた
                    if (t.PathEnd - (t.Blocked ? SignalMargin : 2) > t.Head + 5 && ShuntOutAhead(t, stopNow.StationId))
                    {
                        t.Dwelling = false;
                        t.Shunting = true;
                        t.ShuntStartHead = t.Head;
                        t.ShuntStation = stopNow;
                        t.Status = "入換（引上げ）中";
                        UpdateInfo(t);
                        return;
                    }

                    // 自動進路構成が扱う駅がもうない（入換の進路を開けない）まま、
                    // 次の列番の発車1分前になったら、この列車は消す（次の列番は、その時刻にダイヤの番線へ出す）
                    if (t.PlanDetail == null && nowGame is { } ng1 && _rail.DiaOf(sn)?.Stops.FirstOrDefault(s => s.StationId == stopNow.StationId) is { } ns
                        && Time(ns.Dep ?? ns.Arr) is { } nsDep && Wrap(ng1 - nsDep) >= -TimeSpan.FromSeconds(60))
                    {
                        t.FinishedUtc ??= utcNow - TerminalLinger;
                        t.Status = $"{stopNow.Name} で入換の進路が開かないので終わり（{sn} は {stopNow.Name} の番線に出します）";
                        UpdateInfo(t);
                        return;
                    }

                    t.Status = $"{stopNow.Name} で入換（引上げ）の進路待ち" + (t.PlanDetail is { } pd1 ? $"（{pd1}）" : "");
                    UpdateInfo(t);
                    return;
                }

                // 増結車（列番が「連」＋連結する列車の列番。連777）: 番線で連結を待つ。連結する列車が来なければ、その列車の発車から10分で消す
                if (t.Number.StartsWith("連") && t.Number[1..] is { Length: > 0 } cn)
                {
                    if (nowGame is { } ng2 && _rail.DiaOf(cn)?.Stops.FirstOrDefault(s => s.StationId == stopNow.StationId) is { } cs
                        && Time(cs.Dep ?? cs.Arr) is { } cdep && Wrap(ng2 - cdep) > TimeSpan.FromMinutes(10))
                    {
                        t.FinishedUtc ??= utcNow - TerminalLinger;
                        t.Status = $"{stopNow.Name} で {cn} が来ないので終わり";
                        return;
                    }
                    t.Status = $"{stopNow.Name} で {cn} の連結待ち";
                    UpdateInfo(t);
                    return;
                }

                // ダイヤの「折返」があれば、その列番に変えて、反対向きに同じ番線から出る
                if (TurnBack(t, utcNow))
                {
                    UpdateInfo(t);
                    return;
                }

                t.FinishedUtc ??= utcNow;
                t.Status = $"{stopNow.Name} に到着（終着）";
                return;
            }

            if (ready && !t.DepartureAsked && !_central.DiaMode) { t.WantDeparture = true; t.DepartureAsked = true; }
            // 出発の進路が開いて、道筋が延びたら発車
            // 信号をまだ受け取っていなければ待つ（道筋が出した番線だけのまま、出発信号機の先まで出てしまう）
            if (ready && t.Received && t.PathEnd - (t.Blocked ? SignalMargin : 2) > t.Head + 5)
            {
                // 次の周期から走る（この周期の「次に止まる駅」は今の駅なので、ここで動かすと、また着いたことになる）
                t.Dwelling = false;
                t.PlanIndex = t.DwellIndex + 1;
                t.FromShunt = false;
                t.CoupleStage = 0;
                t.Status = "走行中";
                UpdateInfo(t);
                return;
            }
            else
            {
                t.Status = ready ? $"{stopNow.Name} で出発の進路待ち" + (t.PlanDetail is { } pd0 ? $"（{pd0}）" : "") : $"{stopNow.Name} に停車中（{(depAt is { } d ? d.ToString(@"hh\:mm\:ss") : "")}発）";
                UpdateInfo(t);
                return;
            }
        }

        // 最初の駅: 発車時刻まで待つ
        if (t.PlanIndex == 0 && t.Speed == 0 && t.Plan.Count > 0 && Math.Abs(t.Head - t.StartHead) < 0.5 && !t.FromShunt && !t.MidLine && !t.Shunting) // 入換（引上げ）を始めた列車は除く（出したばかりの番線から、すぐ引き上げる 解2076C など）
        {
            t.Dwelling = true;
            t.DwellIndex = 0;
            t.ArrivedUtc = utcNow.AddSeconds(-MinDwellSeconds);
            UpdateInfo(t);
            return;
        }

        // 速さ: 次の停車駅の時刻に間に合うように
        // 区間の中にもう停車駅がない（赤山町を通過して出ていく上りの特急など）ときは、残りの通過駅の時刻だけで決める
        var cruise = 75 / 3.6;
        if (nowGame is { } ng && (next != null ? (next.Arr ?? next.Dep) != null : !t.Shunting))
        {
            if (next != null && (next.Arr ?? next.Dep) is { } due)
            {
                var remain = Wrap(due - ng).TotalSeconds;
                var dist = stopX is { } x ? x - t.Head : EstimateDistance(t, next);
                if (dist is { } dd && dd > 0)
                    cruise = remain <= 10 ? 110 / 3.6 : Math.Clamp(dd / remain * 1.1, 35 / 3.6, 110 / 3.6);
            }
            else cruise = 110 / 3.6;

            // その手前の通過駅にも時刻があれば、そこにも早く着きすぎないように（通過の進路は、ダイヤの時刻になってから開く）
            foreach (var pass in t.Plan.Skip(t.PlanIndex).TakeWhile(p => p != next).Where(p => !p.Stops))
            {
                if ((pass.Dep ?? pass.Arr) is not { } pdue || EstimateDistance(t, pass) is not { } pd || pd <= 0) continue;
                var prem = Wrap(pdue - ng).TotalSeconds;
                if (prem > 10) cruise = Math.Min(cruise, Math.Max(35 / 3.6, pd / prem * 1.1));
            }
        }

        // 線路の制限速度（走ったときの記録がある所だけ）
        cruise = Math.Min(cruise, LimitCap(t));
        if (t.Shunting) cruise = Math.Min(cruise, ShuntSpeed);

        var room = Math.Max(0, target - t.Head);
        var allowed = Math.Sqrt(2 * Decel * room);
        var v = Math.Min(Math.Min(t.Speed + Accel * dt, cruise), allowed);
        if (t.Speed > cruise) v = Math.Max(cruise, t.Speed - Decel * dt);
        v = Math.Min(v, allowed);
        var move = Math.Min(v * dt, room);
        t.Head += move;
        t.Speed = room - move < 0.05 && v < 1 ? 0 : v;

        // 入換（引上げ）: 引上線の奥で止まったら、折り返しの列番になる
        if (t.Shunting && t.Speed == 0 && t.Head - t.ShuntStartHead > 20 && t.PathEnd - t.Head < SignalMargin + 3)
        {
            ShuntReverse(t);
            UpdateInfo(t);
            return;
        }

        // 駅に着いた（停止位置の表がなく、線路の終わり（先に信号機がない）で止まったときも、次の停車駅に着いたとする）
        // 停止位置が線路の終わりより先にある（回路の長さの見込みが短い行き止まりなど）ときも、線路の終わりで止まったら着いたとする
        var atEnd = next != null && (stopX == null || stopX > t.PathEnd - 2) && !t.Blocked && t.Speed == 0 && t.PathEnd - t.Head < 5 && !(t.FromShunt && !t.ShuntMoved); // 引上線で折り返した直後（まだ番線へ動いていない）は除く
        if (next != null && (stopX is { } at && t.Speed == 0 && Math.Abs(t.Head - at) < 0.5 || atEnd))
        {
            // 番線への入換で番線に入って止まり、出発の進路が入換で来た道を戻る（江ノ原信号場の 13番線 → 12番線 → 4L）:
            // 着いたことにする前に向きを変える（停止位置の表がない駅は、番線の回路の終わりで着いたことになり、向きを変えないまま出発の進路を待ち続ける）
            if (t.FromShunt && t.ShuntMoved && t.PlanIndex == 0 && DepartGoesBack(t, next) && ReverseInPlatform(t, next, utcNow))
            {
                UpdateInfo(t);
                return;
            }
            // 連結: 停B（停止位置目標のない駅は、連結位置の 4m 手前）で一度止まり（完全に止まってから）、連結位置まで進む
            if (t.CoupleStage < 2 && CouplePartnerAt(t, next) != null && CouplePos(t, next) is { } cpos)
            {
                if (t.CoupleStage == 0) (t.CoupleStage, t.CoupleStopUtc) = (1, utcNow);
                else if ((utcNow - t.CoupleStopUtc).TotalSeconds * _clock.Speed >= CoupleStopB.TotalSeconds) t.CoupleStage = 2;
                t.Status = cpos.Marker ? $"{next.Name} 停B で停止（連結の前）" : $"{next.Name} 連結位置の4m手前で一旦停止（駅係員の合図で連結）";
                Release(t);
                UpdateInfo(t);
                return;
            }
            if (t.CoupleStage == 2) CoupleWith(t, next);
            SplitAt(t, next);
            t.Dwelling = true;
            t.DwellIndex = t.Plan.IndexOf(next);
            t.DepartureAsked = false;
            t.ArrivedUtc = utcNow;
        }

        // 通り過ぎた駅（通過）は、次の停車駅までの一覧から外す
        while (t.PlanIndex < t.Plan.Count && !t.Plan[t.PlanIndex].Stops && Passed(t, t.Plan[t.PlanIndex])) t.PlanIndex++;

        t.Status = t.Shunting ? "入換（引上げ）中" : t.Speed > 0 ? "走行中" : t.Blocked ? $"{t.EndSignal} の手前で停止" + ((t.PlanDetail ?? t.RouteMessage) is { Length: > 0 } why ? $"（{why}）" : "") : "停止";
        Release(t);
        UpdateInfo(t);
    }

    /// <summary>
    /// 入換（引上げ）で引上線の奥に止まったら、折り返しの列番（ダイヤの「折返」）になる。向きを変え、道筋は今いる軌道回路から作り直す。
    /// ホームへの入換の進路は、自動進路構成が新しい列番の始発の入換として、時刻に合わせて開ける（開いたら、その進路で番線に入る）。
    /// inPlace は、引き上げずに着いた番線で列番を変えるとき（<see cref="RouteEngine.TurnsInPlace"/>）。
    /// </summary>
    private void ShuntReverse(Train t, bool inPlace = false)
    {
        var here = t.ShuntStation ?? t.Plan[t.DwellIndex];
        var turn = _rail.Turns.TurnOf(t.Dia);

        // 留置線へ入れる引上げ（大道寺 3番線 → X線 → 5番線）: 引き上げた線では列番を変えず、向きを変えて留置線への進路を待つ。
        // 留置線に入って止まったら（もう一度ここに来る）、そこで列番を変える。
        // 引き上げた線・留置線のほかで止まったとき（番線の中で、引上げの進路が開く前に止まった）は、何もしない（進路を待つ）
        if (!inPlace && turn?.Via is { } via && _rail.Stations.TryGetValue(here.StationId, out var sst)
            && sst.Routes[t.Dir].ShuntOuts.FirstOrDefault(o => o.Label == via && o.ThenRoutes.Count > 0) is { } stable)
        {
            var at = t.Path.SelectMany(s => s.Circuits).LastOrDefault(c => c.Start < t.Head)?.Name;
            var mid = stable.Routes.LastOrDefault()?.Circuits.LastOrDefault();
            var dest = stable.ThenRoutes.LastOrDefault()?.Circuits.LastOrDefault();
            if (at != (t.StableTurned ? dest : mid))
            {
                t.Status = t.StableTurned ? $"{here.Name} {via}への入換中" : $"{here.Name} で入換（引上げ）の進路待ち";
                return;
            }
            if (!t.StableTurned)
            {
                Reverse(t);
                t.StableTurned = true;
                t.ShuntStartHead = t.Head;
                t.Status = $"{here.Name} の引上げた線で向きを変えました。{via}への入換待ち";
                _log.Note($"にぎやかし: {t.Number} は {here.Name} の引上げた線で向きを変えました（{via}へ）");
                return;
            }
        }
        var headCircuit = t.Path.SelectMany(s => s.Circuits).LastOrDefault(c => c.Start < t.Head)?.Name ?? t.Info.TrackCircuits.LastOrDefault() ?? "";
        // 引き上げた先（入換の進路の終わりの回路）に着いていなければ、列番を変えない（進路を待つ）
        if (!inPlace && !t.StableTurned && _rail.Stations.TryGetValue(here.StationId, out var sost) && sost.Routes[t.Dir].ShuntOuts.Count > 0
            && !sost.Routes[t.Dir].ShuntOuts.Any(o => o.Routes.LastOrDefault()?.Circuits.LastOrDefault() == headCircuit))
        {
            t.Status = $"{here.Name} で入換（引上げ）の進路待ち";
            return;
        }

        if (turn?.Number is { Length: > 0 } tn && _routes.DirOfTrain(tn) is { } tdir && _rail.Stations.TryGetValue(here.StationId, out var tst)
            && WrongTrack(tst, tdir, headCircuit) is { } wrong)
        {
            t.Status = $"{here.Name} で入換（引上げ）の進路待ち（{wrong}）";
            if (_placeRefused.Add((t.Id, tn))) _log.Note($"にぎやかし: {t.Number} は {tn} に変わりません（{wrong}。{headCircuit}）");
            return;
        }

        var stabled = t.StableTurned;
        t.StableTurned = false;
        t.Shunting = false;
        if (turn?.Number is not { Length: > 0 } number || _rail.DiaOf(number) is not { } nd || _routes.DirOfTrain(number) is not { } dir)
        {
            t.FinishedUtc ??= DateTime.UtcNow;
            t.Status = "入換（引上げ）を終えました";
            return;
        }

        var i = nd.Stops.FindIndex(s => s.StationId == here.StationId);
        var old = t.Number;
        _spawned.Add(RailData.NormalizeTrain(number));
        t.Number = number;
        t.Dia = nd;
        (t.CutStation, t.CutRest, t.CutWasStop) = (null, null, false);
        t.Dir = dir;
        t.Shown = null;
        t.StartTrack = -1;
        t.FromShunt = true;
        t.ShuntMoved = false;
        t.Plan.Clear();
        foreach (var s in nd.Stops.Skip(Math.Max(0, i)))
        {
            if (s.StationId == null || !InArea(s.StationId)) continue;
            var isFirst = t.Plan.Count == 0;
            t.Plan.Add(isFirst ? new PlanStop(s.Name, s.StationId, true, s.Track, null, Time(s.Dep) ?? Time(turn.Time), nd, s) : PlanOf(nd, s));
        }

        // 今いる軌道回路（引上線）から、反対向きに道筋を作り直す
        t.ShuntOrigin = Reverse(t);
        t.MidTurned = false;
        t.PlanIndex = 0;
        t.Dwelling = false;
        t.Info.TrainNumber = number;
        t.Info.Stops = t.Plan.Select(p => new StopInfo { Name = p.Name, StopType = p.Stops ? "StopForPassenger" : "Passing" }).ToList();
        t.Info.LastCenter = null;
        if (stabled)
        {
            t.Status = $"{here.Name} の留置線に入りました（{old} → {number}）。番線への入換待ち";
            _log.Note($"にぎやかし: {old} は {here.Name} の留置線に入って {number} になりました");
            return;
        }
        if (inPlace)
        {
            t.Status = $"{here.Name} {here.Track}番線で列番変更（{old} → {number}）。入換待ち";
            _log.Note($"にぎやかし: {old} は {here.Name} {here.Track}番線で列番を変えて {number} になりました");
            return;
        }
        t.Status = $"引上線で折り返し（{old} → {number}）。番線への入換待ち";
        _log.Note($"にぎやかし: {old} は {here.Name} の引上線で折り返して {number} になりました");
    }

    /// <summary>列番を変えなかった・出さなかったことを一度だけ記録するため（列車ID・列番）。</summary>
    private readonly HashSet<(string Id, string Number)> _placeRefused = new();

    /// <summary>道筋の先（まだ通っていない所）に、その駅の入換（引上げ）の進路（TrackRoutes.json の shuntOut）が入っているか。</summary>
    private bool ShuntOutAhead(Train t, string stationId)
    {
        if (!_rail.Stations.TryGetValue(stationId, out var st)) return false;
        var keys = st.Routes[t.Dir].ShuntOuts.SelectMany(o => o.Routes).Select(r => (r.Interlock, r.Name)).ToHashSet();
        return t.Path.Any(s => s.End > t.Head && s.Route is { } r && keys.Contains((r.Interlock, r.Name)));
    }

    /// <summary>
    /// その向きの列車が使う番線か（着く・出る・入換で入る・入換で出る番線）。
    /// 上りしか使わない番線に下りの列車を出したり、そこで下りの列番に変えたりしないため（新野崎1番線に下りの 1865 が出た）。
    /// </summary>
    private static bool TrackFitsDir(StationDef st, Dir dir, int track)
    {
        var r = st.Routes[dir];
        // 入換は、番線の回路で書いたもの（大道寺5番線の上りの入換は「TH65_5T から」）も数える。数えないと、5番線で 1653 が上りの 2152 に変われない
        var circuit = st.TrackCircuit.GetValueOrDefault(track);
        return r.Depart.ContainsKey(track) || r.Arrive.ContainsKey(track)
               || r.Shunts.Any(s => s.From == track || s.To == track || circuit != null && s.FromCircuit == circuit)
               || r.ShuntOuts.Any(o => o.From == track);
    }

    /// <summary>軌道回路がその駅の番線の回路で、その番線がその向きの列車の使わない番線なら、その理由。番線でない回路（引上線の分岐など）は null。</summary>
    private static string? WrongTrack(StationDef st, Dir dir, string circuit)
    {
        var tracks = st.TrackCircuit.Where(kv => kv.Value == circuit).Select(kv => kv.Key).ToList();
        return tracks.Count > 0 && !tracks.Any(k => TrackFitsDir(st, dir, k)) ? $"{st.Name} {tracks[0]}番線は{dir.Label()}の列車が使わない番線" : null;
    }

    /// <summary>今いる軌道回路から、反対向きに道筋を作り直す（列番・停車駅はそのまま）。その軌道回路を返す。</summary>
    private string Reverse(Train t)
    {
        var circuit = t.Path.SelectMany(s => s.Circuits).LastOrDefault(c => c.Start < t.Head)?.Name ?? t.Info.TrackCircuits.LastOrDefault() ?? "";
        var len = Math.Max(LengthOf(circuit), t.Length + 10);
        t.Path.Clear();
        t.Path.Add(new Seg(null, null, 0, len, [new SegCircuit(circuit, 0, len)]));
        t.Head = len;
        t.StartHead = t.Head;
        t.Speed = 0;
        t.Blocked = true;
        t.EndSignal = null;
        t.Info.GameRoutes.Clear();
        t.Info.LastCenter = null;
        return circuit;
    }

    /// <summary>
    /// 留置線から番線への入換の途中（大道寺 5番線 → X線 → 2番線 の X線）で止まったら、向きを変える。
    /// 番線への2つ目の進路は、自動進路構成が開ける（開いたら、その進路で番線に入る）。
    /// </summary>
    private bool MidShuntReverse(Train t, PlanStop p)
    {
        if (t.MidTurned || t.ShuntOrigin is not { } origin || t.PathEnd - t.Head > SignalMargin + 3) return false;
        if (!_rail.Stations.TryGetValue(p.StationId, out var st)) return false;
        var here = t.Path.SelectMany(s => s.Circuits).LastOrDefault(c => c.Start < t.Head)?.Name;
        if (here == null || !st.Routes[t.Dir].Shunts.Any(s => s.ThenRoutes.Count > 0 && s.FromCircuit == origin && s.Routes.LastOrDefault()?.Circuits.LastOrDefault() == here))
            return false;
        Reverse(t);
        t.MidTurned = true;
        t.ShuntMoved = false;
        t.Status = $"{st.Name} の入換の途中で向きを変えました。番線への入換待ち";
        return true;
    }

    /// <summary>
    /// 引上線などから番線への入換が、出発と反対向きだったとき（大道寺の X線 → 1番線。X線は出発していく側の本線にある）、
    /// 番線に入って止まったら向きを変え、出発の停止位置にいることにする。道筋は番線の回路から作り直す（<see cref="TurnBack"/> と同じ）。
    /// 出発信号機が道筋の先にあれば（入換と出発が同じ向き。大道寺の Y線 → 番線）、何もしない。
    /// </summary>
    /// <summary>
    /// 今いる番線から出る出発の進路が、ここまで来た道筋の回路を通るか（＝入換と反対向きに出る。番線で向きを変える必要がある）。
    /// 江ノ原信号場の 13番線 → 12番線（13RB）のあと 4L で出るときなど。
    /// </summary>
    private bool DepartGoesBack(Train t, PlanStop p)
    {
        if (!_rail.Stations.TryGetValue(p.StationId, out var st)) return false;
        var here = t.Path.SelectMany(s => s.Circuits).LastOrDefault(c => c.Start < t.Head)?.Name;
        if (here == null || st.TrackCircuit.FirstOrDefault(kv => kv.Value == here) is not { Value: not null } hit) return false;
        if (!st.Routes[t.Dir].Depart.TryGetValue(hit.Key, out var depart)) return false;
        var came = t.Path.SelectMany(s => s.Circuits).Select(c => c.Name).Where(c => c != here).ToHashSet();
        return depart.SelectMany(r => r.Circuits).Any(came.Contains);
    }

    private bool ReverseInPlatform(Train t, PlanStop p, DateTime utcNow)
    {
        if (!_rail.Stations.TryGetValue(p.StationId, out var st) || t.PathEnd - t.Head > SignalMargin + 3) return false;
        var here = t.Path.SelectMany(s => s.Circuits).LastOrDefault(c => c.Start < t.Head)?.Name;
        if (here == null || st.TrackCircuit.FirstOrDefault(kv => kv.Value == here) is not { Value: not null } hit) return false;
        var track = hit.Key;
        if (_stops.FirstOrDefault(s => s.Station == p.Name && s.Dir == t.Dir && s.Track == track) is { } spec && BoundaryOf(t, spec.Signal) != null) return false;

        var before = ((FindStop(p.Name, t.Dir, track) ?? FindStop(st.Name, t.Dir, track))?.Before ?? 20)
                     + MarkerOffset(t, p.Name, t.Dir, track, t.Plan.Count > 0 && t.Plan[^1].StationId == p.StationId);
        var len = Math.Max(LengthOf(here), t.Length + before + 10);
        t.Path.Clear();
        t.Path.Add(new Seg(null, null, 0, len, [new SegCircuit(here, 0, len)]));
        t.Head = len - before;
        t.StartHead = t.Head;
        t.Plan[0] = p with { Track = track };
        t.StartTrack = track;
        t.PlanIndex = 0;
        t.DwellIndex = 0;
        t.Dwelling = true;
        t.ArrivedUtc = utcNow;
        t.DepartureAsked = false;
        t.Blocked = true;
        t.EndSignal = null;
        t.FromShunt = false;
        t.Info.LastCenter = null;
        t.Status = $"{st.Name} {track}番線で向きを変えました（入換を終えて出発待ち）";
        return true;
    }

    /// <summary>
    /// 終着で、ダイヤの「折返」の列車に変わる。列番・向き・停車駅を入れ替え、道筋は今の番線の軌道回路から作り直す
    /// （停止位置は、反対向きの出発信号機の手前）。進路は、列番が変わったので RouteEngine が新しい列車として作る。
    /// </summary>
    private bool TurnBack(Train t, DateTime utcNow)
    {
        var here = t.Plan[t.DwellIndex];
        // 途中折返し・直接折返の指定があれば、その駅でだけ折り返す。打ち切りの駅で、そこでの折返しの指定がなければ、ここで終わり
        var turnAt = _rail.Turns.DirectOf(t.Dia.Number)?.Station;
        if (t.CutStation == here.StationId ? turnAt != here.StationId : turnAt != null && turnAt != here.StationId) return false;
        if (_rail.Turns.TurnOf(t.Dia) is not { Kind: "折返", Number: { Length: > 0 } number } || _rail.DiaOf(number) is not { } nd) return false;
        if (IsUserTrain(number)) { HandOver(t, number); return false; }
        if (_routes.DirOfTrain(number) is not { } dir || !_rail.Stations.TryGetValue(here.StationId, out var st)) return false;
        var i = nd.Stops.FindIndex(s => s.StationId == here.StationId);
        // 増結車に変わる（特発臨の 臨回1186 → 連1185C。連の列車は、この駅だけの列車）: 着いた番線でそのまま列番を変え、連結を待つ
        if (i == 0 && nd.Stops.Count == 1 && number.StartsWith("連"))
        {
            if (st.TrackCircuit.Where(kv => t.Info.TrackCircuits.Contains(kv.Value)).Select(kv => (int?)kv.Key).FirstOrDefault() is not { } ct
                || !st.TrackCircuit.TryGetValue(ct, out var cc)) return false;
            var was = t.Number;
            Become(t, number, nd, dir, st, ct, cc, 0, null, utcNow);
            t.Status = $"{st.Name} {ct}番線で増結車に変わりました（{was} → {number}）";
            _log.Note($"にぎやかし: {was} は {st.Name} {ct}番線で {number} になりました（連結を待ちます）");
            return true;
        }
        if (i < 0 || i == nd.Stops.Count - 1) return false;
        // 番線は、いまいる番線の回路から（直接折返などで、ダイヤと違う番線に着いていることがある。大道寺は 2番線の代わりに 3番線）。
        // 回路から分からなければダイヤの番線
        var dt = here.Track is { } ht && st.TrackCircuit.ContainsKey(ht) ? ht : (int?)null;
        var track = dt is { } d0 && t.Info.TrackCircuits.Contains(st.TrackCircuit[d0]) ? d0
            : st.TrackCircuit.Where(kv => t.Info.TrackCircuits.Contains(kv.Value)).Select(kv => (int?)kv.Key).FirstOrDefault() ?? dt ?? -1;
        if (!st.TrackCircuit.TryGetValue(track, out var platform)) return false;
        if (!TrackFitsDir(st, dir, track))
        {
            if (_placeRefused.Add((t.Id, number))) _log.Note($"にぎやかし: {t.Number} は {st.Name} {track}番線で {number} に折り返しません（{dir.Label()}の列車が使わない番線）");
            return false;
        }

        var old = t.Number;
        Become(t, number, nd, dir, st, track, platform, i, Time(_rail.Turns.TurnOf(t.Dia)?.Time), utcNow);
        t.Status = $"{st.Name} {track}番線で折り返し（{old} → {number}）";
        _log.Note($"にぎやかし: {old} は {st.Name} で折り返して {number} になりました");
        return true;
    }

    /// <summary>
    /// 特発臨（<see cref="TokuhatsuRinji"/>）: 元の列車が、特発臨の発駅の番線に止まったら、特発臨の列番に変わる。
    /// 向き・停車駅を入れ替え、道筋は今の番線の軌道回路から作り直す（<see cref="TurnBack"/> と同じ）。途中駅でも、終点でなくても変わる。
    /// </summary>
    private bool ToRinji(Train t, DateTime utcNow)
    {
        if (Rinji is not { } rinji || rinji.PendingFor(t.Number) is not { } r || _rail.DiaOf(r.Number) is not { } nd) return false;
        if (!_rail.Stations.TryGetValue(r.StationId, out var st)) return false;
        // 発駅の番線（決めた番線にいなければ、止まった番線。決めた番線に入れなかったときも、着いた所で変わる）
        var track = r.Track is { } rt && st.TrackCircuit.TryGetValue(rt, out var rc) && t.Info.TrackCircuits.Contains(rc) ? rt
            : st.TrackCircuit.Where(kv => t.Info.TrackCircuits.Contains(kv.Value)).Select(kv => (int?)kv.Key).FirstOrDefault() ?? -1;
        if (!st.TrackCircuit.TryGetValue(track, out var platform) || !t.Info.TrackCircuits.Contains(platform)) return false; // まだ発駅の番線に着いていない
        if (_routes.DirOfTrain(r.Number) is not { } dir) return false;
        var i = nd.Stops.FindIndex(s => s.StationId == r.StationId);
        if (i < 0 || i == nd.Stops.Count - 1) return false;

        var old = t.Number;
        t.Shunting = false;
        t.FromShunt = false;
        t.CoupleStage = 0;
        // 終着・線区の外へ出る列車として「終わり」の印が付いていたら外す（残すと、特発臨に変わったあと、しばらくして消してしまう）
        t.FinishedUtc = null;
        t.ExitUtc = null;
        Become(t, r.Number, nd, dir, st, track, platform, i, null, utcNow);
        rinji.MarkStarted(r.Number, track);
        t.Status = $"{st.Name} {track}番線で特発臨に変わりました（{old} → {r.Number}）";
        _log.Note($"にぎやかし: {old} は {st.Name} {track}番線で特発臨 {r.Number} になりました");
        return true;
    }

    /// <summary>
    /// 駅の番線で、別の列番（折り返しの列車・特発臨）に変わる。列番・向き・停車駅を入れ替え、道筋は番線の軌道回路から作り直す
    /// （停止位置は、新しい向きの出発信号機の手前）。i は新しい列番の時刻表で、この駅の位置。
    /// </summary>
    private void Become(Train t, string number, DiaTrain nd, Dir dir, StationDef st, int track, string platform, int i, TimeSpan? depFallback, DateTime utcNow)
    {
        _spawned.Add(RailData.NormalizeTrain(number));
        t.Number = number;
        t.Dia = nd;
        t.Dir = dir;
        t.Shown = null;
        t.StartStation = st.Name;
        t.StartTrack = track;
        t.Plan.Clear();
        var first = nd.Stops[i];
        t.Plan.Add(new PlanStop(first.Name, st.Id, true, track, null, Time(first.Dep) ?? depFallback));
        foreach (var s in nd.Stops.Skip(i + 1))
        {
            if (s.StationId == null || !InArea(s.StationId)) continue;
            t.Plan.Add(PlanOf(nd, s));
        }

        var before = ((FindStop(first.Name, dir, track) ?? FindStop(st.Name, dir, track))?.Before ?? 20)
                     + MarkerOffset(t, first.Name, dir, track, t.Plan.Count <= 1);
        var len = Math.Max(LengthOf(platform), t.Length + before + 10);
        t.Path.Clear();
        t.Path.Add(new Seg(null, null, 0, len, [new SegCircuit(platform, 0, len)]));
        t.Head = len - before;
        t.StartHead = t.Head;
        t.Speed = 0;
        t.PlanIndex = 0;
        t.DwellIndex = 0;
        t.Dwelling = true;
        t.ArrivedUtc = utcNow;
        t.DepartureAsked = false;
        t.Blocked = true;
        t.EndSignal = null;
        t.Info.TrainNumber = number;
        t.Info.GameRoutes.Clear();
        t.Info.Stops = t.Plan.Select(p => new StopInfo { Name = p.Name, StopType = p.Stops ? "StopForPassenger" : "Passing" }).ToList();
        t.Info.LastCenter = null;
    }

    /// <summary>ゲームの線区（進路を扱う駅のいちばん端からいちばん端まで）の中の駅か。</summary>
    private bool InArea(string stationId)
    {
        var zone = _rail.Line.ZoneOfStation(stationId);
        if (zone < 0) return false;
        var zones = _rail.ControlStations.Select(st => _rail.Line.ZoneOfStation(st.Id)).Where(z => z >= 0).ToList();
        return zones.Count > 0 && zone >= zones.Min() && zone <= zones.Max();
    }

    private void ChainNext(Train t)
    {
        var here = t.Plan[t.DwellIndex];
        if (t.CutStation == here.StationId) return;
        if (t.Dia.Next is not { } nx || nx.StationId != here.StationId || _rail.DiaOf(nx.Number) is not { } nd) return;
        if (IsUserTrain(nx.Number)) { HandOver(t, nx.Number); return; }
        var i = nd.Stops.FindIndex(s => s.StationId == here.StationId);
        if (i < 0 || i == nd.Stops.Count - 1) return;
        var first = nd.Stops[i];
        t.Plan[t.DwellIndex] = here with { Dep = Time(first.Dep) ?? here.Dep };
        foreach (var s in nd.Stops.Skip(i + 1))
        {
            if (s.StationId == null || !InArea(s.StationId)) continue;
            t.Plan.Add(PlanOf(nd, s));
        }

        t.Dia = nd;
        _spawned.Add(RailData.NormalizeTrain(nd.Number));
        t.Info.Stops = t.Plan.Select(p => new StopInfo { Name = p.Name, StopType = p.Stops ? "StopForPassenger" : "Passing" }).ToList();
        t.Shown = $"{t.Number} → {nd.Number}";
    }

    /// <summary>道筋の先へ、進める信号機の先を足していく（R・進路なしの信号機で止める）。</summary>
    private void Extend(Train t)
    {
        if (!t.Received) return; // 信号をまだ受け取っていない

        // 先に延ばした道筋のうち、まだ入っていない区間の信号機が R になった・進路が消えたら、そこで縮める
        for (var i = 1; i < t.Path.Count; i++)
        {
            var s = t.Path[i];
            if (s.Start <= t.Head + 1) continue;
            var red = s.Signal != null && t.RedSignals.Contains(s.Signal);
            var gone = s.Route is { } r && !t.Info.GameRoutes.Contains((r.Interlock, r.Name));
            if (!red && !gone) continue;
            t.Path.RemoveRange(i, t.Path.Count - i);
            break;
        }

        for (var guard = 0; guard < 20; guard++)
        {
            var last = t.Path[^1];
            var lastCircuit = last.Circuits[^1].Name;
            var candidates = NextSignals(t, last);
            t.EndSignal = candidates.FirstOrDefault();
            t.Blocked = candidates.Count > 0;
            // 道筋が十分先まであれば、それ以上は延ばさない
            if (last.End - t.Head > 3000) return;
            if (candidates.Count == 0)
            {
                // 信号機のつながりに出てこない進路（入換信号機など）: 開いている自分の進路で、今の軌道回路につながるもの
                if (OpenRouteFrom(t, last) is { } os) { t.Path.Add(os); if (t.FromShunt) t.ShuntMoved = true; continue; }
                return; // 線路の終わり
            }

            Seg? seg = null;
            foreach (var name in candidates)
            {
                if (t.RedSignals.Contains(name)) continue;
                if (_routesBySignal.TryGetValue(name, out var defs))
                {
                    // 駅の信号機: 開いている自分の進路
                    var open = defs.FirstOrDefault(d => t.Info.GameRoutes.Contains((d.Interlock, d.Name)) && d.Circuits.Count > 0 && Adjacent(lastCircuit, d.Circuits));
                    open ??= defs.FirstOrDefault(d => t.Info.GameRoutes.Contains((d.Interlock, d.Name)) && d.Circuits.Count > 0);
                    if (open == null) continue;
                    // 進路の回路が、もう道筋に入っている（新野崎の入換25LD は 22L の先の 5LT まで。22L の回路は 5LT だけ）なら、
                    // 長さ 0 の区間にして、その信号機を過ぎたことにする（その先へ道筋を延ばせるように）
                    var path = RoutePath(open, lastCircuit);
                    // 今いる回路から続かない進路（信号機は同じでも別の所から出る進路。盤で開けた進路はどの列車にも写る）は使わない。
                    // 使うと、在線がその進路の回路へ飛ぶ（大道寺5番線の 1653 が、盤で開けた 27L の 11LT へ飛んだ）
                    if (!Continues(t, lastCircuit, open, path)) continue;
                    seg = path.Count > 0 ? MakeSeg(name, open, path, last.End)
                        : new Seg(name, open, last.End, last.End, [new SegCircuit(lastCircuit, last.End, last.End)]);
                }
                else if (_rail.Signals.TryGetValue(name, out var sig) && sig.Protects.Count > 0 && Order(sig.Protects, lastCircuit) is { Count: > 0 } protects)
                {
                    seg = MakeSeg(name, null, protects, last.End);
                }

                if (seg != null) break;
            }

            seg ??= OpenRouteFrom(t, last, candidates);
            if (seg == null)
            {
                // 止める信号機: 自分の進路の信号機（並んで建つものから）を選ぶ
                t.EndSignal = candidates.FirstOrDefault(c => _routesBySignal.TryGetValue(c, out var ds) && ds.Any(d => Adjacent(lastCircuit, d.Circuits))) ?? candidates[0];
                return;
            }

            t.Path.Add(seg);
            t.Blocked = false;
            if (t.FromShunt) t.ShuntMoved = true;
        }
    }

    /// <summary>開いている自分の進路のうち、道筋の最後の軌道回路につながっていて、まだ道筋にないもの（入換など）。R の信号機のものは除く。</summary>
    // ahead: 道筋の先に建つ信号機（信号機のつながりから）。出発・場内信号機の進路は、この中の信号機のものだけ使う。
    // 前の信号機が赤で進めないときに、隣から始まる別の信号機の進路を拾って赤を越えないように（新野崎の 4番線の列車が、
    // 3L が赤なのに、盤で開けた 2L（3番線から出る。回路は同じ 31T・TST）に入って出ていった）
    private Seg? OpenRouteFrom(Train t, Seg last, IReadOnlyCollection<string>? ahead = null)
    {
        var lastCircuit = last.Circuits[^1].Name;
        if (!_adj.TryGetValue(lastCircuit, out var near)) return null;
        var inPath = t.Path.SelectMany(s => s.Circuits).Select(c => c.Name).ToHashSet();
        foreach (var key in t.Info.GameRoutes)
        {
            if (!_rail.Routes.TryGetValue(key, out var d) || d.Circuits.Count == 0 || t.RedSignals.Contains(d.Signal)) continue;
            if (ahead is { Count: > 0 } && d.Kind is "出発信号機" or "場内信号機" && !ahead.Contains(d.Signal)) continue;
            if (t.Path.Any(s => s.Route == d) || d.Circuits.Any(inPath.Contains)) continue;
            if (!near.Contains(d.Circuits[0]) && !near.Contains(d.Circuits[^1])) continue;
            if (RoutePath(d, lastCircuit) is not { Count: > 0 } path || !Continues(t, lastCircuit, d, path)) continue;
            return MakeSeg(d.Signal, d, path, last.End);
        }

        return null;
    }

    /// <summary>道筋に足す進路の回路が、今の最後の回路の隣から始まっているか（足す回路がなければ続いているとみなす）。続かなければ一度だけ記録する。</summary>
    private bool Continues(Train t, string from, RouteDef route, List<string> path)
    {
        if (path.Count == 0) return true;
        if (_adj.TryGetValue(from, out var near) && near.Contains(path[0]))
        {
            // 隣につながっていても、別の番線から出る進路なら使わない。同じ回路から始まる進路がいくつかあり、今いる回路を接近の区間に持つ
            // 別の進路があれば、それが自分の進路（新野崎の 2L は 3番線、3L は 4番線から出る。回路はどちらも 31T・TST。
            // 盤で 2L を開けたら、4番線の列車が 2L を自分の出発進路と取って出た。軌道回路のつながりだけでは転てつ器の向きが分からない）
            if (route.Approach.Count == 0 || route.Approach.Contains(from) || !_otherFromHere(route, from)) return true;
            if (_jumpRefused.Add((t.Id, route.Interlock, route.Name)))
                _log.Note($"にぎやかし: {t.Number} は {route.Name}（{route.Interlock}）に入りません（{from} から出る進路ではない）");
            return false;
        }
        if (_jumpRefused.Add((t.Id, route.Interlock, route.Name)))
            _log.Note($"にぎやかし: {t.Number} は {route.Name}（{route.Interlock}）に入りません（{from} から続いていない。{string.Join(" ", path)}）");
        return false;
    }

    /// <summary>同じ連動装置で、route と同じ回路から始まり、from を接近の区間に持つ別の進路があるか（あれば route は from から出る進路ではない）。</summary>
    private bool _otherFromHere(RouteDef route, string from) =>
        _rail.Routes.Values.Any(d => d != route && d.Interlock == route.Interlock && d.Circuits.Count > 0 && route.Circuits.Count > 0
            && d.Circuits[0] == route.Circuits[0] && d.Approach.Contains(from));

    /// <summary>続かない進路を使わなかったことを一度だけ記録するため（列車ID・連動装置・進路）。</summary>
    private readonly HashSet<(string Id, string Interlock, string Route)> _jumpRefused = new();

    private Seg MakeSeg(string signal, RouteDef? route, List<string> circuits, double start)
    {
        // 長さ: 次の信号機までの距離（走ったときの記録）。なければ軌道回路の長さの合計
        var weights = circuits.Select(LengthOf).ToList();
        var total = weights.Sum();
        var probe = new Seg(signal, route, start, start + total, circuits.Select(c => new SegCircuit(c, 0, 0)).ToList());
        var nextSig = NextSignals(null, probe).FirstOrDefault();
        if (nextSig != null && Distance(signal, nextSig) is { } d && d > 5) total = d;
        var scale = total / Math.Max(1, weights.Sum());
        var list = new List<SegCircuit>();
        var x = start;
        for (var i = 0; i < circuits.Count; i++)
        {
            var len = weights[i] * scale;
            list.Add(new SegCircuit(circuits[i], x, x + len));
            x += len;
        }

        return new Seg(signal, route, start, start + total, list);
    }

    /// <summary>道筋の最後の区間の先にある信号機（並んで建つものは全部）。</summary>
    private List<string> NextSignals(Train? t, Seg last)
    {
        if (last.Circuits.Count == 0) return new List<string>();
        var lastCircuit = last.Circuits[^1].Name;
        IEnumerable<string> raw;
        if (last.Signal != null && _rail.Signals.TryGetValue(last.Signal, out var sig)) raw = sig.Next;
        else if (t != null && last.Signal == null)
        {
            // 出した番線から: その番線の出発の進路の信号機
            var st = _rail.Stations.Values.FirstOrDefault(s => s.Name == t.StartStation);
            raw = st != null && st.Routes[t.Dir].Depart.TryGetValue(t.StartTrack, out var deps) ? deps.Take(1).Select(d => d.Signal) : [];
        }
        else raw = [];

        var list = raw.Where(n => n.Length > 0).Distinct().ToList();
        // 進路が何本もある信号機の「次」は、行き先の番線ごとの出発が並ぶ。今の区間の最後の軌道回路につながるものだけにする
        var near = list.Where(n => FirstCircuits(n).Any(c => c == lastCircuit || (_adj.TryGetValue(lastCircuit, out var a) && a.Contains(c)))).ToList();
        return near.Count > 0 ? near : list;
    }

    private IEnumerable<string> FirstCircuits(string signal)
    {
        if (_routesBySignal.TryGetValue(signal, out var defs)) return defs.Where(d => d.Circuits.Count > 0).Select(d => d.Circuits[0]);
        return _rail.Signals.TryGetValue(signal, out var s) ? s.Protects : [];
    }

    private bool Adjacent(string from, IReadOnlyList<string> circuits) =>
        circuits.Count > 0 && (circuits.Contains(from) || (_adj.TryGetValue(from, out var a) && circuits.Any(a.Contains)));

    /// <summary>軌道回路を、from につながる順に並べる（つながりが分からなければ、書いてある順）。</summary>
    /// <summary>
    /// 進路の軌道回路のうち、列車が実際に通るものを、通る順に。入る所（from につながる回路）から出る所までを、回路のつながりでたどる。
    /// 出る所は、行き先が番線ならその番線の回路、そうでなければ次の信号機の回路につながる回路。
    /// 進路のデータには、列車が通らない回路が入っていることがある（水越の 12R・12L の 21ロT）。
    /// それも在線にすると、その回路を守る信号機（水越の 13L）が停止になり、ほかの列車が動けなくなる。
    /// たどれなければ、全部をつながる順に並べる（<see cref="Order"/>）。
    /// </summary>
    private List<string> RoutePath(RouteDef def, string from)
    {
        var set = def.Circuits.Where(c => c != from).Distinct().ToHashSet();
        if (set.Count == 0) return new List<string>();
        bool Near(string a, string b) => _adj.TryGetValue(a, out var n) && n.Contains(b);

        // 出る所
        var targets = new HashSet<string>();
        var m = System.Text.RegularExpressions.Regex.Match(def.To, @"^(\d+)番線$");
        if (m.Success && _rail.Stations.TryGetValue(def.StationId, out var st) && st.TrackCircuit.TryGetValue(int.Parse(m.Groups[1].Value), out var tc) && set.Contains(tc))
            targets.Add(tc);
        else if (ToCircuit(def, set) is { } toCircuit)
            // 行き先が軌道回路（大道寺の 1RC は「42ﾛT」）: その回路まで。1RB と同じ信号機で、41T の先に 3R も 4R もあるので、
            // 次の信号機の手前の回路で探すと、近い 41T（3R の手前）で切れて、開いていない 3R で止まってしまう
            targets.Add(toCircuit);
        else if (_rail.Signals.TryGetValue(def.Signal, out var sig))
        {
            var after = sig.Next.SelectMany(FirstCircuits).Where(c => !set.Contains(c)).ToHashSet();
            foreach (var c in set)
                if (after.Any(a => Near(c, a))) targets.Add(c);
        }

        // 入る所から出る所まで（いちばん短い道）
        var starts = set.Where(c => Near(from, c)).ToList();
        if (targets.Count > 0 && starts.Count > 0)
        {
            var prev = new Dictionary<string, string?>();
            var q = new Queue<string>();
            foreach (var c in starts) { prev[c] = null; q.Enqueue(c); }
            while (q.Count > 0)
            {
                var c = q.Dequeue();
                if (targets.Contains(c))
                {
                    var path = new List<string>();
                    for (string? x = c; x != null; x = prev[x]) path.Add(x);
                    path.Reverse();
                    return path;
                }

                foreach (var n in set.Where(n => !prev.ContainsKey(n) && Near(c, n)))
                {
                    prev[n] = c;
                    q.Enqueue(n);
                }
            }
        }

        return Order(def.Circuits, from);
    }

    /// <summary>進路の行き先が軌道回路の名前なら、その回路（データには半角の「ﾛ」もあるので、全角にそろえて比べる）。</summary>
    private static string? ToCircuit(RouteDef def, HashSet<string> set)
    {
        var to = def.To.Normalize(System.Text.NormalizationForm.FormKC);
        if (to.Length == 0) return null;
        return set.FirstOrDefault(c => c == to || c.EndsWith("_" + to, StringComparison.Ordinal));
    }

    private List<string> Order(IReadOnlyList<string> circuits, string from)
    {
        var rest = circuits.Where(c => c != from).Distinct().ToList();
        var result = new List<string>();
        var prev = from;
        while (rest.Count > 0)
        {
            var n = rest.FirstOrDefault(c => _adj.TryGetValue(prev, out var a) && a.Contains(c)) ?? rest[0];
            result.Add(n);
            rest.Remove(n);
            prev = n;
        }

        return result;
    }

    /// <summary>
    /// 次の停車駅の停止位置（道筋の上で分かれば）。
    /// ・番線ごとの表（AiTrains.json の stops）にあれば、その信号機からの距離
    /// ・表にない番線・駅は、とりあえず列車が軌道回路の中に収まる位置: 進路を扱う駅はその番線の軌道回路、
    ///   進路を扱わない駅はその駅の区間の軌道回路の、終わりの 5m 手前（最後尾が分岐などにはみ出さないように）
    /// </summary>
    private double? StopX(Train t, PlanStop p)
    {
        var st = _rail.Stations.TryGetValue(p.StationId, out var sd) ? sd : null;
        // 番線: 道筋に入っているその駅の番線の回路から（自動進路構成が番線を変えていれば、そちら）。なければダイヤの番線
        int? track = p.Track;
        SegCircuit? platform = null;
        if (st != null)
            foreach (var c in t.Path.SelectMany(s => s.Circuits))
            {
                // 先頭がもう終わりまで来ている回路は除く（引上線で折り返した直後は、今いる引上線の回路も「番線の回路」なので）
                if (c.End <= t.Head + 1) continue;
                var k = st.TrackCircuit.FirstOrDefault(kv => kv.Value == c.Name);
                if (k.Value == null) continue;
                (track, platform) = (k.Key, c);
                break;
            }

        var spec = st != null && st.RouteControl
            ? _stops.FirstOrDefault(s => s.Station == p.Name && s.Dir == t.Dir && s.Track == (track ?? -1))
            : FindStop(p.Name, t.Dir, 0);
        const double Margin = 5;
        if (spec != null && BoundaryOf(t, spec.Signal) is { } b)
        {
            // 連結: 番線に増結車がいれば、停B（停止位置目標のない駅は連結位置の4m手前）、そのあと連結位置（増結車の後ろ）
            if (CouplePartnerAt(t, p) != null && CouplePos(t, p) is { } cp)
                return t.CoupleStage >= 2 ? b - cp.Couple : b - cp.StopB;
            // 両数に合った停目（4両停目など）は、表の位置（6両停目）から手前
            var x = (spec.After is { } after ? b + after : b - spec.Before)
                    - MarkerOffset(t, p.Name, t.Dir, spec.Track, t.Plan.Count > 0 && t.Plan[^1].StationId == p.StationId);
            // 表の位置だと最後尾が番線の回路の手前（分岐の回路）に残るなら、番線の回路に収まるところまで前へ出す（番線の回路の終わりは越えない）。
            // 残したままだと、分岐を通るほかの進路（日野森で 2番線に着いた上りの最後尾が 22T に残り、3番線の下りの 4L が出られなかった）をふさぐ
            if (platform != null && x < platform.End && x - t.Length < platform.Start + Margin)
                x = Math.Min(platform.Start + t.Length + Margin, platform.End - 1);
            return x;
        }

        // 表にない: 軌道回路の中に収める。出発信号機が停止なら、その手前（止まれる所）を越えない
        // （越えると、信号機の手前で止まったまま着いたことにならない。赤山町の下り 1番線に着く 2351 が、3L の手前で止まり続けた）
        if (platform != null) return Math.Min(Math.Max(platform.Start + 1, platform.End - Margin), t.Blocked ? t.PathEnd - SignalMargin : double.MaxValue);
        if (st == null || !st.RouteControl)
        {
            var zone = _rail.Line.ZoneOfStation(p.StationId);
            var inZone = t.Path.SelectMany(s => s.Circuits).Where(c => c.End > t.Head - 1 && _rail.Line.ZoneOfCircuit(c.Name) == zone).ToList();
            // その駅の区間のうち、いちばん手前から続いている回路の終わり
            if (inZone.Count > 0)
            {
                var last = inZone[0];
                var all = t.Path.SelectMany(s => s.Circuits).ToList();
                for (var i = all.IndexOf(last) + 1; i < all.Count && _rail.Line.ZoneOfCircuit(all[i].Name) == zone; i++) last = all[i];
                // 道筋がまだ駅の区間の先まで延びていなければ（先の信号機が停止）、決めずに待つ（その手前では信号で止まる）
                var idx = all.IndexOf(last);
                if (idx < all.Count - 1 || !t.Blocked) return Math.Max(last.Start + 1, last.End - Margin);
            }
        }

        // 出発信号機の手前で止まっている（番線の回路が分からない）: 20m 手前
        if (t.Blocked && t.EndSignal is { } e && e.StartsWith(p.Name) && e.Contains("出発")) return t.PathEnd - 20;
        return null;
    }

    /// <summary>連結: その駅の番線で止まって待っている増結車の列番（RouteEngine が見つけたもの）。いなければ null。</summary>
    private string? CouplePartnerAt(Train t, PlanStop p) =>
        _couplePartners.TryGetValue(t.Id, out var m) && m.TryGetValue(p.StationId, out var partner) ? partner : null;

    /// <summary>
    /// 連結位置と、その前に一旦止まる位置（信号機の手前 m）。AiTrains.json の stops に couple があればそれ（大道寺: 併B・停B の停止位置目標がある）。
    /// 書いていなければ（臨時増結の新野崎など。併A・併B の停止位置目標はなく、駅係員の停止位置合図に合わせて止まる）、
    /// その番線の停止位置から増結車の長さぶん手前を連結位置とし（大道寺の測った値と同じ考え方）、その 4m 手前で一旦止まる。停止位置の表がなければ null。
    /// Marker は、停止位置目標（停B）がある駅か
    /// </summary>
    private (double Couple, double StopB, bool Marker)? CouplePos(Train t, PlanStop p)
    {
        var spec = _stops.FirstOrDefault(s => s.Station == p.Name && s.Dir == t.Dir && s.Track == (p.Track ?? -1));
        if (spec == null) return null;
        if (spec.CoupleBefore is { } cb) return (cb, spec.StopBBefore ?? cb, true);
        if (spec.After != null || CouplePartnerAt(t, p) is not { } partner) return null;
        var cars = _trains.Values.FirstOrDefault(x => x.Number == partner)?.Cars ?? CarsOf(partner);
        var c = spec.Before + cars * CarLength;
        return (c, c + 4, false);
    }

    /// <summary>
    /// 連結位置に着いた: 増結車（自動運転）を消し、両数を足す。先頭は増結車の先頭まで進める（連結位置は増結車の最後尾）。
    /// 増結車がユーザーの列車なら消せないので、両数だけ足す（ユーザーが運転を終えれば在線も消える）。
    /// </summary>
    private void CoupleWith(Train t, PlanStop p)
    {
        t.CoupleStage = 3;
        if (CouplePartnerAt(t, p) is not { } partner) return;
        var cars = CarsOf(partner);
        foreach (var pt in _trains.Values.Where(x => x.Number == partner).ToList())
        {
            cars = pt.Cars;
            _trains.Remove(pt.Id);
        }
        t.Cars += cars;
        t.Length = t.Cars * CarLength;
        t.Head = Math.Min(t.Head + cars * CarLength, t.PathEnd - 1);
        _log.Note($"にぎやかし: {t.Number} に {partner}（{cars}両）を連結しました（{p.Name}。{t.Cars}両）");
    }

    /// <summary>
    /// 解放: ダイヤに「解」＋この列番の列車があり、その最初の駅に着いたら、両数を減らし、切り離した車両を出す
    /// （出すのは周期の終わりに、自分のロックの外で）。
    /// </summary>
    private void SplitAt(Train t, PlanStop p)
    {
        if (_rail.DiaOf("解" + t.Number) is not { } child || child.Stops.FirstOrDefault(s => s.StationId != null) is not { } cs
            || cs.StationId != p.StationId || _trains.Values.Any(x => x.Number == child.Number) || _userTrains.Contains(child.Number)) return;
        var cars = CarsOf(child.Number);
        // 臨時列車で両数を決めた列車は、決めた両数から切り離した分を引く（運行番号の既定の両数で下を止めると、減らなくなる）
        t.Cars = t.Dia.Cars != null ? Math.Max(1, t.Cars - cars) : Math.Max(CarsOf(t.Number), t.Cars - cars);
        t.Length = t.Cars * CarLength;
        var split = (child.Number, cars, p.Name, cs.Track ?? p.Track ?? -1, t.Number);
        // 元の列車がこの駅から先へ進むなら、番線から出たあと、その跡地に出す（終わって折り返すなら、切り離した車両が先に出るので、すぐ）
        var goesOn = t.Plan.Skip(t.Plan.IndexOf(p) + 1).Any() || t.Dia.Stops.LastOrDefault()?.StationId != p.StationId;
        if (goesOn && _rail.Stations.TryGetValue(p.StationId, out var sd) && sd.TrackCircuit.TryGetValue(split.Item4, out var circuit))
            _splitsAfterLeave.Add((split, t.Id, circuit));
        else _pendingSplits.Add(split);
    }

    private double? BoundaryOf(Train t, string signal)
    {
        foreach (var s in t.Path)
            if (s.Signal == signal) return s.Start;
        if (t.EndSignal == signal || (t.Blocked && t.EndSignal != null && SamePlace(t.EndSignal, signal))) return t.PathEnd;
        // 並んで建つ信号機（同じ位置）
        foreach (var s in t.Path)
            if (s.Signal != null && SamePlace(s.Signal, signal)) return s.Start;
        return null;
    }

    private bool SamePlace(string a, string b) => a == b || (Distance(a, b) is { } d && Math.Abs(d) < 3) || (Distance(b, a) is { } e && Math.Abs(e) < 3);

    /// <summary>次の停車駅までの距離の見込み（最後に過ぎた信号機から、走ったときの記録で）。</summary>
    private double? EstimateDistance(Train t, PlanStop p)
    {
        var spec = FindStop(p.Name, t.Dir, p.Track ?? 0) ?? _stops.FirstOrDefault(s => s.Station == p.Name && s.Dir == t.Dir);
        if (spec == null) return null;
        var passed = t.Path.LastOrDefault(s => s.Signal != null && s.Start <= t.Head);
        if (passed?.Signal is not { } from) return null;
        if (ChainDistance(from, spec.Signal) is not { } d) return null;
        return d - (t.Head - passed.Start) + (spec.After ?? -spec.Before)
               - MarkerOffset(t, p.Name, t.Dir, spec.Track, t.Plan.Count > 0 && t.Plan[^1].StationId == p.StationId);
    }

    /// <summary>
    /// 信号機 a から b までの距離。1本の記録に両方がなくても、記録どうしを同じ信号機でつないで引く
    /// （館浜から大道寺1番のように、1本の記録では届かない所。届かないと速さが決め打ちの 75km/h になる）。
    /// 記録ごとに、信号機を位置の順に並べた隣どうしを道としてつなぎ、いちばん短い道のりを取る。
    /// </summary>
    private double? ChainDistance(string a, string b)
    {
        if (a == b) return 0;
        lock (_chainCache)
        {
            if (_chainCache.TryGetValue((a, b), out var cached)) return cached;
            if (_chainEdges == null)
            {
                _chainEdges = new Dictionary<string, List<(string To, double D)>>();
                foreach (var run in _signalRuns)
                {
                    var order = run.OrderBy(x => x.Value).ToList();
                    for (var k = 0; k + 1 < order.Count; k++)
                    {
                        if (!_chainEdges.TryGetValue(order[k].Key, out var list)) _chainEdges[order[k].Key] = list = new();
                        list.Add((order[k + 1].Key, order[k + 1].Value - order[k].Value));
                    }
                }
            }

            double? result = null;
            var best = new Dictionary<string, double> { [a] = 0 };
            var queue = new PriorityQueue<string, double>();
            queue.Enqueue(a, 0);
            while (queue.TryDequeue(out var s, out var d))
            {
                if (d > best[s]) continue;
                if (s == b) { result = d; break; }
                if (!_chainEdges.TryGetValue(s, out var list)) continue;
                foreach (var (to, len) in list)
                    if (!best.TryGetValue(to, out var old) || d + len < old)
                    {
                        best[to] = d + len;
                        queue.Enqueue(to, d + len);
                    }
            }
            _chainCache[(a, b)] = result;
            return result;
        }
    }

    private bool Passed(Train t, PlanStop p)
    {
        var zone = _rail.Line.ZoneOfStation(p.StationId);
        if (zone < 0) return true;
        var rear = t.Info.Zones.Count == 0 ? (int?)null : t.Dir == Dir.Down ? t.Info.Zones.Min() : t.Info.Zones.Max();
        return rear is { } r && (r - zone) * t.Dir.Step() > 0;
    }

    /// <summary>
    /// 速さの上限（m/s）。
    /// ・区間の最高速度: 館浜〜新野崎 110km/h、新野崎〜大路（赤山町方）100km/h
    /// ・線路の制限: 道筋の信号機を目印に、ゲームの自動運転で走ったときの記録の位置へ置き換えて、今いる所（最後尾まで）の制限と、
    ///   この先 1500m の制限の手前で減速するための速さ。記録の制限は、前の信号機が G の間の値だけ（信号の現示による制限は除いてある）
    ///   当てはまる記録が何本もあれば、いちばん高いものを取る（同じ線路なら制限は同じはず。低い記録は、信号などで抑えられた値が残ったもの。
    ///   羽衣橋〜江ノ原で、ある記録だけ 55km/h が続いていた）。記録の走った範囲の外は使わない
    /// ・分岐: 記録が当てはまらない駅の進路（記録の走りと違う番線に入る・出る、記録がない）は 40km/h
    /// </summary>
    private double LimitCap(Train t)
    {
        var cap = SectionMax(t);
        const double Turnout = 40 / 3.6;

        // 目印の信号機: 過ぎた信号機のうちいちばん新しいもの、なければ前の信号機
        Seg? refSeg = t.Path.LastOrDefault(s => s.Signal != null && s.Start <= t.Head) ?? t.Path.FirstOrDefault(s => s.Signal != null);
        var refSignal = refSeg?.Signal ?? t.EndSignal;
        var refX = refSeg?.Start ?? t.PathEnd;
        // 使う記録: 目印の信号機がある記録のうち、この先 1500m の駅の進路（番線）に当てはまるもの。なければ目印の信号機がある記録すべて
        var routesAhead = t.Path.Where(s => s.Route != null && s.End > t.Head - t.Length && s.Start <= t.Head + 1500).Select(s => s.Route!).ToList();
        var candidates = refSignal == null ? new List<int>()
            : Enumerable.Range(0, _signalRuns.Count).Where(i => _signalRuns[i].ContainsKey(refSignal) && _limitRuns[i].Count > 0).ToList();
        var covering = candidates.Where(i => routesAhead.All(d => RecordCovers(i, d, t))).ToList();
        var use = covering.Count > 0 ? covering : candidates;

        double? recordCap = null;
        foreach (var i in use)
        {
            var at = _signalRuns[i][refSignal!];
            var lims = _limitRuns[i];
            var p0 = at + (t.Head - refX);
            var end = Math.Max(_signalRuns[i].Values.Max(), lims[^1].Pos) + 300; // 記録の走った範囲の終わり（最後の信号機・制限の少し先まで）
            if (p0 > end) continue;
            double LimitAt(double p)
            {
                var v = double.PositiveInfinity;
                foreach (var (pos, kmh) in lims)
                    if (pos <= p) v = kmh; else break;
                return v;
            }

            // 今の制限: 先頭から最後尾までのうち、いちばん低いもの（上がるのは最後尾が抜けてから）
            var now = LimitAt(p0);
            foreach (var (pos, kmh) in lims)
                if (pos > p0 - t.Length && pos <= p0) now = Math.Min(now, kmh);
            now = Math.Min(now, LimitAt(p0 - t.Length));
            var c = now / 3.6;
            foreach (var (pos, kmh) in lims)
            {
                if (pos <= p0) continue;
                if (pos > p0 + 1500) break;
                var l = kmh / 3.6;
                c = Math.Min(c, Math.Sqrt(l * l + 2 * Decel * (pos - p0)));
            }
            recordCap = recordCap is { } rc ? Math.Max(rc, c) : c;
        }
        if (recordCap is { } r) cap = Math.Min(cap, r);

        // 分岐: 記録が当てはまらない駅の進路は 40km/h（列車がその進路にいる間と、その手前からの減速）
        foreach (var s in t.Path)
        {
            // 進路に駅間の回路（三郷の 12L の AS2T など。名前が TH で始まらない）が入っていれば、40km/h は駅の回路の終わりまで
            var end = s.Circuits.LastOrDefault(c => c.Name.StartsWith("TH"))?.End ?? s.End;
            if (s.Route is not { } def || end <= t.Head - t.Length || s.Start > t.Head + 1500) continue;
            // 入換の進路はいつも 25km/h
            var shunt = def.Kind.Contains("入換") || def.Category.Contains("入換");
            if (!shunt && use.Any(j => RecordCovers(j, def, t))) continue;
            var lim = shunt ? ShuntSpeed : Turnout;
            cap = s.Start <= t.Head ? Math.Min(cap, lim) : Math.Min(cap, Math.Sqrt(lim * lim + 2 * Decel * (s.Start - t.Head)));
        }

        return cap;
    }

    /// <summary>区間の最高速度（m/s）。新野崎（TH67）から館浜方は 110km/h、それより赤山町方は 100km/h。</summary>
    private double SectionMax(Train t)
    {
        var z = _rail.Line.ZoneOfStation("TH67");
        return z >= 0 && t.Info.Zones.Count > 0 && t.Info.Zones.Min() >= z ? 110 / 3.6 : 100 / 3.6;
    }

    /// <summary>
    /// 走ったときの記録（run）の制限が、その駅の進路に当てはまるか。記録の走りがその駅に止まっていれば、同じ番線の進路だけ。
    /// 記録の走りが通過した駅（番線の記録がない）は、こちらも通過するときだけ（どちらも本線を通ったと見なす）。
    /// 記録の走りがその駅まで来ていなければ当てはまらない。
    /// </summary>
    private bool RecordCovers(int run, RouteDef def, Train t)
    {
        if (!_rail.Stations.TryGetValue(def.StationId, out var st)) return false;
        var m = System.Text.RegularExpressions.Regex.Match(def.To + " " + def.From, @"(\d+)番線");
        int? track = m.Success ? int.Parse(m.Groups[1].Value) : null;
        // 記録の走りがその駅まで来ていない（駅の信号機が載っていない）なら当てはまらない
        if (!_signalRuns[run].Keys.Any(k => k.StartsWith(st.Name, StringComparison.Ordinal))) return false;
        // 番線のない進路（江ノ原信号場の本線の通過など）は、記録の走りが番線に入った（江ノ原車庫へ入った）なら当てはまらない
        if (_trackRuns[run].TryGetValue(st.Name, out var rt)) return track == rt;
        return !t.Plan.Any(p => p.Name == st.Name && p.Stops);
    }

    // =========================================================================================
    // 自動運転列車アリ: ダイヤの列車を時刻に合わせて出す・ユーザーの列車
    // =========================================================================================

    /// <summary>
    /// ユーザーの列車の列番: 出勤している人の行路（臨変を含む）の列車と、参加者・車掌が入力している列番。
    /// 自動運転列車アリでも、これらは自動では出さない。
    /// </summary>
    private HashSet<string> UserTrains(List<PlayerInfo> snapshot)
    {
        var set = new HashSet<string>();
        foreach (var p in snapshot)
            if (!PlayerRegistry.IsVirtual(p.ConnectionId) && p.CountedTrainNumber.Length > 0) set.Add(RailData.NormalizeTrain(p.CountedTrainNumber)); // 入換で動かしている人は、残した在線の列番
        foreach (var c in _relay.Conductors())
            if (c.TrainNumber.Length > 0) set.Add(RailData.NormalizeTrain(c.TrainNumber));
        var roster = _duties.Current;
        foreach (var duty in _crew.AttendedDuties())
        {
            if (roster.DutyById(duty) is not { } d) continue;
            foreach (var item in d.Items)
                if (item.Train.Length > 0) set.Add(RailData.NormalizeTrain(item.Train));
        }

        return set;
    }

    private bool IsUserTrain(string number) => _userTrains.Contains(RailData.NormalizeTrain(number)) || _userInGame.Contains(RailData.NormalizeTrain(number));

    /// <summary>続けて走る列番がユーザーの列車なら、着いたところで消す（ユーザーがその列番で出てくる）。</summary>
    private void HandOver(Train t, string number)
    {
        t.FinishedUtc = DateTime.UtcNow - TerminalLinger;
        t.Status = $"{t.Plan[t.DwellIndex].Name} に到着（{number} はユーザーの列車なので、ここで消します）";
    }

    // =========================================================================================
    // 継続乗務の在線の引き継ぎ（ダイヤ運転中、ユーザーが運転画面を出たあと）
    // =========================================================================================

    /// <summary>ダイヤ運転中、継続乗務の在線が3分残ったら、自動運転の列車として引き継ぐ（自動運転列車のアリ/ナシによらない）。</summary>
    private void TakeOverStaleGhosts()
    {
        if (!_central.DiaMode) return;
        var now = DateTime.UtcNow;
        // 入換で動かすために残した在線（X線入換など）は引き継がない（ユーザーがゲームの中だけで動かしている間）
        foreach (var g in _players.Ghosts().Where(g => g.Info.ShuntMove != ShuntMoveState.Parked && now - g.SinceUtc >= GhostTakeOverAfter))
        {
            if (_players.TakeGhost(g.Id) is not { } taken) continue;
            var (ok, message) = TakeOver(taken);
            if (ok) { _takeOverLogged.Remove(g.Id); continue; }
            _players.RestoreGhost(taken); // 引き継げなければ、在線は残したまま（2秒ごとに試し直す）
            if (_takeOverLogged.Add(g.Id)) _log.Note($"にぎやかし: 継続乗務 {g.Info.TrainNumber} を引き継げません（{message}）");
        }
    }

    /// <summary>ダイヤ運転中、継続乗務の在線が消されたとき、自動運転列車アリなら、自動運転の列車として引き継ぐ。</summary>
    private void OnGhostDeleted(PlayerRegistry.Ghost g)
    {
        if (!_central.DiaMode || !Enabled) return;
        try
        {
            var (ok, message) = TakeOver(g);
            if (!ok) _log.Note($"にぎやかし: 継続乗務 {g.Info.TrainNumber} を引き継げません（{message}）");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "継続乗務 {Train} の引き継ぎでエラー", g.Info.TrainNumber);
        }
    }

    /// <summary>
    /// 継続乗務の在線（レジストリから外したもの）を、自動運転の列車として、その場所から走らせる。進路・駅の状態は RouteEngine が引き継ぐ。
    /// ・進路を扱う駅の番線にいる: その駅・番線に出したのと同じ（ダイヤの発車時刻まで待って発車）
    /// ・それ以外（駅間・進路を扱わない駅など）: 踏んでいる軌道回路から、最後に過ぎた信号機の先として走り出す。
    ///   ダイヤで止まる駅にいれば、そこに止まっていることにする
    /// 両数は分からないので6両にする。
    /// </summary>
    private (bool Ok, string Message) TakeOver(PlayerRegistry.Ghost g)
    {
        var info = g.Info;
        var number = RailData.NormalizeTrain(info.TrainNumber);
        if (_rail.DiaOf(number) is not { } dia) return (false, $"{number}: ダイヤにない列車です");
        if (_routes.DirOfTrain(number) is not { } dir) return (false, $"{number}: 列車番号の形が違います");
        if (info.TrackCircuits.Count == 0) return (false, "在線の位置が分かりません");

        // 進路を扱う駅の番線にいる
        foreach (var st in _rail.Stations.Values.Where(s => s.RouteControl))
        {
            var k = st.TrackCircuit.FirstOrDefault(kv => info.TrackCircuits.Contains(kv.Value));
            if (k.Value == null) continue;
            if (dia.Stops.FirstOrDefault(s => s.StationId == st.Id) is not { } ds) break; // ダイヤで通らない駅（番線を変えて逃がした先など）: 駅間と同じ扱い
            return Add(number, CarsOf(number), ds.Name, true, k.Key, info);
        }

        lock (_lock)
        {
            if (_trains.Values.Any(t => t.Number == number)) return (false, $"{number} はもう走っています");
            var step = dir.Step();
            // 踏んでいる軌道回路を、後ろから前へ
            var sorted = info.TrackCircuits.OrderBy(c => (_rail.Line.ZoneOfCircuit(c) ?? 0) * step).ToList();
            var ordered = new List<string> { sorted[0] };
            ordered.AddRange(Order(sorted.Skip(1).ToList(), sorted[0]));

            // 最後に過ぎた信号機: 前の回路から、その回路を通る自分の進路の信号機、なければその回路を守る同じ向きの信号機
            string? signal = null;
            RouteDef? route = null;
            for (var i = ordered.Count - 1; i >= 0 && signal == null; i--)
            {
                var c = ordered[i];
                foreach (var key in info.GameRoutes)
                    if (_rail.Routes.TryGetValue(key, out var d) && d.Signal.Length > 0 && d.Circuits.Contains(c)) { signal = d.Signal; route = d; break; }
                if (signal != null) break;
                signal = _rail.Signals.Values
                    .Where(s => s.Protects.Contains(c) && !_routesBySignal.ContainsKey(s.Name) && (s.Direction == null || s.Direction == dir))
                    .OrderByDescending(s => s.Direction == dir).FirstOrDefault()?.Name;
            }

            if (signal == null) return (false, $"{string.Join(" ", info.TrackCircuits)} の手前の信号機が分かりません");

            var train = new Train
            {
                Id = $"{PlayerRegistry.AiPrefix}{++_seq}", Seq = _seq, Number = number, Dia = dia, Dir = dir, Cars = CarsOf(number),
                Length = CarsOf(number) * CarLength, StartStation = "", Auto = true, MidLine = true
            };
            var x = 0.0;
            var circuits = new List<SegCircuit>();
            foreach (var c in ordered)
            {
                var len = LengthOf(c);
                circuits.Add(new SegCircuit(c, x, x + len));
                x += len;
            }

            train.Path.Add(new Seg(signal, route, 0, x, circuits));
            train.Head = x - 1;
            train.StartHead = train.Head;

            // 停車・通過の一覧（今いる所から先）
            var front = info.Zones.Count > 0 ? (dir == Dir.Down ? info.Zones.Max() : info.Zones.Min()) : _rail.Line.ZoneOfCircuit(ordered[^1]) ?? -1;
            foreach (var s in dia.Stops)
            {
                if (s.StationId == null || !InArea(s.StationId)) continue;
                var zone = _rail.Line.ZoneOfStation(s.StationId);
                if (front >= 0 && (zone - front) * step < 0) continue;
                train.Plan.Add(PlanOf(dia, s));
            }

            train.Info = new PlayerInfo
            {
                ConnectionId = train.Id, Mode = ShareMode.Shared, InGame = true, TrainCrewConnected = true, TrainNumber = number,
                TrainNumberTyped = true, JoinedUtc = DateTime.UtcNow, LastReportUtc = DateTime.UtcNow, TakesOver = info.ConnectionId,
                GameRoutes = new HashSet<(string, string)>(info.GameRoutes), // 進路も引き継ぐ（Add と同じ）
                Stops = train.Plan.Select(p => new StopInfo { Name = p.Name, StopType = p.Stops ? "StopForPassenger" : "Passing" }).ToList()
            };

            // ダイヤで止まる駅にいる: そこに止まっていることにする（発車時刻が来て、出発の進路が開いたら発車）
            var here = train.Plan.FindIndex(p => p.Stops && _rail.Line.ZoneOfStation(p.StationId) == front);
            if (here >= 0)
            {
                train.Dwelling = true;
                train.DwellIndex = here;
                train.PlanIndex = here;
                train.ArrivedUtc = DateTime.UtcNow.AddSeconds(-MinDwellSeconds);
            }

            var where = _rail.Line.DescribeZones(info.Zones) ?? string.Join(" ", info.TrackCircuits);
            train.Status = here >= 0 ? $"{train.Plan[here].Name} に停車中" : "停止";
            UpdateInfo(train);
            _trains[train.Id] = train;
            _spawned.Add(number);
            _log.Note($"にぎやかし: 継続乗務 {number} の在線を、自動運転の列車として {where} から走らせます");
            return (true, $"{number} を {where} から走らせます");
        }
    }

    /// <summary>
    /// ダイヤの列車のうち、始発駅（進路を扱う駅で最初の駅）の発車時刻が近い（2分前から）ものを、その駅に出す。
    /// 出さないもの: ユーザーの列車、もう出した・走っている列番、走っている列車がこのあと続けて・折り返して走る列番。
    /// 番線にほかの列車がいるなどで出せなければ、始発駅で2秒ごとに試し直す（発車時刻から SpawnLateLimit まで。先の駅には出さない）。
    /// 司令員がダイヤグラムの◆で入れた指示に従う: 発車時刻を決めていればその時刻に合わせて出し、抑止していれば外すまで出さない、番線を決めていればその番線に出す。
    /// これから出す列車の一覧（Pending）も、ここで作り直す（ダイヤグラムに出現前の列車として出す）。
    /// </summary>
    private void AutoSpawn(HashSet<string> users, List<PlayerInfo> snapshot)
    {
        var now = _clock.Now;
        List<(string Number, string Station, int? Track)> todo = new();

        // ユーザーが運転している列車（継続乗務で残した在線も）の運行番号と、その列車の最初の時刻。
        // 同じ運行番号で、それより後に走る列車は出さない（ユーザーの列車が遅れていても、その後の列番はユーザーが続けて走る。回1750 が遅れているのに 1751 が出た）
        var userUnyo = new Dictionary<string, TimeSpan>();
        foreach (var p in snapshot.Where(p => !p.ConnectionId.StartsWith(PlayerRegistry.AiPrefix) && p.TrainNumber.Length > 0))
        {
            if (_rail.DiaOf(p.TrainNumber) is not { Unyo: { } u } ud || ud.Stops.Select(s => Time(s.Arr ?? s.Dep)).FirstOrDefault(t => t != null) is not { } first) continue;
            userUnyo[u] = userUnyo.TryGetValue(u, out var old) && old < first ? old : first;
        }
        lock (_lock)
        {
            if (!_enabled || now is null) _pending = [];
            if (!_enabled || now is not { } n || !_clock.IsRunning) return;
            // ロープレ時刻を戻したら、出した記録を消す（もう一度出せるように）
            if (_lastGame is { } lg && n < lg - TimeSpan.FromMinutes(2)) { _spawned.Clear(); _heldSeen.Clear(); }
            // アリにした直後と、ロープレ時刻を飛ばした（2分より大きく進めた・戻した）ときだけ、途中を走っているはずの列車を途中の駅から出す
            if (_catchUpNext || _lastGame is { } lg2 && (n < lg2 - TimeSpan.FromMinutes(2) || n > lg2 + TimeSpan.FromMinutes(2)))
            {
                _catchUpAt = n;
                _catchUpNext = false;
            }
            _lastGame = n;

            // 走っている列車が、このあと続けて・折り返して走る列番
            // 切り離す列車（解874 など）と、その先の列番（回933）も、元の列車（874）が走っていれば出さない（切り離したときに出る）
            var covered = new HashSet<string>();
            foreach (var t in _trains.Values) CoverChain(t.Dia, covered);
            // ユーザーの列車が切り離す車両も（ユーザーの列車の折り返しは、今までどおり自動で出す）
            foreach (var u in users)
                if (_rail.DiaOf("解" + u) is { } usplit && covered.Add(usplit.Number)) CoverChain(usplit, covered);

            var pending = new List<PendingTrain>();
            var runningNumbers = _trains.Values.Select(t => t.Number).ToHashSet();
            foreach (var (dia, number, startDir, cands, firstDep) in SpawnPlans())
            {
                if (_spawned.Contains(number) || users.Contains(number) || covered.Contains(number) || runningNumbers.Contains(number)) continue;
                if (Rinji?.IsConversion(number) == true) continue; // 特発臨は出さない（元の列車が発駅で列番を変えて走る。出庫の特発臨は、ふつうの出庫と同じく出す）
                if (_rail.IsIdleTrial(dia)) continue; // 選ばれていない運行番号の試運転は走らない
                // 出す駅は始発駅（線区の外から来る列車は、線区に入る最初の駅）だけ。終着と、番線を使わずに通過する駅には出さない。
                // 始発駅で出せなければ、出せるまでそこで待つ（遅れた列車として出す）。先の駅に飛ばして出すと、単線区間を越えて急に現れ、
                // ほかの列車との順番が崩れて詰まる。
                // アリにした・ロープレ時刻を飛ばした時刻（_catchUpAt）に、もう始発駅を出ているはずの列車だけは、そのとき走っているはずの所の次の駅を始発駅とみなす
                // （始発駅の候補と最初の発車時刻は、ダイヤが変わるまで変わらないので SpawnPlans で作り置く）
                var start = _catchUpAt is { } cu && firstDep < cu - TimeSpan.FromSeconds(30)
                    ? cands.FirstOrDefault(s => Time(s.Dep ?? s.Arr) is { } t && t >= cu - TimeSpan.FromSeconds(30))
                    : cands[0];
                if (start?.StationId is not { } startId || Time(start.Dep ?? start.Arr) is not { } dep) continue;
                if (dia.Unyo is { } du && userUnyo.TryGetValue(du, out var userFirst) && IsLater(dep, userFirst)) continue; // ユーザーの運行番号のこの先の列車

                // 司令員の指示（ダイヤグラムの◆）は、出現前でも列番で入れておける: 発車時刻（その時刻に合わせて出す）・抑止（外すまで出さない）・番線
                var due = dep;
                if (_central.OpenAt(number, startId) is { } openAt && GameAt(openAt, n) > due) due = GameAt(openAt, n);
                var held = _central.IsHeld(number, startId);
                if (held) _heldSeen[number] = n;
                // 始発駅で長く出せなかった列車は、もう出さない（抑止していた間は数えない）
                var lateFrom = _heldSeen.TryGetValue(number, out var hs) && hs > due ? hs : due;
                if (n - lateFrom > SpawnLateLimit) continue;
                if (due > n + PendingHorizon) continue;

                // 三郷を通過する下り（新大路方から入ってくる）: 通過時刻の20秒前に2番線に出す（早く出すと、通過列車がホームで長く待つ）
                var entryPass = startId == EntryStation && start.Stop != "停" && startDir == Dir.Down;
                var track = _central.TrackOverride(number, startId);
                pending.Add(new PendingTrain(number, startId, track ?? start.Track, track != null, due, held, n > due));
                if (held || due - n > (entryPass ? TimeSpan.FromSeconds(EntryLeadSeconds) : TimeSpan.FromMinutes(2))) continue;
                todo.Add((number, start.Name, track));
            }
            // 出現前の列車が、このあと続けて・折り返して走る列番は、出さない（その列車が出れば、走っている列車の続きとして covered に入る）。
            // 出現前の 回1680 の折り返しの 1781C を別の列車として一覧に出すと、ダイヤグラムで 回1680 → 1781C のつなぎが切れ、
            // 回1680 が出るのが遅れれば 1781C が先に別に出てしまう
            var chained = new HashSet<string>();
            foreach (var p in pending)
            {
                var c = new HashSet<string>();
                CoverChain(_rail.DiaOf(p.Number), c);
                c.Remove(p.Number);
                chained.UnionWith(c);
            }
            if (chained.Count > 0)
            {
                pending.RemoveAll(p => chained.Contains(p.Number));
                todo.RemoveAll(x => chained.Contains(x.Number));
            }
            _pending = pending;
        }

        foreach (var (number, station, track) in todo) Add(number, CarsOf(number), station, true, track);
    }

    // ダイヤの列車ごとの、出す駅の候補（進路を扱う駅のうち、終着と、番線を使わずに通過する駅を除いたもの）と、最初の候補の発車時刻。
    // 列番・駅の並び・時刻の文字はダイヤが変わるまで同じなので、2秒ごとに全ダイヤを読み直さないよう、ダイヤ（臨時列車の足し外し）が
    // 変わったときだけ作り直す。出せない列車（進路を扱う駅が2つない・候補がない・時刻が書かれていない）は入れない
    private (Dictionary<string, DiaTrain>? Dia, List<(DiaTrain Dia, string Number, Dir? StartDir, List<DiaStop> Cands, TimeSpan FirstDep)> List) _spawnPlans = (null, new());

    private List<(DiaTrain Dia, string Number, Dir? StartDir, List<DiaStop> Cands, TimeSpan FirstDep)> SpawnPlans()
    {
        var all = _rail.Dia;
        if (ReferenceEquals(_spawnPlans.Dia, all)) return _spawnPlans.List;
        var list = new List<(DiaTrain, string, Dir?, List<DiaStop>, TimeSpan)>();
        foreach (var dia in all.Values)
        {
            var number = RailData.NormalizeTrain(dia.Number);
            var stops = dia.Stops.Where(s => s.StationId != null && _rail.Stations.TryGetValue(s.StationId, out var sd) && sd.RouteControl).ToList();
            if (stops.Count < 2) continue;
            var startDir = RouteEngine.TrainInfo(number)?.Dir; // 列車の向きは列番で決まる（走っている列車の向きも同じ）
            var cands = stops.Take(stops.Count - 1).Where(s => !(startDir is { } sd2 && PassesWithoutTrack(s, sd2))).ToList();
            if (cands.Count == 0 || Time(cands[0].Dep ?? cands[0].Arr) is not { } firstDep) continue;
            list.Add((dia, number, startDir, cands, firstDep));
        }
        _spawnPlans = (all, list);
        return list;
    }

    /// <summary>最後尾が抜けた進路は、ゲームと同じく解く（道筋からも外す）。</summary>
    private void Release(Train t)
    {
        var tail = t.Head - t.Length;
        while (t.Path.Count > 1 && t.Path[0].End < tail - 1)
        {
            if (t.Path[0].Route is { } r) t.Info.GameRoutes.Remove((r.Interlock, r.Name));
            t.Path.RemoveAt(0);
        }
    }

    private void UpdateInfo(Train t)
    {
        var tail = t.Head - t.Length;
        var circuits = t.Path.SelectMany(s => s.Circuits).Where(c => c.End > tail && c.Start < t.Head + 0.5).Select(c => c.Name).Distinct().ToList();
        if (circuits.Count == 0) circuits = [t.Path[0].Circuits[^1].Name];
        var p = t.Info;
        p.TrackCircuits = circuits;
        p.Zones = _rail.Line.ZonesOf(circuits);
        if (p.Zones.Count > 0)
        {
            var center = p.Zones.Average();
            if (p.LastCenter is { } lc && Math.Abs(center - lc) > 1e-9) p.Direction = center > lc ? 1 : -1;
            p.LastCenter = center;
            p.ZonesUpdatedUtc = DateTime.UtcNow;
        }

        p.Speed = (float)(t.Speed * 3.6);
        p.ReportedSpeed = p.Speed;
        p.TraveledDistance = (float)(t.Head - t.StartHead);
        // 停車駅までの距離（走った距離で数える）。ゲームの列車と同じく、駅間のどこにいるかをダイヤグラムの線に出すため（RouteEngine の Fraction）。
        // これから行く駅（3つまで）だけ、走ったときの記録から見込み直す。過ぎた駅は、最後に見込んだ値のまま
        var ahead = 0;
        foreach (var ps in t.Plan)
        {
            if (ahead >= 3) break;
            if (Passed(t, ps)) continue;
            ahead++;
            if (EstimateDistance(t, ps) is not { } est) continue;
            if (p.Stops.FirstOrDefault(x => x.Name == ps.Name) is { } si) si.Distance = (float)(t.Head - t.StartHead + est);
        }
        p.LastReportUtc = DateTime.UtcNow;
    }

    // =========================================================================================
    // 距離の表
    // =========================================================================================

    /// <summary>
    /// 自動で出す列車の両数。ダイヤに両数が書いてあれば（運転課の臨時列車）それ。なければ列番の表（Data/TrainCars.csv）、
    /// それもなければダイヤの運行番号から両数の表（Data/UnyoCars.csv）で引く（ない運行は6両）。
    /// </summary>
    private int CarsOf(string number) =>
        _rail.DiaOf(number) is { Cars: { } dc } ? dc : _unyoCars.Of(number, _rail.DiaOf(number)?.Unyo);

    /// <summary>両数の表を読んだときのおかしな所（管理画面に出す）。</summary>
    public IReadOnlyList<string> CarsProblems => _unyoCars.Problems;

    private double LengthOf(string circuit) =>
        _circuitLengths.TryGetValue(circuit, out var l) ? l : circuit.StartsWith("TH") ? DefaultStationCircuit : DefaultLineCircuit;

    /// <summary>信号機 a から b までの距離（同じ走りの記録に両方あれば）。</summary>
    private double? Distance(string a, string b)
    {
        foreach (var run in _signalRuns)
            if (run.TryGetValue(a, out var x) && run.TryGetValue(b, out var y) && y >= x) return y - x;
        return null;
    }

    /// <summary>
    /// 番線を使わずに本線を通過する駅（江ノ原信号場の通過など。ダイヤに番線がなく、駅にそのまま通る進路がある）。
    /// ここには列車を出さない。出すと、通過する列車を行き止まりや出区用の番線（江ノ原信号場の下りなら11番線）に置いて、そこから発車させてしまう
    /// （手前の駅で番線がふさがって出せないまま時刻を過ぎると、次の駅として選ばれる）
    /// </summary>
    private bool PassesWithoutTrack(DiaStop s, Dir dir) =>
        s.Stop == "通" && s.Track == null && s.StationId != null
        && _rail.Stations.TryGetValue(s.StationId, out var st) && st.Routes[dir].Through.Count > 0;

    /// <summary>番線ごとの6両停目（AiTrains.json の stops。信号機の Before m 手前、After があれば After m 先）。停目までの距離の表（/api/stop-targets）に使う。</summary>
    public IEnumerable<(string Station, Dir Dir, int Track, string Signal, double Before, double? After)> StopSignals() =>
        _stops.Select(s => (s.Station, s.Dir, s.Track, s.Signal, s.Before, s.After));

    /// <summary>
    /// その駅・番線に入るときの制限速度の変わり目（ゲームの自動運転でその番線に入った記録から）。Before は6両停目から手前への距離（m。先はマイナス）、
    /// Kmh はそこからの制限。6両停目の 800m 手前から 400m 先まで（最初の1つは 800m 手前で効いている制限）。記録がなければ空。
    /// クライアントの自動運転が、シナリオと違う番線に入るとき、ゲームの制限（シナリオの番線の進路のもの）の代わりに使う。
    /// </summary>
    public List<(double Before, double Kmh)> ApproachLimits(string station, Dir dir, int track, string signal, double before, double? after)
    {
        for (var i = 0; i < _signalRuns.Count; i++)
        {
            if (!_trackRuns[i].TryGetValue(station, out var tr) || tr != track || !_signalRuns[i].TryGetValue(signal, out var at) || _limitRuns[i].Count == 0) continue;
            var marker = after is { } a ? at + a : at - before;
            var lims = _limitRuns[i];
            var result = new List<(double, double)>();
            var from = marker - 800;
            var before0 = lims.LastOrDefault(l => l.Pos <= from);
            if (before0.Kmh > 0) result.Add((800, before0.Kmh));
            foreach (var (p, kmh) in lims)
                if (p > from && p <= marker + 400) result.Add((Math.Round(marker - p, 1), kmh));
            if (result.Count > 0) return result;
        }
        return new List<(double, double)>();
    }

    private StopSpec? FindStop(string station, Dir dir, int track) =>
        _stops.FirstOrDefault(s => s.Station == station && s.Dir == dir && s.Track == track)
        ?? (track == 0 ? _stops.FirstOrDefault(s => s.Station == station && s.Dir == dir) : null);

    /// <summary>
    /// 両数に合った停目が、停止位置の表（AiTrains.json の stops。6両の列車で測ったので6両停目）から何 m 手前にあるか（停止位置目標の表から）。
    /// 表にない駅・番線、その両数の停目がない（6両停目に止まる）ときは 0。
    /// 増結車がその駅で連結を待つときも 0（いちばん先の停目に止まり、後ろに列車が来て連結する。大道寺の連結位置は6両停目に止まった増結車で測った）。
    /// station はダイヤの駅名（江原検など）。track は番線（進路を扱わない駅は 0）。
    /// </summary>
    private double MarkerOffset(Train t, string station, Dir dir, int? track, bool terminal)
    {
        if (WaitsForCouple(t, terminal)) return 0;
        return _markers.Choose(dir, station, track is > 0 ? track.Value.ToString(CultureInfo.InvariantCulture) : null, t.Cars)?.Offset ?? 0;
    }

    /// <summary>増結車として連結を待つ列車か（列番が「連」で始まる、または終着で「連」の列番に折り返す。連777・連777 に折り返す 回730）。</summary>
    private bool WaitsForCouple(Train t, bool terminal) =>
        RailData.NormalizeTrain(t.Number).StartsWith('連')
        || (terminal && _rail.Turns.TurnOf(t.Dia) is { Kind: "折返", Number: { } n } && RailData.NormalizeTrain(n).StartsWith('連'));

    private void LoadDistances(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.TryGetProperty("signalRuns", out var runs))
                foreach (var r in runs.EnumerateArray())
                    if (r.TryGetProperty("signals", out var s))
                    {
                        _signalRuns.Add(s.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble()));
                        _limitRuns.Add(r.TryGetProperty("limits", out var lim)
                            ? lim.EnumerateArray().Select(x => (x[0].GetDouble(), x[1].GetDouble())).ToList()
                            : new List<(double, double)>());
                        _trackRuns.Add(r.TryGetProperty("tracks", out var tr)
                            ? tr.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt32())
                            : new Dictionary<string, int>());
                    }
            if (root.TryGetProperty("stops", out var stops))
                foreach (var s in stops.EnumerateArray())
                    _stops.Add(new StopSpec(s.GetProperty("station").GetString()!, s.GetProperty("dir").GetString() == "down" ? Dir.Down : Dir.Up,
                        s.GetProperty("track").GetInt32(), s.GetProperty("signal").GetString()!,
                        s.TryGetProperty("before", out var b) ? b.GetDouble() : 0, s.TryGetProperty("after", out var a) ? a.GetDouble() : null,
                        s.TryGetProperty("couple", out var cp) ? cp.GetDouble() : null, s.TryGetProperty("stopB", out var sb) ? sb.GetDouble() : null));
            if (root.TryGetProperty("circuitLengths", out var lens))
                foreach (var c in lens.EnumerateObject())
                    _circuitLengths[c.Name] = c.Value.GetDouble();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "AiTrains.json を読めませんでした");
        }
    }

    private static Dictionary<string, HashSet<string>> LoadAdjacency(string path)
    {
        var adj = new Dictionary<string, HashSet<string>>();
        if (!File.Exists(path)) return adj;
        using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!doc.RootElement.TryGetProperty("trackCircuits", out var list)) return adj;
        HashSet<string> Of(string n) => adj.TryGetValue(n, out var s) ? s : adj[n] = new HashSet<string>();
        foreach (var tc in list.EnumerateArray())
        {
            var name = tc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (name.Length == 0 || !tc.TryGetProperty("adjacent", out var a)) continue;
            foreach (var x in a.EnumerateArray())
            {
                var other = x.GetString() ?? "";
                if (other.Length == 0) continue;
                Of(name).Add(other);
                Of(other).Add(name);
            }
        }

        return adj;
    }

    /// <summary>a が b と同じか後の時刻か（日をまたぐ所は、12時間以内の差で見る）。</summary>
    /// <summary>時刻の差を ±12時間に収める（0時をまたぐ: 23:46 から見た 00:30 発は 44分後。終夜運転の列車など）。</summary>
    private static TimeSpan Wrap(TimeSpan d)
    {
        while (d > TimeSpan.FromHours(12)) d -= TimeSpan.FromDays(1);
        while (d < TimeSpan.FromHours(-12)) d += TimeSpan.FromDays(1);
        return d;
    }

    private static bool IsLater(TimeSpan a, TimeSpan b)
    {
        var d = (a - b).TotalHours;
        if (d > 12) d -= 24;
        if (d < -12) d += 24;
        return d >= 0;
    }

    private static TimeSpan? Time(string? hms) =>
        hms != null && TimeSpan.TryParseExact(hms, [@"hh\:mm\:ss", @"h\:mm\:ss", @"hh\:mm", @"h\:mm"], CultureInfo.InvariantCulture, out var t) ? t : null;

    /// <summary>
    /// 時刻表の1駅 → 停車・通過の一覧の1駅。
    /// 列車ごとの指示（司令の臨時停車・臨時通過）が先。臨時停車は、着が書いてあれば着（臨時停車用に時刻を変えた列車）、
    /// なければ通過時刻 − 停車時間に着き、通過時刻に出る。打ち切りは <see cref="ApplyCut"/> で扱う。
    /// 指示がなければ、催物用臨停設定で臨時停車になる駅（時刻表では通過）は止まる（EventStops.Times）。
    /// 始発・終着の駅は、臨時通過にしない。
    /// </summary>
    private PlanStop PlanOf(DiaTrain dia, DiaStop s)
    {
        var ov = s.StationId != null ? _central.StopOverride(dia.Number, s.StationId) : null;
        if (ov == "停車" && s.Stop == "通")
        {
            var dep = Time(s.Dep ?? s.Arr);
            return new PlanStop(s.Name, s.StationId!, true, s.Track, Time(s.Arr) ?? dep - TimeSpan.FromSeconds(EventStops.DwellSeconds), dep, dia, s);
        }
        if (ov == "通過" && s != dia.Stops[0] && s != dia.Stops[^1])
            return new PlanStop(s.Name, s.StationId!, false, s.Track, null, Time(s.Dep ?? s.Arr), dia, s);
        return ov == null && _routes.EventStops.Times(dia, dia.Stops.IndexOf(s)) is { } ev
            ? new PlanStop(s.Name, s.StationId!, true, s.Track, Time(ev.Arr), Time(ev.Dep), dia, s)
            : new PlanStop(s.Name, s.StationId!, s.Stop == "停", s.Track, Time(s.Arr), Time(s.Dep), dia, s);
    }

    /// <summary>
    /// 打ち切り（列車別設定の「打ち切り」。途中折返しでも入る）: その駅を終着にし、その先の駅を外す（通過する駅なら止まる駅にする。
    /// もう止まりきれなければ、そのまま）。打ち切りを外したら、外した駅を戻す。
    /// </summary>
    private void ApplyCut(Train t)
    {
        var from = t.Dwelling ? t.DwellIndex : t.PlanIndex;
        if (t.CutStation is { } cs)
        {
            if (_central.StopOverride(t.Number, cs) == "打ち切り") return;
            var ci = t.Plan.FindIndex(p => p.StationId == cs);
            if (ci >= 0 && t.CutRest != null)
            {
                t.Plan[ci] = t.Plan[ci] with { Stops = t.CutWasStop };
                t.Plan.AddRange(t.CutRest);
                _log.Note($"にぎやかし: {t.Number} の {t.Plan[ci].Name} での打ち切りを外しました");
            }
            (t.CutStation, t.CutRest, t.CutWasStop) = (null, null, false);
            t.Info.Stops = t.Plan.Select(p => new StopInfo { Name = p.Name, StopType = p.Stops ? "StopForPassenger" : "Passing" }).ToList();
            return;
        }
        for (var i = Math.Max(0, from); i < t.Plan.Count - 1; i++)
        {
            var p = t.Plan[i];
            if (_central.StopOverride(t.Number, p.StationId) != "打ち切り") continue;
            if (!p.Stops && !(t.Dwelling && i == t.DwellIndex))
            {
                var braking = t.Speed * t.Speed / (2 * Decel) + 10;
                if ((StopX(t, p with { Stops = true }) is { } x ? x - t.Head : EstimateDistance(t, p)) is { } dist && dist < braking) continue;
            }
            t.CutRest = t.Plan.Skip(i + 1).ToList();
            t.CutWasStop = p.Stops;
            t.CutStation = p.StationId;
            t.Plan.RemoveRange(i + 1, t.Plan.Count - i - 1);
            t.Plan[i] = p with { Stops = true };
            if (t.Info != null)
                t.Info.Stops = t.Plan.Select(q => new StopInfo { Name = q.Name, StopType = q.Stops ? "StopForPassenger" : "Passing" }).ToList();
            _log.Note($"にぎやかし: {t.Number} は {p.Name} で打ち切り");
            break;
        }
    }

    /// <summary>
    /// 催物用臨停設定・司令の臨時停車・臨時通過が変わったら、これから通る駅の停車・通過と時刻を作り直す。
    /// 時刻を変えた列車（浜園の特急の臨時停車の時刻など。<see cref="TempStopRetime"/>）は、新しい表の時刻を読む。
    /// 止まっている駅は、停車のまま発車時刻だけ読み直す。
    /// 新しく止まる駅が、今の速さではもう止まりきれない所なら、通過のままにする。
    /// </summary>
    private void RefreshEventStops(Train t)
    {
        var version = (_routes.EventStops.Version, _central.StopVersion, _rail.Turns.Version);
        if (t.PlanVersion == version) return;
        t.PlanVersion = version;
        var changed = false;
        var dia = _rail.Dia;
        for (var i = t.Dwelling ? t.DwellIndex : t.PlanIndex; i >= 0 && i < t.Plan.Count; i++)
        {
            var p = t.Plan[i];
            if (p.Dia is not { } pd || p.Src is not { } src) continue;
            if (dia.GetValueOrDefault(pd.Number) is { } cur && !ReferenceEquals(cur, pd)
                && cur.Stops.FirstOrDefault(x => x.StationId == src.StationId) is { } cs)
                (pd, src) = (cur, cs);
            if (t.Dwelling ? i == t.DwellIndex : i == 0 && t.PlanIndex == 0)
            {
                // 止まっている駅・まだ出ていない最初の駅: 発車時刻だけ（臨時停車の駅でも、書いてある発時刻＝通過時刻に出る）
                t.Plan[i] = p with { Dep = Time(src.Dep) ?? p.Dep, Dia = pd, Src = src };
                continue;
            }
            var np = PlanOf(pd, src);
            if (np == p) continue;
            if (np.Stops && !p.Stops)
            {
                var braking = t.Speed * t.Speed / (2 * Decel) + 10;
                if ((StopX(t, np) is { } x ? x - t.Head : EstimateDistance(t, np)) is { } dist && dist < braking) continue;
            }
            t.Plan[i] = np;
            if (np.Stops == p.Stops) continue;
            changed = true;
            _log.Note($"にぎやかし: {t.Number} {p.Name} を{(np.Stops ? "臨時停車" : "通過")}にしました");
        }
        if (changed && t.Info != null)
            t.Info.Stops = t.Plan.Select(p => new StopInfo { Name = p.Name, StopType = p.Stops ? "StopForPassenger" : "Passing" }).ToList();
    }

    // =========================================================================================
    // 型
    // =========================================================================================

    /// <summary>番線ごとの停止位置。CoupleBefore・StopBBefore は連結する列車の連結位置・停B（信号機の手前 m）。</summary>
    private sealed record StopSpec(string Station, Dir Dir, int Track, string Signal, double Before, double? After, double? CoupleBefore = null, double? StopBBefore = null);
    private sealed record SegCircuit(string Name, double Start, double End);
    private sealed record Seg(string? Signal, RouteDef? Route, double Start, double End, List<SegCircuit> Circuits);
    /// <summary>停車・通過の一覧の1駅。Dia・Src は、時刻表のどの駅から作ったか（催物用臨停設定が変わったら作り直す。出す駅・折り返しの最初の駅は null）。</summary>
    private sealed record PlanStop(string Name, string StationId, bool Stops, int? Track, TimeSpan? Arr, TimeSpan? Dep, DiaTrain? Dia = null, DiaStop? Src = null);

    private sealed class Train
    {
        public required string Id { get; init; }
        public int Seq { get; init; }
        public required string Number { get; set; }
        public required DiaTrain Dia { get; set; }
        public string? Shown { get; set; }
        public Dir Dir { get; set; }
        public int Cars { get; set; }
        public double Length { get; set; }
        public int CoupleStage { get; set; }      // 連結: 0 まだ・1 停B で停止中・2 連結位置へ・3 連結した
        public DateTime CoupleStopUtc { get; set; }
        public required string StartStation { get; set; }
        public int StartTrack { get; set; }
        public PlayerInfo Info { get; set; } = null!;
        public List<Seg> Path { get; } = new();
        public List<PlanStop> Plan { get; } = new();
        public int PlanIndex { get; set; }
        public double Head { get; set; }
        public double StartHead { get; set; }
        public double Speed { get; set; } // m/s
        public double PathEnd => Path[^1].End;
        public bool Blocked { get; set; }
        public string? EndSignal { get; set; }
        public HashSet<string> RedSignals { get; set; } = new();
        /// <summary>信号制御盤から写した進路（Info.GameRoutes のうち、盤の一覧から入れたもの）。</summary>
        public HashSet<(string Interlock, string Route)> PanelRoutes { get; } = new();
        public bool Received { get; set; }
        public HashSet<(int First, int Last)> ReceivedRanges { get; } = new();
        public bool BrakingForProtection { get; set; }
        public bool Dwelling { get; set; }
        public int DwellIndex { get; set; }
        public DateTime ArrivedUtc { get; set; }
        public bool DepartureAsked { get; set; }
        public bool WantDeparture { get; set; }
        public bool CanRequestDeparture { get; set; }
        public string? RouteMessage { get; set; }
        public string? PlanDetail { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public string Status { get; set; } = "";
        public string LoggedStatus { get; set; } = ""; // 状態の記録（logs/ai-*.log）に最後に書いた状態
        public bool Auto { get; init; }
        public DateTime? ExitUtc { get; set; }    // 三郷から線区の外へ出て消す時刻
        public bool Shunting { get; set; }        // 入換（引上げ）中
        public double ShuntStartHead { get; set; }
        public PlanStop? ShuntStation { get; set; }
        public bool ShuntMoved { get; set; }      // 引上線から番線へ、道筋が延びた
        public bool FromShunt { get; set; }       // 引上線から折り返した（ホームへの入換を待つ。発車時刻まで止まらずに番線へ入る）
        public bool StableTurned { get; set; }    // 留置線へ入れる引上げで、引き上げた線（大道寺の X線）で向きを変えた（次に止まった所で列番を変える）
        public string? ShuntOrigin { get; set; }  // 列番を変えた（入換を始める）軌道回路（大道寺の 5番線 TH65_5T）
        public bool MidTurned { get; set; }       // 留置線から番線への入換で、途中の線（X線・Y線）で向きを変えた
        public bool MidLine { get; set; }         // 駅間など、番線でない所から走り出した（継続乗務の在線を引き継いだ）。最初の駅で発車待ちにしない
        public (long, int, int)? PlanVersion { get; set; } // 停車・通過の一覧に入れた催物用臨停設定・司令の臨時停車・ダイヤの版
        public string? CutStation { get; set; }   // 打ち切り（列車別設定・途中折返し）の駅。そこを終着にしている
        public List<PlanStop>? CutRest { get; set; } // 打ち切りで外した、その先の駅（打ち切りを外したら戻す）
        public bool CutWasStop { get; set; }      // 打ち切りの駅は、もとは止まる駅だったか
    }
}
