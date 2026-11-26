using System.Text.Json;
using Gokko.Common;

namespace Gokko.Server.Services;

/// <summary>
/// 自動運転の見込みの値（Data/BrakeProfiles.json）。ふだんは読んだものを配るだけ（GET /api/brake-profiles。クライアントは乗務の初めに1回）。
/// 集計（<see cref="Build"/>）は管理用の API を押したときだけ、logs/runcurve-*.log（人の運転も自動運転も）から作り直す。
/// ・段の効き方: ブレーキの段が同じまま 2.5秒以上続いた所の、入れて 1.5秒あと（効ききったあと）からの減速度。10km/h 以上・勾配のゆるい所（|勾配| ≤ 5）だけ。
///   形式・段ごとの中央値（3つ以上あるものだけ）
/// ・効き始めの遅れ: 緩めた状態（B0）から入れた所で、減速度が効ききった値の 8割に届くまでの秒。形式ごとの中央値
/// ・駅ごとの補正: 自動運転が確定の残りで止まった記録の、補正なしのずれ（足していた補正＋止まったずれ）の中央値。3つ以上・3m 以内のものだけ
/// </summary>
public sealed class BrakeProfiles(string dataDir, RunCurves runs, ILogger<BrakeProfiles>? logger = null)
{
    public const string FileName = "BrakeProfiles.json";
    private const double SteadyAfterSec = 1.5;
    private const double MinSegmentSec = 2.5;
    private const double FlatGradient = 5;
    private const int MinSamples = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private BrakeProfileTable? _current;

    private string PathOf => Path.Combine(dataDir, FileName);

    /// <summary>今の表（ファイルがなければ空の表）。</summary>
    public BrakeProfileTable Current
    {
        get
        {
            lock (_gate)
            {
                if (_current != null) return _current;
                try
                {
                    _current = File.Exists(PathOf) ? JsonSerializer.Deserialize<BrakeProfileTable>(File.ReadAllText(PathOf), Json) : null;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "{File} を読めませんでした", FileName);
                }
                return _current ??= new BrakeProfileTable();
            }
        }
    }

    /// <summary>この何日かのランカーブから作り直して保存する。</summary>
    public BrakeProfileTable Build(int days)
    {
        var decel = new Dictionary<(string Model, int Notch), List<double>>();
        var lag = new Dictionary<string, List<double>>();
        var stops = new Dictionary<(string StopPos, string Track), (string Station, List<double> Errors)>();
        var count = 0;
        foreach (var (_, c) in runs.ReadDays(days))
        {
            count++;
            if (c.Model.Length > 0) Measure(c, decel, lag);
            if (c.Mode == "auto" && c.StopKind == "Confirmed" && c.StopPosName.Length > 0 && Math.Abs(c.StopRemaining) <= 5)
            {
                var key = (c.StopPosName, c.Track);
                if (!stops.TryGetValue(key, out var s)) stops[key] = s = (c.To, new List<double>());
                s.Errors.Add(c.StopOffset + c.StopRemaining);
            }
        }

        var table = new BrakeProfileTable { BuiltUtc = DateTime.UtcNow, Runs = count };
        foreach (var model in decel.Keys.Select(k => k.Model).Concat(lag.Keys).Distinct().OrderBy(m => m, StringComparer.Ordinal))
        {
            var p = new BrakeProfile { Model = model };
            for (var n = 2; n < p.Decel.Length; n++)
                if (decel.TryGetValue((model, n), out var list) && list.Count >= MinSamples)
                {
                    p.Decel[n] = Math.Round(Median(list), 3);
                    p.Samples[n] = list.Count;
                }
            if (lag.TryGetValue(model, out var lags) && lags.Count >= MinSamples)
            {
                p.LagSec = Math.Round(Median(lags), 2);
                p.LagSamples = lags.Count;
            }
            table.Models.Add(p);
        }
        foreach (var ((stopPos, track), (station, errors)) in stops.OrderBy(x => x.Key.StopPos, StringComparer.Ordinal))
        {
            if (errors.Count < MinSamples) continue;
            var m = Median(errors);
            if (Math.Abs(m) > 3) continue; // 大きすぎるずれは、補正ではなく停目の表か自動運転の直しで
            table.Stops.Add(new StopCorrection { Station = station, Track = track, StopPosName = stopPos, Offset = Math.Round(m, 2), Samples = errors.Count });
        }

        lock (_gate)
        {
            try
            {
                var tmp = PathOf + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(table, Json));
                File.Move(tmp, PathOf, overwrite: true);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "{File} を書けませんでした", FileName);
            }
            _current = table;
        }
        logger?.LogInformation("自動運転の見込みを作り直しました（記録 {Runs}・形式 {Models}・駅 {Stops}）", count, table.Models.Count, table.Stops.Count);
        return table;
    }

    /// <summary>1駅間の記録から、段ごとの効ききった減速度と効き始めの遅れを拾う。</summary>
    private static void Measure(RunCurve c, Dictionary<(string, int), List<double>> decel, Dictionary<string, List<double>> lag)
    {
        var pts = c.Points;
        var i = 0;
        while (i < pts.Count)
        {
            var b = pts[i].B;
            var j = i;
            while (j + 1 < pts.Count && pts[j + 1].B == b && pts[j + 1].P == 0) j++;
            if (b >= 2 && b < 8 && pts[i].P == 0 && pts[j].T - pts[i].T >= MinSegmentSec)
            {
                var t0 = pts[i].T;
                var window = pts.Skip(i).Take(j - i + 1).Where(p => p.T >= t0 + SteadyAfterSec && p.V >= 10).ToList();
                if (window.Count >= 3 && window[^1].T - window[0].T >= 1.0 && window.All(p => Math.Abs(p.G) <= FlatGradient))
                {
                    var steady = -Slope(window);
                    if (steady is > 0.3 and < 6)
                    {
                        if (!decel.TryGetValue((c.Model, b), out var list)) decel[(c.Model, b)] = list = new List<double>();
                        list.Add(steady);

                        // 緩めた状態から入れた所: 効ききった値の 8割に届くまでの秒
                        if (i > 0 && pts[i - 1].B == 0)
                            for (var k = i + 1; k <= j; k++)
                            {
                                var dt = pts[k].T - pts[k - 1].T;
                                if (dt <= 0) continue;
                                if ((pts[k - 1].V - pts[k].V) / dt >= steady * 0.8)
                                {
                                    var l = pts[k].T - t0;
                                    if (l is > 0 and < 3)
                                    {
                                        if (!lag.TryGetValue(c.Model, out var ls)) lag[c.Model] = ls = new List<double>();
                                        ls.Add(l);
                                    }
                                    break;
                                }
                            }
                    }
                }
            }
            i = j + 1;
        }
    }

    /// <summary>速度の時間に対する傾き（最小二乗。km/h/s）。</summary>
    private static double Slope(List<RunPoint> pts)
    {
        double mt = pts.Average(p => p.T), mv = pts.Average(p => p.V), num = 0, den = 0;
        foreach (var p in pts)
        {
            num += (p.T - mt) * (p.V - mv);
            den += (p.T - mt) * (p.T - mt);
        }
        return den > 0 ? num / den : 0;
    }

    private static double Median(List<double> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }
}
