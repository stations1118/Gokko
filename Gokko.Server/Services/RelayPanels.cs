using System.Text.Json;
using Gokko.Common;
using System.Text.Json.Serialization;

namespace Gokko.Server.Services;

// 信号制御盤（relaypanel.html）。盤の状態はサーバーだけが持ち、開いている全員に同じ状態を配る。
// 駅のデータは wwwroot/relaypanel/stations/<駅ID>.json（画面の絵の位置と、連動のデータを1つのファイルに書く）。
// 盤で進路が開通している間、その進路を在線共有の全員のゲームに設定する（RouteEngine.OtherRoutes に混ぜて送る。列車が進路に入ったら外す）。
// 現示時素（clearTime）のある進路は、先に送って時素の間は信号を停止に押さえる（RouteEngine.ClearTimeStopSignals）。
//  ・発点てこを倒し、着点ボタンを押すと、その間の進路を構成する。進路は発点てこを戻したときだけ解ける
//  ・総括制御: 第一場内のてこを倒して番線の着点を押すと、第二場内もいっしょに開通する（進路の through）。第二場内は、第一場内のてこを戻しても
//    開通と現示を保つ。列車が入ると、ふつうの進路と同じく通り終わった区分から解ける。第二場内のてこを倒して戻しても解ける
//  ・進路てこ式（藤江など。着点ボタンがない駅）: 進路の dest を書かない。てこを倒すだけで、その向きの進路を構成する（12 を R で 12R、L で 12L）
//  ・接近区間に列車がいるときに戻すと、時素（進路の approachTime 秒）が過ぎてから解ける。時素の間に列車が進路に入ったら進路区分鎖錠に移る
//  ・進路区分鎖錠: 列車が進路に入ってから戻すと、列車が通り終わった区分から解ける。列車がいる区分と、その先の区分は鎖錠したまま
//  ・保留鎖錠は入れない
//  ・一斉停止: 全部の信号が停止になる。進路は残る

public sealed class RelayStationData
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>ゲームの連動装置の名前（Interlockings.json の interlock。例: 津崎連動装置）。ゲームに進路を送るのに使う。</summary>
    public string Interlock { get; set; } = "";
    public int ApproachTime { get; set; } = 30;
    /// <summary>操作画面。"" = 信号制御盤（relaypanel.html）、"denshirendo" = 電子連動装置（denshirendo.html。データは wwwroot/denshirendo/stations）。</summary>
    public string Screen { get; set; } = "";
    /// <summary>列車が進路に入ったら、発点のてこを自動で定位に戻す（電子連動装置。進路は通り終わった区分から解け、列車が通り抜けても進路を取り直さない）。</summary>
    public bool AutoCancel { get; set; }
    public List<RelayArea> Areas { get; set; } = new();
    public List<RelayDirection> Directions { get; set; } = new();
    public List<RelayPoint> Points { get; set; } = new();
    public List<RelayLever> SignalLevers { get; set; } = new();
    public List<RelayDest> DestButtons { get; set; } = new();
    public List<RelayRoute> Routes { get; set; } = new();
    public List<RelayBell> ApproachBells { get; set; } = new();
    public List<RelayTimer> Timers { get; set; } = new();
    public List<RelayTrackLamp> TrackLamps { get; set; } = new();   // ラインライト。回路だけ読む（在線を受け取る回路に入れる）
    public JsonElement? ReleaseKey { get; set; }
    public JsonElement? AllStop { get; set; }
}

public sealed class RelayArea { public string Id { get; set; } = ""; }
public sealed class RelayTrackLamp { public string C { get; set; } = ""; }
/// <summary>単線の方向てこ。L = 隣の駅から当駅へ（受ける）、R = 当駅から隣の駅へ（出す）。
/// area の駅扱いてこが R（駅扱）のときだけ効き、out の進路は R、in の進路は L でないと開通しない。集中（N）のときは CTC が決めるので、倒しても効き目がない。
/// 駅間の回路（circuits）に列車がいるときと、out・in の進路があるときは、向きを変えられない。</summary>
public sealed class RelayDirection
{
    public string N { get; set; } = "";
    public string? Area { get; set; }
    public List<string> Circuits { get; set; } = new();
    public List<string> Out { get; set; } = new();
    public List<string> In { get; set; } = new();
    /// <summary>単線区間（RouteRules.json の singleTrackSections の駅ID 2つ）と、L のときの列車の向き（"下り" か "上り"）。</summary>
    public List<string> Section { get; set; } = new();
    public string L { get; set; } = "下り";
    public (string, string) Key => (Section.ElementAtOrDefault(0) ?? "", Section.ElementAtOrDefault(1) ?? "");
    public Dir DirOf(string lever) => (lever == "L") == (L == "下り") ? Dir.Down : Dir.Up;
    public string LeverOf(Dir d) => (d == Dir.Down) == (L == "下り") ? "L" : "R";
}
/// <summary>転てつ器。circuit は てっ査の回路。2動の転てつ器などで回路が2つ以上あるときは circuits に並べる。</summary>
public sealed class RelayPoint
{
    public string N { get; set; } = "";
    public string Circuit { get; set; } = "";
    public List<string> Circuits { get; set; } = new();
    public IEnumerable<string> All => Circuits.Count > 0 ? Circuits : new[] { Circuit };
}
public sealed class RelayLever { public string N { get; set; } = ""; public string Side { get; set; } = "R"; public string? Area { get; set; } }
public sealed class RelayDest { public string Id { get; set; } = ""; public string? Area { get; set; } }
/// <summary>接近ボタン。circuits に列車が入ると鳴る。when は「ほかの駅の進路（"連動装置/進路"）がゲームで開通しているときだけ見る回路」
/// （例: 江ノ原信号場で大道寺へ向かう進路が出ているときだけ、江ノ原の回路で鳴らす。車庫へ入る列車では鳴らさない）。</summary>
public sealed class RelayBell
{
    public string Id { get; set; } = "";
    public List<string> Circuits { get; set; } = new();
    public List<RelayBellIf> When { get; set; } = new();
}
public sealed class RelayBellIf
{
    public List<string> Routes { get; set; } = new();
    public List<string> Circuits { get; set; } = new();
}
public sealed class RelayTimer { public int Sec { get; set; } }

/// <summary>条件つきの接近区間。signal = この駅の進路が開通していてそのてこが倒れている（反位）とき、point/pos = 転てつ器がその向きのとき。
/// other = ほかの駅の条件（この盤では分からないので、そろっているとみなす。長く鎖錠する側）。</summary>
public sealed class RelayApproachIf
{
    public List<string> Circuits { get; set; } = new();
    public string? Signal { get; set; }
    public string? Point { get; set; }
    public string? Pos { get; set; }
    public string? Other { get; set; }
}

/// <summary>盤に渡す列車1本の在線: いまいる回路と、少し前（60秒以内）からいままでに踏んだ回路。</summary>
public sealed record RelayTrain(HashSet<string> Occ, HashSet<string> Touched);

public sealed class RelayRoute
{
    public string Name { get; set; } = "";
    public string Lever { get; set; } = "";
    public string Side { get; set; } = "R";
    /// <summary>着点ボタン。書かない進路は進路てこ式で、てこを倒すだけで構成する。</summary>
    public string? Dest { get; set; }
    [JsonIgnore] public bool ByLever => string.IsNullOrEmpty(Dest);
    /// <summary>総括制御: 着点 → いっしょに開通させる進路（例: 1RA の {"1": "2R"}。1 のてこを倒して着点 1 を押すと、1RA と 2R）。</summary>
    public Dictionary<string, string> Through { get; set; } = new();
    /// <summary>過走余裕の回路（着点の先）。鎖錠はするが、ラインライトは点けない。</summary>
    public List<string> Overrun { get; set; } = new();
    public Dictionary<string, string> Points { get; set; } = new();
    public List<string> Circuits { get; set; } = new();
    public List<string> Approach { get; set; } = new();
    public int? ApproachTime { get; set; }
    /// <summary>現示時素（秒）。進路ができてから、この秒数たってから信号を進行にする（踏切の警報時分のため）。</summary>
    public int? ClearTime { get; set; }
    public List<string> Stops { get; set; } = new();
    /// <summary>出発信号機の進路（ゲームに送るとき、列選に列車の種別を出す）。</summary>
    public bool Departure { get; set; }
    /// <summary>進路区分鎖錠の区分（列車が進む順）。書かなければ回路1つずつ。</summary>
    public List<List<string>>? LockGroups { get; set; }
    /// <summary>区分ごとの時間解錠（秒、0 = なし）。列車がその区分に入ってからこの秒数たつと、まだ在線していても解ける（{1RAT if 30秒} など）。</summary>
    public List<int>? GroupTimes { get; set; }
    /// <summary>条件つきの接近区間（{68T 74T if(1RB)} など）。条件がそろっているときだけ、approach に足す。</summary>
    public List<RelayApproachIf> ApproachIf { get; set; } = new();

    [JsonIgnore] public List<List<string>> Groups { get; set; } = new();
}

/// <summary>
/// 単線区間の出発の取り合い（盤どうし）。区間へ出る出発進路を盤で開通させるとき、その区間をその向きで押さえる。
/// 先に押さえた盤がある間は、反対向きの出発進路は開通しない（両方の駅を盤で扱い、方向てこを自動のままにしても、単線区間で向かい合わない）。
/// 自分の出発進路がなくなったら放す。駅どうしのロックをまたがないよう、これだけで1つのロックを持つ。
/// </summary>
public sealed class SectionClaims
{
    private readonly object _lock = new();
    private readonly Dictionary<(string, string), (Dir Dir, string Owner)> _claims = new();

    public static (string, string) Norm((string, string) k) => string.CompareOrdinal(k.Item1, k.Item2) <= 0 ? k : (k.Item2, k.Item1);

    /// <summary>ほかの盤が反対向きで押さえていないか。</summary>
    public bool Free((string, string) key, Dir dir, string owner)
    {
        lock (_lock) return !_claims.TryGetValue(Norm(key), out var c) || c.Owner == owner || c.Dir == dir;
    }

    /// <summary>押さえる。ほかの盤が反対向きで押さえていれば false。</summary>
    public bool TryClaim((string, string) key, Dir dir, string owner)
    {
        lock (_lock)
        {
            var k = Norm(key);
            if (_claims.TryGetValue(k, out var c) && c.Owner != owner && c.Dir != dir) return false;
            if (!_claims.ContainsKey(k) || c.Owner == owner) _claims[k] = (dir, owner);
            return true;
        }
    }

    public void Release((string, string) key, string owner)
    {
        lock (_lock) { var k = Norm(key); if (_claims.TryGetValue(k, out var c) && c.Owner == owner) _claims.Remove(k); }
    }
}

/// <summary>1駅の盤。操作と時間の経過で状態が変わったら <see cref="Changed"/> を起こす。</summary>
public sealed class RelayStation
{
    /// <summary>転てつ器の転換にかかる時間。電気転てつ機の目安で 4 秒（ユーザーの指示があれば変える）。</summary>
    public const int PointTimeMs = 4000;
    private const int MaxEvents = 64;

    private sealed class SetRoute
    {
        public bool Entered;
        public bool[] WasOcc = [];
        public bool[] Passed = [];
        public long? ReleaseUntil;
        public int ReleaseSec;
        public long GoAfter;   // 現示時素: この時刻を過ぎてから進行にする（ゲームで開通を確かめたら、そこから数え直す）
        public bool Seen;      // ゲームで開通したのを確かめた（現示時素をそこから数えた）
        public long[] OccSince = [];   // 区分に列車が入った時刻（時間解錠に使う。0 = まだ）
        public bool Hold;              // 総括制御で開通させた進路: てこが定位でも、列車が入るまで保つ
    }

    private sealed class Bell { public bool WasOcc, Ringing, Acked; }

    private readonly object _lock = new();
    private readonly Dictionary<string, RelayRoute> _routes;
    private readonly Dictionary<string, List<string>> _pointCircuit;
    private readonly HashSet<string> _known;

    private readonly Dictionary<string, string> _sig = new();       // 発点てこ N / L / R
    private readonly Dictionary<string, long> _revAt = new();       // 発点てこを倒した時刻
    private readonly Dictionary<string, string> _request = new();   // 着点まで選んで待っている進路（てこ → 進路）
    private readonly Dictionary<string, string> _through = new();   // 総括制御で待っている進路（進路 → 元の進路のてこ）
    private readonly Dictionary<string, string> _ptLever = new();   // 転てつ器てこ N / A（自動）/ R
    private readonly Dictionary<string, string> _point = new();     // 転てつ器の向き N / R
    private readonly Dictionary<string, (string Target, long Until)> _moving = new();
    private readonly HashSet<string> _testOcc = new();
    private HashSet<string> _gameOcc = new();
    private readonly HashSet<string> _occ = new();
    private readonly Dictionary<string, SetRoute> _set = new();
    private readonly Dictionary<string, string> _area = new();      // 集中／駅扱の切換てこ N（集中）/ R（駅扱）
    private bool _keyIn;
    private string _keyPos = "N";
    private string? _keyBy;
    private string _allStop = "N";
    private readonly Dictionary<string, string> _dir = new();       // 方向てこの位置 L（受ける）/ R（出す）。てこはいつでも倒せる
    private readonly Dictionary<string, string> _dirSet = new();    // 方向てこで決まっている向き。駅間に列車がいる・その方向の進路があるうちは、てこを倒しても変わらない
    private readonly Dictionary<string, Bell> _bells = new();

    private readonly List<(long Seq, string Type, string Id)> _events = new();
    private long _seq;
    private long _version;
    private string _lastJson = "";
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RelayStationData Data { get; }
    public string DataJson { get; }

    private readonly Func<bool> _adminLocal;
    private readonly Action<bool> _keyChanged;
    private readonly Func<(string, string), Dir?> _sectionDir;
    private readonly Action<string> _note;

    /// <param name="adminLocal">管理画面でこの駅が「駅扱い」になっているか</param>
    private readonly SectionClaims _claims;

    public RelayStation(RelayStationData data, string dataJson, Func<bool> adminLocal, Action<bool>? keyChanged = null, Func<(string, string), Dir?>? sectionDir = null, Action<string>? note = null, SectionClaims? claims = null)
    {
        _claims = claims ?? new SectionClaims();
        _note = note ?? (_ => { });
        _adminLocal = adminLocal;
        _sectionDir = sectionDir ?? (_ => null);
        _keyChanged = keyChanged ?? (_ => { });
        Data = data;
        DataJson = dataJson;
        foreach (var r in data.Routes)
            // 鎖錠の区分は、書いていなければ回路ごと。過走余裕（overrun）の回路は列車が入らないので区分にしない
            // （入れると、その区分がいつまでも解けず、進路が残る。新野崎の 25LC は着点 SC の先の 4LT が過走余裕）
            r.Groups = r.LockGroups is { Count: > 0 } g ? g : r.Circuits.Except(r.Overrun).Select(c => new List<string> { c }).ToList();
        _routes = data.Routes.ToDictionary(r => r.Name);
        _pointCircuit = data.Points.ToDictionary(p => p.N, p => p.All.ToList());
        _known = data.Routes.SelectMany(r => r.Circuits.Concat(r.Approach).Concat(r.ApproachIf.SelectMany(a => a.Circuits))).Concat(data.Points.SelectMany(p => p.All))
            .Concat(data.ApproachBells.SelectMany(b => b.Circuits.Concat(b.When.SelectMany(w => w.Circuits))))
            // ラインライトだけある回路（館浜の上り6T など、進路にも接近区間にも入らない駅の外の回路）も、在線を赤く点けるので受け取る
            .Concat(data.TrackLamps.Select(l => l.C)).ToHashSet();
        foreach (var l in data.SignalLevers) _sig[l.N] = "N";
        foreach (var p in data.Points) { _ptLever[p.N] = "A"; _point[p.N] = "N"; }
        foreach (var a in data.Areas) _area[a.Id] = "N";
        foreach (var d in data.Directions) _dir[d.N] = _dirSet[d.N] = "L";
        foreach (var b in data.ApproachBells) _bells[b.Id] = new Bell();
        lock (_lock) Update(Now());
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>状態が変わったときに終わる Task（待つ前に取ってから状態を読む）。</summary>
    public Task Changed { get { lock (_lock) return _changed.Task; } }

    // ---- 連動 ----

    private bool Back(RelayRoute r) => _sig.GetValueOrDefault(r.Lever, "N") != r.Side && !(_set.TryGetValue(r.Name, out var s) && s.Hold);
    private bool GroupLive(RelayRoute r, SetRoute s, int i) => !(s.Entered && Back(r) && (s.Passed[i] || TimedOut(r, s, i)));

    // 着点の区分（最後の区分）は、時素の書いていない進路でも、列車が入って DestReleaseSec たてば解ける（てこを戻していれば）。
    // 列車が着点に止まったままだと進路が残り、向かいの進路（引上線から戻す進路など）が取れなくなる
    // （新野崎で 23R の引上線 23RT に列車を入れたら、23R が残り、戻す 25LC・25LD や 8R が取れなくなった）。
    // 着点に列車がいる間は、そこへ入る進路はどれも在線で取れないので、解いても列車の上に進路はできない
    private const int DestReleaseSec = 30;

    private bool TimedOut(RelayRoute r, SetRoute s, int i)
    {
        var t = r.GroupTimes is { } g && i < g.Count ? g[i] : i == r.Groups.Count - 1 ? DestReleaseSec : 0;
        return t > 0 && s.OccSince[i] > 0 && _now >= s.OccSince[i] + t * 1000L;
    }

    // 接近区間（条件つきの区間は、条件がそろっているときだけ）
    private IEnumerable<string> ApproachOf(RelayRoute r)
    {
        foreach (var c in r.Approach) yield return c;
        foreach (var a in r.ApproachIf)
        {
            var ok = a.Other != null
                || (a.Signal != null && _routes.TryGetValue(a.Signal, out var sr) && _set.ContainsKey(a.Signal) && !Back(sr))
                || (a.Point != null && _point.GetValueOrDefault(a.Point) == a.Pos && !_moving.ContainsKey(a.Point));
            if (ok) foreach (var c in a.Circuits) yield return c;
        }
    }

    private IEnumerable<string> LiveCircuits(RelayRoute r, SetRoute s)
    {
        for (var i = 0; i < r.Groups.Count; i++)
            if (GroupLive(r, s, i)) foreach (var c in r.Groups[i]) yield return c;
    }

    // ---- ラインライトの白・在線位置の画面の緑（開通の表示） ----
    // 条件は TID の自動の進路の緑と同じ: ゲームの進路の一覧に出ている（開通を確かめた）進路の、列車がまだ通り終わっていない回路。
    // 盤で取った進路も、ゲームの一覧に出るまでは白くしない。盤で取っていない自動の進路（集中のときにサーバーが取った進路）も白くする
    // （2026-11-19 ユーザーの決まり）。過走余裕の回路は白くしない

    private readonly Dictionary<string, int> _autoFrom = new(); // 自動の進路 → 列車が入った区分（入る前は -1、通り抜けたら int.MaxValue）

    private bool InGame(RelayRoute r) => Data.Interlock.Length > 0 && _otherRoutes.Contains($"{Data.Interlock}/{r.Name}");

    /// <summary>自動の進路の、列車がどこまで進んだか（Update のたびに）。ゲームの一覧から消えたら忘れる。</summary>
    private HashSet<string> _enteredBefore = new(); // 前の Update で、列車が入っていた盤の進路
    private void TrackAutoRoutes()
    {
        foreach (var r in Data.Routes)
        {
            if (_set.ContainsKey(r.Name) || !InGame(r)) { _autoFrom.Remove(r.Name); continue; }
            var at = r.Groups.FindIndex(g => g.Any(_occ.Contains));
            // 列車が入ったあとに盤で解いた進路が、まだゲームの一覧に残っているとき: 通り終わった所を白く点け直さない
            if (!_autoFrom.ContainsKey(r.Name) && _enteredBefore.Contains(r.Name)) _autoFrom[r.Name] = at >= 0 ? at : int.MaxValue;
            var prev = _autoFrom.GetValueOrDefault(r.Name, -1);
            _autoFrom[r.Name] = at >= 0 ? Math.Max(prev == int.MaxValue ? at : prev, at) : prev >= 0 ? int.MaxValue : prev;
        }
        _enteredBefore = _set.Where(kv => kv.Value.Entered).Select(kv => kv.Key).ToHashSet();
    }

    /// <summary>
    /// ゲームの進路の一覧に残っている前の進路（盤で取っていない自動の進路）が、転てつ器 p を別の向き（need でない向き）で使っているか。
    /// 列車がその転てつ器の区分を通り終わるまで（TrackAutoRoutes の _autoFrom）は使っているとみなす
    /// </summary>
    private bool HeldByGameRoute(string p, string need)
    {
        foreach (var o in Data.Routes)
        {
            if (_set.ContainsKey(o.Name) || !InGame(o) || !o.Points.TryGetValue(p, out var w) || w == need) continue;
            var from = _autoFrom.GetValueOrDefault(o.Name, -1);
            if (from == int.MaxValue) continue;
            var gi = o.Groups.FindIndex(g => g.Any(_pointCircuit[p].Contains));
            if (gi < 0 || gi >= from) return true;
        }
        return false;
    }

    /// <summary>
    /// 信号表示灯を点ける進路: ゲームで開通していて（一覧に出ていて）、信号が開いているもの。
    /// 盤の進路は現示時素がたってから（Go）、自動の進路はサーバーが停止に押さえている間（現示時素・踏切防護）を除く。
    /// ラインライトは進路ができた時点（LitCircuits）、信号灯は信号が開いた時点（2026-11-19 ユーザーの決まり）
    /// </summary>
    private IEnumerable<string> GoRoutes()
    {
        foreach (var r in Data.Routes)
        {
            // 盤で取った進路は、サーバーが持つ進路。ゲームの一覧に載るのを待たずに、盤の判断で進行にする
            // （対象の列車がいないとゲームは一覧に載せないので。2026-11-27 ユーザーの決まり。載らないまま列車が近づいたら NotInGame）
            if (_set.ContainsKey(r.Name)) { if (Go(r)) yield return r.Name; continue; }
            if (Data.Interlock.Length > 0 && !InGame(r)) continue;
            if (_autoHeld.Contains(r.Name) || _allStop != "N" || r.Points.Keys.Any(_moving.ContainsKey) || r.Circuits.Any(_occ.Contains)) continue;
            if (_autoFrom.GetValueOrDefault(r.Name, -1) >= 0) continue;   // 列車が入ったあと
            // 転てつ器が進路の向きにないものは点けない。ゲームの一覧には、まだ開通していない進路も載る
            // （津崎で盤が 4R を取っている間、ゲームに 5R が載ると、23 が反位のままでも 5R の表示灯が点いた）
            if (r.Points.Any(p => _point.GetValueOrDefault(p.Key) != p.Value)) continue;
            yield return r.Name;
        }
    }

    private IEnumerable<string> LitCircuits() => LitRoutes().SelectMany(x => x.Circuits);

    /// <summary>
    /// ラインライトを白く点ける進路と、その回路。盤は、この進路の転てつ器の向き（進路の points）で、回路のどちら側の灯を点けるか決める。
    /// 盤の転てつ器の向きは、ゲームの一覧のほかの進路や列車からの推しで、進路と食い違ったまま残ることがある（新野崎で 23R・9R が分岐側に点いた）
    /// </summary>
    private IEnumerable<(string Route, List<string> Circuits)> LitRoutes()
    {
        foreach (var r in Data.Routes)
        {
            // 盤で取った進路はゲームの一覧を待たない。自動の進路は、ゲームの一覧に出てから。転てつ器の転換中は、まだ点けない
            if ((!_set.ContainsKey(r.Name) && !InGame(r)) || r.Points.Keys.Any(_moving.ContainsKey)) continue;
            IEnumerable<string> cs;
            if (_set.TryGetValue(r.Name, out var s)) cs = s.Entered ? LiveCircuits(r, s) : r.Circuits;
            else
            {
                var from = _autoFrom.GetValueOrDefault(r.Name, -1);
                cs = r.Groups.Skip(Math.Max(0, from)).Take(from == int.MaxValue ? 0 : int.MaxValue).SelectMany(g => g);
            }
            var list = cs.Except(r.Overrun).ToList();
            if (list.Count > 0) yield return (r.Name, list);
        }
    }

    private bool UsedByOther(string c, RelayRoute self) =>
        _set.Any(kv => kv.Key != self.Name && LiveCircuits(_routes[kv.Key], kv.Value).Contains(c));

    private bool StoppedBy(RelayRoute r) => _set.Keys.Any(n => _routes[n].Stops.Contains(r.Name));

    private bool PointLocked(string p)
    {
        var circuits = _pointCircuit[p];
        if (circuits.Any(_occ.Contains)) return true;
        foreach (var (name, s) in _set)
        {
            var r = _routes[name];
            if (!r.Points.ContainsKey(p)) continue;
            foreach (var circuit in circuits)
            {
                var gi = r.Groups.FindIndex(g => g.Contains(circuit));
                if (gi >= 0 && GroupLive(r, s, gi)) return true;
            }
            if (!circuits.Any(c => r.Groups.Any(g => g.Contains(c)))) return true;   // 進路の回路にない転てつ器（過走余裕など）は、進路があるあいだ鎖錠

        }
        return false;
    }

    private bool LeverHasRoute(string lever) => _set.Keys.Any(n => _routes[n].Lever == lever);

    /// <summary>方向てこが、この進路の向きと合っているか（駅扱いのときだけ見る）。</summary>
    private bool DirectionOk(RelayRoute r)
    {
        foreach (var d in Data.Directions)
        {
            if (d.Area != null && _area.GetValueOrDefault(d.Area) != "R") continue;
            // いまの区間方向（隣の駅とてこがそろって変わった向き）で見る。分からなければてこの向き
            var actual = ActualDir(d);
            if (d.Out.Contains(r.Name) && actual != "R") return false;
            if (d.In.Contains(r.Name) && actual != "L") return false;
        }
        return true;
    }

    /// <summary>いまの区間方向を、この盤の方向てこの L・R で。</summary>
    private string ActualDir(RelayDirection d) => _sectionDir(d.Key) is { } a ? d.LeverOf(a) : _dirSet[d.N];

    /// <summary>この盤の方向てこで駅扱いにしている単線区間と、てこの向き（解放の鍵が R で、駅扱いてこが駅扱のとき）。</summary>
    public IEnumerable<((string, string) Section, Dir Dir)> PanelSectionDirs()
    {
        lock (_lock)
        {
            if (!Local()) yield break;
            foreach (var d in Data.Directions)
                if (d.Section.Count == 2 && (d.Area == null || _area.GetValueOrDefault(d.Area) == "R"))
                    yield return (d.Key, d.DirOf(_dirSet[d.N]));
        }
    }

    // 単線区間へ出る出発進路: ほかの盤が反対向きの出発で区間を押さえていない。区間に列車がいれば、その列車と同じ向きのときだけ
    private bool SectionOk(RelayRoute r)
    {
        foreach (var d in Data.Directions)
        {
            if (!d.Out.Contains(r.Name) || d.Section.Count != 2) continue;
            var dir = d.DirOf("R");
            if (!_claims.Free(d.Key, dir, Data.Id)) return false;
            if (d.Circuits.Any(_occ.Contains) && _sectionDir(d.Key) != dir) return false;
        }
        return true;
    }

    // 開通させるときに区間を押さえる（取り合いに負けたら開通させない）
    private bool ClaimSections(RelayRoute r)
    {
        foreach (var d in Data.Directions)
            if (d.Out.Contains(r.Name) && d.Section.Count == 2 && !_claims.TryClaim(d.Key, d.DirOf("R"), Data.Id)) return false;
        return true;
    }

    private bool CanSet(RelayRoute r)
    {
        if (!DirectionOk(r) || !SectionOk(r)) return false;
        foreach (var (p, need) in r.Points) if (_point[p] != need || _moving.ContainsKey(p)) return false;
        if (r.Circuits.Any(c => _occ.Contains(c) || UsedByOther(c, r))) return false;
        return !StoppedBy(r);
    }

    private bool Feasible(RelayRoute r, Dictionary<string, string> claims)
    {
        if (r.Circuits.Any(c => _occ.Contains(c) || UsedByOther(c, r)) || StoppedBy(r) || !SectionOk(r)) return false;
        foreach (var (p, need) in r.Points)
        {
            var lev = _ptLever[p];
            if (lev != "A") { if (lev != need) return false; continue; }
            if (claims.TryGetValue(p, out var c) && c != need) return false;
            if (_point[p] != need && PointLocked(p)) return false;
        }
        return true;
    }

    // ---- ゲームに送る進路 ----
    // 盤の信号が進行の間だけ、全員のゲームにその進路を設定する。番号は進行になるたびに振り直す（クライアントは同じ番号を送り直さない）
    private static long _gameIdSeq = 9_000_000_000_000;
    private readonly Dictionary<string, long> _gameIds = new();
    private List<RouteCommand> _gameRoutes = new();

    public IReadOnlyList<RouteCommand> GameRoutes { get { lock (_lock) return _gameRoutes; } }

    private void UpdateGameRoutes()
    {
        if (Data.Interlock.Length == 0) return;
        var list = new List<RouteCommand>();
        foreach (var r in Data.Routes)
        {
            if (!Ready(r)) { _gameIds.Remove(r.Name); continue; }
            if (!_gameIds.TryGetValue(r.Name, out var id)) _gameIds[r.Name] = id = Interlocked.Increment(ref _gameIdSeq);
            list.Add(new RouteCommand { Id = id, Interlocking = Data.Interlock, RouteName = r.Name, UseTrainType = r.Departure, StopType = "停車" });
        }
        _gameRoutes = list;
    }

    private long _now;

    // 進路が開通していて、信号を進行にしてよい条件（現示時素を除く）。この間はゲームに進路を送る。
    // 現示時素のある進路（踏切がすぐ先にある出発信号機）は、ゲームに先に進路を送って踏切を鳴らし始め、時素がたつまでは
    // 信号を停止に押さえる（HeldRoutes → RouteEngine.ClearTimeStopSignals。自動の進路の clearTimes と同じ仕組み）。
    // 時素がたってから送ると、ゲームでは進路が入った瞬間に信号が進行になり、踏切の警報時分が取れない
    private bool Ready(RelayRoute r) =>
        _set.TryGetValue(r.Name, out var s) && !Back(r) && !s.Entered && s.ReleaseUntil == null && _allStop == "N" && !r.Circuits.Any(_occ.Contains);

    private bool Held(RelayRoute r) => Ready(r) && _now < _set[r.Name].GoAfter;

    private bool Go(RelayRoute r) => Ready(r) && _now >= _set[r.Name].GoAfter;

    private List<string> _held = new();

    // 盤で取った進路が、ゲームの一覧に載らないまま列車が近づいた（接近区間に入って NotInGameMs たった）。
    // 盤は転てつ不良の灯を点け、その進路の信号表示灯を点滅させる。ゲームに載れば消える
    private const long NotInGameMs = 5000;
    private readonly Dictionary<string, long> _missSince = new();
    private List<string> _notInGame = new();

    private void TrackNotInGame(long now)
    {
        foreach (var r in Data.Routes)
        {
            var miss = Data.Interlock.Length > 0 && Ready(r) && !InGame(r) && r.Approach.Any(_occ.Contains);
            if (!miss) _missSince.Remove(r.Name);
            else _missSince.TryAdd(r.Name, now);
        }
        _notInGame = _missSince.Where(kv => now - kv.Value >= NotInGameMs).Select(kv => kv.Key).OrderBy(n => n).ToList();
    }

    /// <summary>ゲームに送っているが、現示時素の間なので信号を停止に押さえる進路の名前（連動装置は <see cref="RelayStationData.Interlock"/>）。</summary>
    public IReadOnlyList<string> HeldRoutes { get { lock (_lock) return _held; } }

    private void Event(string type, string id)
    {
        _events.Add((++_seq, type, id));
        if (_events.Count > MaxEvents) _events.RemoveAt(0);
    }

    private void Update(long now)
    {
        _now = now;
        _occ.Clear();
        _occ.UnionWith(_testOcc);
        _occ.UnionWith(_gameOcc);

        // 転換が終わった転てつ器
        foreach (var (p, m) in _moving.ToList())
            if (now >= m.Until) { _point[p] = m.Target; _moving.Remove(p); Event("pointEnd", p); }

        _keyChanged(_keyIn && _keyPos == "R");   // 鍵が R の駅は、自動の進路から切り離す（RouteEngine が見る）
        var local = Local();

        // 方向てこ: てこの向きに変えるのは、駅間に列車がいなくて、その方向の進路（選んで待っているものも）がないときだけ
        foreach (var d in Data.Directions)
            if (_dirSet[d.N] != _dir[d.N] && (!local || !d.Circuits.Any(_occ.Contains)
                    && !_set.Keys.Concat(_request.Values).Any(n => d.Out.Contains(n) || d.In.Contains(n))))
                _dirSet[d.N] = _dir[d.N];
        foreach (var lever in _request.Keys.ToList())
            if (_sig[lever] == "N" || !local) _request.Remove(lever);

        // 集中（CTC）の間は盤で転てつ器を動かさないので、ゲームで開通している進路から転てつ器の向きを写す。
        // 写さないと、盤の転てつ器が前の向き（初めは定位）のままで、線路の灯が列車のいない側に点く（大道寺で 1RB の列車が 1RA 側に点いた）。
        // 進路が解けても、転てつ器はその向きのまま残す。列車が通り抜けたあと一覧に残っている進路からは写さない（次の進路の転換を戻さない）
        if (!local && Data.Interlock.Length > 0) TrackAutoRoutes();
        if (!local && Data.Interlock.Length > 0)
            foreach (var r in Data.Routes)
            {
                if (!_otherRoutes.Contains($"{Data.Interlock}/{r.Name}")) continue;
                if (!_set.ContainsKey(r.Name) && _autoFrom.GetValueOrDefault(r.Name, -1) == int.MaxValue) continue;
                foreach (var (p, need) in r.Points)
                {
                    if (!_point.ContainsKey(p)) continue;
                    // 自動の進路の要求で、その向きへ転換中なら、転換し終わるまで待つ（白い灯は転換し終わってから点く）
                    if (_moving.TryGetValue(p, out var mv) && mv.Target == need) continue;
                    _moving.Remove(p);
                    if (_point[p] != need) { _point[p] = need; Event("pointEnd", p); }
                }
            }
        // 集中のとき、サーバーが自動で要求した進路（まだゲームの一覧に出ていない）の転てつ器を、要求した時点で転換させる。
        // 要求 → 盤で転換 → ゲームで開通を確かめたら白い灯（2026-11-19 ユーザーの決まり）。ほかの要求の向きへ転換中なら、そちらが先。
        // 前の進路（盤の進路・ゲームの一覧に残っている進路）がまだその転てつ器を使っている間と、列車が転てつ器の上にいる間は、転換を待つ
        if (!local && Data.Interlock.Length > 0)
            foreach (var r in Data.Routes)
            {
                if (!_autoRequests.Contains(r.Name)) continue;
                foreach (var (p, need) in r.Points)
                {
                    if (!_point.ContainsKey(p) || _point[p] == need || _moving.ContainsKey(p) || PointLocked(p) || HeldByGameRoute(p, need)) continue;
                    _moving[p] = (need, now + PointTimeMs);
                    Event("pointStart", p);
                }
            }
        if (!local) InferPointsFromTrains();

        // 開通している進路: 列車が入ったか、区分を通り終わったか。発点てこが戻っていれば解く
        foreach (var (name, s) in _set.ToList())
        {
            var r = _routes[name];
            var occG = r.Groups.Select(g => g.Any(_occ.Contains)).ToArray();
            if (!s.Entered && occG.Any(o => o))
            {
                s.Entered = true; s.ReleaseUntil = null; s.Hold = false;
                if (Data.AutoCancel && _sig.GetValueOrDefault(r.Lever) == r.Side) { _sig[r.Lever] = "N"; Event("lever", "s" + r.Lever); }
            }
            for (var i = 0; i < occG.Length; i++)
            {
                if (occG[i]) { s.WasOcc[i] = true; if (s.OccSince[i] == 0) s.OccSince[i] = now; }
                else if (s.WasOcc[i]) s.Passed[i] = true;
            }
            if (!Back(r))
            {
                // 接近鎖錠の時素の間にてこを倒し直しただけでは、進路は戻らない。同じ進路の着点ボタンを押すと戻る（取り直し）
                if (s.ReleaseUntil != null)
                {
                    if (now >= s.ReleaseUntil) { _set.Remove(name); Event("release", name); }
                    continue;
                }
                // てこを倒したまま列車が進路を全部通り抜けたら、最初に進路を作ったときと同じ状態に戻り、信号はまた進行になる
                // （てこが進行の指示を出し続けているのと同じ。2026-11-16 ユーザーの決まり）
                if (s.Entered && !occG.Any(o => o))
                {
                    s.Entered = false;
                    Array.Clear(s.WasOcc);
                    Array.Clear(s.Passed);
                    Array.Clear(s.OccSince);
                    s.GoAfter = now + (r.ClearTime ?? 0) * 1000L;
                    s.Seen = false;
                    Event("route", name);
                }
                continue;
            }
            if (s.Entered)
            {
                // 列車が進路の中にいなくなるか、全部の区分が解けたら進路を解く。いる間は、通り終わった区分（と時間解錠の区分）だけ解ける（GroupLive）
                if (!occG.Any(o => o) || Enumerable.Range(0, occG.Length).All(i => !GroupLive(r, s, i))) { _set.Remove(name); Event("release", name); }
                continue;
            }
            if (s.ReleaseUntil == null)
            {
                if (ApproachOf(r).Any(_occ.Contains))
                {
                    s.ReleaseSec = r.ApproachTime ?? Data.ApproachTime;
                    s.ReleaseUntil = now + s.ReleaseSec * 1000L;
                }
                else { _set.Remove(name); Event("release", name); }
            }
            else if (now >= s.ReleaseUntil) { _set.Remove(name); Event("release", name); }
        }

        // 進路てこ式: 倒れているてこの進路を選ぶ（進路も選んだ進路もまだないとき）
        if (local)
            foreach (var l in Data.SignalLevers)
            {
                var side = _sig[l.N];
                if (side == "N" || _request.ContainsKey(l.N) || LeverHasRoute(l.N)) continue;
                if (Data.Routes.FirstOrDefault(r => r.ByLever && r.Lever == l.N && r.Side == side) is { } lr) _request[l.N] = lr.Name;
            }

        // 着点まで選んだ進路: 自動の転てつ器を向け、条件がそろったら開通させる
        var claims = new Dictionary<string, string>();
        foreach (var (lever, name) in _request.OrderBy(kv => _revAt.GetValueOrDefault(kv.Key)).ToList())
        {
            var r = _routes[name];
            if (LeverHasRoute(lever)) { _request.Remove(lever); continue; }
            if (CanSet(r) && ClaimSections(r))
            {
                var n = r.Groups.Count;
                _set[name] = new SetRoute { WasOcc = new bool[n], Passed = new bool[n], OccSince = new long[n], GoAfter = now + (r.ClearTime ?? 0) * 1000L };
                _request.Remove(lever);
                Event("route", name);
                continue;
            }
            if (!Feasible(r, claims)) continue;
            foreach (var (p, need) in r.Points) claims[p] = need;
        }

        // 総括制御で待っている進路: 元の進路のてこが戻ったら取りやめる。開通させた進路は Hold で保つ
        foreach (var (name, fromLever) in _through.ToList())
        {
            var r = _routes[name];
            if (!local || _sig[fromLever] == "N" || LeverHasRoute(r.Lever)) { _through.Remove(name); continue; }
            if (CanSet(r) && ClaimSections(r))
            {
                var n = r.Groups.Count;
                _set[name] = new SetRoute { WasOcc = new bool[n], Passed = new bool[n], OccSince = new long[n], GoAfter = now + (r.ClearTime ?? 0) * 1000L, Hold = true };
                _through.Remove(name);
                Event("route", name);
                continue;
            }
            if (!Feasible(r, claims)) continue;
            foreach (var (p, need) in r.Points) claims[p] = need;
        }

        // 自分の出発進路がなくなった単線区間は放す
        foreach (var d in Data.Directions)
            if (d.Section.Count == 2 && !_set.Keys.Any(d.Out.Contains)) _claims.Release(d.Key, Data.Id);

        foreach (var p in _point.Keys.ToList())
        {
            var lev = _ptLever[p];
            // 自動（A）: 選んで待っている進路が求める向きへだけ動かす。求める進路がなければ今の向きのまま（R・N で転換した向きを保つ）
            var target = lev == "A" ? claims.GetValueOrDefault(p, _point[p]) : lev;
            if (local && _point[p] != target && !_moving.ContainsKey(p) && !PointLocked(p))
            {
                _moving[p] = (target, now + PointTimeMs);
                Event("pointStart", p);
            }
        }

        // 接近ボタン: 接近区間に列車が入ったら鳴らす
        foreach (var b in Data.ApproachBells)
        {
            var st = _bells[b.Id];
            // 鳴り始めは circuits か、when の進路が出ているときの回路。いちど鳴ったら、進路が消えても列車が接近の回路にいる間は続ける（鳴り直さない）
            var occ = b.Circuits.Any(_occ.Contains) || b.When.Any(w => w.Routes.Any(_otherRoutes.Contains) && w.Circuits.Any(_occ.Contains))
                      || st.WasOcc && b.When.Any(w => w.Circuits.Any(_occ.Contains));
            if (occ && !st.WasOcc) { st.Ringing = true; st.Acked = false; Event("bell", b.Id); }
            if (!occ) { st.Ringing = false; st.Acked = false; }
            st.WasOcc = occ;
        }

        // 現示時素は、ゲームで進路が開通した（踏切が鳴り始めた）ときから数え直す。ゲームに誰もいなければ、盤で開通したときから
        foreach (var r in Data.Routes)
        {
            if (r.ClearTime is not > 0 || !Ready(r)) continue;
            var s = _set[r.Name];
            if (s.Seen || !_otherRoutes.Contains($"{Data.Interlock}/{r.Name}")) continue;
            s.Seen = true;
            s.GoAfter = Math.Max(s.GoAfter, now + r.ClearTime.Value * 1000L);
        }

        TrackAutoRoutes();
        UpdateGameRoutes();
        TrackNotInGame(now);
        _held = Data.Interlock.Length == 0 ? new() : Data.Routes.Where(Held).Select(r => r.Name).ToList();
        Publish();
    }

    /// <summary>
    /// 在線位置の画面（tid.html）用: 盤で取っている進路。circuits はまだ鎖錠している回路（列車が入ったあとは、通り終わった区分を除く）。
    /// open はゲームの進路の一覧に出ている（実線。自動の進路の緑・盤の白い灯と同じ条件）、それ以外は転換待ち・現示時素の間・
    /// ゲームに出るのを待っている間（破線）。approach は線をどちらの枝へ引くかの手がかり
    /// </summary>
    public List<object> TidRoutes(Func<string, string, (string? Dest, string From, string To)> infoOf)
    {
        lock (_lock)
        {
            // 取っている進路と、選んで待っている進路（転てつ器の転換中など、まだ進路ができていない。要求中＝破線）
            var items = _set.OrderBy(kv => kv.Key).Select(kv =>
            {
                var r = _routes[kv.Key];
                var s = kv.Value;
                return (r, open: InGame(r), circuits: (s.Entered ? LiveCircuits(r, s) : r.Circuits.Except(r.Overrun)).ToList());
            }).ToList();
            foreach (var name in _request.Values.Concat(_through.Keys).Distinct().Where(n => !_set.ContainsKey(n) && _routes.ContainsKey(n)).OrderBy(n => n))
                items.Add((_routes[name], false, _routes[name].Circuits.Except(_routes[name].Overrun).ToList()));
            var info = _routes.Keys.ToDictionary(n => n, n => infoOf(Data.Interlock, n));
            bool Leads(string from, string to) => info[from].To.Length > 0 && !info[from].To.EndsWith('方') && info[to].From == info[from].To;
            // 続けて取っている進路の回路。まだ取っていなければ、この進路の行き先から出る進路すべてに入っている回路
            // （大道寺の 13LD は 44T まで。44T から出るのは 12L・28L で、どちらも 4番線の 12LT を通る。13LD で 5番・6番へは入らない）
            List<string> Next(RelayRoute r)
            {
                var taken = items.Where(y => y.r != r && Leads(r.Name, y.r.Name)).SelectMany(y => y.r.Circuits).Distinct().ToList();
                if (taken.Count > 0) return taken;
                var all = _routes.Values.Where(y => y != r && Leads(r.Name, y.Name)).Select(y => y.Circuits).ToList();
                return all.Count == 0 ? [] : all.Skip(1).Aggregate(all[0].AsEnumerable(), (a, b) => a.Intersect(b)).ToList();
            }
            return items.Select(x => (object)new
            {
                route = x.r.Name,
                open = x.open,
                circuits = x.circuits,
                // 進路の回路全部（行き先の番線も）。列車が入って circuits が鎖錠中の区分だけになっても、
                // 在線位置の画面がどちらの枝を引くか決められるように（津崎の 6LC で、24T の渡り線側に線が出た）
                all = x.r.Circuits,
                approach = x.r.Approach,
                // 行き先の番線の回路。回路の並びが同じで行き先だけ違う進路（新野崎の 10LC・10LD）を、行き先の枝へ引くため
                dest = info[x.r.Name].Dest,
                // 続く進路（この進路の行き先から出る進路）の回路。行き先が分かれる回路で終わる進路を、続く進路の側へ引くため
                // （大道寺の 13LD は 44T まで。これがないと、44T を 6番側の枝で引いてしまう）
                next = Next(x.r)
            }).ToList();
        }
    }

    // 盤で進路を取れるのは、管理画面で駅扱いになっていて、盤の解放の鍵が R のときだけ。
    // それ以外のときも、てこは倒せてボタンは押せるが、効果はない（進路を選ばない・転てつ器を動かさない。戻したてこで進路が解けるのは同じ）
    private bool Local() => _adminLocal() && _keyIn && _keyPos == "R";

    private string? AreaOfLever(string n) => Data.SignalLevers.FirstOrDefault(l => l.N == n)?.Area;

    private object Snapshot() => new
    {
        sig = _sig,
        req = _request,
        ptLever = _ptLever,
        point = _point,
        moving = _moving.Keys.ToList(),
        locked = _point.Keys.Where(PointLocked).ToList(),
        occ = _occ.OrderBy(c => c).ToList(),
        testOcc = _testOcc.OrderBy(c => c).ToList(),
        caps = _caps.OrderBy(c => c).ToList(),   // 誤操作防止のキャップをかぶせたてこ・ボタン
        // ラインライトの白: 開通している進路の、列車がまだ通り終わっていない回路（LitCircuits）
        route = LitCircuits().Distinct().OrderBy(c => c).ToList(),
        lit = LitRoutes().Select(x => new { r = x.Route, c = x.Circuits }).ToList(),   // 白く点ける進路ごとの回路（灯の側は進路の points で決める）
        set = _set.Keys.OrderBy(n => n).ToList(),
        go = GoRoutes().ToList(),   // 信号表示灯（信号が開いている進路）
        notInGame = _notInGame,   // 盤で取ったのに、列車が近づいてもゲームの一覧に載らない進路（転てつ不良の灯・信号表示灯の点滅）
        game = _gameRoutes.Select(r => r.RouteName).ToList(),   // 全員のゲームに送っている進路
        timers = _set.Where(kv => kv.Value.ReleaseUntil != null).ToDictionary(kv => kv.Key, kv => new { until = kv.Value.ReleaseUntil, sec = kv.Value.ReleaseSec }),
        area = _area,
        local = Local(),
        adminLocal = _adminLocal(),
        key = new { inserted = _keyIn, pos = _keyPos, by = _keyBy },
        allStop = _allStop,
        dir = _dir,
        // 方向の灯: いま取れている区間方向。分からないとき（集中で、まだ向きが決まっていない）は "N"（どちらも点けない）
        dirActual = Data.Directions.ToDictionary(d => d.N, d => _sectionDir(d.Key) is { } a ? d.LeverOf(a)
            : d.Area == null || _area.GetValueOrDefault(d.Area) == "R" ? _dirSet[d.N] : "N"),
        bells = _bells.ToDictionary(kv => kv.Key, kv => new { ringing = kv.Value.Ringing && !kv.Value.Acked, occ = kv.Value.WasOcc })
    };

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private void Publish()
    {
        var json = JsonSerializer.Serialize(Snapshot(), Json);
        var hasNewEvents = _events.Count > 0 && _events[^1].Seq > _publishedSeq;
        if (json == _lastJson && !hasNewEvents) return;
        _lastJson = json;
        _publishedSeq = _seq;
        _version++;
        var old = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        old.TrySetResult();
    }
    private long _publishedSeq;

    /// <summary>配る中身（状態と、seq より後の出来事）。</summary>
    public (long Version, long Seq, string Json) Read(long afterSeq)
    {
        lock (_lock)
        {
            var events = _events.Where(e => e.Seq > afterSeq).Select(e => new { type = e.Type, id = e.Id }).ToList();
            var json = "{\"v\":" + _version + ",\"now\":" + Now() + ",\"state\":" + _lastJson + ",\"events\":" + JsonSerializer.Serialize(events, Json) + "}";
            return (_version, _seq, json);
        }
    }

    public long CurrentSeq { get { lock (_lock) return _seq; } }

    // ---- 時間の経過・ゲームの在線 ----

    private HashSet<string> _otherRoutes = new();   // ゲームで開通している進路（"連動装置/進路"）。接近ボタンの when に使う
    private IReadOnlyList<RelayTrain> _trains = Array.Empty<RelayTrain>();

    /// <summary>
    /// 集中（CTC）の間、ゲームの進路が出ていないまま列車が転てつ器の上を通ったとき（側線から出る列車・入換など）、
    /// 列車が踏んできた回路の並びに合う盤の進路から、転てつ器の向きを推して写す（大道寺で YT から 50ロT・ET・48T・44T を通って
    /// 12LT へ出る列車は、場内も入換の進路も出ないので、50・48・46 が前の向きのまま、線路の灯が列車のいない側に点いた）。
    /// 進路の並び（接近区間＋進路の回路＋総括制御の続きの進路の回路）のうち、列車が踏んだ回路が続けて2つ以上並び、
    /// 進路の先がまだ続くのに列車がほかの回路へ行ったものは除き、いちばん多く踏んだ進路どうしが同じ向きを言う転てつ器だけ変える。
    /// ゲームの開通している進路が使う転てつ器は、そちらを優先して触らない
    /// </summary>
    private void InferPointsFromTrains()
    {
        if (_trains.Count == 0) return;
        // ゲームの一覧の進路と、盤で取ってある進路が使う転てつ器は、推しで変えない
        var claimed = Data.Routes.Where(r => _otherRoutes.Contains($"{Data.Interlock}/{r.Name}") || _set.ContainsKey(r.Name)).SelectMany(r => r.Points.Keys).ToHashSet();
        foreach (var t in _trains)
        {
            if (!t.Occ.Any(_known.Contains)) continue;
            var best = new Dictionary<string, (int Score, string? Pos)>(); // 転てつ器 → いちばん多く踏んだ進路の数と向き（食い違えば null）
            foreach (var r in Data.Routes)
            {
                if (r.Points.Count == 0) continue;
                var seq = r.Approach.Concat(r.Circuits).ToList();
                if (r.Through.Count == 1 && _routes.TryGetValue(r.Through.Values.First(), out var next)) seq.AddRange(next.Circuits);
                var idx = Enumerable.Range(0, seq.Count).Where(i => t.Touched.Contains(seq[i])).ToList();
                if (idx.Count < 2 || idx[^1] - idx[0] + 1 != idx.Count) continue;   // 2つ以上、とびとびでなく
                if (!r.Circuits.Any(t.Touched.Contains)) continue;
                if (idx[^1] < seq.Count - 1 && t.Touched.Any(c => !seq.Contains(c))) continue;   // 進路の先へ行かず、ほかへ行った
                foreach (var (p, pos) in r.Points)
                {
                    if (claimed.Contains(p) || !_point.ContainsKey(p) || !_pointCircuit[p].Any(t.Occ.Contains)) continue;   // いま転てつ器の上にいる列車だけ
                    if (!best.TryGetValue(p, out var b) || idx.Count > b.Score) best[p] = (idx.Count, pos);
                    else if (idx.Count == b.Score && b.Pos != pos) best[p] = (b.Score, null);
                }
            }
            foreach (var (p, (_, pos)) in best)
            {
                if (pos == null || _point[p] == pos) continue;
                _moving.Remove(p);
                _point[p] = pos;
                Event("pointEnd", p);
            }
        }
    }

    private HashSet<string> _autoRequests = new(); // サーバーが自動で要求していて、まだゲームの一覧に出ていない、この駅の進路
    private HashSet<string> _autoHeld = new();     // サーバーが自動で取った進路のうち、現示時素（踏切防護）の間で信号を停止に押さえている、この駅の進路

    public void Tick(HashSet<string> gameOcc, HashSet<string>? gameRoutes = null, IReadOnlyList<RelayTrain>? trains = null,
        IEnumerable<(string Interlock, string Route)>? autoRequests = null, IEnumerable<(string Interlock, string Route)>? autoHeld = null)
    {
        lock (_lock)
        {
            _otherRoutes = gameRoutes ?? new();
            _autoRequests = (autoRequests ?? Array.Empty<(string, string)>()).Where(a => a.Interlock == Data.Interlock).Select(a => a.Route).ToHashSet();
            _autoHeld = (autoHeld ?? Array.Empty<(string, string)>()).Where(a => a.Interlock == Data.Interlock).Select(a => a.Route).ToHashSet();
            _trains = trains ?? Array.Empty<RelayTrain>();
            var mine = gameOcc.Where(_known.Contains).ToHashSet();
            if (!mine.SetEquals(_gameOcc)) _gameOcc = mine;
            Update(Now());
        }
    }

    // ---- 操作 ----

    /// <summary>盤の操作。できなければ理由を返す。</summary>
    /// <summary>扱った後の向きを、記録の文にする（変わらなかった操作や、記録しない操作は null）。</summary>
    private string? LogText(string op, string id, string dir) => op switch
    {
        "signal" => $"信号てこ {id} を {(_sig.GetValueOrDefault(id) is "N" or null ? "定位" : _sig[id] + " に反位")}",
        "point" => $"転てつてこ {id} を {_ptLever.GetValueOrDefault(id) switch { "N" => "定位", "R" => "反位", _ => "自動" }}",
        "dest" => $"着点ボタン {id}",
        "direction" => $"方向てこ {id} を {_dir.GetValueOrDefault(id)}",
        "area" => $"駅扱いてこ {id} を {(_area.GetValueOrDefault(id) == "R" ? "駅扱" : "集中")}",
        "allStop" => $"一斉停止てこを {(_allStop == "R" ? "反位" : "定位")}",
        "key" when id == "turn" => _keyPos == "R" ? "解放の鍵を R（CTC から解放）" : "解放の鍵を N（自動に復位）",
        _ => null
    };

    public string? Operate(string op, string id, string? dir, bool keyAllowed, string? crewId)
    {
        lock (_lock)
        {
            var wasLocal = Local();
            var d = dir is "L" or "R" ? dir : "R";
            var error = OperateCore(op ?? "", id ?? "", d, keyAllowed, crewId);
            // 管理画面の記録: 解放（鍵を R）から自動に復位（鍵を N）するまでの、てこ・ボタンの扱い
            if (error == null && (wasLocal || Local()) && LogText(op ?? "", id ?? "", d) is { } text)
                _note($"信号制御盤 {Data.Name}: {text}{(crewId != null ? $"（{crewId}）" : "")}");
            Update(Now());
            return error;
        }
    }

    // 誤操作防止のキャップ（"s:12" 発点てこ・"p:31" 転てつ器てこ・"d:A" 着点ボタン・"allStop" 一斉停止・"dir:11" 方向てこ・"a:98" 駅扱の切換てこ）。
    // かぶせている間、そのてこ・ボタンは動かない。盤の状態として全員の画面に出す（サーバーを再起動するまで残る）
    private readonly HashSet<string> _caps = new();

    private bool CapOk(string cap) => cap == "allStop" && Data.AllStop != null
        || cap.StartsWith("s:") && Data.SignalLevers.Any(l => "s:" + l.N == cap)
        || cap.StartsWith("p:") && _ptLever.ContainsKey(cap[2..])
        || cap.StartsWith("d:") && Data.DestButtons.Any(b => "d:" + b.Id == cap)
        || cap.StartsWith("dir:") && Data.Directions.Any(d => "dir:" + d.N == cap)
        || cap.StartsWith("a:") && Data.Areas.Any(a => "a:" + a.Id == cap);

    private string? OperateCore(string op, string id, string dir, bool keyAllowed, string? crewId)
    {
        // キャップをかぶせたてこ・ボタンは、扱っても何も起きない
        var capOf = op switch { "signal" => "s:" + id, "point" => "p:" + id, "dest" => "d:" + id, "allStop" => "allStop", "direction" => "dir:" + id, "area" => "a:" + id, _ => null };
        if (capOf != null && _caps.Contains(capOf)) return "";   // 文は出さない（記録にも残さない）
        switch (op)
        {
            case "cap":
            {
                if (!CapOk(id)) return "そのてこ・ボタンはありません";
                if (!_caps.Remove(id)) _caps.Add(id);
                return null;
            }
            case "signal":
            {
                var l = Data.SignalLevers.FirstOrDefault(x => x.N == id);
                if (l == null) return "そのてこはありません";
                var cur = _sig[id];
                var next = cur == "N" ? (l.Side.Contains(dir) ? dir : "N") : (cur == dir ? cur : "N");
                if (next == cur) return null;
                _sig[id] = next;
                if (next != "N") _revAt[id] = Now();
                Event("lever", "s" + id);
                // 総括制御で保っている進路は、そのてこを倒すと、ふつうの進路に戻る（てこを戻せば解ける）
                foreach (var (n, s) in _set)
                    if (s.Hold && _routes[n].Lever == id && next == _routes[n].Side) s.Hold = false;
                // 進路てこ式の取り直し: 接近鎖錠の時素の間に、てこを同じ向きに倒し直すと、すぐ進路が戻る
                if (next != "N" && Local() && Data.Routes.FirstOrDefault(r => r.ByLever && r.Lever == id && r.Side == next
                        && _set.TryGetValue(r.Name, out var rs) && rs.ReleaseUntil != null) is { } back)
                {
                    _set[back.Name].ReleaseUntil = null;
                    Event("route", back.Name);
                }
                return null;
            }
            case "point":
            {
                if (!_ptLever.TryGetValue(id, out var cur)) return "そのてこはありません";
                string[] order = ["N", "A", "R"];
                var k = Math.Clamp(Array.IndexOf(order, cur) + (dir == "L" ? -1 : 1), 0, 2);
                if (order[k] == cur) return null;
                _ptLever[id] = order[k];
                Event("lever", "p" + id);
                return null;
            }
            case "dest":
            {
                var b = Data.DestButtons.FirstOrDefault(x => x.Id == id);
                if (b == null) return "そのボタンはありません";
                Event("button", "d" + id);
                if (!Local()) return null;
                // 取り直し: 接近鎖錠の時素の間に、てこを倒し直して同じ進路の着点ボタンを押すと、すぐ進路が戻る
                var retake = Data.Routes.FirstOrDefault(r => r.Dest == id && _sig[r.Lever] == r.Side && _set.TryGetValue(r.Name, out var rs) && rs.ReleaseUntil != null);
                if (retake != null)
                {
                    _set[retake.Name].ReleaseUntil = null;
                    Event("route", retake.Name);
                    return null;
                }
                // 倒れている発点てこ（いちばん新しく倒したもの）から、このボタンまでの進路を選ぶ
                var cand = Data.Routes.Where(r => r.Dest == id && _sig[r.Lever] == r.Side && !LeverHasRoute(r.Lever))
                    .OrderByDescending(r => _revAt.GetValueOrDefault(r.Lever)).FirstOrDefault();
                if (cand != null) { _request[cand.Lever] = cand.Name; return null; }
                // 総括制御: 倒れている第一場内のてこから、この着点まで（第一場内と第二場内をいっしょに）
                var first = Data.Routes.Where(r => r.Through.ContainsKey(id) && _sig[r.Lever] == r.Side)
                    .OrderByDescending(r => _revAt.GetValueOrDefault(r.Lever)).FirstOrDefault();
                if (first == null) return null;
                if (!_set.ContainsKey(first.Name))
                {
                    if (LeverHasRoute(first.Lever)) return null;   // 同じてこの別の進路がある
                    _request[first.Lever] = first.Name;
                }
                var second = _routes[first.Through[id]];
                if (!LeverHasRoute(second.Lever)) _through[second.Name] = first.Lever;
                return null;
            }
            case "area":
            {
                if (!_area.ContainsKey(id)) return "その切換てこはありません";
                var next = dir == "R" ? "R" : "N";
                if (_area[id] == next) return null;
                _area[id] = next;
                Event("lever", "a" + id);
                return null;
            }
            case "direction":
            {
                var d = Data.Directions.FirstOrDefault(x => x.N == id);
                if (d == null) return "その方向てこはありません";
                var next = dir == "R" ? "R" : "L";
                if (_dir[id] == next) return null;
                _dir[id] = next;   // てこは倒れる。向きが変わるのは、条件がそろってから（Update）
                Event("lever", "d" + id);
                return null;
            }
            case "allStop":
            {
                var next = dir == "R" ? "R" : "N";
                if (_allStop == next) return null;
                _allStop = next;
                Event("lever", "allStop");
                return null;
            }
            case "key":
            {
                if (Data.ReleaseKey == null) return "鍵はありません";
                if (!keyAllowed) return "鍵を扱えるのは、勤怠管理で役職「" + CrewRoster.RelayKeyRole + "」を振られた人だけです";
                switch (id)
                {
                    case "insert":
                        if (_keyIn) return null;
                        _keyIn = true; _keyPos = "N"; _keyBy = crewId;
                        Event("keyIn", "release");
                        return null;
                    case "remove":
                        if (!_keyIn) return null;
                        if (_keyPos != "N") return "鍵を N に戻してから抜いてください";
                        _keyIn = false; _keyBy = null;
                        Event("keyOut", "release");
                        return null;
                    case "turn":
                        if (!_keyIn) return "鍵が刺さっていません";
                        var next = dir == "R" ? "R" : "N";
                        if (_keyPos == next) return null;
                        _keyPos = next;
                        Event("keyTurn", "release");
                        return null;
                }
                return "その操作はありません";
            }
            case "bell":
            {
                if (!_bells.TryGetValue(id, out var b)) return "そのボタンはありません";
                Event("button", "b" + id);
                if (b.Ringing) b.Acked = true;
                return null;
            }
            case "occ":
            {
                // 試験用: 線路をクリックして在線を置く・消す（relaypanel.html?test=1 のときだけ画面から送る）
                if (!_known.Contains(id)) return "その回路はありません";
                if (!_testOcc.Remove(id)) _testOcc.Add(id);
                return null;
            }
        }
        return "その操作はありません";
    }
}

/// <summary>全駅の盤。駅のデータを読み、0.2 秒ごとに時間を進め、ゲームの在線を渡す。</summary>
public sealed class RelayPanels : IDisposable
{
    private readonly Dictionary<string, RelayStation> _stations = new(StringComparer.OrdinalIgnoreCase);
    private readonly SectionClaims _sectionClaims = new();   // 単線区間の出発の取り合い（盤どうし）
    private readonly PlayerRegistry _players;
    private readonly CentralControl _central;
    private readonly Timer _timer;

    public RelayPanels(IWebHostEnvironment env, PlayerRegistry players, CentralControl central, IncidentManager incidents, ILogger<RelayPanels> logger)
    {
        _players = players;
        _central = central;
        var root = env.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        // 信号制御盤と電子連動装置（denshirendo.html）の駅。どちらも同じ仕組みで動かす（駅ID は1駅に1つ）
        foreach (var dir in new[] { Path.Combine(root, "relaypanel", "stations"), Path.Combine(root, "denshirendo", "stations") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var data = JsonSerializer.Deserialize<RelayStationData>(text, options);
                    if (data == null || string.IsNullOrEmpty(data.Id)) continue;
                    var id = data.Id;
                    _stations[id] = new RelayStation(data, text, () => central.IsLocal(id), turned => central.SetPanelKey(id, turned), central.SectionDir, incidents.Note, _sectionClaims);
                }
                catch (Exception e)
                {
                    logger.LogWarning("信号制御盤のデータを読めません {File}: {Message}", Path.GetFileName(file), e.Message);
                }
            }
        }
        logger.LogInformation("信号制御盤: {Count}駅（{Ids}）", _stations.Count, string.Join("・", _stations.Keys));
        central.PanelRoutes = () => _stations.Values.SelectMany(st => st.GameRoutes).ToList();
        central.PanelHeldRoutes = () => _stations.Values.SelectMany(st => st.HeldRoutes.Select(r => (st.Data.Interlock, r))).ToList();
        central.PanelSectionDirs = () => _stations.Values.SelectMany(st => st.PanelSectionDirs().ToList()).GroupBy(x => x.Section).ToDictionary(g => g.Key, g => g.First().Dir);
        _timer = new Timer(_ => Tick(), null, 200, 200);
    }

    public RelayStation? Get(string id) => _stations.GetValueOrDefault(id ?? "");

    /// <summary>
    /// 在線位置の画面（tid.html）用: 駅ごとに、盤で扱っているか（管理画面で駅扱い、かつ盤の解放の鍵が R）と、盤で取っている進路。
    /// </summary>
    public object TidView(Func<string, string, (string? Dest, string From, string To)> infoOf) => _stations.Select(kv => new
    {
        station = kv.Key,
        controlled = _central.IsPanelControlled(kv.Key),
        routes = kv.Value.TidRoutes(infoOf)
    }).ToList();

    private readonly Dictionary<string, Dictionary<string, long>> _seen = new(); // 接続ID → 回路 → 最後に踏んでいた時刻（Tick の中だけで使う）
    private int _ticking;
    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            var occ = new HashSet<string>();
            var routes = new HashSet<string>();
            var now = Environment.TickCount64;
            var trains = new List<RelayTrain>();
            var players = _players.Snapshot();
            foreach (var p in players)
            {
                occ.UnionWith(p.TrackCircuits);
                foreach (var (il, rt) in p.GameRoutes) routes.Add($"{il}/{rt}");
                // 列車ごとに、いまいる回路と、少し前（60秒以内）に踏んでいた回路（盤が転てつ器の向きを推す手がかり）
                if (p.TrackCircuits.Count == 0) continue;
                if (!_seen.TryGetValue(p.ConnectionId, out var seen)) _seen[p.ConnectionId] = seen = new();
                foreach (var c in p.TrackCircuits) seen[c] = now;
                foreach (var c in seen.Where(kv => now - kv.Value > 60_000).Select(kv => kv.Key).ToList()) seen.Remove(c);
                trains.Add(new RelayTrain(p.TrackCircuits.ToHashSet(), seen.Keys.ToHashSet()));
            }
            foreach (var id in _seen.Keys.Where(id => players.All(p => p.ConnectionId != id)).ToList()) _seen.Remove(id);
            var auto = _central.AutoRouteRequests(); // ロックの外で（RouteEngine のロックの中から盤を読むので、盤のロックの中では呼ばない）
            var held = _central.AutoHeldRoutes();
            foreach (var s in _stations.Values) s.Tick(occ, routes, trains, auto, held);
        }
        catch { /* 次の回でやり直す */ }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    /// <summary>盤のある駅（駅ID と駅名）。内線電話の駅の一覧に使う。</summary>
    public List<(string Id, string Name)> StationList() =>
        _stations.Values.Select(s => (s.Data.Id, s.Data.Name.Length > 0 ? s.Data.Name : s.Data.Id)).OrderBy(s => s.Id, StringComparer.Ordinal).ToList();

    public void Dispose() => _timer.Dispose();
}
