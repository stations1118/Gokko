using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gokko.Tests;

/// <summary>
/// データの突き合わせ。同じこと（進路が通る軌道回路・接近の回路・信号機や回路の名前）が、何か所にも手で書いてあるので、
/// 食い違い・書きまちがいを機械で探す。サーバーは起動しない（ファイルを読むだけ。数秒）。
///
/// 見るファイル:
/// ・Gokko.Server/Data: TrackCircuits.json（回路）・Signals.json（信号機）・Interlockings.json（進路。自動進路構成・自動運転列車が使う）
///   ・TrackRoutes.json（番線ごとの進路）・Stations.json・AiTrains.json（自動運転列車の距離の表）
/// ・Gokko.Server/wwwroot: relaypanel/stations・denshirendo/stations（信号制御盤・電子連動装置）、tid-layout.json・tid-paths.json（在線位置）
///
/// 種類は2つ:
/// ・誤り … 名前が見つからない・参照先がないなど、どう見てもおかしいもの。known には書けない（直す）
/// ・食い違い … 2か所の書き方が違うもの。わざと違えているものは data-known.txt に理由と一緒に書く（書いていないものは落とす）
/// </summary>
public static class DataCheck
{
    public sealed record Finding(string Kind, string Rule, string Station, string Name, string Text);

    private static readonly JsonDocumentOptions Json = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static int Run(string root)
    {
        var findings = Check(root);
        var known = DataKnown.Load(Path.Combine(root, "Gokko.Tests", "data-known.txt"));
        var errors = findings.Where(f => f.Kind == "誤り").ToList();
        var diffs = findings.Where(f => f.Kind == "食い違い").ToList();
        var fresh = diffs.Where(f => !known.Covers(f)).ToList();
        var stale = known.Unused(diffs);

        Console.WriteLine($"データの突き合わせ: 誤り {errors.Count}件、食い違い {diffs.Count}件（うち、data-known.txt に書いてあるもの {diffs.Count - fresh.Count}件）");
        foreach (var g in errors.Concat(fresh).GroupBy(f => (f.Kind, f.Rule)))
        {
            Console.WriteLine();
            Console.WriteLine($"■ {g.Key.Kind}: {g.Key.Rule}（{g.Count()}件）");
            foreach (var f in g) Console.WriteLine($"  ✗ {f.Station} {f.Name} … {f.Text}");
        }
        if (diffs.Count > fresh.Count)
        {
            Console.WriteLine();
            Console.WriteLine("■ data-known.txt に書いてある食い違い（わざと・まだ決めていないもの）");
            foreach (var f in diffs.Where(known.Covers)) Console.WriteLine($"  （書いてある）{f.Rule} {f.Station} {f.Name} … {f.Text}");
        }
        foreach (var s in stale)
            Console.WriteLine($"  ※ data-known.txt の「{s}」は、もう食い違っていません（行を消してください）");

        Console.WriteLine();
        var bad = errors.Count + fresh.Count;
        Console.WriteLine(bad == 0 ? "合格: データに、誤りと、data-known.txt に書いていない食い違いはありません" : $"不合格: {bad}件を直すか、わざとなら data-known.txt に理由を書いてください");
        return bad == 0 ? 0 : 1;
    }

    public static List<Finding> Check(string root)
    {
        var data = Path.Combine(root, "Gokko.Server", "Data");
        var www = Path.Combine(root, "Gokko.Server", "wwwroot");
        var list = new List<Finding>();
        void Err(string rule, string st, string name, string text) => list.Add(new Finding("誤り", rule, st, name, text));
        void Diff(string rule, string st, string name, string text) => list.Add(new Finding("食い違い", rule, st, name, text));

        // ---- 元になるもの: 回路・信号機・進路 ----
        var circuits = Load(Path.Combine(data, "TrackCircuits.json")).GetProperty("trackCircuits").EnumerateArray()
            .Select(c => Str(c, "name")).ToHashSet();
        var signals = Load(Path.Combine(data, "Signals.json")).GetProperty("signals").EnumerateArray().Select(s => Str(s, "name")).ToHashSet();
        var stations = Load(Path.Combine(data, "Stations.json")).EnumerateArray()
            .ToDictionary(s => Str(s, "id"), s => (Name: Str(s, "name"), Interlock: Str(s, "interlock"), Control: s.TryGetProperty("routeControl", out var rc) && rc.ValueKind == JsonValueKind.True));

        // 進路のデータ（駅 → 進路名 → 通る回路・接近の回路・信号機）
        var routes = new Dictionary<string, Dictionary<string, (List<string> Circuits, List<string> Approach, string Signal)>>();
        foreach (var il in Load(Path.Combine(data, "Interlockings.json")).GetProperty("interlockings").EnumerateArray())
        {
            var st = Str(il, "stationId");
            var map = routes[st] = new();
            if (!stations.TryGetValue(st, out var sd)) Err("駅がない", st, "-", "進路のデータの駅が、駅の一覧（Stations.json）にありません");
            else if (sd.Interlock != Str(il, "interlock")) Err("連動装置の名前", st, "-", $"進路のデータは「{Str(il, "interlock")}」、駅の一覧は「{sd.Interlock}」");
            foreach (var r in il.GetProperty("routes").EnumerateArray())
            {
                var name = Str(r, "name");
                if (!map.TryAdd(name, (Arr(r, "circuits"), Arr(r, "approach"), Str(r, "signal")))) Err("進路が2つ", st, name, "進路のデータに、同じ名前の進路が2つあります");
                foreach (var c in Arr(r, "circuits").Concat(Arr(r, "approach")).Where(c => !circuits.Contains(c)))
                    Err("回路の名前", st, name, $"進路のデータの回路 {c} が、回路の一覧（TrackCircuits.json）にありません");
                if (Arr(r, "circuits").Count == 0) Err("通る回路がない", st, name, "進路のデータに、通る回路が書かれていません");
                if (Str(r, "signal") is { Length: > 0 } sig && !signals.Contains(sig)) Err("信号機の名前", st, name, $"進路の信号機「{sig}」が、信号機の一覧（Signals.json）にありません");
            }
        }

        // ---- 番線ごとの進路（TrackRoutes.json）: 進路の名前が進路のデータにある・回路の名前がある ----
        foreach (var s in Load(Path.Combine(data, "TrackRoutes.json")).GetProperty("stations").EnumerateArray())
        {
            var st = Str(s, "id");
            var have = routes.GetValueOrDefault(st);
            if (have == null) { Err("駅がない", st, "-", "番線ごとの進路（TrackRoutes.json）の駅が、進路のデータにありません"); continue; }
            foreach (var (path, el) in Walk(s, ""))
            {
                if (el.ValueKind != JsonValueKind.Array) continue;
                var key = path[(path.LastIndexOf('.') + 1)..];
                if (key is "routes" or "thenRoutes")
                    foreach (var n in el.EnumerateArray().Select(x => x.GetString() ?? "").Where(n => !have.ContainsKey(n)))
                        Err("進路の名前", st, n, $"番線ごとの進路（{path}）の進路が、進路のデータにありません");
                if (key is "requireFree")
                    foreach (var c in el.EnumerateArray().Select(x => x.GetString() ?? "").Where(c => !circuits.Contains(c)))
                        Err("回路の名前", st, c, $"番線ごとの進路（{path}）の回路が、回路の一覧にありません");
            }
            foreach (var (path, el) in Walk(s, ""))
                if (el.ValueKind == JsonValueKind.String && path.EndsWith("fromCircuit") && !circuits.Contains(el.GetString() ?? ""))
                    Err("回路の名前", st, el.GetString() ?? "", $"番線ごとの進路（{path}）の回路が、回路の一覧にありません");
        }

        // ---- 信号制御盤・電子連動装置 ----
        var panels = new Dictionary<string, (string File, JsonElement Data)>();
        foreach (var dir in new[] { Path.Combine(www, "relaypanel", "stations"), Path.Combine(www, "denshirendo", "stations") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                var d = Load(file);
                var id = Str(d, "id");
                var rel = Path.GetRelativePath(www, file).Replace('\\', '/');
                // サーバーは relaypanel → denshirendo の順に読み、同じ駅はあとのもので上書きする
                if (panels.TryGetValue(id, out var prev))
                    Diff("盤のデータが2つ", id, "-", $"{prev.File} と {rel} の両方に、この駅のデータがあります（サーバーが使うのは {rel}。{prev.File} は使われていません）");
                panels[id] = (rel, d);
            }
        }

        foreach (var (id, (file, d)) in panels.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var have = routes.GetValueOrDefault(id);
            if (have == null) { Err("駅がない", id, "-", $"盤（{file}）の駅が、進路のデータにありません"); continue; }
            if (stations.TryGetValue(id, out var sd) && Str(d, "interlock") != sd.Interlock)
                Err("連動装置の名前", id, "-", $"盤（{file}）は「{Str(d, "interlock")}」、駅の一覧は「{sd.Interlock}」");

            var points = Items(d, "points").Select(p => Str(p, "n")).ToHashSet();
            var levers = Items(d, "signalLevers").Select(l => Str(l, "n")).ToHashSet();
            var dests = Items(d, "destButtons").Select(b => Str(b, "id")).ToHashSet();
            var panelRoutes = new HashSet<string>();
            foreach (var r in Items(d, "routes"))
            {
                var name = Str(r, "name");
                if (!panelRoutes.Add(name)) Err("進路が2つ", id, name, $"盤（{file}）に、同じ名前の進路が2つあります");
                foreach (var c in Arr(r, "circuits").Concat(Arr(r, "approach")).Concat(Arr(r, "overrun")).Where(c => !circuits.Contains(c)))
                    Err("回路の名前", id, name, $"盤の進路の回路 {c} が、回路の一覧にありません");
                if (Str(r, "lever") is { Length: > 0 } lever && !levers.Contains(lever)) Err("盤の部品", id, name, $"進路のてこ {lever} が、盤のてこにありません");
                if (Str(r, "dest") is { Length: > 0 } dest && dests.Count > 0 && !dests.Contains(dest)) Err("盤の部品", id, name, $"進路の着点 {dest} が、盤の着点ボタンにありません");
                if (r.TryGetProperty("points", out var ps) && ps.ValueKind == JsonValueKind.Object)
                    foreach (var p in ps.EnumerateObject().Where(p => !points.Contains(p.Name)))
                        Err("盤の部品", id, name, $"進路の転てつ器 {p.Name} が、盤の転てつ器にありません");

                if (!have.TryGetValue(name, out var ir)) { Err("進路の名前", id, name, $"盤（{file}）の進路が、進路のデータ（Interlockings.json）にありません"); continue; }
                // 通る回路。盤の進路は、過走防護の回路（overrun）も circuits に入れている（鎖錠のため。ラインライトは点けない）ので、除いて比べる。
                // 進路のデータ（自動進路構成・自動運転列車が使う）は、行き先の回路までにして、その先の過走防護の回路を入れない決まり。
                // ゲームの進路のデータどおりに残すと決めた進路は、data-known.txt に理由と一緒に書いてある
                var over = Arr(r, "overrun").ToHashSet();
                var (pc, ic) = (Arr(r, "circuits").Where(c => !over.Contains(c)).ToHashSet(), ir.Circuits.ToHashSet());
                foreach (var c in over.Where(ic.Contains))
                    Diff("過走防護が進路のデータにある", id, name, $"盤で過走防護にしている回路 {c} が、進路のデータの通る回路に入っています");
                foreach (var c in over.Where(c => !Arr(r, "circuits").Contains(c)))
                    Diff("過走防護が盤の回路にない", id, name, $"過走防護の回路 {c} が、盤の進路の回路に入っていません（ほかの進路は入れています）");
                ic.ExceptWith(over);
                if (!pc.SetEquals(ic))
                    Diff("通る回路", id, name, $"盤だけ: {Join(pc.Except(ic))}　進路のデータだけ: {Join(ic.Except(pc))}");
                var (pa, ia) = (Arr(r, "approach").ToHashSet(), ir.Approach.ToHashSet());
                if (!pa.SetEquals(ia))
                    Diff("接近の回路", id, name, $"盤だけ: {Join(pa.Except(ia))}　進路のデータだけ: {Join(ia.Except(pa))}");
            }
            foreach (var n in have.Keys.Where(n => !panelRoutes.Contains(n)))
                Diff("盤にない進路", id, n, $"進路のデータにある進路が、盤（{file}）にありません");

            foreach (var h in Items(d, "signalHeads"))
                foreach (var n in Arr(h, "routes").Where(n => !panelRoutes.Contains(n)))
                    Diff("灯だけの信号", id, n, "盤に信号の灯はありますが、その進路が盤にも進路のデータにもありません（取れない進路）");
            foreach (var r in Items(d, "routes"))
                foreach (var n in Arr(r, "stops").Where(n => !panelRoutes.Contains(n)))
                    Err("盤の部品", id, Str(r, "name"), $"この進路が止める相手の進路 {n} が、盤の進路にありません");
            foreach (var l in Items(d, "trackLamps"))
                if (Str(l, "c") is { Length: > 0 } c && !circuits.Contains(c)) Err("回路の名前", id, c, "盤の軌道回路の灯の回路が、回路の一覧にありません");
            foreach (var p in Items(d, "points"))
                foreach (var c in Arr(p, "circuits").Append(Str(p, "circuit")).Where(c => c.Length > 0 && !circuits.Contains(c)))
                    Err("回路の名前", id, c, $"盤の転てつ器 {Str(p, "n")} の回路が、回路の一覧にありません");
            foreach (var x in Items(d, "directions"))
            {
                foreach (var c in Arr(x, "circuits").Where(c => !circuits.Contains(c))) Err("回路の名前", id, c, "盤の方向てこの回路が、回路の一覧にありません");
                foreach (var n in Arr(x, "out").Concat(Arr(x, "in")).Where(n => !panelRoutes.Contains(n)))
                    Err("盤の部品", id, n, "盤の方向てこが指す進路が、盤の進路にありません");
            }
        }
        foreach (var (id, sd) in stations.Where(s => s.Value.Control && !panels.ContainsKey(s.Key)))
            Diff("盤のない駅", id, sd.Name, "進路を扱う駅ですが、信号制御盤・電子連動装置のデータがありません");

        // ---- 在線位置（tid-layout.json・tid-paths.json） ----
        var drawn = new HashSet<string>();
        foreach (var (path, el) in Walk(Load(Path.Combine(www, "tid-layout.json")), ""))
        {
            if (!path.EndsWith(".circuits") && path != "circuits") continue;
            // 駅の線は「回路名 → 線」、駅間の線は [回路名, 長さ] の並び
            var here = el.ValueKind == JsonValueKind.Object ? el.EnumerateObject().Select(c => c.Name).ToList()
                : el.ValueKind == JsonValueKind.Array && el.GetArrayLength() > 0 && el[0].ValueKind == JsonValueKind.Array
                    ? el.EnumerateArray().Where(x => x.GetArrayLength() > 0 && x[0].ValueKind == JsonValueKind.String).Select(x => x[0].GetString() ?? "").ToList()
                    : new List<string>();
            foreach (var c in here)
            {
                drawn.Add(c);
                if (!circuits.Contains(c)) Err("回路の名前", "在線位置", c, "在線位置の線（tid-layout.json）の回路が、回路の一覧にありません");
            }
        }
        foreach (var p in Load(Path.Combine(www, "tid-paths.json")).GetProperty("paths").EnumerateArray())
            foreach (var c in p.EnumerateArray().Select(x => x.GetString() ?? ""))
            {
                if (!circuits.Contains(c)) Err("回路の名前", "在線位置", c, "在線位置の通り道（tid-paths.json）の回路が、回路の一覧にありません");
                else if (!drawn.Contains(c)) Err("在線位置に線がない", "在線位置", c, "通り道（tid-paths.json）にある回路が、在線位置の線（tid-layout.json）にありません");
            }
        // 進路が通る回路は、在線位置に線がある（ないと、その進路・在線が画面に出ない）
        foreach (var (st, map) in routes)
            foreach (var (name, r) in map)
                foreach (var c in r.Circuits.Where(c => circuits.Contains(c) && !drawn.Contains(c)))
                    Diff("在線位置に線がない", st, name, $"進路が通る回路 {c} が、在線位置の線（tid-layout.json）にありません");

        // ---- 自動運転列車の距離の表（AiTrains.json） ----
        var ai = Load(Path.Combine(data, "AiTrains.json"));
        var names = stations.Values.Select(s => s.Name).ToHashSet();
        if (Load(Path.Combine(data, "LineData.json")).TryGetProperty("stations", out var lineStations))
            foreach (var x in lineStations.EnumerateObject()) names.Add(x.Value.GetString() ?? "");
        foreach (var (path, el) in Walk(Load(Path.Combine(data, "Timetable.json")), "")) // ダイヤに出てくる駅名（江原検 など、駅の一覧にない呼び名）
            if (el.ValueKind == JsonValueKind.String && path.EndsWith("stops.name")) names.Add(el.GetString() ?? "");
        foreach (var s in Items(ai, "stops"))
        {
            var where = $"{Str(s, "station")} {Str(s, "dir")} {(s.TryGetProperty("track", out var t) ? t.ToString() : "")}番線";
            if (!names.Contains(Str(s, "station"))) Err("駅がない", "自動運転", where, "停止位置の表（AiTrains.json の stops）の駅が、路線の駅にも、ダイヤの駅名にもありません");
            if (Str(s, "signal") is { Length: > 0 } sig && !signals.Contains(sig)) Err("信号機の名前", "自動運転", where, $"停止位置の表の信号機「{sig}」が、信号機の一覧にありません");
        }
        foreach (var run in Items(ai, "signalRuns"))
            if (run.TryGetProperty("signals", out var sg) && sg.ValueKind == JsonValueKind.Object)
                foreach (var s in sg.EnumerateObject().Where(s => !signals.Contains(s.Name)))
                    Err("信号機の名前", "自動運転", Str(run, "train"), $"走ったときの記録（signalRuns）の信号機「{s.Name}」が、信号機の一覧にありません");
        if (ai.TryGetProperty("circuitLengths", out var cl) && cl.ValueKind == JsonValueKind.Object)
        {
            var lengths = cl.EnumerateObject().Select(x => x.Name).ToHashSet();
            foreach (var c in lengths.Where(c => !circuits.Contains(c))) Err("回路の名前", "自動運転", c, "回路の長さの表（circuitLengths）の回路が、回路の一覧にありません");
            // 進路が通る回路は、長さが分かる（分からないと、自動運転列車の位置がずれる）
            foreach (var (st, map) in routes)
                foreach (var c in map.Values.SelectMany(r => r.Circuits).Distinct().Where(c => circuits.Contains(c) && !lengths.Contains(c)))
                    Diff("回路の長さがない", st, c, "進路が通る回路の長さが、自動運転列車の表（AiTrains.json の circuitLengths）にありません");
        }

        return list.Distinct().ToList();
    }

    // ---- 読むための小さな道具 ----

    private static JsonElement Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), Json);
        return doc.RootElement.Clone();
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static List<string> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList() : new();

    private static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    /// <summary>JSON の中身を、場所の名前（a.b.c）つきで全部たどる。</summary>
    private static IEnumerable<(string Path, JsonElement El)> Walk(JsonElement e, string path)
    {
        yield return (path, e);
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
                foreach (var x in Walk(p.Value, path.Length == 0 ? p.Name : $"{path}.{p.Name}"))
                    yield return x;
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var item in e.EnumerateArray())
                foreach (var x in Walk(item, path))
                    yield return x;
    }

    private static string Join(IEnumerable<string> items)
    {
        var l = items.OrderBy(x => x, StringComparer.Ordinal).ToList();
        return l.Count == 0 ? "なし" : string.Join("・", l);
    }
}

/// <summary>
/// わざと違えている食い違い（data-known.txt）。1行に「決まりの名前 TAB 駅 TAB 進路や回路の名前（どれでもなら *）」。# から後ろに理由を書く。
/// </summary>
public sealed class DataKnown
{
    private readonly List<(string Rule, string Station, string Name, string Raw)> _items = new();

    public static DataKnown Load(string path)
    {
        var k = new DataKnown();
        if (!File.Exists(path)) return k;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = Regex.Replace(raw, "#.*$", "").Trim();
            if (line.Length == 0) continue;
            var p = line.Split('\t', StringSplitOptions.TrimEntries);
            if (p.Length < 2) continue;
            k._items.Add((p[0], p[1], p.Length > 2 ? p[2] : "*", line.Replace('\t', ' ')));
        }
        return k;
    }

    private static bool Match((string Rule, string Station, string Name, string Raw) x, DataCheck.Finding f) =>
        x.Rule == f.Rule && (x.Station == "*" || x.Station == f.Station) && (x.Name == "*" || x.Name == f.Name);

    public bool Covers(DataCheck.Finding f) => _items.Any(x => Match(x, f));

    /// <summary>もう当てはまる食い違いがない行（消し忘れ）。</summary>
    public List<string> Unused(List<DataCheck.Finding> diffs) => _items.Where(x => !diffs.Any(f => Match(x, f))).Select(x => x.Raw).ToList();
}
