using Gokko.Common;

namespace Gokko.Server.Services;

/// <summary>抑止の種類（場内・出発。両方なら全部の進路を取らない）。</summary>
[Flags]
public enum HoldKind
{
    None = 0,
    Arrive = 1,
    Depart = 2,
    Both = Arrive | Depart
}

/// <summary>
/// 集中扱い（司令役の指示）。列車扱い（ユーザーの選択・ダイヤ・所定・待避）より優先される。
/// ・番線の指定: 列番＋駅 → 番線（列車扱いの選択や待避より優先）
/// ・抑止: 列番＋駅 → その駅の場内・出発（どちらか、または両方）の進路を開通させない
/// ・開通指示: 列番＋駅 → 手前の駅を通過していなくても、安全の確認ができしだい開通させる（集中扱いの駅では必須）
/// ・駅扱い（強制進路）: 列番＋駅＋「本線→1番線」などの進路 → 連動に問題がなければ、サーバーの抑止・待ちを無視して開通させる
/// ・駅の出発順序: 駅・向き（上り・下り）ごとの列番の並び。進路が競合する列車どうしは、先の列車が駅を抜けるまで出発させない
/// ・単線区間の走行順: 駅間ごとの列番の並び。先の列車が区間を抜けるまで、後の列車をその区間へ出発させない
/// ・駅の扱い: 駅ごとに「列車扱い」「集中扱い」「駅扱い」。集中扱いの駅は、開通指示があるまで自動では開通させない。
///   駅扱いの駅は、信号制御盤（relaypanel.html）で進路を取れる（盤の解放の鍵も R のときだけ）。そのときは自動の進路から切り離し、手動（駅扱いの強制進路）でないと進路を取らない
/// 列番で覚えるので、クライアントがつなぎ直しても引き継がれる。サーバーを再起動すると消える。
/// </summary>
public sealed class CentralControl(GameClock clock)
{
    private readonly object _lock = new();
    private readonly Dictionary<(string Train, string Station), int> _tracks = new();
    private readonly Dictionary<(string Train, string Station), HoldKind> _holds = new();
    private readonly HashSet<(string Train, string Station)> _forces = new();
    private readonly HashSet<string> _centralStations = new();
    private readonly HashSet<string> _localStations = new();
    private readonly Dictionary<(string Train, string Station), DateTime> _openAt = new();
    private readonly Dictionary<(string Station, Dir Dir), List<string>> _orders = new();
    private readonly Dictionary<(string A, string B), List<string>> _sectionOrders = new();
    private readonly Dictionary<(string Train, string Station), string> _manual = new();

    // 順序を最後に変えた順番（出発順序と走行順のどちらが新しいかで、同期の向きを決める）
    private long _stamp;
    private readonly Dictionary<(string Station, Dir Dir), long> _orderStamps = new();
    private readonly Dictionary<(string A, string B), long> _sectionStamps = new();

    public long OrderStamp(string station, Dir dir) { lock (_lock) return _orderStamps.GetValueOrDefault((station, dir)); }
    public long SectionStamp(string a, string b) { lock (_lock) return _sectionStamps.GetValueOrDefault(SectionKey(a, b)); }

    public event Action<string>? Changed;

    // ---- 駅の出発順序 ----

    /// <summary>駅の出発順序（その向きの列車の列番の並び）。上り・下りは別々に持ち、片方だけ自動に戻せる。</summary>
    public IReadOnlyList<string>? Order(string station, Dir dir)
    {
        lock (_lock) return _orders.TryGetValue((station, dir), out var l) ? l.ToList() : null;
    }

    public void SetOrder(string station, Dir dir, IReadOnlyList<string>? trains)
    {
        lock (_lock)
        {
            if (trains is { Count: > 0 }) _orders[(station, dir)] = trains.Distinct().ToList();
            else _orders.Remove((station, dir));
            _orderStamps[(station, dir)] = ++_stamp;
        }

        Changed?.Invoke($"集中扱い: {station} {dir.Label()}出発順序 → {(trains is { Count: > 0 } ? string.Join(" → ", trains) : "自動に戻す")}");
    }

    /// <summary>走行順に合わせて出発順序を直す（同期。記録には出さず、走行順と同じ更新時刻にする）。</summary>
    public void SyncOrder(string station, Dir dir, IReadOnlyList<string> trains, long stamp)
    {
        lock (_lock)
        {
            _orders[(station, dir)] = trains.Distinct().ToList();
            _orderStamps[(station, dir)] = stamp;
        }
    }

    /// <summary>出発順序に合わせて走行順を直す（同期。記録には出さず、出発順序と同じ更新時刻にする）。</summary>
    public void SyncSectionOrder(string a, string b, IReadOnlyList<string> trains, long stamp)
    {
        lock (_lock)
        {
            _sectionOrders[SectionKey(a, b)] = trains.Distinct().ToList();
            _sectionStamps[SectionKey(a, b)] = stamp;
        }
    }

    /// <summary>その駅を抜けた列車を出発順序から外す（黙って。全部抜けたら順序を消す）。</summary>
    public void RemoveFromOrder(string station, string train)
    {
        lock (_lock)
        {
            foreach (var dir in new[] { Dir.Down, Dir.Up })
            {
                if (!_orders.TryGetValue((station, dir), out var l) || !l.Remove(train)) continue;
                if (l.Count == 0) _orders.Remove((station, dir));
            }
        }
    }

    // ---- 単線区間の走行順 ----

    private static (string, string) SectionKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    /// <summary>単線区間（駅間）の走行順。列番の並び（上下とも）。</summary>
    public IReadOnlyList<string>? SectionOrder(string a, string b)
    {
        lock (_lock) return _sectionOrders.TryGetValue(SectionKey(a, b), out var l) ? l.ToList() : null;
    }

    public void SetSectionOrder(string a, string b, IReadOnlyList<string>? trains)
    {
        lock (_lock)
        {
            if (trains is { Count: > 0 }) _sectionOrders[SectionKey(a, b)] = trains.Distinct().ToList();
            else _sectionOrders.Remove(SectionKey(a, b));
            _sectionStamps[SectionKey(a, b)] = ++_stamp;
        }

        Changed?.Invoke($"集中扱い: {a}〜{b} 走行順 → {(trains is { Count: > 0 } ? string.Join(" → ", trains) : "自動に戻す")}");
    }

    /// <summary>区間を抜けた列車を走行順から外す（黙って）。</summary>
    public void RemoveFromSectionOrder(string a, string b, string train)
    {
        lock (_lock)
        {
            var k = SectionKey(a, b);
            if (!_sectionOrders.TryGetValue(k, out var l) || !l.Remove(train)) return;
            if (l.Count == 0) _sectionOrders.Remove(k);
        }
    }

    // ---- 発車時刻（ダイヤグラムのスジずらし） ----

    /// <summary>ダイヤグラムで指定した発車時刻（UTC）。この時刻の30秒前まで、その駅の出発進路を開通させない。</summary>
    public DateTime? OpenAt(string train, string station)
    {
        lock (_lock) return _openAt.TryGetValue((train, station), out var t) ? t : null;
    }

    public void SetOpenAt(string train, string station, DateTime? utc)
    {
        lock (_lock)
        {
            if (utc is { } t) _openAt[(train, station)] = t;
            else _openAt.Remove((train, station));
        }

        Changed?.Invoke($"集中扱い: {train} {station} 発車時刻 → {(utc is { } at ? clock.TextAt(at) : "指定解除")}");
    }

    // ---- 運転モード（フリー運転・ダイヤ運転） ----

    private bool _diaMode;

    /// <summary>ダイヤ運転中か。ダイヤ運転中は、列車番号の手入力が要り、継続乗務は常に入（時間で消さない）。</summary>
    public bool DiaMode
    {
        get { lock (_lock) return _diaMode; }
    }

    public void SetDiaMode(bool dia)
    {
        lock (_lock)
        {
            if (_diaMode == dia) return;
            _diaMode = dia;
        }

        Changed?.Invoke($"運転モード: {(dia ? "ダイヤ運転" : "フリー運転")}に切り替え");
    }

    // ---- 臨時停車・臨時通過・打ち切り（サーバーの扱いだけ。ゲームの停車駅は変わらない） ----

    private readonly Dictionary<(string Train, string Station), string> _stops = new();

    /// <summary>"停車"（臨時停車）・"通過"（臨時通過）・"打ち切り"。指定がなければ null。</summary>
    public string? StopOverride(string train, string station)
    {
        lock (_lock) return _stops.GetValueOrDefault((train, station));
    }

    // 打ち切りにした駅（列車がいなくなっても残す。ダイヤグラムの計画の線を、打ち切った駅で切るため）。指定を外す・ダイヤのリセットで消す
    private readonly Dictionary<string, string> _cuts = new();

    /// <summary>打ち切りを変えるたびに増える（ダイヤグラムの計画の線を読み直す合図）。</summary>
    public int CutVersion { get; private set; }

    /// <summary>列車を打ち切りにした駅（列車がもう走り終えていても）。なければ null。</summary>
    public string? CutStation(string train)
    {
        lock (_lock) return _cuts.GetValueOrDefault(train);
    }

    /// <summary>臨時停車・臨時通過・打ち切りの指示を変えるたびに増える（自動運転列車の停車駅を作り直す合図）。</summary>
    public int StopVersion { get; private set; }

    /// <summary>臨時停車・臨時通過・打ち切りの指示が変わった（浜園の特急の臨時停車の時刻を作り直す合図）。</summary>
    public event Action? StopsChanged;

    public void SetStop(string train, string station, string? kind)
    {
        lock (_lock)
        {
            if (kind is "停車" or "通過" or "打ち切り") _stops[(train, station)] = kind;
            else _stops.Remove((train, station));
            if (kind == "打ち切り") { _cuts[train] = station; CutVersion++; }
            else if (_cuts.GetValueOrDefault(train) == station) { _cuts.Remove(train); CutVersion++; }
        }

        StopVersion++;
        StopsChanged?.Invoke();
        var label = kind switch { "停車" => "臨時停車", "通過" => "臨時通過", "打ち切り" => "打ち切り", _ => "停車・通過を元に戻す" };
        Changed?.Invoke($"集中扱い: {train} {station} {label}");
    }

    // ---- 番線・抑止・開通指示 ----

    public int? TrackOverride(string train, string station)
    {
        lock (_lock) return _tracks.TryGetValue((train, station), out var t) ? t : null;
    }

    public HoldKind Hold(string train, string station)
    {
        lock (_lock) return _holds.GetValueOrDefault((train, station));
    }

    public bool IsHeld(string train, string station) => Hold(train, station) != HoldKind.None;

    public bool IsForced(string train, string station)
    {
        lock (_lock) return _forces.Contains((train, station));
    }

    public bool IsCentral(string station)
    {
        lock (_lock) return _centralStations.Contains(station);
    }

    /// <summary>駅扱い（信号制御盤で進路を取る駅）か。</summary>
    public bool IsLocal(string station)
    {
        lock (_lock) return _localStations.Contains(station);
    }

    private readonly HashSet<string> _panelKeys = new();

    /// <summary>信号制御盤で開通させている進路（盤が入れる）。在線共有の全員のゲームに写す（RouteEngine.OtherRoutes）。</summary>
    public Func<IReadOnlyList<RouteCommand>> PanelRoutes { get; set; } = () => Array.Empty<RouteCommand>();

    /// <summary>
    /// サーバーが自動で要求していて、まだゲームの進路の一覧に出ていない進路（連動装置, 進路名）。RouteEngine が入れる。
    /// 信号制御盤は、集中のとき、これを見て転てつ器を転換させる（要求 → 盤で転換 → ゲームで開通を確かめたら白い灯）
    /// </summary>
    public Func<IReadOnlyList<(string Interlock, string Route)>> AutoRouteRequests { get; set; } = () => Array.Empty<(string, string)>();

    /// <summary>
    /// サーバーが自動で取った進路のうち、ゲームで開通したが現示時素（踏切防護）の間なので信号を停止に押さえているもの（連動装置, 進路名）。RouteEngine が入れる。
    /// 信号制御盤は、これがなくなってから信号表示灯を点ける（ラインライトは進路ができた時点、信号灯は信号が開いた時点）
    /// </summary>
    public Func<IReadOnlyList<(string Interlock, string Route)>> AutoHeldRoutes { get; set; } = () => Array.Empty<(string, string)>();

    /// <summary>信号制御盤の進路のうち、ゲームには送っているが現示時素（踏切の警報時分）の間なので信号を停止に押さえるもの（連動装置, 進路名）。</summary>
    public Func<IReadOnlyList<(string Interlock, string Route)>> PanelHeldRoutes { get; set; } = () => Array.Empty<(string, string)>();

    /// <summary>信号制御盤の方向てこで、駅扱いにしている単線区間の向き（区間 → 向き）。盤が入れ、RouteEngine が区間方向に使う。</summary>
    public Func<IReadOnlyDictionary<(string, string), Dir>> PanelSectionDirs { get; set; } = () => new Dictionary<(string, string), Dir>();

    private readonly Dictionary<(string, string), Dir> _sectionDirs = new();

    /// <summary>いまの単線区間の向き（TID の区間方向と同じ）。RouteEngine が毎周期入れる。</summary>
    public void SetSectionDir((string, string) section, Dir dir)
    {
        lock (_lock) _sectionDirs[section] = dir;
    }

    public Dir? SectionDir((string, string) section)
    {
        lock (_lock)
            return _sectionDirs.TryGetValue(section, out var d) ? d
                : _sectionDirs.TryGetValue((section.Item2, section.Item1), out var e) ? e : null;
    }

    /// <summary>信号制御盤の解放の鍵が R になっているか（盤から知らせる）。</summary>
    public void SetPanelKey(string station, bool turned)
    {
        lock (_lock) { if (turned) _panelKeys.Add(station); else _panelKeys.Remove(station); }
    }

    /// <summary>
    /// 盤で扱っている駅（管理画面で駅扱い、かつ盤の解放の鍵が R）。自動の進路から切り離し、手動でないと進路を取らない
    /// （管理画面の「駅扱い（強制進路）」は通す）。
    /// </summary>
    public bool IsPanelControlled(string station)
    {
        lock (_lock) return _localStations.Contains(station) && _panelKeys.Contains(station);
    }

    /// <summary>駅の扱い: train（列車扱い）/ central（集中扱い）/ local（駅扱い）。</summary>
    public string ModeOf(string station)
    {
        lock (_lock) return _centralStations.Contains(station) ? "central" : _localStations.Contains(station) ? "local" : "train";
    }

    public void SetTrack(string train, string station, int? track)
    {
        lock (_lock)
        {
            if (track is { } t) _tracks[(train, station)] = t;
            else _tracks.Remove((train, station));
        }

        Changed?.Invoke($"集中扱い: {train} {station} 番線 → {(track?.ToString() ?? "指定解除")}");
    }

    /// <summary>抑止をかける・外す。kind を省くと場内・出発の両方。</summary>
    public void SetHold(string train, string station, bool hold, HoldKind kind = HoldKind.Both)
    {
        lock (_lock)
        {
            var cur = _holds.GetValueOrDefault((train, station));
            cur = hold ? cur | kind : cur & ~kind;
            if (cur == HoldKind.None) _holds.Remove((train, station));
            else _holds[(train, station)] = cur;
        }

        var what = kind switch { HoldKind.Arrive => "場内抑止", HoldKind.Depart => "出発抑止", _ => "抑止" };
        Changed?.Invoke($"集中扱い: {train} {station} {what}{(hold ? "" : "解除")}");
    }

    private readonly HashSet<(string Train, string Station)> _cancels = new();

    public void Force(string train, string station)
    {
        lock (_lock)
        {
            _forces.Add((train, station));
            _holds.Remove((train, station));
        }

        Changed?.Invoke($"集中扱い: {train} {station} 開通指示");
    }

    // ---- 駅扱い（強制進路） ----

    /// <summary>
    /// 駅扱い: 「本線→1番線」（arrive:1）「1番線→本線」（depart:1）「通過」（through）「入換」（shunt:3）などの進路を、
    /// 連動に問題がなければ（進路に他の列車がいない・他の列車の進路と重ならない）、抑止・待ちを無視して開通させる。
    /// 実際の開通は進路の判断（RouteEngine）が次の周期で行う。
    /// </summary>
    public void RequestManual(string train, string station, string action, string label)
    {
        lock (_lock) _manual[(train, station)] = action;
        Changed?.Invoke($"駅扱い: {train} {station} {label}");
    }

    /// <summary>駅扱いの指示を受け取る（1回だけ）。</summary>
    public string? TakeManual(string train, string station)
    {
        lock (_lock) return _manual.Remove((train, station), out var a) ? a : null;
    }

    public void SetCentral(string station, bool central) => SetMode(station, central ? "central" : "train");

    /// <summary>駅の扱いを切り替える（train / central / local）。</summary>
    public void SetMode(string station, string mode)
    {
        lock (_lock)
        {
            _centralStations.Remove(station);
            _localStations.Remove(station);
            if (mode == "central") _centralStations.Add(station);
            else if (mode == "local") _localStations.Add(station);
        }

        var label = mode == "central" ? "集中扱い" : mode == "local" ? "駅扱い" : "列車扱い";
        Changed?.Invoke($"集中扱い: {station} を{label}に切り替え");
    }

    /// <summary>
    /// 列車走行位置（お客様向け）の「運転整理中」の印を付ける列車: 出発順序・走行順・番線・抑止・開通指示・臨時停車などの指示が出ている列車。
    /// </summary>
    public HashSet<string> PassengerAdvisoryTrains()
    {
        lock (_lock)
        {
            var trains = _orders.Values.SelectMany(list => list).ToHashSet(StringComparer.Ordinal);
            foreach (var list in _sectionOrders.Values) trains.UnionWith(list);
            foreach (var key in _tracks.Keys) trains.Add(key.Train);
            foreach (var key in _holds.Keys) trains.Add(key.Train);
            foreach (var key in _openAt.Keys) trains.Add(key.Train);
            foreach (var key in _stops.Keys) trains.Add(key.Train);
            return trains;
        }
    }

    /// <summary>
    /// 手で入れた指示をまとめて取り消す（ロープレ時刻を設定したとき）: 列車ごとの指示（番線・抑止・開通指示・臨時停車など・
    /// 発車時刻・駅扱いの強制進路）と、出発順序・走行順。駅の扱い（列車扱い・集中扱い・駅扱い）と運転モードは残す。
    /// 取り消した数を返す。
    /// </summary>
    public int ResetManual()
    {
        int n;
        bool hadStops;
        lock (_lock)
        {
            n = ResetManualCore(out hadStops);
        }
        if (hadStops)
        {
            StopVersion++;
            StopsChanged?.Invoke();
        }
        return n;
    }

    /// <summary>
    /// 列車がその駅を抜けた・その駅で走り終えたので、その駅の指示（番線・抑止・開通指示・駅扱いの強制進路・発車時刻）を消す。
    /// 消さないと、終夜運転で翌日の同じ列番や、同じ列番でやり直した列車に、前の指示が効いてしまう。まだ通っていない駅の指示は残る。
    /// 臨時停車・臨時通過・打ち切りは消さない: 浜園の臨時停車は関わる列車の時刻を変えていて（消すと、走っている列車の時刻が元に戻る）、
    /// 打ち切りはダイヤグラムの計画の線を打ち切った駅で切るのに、走り終えたあとも使う。これらは、指示を外すか、ロープレ時刻の設定で消える。
    /// 消したものがあれば true。
    /// </summary>
    public bool ForgetAt(string train, string station)
    {
        var key = (train, station);
        lock (_lock)
            return _tracks.Remove(key) | _holds.Remove(key) | _forces.Remove(key) | _manual.Remove(key) | _openAt.Remove(key);
    }

    /// <summary>ResetManual の中身（_lock の中で）。臨時停車などの指示があったかを hadStops に返す。</summary>
    private int ResetManualCore(out bool hadStops)
    {
        var n = _tracks.Count + _holds.Count + _forces.Count + _manual.Count + _openAt.Count + _stops.Count
            + _orders.Count + _sectionOrders.Count;
        hadStops = _stops.Count > 0;
        _tracks.Clear();
        _holds.Clear();
        _forces.Clear();
        _cancels.Clear();
        _manual.Clear();
        _openAt.Clear();
        _stops.Clear();
        if (_cuts.Count > 0) { _cuts.Clear(); CutVersion++; }
        // 順序は消したうえで更新時刻を進める（同期で古い順序から書き戻されないように）
        foreach (var k in _orders.Keys.ToList()) _orderStamps[k] = ++_stamp;
        foreach (var k in _sectionOrders.Keys.ToList()) _sectionStamps[k] = ++_stamp;
        _orders.Clear();
        _sectionOrders.Clear();
        return n;
    }

}
