using System.Text;
using System.Text.Json;

namespace Gokko.Server.Services;

/// <summary>
/// 浜園の特急の臨時停車の時刻（Data/TempStopRetime.json）。
/// 司令が特急に浜園の臨時停車を入れたら（列車ごとの指示「臨時停車」・催物用臨停設定）、その特急の時刻を臨停用にする
/// （上りは館浜〜津崎を2分繰り上げて浜園に1分止まる、下りは浜園に1分止まって津崎から先を繰り下げる）。
/// 待避・折返しで関わる普通・準急・回送の時刻も、ひな形（14時台の臨停用ダイヤ）と同じ形で動かす。
/// 指示を外せば所定の時刻に戻る。所定のダイヤ（Timetable.json）は変えず、時刻を変えた列車を重ねるだけ（<see cref="RailData.SetRetimed"/>）。
///
/// ・ひな形の特急と、area の駅の時刻が同じだけずれた特急（同じパターンの時間帯）には、ひな形をずらして当てはめる。
///   関わる列車も、ひな形の列車と area の駅の時刻が同じだけずれた列車を探す（見つからなければ、その列車は動かさない）
/// ・当てはまるひな形がない特急は、rule で特急の時刻だけ変える
/// ・浜園は時刻表では通過のまま（停車・通過は臨時停車の指示で決まる）。着・発の時刻だけ書く
/// ・ひな形は1時間ずつずらして当てはめる（:00 発と :30 発は時刻の形が同じでも、関わる列車が違う）
/// ・変えるのは時刻だけ。記事（待避・接続）は所定のまま
/// </summary>
public sealed class TempStopRetime
{
    public const string FileName = "TempStopRetime.json";

    private sealed record StopEdit(string St, bool SetArr, string? Arr, bool SetDep, string? Dep);
    private sealed record TrainEdit(string Number, string? Turn, List<StopEdit> Stops);
    private sealed record Rule(int Before, int Arr, int Dep, int After);

    private readonly RailData _rail;
    private readonly CentralControl _central;
    private readonly EventStops _events;
    private readonly ILogger<TempStopRetime>? _log;
    private readonly object _lock = new();

    /// <summary>臨時停車させる駅（浜園）。</summary>
    public string Station { get; } = "";

    /// <summary>時刻を変える種別（特急）。</summary>
    public string Type { get; } = "";

    // 特急の列番 → 臨時停車したときに変える列車と駅（その特急自身を含む）
    private readonly Dictionary<string, List<TrainEdit>> _plans = new();
    private string _lastKey = "";

    /// <summary>時刻を変えた・戻したときの記録（司令の操作の記録に残す）。</summary>
    public event Action<string>? Changed;

    public TempStopRetime(string dataDir, RailData rail, CentralControl central, EventStops events, ILogger<TempStopRetime>? log = null)
    {
        _rail = rail;
        _central = central;
        _events = events;
        _log = log;
        var path = Path.Combine(dataDir, FileName);
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            Station = Str(root, "station") ?? "";
            Type = Str(root, "type") ?? "";
            var area = root.TryGetProperty("area", out var ar) ? ar.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet() : new HashSet<string>();
            var rules = new Dictionary<Dir, Rule>();
            if (root.TryGetProperty("rule", out var ru))
                foreach (var p in ru.EnumerateObject())
                    rules[p.Name == "上り" ? Dir.Up : Dir.Down] = new Rule(Int(p.Value, "before"), Int(p.Value, "arr"), Int(p.Value, "dep"), Int(p.Value, "after"));
            var cases = root.TryGetProperty("cases", out var cs)
                ? cs.EnumerateArray().Select(c => (Train: Str(c, "train") ?? "", Edits: c.GetProperty("trains").EnumerateArray().Select(ReadEdit).ToList())).ToList()
                : new();
            Build(area, rules, cases);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "{File} を読めませんでした（臨時停車の時刻は変えません）", FileName);
            _plans.Clear();
        }
    }

    private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int Int(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static TrainEdit ReadEdit(JsonElement e)
    {
        var stops = new List<StopEdit>();
        if (e.TryGetProperty("stops", out var ss))
            foreach (var s in ss.EnumerateArray())
                stops.Add(new StopEdit(Str(s, "st") ?? "",
                    s.TryGetProperty("arr", out _), Str(s, "arr"),
                    s.TryGetProperty("dep", out _), Str(s, "dep")));
        return new TrainEdit(RailData.NormalizeTrain(Str(e, "number")), Str(e, "turn"), stops);
    }

    /// <summary>浜園を通過する特急ごとに、臨時停車したときに変える列車と駅を作っておく（所定のダイヤは起動したあと変わらない）。</summary>
    private void Build(HashSet<string> area, Dictionary<Dir, Rule> rules, List<(string Train, List<TrainEdit> Edits)> cases)
    {
        var baseDia = _rail.BaseDia;
        var byType = baseDia.Values.GroupBy(d => d.Type).ToDictionary(g => g.Key, g => g.ToList());
        var targets = baseDia.Values.Where(d => d.Type == Type && StationIndex(d) is { } i && d.Stops[i].Stop == "通").ToList();
        var fromCase = 0;
        foreach (var b in targets)
        {
            List<TrainEdit>? plan = null;
            foreach (var (train, edits) in cases)
            {
                if (!baseDia.TryGetValue(RailData.NormalizeTrain(train), out var a) || DirOf(a) != DirOf(b)) continue;
                if (Offset(a, b, area) is not { } delta || delta % 3600 != 0) continue; // 同じパターンは1時間ごと（:00 発と :30 発は形が同じでも、関わる列車が違う）
                plan = new List<TrainEdit>();
                var missing = new List<string>();
                foreach (var e in edits)
                {
                    if (!baseDia.TryGetValue(e.Number, out var x)) continue;
                    var y = x == a ? b : byType.GetValueOrDefault(x.Type)?.FirstOrDefault(c => Offset(x, c, area, delta) != null);
                    if (y == null)
                    {
                        missing.Add(x.Number);
                        continue;
                    }
                    plan.Add(Shift(e, y.Number, delta));
                }
                if (missing.Count > 0)
                    _log?.LogInformation("臨時停車の時刻: {Train}（{Case} の形）で動かす列車のうち、{Missing} に当たる列車がありません（動かしません）", b.Number, a.Number, string.Join("・", missing));
                fromCase++;
                break;
            }
            plan ??= RuleEdit(b, rules) is { } re ? [re] : null;
            if (plan != null) _plans[b.Number] = plan;
        }
        _log?.LogInformation("臨時停車の時刻: {Station} の{Type} {Count}本（ひな形から {Case}本、特急だけ {Rule}本）",
            _rail.Line.StationName(Station), Type, _plans.Count, fromCase, _plans.Count - fromCase);
    }

    private int? StationIndex(DiaTrain d)
    {
        var i = d.Stops.FindIndex(s => s.StationId == Station);
        return i >= 0 ? i : null;
    }

    private Dir? DirOf(DiaTrain d) => RouteEngine.TrainInfo(d.Number)?.Dir;

    /// <summary>
    /// y が x と同じ形で時刻だけずれた列車なら、そのずれ（秒）。area の駅の並び・停車・通過が同じで、時刻がどれも同じだけずれていること。
    /// expect を渡せば、そのずれのときだけ。
    /// </summary>
    private static int? Offset(DiaTrain x, DiaTrain y, HashSet<string> area, int? expect = null)
    {
        var xs = x.Stops.Where(s => s.StationId is { } id && area.Contains(id)).ToList();
        var ys = y.Stops.Where(s => s.StationId is { } id && area.Contains(id)).ToList();
        if (xs.Count == 0 || xs.Count != ys.Count) return null;
        var delta = expect;
        for (var i = 0; i < xs.Count; i++)
        {
            if (xs[i].StationId != ys[i].StationId || xs[i].Stop != ys[i].Stop) return null;
            foreach (var (a, b) in new[] { (xs[i].Arr, ys[i].Arr), (xs[i].Dep, ys[i].Dep) })
            {
                if ((a == null) != (b == null)) return null;
                if (Sec(a) is not { } sa || Sec(b) is not { } sb) continue;
                var d = Wrap(sb - sa);
                if (delta == null) delta = d;
                else if (delta != d) return null;
            }
        }
        return delta;
    }

    /// <summary>ひな形の列車の変え方を、列番 number・ずれ delta 秒の列車に当てはめる。</summary>
    private static TrainEdit Shift(TrainEdit e, string number, int delta)
    {
        return new TrainEdit(number, e.Turn != null ? Format(Sec(e.Turn), delta, false) : null,
            e.Stops.Select(s => s with
            {
                Arr = s.SetArr ? Format(Sec(s.Arr), delta, true) : null,
                Dep = s.SetDep ? Format(Sec(s.Dep), delta, true) : null
            }).ToList());
    }

    /// <summary>ひな形のない特急: 浜園の着・発を所定の通過時刻から、浜園より前・後の駅を rule のぶんずらす。</summary>
    private TrainEdit? RuleEdit(DiaTrain d, Dictionary<Dir, Rule> rules)
    {
        if (StationIndex(d) is not { } si || DirOf(d) is not { } dir || !rules.TryGetValue(dir, out var r)) return null;
        if (Sec(d.Stops[si].Dep ?? d.Stops[si].Arr) is not { } pass) return null;
        var stops = new List<StopEdit>();
        for (var i = 0; i < d.Stops.Count; i++)
        {
            var s = d.Stops[i];
            if (s.StationId == null) continue;
            if (i == si)
            {
                stops.Add(new StopEdit(s.StationId, true, Format(pass, r.Arr, true), true, Format(pass, r.Dep, true)));
                continue;
            }
            var off = i < si ? r.Before : r.After;
            if (off == 0) continue;
            stops.Add(new StopEdit(s.StationId, s.Arr != null, Format(Sec(s.Arr), off, true), s.Dep != null, Format(Sec(s.Dep), off, true)));
        }
        return new TrainEdit(d.Number, null, stops);
    }

    /// <summary>
    /// 臨時停車の指示を見て、時刻を変える列車を作り直す（指示・催物用臨停設定が変わったとき）。変わらなければ何もしない。
    /// </summary>
    public void Refresh()
    {
        if (_plans.Count == 0) return;
        lock (_lock)
        {
            var stopped = _plans.Keys.Where(IsStopped).Order(StringComparer.Ordinal).ToList();
            var key = string.Join(",", stopped);
            if (key == _lastKey) return;
            var before = _lastKey.Length == 0 ? new List<string>() : _lastKey.Split(',').ToList();
            _lastKey = key;

            var retimed = new Dictionary<string, DiaTrain>();
            foreach (var n in stopped)
                foreach (var e in _plans[n])
                {
                    var cur = retimed.GetValueOrDefault(e.Number) ?? _rail.BaseDia.GetValueOrDefault(e.Number);
                    if (cur != null) retimed[e.Number] = Apply(cur, e);
                }
            _rail.SetRetimed(retimed);

            var added = stopped.Except(before).ToList();
            var removed = before.Except(stopped).ToList();
            var others = retimed.Keys.Except(stopped).Order(StringComparer.Ordinal).ToList();
            Changed?.Invoke($"{_rail.Line.StationName(Station)}の臨時停車の時刻: "
                            + string.Join("、", new[]
                            {
                                added.Count > 0 ? $"{string.Join("・", added)} を臨停用の時刻に" : null,
                                removed.Count > 0 ? $"{string.Join("・", removed)} を所定の時刻に" : null
                            }.OfType<string>())
                            + (others.Count > 0 ? $"（関わる列車: {string.Join("・", others)}）" : ""));
        }
    }

    /// <summary>この特急が浜園に臨時停車するか（列車ごとの指示が先。なければ催物用臨停設定）。</summary>
    private bool IsStopped(string number)
    {
        if (!_rail.BaseDia.TryGetValue(number, out var d) || StationIndex(d) is not { } i) return false;
        return _central.StopOverride(number, Station) switch
        {
            "停車" => true,
            null => _events.Applies(d, i),
            _ => false
        };
    }

    /// <summary>列車 d の時刻を e のとおりに変えた、新しい列車（d は変えない）。</summary>
    private static DiaTrain Apply(DiaTrain d, TrainEdit e)
    {
        var n = new DiaTrain
        {
            Number = d.Number, Type = d.Type, TypeLabel = d.TypeLabel, Dest = d.Dest, TerminalId = d.TerminalId, Next = d.Next,
            Unyo = d.Unyo, Cars = d.Cars, Turn = e.Turn != null && d.Turn != null ? d.Turn with { Time = e.Turn } : d.Turn
        };
        foreach (var s in d.Stops)
        {
            var x = e.Stops.FirstOrDefault(y => y.St == s.StationId);
            n.Stops.Add(x == null ? s : s with
            {
                Arr = x.SetArr ? x.Arr : s.Arr,
                Dep = x.SetDep ? x.Dep : s.Dep
            });
        }
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

    /// <summary>ずれを -12時間〜12時間にする（日をまたぐ所）。</summary>
    private static int Wrap(int d) => (d % 86400 + 86400 + 43200) % 86400 - 43200;

    private static string? Format(int? sec, int delta, bool seconds)
    {
        if (sec is not { } s) return null;
        var t = ((s + delta) % 86400 + 86400) % 86400;
        return seconds ? $"{t / 3600:00}:{t / 60 % 60:00}:{t % 60:00}" : $"{t / 3600:00}:{t / 60 % 60:00}";
    }
}
