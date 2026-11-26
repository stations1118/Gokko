using System.Text.RegularExpressions;

namespace Gokko.Server.Services;

/// <summary>
/// 時刻表の記事のうち、待避「(待)」・連絡「(連)」・交換「×」を時刻表から作る。
/// ふだんは手書きの記事のまま。臨時停車で時刻を変えた列車や、臨時列車（特発臨・催物用）があるときだけ、
/// その列車と、相手になる列車の記事を作り直す（<see cref="RailData"/> が表を作り直すとき）。
///
/// 記事の形: 「(待)特手②05通」＝ その駅に止まっている間に、②番線を大手橋行きの特急が05分に通過する。
///           「(連)普野①08発」＝ その駅で、①番線から新野崎行きの普通が08分に出る。
///           「×普東②54着」  ＝ その駅で、②番線に東井行きの普通が54分に着く（反対向きの列車と行き違う）。
/// ・待避: 同じ向きで、その駅の着から発までの間に、その駅を通過する列車（着と発のある途中駅だけ。始発駅の待避は作らない）
/// ・連絡: 手書きの連絡がある駅で、着いてから <see cref="ConnectWindowSeconds"/> 秒以内に出る同じ向きの列車のうち、最初に
///         「この列車が止まらない駅へ行く」か「先の駅にこの列車より早く着く」もの。回送・試運転は連絡を作らない・相手にしない
/// ・交換: 単線の区間（考察による。<see cref="SingleTrack"/>）で行き違う反対向きの列車。
///         駅に同時にいるなら、その駅から単線に入るときだけその駅に書く。
///         複線の区間ですれ違ったなら、単線から出てきた列車（<see cref="CrossWindowSeconds"/> 秒以内）を、次に単線に入る駅に書く。
///         始発の列車は、発の <see cref="OriginHoldSeconds"/> 秒前から駅にいるとみる
/// ・試運転（走るかどうかわからない）は、ほかの列車の記事には書かない。試運転の列車の記事にだけ、相手の列車を書く
/// ・接続「(接)」（待つもの。ConnectionTable が使う）・灰色などの記事は変えない
/// ・作り直すときは、手書きの記事のうち、時刻を変えた列車に関わる (待)(連)× を読み直す（相手の時刻が変われば分を書き直し、
///   待避・連絡・交換でなくなれば消す）。決まりで選ぶ相手が変わったら、前の相手を消して新しい相手を書き足す
/// </summary>
public static class DiaNotes
{
    /// <summary>連絡を探す時間（着いてから、この秒数のうちに出る列車）。</summary>
    public const int ConnectWindowSeconds = 30 * 60;

    /// <summary>複線の区間ですれ違った相手を、単線に入る駅に書くのは、相手がその駅を出てからこの秒数のうちに着くときだけ。</summary>
    public const int CrossWindowSeconds = 3 * 60;

    /// <summary>始発の列車が、発の何秒前から駅にいるとみるか（交換を探すとき）。</summary>
    public const int OriginHoldSeconds = 10 * 60;

    /// <summary>
    /// 単線の区間（考察による）。並びの中のどの2駅の間も単線。並びの外へ出る所からは複線。
    /// 二木戸〜珠川信号場（三石・名田・下吉沢・上吉沢で行き違い）、三郷〜西赤山（赤山町で行き違い）、日野森〜大道寺（高見沢・水越・藤江で行き違い）。
    /// </summary>
    private static readonly string[][] SingleTrack =
    [
        ["TH50", "TH51", "TH52", "TH53", "TH54", "TH55", "TH55S"],
        ["TH57", "TH58", "TH59"],
        ["TH61", "TH62", "TH63", "TH64", "TH65"]
    ];

    private static readonly Regex Item = new(@"^(?:\((待|連)\)|(×))(.)(.)([①-⑳]?)(\d\d)(着|発|通)$");
    private const string Circled = "①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳";

    // 行き先 → 記事の1字（書いていない行き先は、行き先の最初の字）
    private static readonly Dictionary<string, string> DestShort = new()
    {
        ["大手橋"] = "手", ["大道寺"] = "道", ["新野崎"] = "野", ["大路"] = "路", ["新長野公園"] = "公"
    };

    /// <summary>行き先の1字（大手橋 → 手、館浜 → 館）。</summary>
    public static string DestChar(string dest) =>
        DestShort.TryGetValue(dest, out var c) ? c : dest.Length > 0 ? dest[..1] : "";

    private static bool SingleBetween(string a, string b) => SingleTrack.Any(g => g.Contains(a) && g.Contains(b));

    // 単線の区間の中の駅（両側とも単線）
    private static bool Inner(string a) => SingleTrack.Any(g => Array.IndexOf(g, a) is var i && i > 0 && i < g.Length - 1);

    private sealed class View(DiaTrain train, Dir? dir, List<Pt> stops)
    {
        public DiaTrain Train { get; } = train;
        public Dir? Dir { get; } = dir;
        public List<Pt> Stops { get; } = stops;
        public string Number => Train.Number;

        private Dictionary<string, int>? _index;

        /// <summary>駅 → 時刻のある最初の駅の番号。</summary>
        public int? IndexOf(string key)
        {
            if (_index == null)
            {
                _index = new();
                for (var i = 0; i < Stops.Count; i++)
                    if (Stops[i].At != null) _index.TryAdd(Stops[i].Key, i);
            }
            return _index.TryGetValue(key, out var k) ? k : null;
        }

        /// <summary>その駅にいる時間（着〜発。通過は通過時刻だけ）。hold なら、始発の駅は発の少し前からいるとみる。</summary>
        public (int From, int To)? Span(int i, bool hold)
        {
            var p = Stops[i];
            if (!p.Stops) return p.Pass is { } x ? (x, x) : null;
            var from = p.Arr ?? (hold && i == FirstTimed ? p.Dep - OriginHoldSeconds : p.Dep);
            var to = p.Dep ?? p.Arr;
            return from is { } f && to is { } t ? (f, t) : null;
        }

        private int FirstTimed => Stops.FindIndex(p => p.At != null);

        /// <summary>i の次の、時刻のある駅。なければ null。</summary>
        public int? NextTimed(int i)
        {
            for (var k = i + 1; k < Stops.Count; k++)
                if (Stops[k].At != null) return k;
            return null;
        }

        /// <summary>i の前の、時刻のある駅。なければ null。</summary>
        public int? PrevTimed(int i)
        {
            for (var k = i - 1; k >= 0; k--)
                if (Stops[k].At != null) return k;
            return null;
        }

        /// <summary>この駅から先が単線か（終着なら、その駅が単線の区間の中か）。</summary>
        public bool EntersSingle(int i) =>
            NextTimed(i) is { } n ? SingleBetween(Stops[i].Key, Stops[n].Key) : Inner(Stops[i].Key);
    }

    // 1駅。Key は駅ID（なければ駅名）。Stops は止まるか（時刻表で通過でも、臨時停車用に着と発を書いた駅は止まる）
    private sealed record Pt(string Key, bool Stops, int? Arr, int? Dep, int? Track)
    {
        public int? At => Arr ?? Dep;
        public int? Pass => Dep ?? Arr;
    }

    private sealed class Table
    {
        public readonly Dictionary<string, View> Trains = new();
        public readonly Dictionary<string, List<(View V, int I)>> ByStation = new();
        private readonly Dictionary<View, Dictionary<int, List<(View U, int J)>>> _crossings = new();

        public Table(IEnumerable<DiaTrain> trains)
        {
            foreach (var t in trains)
            {
                var v = new View(t, RouteEngine.TrainInfo(t.Number)?.Dir, t.Stops.Select(ToPt).ToList());
                Trains[t.Number] = v;
                for (var i = 0; i < v.Stops.Count; i++)
                {
                    if (!ByStation.TryGetValue(v.Stops[i].Key, out var l)) ByStation[v.Stops[i].Key] = l = new();
                    l.Add((v, i));
                }
            }
        }

        private static Pt ToPt(DiaStop s) =>
            new(s.StationId ?? s.Name, s.Stop != "通" || (s.Arr != null && s.Dep != null), Sec(s.Arr), Sec(s.Dep), s.Track);

        public IEnumerable<(View V, int I)> At(string key) => ByStation.GetValueOrDefault(key) ?? [];

        /// <summary>この列車の交換（駅の番号 → 相手と、相手のその駅の番号）。</summary>
        public Dictionary<int, List<(View U, int J)>> Crossings(View t)
        {
            if (_crossings.TryGetValue(t, out var r)) return r;
            r = new();
            _crossings[t] = r;
            if (t.Dir == null) return r;

            var timed = Enumerable.Range(0, t.Stops.Count).Where(k => t.Stops[k].At != null).ToList();
            var others = new HashSet<View>();
            foreach (var k in timed)
                foreach (var (u, j) in At(t.Stops[k].Key))
                    if (u != t && u.Dir != null && u.Dir != t.Dir && u.Stops[j].At is { } ua
                        && Math.Abs(Wrap(ua - t.Stops[k].At!.Value)) <= 3 * 3600)
                        others.Add(u);

            foreach (var u in others)
            {
                int? prev = null;
                foreach (var k in timed)
                {
                    if (u.IndexOf(t.Stops[k].Key) is not { } j || t.Span(k, true) is not { } ts || u.Span(j, false) is not { } us) continue;
                    if (Math.Abs(Wrap(us.From - ts.From)) > 3 * 3600) break;
                    if (Wrap(us.From - ts.To) <= 0 && Wrap(us.To - ts.From) >= 0)
                    {
                        // 駅に同時にいる: この駅から単線に入るときだけ
                        if (t.EntersSingle(k)) Add(r, k, u, j);
                        break;
                    }
                    if (Wrap(us.To - ts.From) < 0)
                    {
                        // 相手はもう出た: 手前の駅からこの駅までの間ですれ違った
                        if (prev is { } pk)
                        {
                            if (SingleBetween(t.Stops[pk].Key, t.Stops[k].Key)) Add(r, k, u, j);
                            else
                                // 複線ですれ違った: 次に単線に入る駅に、単線から出てきた相手を書く
                                foreach (var m in timed.Where(m => m >= k))
                                {
                                    if (!t.EntersSingle(m)) continue;
                                    if (u.IndexOf(t.Stops[m].Key) is { } jm && u.PrevTimed(jm) is { } up
                                        && SingleBetween(u.Stops[up].Key, u.Stops[jm].Key)
                                        && t.Span(m, true) is { } tm && u.Span(jm, false) is { } um
                                        && Wrap(tm.From - um.To) is var gap && gap >= 0 && gap <= CrossWindowSeconds)
                                        Add(r, m, u, jm);
                                    break;
                                }
                        }
                        break;
                    }
                    prev = k;
                }
            }
            return r;
        }

        private static void Add(Dictionary<int, List<(View, int)>> r, int k, View u, int j)
        {
            if (!r.TryGetValue(k, out var l)) r[k] = l = new();
            l.Add((u, j));
        }
    }

    /// <summary>
    /// 所定のダイヤ baseDia に overlay（列番 → 時刻を変えた列車・足した臨時列車）を重ねたときの、記事を作り直した列車の表を返す。
    /// overlay の列車（記事を作り直したもの）と、記事が変わった所定の列車が入る。overlay が空なら空。
    /// </summary>
    public static Dictionary<string, DiaTrain> Rewrite(IReadOnlyDictionary<string, DiaTrain> baseDia, IReadOnlyDictionary<string, DiaTrain> overlay)
    {
        var result = new Dictionary<string, DiaTrain>();
        if (overlay.Count == 0) return result;

        var before = new Table(baseDia.Values);
        var merged = new Dictionary<string, DiaTrain>(baseDia);
        foreach (var (n, t) in overlay) merged[n] = t;
        var after = new Table(merged.Values);

        // 連絡を書く駅: 手書きの連絡がある駅
        var connStations = new HashSet<string>();
        foreach (var t in baseDia.Values)
            foreach (var s in t.Stops)
                if (s.Note != null && s.Note.Contains("(連)")) connStations.Add(s.StationId ?? s.Name);

        // 作り直す駅と時刻: 重ねた列車（と、その所定の列車）が通る駅
        var touched = new Dictionary<string, List<int>>();
        void Touch(View v)
        {
            foreach (var p in v.Stops)
                if (p.At is { } x)
                {
                    if (!touched.TryGetValue(p.Key, out var l)) touched[p.Key] = l = new();
                    l.Add(x);
                }
        }
        foreach (var n in overlay.Keys)
        {
            Touch(after.Trains[n]);
            if (before.Trains.TryGetValue(n, out var bv)) Touch(bv);
        }

        foreach (var (number, train) in merged)
        {
            var bt = before.Trains.GetValueOrDefault(number);
            var at = after.Trains[number];
            var mine = overlay.ContainsKey(number);
            var stops = new List<DiaStop>(train.Stops);
            var dirty = false;
            for (var i = 0; i < at.Stops.Count; i++)
            {
                var p = at.Stops[i];
                // 重ねた列車が近い時刻に通らない駅は、記事が変わらない
                if (!mine && (p.At is not { } x || !touched.TryGetValue(p.Key, out var times)
                              || !times.Any(y => Math.Abs(Wrap(y - x)) <= 3 * 3600))) continue;
                var note = RewriteStop(before, after, bt, at, i, overlay, connStations, stops[i].Note);
                if (note == stops[i].Note) continue;
                stops[i] = stops[i] with { Note = note };
                dirty = true;
            }
            if (!dirty && !mine) continue;
            result[number] = dirty ? Copy(train, stops) : train;
        }
        return result;
    }

    /// <summary>1駅の記事を作り直す。変わらなければ note をそのまま返す。bt は所定のダイヤの同じ列車（臨時列車なら null）。</summary>
    private static string? RewriteStop(Table before, Table after, View? bt, View at, int i, IReadOnlyDictionary<string, DiaTrain> overlay,
        HashSet<string> connStations, string? note)
    {
        var genBefore = bt != null && i < bt.Stops.Count ? Generate(before, bt, i, connStations) : new();
        var genAfter = Generate(after, at, i, connStations);
        var selfChanged = overlay.ContainsKey(at.Number);

        var items = (note ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        var outItems = new List<string>();
        var present = new HashSet<string>();
        foreach (var item in items)
        {
            var m = Item.Match(item);
            if (!m.Success || bt == null || i >= bt.Stops.Count || Resolve(before, bt, i, m) is not { } u)
            {
                outItems.Add(item);
                continue;
            }
            var kind = m.Groups[1].Success ? m.Groups[1].Value : "×";
            var how = m.Groups[7].Value;
            // 決まりで選ぶ連絡・交換の相手が変わった: 前の相手は消す
            if (kind != "待" && genBefore.ContainsKey(u) && !genAfter.ContainsKey(u)) continue;
            var uv = after.Trains.GetValueOrDefault(u);
            // この駅で、どちらの時刻も変わっていなければ、手書きのまま
            if ((!selfChanged && !overlay.ContainsKey(u)) || (uv != null && SameHere(bt, at, i, before.Trains.GetValueOrDefault(u), uv)))
            {
                outItems.Add(item);
                present.Add(u);
                continue;
            }
            // どちらかの時刻が変わった: 読み直す（待避・連絡でなくなれば消す）。
            // 交換は、決まりで作れればその記事、作れなければ（手書きだけの交換）相手の時刻で分を書き直す
            var re = uv == null ? null
                : kind != "×" ? Recheck(at, i, uv, kind, how)
                : genAfter.GetValueOrDefault(u) ?? Rewrite(uv, at.Stops[i].Key, how);
            if (re != null)
            {
                outItems.Add(re);
                present.Add(u);
            }
        }
        foreach (var (u, text) in genAfter)
            if (!present.Contains(u) && !genBefore.ContainsKey(u))
            {
                outItems.Add(text);
                present.Add(u);
            }

        var joined = string.Join(";", outItems);
        if (joined == string.Join(";", items)) return note;
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>決まりで作る記事（相手の列番 → 記事）。</summary>
    private static Dictionary<string, string> Generate(Table tb, View t, int i, HashSet<string> connStations)
    {
        var r = new Dictionary<string, string>();
        var s = t.Stops[i];
        if (t.Dir == null || s.At == null) return r;

        // 交換（通過する駅にも書く）
        if (tb.Crossings(t).TryGetValue(i, out var cs))
            foreach (var (u, j) in cs)
            {
                if (Hidden(t, u)) continue;
                var up = u.Stops[j];
                var (sec, how) = !up.Stops ? (up.Pass!.Value, "通") : up.Arr is { } ua ? (ua, "着") : (up.Dep!.Value, "発");
                r.TryAdd(u.Number, Text("×", u, up, sec, how));
            }

        if (!s.Stops) return r;

        // 待避: 着から発までの間に通過する、同じ向きの列車
        if (s.Arr is { } a && s.Dep is { } d)
            foreach (var (u, j) in tb.At(s.Key))
            {
                if (u == t || u.Dir != t.Dir || Hidden(t, u) || u.Stops[j].Stops || u.Stops[j].Pass is not { } p) continue;
                if (Wrap(p - a) >= 0 && Wrap(d - p) >= 0) r.TryAdd(u.Number, Text("待", u, u.Stops[j], p, "通"));
            }

        // 連絡
        if (i > 0 && connStations.Contains(s.Key) && !NoConnect(t.Train) && s.At is { } ta
            && FirstConnection(tb, t, i, ta) is { } c)
            r.TryAdd(c.V.Number, Text("連", c.V, c.V.Stops[c.J], c.V.Stops[c.J].Dep!.Value, "発"));
        return r;
    }

    private static bool NoConnect(DiaTrain d) => d.Type is "回送" or "試運転" || IsTest(d);

    /// <summary>試運転は、試運転でない列車の記事には書かない（走るかどうかわからない）。</summary>
    private static bool Hidden(View t, View u) => IsTest(u.Train) && !IsTest(t.Train);

    /// <summary>試運転か（ダイヤでは種別を回送として読むので、列番の「試」で見る）。</summary>
    private static bool IsTest(DiaTrain d) => d.Number.StartsWith("試") || d.TypeLabel == "試運転";

    /// <summary>記事に書く種別の1字（試運転は「試」）。</summary>
    private static string TypeChar(DiaTrain d) => IsTest(d) ? "試" : d.Type.Length > 0 ? d.Type[..1] : "";

    /// <summary>着いてから時間内に出る同じ向きの列車のうち、最初に、止まらない駅へ行くか、先の駅に早く着くもの。</summary>
    private static (View V, int J)? FirstConnection(Table tb, View t, int i, int ta)
    {
        var mine = new Dictionary<string, int>();
        for (var k = i + 1; k < t.Stops.Count; k++)
            if (t.Stops[k].Stops) mine.TryAdd(t.Stops[k].Key, k);

        var cands = new List<(int Wait, View V, int J)>();
        foreach (var (u, j) in tb.At(t.Stops[i].Key))
        {
            if (u == t || u.Dir != t.Dir || NoConnect(u.Train) || !u.Stops[j].Stops || u.Stops[j].Dep is not { } ud) continue;
            var w = Wrap(ud - ta);
            if (w > 0 && w <= ConnectWindowSeconds) cands.Add((w, u, j));
        }
        foreach (var (_, u, j) in cands.OrderBy(c => c.Wait))
            for (var k = j + 1; k < u.Stops.Count; k++)
            {
                var up = u.Stops[k];
                if (!up.Stops) continue;
                if (!mine.TryGetValue(up.Key, out var tk)) return (u, j); // この列車が止まらない駅へ行く
                if (up.At is { } ua && t.Stops[tk].At is { } tka && Wrap(tka - ua) > 0) return (u, j); // 先の駅に早く着く
            }
        return null;
    }

    /// <summary>手書きの (待)(連)× の相手（所定のダイヤで、同じ駅にいる列車）を探す。見つからなければ null。</summary>
    private static string? Resolve(Table tb, View t, int i, Match m)
    {
        var s = t.Stops[i];
        if (s.At is not { } baseSec) return null;
        var (ty, de, tr, mi, how) = (m.Groups[3].Value, m.Groups[4].Value, m.Groups[5].Value, int.Parse(m.Groups[6].Value), m.Groups[7].Value);
        string? best = null;
        var bestDiff = int.MaxValue;
        foreach (var (u, j) in tb.At(s.Key))
        {
            if (u == t || TypeChar(u.Train) != ty || DestChar(u.Train.Dest) != de) continue;
            var up = u.Stops[j];
            if (tr.Length > 0 && (up.Track is not { } k || k < 1 || k > Circled.Length || Circled[k - 1].ToString() != tr)) continue;
            var tm = how == "着" ? up.Arr : up.Dep ?? (how == "通" ? up.Arr : null);
            if (tm is not { } x || x / 60 % 60 != mi) continue;
            var diff = Math.Abs(Wrap(x - baseSec));
            if (diff < bestDiff && diff <= 3600)
            {
                best = u.Number;
                bestDiff = diff;
            }
        }
        return best;
    }

    /// <summary>この駅で、この列車と相手 u の時刻がどちらも変わっていないか。</summary>
    private static bool SameHere(View? bt, View at, int i, View? bu, View au)
    {
        if (bt == null || bu == null || i >= bt.Stops.Count || bt.Stops[i] != at.Stops[i]) return false;
        var key = at.Stops[i].Key;
        var j = bu.Stops.FindIndex(p => p.Key == key);
        var k = au.Stops.FindIndex(p => p.Key == key);
        return j >= 0 && k >= 0 && bu.Stops[j] == au.Stops[k];
    }

    /// <summary>手書きだけの交換を、相手の新しい時刻で書き直す。相手がその駅を通らなくなれば null。</summary>
    private static string? Rewrite(View u, string key, string how)
    {
        var j = u.Stops.FindIndex(p => p.Key == key);
        if (j < 0) return null;
        var up = u.Stops[j];
        var sec = how == "着" ? up.Arr ?? up.Dep : how == "発" ? up.Dep ?? up.Arr : up.Pass;
        return sec is { } x ? Text("×", u, up, x, how) : null;
    }

    /// <summary>時刻を変えたあとも待避・連絡になるなら、書き直した記事。ならなければ null。</summary>
    private static string? Recheck(View t, int i, View u, string kind, string how)
    {
        var s = t.Stops[i];
        var j = u.Stops.FindIndex(p => p.Key == s.Key);
        if (j < 0) return null;
        var up = u.Stops[j];
        if (kind == "待")
        {
            if (how == "通")
            {
                if (up.Stops || up.Pass is not { } p || s.Dep is not { } d) return null;
                // 始発駅（着がない）は、発までの <see cref="ConnectWindowSeconds"/> 秒の間に通過すれば待避のまま
                var ok = s.Arr is { } a ? Wrap(p - a) >= 0 && Wrap(d - p) >= 0 : Wrap(d - p) >= 0 && Wrap(d - p) <= ConnectWindowSeconds;
                return ok ? Text("待", u, up, p, "通") : null;
            }
            // (待)…発: 先に出る列車を待つ
            if (!up.Stops || up.Dep is not { } ud || s.Dep is not { } sd) return null;
            return Wrap(sd - ud) >= 0 ? Text("待", u, up, ud, "発") : null;
        }
        if (s.At is not { } ta) return null;
        if (how == "着")
            return up.Arr is { } ua && Math.Abs(Wrap(ua - ta)) <= ConnectWindowSeconds ? Text("連", u, up, ua, "着") : null;
        if (!up.Stops || up.Dep is not { } dep) return null;
        var w = Wrap(dep - ta);
        return w > 0 && w <= ConnectWindowSeconds ? Text("連", u, up, dep, "発") : null;
    }

    private static string Text(string kind, View u, Pt p, int sec, string how)
    {
        var track = p.Track is { } k && k >= 1 && k <= Circled.Length ? Circled[k - 1].ToString() : "";
        var head = kind == "×" ? "×" : $"({kind})";
        return $"{head}{TypeChar(u.Train)}{DestChar(u.Train.Dest)}{track}{sec / 60 % 60:00}{how}";
    }

    private static DiaTrain Copy(DiaTrain d, List<DiaStop> stops)
    {
        var n = new DiaTrain
        {
            Number = d.Number, Type = d.Type, TypeLabel = d.TypeLabel, Dest = d.Dest, TerminalId = d.TerminalId, Next = d.Next,
            Unyo = d.Unyo, Cars = d.Cars, Turn = d.Turn
        };
        n.Stops.AddRange(stops);
        foreach (var (k, v) in d.Stations) n.Stations[k] = v;
        return n;
    }

    private static int? Sec(string? hms)
    {
        if (string.IsNullOrEmpty(hms)) return null;
        var p = hms.Split(':');
        if (p.Length < 2 || !int.TryParse(p[0], out var h) || !int.TryParse(p[1], out var m)) return null;
        var s = p.Length > 2 && int.TryParse(p[2], out var x) ? x : 0;
        return h * 3600 + m * 60 + s;
    }

    /// <summary>差を -12時間〜12時間にする（日をまたぐ所）。</summary>
    private static int Wrap(int d) => (d % 86400 + 86400 + 43200) % 86400 - 43200;
}
