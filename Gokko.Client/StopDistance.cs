using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gokko.Common;

namespace Gokko.Client;

/// <summary>ゲームの停車駅1つ（TrainCrewInput の stationList から）。TotalLength はその駅の停止位置を走った距離で（シナリオの番線・両数に合った停目）。</summary>
public sealed record GameStop(string Name, string StopPosName, bool Stops, double TotalLength);

/// <summary>ゲームの信号機1つ（名前・現示・そこまでの距離 m）。</summary>
public sealed record GameSignal(string Name, string Phase, double Distance);

/// <summary>
/// 停目までの距離を出すのに要る、ゲームの今の値。Traveled は走った距離（TrainState.TotalLength）、Speed は km/h、
/// OwnCircuits は自列車のいる軌道回路（なければ空）。
/// </summary>
public sealed record StopDistanceInput(
    double Traveled,
    double Speed,
    int Cars,
    IReadOnlyList<GameStop> Stations,
    IReadOnlyList<GameSignal> Signals,
    IReadOnlyCollection<string> OwnCircuits);

/// <summary>残り m の確かさ。Rough はゲームの距離から出した値（数 m〜数十 m ずれることがある）、Confirmed は信号機から出した値。</summary>
public enum StopDistanceKind { None, Rough, Confirmed }

/// <summary>
/// 次に止まる停目までの残り（m。行き過ぎていれば負）。Station はダイヤの駅名、Track は実際に入る（入った）番線、
/// ScenarioTrack はシナリオの番線（番線のない駅は ""）。Kind が None なら止まる駅がない（Remaining は使わない）。
/// 記録用: Rough はゲームの距離から出した残り（確定のときも入る。確定との差がずれ）、Signal・SignalDistance は止まる番線の信号機と、
/// そこまでの距離（200m より遠くても、見えていれば入る。見えなければ null）。
/// </summary>
public sealed record StopDistanceResult(double Remaining, StopDistanceKind Kind, string Station, string Track, string ScenarioTrack,
    double Rough = 0, string? Signal = null, double? SignalDistance = null)
{
    public static readonly StopDistanceResult Nothing = new(0, StopDistanceKind.None, "", "", "");
}

/// <summary>
/// 次に止まる停目までの残り距離を出す（クライアントの自動運転のブレーキに渡す。サーバーには毎回聞かない）。
///
/// ・ゲームの stationList の停車駅の位置（TotalLength）は、シナリオの番線の、その列車の両数に合った停目（測定記録で確認）。
///   ただし走った距離は実際の道筋なので、シナリオと違う番線を通ると、その先の駅の距離がずれていく。そこで
///   (1) 停目に止まるたびに、そこを起点に付け直す（その駅間のずれだけになり、積み重ならない）
///   (2) 止まる番線の信号機が見えたら、信号機までの距離 − 6両停目の位置 − 両数の停目の距離 で出す（確定）
/// ・実際に入る番線は、自列車の軌道回路がその番線の回路か、その番線に入る場内信号機が R でないかで見分ける。分からなければシナリオの番線
/// ・表（<see cref="StopTargetTable"/>）は GET /api/stop-targets を乗務の初めに1回だけ読む。読めなければ、ゲームの距離だけで出す（ずっと Rough）
/// </summary>
public sealed partial class StopDistance
{
    /// <summary>止まったとみなす速度（km/h）と、止まった駅とみなす残りの幅（m）。</summary>
    private const double StoppedSpeed = 0.5, StoppedWithin = 15;

    /// <summary>この距離（m）より先へ行き過ぎたら、止まらずにその駅を過ぎたとみなして次の駅へ進む。</summary>
    private const double PassedBeyond = 50;

    /// <summary>
    /// 信号機から出すのは、信号機までこの距離（m）以内のときだけ。それより遠いと、ゲームの信号機の距離が実際より短く出ることがある
    /// （測定記録で、200m 以内は ±2.2m、高見沢2番上りの 300〜350m で 9〜23m 短かった）。
    /// </summary>
    private const double SignalTrustedWithin = 200;

    private readonly Dictionary<(string Dir, string Station, string Track), StopTarget> _rows = new();
    private int _doneIndex = -1;
    private double? _anchorScenario, _anchorTraveled;

    /// <summary>信号機から出せた停目（駅の並びの番号・番線・その停目の位置を走った距離で）。</summary>
    private (int Index, string Track, double At)? _confirmed;

    public StopDistance(StopTargetTable? table)
    {
        foreach (var t in table?.Targets ?? new List<StopTarget>())
            _rows[(t.Dir, t.Station, t.Track)] = t;
    }

    /// <summary>表を読む（GET /api/stop-targets）。読めなければ null（そのときもゲームの距離だけで動く）。</summary>
    public static async Task<StopTargetTable?> LoadTableAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            return await http.GetFromJsonAsync<StopTargetTable>(ServerAddress.Current.TrimEnd('/') + "/api/stop-targets",
                new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>起点を捨てる（シナリオが変わった、運転をやり直したなど）。</summary>
    public void Reset()
    {
        _doneIndex = -1;
        _anchorScenario = _anchorTraveled = null;
        _confirmed = null;
    }

    /// <summary>今の残り距離。ゲームの値が来るたびに呼ぶ。</summary>
    public StopDistanceResult Update(StopDistanceInput x)
    {
        // 止まった駅にまだいる（起点から動いていない）間は、その駅の残りを出し続ける
        if (_doneIndex >= 0 && _doneIndex < x.Stations.Count && _anchorTraveled is { } at && x.Speed < StoppedSpeed && Math.Abs(x.Traveled - at) < 1)
            return Compute(x, _doneIndex);

        while (true)
        {
            var target = NextStop(x.Stations);
            if (target < 0) return StopDistanceResult.Nothing;

            var r = Compute(x, target);
            if (x.Speed < StoppedSpeed && Math.Abs(r.Remaining) <= StoppedWithin)
            {
                // 止まった: ここを起点にする（止まっている間はこの駅の残りを出し続け、走り出したら次の駅へ）
                _doneIndex = target;
                _anchorScenario = x.Stations[target].TotalLength;
                _anchorTraveled = x.Traveled;
                return r;
            }
            if (r.Remaining < -PassedBeyond)
            {
                _doneIndex = target; // 止まらずに過ぎた。起点はそのまま
                continue;
            }
            return r;
        }
    }

    /// <summary>まだ止まっていない、いちばん手前の停車駅（なければ -1）。</summary>
    private int NextStop(IReadOnlyList<GameStop> stations)
    {
        for (var i = _doneIndex + 1; i < stations.Count; i++)
            if (stations[i].Stops) return i;
        return -1;
    }

    private StopDistanceResult Compute(StopDistanceInput x, int index)
    {
        var stop = x.Stations[index];
        var (station, scenarioTrack, dir) = ParseStopPos(stop.StopPosName, stop.Name);
        var track = ActualTrack(x, station, dir, scenarioTrack);
        var actual = Find(dir, station, track);

        // (1) ゲームの距離。止まった駅を起点に、その駅間の距離だけ使う（確定のときも、比べられるように Rough に入れて返す）
        var rough = _anchorScenario is { } a && _anchorTraveled is { } t
            ? stop.TotalLength - a - (x.Traveled - t)
            : stop.TotalLength - x.Traveled;
        if (track != scenarioTrack && Find(dir, station, scenarioTrack) is { } scenario && actual != null)
            rough += scenario.OffsetFor(x.Cars) - actual.OffsetFor(x.Cars); // ゲームの値はシナリオの番線の停目。入る番線の停目に直す

        // (2) 止まる番線の信号機が見えていれば、信号機から出す
        var signal = actual?.Signal is { } name ? x.Signals.FirstOrDefault(s => s.Name == name) : null;
        if (actual != null && signal is { Distance: <= SignalTrustedWithin })
        {
            var sixCar = actual.After is { } after ? signal.Distance + after : signal.Distance - (actual.Before ?? 0);
            var confirmed = sixCar - actual.OffsetFor(x.Cars);
            _confirmed = (index, track, confirmed + x.Traveled);
            return new StopDistanceResult(confirmed, StopDistanceKind.Confirmed, station, track, scenarioTrack, rough, signal.Name, signal.Distance);
        }
        // 一度信号機から出せた駅・番線は、信号機が見えなくなっても（場内を過ぎた、ゲームの信号機の知らせが途切れた）走った距離で数え続ける
        if (_confirmed is { } c && c.Index == index && c.Track == track)
            return new StopDistanceResult(c.At - x.Traveled, StopDistanceKind.Confirmed, station, track, scenarioTrack, rough, signal?.Name, signal?.Distance);

        return new StopDistanceResult(rough, StopDistanceKind.Rough, station, track, scenarioTrack, rough, signal?.Name, signal?.Distance);
    }

    /// <summary>実際に入る（入った）番線。自列車の軌道回路 → 開いている場内信号機 → シナリオの番線 の順に見る。</summary>
    private string ActualTrack(StopDistanceInput x, string station, string dir, string scenarioTrack)
    {
        var rows = _rows.Values.Where(r => r.Dir == dir && r.Station == station && r.Track.Length > 0).ToList();
        if (rows.Count == 0) return scenarioTrack;
        if (rows.FirstOrDefault(r => r.Circuit != null && x.OwnCircuits.Contains(r.Circuit)) is { } inside) return inside.Track;
        // 場内信号機は、1つの番線だけに入るものだけ使う（館浜 5L のように、どの番線にも入る場内では番線が分からない）
        var shared = rows.SelectMany(r => r.ArriveSignals).GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var open = rows
            .SelectMany(r => r.ArriveSignals.Where(n => !shared.Contains(n)).Select(name => (Row: r, Signal: x.Signals.FirstOrDefault(s => s.Name == name))))
            .Where(p => p.Signal != null && p.Signal.Phase.Length > 0 && p.Signal.Phase != "R" && p.Signal.Phase != "None")
            .OrderBy(p => p.Signal!.Distance)
            .FirstOrDefault();
        return open.Row?.Track ?? scenarioTrack;
    }

    /// <summary>その駅・番線の行。番線の行がなければ、番線のない行。</summary>
    private StopTarget? Find(string dir, string station, string track) =>
        track.Length > 0 && _rows.TryGetValue((dir, station, track), out var row) ? row
        : _rows.TryGetValue((dir, station, ""), out var any) ? any : null;

    [GeneratedRegex(@"^(?<name>.+?)駅?(?:(?<track>\d+)番)?(?<dir>上り|下り)$")]
    private static partial Regex StopPosPattern();

    /// <summary>
    /// 停止位置名（「高見沢駅1番下り」「江ノ原信号場下り」「江ノ原検車区11番上り」）を、ダイヤの駅名・番線・方向（"up"・"down"）に分ける。
    /// 読めなければ駅名はゲームの駅名、番線は ""、方向は ""。
    /// </summary>
    public static (string Station, string Track, string Dir) ParseStopPos(string stopPosName, string gameName)
    {
        var m = StopPosPattern().Match(stopPosName ?? "");
        var name = m.Success ? m.Groups["name"].Value : gameName ?? "";
        if (name.StartsWith("江ノ原検車区", StringComparison.Ordinal) || name.StartsWith("江ノ原車庫", StringComparison.Ordinal)) name = "江原検";
        return (name, m.Success ? m.Groups["track"].Value : "", !m.Success ? "" : m.Groups["dir"].Value == "上り" ? "up" : "down");
    }
}
