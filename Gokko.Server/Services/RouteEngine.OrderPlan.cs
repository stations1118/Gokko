namespace Gokko.Server.Services;

// ダイヤ運転中の運転順序。順序の元はダイヤグラムだけ（ダイヤの時刻・◆を横にずらした発車時刻・司令員が手で決めた出発順序）。
// ・駅を進む順に、列車ごとの「調整したダイヤ」の着・発を出し、駅の出発順序はその発の順にする
// ・ある駅で順序を入れ替えた・◆をずらした列車は、遅れた分をこの先の駅にも足すので、この先の駅の順序も同じように入れ替わる
// ・駅間では追い越せない（先に出た列車より先に着かない）。同じ番線の先の列車が出るまでは、その番線に入れない
// ・いまの遅れは順序に入れない（遅れだけでは順序を変えない。変えるのは司令員がダイヤグラムで調整したときだけ）
// 計算は Tick で、順序に関わるもの（集中扱いの指示・列車の出入り・駅を抜けた）が変わったときだけ行う
public sealed partial class RouteEngine
{
    /// <summary>続く列車の間隔（ロープレ秒）。先の列車が着いて（同じ番線なら出て）から、これだけあとでないと着かない。</summary>
    private const double OrderHeadwaySeconds = 60;

    /// <summary>
    /// 調整したダイヤの、その駅の着・発（運転日の秒）。Leader は手前から続いて着く1本前の列車、TrackLeader は同じ番線を先に使う列車。
    /// </summary>
    private sealed record OrderTimes(double Arr, double Dep, string? Leader, string? TrackLeader);

    private sealed class OrderPlan
    {
        /// <summary>駅・向き → 出発の順（列番）。</summary>
        public Dictionary<(string Station, Dir Dir), List<string>> Depart { get; } = new();
        /// <summary>列番・駅 → 調整したダイヤの着・発。</summary>
        public Dictionary<(string Train, string Station), OrderTimes> Times { get; } = new();
    }

    private OrderPlan? _orderPlan;
    private int _orderPlanKey;
    /// <summary>集中扱いの指示が変わった回数（_central.Changed）。順序を作り直すかの目安。</summary>
    private long _centralVersion;
    /// <summary>出発順序・走行順を同期で直した回数（同期は _central.Changed を出さないため）。</summary>
    private long _orderSyncVersion;

    private double ClockSpeed => _clock.Now != null && _clock.Speed > 0 ? _clock.Speed : 1;

    /// <summary>ダイヤのその駅の時刻（運転日の秒。4時前は翌日）。depart なら発（通過）、なければ着。運転停車（運停）も止まる駅として扱う。</summary>
    private double? DiaDaySec(TrainRun run, string stationId, bool depart)
    {
        var stop = run.Dia?.Stops.FirstOrDefault(x => x.StationId == stationId);
        if (stop == null) return null;
        var arr = StopArr(run.Dia!, stop); // 催物用臨停設定の臨時停車は、通過時刻の少し前に着く
        return depart ? DaySec(stop.Dep) ?? DaySec(arr) : DaySec(arr) ?? DaySec(stop.Dep);
    }

    /// <summary>
    /// 運転順序を、変わったときだけ作り直す（Tick から。_lock の中）。変わったかは、集中扱いの指示の回数・列車ごとの抜けた駅の数などで見る。
    /// </summary>
    private void RefreshOrderPlan(DateTime now)
    {
        if (!DiaMode) { _orderPlan = null; return; }
        var h = new HashCode();
        h.Add(Interlocked.Read(ref _centralVersion));
        h.Add(_orderSyncVersion);
        foreach (var r in _runs.Values)
        {
            if (!r.HasPosition || !r.Initialized || r.Dia == null) continue;
            h.Add(r.Train);
            h.Add(r.Dir);
            h.Add(r.Stations.Count);
            foreach (var s in r.Stations) { h.Add(s.Done); h.Add(s.Track); }
        }
        // ◆をずらした発車時刻（UTC）を運転日の秒に直すずれ。時計を合わせ直したら作り直す
        var speed = ClockSpeed;
        h.Add(speed);
        h.Add(Math.Floor((NowSec() - (now - DateTime.UnixEpoch).TotalSeconds * speed) / 5));
        var key = h.ToHashCode();
        if (_orderPlan != null && key == _orderPlanKey) return;
        _orderPlanKey = key;
        _orderPlan = BuildOrderPlan(now);
    }

    private OrderPlan BuildOrderPlan(DateTime now)
    {
        var plan = new OrderPlan();
        var nowSec = NowSec();
        var speed = ClockSpeed;
        double ToSec(DateTime utc) => nowSec + (utc - now).TotalSeconds * speed;

        foreach (var dir in new[] { Dir.Down, Dir.Up })
        {
            var step = dir.Step();
            var runs = _runs.Values.Where(r => r.HasPosition && r.Initialized && r.Dia != null && r.Dir == dir).ToList();
            if (runs.Count == 0) continue;
            var shift = runs.ToDictionary(r => r, _ => 0.0); // 調整で遅れた分（この先の駅にも足す）
            var departed = new Dictionary<TrainRun, long>();  // 手前の駅を出た順（小さいほど先）
            long seq = 0;

            var stations = _data.ControlStations
                .Select(s => (St: s, Z: _data.Line.ZoneOfStation(s.Id)))
                .Where(x => x.Z >= 0)
                .OrderBy(x => x.Z * step)
                .ToList();
            foreach (var (station, z) in stations)
            {
                var here = new List<(TrainRun R, StationRun S, double A, double D, int? Track, bool Ends)>();
                foreach (var r in runs)
                {
                    var s = r.Stations.FirstOrDefault(x => x.St.Id == station.Id && !x.Done);
                    if (s == null || IsCutOff(r, s)) continue;
                    if (DiaDaySec(r, station.Id, false) is not { } a || DiaDaySec(r, station.Id, true) is not { } d) continue;
                    if (d < a) d = a;
                    var track = s.Track ?? r.Dia!.Stops.FirstOrDefault(x => x.StationId == station.Id)?.Track;
                    here.Add((r, s, a, d, track, s.Terminal || IsTurnbackTrack(r, s)));
                }
                if (here.Count == 0) continue;

                // 着く順: もう着いている列車（始発・在線）→ 駅間にいる列車（近い順）→ 手前の駅を出た順
                var present = here.Where(x => x.S.IsStart || (x.R.Front - z) * step >= 0).OrderBy(x => x.D).ToList();
                // 着いている列車（始発を除く）どうしは、先に着いた順に並べ直す（始発の列車の位置はそのまま）
                var arrivedSlots = present.Select((x, i) => (x, i)).Where(p => !p.x.S.IsStart).Select(p => p.i).ToList();
                var arrivedByHere = present.Where(x => !x.S.IsStart).ToList();
                for (var i = 0; i < arrivedByHere.Count; i++) // 前を走る列車を前へ（IsAheadOf。前後が分からなければダイヤの発の順のまま）
                    for (var j = i + 1; j < arrivedByHere.Count; j++)
                        if (IsAheadOf(arrivedByHere[j].R, arrivedByHere[i].R)) (arrivedByHere[i], arrivedByHere[j]) = (arrivedByHere[j], arrivedByHere[i]);
                for (var i = 0; i < arrivedSlots.Count; i++) present[arrivedSlots[i]] = arrivedByHere[i];
                var coming = here.Except(present)
                    .OrderBy(x => departed.ContainsKey(x.R) ? 1 : 0)
                    .ThenBy(x => departed.TryGetValue(x.R, out var k) ? k : -(x.R.Front * step + x.R.Fraction))
                    .ToList();
                // 入換で番線に入る始発列車（検車区・引上線から。まだ入換を始めていない）は、入換の時刻から番線を使う。
                // それより前に着く列車は、その番線に入れる（入換の列車がもう番線をふさいでいるとは考えない）
                var shuntIn = present.Where(x => x.S.IsStart && x.S.ShuntFrom != null && x.S.Shunt != GroupState.Requested).ToList();
                present = present.Except(shuntIn).ToList();
                double ShuntAt((TrainRun R, StationRun S, double A, double D, int? Track, bool Ends) x) =>
                    x.R.Dia != null && DiaShuntTimes.TryGetValue(x.R.Dia.Number, out var st) ? Math.Min(st, x.D) : x.D - 120;
                // 番線に入る順: もう着いている列車 → 手前から着く列車（着く順）と、入換で入る列車（入換の時刻）を時刻で合わせる
                var sequence = present.Select(x => (X: x, Kind: 0)).ToList();
                var pendingShunt = new Queue<(TrainRun R, StationRun S, double A, double D, int? Track, bool Ends)>(shuntIn.OrderBy(ShuntAt));
                foreach (var c in coming)
                {
                    while (pendingShunt.Count > 0 && ShuntAt(pendingShunt.Peek()) <= c.A + shift[c.R]) sequence.Add((pendingShunt.Dequeue(), 2));
                    sequence.Add((c, 1));
                }
                while (pendingShunt.Count > 0) sequence.Add((pendingShunt.Dequeue(), 2));
                var manual = _central.Order(station.Id, dir);
                var floor = new Dictionary<TrainRun, double>(); // 手で決めた出発順序で、先の列車を待つ発
                var times = new Dictionary<TrainRun, OrderTimes>();
                List<string> order = new();
                var arrival = sequence.Select((x, i) => (x.X.R, i)).ToDictionary(p => p.R, p => p.i); // 番線に入る順

                // 手で決めた出発順序で先の列車を待つと、同じ番線の後ろの列車も遅れるので、もう一度まわす
                for (var pass = 0; pass < 3; pass++)
                {
                    times.Clear();
                    var lastOnTrack = new Dictionary<int, (string Train, double Dep)>();
                    string? leader = null;
                    double? leaderArr = null;
                    foreach (var (x, kind) in sequence)
                    {
                        var isComing = kind == 1;
                        var arr = kind == 2 ? ShuntAt(x) : x.A + shift[x.R];
                        string? trackLeader = null;
                        if (kind == 2 && x.Track is { } ts && lastOnTrack.TryGetValue(ts, out var lts))
                        {
                            arr = Math.Max(arr, lts.Dep + OrderHeadwaySeconds); // 入換は、その番線の先の列車が出てから
                            trackLeader = lts.Train;
                        }
                        if (isComing)
                        {
                            if (leaderArr is { } la) arr = Math.Max(arr, la + OrderHeadwaySeconds);
                            if (x.Track is { } t && lastOnTrack.TryGetValue(t, out var lt))
                            {
                                arr = Math.Max(arr, lt.Dep + OrderHeadwaySeconds);
                                trackLeader = lt.Train;
                            }
                        }
                        var dep = x.Ends ? arr : Math.Max(x.D + shift[x.R], arr + (x.D - x.A));
                        if (!x.Ends && _central.OpenAt(x.R.Train, station.Id) is { } open) dep = Math.Max(dep, ToSec(open));
                        if (floor.TryGetValue(x.R, out var f)) dep = Math.Max(dep, f);
                        // 前を走る列車（先に番線に入る・着いている列車。始発・終わる列車を除く）より先には出ない。
                        // 遅れで順番が入れ替わっていても、ダイヤ・司令員が決めた追い越しでなければ、追い越さない（AheadHold と同じ）
                        if (!x.Ends && kind != 2 && !x.S.IsStart)
                            foreach (var (y, ykind) in sequence)
                            {
                                if (y.R == x.R) break;
                                if (ykind == 2 || y.S.IsStart || y.Ends || !times.TryGetValue(y.R, out var yt)) continue;
                                if (!IsAheadOf(y.R, x.R)) continue;
                                if (PlanMayOvertake(x.R, y.R, station.Id, manual)) continue;
                                dep = Math.Max(dep, yt.Dep + OrderHeadwaySeconds);
                            }
                        times[x.R] = new OrderTimes(arr, dep, isComing ? leader : null, trackLeader);
                        if (x.Track is { } t2 && !x.Ends) lastOnTrack[t2] = (x.R.Train, dep); // 終わる列車がいつ番線を空けるかは分からないので数えない
                        if (isComing) (leader, leaderArr) = (x.R.Train, arr);
                    }

                    // 出発の順: 手で決めていればその順（入っていない列車は後ろに発の順）、なければ調整したダイヤの発の順
                    var byDep = here.OrderBy(x => times[x.R].Dep).ThenBy(x => x.D).ThenBy(x => x.R.Train, StringComparer.Ordinal)
                        .Select(x => x.R.Train).ToList();
                    order = manual == null ? byDep : manual.Where(byDep.Contains).Concat(byDep.Where(t => !manual.Contains(t))).ToList();
                    if (manual == null) break;
                    // 手で決めた順で、先の列車が出てから出る。ただし同じ番線で後ろにいる列車は追い越させられないので待たない（OrderHold と同じ）
                    var changed = false;
                    var fixedDeps = new List<(TrainRun R, int? Track, double Dep)>();
                    foreach (var name in order)
                    {
                        var x = here.First(y => y.R.Train == name);
                        if (x.Ends) continue;
                        var dep = times[x.R].Dep;
                        foreach (var y in fixedDeps)
                        {
                            if (x.Track != null && y.Track == x.Track && arrival[y.R] > arrival[x.R]) continue;
                            dep = Math.Max(dep, y.Dep + OrderHeadwaySeconds);
                        }
                        if (dep > times[x.R].Dep && (!floor.TryGetValue(x.R, out var f0) || f0 < dep)) { floor[x.R] = dep; changed = true; }
                        fixedDeps.Add((x.R, x.Track, dep));
                    }
                    if (!changed) break;
                }

                plan.Depart[(station.Id, dir)] = order;
                foreach (var x in here)
                {
                    var t = times[x.R];
                    if (floor.TryGetValue(x.R, out var f) && f > t.Dep) t = t with { Dep = f };
                    plan.Times[(x.R.Train, station.Id)] = t;
                    if (x.Ends) continue;
                    shift[x.R] = Math.Max(0, t.Dep - x.D);
                }
                // この先の駅に着く順は、実際に出る順（同じ番線で追い越せず、手で決めた順のとおりに出ないこともある）
                foreach (var x in here.Where(x => !x.Ends).OrderBy(x => plan.Times[(x.R.Train, station.Id)].Dep).ThenBy(x => order.IndexOf(x.R.Train)))
                    departed[x.R] = seq++;
            }
        }

        return plan;
    }

    /// <summary>運転順序の計画で、後ろを走る x が前の y を追い越してよいか（ダイヤの追い越し・司令員が決めた順序・y の◆をずらした）。</summary>
    private bool PlanMayOvertake(TrainRun x, TrainRun y, string stationId, IReadOnlyList<string>? manual)
    {
        if (DiaOvertakes(x, y, stationId) || _central.OpenAt(y.Train, stationId) != null) return true;
        if (manual == null) return false;
        var list = manual.ToList();
        var (xi, yi) = (list.IndexOf(x.Train), list.IndexOf(y.Train));
        return xi >= 0 && yi >= 0 && xi < yi;
    }

    /// <summary>
    /// ダイヤグラムの見込みの線で、先の列車に詰まる分だけ着を遅らせる（駅間で追いつかない・同じ番線は先の列車が出てから）。
    /// prev は先の列車の見込み（前の回の計算）。ダイヤ運転でなければ null。
    /// </summary>
    private DateTime? MinArrive(Dictionary<TrainRun, PlanResult> prev, TrainRun r, int z)
    {
        if (!DiaMode || _orderPlan is not { } op || _data.Line.StationAtZone(z) is not { } sid) return null;
        if (!op.Times.TryGetValue((r.Train, sid), out var ot)) return null;
        var gap = TimeSpan.FromSeconds(OrderHeadwaySeconds / ClockSpeed);
        DateTime? m = null;
        if (ot.Leader is { } l && RunByTrain(l) is { } p && prev.TryGetValue(p, out var pp) && pp.ArrAt.TryGetValue(z, out var la))
            m = la + gap;
        if (ot.TrackLeader is { } tl && RunByTrain(tl) is { } q && prev.TryGetValue(q, out var qp) && qp.DepAt.TryGetValue(z, out var ld)
            && (m == null || ld + gap > m))
            m = ld + gap;
        return m;
    }
}
