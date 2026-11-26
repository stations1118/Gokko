namespace Gokko.Server.Services;

/// <summary>催物用臨停設定の1件。Dir が null なら上下とも。時刻は運転日の秒（4時より前は翌日として 24時以降の秒）。ToSec が null なら終わりなし。</summary>
public sealed record EventStopRule(long Id, string Station, string StationName, string Kind, Dir? Dir, int FromSec, int? ToSec);

/// <summary>
/// 催物用臨停設定（司令用管理画面）。催し物の日に、駅・種別・向き・時間帯をまとめて臨時停車にする。
/// ・選んだ種別と、それより下の種別（準急まで）が臨時停車になる。普通はもともと止まる。回送・試運転は対象外
/// ・時刻表でその駅が「通」の列車だけ。時間帯は、その駅を通る時刻（通過時刻）で判断する
/// ・臨時停車の時刻は、発＝通過時刻、着＝通過時刻 − <see cref="DwellSeconds"/>
/// 列車ごとの指示（臨時停車・臨時通過・打ち切り）があれば、そちらが先。
/// 司令の指示と同じく、サーバーを再起動すると消える（ロープレ時刻を設定しても消さない。前もって入れておく設定のため）。
/// </summary>
public sealed class EventStops
{
    /// <summary>種別の上からの順（略称。時刻表の種別）。選んだ種別から下がすべて臨時停車になる。</summary>
    public static readonly string[] KindOrder = ["特急", "快急", "急行", "区急", "準急"];

    /// <summary>画面に出す種別の名前。</summary>
    public static string KindLabel(string kind) => kind switch { "快急" => "快速急行", "区急" => "区間急行", _ => kind };

    /// <summary>臨時停車の停車時間（秒）。時刻表の普通・準急の停車時分で一番多いもの。</summary>
    public const int DwellSeconds = 30;

    private readonly object _lock = new();
    private readonly List<EventStopRule> _rules = new();
    private long _seq;
    private long _version;

    /// <summary>設定を変えるたびに増える（ダイヤグラムの計画の線を読み直す合図など）。</summary>
    public long Version => Interlocked.Read(ref _version);

    public event Action<string>? Changed;

    public IReadOnlyList<EventStopRule> List()
    {
        lock (_lock) return _rules.ToList();
    }

    public EventStopRule Add(string station, string stationName, string kind, Dir? dir, int fromSec, int? toSec)
    {
        EventStopRule rule;
        lock (_lock)
        {
            rule = new EventStopRule(++_seq, station, stationName, kind, dir, fromSec, toSec);
            _rules.Add(rule);
        }
        Interlocked.Increment(ref _version);
        Changed?.Invoke($"催物用臨停設定: 追加 {Describe(rule)}");
        return rule;
    }

    public bool Remove(long id)
    {
        EventStopRule? rule;
        lock (_lock)
        {
            rule = _rules.FirstOrDefault(r => r.Id == id);
            if (rule == null) return false;
            _rules.Remove(rule);
        }
        Interlocked.Increment(ref _version);
        Changed?.Invoke($"催物用臨停設定: 削除 {Describe(rule)}");
        return true;
    }

    public static string Describe(EventStopRule r) =>
        $"{r.StationName} {KindLabel(r.Kind)}{(r.Kind == "準急" ? "" : "〜準急")} {(r.Dir?.Label() ?? "上下")} {FormatTime(r.FromSec)}〜{(r.ToSec is { } t ? FormatTime(t) : "")}";

    /// <summary>"HH:mm" または "HH:mm:ss" → 運転日の秒（4時より前は翌日）。読めなければ null。</summary>
    public static int? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var p = text.Trim().Replace('：', ':').Split(':');
        if (p.Length is < 2 or > 3 || !int.TryParse(p[0], out var hh) || !int.TryParse(p[1], out var mm) || hh is < 0 or > 29 || mm is < 0 or > 59) return null;
        var ss = 0;
        if (p.Length == 3 && (!int.TryParse(p[2], out ss) || ss is < 0 or > 59)) return null;
        return DaySec(hh * 3600 + mm * 60 + ss);
    }

    public static string FormatTime(int sec) => $"{sec / 3600 % 24:00}:{sec / 60 % 60:00}";

    private static int DaySec(int sec) => sec < 4 * 3600 ? sec + 86400 : sec;

    private static int? StopSec(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var p = text.Split(':');
        if (p.Length < 2 || !int.TryParse(p[0], out var hh) || !int.TryParse(p[1], out var mm)) return null;
        var ss = p.Length > 2 && int.TryParse(p[2], out var x) ? x : 0;
        return DaySec(hh * 3600 + mm * 60 + ss);
    }

    private static string FormatStopTime(int sec) => $"{sec / 3600 % 24:00}:{sec / 60 % 60:00}:{sec % 60:00}";

    /// <summary>
    /// その駅の通過時刻（運転日の秒）。時刻が書かれていなければ、前後の時刻のある駅から割り振る（駅の数で等分）。
    /// </summary>
    private static int? PassSec(DiaTrain dia, int index)
    {
        var s = dia.Stops[index];
        if (StopSec(s.Dep ?? s.Arr) is { } t) return t;
        int? before = null, after = null;
        int bi = -1, ai = -1;
        for (var i = index - 1; i >= 0 && before == null; i--)
            if (StopSec(dia.Stops[i].Dep ?? dia.Stops[i].Arr) is { } b) (before, bi) = (b, i);
        for (var i = index + 1; i < dia.Stops.Count && after == null; i++)
            if (StopSec(dia.Stops[i].Arr ?? dia.Stops[i].Dep) is { } a) (after, ai) = (a, i);
        if (before is { } b0 && after is { } a0) return b0 + (a0 - b0) * (index - bi) / (ai - bi);
        return before ?? after;
    }

    /// <summary>列車 → 所定のダイヤの同じ列車（時刻を変えていないもの）。Program.cs で RailData.BaseDia を渡す。</summary>
    public Func<DiaTrain, DiaTrain?>? BaseOf { get; set; }

    /// <summary>この列車の、時刻表の index 番目の駅が、催物用臨停設定で臨時停車になるか。</summary>
    public bool Applies(DiaTrain dia, int index)
    {
        if (index < 0 || index >= dia.Stops.Count) return false;
        var s = dia.Stops[index];
        if (s.Stop != "通" || s.StationId == null) return false;
        var rank = Array.IndexOf(KindOrder, dia.Type);
        if (rank < 0) return false;
        List<EventStopRule> rules;
        lock (_lock)
        {
            if (_rules.Count == 0) return false;
            rules = _rules.Where(r => r.Station == s.StationId).ToList();
        }
        // 時間帯は所定の通過時刻で見る（臨時停車用に時刻を変えた列車でも、入るかどうかが変わらないように）
        var baseDia = BaseOf?.Invoke(dia) is { } bd && bd.Stops.Count == dia.Stops.Count ? bd : dia;
        if (rules.Count == 0 || PassSec(baseDia, index) is not { } sec) return false;
        var dir = RouteEngine.TrainInfo(dia.Number)?.Dir;
        return rules.Any(r => rank >= Array.IndexOf(KindOrder, r.Kind) && (r.Dir == null || r.Dir == dir)
                              && sec >= r.FromSec && (r.ToSec is not { } to || sec <= to));
    }

    /// <summary>駅IDで探す版（その駅が時刻表にない列車は false）。</summary>
    public bool Applies(DiaTrain dia, string stationId) => Applies(dia, dia.Stops.FindIndex(s => s.StationId == stationId));

    /// <summary>
    /// 臨時停車になるなら、その駅の着・発（"HH:mm:ss"）。発＝通過時刻、着＝通過時刻 − 停車時間。
    /// 通過時刻が書かれていない駅は、どちらも null（スタフでは「臨時停車」とだけ書く）。臨時停車でなければ null。
    /// 臨時停車用に時刻を変えた列車（着も発も書いてある）は、その着・発。
    /// </summary>
    public (string? Arr, string? Dep)? Times(DiaTrain dia, int index)
    {
        if (!Applies(dia, index)) return null;
        var s = dia.Stops[index];
        // 臨時停車用に時刻を変えた列車（TempStopRetime）は、着も書いてあるので、それを使う
        if (s.Arr != null && s.Dep != null) return (s.Arr, s.Dep);
        var dep = s.Dep ?? s.Arr;
        return (StopSec(dep) is { } t ? FormatStopTime(t - DwellSeconds) : null, dep);
    }
}
