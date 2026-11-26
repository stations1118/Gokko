using System.Globalization;
using Gokko.Common;

namespace Gokko.Server.Services;

/// <summary>
/// 停目までの距離の表（GET /api/stop-targets）を作る。クライアントの自動運転が乗務の初めに1回だけ読み、
/// 距離の計算はクライアントがする。表は起動時のデータから1回だけ作って使い回す。
/// ・6両停目の位置: AiTrains.json の stops（番線ごとに、信号機の何 m 手前か）
/// ・両数の停目: 停止位置目標の表（StopMarkers.*.csv）
/// ・番線の軌道回路・場内信号機: 駅の設定（TrackRoutes.json）。クライアントが、実際に入った番線を見分けるのに使う
/// </summary>
public static class StopTargets
{
    public static StopTargetTable Build(AiTrains ai, StopMarkers markers, RailData rail)
    {
        var rows = new Dictionary<(Dir Dir, string Station, string Track), StopTarget>();
        StopTarget Row(Dir dir, string station, string track)
        {
            if (!rows.TryGetValue((dir, station, track), out var t))
                rows[(dir, station, track)] = t = new StopTarget { Station = station, Dir = dir == Dir.Up ? "up" : "down", Track = track };
            return t;
        }

        foreach (var s in ai.StopSignals())
        {
            var t = Row(s.Dir, s.Station, s.Track > 0 ? s.Track.ToString(CultureInfo.InvariantCulture) : "");
            t.Signal = s.Signal;
            t.Before = s.After == null ? s.Before : null;
            t.After = s.After;
            t.Limits = ai.ApproachLimits(s.Station, s.Dir, s.Track, s.Signal, s.Before, s.After).Select(l => new StopTargetLimit { Before = l.Before, Kmh = l.Kmh }).ToList();
        }
        foreach (var e in markers.Entries) Row(e.Dir, e.Station, e.Track);

        foreach (var ((dir, station, track), t) in rows)
        {
            if (markers.Find(dir, station, track.Length > 0 ? track : null) is { } e)
                t.Markers = e.Markers.Select(m => new StopTargetMarker { Cars = m.Cars, Offset = m.Offset }).ToList();
            var st = rail.Stations.Values.FirstOrDefault(x => x.Name == station);
            if (st != null && int.TryParse(track, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                if (st.TrackCircuit.TryGetValue(n, out var c)) t.Circuit = c;
                if (st.Routes[dir].Arrive.TryGetValue(n, out var arrive))
                    t.ArriveSignals = arrive.Select(r => r.Signal).Where(x => x.Length > 0).Distinct().ToList();
            }
        }

        return new StopTargetTable
        {
            Targets = rows.Values.OrderBy(t => t.Dir).ThenBy(t => t.Station, StringComparer.Ordinal).ThenBy(t => t.Track, StringComparer.Ordinal).ToList(),
        };
    }
}
