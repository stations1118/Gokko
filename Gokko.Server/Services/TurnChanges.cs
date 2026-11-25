namespace Gokko.Server.Services;

/// <summary>
/// 後運用の差し替え（ダイヤ運転中に、遅れた列車を別の運用に折り返しでつなぐ）。
/// 列車 A の後運用（終点で折り返して次になる列車）を C に差し替えると、もともと C に折り返すはずだった列車 X が、
/// A のもとの後運用 B を受け持つ（2つの運用の、そこから先を入れ替える）。折返の時刻・仕方（発／入換）は後運用の列車のものなので、
/// A と X の「終点のあと」（<see cref="DiaTurn"/>）をそのまま入れ替える。C の前の列車がなければ、A を C につなぐだけ。
/// 運行番号も運用ごと入れ替わる（差し替えた先の列車とその先は、つないだ列車の運行番号になる）。
/// 時刻表（Timetable.json）は書き換えず、メモリに持つだけ（サーバーを再起動すると消える）。取消で元に戻す。
/// 直接折返（<see cref="SetDirect"/>）: 折返が「入換」の列車を、引上げずに着いた番線でそのまま折り返させる（大道寺で 2番線 → Y線 → 4番線の代わりに 3番線で）。
/// 後運用の列番はそのままで、折返の仕方だけ「発」にする。番線の指定（着く列車と後運用の列車をその番線に）は呼ぶ側が集中扱いで入れる。
/// </summary>
public sealed class TurnChanges(RailData rail)
{
    private readonly object _lock = new();
    // 列番 → 差し替えた「終点のあと」（null は「なし」に差し替えた）
    private readonly Dictionary<string, DiaTurn?> _over = new();
    private readonly List<Change> _changes = new();
    private Dictionary<string, string>? _origPred;
    private Dictionary<string, string>? _pred;
    private Dictionary<string, string>? _nextPred;

    private sealed record Change(string A, string? X, DiaTurn? PrevA, DiaTurn? PrevX, DiaTurn? NewA, DiaTurn? NewX, DirectTurn? Direct = null);

    /// <summary>
    /// 直接折返: 列車 Train が駅 Station の Track 番線に着き、そこで後運用 Next になって出る。
    /// Moved は、その間に Track 番線を使うはずだった列車で、後運用のもとの番線（大道寺なら 4番線）へ移すもの（列番, 番線）。
    /// Midway は途中折返し（Station は Train の途中の駅。Train はそこで打ち切り、Next はそこから走る）。
    /// </summary>
    public sealed record DirectTurn(string Train, string Station, int Track, string Next, IReadOnlyList<(string Train, int Track)> Moved, bool Midway = false);

    // 直接折返・途中折返しにしている列車（列番 → 指定）
    private readonly Dictionary<string, DirectTurn> _direct = new();

    /// <summary>途中折返しで、train から折り返して走る列車の指定（train が後運用の側。なければ null）。</summary>
    public DirectTurn? MidwayInto(string train)
    {
        var no = RailData.NormalizeTrain(train);
        lock (_lock) return _direct.Values.FirstOrDefault(d => d.Midway && d.Next == no);
    }

    /// <summary>直接折返・途中折返しの指定（なければ null）。</summary>
    public DirectTurn? DirectOf(string train)
    {
        lock (_lock) return _direct.GetValueOrDefault(RailData.NormalizeTrain(train));
    }

    // 駅 → その駅に止まる列車と、その向き・その駅の発時刻（途中折返しの候補を探す。ダイヤが変われば作り直す）
    private Dictionary<string, List<(DiaTrain D, int Index, Dir? Dir, double? Dep)>>? _byStation;

    /// <summary>差し替えるたびに増える（ダイヤグラムの計画の線を読み直す合図）。</summary>
    public int Version { get; private set; }

    public event Action<string>? Changed;

    /// <summary>その列車の「終点のあと」。差し替えていればそれ、なければ時刻表のもの。</summary>
    public DiaTurn? TurnOf(DiaTrain d)
    {
        lock (_lock) return _over.TryGetValue(d.Number, out var t) ? t : d.Turn;
    }

    public bool IsChanged(string train)
    {
        lock (_lock) return _over.ContainsKey(RailData.NormalizeTrain(train));
    }

    /// <summary>運行番号（差し替えたあとは、つないだ列車の運行番号を受け継ぐ）。</summary>
    public string? UnyoOf(DiaTrain d)
    {
        lock (_lock) return _over.Count == 0 ? d.Unyo : Unyo(d.Number, 0);
    }

    private string? Unyo(string number, int depth)
    {
        var d = rail.Dia.GetValueOrDefault(number);
        if (d == null) return null;
        if (depth < 60 && (Pred().TryGetValue(number, out var p) || NextPred().TryGetValue(number, out p)) && rail.Dia.TryGetValue(p, out var pd))
        {
            var pu = Unyo(p, depth + 1);
            if (_over.ContainsKey(p) || pu != pd.Unyo) return pu;
        }
        return d.Unyo;
    }

    // 時刻表の折返のつなぎ（後運用の列番 → 前の列車）
    private Dictionary<string, string> OrigPred()
    {
        if (_origPred != null) return _origPred;
        var m = new Dictionary<string, string>();
        foreach (var d in rail.Dia.Values)
            if (d.Turn is { Kind: "折返", Number: { } n }) m.TryAdd(n, d.Number);
        return _origPred = m;
    }

    // 差し替えを入れた折返のつなぎ
    private Dictionary<string, string> Pred()
    {
        if (_pred != null) return _pred;
        var m = new Dictionary<string, string>(OrigPred());
        foreach (var (a, _) in _over)
            if (rail.Dia.TryGetValue(a, out var d) && d.Turn is { Kind: "折返", Number: { } n } && m.GetValueOrDefault(n) == a) m.Remove(n);
        foreach (var (a, t) in _over)
            if (t is { Kind: "折返", Number: { } n }) m[n] = a;
        return _pred = m;
    }

    // 途中駅の列番の読み替え（読み替えたあとの列番 → 前の列番）
    private Dictionary<string, string> NextPred()
    {
        if (_nextPred != null) return _nextPred;
        var m = new Dictionary<string, string>();
        foreach (var d in rail.Dia.Values)
            if (d.Next is { } nx) m.TryAdd(nx.Number, d.Number);
        return _nextPred = m;
    }

    private static string? Terminal(DiaTrain d) => d.Stops.Count > 0 ? d.Stops[^1].StationId : null;
    private static string? Origin(DiaTrain d) => d.Stops.Count > 0 ? d.Stops[0].StationId : null;

    private static double? Sec(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var p = text.Split(':');
        if (p.Length < 2 || !int.TryParse(p[0], out var hh) || !int.TryParse(p[1], out var mm)) return null;
        return hh * 3600 + mm * 60 + (p.Length > 2 && int.TryParse(p[2], out var ss) ? ss : 0);
    }

    /// <summary>
    /// 画面に出す後運用（列車別設定・ダイヤグラムの◆の窓）。number はいまの後運用、original は時刻表の後運用、
    /// candidates は差し替えられる列車（終点の駅から、着いたあとに出る列車。早い順）。
    /// </summary>
    public object? View(DiaTrain? d)
    {
        if (d == null) return null;
        lock (_lock)
        {
            // 候補を作るには全ダイヤを調べるので、差し替え・ダイヤが変わる（Version が増える）まで、列車ごとの答えを覚えておく。
            // ダイヤグラム・列車別設定を作るたびに、走っている列車の数だけ作り直していた
            if (_viewsVersion != Version)
            {
                _views.Clear();
                _viewsVersion = Version;
            }
            if (_views.TryGetValue(d.Number, out var kept) && ReferenceEquals(kept.Dia, d)) return kept.View;
            var view = BuildView(d);
            _views[d.Number] = (d, view);
            return view;
        }
    }

    // 列番 → 覚えている View の答え（_viewsVersion のときのもの）
    private readonly Dictionary<string, (DiaTrain Dia, object View)> _views = new();
    private int _viewsVersion = -1;

    private object BuildView(DiaTrain d)
    {
        {
            var t = _over.TryGetValue(d.Number, out var o) ? o : d.Turn;
            var changed = _over.ContainsKey(d.Number);
            var term = Terminal(d);
            var candidates = new List<object>();
            if (term != null && rail.Line.ZoneOfStation(term) >= 0)
            {
                var arr = Sec(d.Stops[^1].Arr) ?? Sec(d.Stops[^1].Dep) ?? 0;
                candidates = rail.Dia.Values
                    .Where(c => c.Number != d.Number && Origin(c) == term && !NextPred().ContainsKey(c.Number))
                    .Select(c => (c, dep: Sec(c.Stops[0].Dep) ?? Sec(c.Stops[0].Arr)))
                    .Where(x => x.dep is { } s && s >= arr && s <= arr + 3 * 3600)
                    .OrderBy(x => x.dep)
                    .Take(30)
                    .Select(x => (object)new
                    {
                        train = x.c.Number,
                        type = x.c.Type,
                        time = x.c.Stops[0].Dep ?? x.c.Stops[0].Arr,
                        unyo = Unyo(x.c.Number, 0),
                        prev = Pred().GetValueOrDefault(x.c.Number)
                    }).ToList();
            }
            return new
            {
                kind = t?.Kind,
                number = t?.Number,
                time = t?.Time,
                how = t?.How,
                original = d.Turn?.Number,
                changed,
                cancelable = _changes.Any(c => c.A == d.Number || c.X == d.Number),
                candidates,
                direct = _direct.TryGetValue(d.Number, out var dt) && !dt.Midway ? dt.Track : (int?)null,
                directTracks = _direct.ContainsKey(d.Number) ? new List<int>() : DirectTracks(d, t),
                // 途中折返し（いまの指定と、駅ごとの候補。◆の窓でその駅の分を出す）
                midway = dt is { Midway: true } ? new { station = dt.Station, track = dt.Track, next = dt.Next } : null,
                midTurns = _direct.ContainsKey(d.Number) ? new List<object>() : MidTurns(d)
            };
        }
    }

    /// <summary>ダイヤに列車を足した・外した（臨時列車の適用・取消）。折返のつなぎを作り直し、計画の線を読み直させる。</summary>
    public void DiaChanged()
    {
        lock (_lock)
        {
            _origPred = _pred = _nextPred = null;
            _byStation = null;
            Version++;
        }
    }

    /// <summary>列車 train の後運用を next に差し替える。うまくいかなければ理由を返す。</summary>
    public string? Set(string train, string next)
    {
        string message;
        lock (_lock)
        {
            var a = rail.DiaOf(train);
            var c = rail.DiaOf(next);
            if (a == null) return $"{train} は時刻表にありません";
            if (c == null) return $"{next} は時刻表にありません";
            if (a.Number == c.Number) return "同じ列車です";
            if (Terminal(a) is not { } term || Origin(c) != term) return $"{c.Number} は {a.Number} の終点から出る列車ではありません";
            if (NextPred().ContainsKey(c.Number)) return $"{c.Number} は途中駅で列番が変わって続く列車なので、後運用にできません";
            var curA = _over.TryGetValue(a.Number, out var oa) ? oa : a.Turn;
            if (curA?.Number == c.Number) return $"{c.Number} はもう {a.Number} の後運用です";

            var xNo = Pred().GetValueOrDefault(c.Number);
            var x = xNo != null ? rail.Dia.GetValueOrDefault(xNo) : null;
            if (_direct.ContainsKey(a.Number)) return $"{a.Number} は直接折返にしています。先に取り消してください";
            if (x != null && _direct.ContainsKey(x.Number)) return $"{x.Number} は直接折返にしています。先に取り消してください";
            var curX = x == null ? null : _over.TryGetValue(x.Number, out var ox) ? ox : x.Turn;
            var newA = x != null ? curX : new DiaTurn("折返", c.Number, c.Stops[0].Dep ?? c.Stops[0].Arr, "発");
            var newX = curA;

            Put(a, newA);
            if (x != null) Put(x, newX);
            _changes.Add(new Change(a.Number, x?.Number, curA, curX, newA, newX));
            Version++;
            message = $"後運用の差し替え: {a.Number} の後運用 {curA?.Number ?? "なし"} → {c.Number}"
                + (x != null ? $"（{x.Number} の後運用は {newX?.Number ?? "なし"}）" : "");
        }

        Changed?.Invoke(message);
        return null;
    }

    /// <summary>列車 train が関わる、いちばん新しい差し替えを取り消す。取り消すものがなければ理由を返す。</summary>
    public string? Cancel(string train) => Cancel(train, out _);

    /// <summary>取り消したのが直接折返なら undone にその指定を返す（呼ぶ側が番線の指定を外す）。</summary>
    public string? Cancel(string train, out DirectTurn? undone)
    {
        undone = null;
        string message;
        lock (_lock)
        {
            var no = RailData.NormalizeTrain(train);
            var i = _changes.FindLastIndex(c => c.A == no || c.X == no);
            if (i < 0) return $"{no} の後運用は差し替えていません";
            var ch = _changes[i];
            _changes.RemoveAt(i);
            if (rail.Dia.TryGetValue(ch.A, out var a)) Put(a, ch.PrevA);
            if (ch.X != null && rail.Dia.TryGetValue(ch.X, out var x)) Put(x, ch.PrevX);
            Version++;
            if (ch.Direct is { Midway: true } mt)
            {
                _direct.Remove(ch.A);
                undone = mt;
                message = $"途中折返しを取消: {ch.A} は {mt.Next} に折り返さず、終点まで走る（打ち切りと {mt.Track}番線の指定を外す）"
                    + (ch.X != null ? $"（{ch.X} の後運用は {ch.PrevX?.Number ?? "なし"} に戻す）" : "");
            }
            else if (ch.Direct is { } dt)
            {
                _direct.Remove(ch.A);
                undone = dt;
                message = $"直接折返を取消: {ch.A} は {dt.Next} に入換で折り返す（{dt.Track}番線の指定を外す）";
            }
            else
                message = $"後運用の差し替えを取消: {ch.A} の後運用を {ch.PrevA?.Number ?? "なし"} に戻す"
                    + (ch.X != null ? $"（{ch.X} は {ch.PrevX?.Number ?? "なし"}）" : "");
        }

        Changed?.Invoke(message);
        return null;
    }

    /// <summary>差し替えをすべて取り消して、時刻表どおりに戻す（ロープレ時刻を設定したとき）。取り消した数を返す。</summary>
    public int Reset()
    {
        lock (_lock)
        {
            var n = _changes.Count;
            if (n == 0 && _over.Count == 0) return 0;
            _changes.Clear();
            _over.Clear();
            _direct.Clear();
            _pred = null;
            Version++;
            return n;
        }
    }

    /// <summary>
    /// 直接折返できる番線: 折返が「入換」で、後運用がこの列車の終点から出るとき、
    /// 終点にこの列車の向きで着けて、後運用の向きで出発できる番線（大道寺の上りの終着なら 3番線）。
    /// </summary>
    private List<int> DirectTracks(DiaTrain d, DiaTurn? t)
    {
        if (t is not { Kind: "折返", How: "入換", Number: { Length: > 0 } n } || rail.DiaOf(n) is not { } nd) return new();
        if (Terminal(d) is not { } term || Origin(nd) != term || !rail.Stations.TryGetValue(term, out var st)) return new();
        if (RouteEngine.TrainInfo(d.Number)?.Dir is not { } dir || RouteEngine.TrainInfo(nd.Number)?.Dir is not { } ndir) return new();
        return st.Routes[dir].Arrive.Keys.Where(k => st.Routes[ndir].Depart.ContainsKey(k)).OrderBy(k => k).ToList();
    }

    /// <summary>
    /// 列車 train を、終点の track 番線で直接折り返させる（折返の仕方を「入換」から「発」に。後運用の列番は変えない）。
    /// うまくいけば指定を返し、うまくいかなければ error に理由を入れる。
    /// </summary>
    public DirectTurn? SetDirect(string train, int track, out string? error) => SetDirect(train, track, null, null, out error);

    /// <param name="delaySec">着く列車の遅れ（秒）。3番線をふさぐ時間の見込みに使う。</param>
    /// <param name="keep">番線をもう指定してある列車（移さない）。引数は列番と駅ID。</param>
    public DirectTurn? SetDirect(string train, int track, double? delaySec, Func<string, string, bool>? keep, out string? error)
    {
        string message;
        DirectTurn result;
        lock (_lock)
        {
            error = null;
            var a = rail.DiaOf(train);
            if (a == null) { error = $"{train} は時刻表にありません"; return null; }
            if (_direct.ContainsKey(a.Number)) { error = $"{a.Number} はもう直接折返にしています"; return null; }
            var cur = _over.TryGetValue(a.Number, out var o) ? o : a.Turn;
            if (cur is not { Kind: "折返", How: "入換", Number: { Length: > 0 } next } || rail.DiaOf(next) is not { } nd)
            { error = $"{a.Number} は入換で折り返す列車ではありません"; return null; }
            if (!DirectTracks(a, cur).Contains(track)) { error = $"{a.Number} は {track}番線で直接折り返せません"; return null; }

            var dep = nd.Stops[0].Dep ?? nd.Stops[0].Arr;
            var newA = new DiaTurn("折返", nd.Number, dep, "発");
            var moved = MovedTrains(a, nd, track, delaySec ?? 0, keep);
            result = new DirectTurn(a.Number, Terminal(a)!, track, nd.Number, moved);
            Put(a, newA);
            _direct[a.Number] = result;
            _changes.Add(new Change(a.Number, null, cur, null, newA, null, result));
            Version++;
            var stName = rail.Stations.TryGetValue(result.Station, out var sd) ? sd.Name : result.Station;
            message = $"直接折返: {a.Number} は {stName} {track}番線に着き、そのまま {nd.Number} で折り返す（入換なし）"
                + (moved.Count > 0 ? "。" + string.Join("・", moved.Select(m => $"{m.Train} は {m.Track}番線へ")) : "");
        }

        Changed?.Invoke(message);
        return result;
    }

    /// <summary>
    /// 直接折返で track 番線をふさぐ間（着く列車の着〔遅れを足す〕から、後運用の発車まで）に、その番線を使うはずだった列車を、
    /// 後運用のもとの番線（入換で入るはずだった番線。大道寺なら 4番線）へ移す。もとの番線はその間あくので、入れ替えになる。
    /// 移す先に、着けない・出発できない、またはほかの列車がいる列車は移さない（司令員が番線を決める）。
    /// </summary>
    private List<(string Train, int Track)> MovedTrains(DiaTrain a, DiaTrain nd, int track, double delaySec, Func<string, string, bool>? keep)
    {
        var result = new List<(string, int)>();
        var sid = Terminal(a)!;
        if (!rail.Stations.TryGetValue(sid, out var st) || nd.Stops[0].Track is not { } orig || orig == track) return result;
        if (Sec(a.Stops[^1].Arr ?? a.Stops[^1].Dep) is not { } arr) return result;
        var from = arr + Math.Max(0, delaySec);
        var to = Math.Max(Sec(nd.Stops[0].Dep ?? nd.Stops[0].Arr) ?? from, from + 120);

        // 駅にいる間（着〜発）の見込み。始発は発車の5分前から、終着は折返の時刻（なければ着いて5分）まで
        (double From, double To)? Span(DiaTrain d, DiaStop s)
        {
            var sArr = Sec(s.Arr);
            var sDep = Sec(s.Dep);
            if (sArr == null && sDep == null) return null;
            if (s == d.Stops[^1]) return (sArr ?? sDep!.Value, Sec(TurnOf(d)?.Time) is { } tt && tt > (sArr ?? 0) ? tt : (sArr ?? sDep!.Value) + 300);
            if (s == d.Stops[0]) return (sDep!.Value - 300, sDep.Value);
            return (sArr ?? sDep!.Value, sDep ?? sArr!.Value);
        }

        bool Overlaps((double From, double To) x, double f, double t) => x.From < t && x.To > f;

        var users = new List<(DiaTrain D, DiaStop S, (double From, double To) Span)>();
        foreach (var d in rail.Dia.Values)
        {
            if (d.Number == a.Number || d.Number == nd.Number) continue;
            foreach (var s in d.Stops)
                if (s.StationId == sid && s.Track is { } && Span(d, s) is { } sp) users.Add((d, s, sp));
        }

        foreach (var (d, s, sp) in users.Where(u => u.S.Track == track && Overlaps(u.Span, from, to)).ToList())
        {
            if (keep?.Invoke(d.Number, sid) == true || RouteEngine.TrainInfo(d.Number)?.Dir is not { } dir) continue;
            var routes = st.Routes[dir];
            var canArrive = s == d.Stops[0] || routes.Arrive.ContainsKey(orig);
            var canDepart = s == d.Stops[^1] || routes.Depart.ContainsKey(orig);
            if (!canArrive || !canDepart) continue;
            if (users.Any(u => u.D != d && u.S.Track == orig && Overlaps(u.Span, sp.From, sp.To))) continue;
            result.Add((d.Number, orig));
        }
        return result;
    }

    /// <summary>
    /// 途中折返し: 列車 d の途中の駅で打ち切って、そこから反対向きに出る列車に折り返す（大道寺方面から来た下りを、新野崎 3・4番線、浜園 2番線、
    /// 藤江・水越・高見沢で上りに）。番線は、d の向きで着けて反対向きに出発できる番線。
    /// 候補は、その駅に止まる反対向きの列車で、d がその駅に着く時刻の15分前から2時間後までに出るもの（早い順に10本）。
    /// </summary>
    private List<object> MidTurns(DiaTrain d)
    {
        var result = new List<object>();
        if (d.Stops.Count < 3 || RouteEngine.TrainInfo(d.Number)?.Dir is not { } dir) return result;
        var back = dir == Dir.Down ? Dir.Up : Dir.Down;
        var by = ByStation();
        for (var i = 1; i < d.Stops.Count - 1; i++)
        {
            var s = d.Stops[i];
            if (s.StationId is not { } sid || MidTracks(sid, dir) is not { Count: > 0 } tracks) continue;
            if ((Sec(s.Arr) ?? Sec(s.Dep)) is not { } at) continue;
            var cands = (by.GetValueOrDefault(sid) ?? new())
                .Where(x => x.D.Number != d.Number && x.Index < x.D.Stops.Count - 1 && x.D.Stops[x.Index].Stop == "停"
                    && x.Dir == back)
                .Select(x => (x.D, dep: x.Dep))
                .Where(x => x.dep is { } t && t >= at - 900 && t <= at + 7200)
                .OrderBy(x => x.dep).Take(10)
                .Select(x => (object)new { train = x.D.Number, type = x.D.Type, time = Hhmm(x.dep!.Value), unyo = Unyo(x.D.Number, 0) })
                .ToList();
            if (cands.Count == 0) continue;
            var name = rail.Stations.TryGetValue(sid, out var st) ? st.Name : s.Name;
            result.Add(new { station = sid, name, tracks, candidates = cands });
        }
        return result;
    }

    private static string Hhmm(double sec) => $"{(int)(sec / 3600) % 24:00}:{(int)(sec % 3600) / 60:00}";

    /// <summary>駅 sid に dir の向きで着けて、反対向きに出発できる番線。</summary>
    private List<int> MidTracks(string sid, Dir dir)
    {
        if (!rail.Stations.TryGetValue(sid, out var st) || !st.RouteControl) return new();
        var back = dir == Dir.Down ? Dir.Up : Dir.Down;
        return st.Routes[dir].Arrive.Keys.Where(k => st.Routes[back].Depart.ContainsKey(k)).OrderBy(k => k).ToList();
    }

    private Dictionary<string, List<(DiaTrain D, int Index, Dir? Dir, double? Dep)>> ByStation()
    {
        if (_byStation != null) return _byStation;
        var m = new Dictionary<string, List<(DiaTrain, int, Dir?, double?)>>();
        foreach (var d in rail.Dia.Values)
        {
            var dir = RouteEngine.TrainInfo(d.Number)?.Dir; // 向きと時刻は、候補を探すたびに読み直さないよう、ここで1回だけ読む
            for (var i = 0; i < d.Stops.Count; i++)
                if (d.Stops[i].StationId is { } sid)
                {
                    if (!m.TryGetValue(sid, out var l)) m[sid] = l = new();
                    l.Add((d, i, dir, Sec(d.Stops[i].Dep) ?? Sec(d.Stops[i].Arr)));
                }
        }
        return _byStation = m;
    }

    /// <summary>
    /// 途中折返し: 列車 train を駅 station で打ち切り、track 番線で next に折り返させる。next のその駅より手前は走らない。
    /// もともと next に折り返すはずだった列車は、train と同じ駅が終点なら train のもとの後運用を受け持ち、違えば後運用なしになる。
    /// 打ち切りと番線の指定は呼ぶ側が集中扱いで入れる。うまくいかなければ error に理由を入れる。
    /// </summary>
    public DirectTurn? SetMid(string train, string next, string station, int track, out string? error)
    {
        string message;
        DirectTurn result;
        lock (_lock)
        {
            error = null;
            var a = rail.DiaOf(train);
            var c = rail.DiaOf(next);
            if (a == null) { error = $"{train} は時刻表にありません"; return null; }
            if (c == null) { error = $"{next} は時刻表にありません"; return null; }
            if (_direct.ContainsKey(a.Number)) { error = $"{a.Number} はもう折返しを指定しています。先に取り消してください"; return null; }
            var ai = a.Stops.FindIndex(s => s.StationId == station);
            if (ai <= 0 || ai >= a.Stops.Count - 1) { error = $"{a.Number} の途中の駅ではありません"; return null; }
            if (RouteEngine.TrainInfo(a.Number)?.Dir is not { } dir) { error = $"{a.Number} の向きが分かりません"; return null; }
            if (!MidTracks(station, dir).Contains(track)) { error = $"{track}番線では折り返せません"; return null; }
            var ci = c.Stops.FindIndex(s => s.StationId == station);
            if (ci < 0 || ci >= c.Stops.Count - 1) { error = $"{c.Number} はこの駅から先へ走る列車ではありません"; return null; }
            if (RouteEngine.TrainInfo(c.Number)?.Dir == dir) { error = $"{c.Number} は同じ向きの列車です"; return null; }
            if (Pred().TryGetValue(c.Number, out var p0) && _direct.ContainsKey(p0)) { error = $"{c.Number} はもう {p0} の折返しにしています"; return null; }

            var curA = _over.TryGetValue(a.Number, out var oa) ? oa : a.Turn;
            var xNo = Pred().GetValueOrDefault(c.Number);
            var x = xNo != null && xNo != a.Number ? rail.Dia.GetValueOrDefault(xNo) : null;
            var curX = x == null ? null : _over.TryGetValue(x.Number, out var ox) ? ox : x.Turn;
            var newA = new DiaTurn("折返", c.Number, c.Stops[ci].Dep ?? c.Stops[ci].Arr, "発");
            var newX = x != null && Terminal(x) == Terminal(a) ? curA : null;
            result = new DirectTurn(a.Number, station, track, c.Number, Array.Empty<(string, int)>(), Midway: true);

            Put(a, newA);
            if (x != null) Put(x, newX);
            _direct[a.Number] = result;
            _changes.Add(new Change(a.Number, x?.Number, curA, curX, newA, newX, result));
            Version++;
            var stName = rail.Stations.TryGetValue(station, out var sd) ? sd.Name : station;
            message = $"途中折返し: {a.Number} は {stName} で打ち切り、{track}番線で {c.Number} に折り返す"
                + (x != null ? $"（{x.Number} の後運用は {newX?.Number ?? "なし"}）" : "");
        }

        Changed?.Invoke(message);
        return result;
    }

    private void Put(DiaTrain d, DiaTurn? t)
    {
        if (t == d.Turn) _over.Remove(d.Number);
        else _over[d.Number] = t;
        _pred = null;
    }
}
