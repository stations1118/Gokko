namespace Gokko.Server.Services;

// 予告（在線位置の画面の駅の到着・出発の表、乗務タブレットの接近案内）の並び。
// ・ダイヤ運転中は、終点で折り返して次になる列車（後運用）を、折り返す前から「これから走る列車」として出す
// ・出発の順: もう出発進路を取った列車 → 司令員が決めた出発順序 → 抑止中は後ろ → ダイヤの発車時刻（ダイヤ運転でなければ見込み）
// ・到着の順: もう場内を取った列車 → 手前の駅を出た列車（近い順）と、手前の駅の出発の順をそのまま受け継いだ列車を、着く見込みで合わせる。
//   手前の駅の出発順序を司令員が変えれば（遅れた列車を津崎で待たせるなど）、先の駅の到着の順もそれに合わせて変わる
public sealed partial class RouteEngine
{
    /// <summary>折り返して次になる列車（まだ走っていない）。From は折り返す前の列車、StartId は折り返す駅。</summary>
    private sealed record PlannedTrain(string Train, DiaTrain Dia, Dir Dir, TrainRun From, string StartId);

    /// <summary>駅・向きごとの予告の並び（列番 → 順位。小さいほど先）。</summary>
    private sealed class ForecastRank
    {
        public Dictionary<string, int> Depart { get; } = new();
        public Dictionary<string, int> Arrive { get; } = new();
    }

    /// <summary>
    /// ダイヤ運転中、走っている列車の後運用で、まだ走っていない列車（後運用の差し替えも反映）。ダイヤ運転でなければ空。
    /// 打ち切りなどで、時刻表の終点まで行かない列車の後運用は出さない（どこで折り返すか分からないため）。
    /// </summary>
    private List<PlannedTrain> PlannedTrains()
    {
        var list = new List<PlannedTrain>();
        if (!DiaMode) return list;
        var running = _runs.Values.Select(r => r.Train).ToHashSet();
        foreach (var r in _runs.Values.Where(r => r.HasPosition && r.Dia != null))
        {
            if (_data.Turns.TurnOf(r.Dia!) is not { Kind: "折返", Number: { } number }) continue;
            if (r.Dia!.TerminalId is not { } turn || running.Contains(number)) continue;
            if (PlanEnd(r) is { } end && end != _data.Line.ZoneOfStation(turn)) continue;
            if (r.Stations.FirstOrDefault(s => s.St.Id == turn) is not { } ts || IsCutOff(r, ts)) continue;
            if (_data.DiaOf(number) is not { } dia || TrainInfo(number) is not { } info) continue;
            if (list.Any(p => p.Train == number)) continue;
            list.Add(new PlannedTrain(number, dia, info.Dir, r, turn));
        }

        return list;
    }

    /// <summary>後運用の列車の、その駅の手前の進路を扱う駅（ダイヤの並びで、その駅の1つ前）。なければ null。</summary>
    private string? PlannedPrevious(PlannedTrain p, string stationId)
    {
        string? prev = null;
        foreach (var s in p.Dia.Stops)
        {
            if (s.StationId == null || !_data.Stations.TryGetValue(s.StationId, out var def) || !def.RouteControl) continue;
            if (s.StationId == stationId) return prev;
            prev = s.StationId;
        }
        return null;
    }

    /// <summary>後運用の列車が、その駅を通る（着く・出る）か。</summary>
    private static bool PlannedPasses(PlannedTrain p, string stationId) =>
        p.Dia.Stops.Any(s => s.StationId == stationId && s.Stop != "運停");

    private static double? PlannedDiaSec(PlannedTrain p, string stationId, bool depart)
    {
        var stop = p.Dia.Stops.FirstOrDefault(x => x.StationId == stationId);
        if (stop == null) return null;
        return depart ? ParseDiaTime(stop.Dep) ?? ParseDiaTime(stop.Arr) : ParseDiaTime(stop.Arr) ?? ParseDiaTime(stop.Dep);
    }

    /// <summary>
    /// 駅・向きごとの予告の並び（出発・到着）。走っている列車と、後運用の列車（planned）の両方に順位を付ける。
    /// 出発の順は駅ごとに決まり、到着の順は手前の駅の出発の順から決まるので、先に全部の駅の出発の順を出してから到着の順を出す。
    /// </summary>
    private Dictionary<(string Station, Dir Dir), ForecastRank> ForecastRanks(List<PlannedTrain> planned)
    {
        var nowSec = NowSec();
        var result = new Dictionary<(string, Dir), ForecastRank>();
        var stations = _data.ControlStations.ToList();

        double Eta(TrainRun r, StationRun s, bool dep)
        {
            double? sec = s.IsStart ? 0 : _model.SecondsTo(RunType(r), r.Dir, r.Front, r.Fraction, s.St.Id);
            if (sec != null && dep) sec += DwellSeconds(r, s);
            return sec is { } v ? nowSec + v : double.MaxValue;
        }
        long Taken(TrainRun r, StationRun s, bool dep) =>
            r.Requests.Where(q => q.Station == s && q.Departure == dep && q.CancelledUtc == null).Select(q => q.Id).DefaultIfEmpty(long.MaxValue).Min();
        List<(TrainRun Run, StationRun St)> Here(string stationId, Dir dir) => _runs.Values
            .Where(r => r.HasPosition && r.Dir == dir)
            .Select(r => (Run: r, St: r.Stations.FirstOrDefault(s => s.St.Id == stationId && !s.Done)))
            .Where(x => x.St != null && !IsCutOff(x.Run, x.St))
            .Select(x => (x.Run, x.St!))
            .ToList();

        // ---- 出発の順 ----
        foreach (var station in stations)
        foreach (var dir in new[] { Dir.Down, Dir.Up })
        {
            var rank = new ForecastRank();
            result[(station.Id, dir)] = rank;
            var manual = _central.Order(station.Id, dir)?.ToList();
            int Index(string train) => manual != null && manual.IndexOf(train) is var i and >= 0 ? i : int.MaxValue;
            var items = new List<(string Train, int Req, long Taken, int Index, bool Held, double T)>();
            foreach (var (r, st) in Here(station.Id, dir))
            {
                var req = st.Depart == GroupState.Requested;
                var t = DiaMode && DiaSec(r, station.Id, depart: true) is { } d ? d : Eta(r, st, true);
                items.Add((r.Train, req ? 0 : 1, req ? Taken(r, st, true) : 0, Index(r.Train),
                    (_central.Hold(r.Train, station.Id) & HoldKind.Depart) != 0, t));
            }
            foreach (var p in planned.Where(p => p.Dir == dir && PlannedPasses(p, station.Id)))
                items.Add((p.Train, 1, 0, Index(p.Train), (_central.Hold(p.Train, station.Id) & HoldKind.Depart) != 0,
                    PlannedDiaSec(p, station.Id, depart: true) ?? double.MaxValue));
            var i = 0;
            foreach (var x in items.OrderBy(x => x.Req).ThenBy(x => x.Taken).ThenBy(x => x.Index).ThenBy(x => x.Held).ThenBy(x => x.T))
                rank.Depart.TryAdd(x.Train, i++);
        }

        // ---- 到着の順 ----
        foreach (var station in stations)
        foreach (var dir in new[] { Dir.Down, Dir.Up })
        {
            var rank = result[(station.Id, dir)];
            var requested = new List<(string Train, long Taken)>();
            var between = new List<(string Train, double T)>();                    // 手前の駅を出た列車・手前の駅がない列車
            var fromPrev = new Dictionary<string, List<(string Train, int Rank, double T)>>(); // 手前の駅 → まだそこを出ていない列車

            foreach (var (r, st) in Here(station.Id, dir))
            {
                if (st.Arrive == GroupState.Requested) { requested.Add((r.Train, Taken(r, st, false))); continue; }
                var t = Eta(r, st, false);
                if (DiaMode && DiaSec(r, station.Id, depart: false) is { } da) t = t == double.MaxValue ? da : Math.Max(t, da);
                var idx = r.Stations.IndexOf(st);
                var prev = idx > 0 ? r.Stations[idx - 1] : null;
                if (prev != null && !prev.Done && result.TryGetValue((prev.St.Id, dir), out var pr) && pr.Depart.TryGetValue(r.Train, out var pd))
                    (fromPrev.TryGetValue(prev.St.Id, out var l) ? l : fromPrev[prev.St.Id] = new()).Add((r.Train, pd, t));
                else between.Add((r.Train, t));
            }
            foreach (var p in planned.Where(p => p.Dir == dir && p.StartId != station.Id && PlannedPasses(p, station.Id)))
            {
                var t = Math.Max(nowSec, PlannedDiaSec(p, station.Id, depart: false) ?? double.MaxValue);
                if (PlannedPrevious(p, station.Id) is { } prev && result.TryGetValue((prev, dir), out var pr) && pr.Depart.TryGetValue(p.Train, out var pd))
                    (fromPrev.TryGetValue(prev, out var l) ? l : fromPrev[prev] = new()).Add((p.Train, pd, t));
                else between.Add((p.Train, t));
            }

            // 並びを変えられない列（手前の駅の出発の順・駅間は近い順）ごとに、見込みの時刻を前の列車より早くならないようにして、時刻で合わせる
            var queues = new List<Queue<(string Train, double T)>>();
            if (between.Count > 0) queues.Add(new Queue<(string, double)>(between.OrderBy(x => x.T)));
            foreach (var l in fromPrev.Values)
            {
                var q = new Queue<(string, double)>();
                var last = double.MinValue;
                foreach (var x in l.OrderBy(x => x.Rank)) q.Enqueue((x.Train, last = Math.Max(last, x.T)));
                queues.Add(q);
            }

            var i = 0;
            foreach (var x in requested.OrderBy(x => x.Taken)) rank.Arrive.TryAdd(x.Train, i++);
            while (queues.Any(q => q.Count > 0))
            {
                var q = queues.Where(q => q.Count > 0).MinBy(q => q.Peek().T)!;
                rank.Arrive.TryAdd(q.Dequeue().Train, i++);
            }
        }

        return result;
    }
}
