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
///         複線の区間ですれ違ったなら、単線から出てきたばかりの列車は、次に単線に入る駅に書く
///         （路線の端まで続く複線（二木戸から新大路方・大道寺から館浜方）は <see cref="LineEndWindowSeconds"/> 秒以内をまとめて、
///          単線にはさまれた複線は <see cref="CrossWindowSeconds"/> 秒以内）。
///         そうでなく、単線にはさまれた複線の駅（明神川・奥峯口）の手前ですれ違ったなら、その駅に書く
///         （相手の時刻表にその駅がなければ、前後の駅の時刻から割り振った通過時刻で書く）。
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
    public const int CrossWindowSeconds = 160;

    /// <summary>路線の端まで続く複線ですれ違った相手を、単線に入る駅にまとめて書くのは、相手がその駅を出てからこの秒数のうちに着くときだけ。</summary>
    public const int LineEndWindowSeconds = 12 * 60;

    /// <summary>大道寺から館浜方の複線ですれ違った相手を書くのは、相手が大道寺を出てから、この列車が大道寺を出るまでがこの秒数未満のとき（分で数えて10分まで）。</summary>
    public const int DepartWindowSeconds = 11 * 60;

    /// <summary>始発の列車が、発の何秒前から駅にいるとみるか（交換を探すとき）。</summary>
    public const int OriginHoldSeconds = 10 * 60;

    /// <summary>終着の列車が、着いてから何秒の間は駅にいるとみるか（交換の相手として）。</summary>
    public const int TerminalStaySeconds = 10 * 60;

    /// <summary>始発の列車が、折り返す前の列車が着いてからでも、発の何秒前まで駅にいるとみるか（交換の相手を探すとき）。</summary>
    public const int OriginStaySeconds = 12 * 60;

    /// <summary>重ねた列車がこの秒数より離れた時刻にしか通らない駅は、記事が変わらないとみて計算しない（連絡の30分・交換の12分より長く）。</summary>
    private const int TouchSeconds = 60 * 60;

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
        ["大手橋"] = "手", ["大道寺"] = "道", ["新野崎"] = "野", ["大路"] = "路", ["新大路"] = "大", ["新長野公園"] = "公"
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
        private readonly Dictionary<View, Dictionary<int, List<(View U, Pt P)>>> _crossings = new();
        private readonly Dictionary<string, int> _order = new();   // 駅 → 路線の並びの番号
        private readonly HashSet<string> _short = new();          // 単線にはさまれた複線の駅

        // 同じ列車が列番を変えるので時刻表に2本で載っている所: 変わる先の列車の、この番号までの駅は相手にしない
        // （回1052 が名田から 1052 になる。1052 にも上吉沢〜名田が書いてあるが、そこを走るのは回1052）
        private readonly Dictionary<View, int> _hidden = new();

        // 始発の列車 → 折り返す前の列車が、その駅に着いた時刻
        private readonly Dictionary<View, int> _arrived = new();

        // 路線の端まで続く複線ですれ違った列車を、単線に入る駅にまとめて書く駅（二木戸。新大路方の複線）
        private static readonly HashSet<string> LineEndBatch = ["TH50"];

        private readonly Table? _base;                            // 所定のダイヤの表（重ねた表のとき）
        private readonly HashSet<string> _overlay = new();        // 重ねた列車の列番（重ねた表のとき）

        /// <summary>所定のダイヤの表で、決まりで作った記事（列車・駅 → 記事）。所定のダイヤは変わらないので、一度作れば使い回せる。</summary>
        public readonly Dictionary<(View, int), Dictionary<string, string>> Generated = new();

        public bool IsBase => _base == null;

        /// <summary>
        /// 所定のダイヤの表 baseTable に、overlay の列車を重ねた表。重ねない列車は、所定の表の View をそのまま使う
        /// （交換は、所定の表の結果から重ねた列車との組だけを計算し直す）。
        /// </summary>
        public Table(Table baseTable, IEnumerable<DiaTrain> overlay)
        {
            _base = baseTable;
            _order = baseTable._order;
            _short = baseTable._short;
            var views = new List<View>();
            foreach (var t in overlay)
            {
                _overlay.Add(t.Number);
                views.Add(new View(t, RouteEngine.TrainInfo(t.Number)?.Dir, t.Stops.Select(ToPt).ToList()));
            }
            foreach (var (n, v) in baseTable.Trains)
                if (!_overlay.Contains(n)) Trains[n] = v;
            foreach (var (key, list) in baseTable.ByStation)
                ByStation[key] = list.Where(x => !_overlay.Contains(x.V.Number)).ToList();
            foreach (var v in views)
            {
                Trains[v.Number] = v;
                for (var i = 0; i < v.Stops.Count; i++)
                {
                    if (!ByStation.TryGetValue(v.Stops[i].Key, out var l)) ByStation[v.Stops[i].Key] = l = new();
                    l.Add((v, i));
                }
            }
            FindHidden();
        }

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
            // 路線の駅の並び: 駅のいちばん多い列車の時刻表の並び（路線の外の駅も含む）
            var order = trains.MaxBy(d => d.Stops.Count)?.Stops.Select(x => x.StationId ?? x.Name).ToList() ?? new();
            for (var i = 0; i < order.Count; i++) _order.TryAdd(order[i], i);

            // 単線にはさまれた複線の駅: 単線の区間の外にあって、路線の並びで両側に別の単線の区間がある駅
            static bool InGroup(string a) => SingleTrack.Any(g => g.Contains(a));
            for (var i = 0; i < order.Count; i++)
            {
                if (InGroup(order[i])) continue;
                var left = order.Take(i).LastOrDefault(InGroup);
                var right = order.Skip(i + 1).FirstOrDefault(InGroup);
                if (left != null && right != null && !SingleBetween(left, right)) _short.Add(order[i]);
            }
            FindHidden();
        }

        /// <summary>
        /// 列番を変える列車の重なりを探す。変わる先の列車の、変わる駅までは相手にしない（その駅を着くのは前の列番の列車）。
        /// ・時刻表に「次の列番」が書いてある列車（回1052 → 名田から 1052）
        /// ・ある列車の終着の駅で、同じ向きの別の列車が同じ着・発で始まる（703SB が名田で 回703S になる）
        /// </summary>
        private void FindHidden()
        {
            void Hide(View y, int k)
            {
                if (k >= 0 && (!_hidden.TryGetValue(y, out var h) || h < k)) _hidden[y] = k;
            }
            foreach (var x in Trains.Values)
            {
                // 折り返して次の列車になる: 次の列車は、この列車が着いた時から始発の駅にいる
                if (x.Train.Turn is { Kind: "折返", Number: { } tn } && Trains.TryGetValue(tn, out var nt) && nt != x
                    && x.Stops.FindLastIndex(p => p.At != null) is var xl && xl >= 0
                    && nt.Stops.FindIndex(p => p.At != null) is var nf && nf >= 0
                    && x.Stops[xl].Key == nt.Stops[nf].Key && x.Stops[xl].At is { } arr)
                    _arrived[nt] = arr;
                if (x.Train.Next is { } nx && Trains.TryGetValue(nx.Number, out var y) && y != x)
                    Hide(y, y.Stops.FindIndex(p => p.Key == nx.StationId));
                var lx = x.Stops.FindLastIndex(p => p.At != null);
                if (lx < 0) continue;
                var last = x.Stops[lx];
                foreach (var (z, k) in ByStation.GetValueOrDefault(last.Key) ?? [])
                    if (z != x && z.Dir == x.Dir && k == z.Stops.FindIndex(p => p.At != null)
                        && z.Stops[k].Arr == last.Arr && z.Stops[k].Dep == last.Dep)
                        Hide(z, k);
            }
        }

        /// <summary>
        /// この列車が k の駅にいる時間。始発の駅は、折り返す前の列車が着いた時から（わからなければ発の <see cref="OriginHoldSeconds"/> 秒前から）。
        /// </summary>
        private (int From, int To)? TSpan(View t, int k)
        {
            if (t.Stops[k].Arr == null && t.Stops[k].Dep is { } dep && t.PrevTimed(k) == null
                && _arrived.TryGetValue(t, out var from) && Wrap(dep - from) is var w && w >= 0 && w <= 3 * 3600)
                return (dep - Math.Min(w, OriginStaySeconds), dep);
            return t.Span(k, true);
        }

        /// <summary>列車 u が、その駅の前か後で単線を通るか（時刻表にその駅がない列車は、通るとみる）。</summary>
        private bool TouchesSingle(View u, string key)
        {
            if (u.IndexOf(key) is not { } j) return true;
            return (u.PrevTimed(j) is { } p && SingleBetween(u.Stops[p].Key, key))
                   || (u.NextTimed(j) is { } n && SingleBetween(key, u.Stops[n].Key));
        }

        private bool Hidden(View u, int j) => _hidden.TryGetValue(u, out var h) && j <= h;

        /// <summary>相手にする列車 u の、その駅の番号（列番を変える前の列車が走る所は null）。</summary>
        private int? PartnerIndex(View u, string key) => u.IndexOf(key) is { } j && !Hidden(u, j) ? j : null;

        private static Pt ToPt(DiaStop s) =>
            new(s.StationId ?? s.Name, s.Stop != "通" || (s.Arr != null && s.Dep != null), Sec(s.Arr), Sec(s.Dep), s.Track);

        /// <summary>その駅にいる列車（相手にするもの。列番を変える前の列車が走る所は、変わる先の列車を除く）。</summary>
        public IEnumerable<(View V, int I)> At(string key) =>
            (ByStation.GetValueOrDefault(key) ?? []).Where(x => !Hidden(x.V, x.I));

        /// <summary>
        /// 列車 u がその駅にいる時間。時刻表にその駅がなければ（特急の時刻表にない明神川など）、前後の駅の時刻から割り振った通過時刻。
        /// </summary>
        private (Pt P, int From, int To)? SpanAt(View u, string key)
        {
            if (u.IndexOf(key) is { } j)
            {
                if (Hidden(u, j)) return null;
                if (u.Span(j, false) is not { } s) return null;
                // 終着の駅: 折り返して出るまで駅にいる（折り返しの時刻が書いていなければ <see cref="TerminalStaySeconds"/> 秒）
                if (u.Stops[j].Dep == null && u.NextTimed(j) == null)
                    return (u.Stops[j], s.From, Sec(u.Train.Turn?.Time) is { } back && Wrap(back - s.From) is var w && w >= 0 && w <= 3 * 3600
                        ? s.From + Math.Min(w, TerminalStaySeconds) : s.From + TerminalStaySeconds);
                return (u.Stops[j], s.From, s.To);
            }
            if (!_order.TryGetValue(key, out var x)) return null;
            int? prev = null;
            for (var k = 0; k < u.Stops.Count; k++)
            {
                if (u.Stops[k].At == null || Hidden(u, k) || !_order.TryGetValue(u.Stops[k].Key, out var ok)) continue;
                if (prev is { } pk && _order[u.Stops[pk].Key] is var op && Math.Min(op, ok) < x && x < Math.Max(op, ok)
                    && u.Span(pk, false) is { } a && u.Span(k, false) is { } b)
                {
                    var v = a.To + (int)Math.Round((double)Wrap(b.From - a.To) * (x - op) / (ok - op));
                    return (new Pt(key, false, null, v, null), v, v);
                }
                prev = k;
            }
            return null;
        }

        /// <summary>この列車の交換（駅の番号 → 相手と、相手のその駅の時刻）。</summary>
        public Dictionary<int, List<(View U, Pt P)>> Crossings(View t)
        {
            if (_crossings.TryGetValue(t, out var r)) return r;
            r = new();
            _crossings[t] = r;
            if (t.Dir == null) return r;

            var timed = Enumerable.Range(0, t.Stops.Count).Where(k => t.Stops[k].At != null).ToList();

            // 重ねた表で、重ねていない列車: 所定の表の交換から、重ねた列車との組を除き、重ねた列車との組だけ計算し直す
            var incremental = _base != null && !_overlay.Contains(t.Number);
            if (incremental)
                foreach (var (k, list) in _base!.Crossings(t))
                    foreach (var (u, p) in list)
                        if (!_overlay.Contains(u.Number)) Add(r, k, u, p);

            var others = new HashSet<View>();
            foreach (var k in timed)
                foreach (var (u, j) in At(t.Stops[k].Key))
                    if (u != t && u.Dir != null && u.Dir != t.Dir && (!incremental || _overlay.Contains(u.Number))
                        && u.Stops[j].At is { } ua && Math.Abs(Wrap(ua - t.Stops[k].At!.Value)) <= 3 * 3600)
                        others.Add(u);

            foreach (var u in others) Pair(r, t, timed, u);
            return r;
        }

        /// <summary>列車 t と、反対向きの列車 u の交換を探して r に入れる。</summary>
        private void Pair(Dictionary<int, List<(View U, Pt P)>> r, View t, List<int> timed, View u)
        {
            int? prev = null;
            foreach (var k in timed)
            {
                var key = t.Stops[k].Key;
                if (SpanAt(u, key) is not { } us || TSpan(t, k) is not { } ts) continue;
                if (Math.Abs(Wrap(us.From - ts.From)) > 3 * 3600) break;
                if (Wrap(us.From - ts.To) <= 0 && Wrap(us.To - ts.From) >= 0)
                {
                    // 駅に同時にいる: この駅から単線に入り、相手も単線を通る（単線から来たか、単線へ出る）なら、この駅に書く
                    // （大道寺始発で館浜方へ出る列車のように、単線を通らない相手は、遅れても関係しない）。
                    // 単線にはさまれた複線の駅なら、単線から出てきたばかりの相手は次に単線に入る駅に、そうでなければこの駅に書く
                    if (t.EntersSingle(k)) { if (TouchesSingle(u, key)) Add(r, k, u, us.P); }
                    else if (!Entry(r, t, timed, k, u) && _short.Contains(key)) Add(r, k, u, us.P);
                    return;
                }
                if (Wrap(us.To - ts.From) < 0)
                {
                    // 相手はもう出た: 手前の駅からこの駅までの間ですれ違った
                    if (prev is { } pk)
                    {
                        if (SingleBetween(t.Stops[pk].Key, key)) Add(r, k, u, us.P);
                        // 複線ですれ違った: 単線から出てきたばかりの相手は、次に単線に入る駅に書く。
                        // そうでなく、この駅が単線にはさまれた複線の駅なら、この駅に書く
                        else if (!Entry(r, t, timed, k, u) && _short.Contains(key)) Add(r, k, u, us.P);
                    }
                    return;
                }
                prev = k;
            }
        }

        /// <summary>
        /// k から先で最初に単線に入る駅に、相手 u が単線から出てきて間もなく着くなら、そこに書く。書いたら true。
        /// 間もなく: 二木戸から新大路方の複線からなら <see cref="LineEndWindowSeconds"/> 秒以内
        /// （その複線ですれ違った列車をまとめて書く。この列車が走り始めてからすれ違ったものだけ）、
        /// 大道寺から館浜方の複線からなら、相手が大道寺を出てからこの列車が大道寺を出るまで <see cref="DepartWindowSeconds"/> 秒未満
        /// （相手が遅れたら、この列車が単線に入るのを待たされる範囲。大道寺止まりの相手は書かない）、
        /// 単線にはさまれた複線からなら <see cref="CrossWindowSeconds"/> 秒以内。
        /// </summary>
        private bool Entry(Dictionary<int, List<(View, Pt)>> r, View t, List<int> timed, int k, View u)
        {
            foreach (var m in timed.Where(m => m >= k))
            {
                if (!t.EntersSingle(m)) continue;
                var lineEnd = LineEnd(t, m);
                if (lineEnd && !LineEndBatch.Contains(t.Stops[m].Key))
                {
                    // 大道寺から館浜方の複線: この列車が出る時刻から数える
                    if (PartnerIndex(u, t.Stops[m].Key) is { } jd && u.PrevTimed(jd) is { } pd && u.NextTimed(jd) != null
                        && SingleBetween(u.Stops[pd].Key, u.Stops[jd].Key)
                        && TSpan(t, m) is { } td && u.Span(jd, false) is { } ud
                        && Wrap(td.To - ud.To) is var dg && dg >= 0 && dg < DepartWindowSeconds)
                    {
                        Add(r, m, u, u.Stops[jd]);
                        return true;
                    }
                    return false;
                }
                if (lineEnd && SpanAt(u, t.Stops[timed[0]].Key) is { } u0 && TSpan(t, timed[0]) is { } t0 && Wrap(u0.From - t0.From) < 0)
                    return false; // この列車が走り始める前に、もうすれ違っていた
                if (PartnerIndex(u, t.Stops[m].Key) is { } jm && u.PrevTimed(jm) is { } up
                    && SingleBetween(u.Stops[up].Key, u.Stops[jm].Key)
                    && TSpan(t, m) is { } tm && u.Span(jm, false) is { } um
                    && Wrap(tm.From - um.To) is var gap && gap >= 0 && gap <= (lineEnd ? LineEndWindowSeconds : CrossWindowSeconds))
                {
                    Add(r, m, u, u.Stops[jm]);
                    return true;
                }
                return false;
            }
            return false;
        }

        /// <summary>この列車が m の駅で単線に入るとき、後ろ（来た方）が路線の端まで複線か（単線にはさまれた複線でないか）。</summary>
        private bool LineEnd(View t, int m)
        {
            var key = t.Stops[m].Key;
            if (t.NextTimed(m) is not { } n || !_order.TryGetValue(key, out var x) || !_order.TryGetValue(t.Stops[n].Key, out var y)) return false;
            var back = y > x ? -1 : 1;
            foreach (var (st, i) in _order)
                if ((i - x) * back > 0 && SingleTrack.Any(g => g.Contains(st)) && !SingleBetween(st, key)) return false;
            return true;
        }

        private static void Add(Dictionary<int, List<(View, Pt)>> r, int k, View u, Pt p)
        {
            if (!r.TryGetValue(k, out var l)) r[k] = l = new();
            if (!l.Any(x => x.Item1 == u)) l.Add((u, p));
        }
    }

    // 所定のダイヤの表と、連絡を書く駅（所定のダイヤは起動したあと変わらないので、一度だけ作る）
    private static readonly object CacheLock = new();
    private static (IReadOnlyDictionary<string, DiaTrain> Dia, Table Table, HashSet<string> Conn)? _cache;

    /// <summary>
    /// 所定のダイヤ baseDia に overlay（列番 → 時刻を変えた列車・足した臨時列車）を重ねたときの、記事を作り直した列車の表を返す。
    /// overlay の列車（記事を作り直したもの）と、記事が変わった所定の列車が入る。overlay が空なら空。
    /// 所定のダイヤの表と、そこで決まりで作った記事は、一度作れば使い回す（重ねた列車に関わる所だけ計算し直す）。
    /// </summary>
    public static Dictionary<string, DiaTrain> Rewrite(IReadOnlyDictionary<string, DiaTrain> baseDia, IReadOnlyDictionary<string, DiaTrain> overlay)
    {
        var result = new Dictionary<string, DiaTrain>();
        if (overlay.Count == 0) return result;

        lock (CacheLock)
        {
            var c = Cache(baseDia);
            return RewriteCore(baseDia, overlay, c.Table, c.Conn, result);
        }
    }

    /// <summary>
    /// 所定のダイヤの表と、そこで決まりで作る記事を、前もって全部作っておく（起動したあと、裏で1回。
    /// 臨時停車・臨時列車を入れたときに、重ねた列車に関わる所だけ計算すれば済むように）。
    /// </summary>
    public static void Prepare(IReadOnlyDictionary<string, DiaTrain> baseDia)
    {
        lock (CacheLock)
        {
            var c = Cache(baseDia);
            foreach (var v in c.Table.Trains.Values)
                for (var i = 0; i < v.Stops.Count; i++) Generated(c.Table, v, i, c.Conn);
        }
    }

    // CacheLock の中で
    private static (IReadOnlyDictionary<string, DiaTrain> Dia, Table Table, HashSet<string> Conn) Cache(IReadOnlyDictionary<string, DiaTrain> baseDia)
    {
        if (_cache is { } c && ReferenceEquals(c.Dia, baseDia)) return c;
        // 連絡を書く駅: 手書きの連絡がある駅
        var conn = new HashSet<string>();
        foreach (var t in baseDia.Values)
            foreach (var s in t.Stops)
                if (s.Note != null && s.Note.Contains("(連)")) conn.Add(s.StationId ?? s.Name);
        _cache = c = (baseDia, new Table(baseDia.Values), conn);
        return c;
    }

    private static Dictionary<string, DiaTrain> RewriteCore(IReadOnlyDictionary<string, DiaTrain> baseDia, IReadOnlyDictionary<string, DiaTrain> overlay,
        Table before, HashSet<string> connStations, Dictionary<string, DiaTrain> result)
    {
        var merged = new Dictionary<string, DiaTrain>(baseDia);
        foreach (var (n, t) in overlay) merged[n] = t;
        var after = new Table(before, overlay.Values);

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
                              || !times.Any(y => Math.Abs(Wrap(y - x)) <= TouchSeconds))) continue;
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
        var genBefore = bt != null && i < bt.Stops.Count ? Generated(before, bt, i, connStations) : new();
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

    /// <summary>所定のダイヤの表なら、作った記事を覚えておいて使い回す。</summary>
    private static Dictionary<string, string> Generated(Table tb, View t, int i, HashSet<string> connStations)
    {
        if (!tb.IsBase) return Generate(tb, t, i, connStations);
        if (!tb.Generated.TryGetValue((t, i), out var g)) tb.Generated[(t, i)] = g = Generate(tb, t, i, connStations);
        return g;
    }

    /// <summary>決まりで作る記事（相手の列番 → 記事）。</summary>
    private static Dictionary<string, string> Generate(Table tb, View t, int i, HashSet<string> connStations)
    {
        var r = new Dictionary<string, string>();
        var s = t.Stops[i];
        if (t.Dir == null || s.At == null) return r;

        // 交換（通過する駅にも書く）
        if (tb.Crossings(t).TryGetValue(i, out var cs))
            foreach (var (u, up) in cs)
            {
                if (Hidden(t, u)) continue;
                // 止まる列車は着（着の書いていない駅は、発の少し前）。始発の駅なら発
                var origin = u.Stops.FindIndex(x => x.At != null) is var f && f >= 0 && ReferenceEquals(u.Stops[f], up);
                var (sec, how) = !up.Stops ? (up.Pass!.Value, "通") : origin ? (up.Dep ?? up.Arr!.Value, "発") : (ArrOf(up)!.Value, "着");
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
            var tm = how == "着" ? ArrOf(up) : up.Dep ?? (how == "通" ? up.Arr : null);
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
        var sec = how == "着" ? ArrOf(up) : how == "発" ? up.Dep ?? up.Arr : up.Pass;
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

    /// <summary>着いた時刻。着の書いていない駅（発だけの短い停車）は、発の <see cref="EventStops.DwellSeconds"/> 秒前。</summary>
    private static int? ArrOf(Pt p) => p.Arr ?? p.Dep - EventStops.DwellSeconds;

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
