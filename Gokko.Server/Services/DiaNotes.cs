using System.Text.RegularExpressions;

namespace Gokko.Server.Services;

/// <summary>
/// 時刻表の記事のうち、待避「(待)」と接続「(連)」を時刻表から作り直す（臨時停車で時刻を変えたとき。<see cref="TempStopRetime"/>）。
/// ふだんは手書きの記事のまま。時刻を変えた列車があるときだけ、その列車と、相手になる列車の記事を作り直す。
///
/// 記事の形: 「(待)特手②05通」＝ その駅に止まっている間に、②番線を大手橋行きの特急が05分に通過する。
///           「(連)普野①08発」＝ その駅で、①番線から新野崎行きの普通が08分に出る。
/// ・待避: 同じ向きで、その駅の着から発までの間に、その駅を通過する列車（着と発のある途中駅だけ。始発駅の待避は手書きのまま）
/// ・接続: 手書きの接続がある駅で、着いてから <see cref="ConnectWindowSeconds"/> 秒以内に出る同じ向きの列車のうち、最初に
///         「この列車が止まらない駅へ行く」か「先の駅にこの列車より早く着く」もの。回送・試運転は接続を作らない・相手にしない
/// ・すれ違い「×」・接続待ち「(接)」・灰色などの記事は変えない
/// ・作り直すときは、手書きの記事のうち、時刻を変えた列車に関わる (待)(連) を読み直す（相手の時刻が変われば分を書き直し、
///   待避・接続でなくなれば消す）。決まりで選ぶ相手が変わったら、前の相手を消して新しい相手を書き足す
/// </summary>
public static class DiaNotes
{
    /// <summary>接続を探す時間（着いてから、この秒数のうちに出る列車）。</summary>
    public const int ConnectWindowSeconds = 30 * 60;

    private static readonly Regex Item = new(@"^\((待|連)\)(.)(.)([①-⑳]?)(\d\d)(着|発|通)$");
    private const string Circled = "①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳";

    // 行き先 → 記事の1字（書いていない行き先は、行き先の最初の字）
    private static readonly Dictionary<string, string> DestShort = new()
    {
        ["大手橋"] = "手", ["大道寺"] = "道", ["新野崎"] = "野", ["大路"] = "路", ["新長野公園"] = "公"
    };

    /// <summary>行き先の1字（大手橋 → 手、館浜 → 館）。</summary>
    public static string DestChar(string dest) =>
        DestShort.TryGetValue(dest, out var c) ? c : dest.Length > 0 ? dest[..1] : "";

    private sealed record View(DiaTrain Train, Dir? Dir, List<Pt> Stops);

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
    }

    /// <summary>
    /// 所定のダイヤ baseDia の一部を changed（列番 → 時刻を変えた列車）に差し替えたときの、記事を作り直した列車の表を返す。
    /// changed の列車（記事を作り直したもの）と、記事が変わった相手の列車が入る。changed が空なら空。
    /// </summary>
    public static Dictionary<string, DiaTrain> Rewrite(IReadOnlyDictionary<string, DiaTrain> baseDia, IReadOnlyDictionary<string, DiaTrain> changed)
    {
        var result = new Dictionary<string, DiaTrain>();
        if (changed.Count == 0) return result;

        var before = new Table(baseDia.Values);
        var merged = baseDia.ToDictionary(kv => kv.Key, kv => changed.GetValueOrDefault(kv.Key) ?? kv.Value);
        var after = new Table(merged.Values);

        // 接続を書く駅: 手書きの接続がある駅
        var connStations = new HashSet<string>();
        foreach (var t in baseDia.Values)
            foreach (var s in t.Stops)
                if (s.Note != null && s.Note.Contains("(連)")) connStations.Add(s.StationId ?? s.Name);

        // 作り直す駅: 時刻を変えた列車が通る駅
        var touched = new HashSet<string>();
        foreach (var n in changed.Keys)
            if (after.Trains.TryGetValue(n, out var cv))
                foreach (var p in cv.Stops) touched.Add(p.Key);

        foreach (var (number, train) in merged)
        {
            var bv = before.Trains[number];
            var av = after.Trains[number];
            var stops = new List<DiaStop>(train.Stops);
            var dirty = false;
            for (var i = 0; i < av.Stops.Count; i++)
            {
                if (!touched.Contains(av.Stops[i].Key)) continue;
                var note = RewriteStop(before, after, bv, av, i, changed, connStations, stops[i].Note);
                if (note == stops[i].Note) continue;
                stops[i] = stops[i] with { Note = note };
                dirty = true;
            }
            if (!dirty && !changed.ContainsKey(number)) continue;
            result[number] = dirty ? Copy(train, stops) : train;
        }
        return result;
    }

    /// <summary>1駅の記事を作り直す。変わらなければ note をそのまま返す。</summary>
    private static string? RewriteStop(Table before, Table after, View bt, View at, int i, IReadOnlyDictionary<string, DiaTrain> changed,
        HashSet<string> connStations, string? note)
    {
        var genBefore = Generate(before, bt, i, connStations);
        var genAfter = Generate(after, at, i, connStations);
        var selfChanged = changed.ContainsKey(at.Train.Number);

        var items = (note ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        var outItems = new List<string>();
        var present = new HashSet<string>();
        foreach (var item in items)
        {
            var m = Item.Match(item);
            if (!m.Success || Resolve(before, bt, i, m) is not { } u)
            {
                outItems.Add(item);
                continue;
            }
            var kind = m.Groups[1].Value;
            var how = m.Groups[6].Value;
            // 決まりで選ぶ接続の相手が変わった: 前の相手は消す
            if (kind == "連" && genBefore.ContainsKey(u) && !genAfter.ContainsKey(u)) continue;
            if (!selfChanged && !changed.ContainsKey(u))
            {
                outItems.Add(item);
                present.Add(u);
                continue;
            }
            // どちらかの時刻が変わった: 読み直す（待避・接続でなくなれば消す）
            if (after.Trains.TryGetValue(u, out var uv) && Recheck(at, i, uv, kind, how) is { } re)
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
        if (!s.Stops || t.Dir == null) return r;

        // 待避: 着から発までの間に通過する、同じ向きの列車
        if (s.Arr is { } a && s.Dep is { } d)
            foreach (var (u, j) in tb.At(s.Key))
            {
                if (u == t || u.Dir != t.Dir || u.Stops[j].Stops || u.Stops[j].Pass is not { } p) continue;
                if (Wrap(p - a) >= 0 && Wrap(d - p) >= 0) r[u.Train.Number] = Text("待", u, u.Stops[j], p, "通");
            }

        // 接続
        if (i > 0 && connStations.Contains(s.Key) && !NoConnect(t.Train.Type) && s.At is { } ta
            && FirstConnection(tb, t, i, ta) is { } c)
            r[c.V.Train.Number] = Text("連", c.V, c.V.Stops[c.J], c.V.Stops[c.J].Dep!.Value, "発");
        return r;
    }

    private static bool NoConnect(string type) => type is "回送" or "試運転";

    /// <summary>着いてから時間内に出る同じ向きの列車のうち、最初に、止まらない駅へ行くか、先の駅に早く着くもの。</summary>
    private static (View V, int J)? FirstConnection(Table tb, View t, int i, int ta)
    {
        var mine = new Dictionary<string, int>();
        for (var k = i + 1; k < t.Stops.Count; k++)
            if (t.Stops[k].Stops) mine.TryAdd(t.Stops[k].Key, k);

        var cands = new List<(int Wait, View V, int J)>();
        foreach (var (u, j) in tb.At(t.Stops[i].Key))
        {
            if (u == t || u.Dir != t.Dir || NoConnect(u.Train.Type) || !u.Stops[j].Stops || u.Stops[j].Dep is not { } ud) continue;
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

    /// <summary>手書きの (待)(連) の相手（所定のダイヤで、同じ駅にいる列車）を探す。見つからなければ null。</summary>
    private static string? Resolve(Table tb, View t, int i, Match m)
    {
        var s = t.Stops[i];
        if (s.At is not { } baseSec) return null;
        var (ty, de, tr, mi, how) = (m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, int.Parse(m.Groups[5].Value), m.Groups[6].Value);
        string? best = null;
        var bestDiff = int.MaxValue;
        foreach (var (u, j) in tb.At(s.Key))
        {
            if (u == t || !u.Train.Type.StartsWith(ty) || DestChar(u.Train.Dest) != de) continue;
            var up = u.Stops[j];
            if (tr.Length > 0 && (up.Track is not { } k || k < 1 || k > Circled.Length || Circled[k - 1].ToString() != tr)) continue;
            var tm = how == "着" ? up.Arr : up.Dep ?? (how == "通" ? up.Arr : null);
            if (tm is not { } x || x / 60 % 60 != mi) continue;
            var diff = Math.Abs(Wrap(x - baseSec));
            if (diff < bestDiff && diff <= 3600)
            {
                best = u.Train.Number;
                bestDiff = diff;
            }
        }
        return best;
    }

    /// <summary>時刻を変えたあとも待避・接続になるなら、書き直した記事。ならなければ null。</summary>
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
        return $"({kind}){u.Train.Type[..1]}{DestChar(u.Train.Dest)}{track}{sec / 60 % 60:00}{how}";
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
