using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gokko.Tests;

/// <summary>
/// 走らせている間に見張る「決まり」。1秒ごと（早回しなので、ロープレ時刻では数秒ごと）に、画面と同じ API の答えを受け取って確かめる。
/// 決まりを足すときは、ここに確かめる処理を足す。破ったら <see cref="Violations"/> に1件入れる（同じ列車・同じ決まりは1回だけ）。
/// </summary>
public sealed class Rules
{
    /// <summary>進路待ち・信号の手前で、これだけ（ロープレ時刻の分）動かなければ「詰まり」。</summary>
    private const double StuckMinutes = 12;
    /// <summary>出現待ちが、これだけ（ロープレ時刻の分）続いたら破ったことにする。</summary>
    private const double AppearMinutes = 5;
    /// <summary>同じ軌道回路に2本いるのが、これだけ（ロープレ時刻の秒）続いたら破ったことにする（列番が変わる一瞬の重なりは見ない）。</summary>
    private const double OverlapSeconds = 40;
    /// <summary>列車がいなくなったのに、その列車の進路が、これだけ（ロープレ時刻の秒）残っていたら破ったことにする。</summary>
    private const double LeftoverSeconds = 120;

    public List<Violation> Violations { get; } = new();
    public int TrainsSeen => _seen.Count;

    private readonly HashSet<string> _seen = new();
    private readonly HashSet<(string Rule, string Train)> _reported = new();
    private readonly Dictionary<string, (double Since, string Where, string Status)> _waiting = new(); // 列番 → 待ち始めた時刻・場所・状態
    private readonly Dictionary<string, double> _appearing = new();                                   // 出現待ちの列番 → 始まった時刻
    private readonly Dictionary<(string A, string B, string Circuit), double> _overlap = new();       // 同じ回路の2本 → 始まった時刻
    private readonly Dictionary<(string Train, string Station, string Route), double> _leftover = new(); // 列車のいない進路 → 始まった時刻
    private double _now;      // ロープレ時刻（走り始めからの通しの秒。日またぎで戻らない）
    private double _lastDay = -1, _dayOffset;
    private string _nowText = "";

    public void Observe(JsonElement tid, JsonElement ai, JsonElement dia)
    {
        _nowText = tid.GetProperty("gameTime").GetString() ?? "";
        if (!TryParseTime(_nowText, out var daySec)) return;
        if (_lastDay >= 0 && daySec < _lastDay - 12 * 3600) _dayOffset += 86400; // 0時をまたいだ
        // ロープレ時刻を飛ばした（設定し直した）: 待ち始めの時刻が合わなくなるので、数え直す
        if (_lastDay >= 0 && Math.Abs(daySec + _dayOffset - _now) > 600)
        {
            _waiting.Clear();
            _appearing.Clear();
            _overlap.Clear();
            _leftover.Clear();
        }
        _lastDay = daySec;
        _now = daySec + _dayOffset;

        var trains = tid.GetProperty("tid").GetProperty("trains").EnumerateArray()
            .Select(t => (Train: t.GetProperty("train").GetString() ?? "", Circuits: t.GetProperty("circuits").EnumerateArray().Select(c => c.GetString() ?? "").ToList()))
            .ToList();
        foreach (var t in trains) _seen.Add(t.Train);

        CheckNames(trains.Select(t => t.Train).ToList());
        CheckOverlap(trains);
        CheckLeftoverRoutes(tid, trains.Select(t => t.Train).ToHashSet());
        CheckStuck(ai);
        CheckAppear(dia);
    }

    /// <summary>決まり「同じ列番」: 同じ列番が2本いない。サーバーが呼び分けのために X・Y・Z や #2 を付けた列車がいない。</summary>
    private void CheckNames(List<string> names)
    {
        foreach (var g in names.GroupBy(n => n).Where(g => g.Count() > 1))
            Report("同じ列番", g.Key, $"同じ列番の列車が {g.Count()}本います");
        foreach (var n in names.Where(n => Regex.IsMatch(n, @"#\d+$")))
            Report("同じ列番", n, "同じ列番が重なって、サーバーが番号を付けて呼び分けています");
        // 接尾辞 X・Y・Z は、ダイヤにもある（1683CX など）ので、元の列番も同時に走っているときだけ
        foreach (var n in names.Where(n => Regex.IsMatch(n, "[XYZ]$") && names.Contains(n[..^1])))
            Report("同じ列番", n, $"{n[..^1]} と同時に走っています（同じ列番が重なって、接尾辞を付けて呼び分けたおそれ）");
    }

    /// <summary>
    /// 決まり「同じ回路」: 同じ軌道回路に2本の列車がいない。
    /// 連結する増結車（連777 と 777）・切り離した車両（解874 と 874）は、同じ番線にいてよい。
    /// </summary>
    private void CheckOverlap(List<(string Train, List<string> Circuits)> trains)
    {
        var live = new HashSet<(string, string, string)>();
        for (var i = 0; i < trains.Count; i++)
        for (var j = i + 1; j < trains.Count; j++)
        {
            var (a, b) = (trains[i], trains[j]);
            if (Paired(a.Train, b.Train)) continue;
            foreach (var c in a.Circuits.Intersect(b.Circuits))
            {
                var key = string.CompareOrdinal(a.Train, b.Train) < 0 ? (a.Train, b.Train, c) : (b.Train, a.Train, c);
                live.Add(key);
                if (!_overlap.TryGetValue(key, out var since)) _overlap[key] = _now;
                else if (_now - since >= OverlapSeconds)
                    Report("同じ回路", key.Item1, $"{key.Item2} と同じ軌道回路 {c} にいます（{(int)(_now - since)}秒）");
            }
        }
        foreach (var k in _overlap.Keys.Where(k => !live.Contains(k)).ToList()) _overlap.Remove(k);
    }

    /// <summary>決まり「残った進路」: 列車がいなくなった（消えた・列番が変わった）のに、その列車の進路が在線位置に残り続けない。</summary>
    private void CheckLeftoverRoutes(JsonElement tid, HashSet<string> trains)
    {
        var live = new HashSet<(string, string, string)>();
        foreach (var r in tid.GetProperty("tid").GetProperty("routes").EnumerateArray())
        {
            var train = r.TryGetProperty("train", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            if (train.Length == 0 || trains.Contains(train)) continue; // 列車のない進路（信号制御盤で取ったもの）は見ない
            var key = (train, r.GetProperty("station").GetString() ?? "", r.GetProperty("route").GetString() ?? "");
            live.Add(key);
            if (!_leftover.TryGetValue(key, out var since)) _leftover[key] = _now;
            else if (_now - since >= LeftoverSeconds)
                Report("残った進路", train, $"列車はもういないのに、{key.Item2} の進路 {key.Item3} が {(int)(_now - since)}秒 残っています");
        }
        foreach (var k in _leftover.Keys.Where(k => !live.Contains(k)).ToList()) _leftover.Remove(k);
    }

    private static bool Paired(string a, string b)
    {
        static string Core(string n) => Regex.Replace(n, "^(連|解|回|臨回|臨)", "");
        return (a.StartsWith('連') || a.StartsWith('解') || b.StartsWith('連') || b.StartsWith('解')) && Core(a) == Core(b);
    }

    /// <summary>
    /// 決まり「詰まり」: 自動運転の列車が、進路待ち・信号の手前で止まったまま、長く動かない。
    /// 発車時刻を待っている・駅に止まっているだけの列車は数えない（状態の文に「進路待ち」「手前で停止」「連結待ち」があるものだけ）。
    /// </summary>
    private void CheckStuck(JsonElement ai)
    {
        var live = new HashSet<string>();
        foreach (var t in ai.GetProperty("trains").EnumerateArray())
        {
            var train = t.GetProperty("train").GetString() ?? "";
            var status = t.GetProperty("status").GetString() ?? "";
            var where = t.GetProperty("where").GetString() ?? "";
            if (!Regex.IsMatch(status, "進路待ち|手前で停止|連結待ち|入換待ち")) continue;
            live.Add(train);
            if (!_waiting.TryGetValue(train, out var w) || w.Where != where) _waiting[train] = (_now, where, status);
            else
            {
                _waiting[train] = (w.Since, where, status);
                if (_now - w.Since >= StuckMinutes * 60)
                    Report("詰まり", train, $"{where} で {(int)((_now - w.Since) / 60)}分動いていません: {status}");
            }
        }
        foreach (var k in _waiting.Keys.Where(k => !live.Contains(k)).ToList()) _waiting.Remove(k);
    }

    /// <summary>決まり「出現待ち」: 現れた列車が、ほかの列車の在線・進路でふさがれて、長く始まらない。</summary>
    private void CheckAppear(JsonElement dia)
    {
        var live = new HashSet<string>();
        foreach (var a in dia.GetProperty("appear").EnumerateArray())
        {
            var train = a.GetProperty("train").GetString() ?? "";
            live.Add(train);
            if (!_appearing.TryGetValue(train, out var since)) _appearing[train] = _now;
            else if (_now - since >= AppearMinutes * 60)
                Report("出現待ち", train, $"{(int)((_now - since) / 60)}分、出現待ちのままです: {a.GetProperty("reason").GetString()}");
        }
        foreach (var k in _appearing.Keys.Where(k => !live.Contains(k)).ToList()) _appearing.Remove(k);
    }

    /// <summary>走り終えたあとの確かめ（今は、途中で見たものだけで決める）。</summary>
    public void Finish() { }

    /// <summary>決まり「エラー」: サーバーの記録に、エラー・例外が出ていない。</summary>
    public void CheckServerLog(string log)
    {
        var lines = log.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!Regex.IsMatch(lines[i], @"^\s*(fail|crit):|Unhandled exception")) continue;
            var text = (i + 1 < lines.Length ? lines[i + 1] : lines[i]).Trim();
            Report("エラー", "-", text.Length > 200 ? text[..200] : text, once: text);
        }
    }

    private void Report(string rule, string train, string text, string? once = null)
    {
        if (!_reported.Add((rule, once ?? train))) return;
        Violations.Add(new Violation(rule, _nowText, train, text));
    }

    private static bool TryParseTime(string text, out double sec)
    {
        sec = 0;
        var p = text.Split(':');
        if (p.Length < 2 || !int.TryParse(p[0], out var h) || !int.TryParse(p[1], out var m)) return false;
        sec = h * 3600 + m * 60 + (p.Length > 2 && int.TryParse(p[2], out var s) ? s : 0);
        return true;
    }
}
