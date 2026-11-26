using System.Text;
using System.Text.RegularExpressions;

namespace Gokko.Server.Services;

/// <summary>
/// 特発臨（運転整理のために、司令員がその場で立てる臨時列車）。司令用管理画面で立てる。
///
/// ・走っている列車（自動運転の列車でもユーザーの列車でも。止まっていても走っていてもよい）を選び、止まっている駅か、これから通る駅を発駅にして、
///   スジ（列番・種別・停車駅・時刻）を立てる。走っている列車は、発駅に着いて止まった番線から出る
///   時刻は、標準運転時分（RunTimes.json）から引いた案を、司令員が直してから立てる
/// ・立てたスジは、所定のダイヤに重ねる（<see cref="RailData.SetExtraTrains"/>、出どころ「特発臨」）。自動進路構成・運転順序・ダイヤグラム・
///   自動運転の列車からは、ふつうのダイヤの列車に見える
/// ・元の列車は、その駅で打ち切りにする（列車別設定の「打ち切り」と同じ指示）。元の列車の後運用は、時刻表どおりに残す。
///   「元の列車の後運用につなぐ」を選んだときは、後運用の差し替え（<see cref="TurnChanges"/>）で特発臨の終点からつなぐ
/// ・自動運転の列車は、その駅に止まったら特発臨の列番に変わって走る（<see cref="AiTrains"/>）。ユーザーの列車は、タブレットに特発臨が出るので、
///   運転士が列番を入れ直す
/// ・取り消せる。走り出す前なら、元の列車の打ち切りも外す。走り出したあとに取り消すと、自動運転の列車は消える
/// ・残し方は後運用の差し替えと同じ: メモリにだけ持ち、サーバーを再起動したとき・ロープレ時刻を設定したときに消える
/// ・元の列車を選ぶ代わりに、車庫（時刻表で出庫の列車が出ている所。江原検など）から出庫させることもできる。両数を選ぶ。
///   自動運転の列車は、ふつうの出庫の列車と同じく、発車時刻の少し前に車庫の番線に出る。元の列車から立てたときは、元の列車の両数を引き継ぐ
/// </summary>
public sealed class TokuhatsuRinji(RailData rail, CentralControl central, PlayerRegistry players, GameClock clock)
{
    public const string Source = "特発臨";

    /// <summary>種別（列番の読み方と合わせる。<see cref="RouteEngine.TrainInfo"/>）。</summary>
    public static readonly string[] Types = ["普通", "準急", "区急", "急行", "快急", "特急", "回送"];

    private readonly object _lock = new();
    private readonly List<Entry> _entries = new();

    private sealed class Entry
    {
        public required DiaTrain Dia { get; init; }
        /// <summary>解放: 元の列車から切り離す「解＋元の列番」の列車（発駅だけの短い列車。特発臨に折り返す）。解放でなければ null。</summary>
        public DiaTrain? SplitDia { get; init; }
        /// <summary>解放: 切り離す元の列車の列番。</summary>
        public string? SplitFrom { get; init; }
        /// <summary>連結: 終点で連結する列車の列番（特発臨の列番は「連」＋この列番）。</summary>
        public string? Couple { get; init; }
        /// <summary>元の列車の列番（出庫なら null）。</summary>
        public string? From { get; init; }
        /// <summary>出庫させた車庫（江原検 など）。元の列車から立てたときは null。</summary>
        public string? Depot { get; init; }
        /// <summary>元の列車の発駅の番線を、特発臨の向きへ出られる番線に指定した（取消で外す）。</summary>
        public bool TrackSet { get; set; }
        public required string StationId { get; init; }
        /// <summary>発駅の番線。走っている列車から立てたときは、着いた番線（着くまでは null）。</summary>
        public int? Track { get; set; }
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
        /// <summary>特発臨の列番の列車が出てきた（自動運転の列車が列番を変えた・運転士が列番を入れ直した）。</summary>
        public bool Started { get; set; }
        /// <summary>元の列車の後運用を、特発臨の終点からつないだ。</summary>
        public bool TurnLinked { get; set; }
    }

    public sealed record StopSpec(string St, string? Stop, string? Arr, string? Dep, int? Track);

    /// <summary>立てる・案を引くときの入力。Stops があれば、それをそのまま立てる。なければ Dest・Dep・StopAt から案を引く。</summary>
    /// Station は発駅（元の列車が止まっている駅か、これから通る駅）。なければ、止まっている駅か、次の駅。
    /// Depot は出庫させる車庫（江原検 など。Train の代わり）、Cars は出庫・解放の両数。
    /// Split は解放する元の列車（Train の代わり。Station で切り離す）、Couple は終点で連結する列車（列番は「連」＋その列番、種別は回送になる）。
    public sealed record Request(string? Train, string? Station, string? Number, string? Type, string? Dest, string? Dep, List<string>? StopAt, List<StopSpec>? Stops, bool LinkTurn,
        string? Depot = null, int? Cars = null, string? Split = null, string? Couple = null);

    /// <summary>
    /// 発駅。Train は元の列車（出庫・解放なら空）、Track は止まっている番線（走っている列車・出庫は null）、Depot は出庫の車庫、
    /// SplitFrom は解放する元の列車（Track はその列車が発駅に着く番線）。
    /// </summary>
    private sealed record From(string Train, StationDef Station, int? Track, string? Depot, string? SplitFrom = null);

    /// <summary>自動運転の列車の両数（列番 → 両数。走っていなければ null）。Program.cs で AiTrains につなぐ。</summary>
    public Func<string, int?>? AiCars { get; set; }

    public event Action<string>? Changed;

    /// <summary>取り消した特発臨のうち、もう走り出していた列番（自動運転の列車を消すため）。</summary>
    public event Action<IReadOnlyCollection<string>>? Removed;

    // =========================================================================================
    // 自動運転の列車・タブレットから
    // =========================================================================================

    /// <summary>その列車に、まだ走り出していない特発臨があれば、(特発臨の列番, 駅ID, 番線（決まっていなければ null）)。</summary>
    public (string Number, string StationId, int? Track)? PendingFor(string train)
    {
        var key = RailData.NormalizeTrain(train);
        lock (_lock)
            return _entries.FirstOrDefault(e => !e.Started && e.From == key) is { } e ? (e.Dia.Number, e.StationId, e.Track) : null;
    }

    /// <summary>特発臨の列番か。</summary>
    public bool IsRinji(string train)
    {
        var key = RailData.NormalizeTrain(train);
        lock (_lock) return _entries.Any(e => e.Dia.Number == key);
    }

    /// <summary>
    /// ほかの列車から変わって走る特発臨か（自動運転の列車を時刻表から出すときは出さない）。元の列車が列番を変える特発臨と、
    /// 解放（元の列車から切り離した車両が折り返して走る）が true。出庫の特発臨は false（ふつうの出庫と同じく出す）。
    /// </summary>
    public bool IsConversion(string train)
    {
        var key = RailData.NormalizeTrain(train);
        lock (_lock) return _entries.Any(e => e.Dia.Number == key && (e.From != null || e.SplitFrom != null));
    }

    /// <summary>特発臨の列番の列車が出てきた（track は、自動運転の列車が列番を変えた番線）。</summary>
    public void MarkStarted(string number, int? track = null)
    {
        var key = RailData.NormalizeTrain(number);
        lock (_lock)
            foreach (var e in _entries.Where(e => e.Dia.Number == key))
            {
                e.Started = true;
                e.Track ??= track;
            }
    }

    /// <summary>タブレットに出す特発臨（その列車がまだ走り出していない特発臨の元の列車なら）。</summary>
    public object? TabletNotice(string train)
    {
        var key = RailData.NormalizeTrain(train);
        lock (_lock)
        {
            if (_entries.FirstOrDefault(e => !e.Started && e.From == key) is not { } e) return null;
            var first = e.Dia.Stops[0];
            var last = e.Dia.Stops[^1];
            return new
            {
                number = e.Dia.Number, type = e.Dia.Type, from = e.From, station = first.Name, track = e.Track,
                dep = first.Dep, dest = last.Name, arr = last.Arr ?? last.Dep
            };
        }
    }

    // =========================================================================================
    // 司令用管理画面
    // =========================================================================================

    /// <summary>
    /// 特発臨を立てられる列車（走っている列車すべて）と、発駅にできる駅: 止まっている駅（here）と、これから通る進路を扱う駅（ahead、近い順）。
    /// </summary>
    public List<object> Candidates()
    {
        var list = new List<(string Key, object Row)>();
        foreach (var p in players.Snapshot())
        {
            if (p.TrainNumber.Length == 0 || p.ConnectionId.StartsWith(PlayerRegistry.GhostPrefix)) continue;
            var number = RailData.NormalizeTrain(p.TrainNumber);
            var (here, ahead) = Stations(p, number);
            if (here == null && ahead.Count == 0) continue;
            var dia = rail.DiaOf(number);
            list.Add((number, new
            {
                train = number,
                here = here is { } h ? new { id = h.Station.Id, name = h.Station.Name, track = h.Track } : null,
                ahead = ahead.Select(s => new { id = s.Id, name = s.Name }),
                where = rail.Line.DescribeZones(p.Zones),
                ai = p.ConnectionId.StartsWith(PlayerRegistry.AiPrefix),
                speed = Math.Round(p.Speed),
                dir = RouteEngine.TrainInfo(number)?.Dir == Dir.Down ? "下り" : "上り",
                type = dia?.Type ?? RouteEngine.TrainInfo(number)?.Type,
                dest = dia?.Dest,
                rinji = PendingFor(number)?.Number,
                // 解放できる駅: この先の駅のうち、時刻表で停車して番線が決まっている駅（もう「解」の列車がある列車は切り離せない）
                split = dia == null || rail.DiaOf("解" + number) != null ? []
                    : ahead.Where(s => dia.Stations.TryGetValue(s.Id, out var ds) && ds.StopType == "停車" && ds.Track != null)
                        .Select(s => new { id = s.Id, name = s.Name, track = dia.Stations[s.Id].Track }).ToList()
            }));
        }
        return list.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Row).ToList();
    }

    /// <summary>
    /// 発駅にできる駅: 番線に止まっている駅（here）と、この先の進路を扱う駅（ahead、近い順。いま番線にいる駅は入れない）。
    /// 番線にいても走っている（出ていくところ・入ってくるところ）ときは、その駅は here にしない。
    /// </summary>
    private ((StationDef Station, int Track)? Here, List<StationDef> Ahead) Stations(PlayerInfo p, string number)
    {
        var w = Where(p);
        var dir = RouteEngine.TrainInfo(number)?.Dir ?? (p.Direction < 0 ? Dir.Up : Dir.Down);
        (StationDef Station, int Track)? here = w is { } ww && p.Speed <= 1 ? ww : null;
        var ahead = new List<StationDef>();
        if (p.Zones.Count > 0)
        {
            var front = dir == Dir.Down ? p.Zones.Max() : p.Zones.Min();
            ahead = rail.ControlStations
                .Select(s => (s, z: rail.Line.ZoneOfStation(s.Id)))
                .Where(x => x.z >= 0 && (x.z - front) * dir.Step() > 0 && x.s.Id != w?.Station.Id)
                .OrderBy(x => (x.z - front) * dir.Step()).Select(x => x.s).ToList();
        }
        return (here, ahead);
    }

    /// <summary>列車がいる駅・番線（先頭がいる番線の回路で見る）。</summary>
    private (StationDef Station, int Track)? Where(PlayerInfo p)
    {
        foreach (var st in rail.ControlStations)
        foreach (var (track, circuit) in st.TrackCircuit)
            if (p.TrackCircuits.Contains(circuit)) return (st, track);
        return null;
    }

    /// <summary>立てた特発臨の一覧。</summary>
    public List<object> View()
    {
        RefreshStarted();
        lock (_lock)
            return _entries.Select(e => (object)new
            {
                number = e.Dia.Number,
                type = e.Dia.Type,
                from = e.From ?? (e.SplitFrom != null ? $"解放（{e.SplitFrom} から）" : $"出庫（{e.Depot}）"),
                depot = e.Depot,
                split = e.SplitFrom,
                couple = e.Couple,
                cars = e.Dia.Cars,
                station = e.Depot ?? rail.Line.StationName(e.StationId),
                track = e.Track,
                dest = e.Dia.Dest,
                dep = e.Dia.Stops[0].Dep,
                arr = e.Dia.Stops[^1].Arr ?? e.Dia.Stops[^1].Dep,
                started = e.Started,
                turn = rail.Turns.TurnOf(e.Dia)?.Number,
                stops = e.Dia.Stops.Select(s => new { name = s.Name, stop = s.Stop, arr = s.Arr, dep = s.Dep, track = s.Track })
            }).ToList();
    }

    // 特発臨の列番の列車が出ていれば、走り出した印を付ける
    private void RefreshStarted()
    {
        var running = players.Snapshot().Select(p => RailData.NormalizeTrain(p.TrainNumber)).ToHashSet();
        lock (_lock)
            foreach (var e in _entries.Where(e => !e.Started && (running.Contains(e.Dia.Number) || e.SplitDia != null && running.Contains(e.SplitDia.Number)))) e.Started = true;
    }

    /// <summary>
    /// スジの案を引く: 元の列車のいる駅・番線から行先まで、路線の駅を並べ、標準運転時分で時刻を入れる。
    /// 停車駅は StopAt（なければ種別がふだん止まる駅）。発車時刻は Dep（なければ今のロープレ時刻の3分後）。
    /// </summary>
    public (object? Plan, string? Error) Draft(Request r)
    {
        var (f, error) = Origin(r);
        if (f == null) return (null, error);
        var type = (r.Type ?? "").Trim();
        if (!Types.Contains(type)) return (null, "種別を選んでください");
        var destZone = r.Dest is { Length: > 0 } dz ? rail.Line.ZoneOfStation(dz) : -1;
        var hereZone = rail.Line.ZoneOfStation(f.Station.Id);
        if (destZone < 0) return (null, "行先の駅を選んでください");
        if (destZone == hereZone) return (null, "行先が発駅と同じです");
        var dir = destZone > hereZone ? Dir.Down : Dir.Up;
        var (startTrack, de, _) = StartTrack(f, dir);
        if (de != null) return (null, de);

        double dep;
        if (r.Dep is { Length: > 0 } dt)
        {
            if (Sec(dt) is not { } d) return (null, "発車時刻の形が違います（例 14:20 か 14:20:30）");
            dep = d;
        }
        else
        {
            if (clock.Now is not { } now) return (null, "ロープレ時刻が設定されていません（発車時刻を入れてください）");
            dep = Math.Ceiling((now.TotalSeconds + 180) / 30) * 30;
            // 解放: 元の列車がその駅に着く時刻の2分後より前にはしない（切り離してから出る）
            if (f.SplitFrom != null && rail.DiaOf(f.SplitFrom)?.Stops.FirstOrDefault(s => s.StationId == f.Station.Id) is { } ss && Sec(ss.Arr ?? ss.Dep) is { } st0)
            {
                var due = Math.Ceiling((st0 + 120) / 30) * 30;
                if (due - dep is > 0 and < 6 * 3600) dep = due;
            }
            // まだ発駅に着いていなければ、元の列車の時刻表のその駅の時刻の1分後より前にはしない
            else if (f.Track == null && f.Depot == null && rail.DiaOf(f.Train)?.Stops.FirstOrDefault(s => s.StationId == f.Station.Id) is { } os && Sec(os.Arr ?? os.Dep) is { } ot)
            {
                var due = Math.Ceiling((ot + 60) / 30) * 30;
                if (due - dep is > 0 and < 6 * 3600) dep = due;
            }
        }

        // 行先までの路線の駅（ゾーンの順）
        var ids = rail.Line.StationIds.Where(id => rail.Line.ZoneOfStation(id) is var z && z >= 0 && z >= Math.Min(hereZone, destZone) && z <= Math.Max(hereZone, destZone))
            .OrderBy(id => rail.Line.ZoneOfStation(id) * dir.Step()).ToList();
        var stopAt = r.StopAt?.ToHashSet() ?? UsualStops(type);
        var stops = new List<StopSpec>();
        var t = dep;
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            var first = i == 0;
            var last = i == ids.Count - 1;
            var stop = first || last || stopAt.Contains(id);
            if (first)
            {
                stops.Add(new StopSpec(id, "停", null, Hms(t), startTrack));
                continue;
            }

            var prevStops = stops[^1].Stop == "停";
            t += RunSeconds(type, dir, ids[i - 1], id, prevStops, stop);
            var arr = Math.Ceiling(t / 15) * 15;
            if (last) stops.Add(new StopSpec(id, "停", Hms(arr), null, null));
            else if (stop)
            {
                t = Math.Ceiling((arr + Math.Max(rail.DefaultDwellSeconds, 20)) / 15) * 15;
                stops.Add(new StopSpec(id, "停", Hms(arr), Hms(t), null));
            }
            else
            {
                t = arr;
                stops.Add(new StopSpec(id, "通", null, Hms(arr), null));
            }
        }

        return (new
        {
            train = f.Train,
            depot = f.Depot,
            split = f.SplitFrom,
            station = f.Station.Id,
            stationName = f.Depot ?? f.Station.Name,
            track = startTrack,
            arriving = f.Track == null && f.Depot == null && f.SplitFrom == null, // まだ発駅に着いていない（その番線に入れる）
            cars = f.Depot != null ? r.Cars ?? 6 : f.SplitFrom != null ? r.Cars ?? 2 : CarsOf(f.Train),
            splitCars = f.SplitFrom != null ? CarsOf(f.SplitFrom) : null, // 解放する元の列車の両数
            couple = CoupleCandidates(stops[^1].St, Sec(stops[^1].Arr) ?? t, dir),
            dir = dir.Label(),
            type,
            stops = stops.Select((s, i) => new
            {
                st = s.St, name = i == 0 && f.Depot != null ? f.Depot : rail.Line.StationName(s.St), stop = s.Stop, arr = s.Arr, dep = s.Dep, track = s.Track,
                control = rail.Stations.TryGetValue(s.St, out var sd) && sd.RouteControl
            })
        }, null);
    }

    /// <summary>特発臨を立てる。うまくいかなければ理由を返す。</summary>
    public string? Create(Request r)
    {
        var (f, error) = Origin(r);
        if (f == null) return error;
        // 連結: 増結車の決まり（時刻表の 連777 と同じ）に合わせ、列番は「連」＋連結する列車の列番、種別は回送
        var couple = r.Couple is { Length: > 0 } cp ? RailData.NormalizeTrain(cp) : null;
        var number = couple != null ? "連" + couple : RailData.NormalizeTrain(r.Number);
        var type = couple != null ? "回送" : (r.Type ?? "").Trim();
        if (number.Length == 0) return "列番を入れてください";
        if (!Types.Contains(type)) return "種別を選んでください";
        if (rail.DiaOf(number) != null) return $"{number} はもう時刻表にある列番です";
        if (RouteEngine.TrainInfo(number) is not { } info) return $"{number}: 列番に数字がありません";
        if (info.Type != type)
            return type == "回送" ? $"{number}: 回送の列番は「回」か「臨回」で始めてください"
                : type == "普通" ? $"{number}: 普通の列番は、数字のあとに種別の文字（A・B・C・D・K）を付けないでください"
                : $"{number}: {type}の列番は、末尾を {Suffix(type)} にしてください";

        var stops = r.Stops ?? new();
        if (stops.Count < 2) return "駅を2つ以上入れてください";
        if (stops[0].St != f.Station.Id) return $"発駅が {f.Station.Name} になっていません";
        var zones = stops.Select(s => rail.Line.ZoneOfStation(s.St)).ToList();
        if (zones.Any(z => z < 0)) return "路線にない駅があります";
        for (var i = 1; i < zones.Count; i++)
            if ((zones[i] - zones[i - 1]) * info.Dir.Step() <= 0)
                return $"{number} は列番の数字の最後が{(info.Dir == Dir.Down ? "奇数（下り）" : "偶数（上り）")}ですが、駅の並びが{info.Dir.Label()}になっていません";
        var (startTrack, de, setTrack) = StartTrack(f, info.Dir);
        if (de != null) return de;
        int? cars;
        if (f.Depot != null || f.SplitFrom != null)
        {
            cars = r.Cars ?? (f.Depot != null ? 6 : 2);
            if (cars is < 1 or > 10) return "両数は1〜10両にしてください";
            if (f.SplitFrom != null && CarsOf(f.SplitFrom) is { } pc && cars >= pc)
                return $"切り離す両数（{cars}両）が、{f.SplitFrom} の両数（{pc}両）以上です";
        }
        else cars = CarsOf(f.Train);
        var splitNumber = f.SplitFrom != null ? "解" + f.SplitFrom : null;
        if (splitNumber != null && rail.DiaOf(splitNumber) != null) return $"{f.SplitFrom} には、もう切り離す列車（{splitNumber}）があります";

        // 時刻: 発駅は発、終点は着、途中の停車駅は着・発、通過駅は通過の時刻（発の欄）。前の駅より前にならないこと
        var dia = new DiaTrain { Number = number, Type = type, TypeLabel = type, Dest = rail.Line.StationName(stops[^1].St), Cars = cars };
        double? prev = null;
        for (var i = 0; i < stops.Count; i++)
        {
            var s = stops[i];
            var first = i == 0;
            var last = i == stops.Count - 1;
            var name = first && f.Depot != null ? f.Depot : rail.Line.StationName(s.St);
            var stop = first || last || s.Stop == "停" ? "停" : "通";
            var arr = first || stop == "通" ? null : NormTime(s.Arr);
            var dep = last ? null : NormTime(s.Dep);
            if (!first && stop == "停" && s.Arr is { Length: > 0 } && arr == null || !last && s.Dep is { Length: > 0 } && dep == null)
                return $"{name}: 時刻の形が違います（例 14:20 か 14:20:30）";
            if (first && dep == null) return $"{name}: 発車時刻を入れてください";
            if (last && arr == null) return $"{name}: 着く時刻を入れてください";
            if (!first && !last && dep == null) return $"{name}: {(stop == "停" ? "発" : "通過")}の時刻を入れてください";
            foreach (var v in new[] { Sec(arr), Sec(dep) })
            {
                if (v == null) continue;
                var vv = prev is { } p0 && v < p0 - 12 * 3600 ? v + 86400 : v; // 0時をまたぐ
                if (prev != null && vv < prev) return $"{name}: 前の駅より早い時刻になっています";
                prev = vv;
            }

            var track = first ? startTrack : s.Track;
            if (track is { } tk && rail.Stations.TryGetValue(s.St, out var sd) && sd.RouteControl && !sd.TrackCircuit.ContainsKey(tk))
                return $"{name}: {tk}番線はありません";
            dia.Stops.Add(new DiaStop(name, s.St, stop, arr, dep, track));
            dia.Stations[s.St] = (stop == "停" ? "停車" : "通過", track);
        }

        var lastId = stops[^1].St;
        if (rail.Stations.TryGetValue(lastId, out var ts) && ts.RouteControl) dia.TerminalId = lastId;
        dia.Unyo = f.Depot == null && f.SplitFrom == null && rail.DiaOf(f.Train) is { } od ? rail.Turns.UnyoOf(od) : null;

        // 連結: 終点で、あとから着く列車に連結する（増結車が先に番線に入って待つ）。終点の番線は、連結する列車が着く番線
        string? coupleNote = null;
        if (couple != null)
        {
            var endName = rail.Line.StationName(lastId);
            if (rail.DiaOf(couple) is not { } pd) return $"連結する列車 {couple} は時刻表にありません";
            if (pd.Stops.FirstOrDefault(s => s.StationId == lastId) is not { Stop: "停" } ps) return $"{couple} は{endName}に停車しません";
            if (ps.Track is not { } ptk) return $"{couple} が{endName}で着く番線が、時刻表にありません";
            if (RouteEngine.TrainInfo(couple) is not { } pinfo || !RouteEngine.CanCoupleAt(rail, lastId, pinfo.Dir, ptk))
                return $"{endName} {ptk}番線は、{couple} が増結車のいる番線へ入る入換信号機がないので、連結できません";
            if (!rail.Stations[lastId].Routes[info.Dir].Arrive.ContainsKey(ptk))
                return $"{endName} {ptk}番線（{couple} が着く番線）には、{info.Dir.Label()}の特発臨は入れません";
            if (Sec(dia.Stops[^1].Arr) is { } ra && Sec(ps.Arr ?? ps.Dep) is { } pa && (pa - ra + 86400) % 86400 > 12 * 3600)
                return $"{couple} が{endName}に着く（{ps.Arr ?? ps.Dep}）のは、増結車が着くより前です（増結車が先に入って待ちます）";
            var last = dia.Stops[^1];
            dia.Stops[^1] = last with { Track = ptk };
            dia.Stations[lastId] = ("停車", ptk);
            dia.Turn = new DiaTurn("併結", couple, ps.Arr ?? ps.Dep, null);
            coupleNote = $"・{endName} {ptk}番線で {couple} に連結";
        }

        // 解放: 元の列車が発駅に着いたら「解」＋元の列番の車両を切り離し、その車両が特発臨に折り返して出る（時刻表の 解874 → 回933 と同じ）
        DiaTrain? splitDia = null;
        if (splitNumber != null)
        {
            var first = dia.Stops[0];
            splitDia = new DiaTrain { Number = splitNumber, Type = "回送", TypeLabel = "回送", Dest = first.Name, Cars = cars, TerminalId = f.Station.Id };
            splitDia.Stops.Add(new DiaStop(first.Name, f.Station.Id, "停", null, first.Dep, startTrack));
            splitDia.Stations[f.Station.Id] = ("停車", startTrack);
            splitDia.Turn = new DiaTurn("折返", number, first.Dep, "発");
        }

        lock (_lock)
        {
            if (_entries.Any(e => e.Dia.Number == number)) return $"{number} はもう特発臨で立てています";
            if (f.Depot == null && f.SplitFrom == null && _entries.Any(e => !e.Started && e.From == f.Train)) return $"{f.Train} には、まだ走り出していない特発臨があります（先に取り消してください）";
            _entries.Add(new Entry
            {
                Dia = dia, From = f.Depot == null && f.SplitFrom == null ? f.Train : null, Depot = f.Depot, StationId = f.Station.Id,
                Track = startTrack, TrackSet = setTrack, SplitDia = splitDia, SplitFrom = f.SplitFrom, Couple = couple
            });
            var skipped = ApplyLayer();
            if (skipped.Contains(number) || splitNumber != null && skipped.Contains(splitNumber))
            {
                _entries.RemoveAll(e => e.Dia.Number == number);
                ApplyLayer();
                return $"{number} はほかの臨時列車の列番とかぶっています";
            }
        }

        if (f.Depot != null)
        {
            Changed?.Invoke($"特発臨: {number}（{type}・{cars}両）{f.Depot} {startTrack}番線から出庫 {dia.Stops[0].Dep}発 → {dia.Dest} {dia.Stops[^1].Arr}着{coupleNote}");
            return null;
        }
        if (f.SplitFrom != null)
        {
            Changed?.Invoke($"特発臨: {number}（{type}・{cars}両）{f.Station.Name} {startTrack}番線で {f.SplitFrom} から切り離し（{splitNumber}） {dia.Stops[0].Dep}発 → {dia.Dest} {dia.Stops[^1].Arr}着{coupleNote}");
            return null;
        }

        // 元の列車は、その駅で打ち切り。着く番線から特発臨の向きへ出られないなら、出られる番線に入れる
        central.SetStop(f.Train, f.Station.Id, "打ち切り");
        if (setTrack) central.SetTrack(f.Train, f.Station.Id, startTrack);

        // 元の列車の後運用につなぐ（後運用の差し替え。元の列車の後運用は「なし」になる）
        string? turnNote = null;
        if (couple == null && r.LinkTurn && rail.DiaOf(f.Train) is { } orig && rail.Turns.TurnOf(orig) is { Kind: "折返", Number: { Length: > 0 } next })
        {
            var err = rail.Turns.Set(number, next);
            if (err == null)
            {
                lock (_lock) _entries.First(e => e.Dia.Number == number).TurnLinked = true;
                turnNote = $"・後運用 {next}";
            }
            else turnNote = $"・後運用はつなげませんでした（{err}）";
        }

        Changed?.Invoke($"特発臨: {number}（{type}）{f.Station.Name} {startTrack}番線 {dia.Stops[0].Dep}発 → {dia.Dest} {dia.Stops[^1].Arr}着。{f.Train} は {f.Station.Name} で打ち切り{turnNote}{coupleNote}");
        return null;
    }

    /// <summary>特発臨を取り消す。うまくいかなければ理由を返す。</summary>
    public string? Cancel(string? number)
    {
        RefreshStarted();
        var key = RailData.NormalizeTrain(number);
        Entry? e;
        lock (_lock)
        {
            e = _entries.FirstOrDefault(x => x.Dia.Number == key);
            if (e == null) return $"{key} の特発臨はありません";
        }

        // 後運用の差し替えを先に戻す（特発臨をダイヤから外す前に）
        if (e.TurnLinked) rail.Turns.Cancel(key);
        lock (_lock)
        {
            _entries.Remove(e);
            ApplyLayer();
        }

        if (!e.Started && e.From != null)
        {
            if (central.StopOverride(e.From, e.StationId) == "打ち切り") central.SetStop(e.From, e.StationId, null);
            if (e.TrackSet && central.TrackOverride(e.From, e.StationId) == e.Track) central.SetTrack(e.From, e.StationId, null);
        }
        if (e.Started) Removed?.Invoke(e.SplitDia != null ? [key, e.SplitDia.Number] : [key]);
        Changed?.Invoke(e.Started
            ? $"特発臨を取消: {key}（走り出していたので、自動運転の列車なら消します）"
            : e.From == null ? $"特発臨を取消: {key}（{e.Depot} からの出庫）"
            : $"特発臨を取消: {key}（{e.From} の {rail.Line.StationName(e.StationId)} での打ち切りも外します）");
        return null;
    }

    /// <summary>すべて取り消す（ロープレ時刻を設定したとき。打ち切り・後運用の差し替えは、それぞれのリセットで消える）。取り消した数を返す。</summary>
    public int Reset()
    {
        List<string> started;
        int n;
        lock (_lock)
        {
            n = _entries.Count;
            if (n == 0) return 0;
            started = _entries.Where(e => e.Started).SelectMany(e => e.SplitDia != null ? new[] { e.Dia.Number, e.SplitDia.Number } : new[] { e.Dia.Number }).ToList();
            _entries.Clear();
            ApplyLayer();
        }
        if (started.Count > 0) Removed?.Invoke(started);
        return n;
    }

    // _lock の中で呼ぶ
    private List<string> ApplyLayer() => rail.SetExtraTrains(Source, _entries.SelectMany(e => e.SplitDia != null ? new[] { e.SplitDia, e.Dia } : new[] { e.Dia }).ToList());

    // =========================================================================================
    // 下ごしらえ
    // =========================================================================================

    /// <summary>
    /// 発駅。元の列車なら、止まっている駅（番線が決まる）か、これから通る駅（番線は null）。station がなければ、止まっている駅か、次の駅。
    /// 出庫（Depot）なら、その車庫の駅。立てられなければ理由。
    /// </summary>
    private (From? From, string? Error) Origin(Request r)
    {
        if (r.Depot is { Length: > 0 } depot)
        {
            var d = Depots().FirstOrDefault(x => x.Name == depot);
            return d.Station == null ? (null, $"{depot} という車庫はありません") : (new From("", d.Station, null, depot), null);
        }
        // 解放: 走っている元の列車が、この先で停車する駅（時刻表で番線が決まっている駅）で切り離す
        if (r.Split is { Length: > 0 } split)
        {
            var sk = RailData.NormalizeTrain(split);
            var sp = players.Snapshot().FirstOrDefault(x => RailData.NormalizeTrain(x.TrainNumber) == sk && !x.ConnectionId.StartsWith(PlayerRegistry.GhostPrefix));
            if (sp == null) return (null, $"{sk} は走っていません");
            if (rail.DiaOf(sk) is not { } sd) return (null, $"{sk} は時刻表にありません（切り離す駅を決められません）");
            var (_, sAhead) = Stations(sp, sk);
            var sst = sAhead.FirstOrDefault(s => s.Id == r.Station);
            if (sst == null) return (null, $"{sk} はこの先 {rail.Line.StationName(r.Station ?? "")} を通りません（切り離すのは、この先の停車駅です）");
            if (!sd.Stations.TryGetValue(sst.Id, out var sds) || sds.StopType != "停車" || sds.Track is not { } stk)
                return (null, $"{sk} は{sst.Name}に停車しないか、番線が時刻表にありません（切り離すのは、停車する駅です）");
            return (new From("", sst, stk, null, sk), null);
        }
        var key = RailData.NormalizeTrain(r.Train);
        if (key.Length == 0) return (null, "元の列車を選ぶか、出庫する車庫を選んでください");
        var p = players.Snapshot().FirstOrDefault(x => RailData.NormalizeTrain(x.TrainNumber) == key && !x.ConnectionId.StartsWith(PlayerRegistry.GhostPrefix));
        if (p == null) return (null, $"{key} は走っていません");
        var (here, ahead) = Stations(p, key);
        var station = r.Station;
        if (string.IsNullOrEmpty(station))
        {
            if (here is { } h0) return (new From(key, h0.Station, h0.Track, null), null);
            return ahead.Count > 0 ? (new From(key, ahead[0], null, null), null) : (null, $"{key} の先に、進路を扱う駅がありません");
        }
        if (here is { } h && h.Station.Id == station) return (new From(key, h.Station, h.Track, null), null);
        if (ahead.FirstOrDefault(s => s.Id == station) is { } a) return (new From(key, a, null, null), null);
        var name = rail.Line.StationName(station);
        return (null, Where(p)?.Station.Id == station
            ? $"{key} は {name} を出ていくところです（この先の駅を発駅にしてください）"
            : $"{key} はこの先 {name} を通りません");
    }

    /// <summary>
    /// 発駅の番線。特発臨の向き（dir）へ出発する進路がある番線にする。出られる番線がなければ理由。
    /// ・止まっている列車: その番線から出られること（浜園の上りは1番線に着くが、1番線から下りへは入換をしないと出られない、など）
    /// ・走っている列車: 元の列車の向きで着けて、dir へ出られる番線（元の列車の時刻表の番線を先に）。時刻表の番線と違うなら、
    ///   元の列車の番線をそこに指定する（SetTrack = true）。着いた番線から出られないと、列番を変えたあと動けなくなるため
    /// ・出庫: 車庫の番線のうち、dir へ出られる番線（時刻表で出庫に使っている番線）
    /// </summary>
    private (int? Track, string? Error, bool SetTrack) StartTrack(From f, Dir dir)
    {
        var depart = f.Station.Routes[dir].Depart;
        var name = f.Depot ?? f.Station.Name;
        string NoTrack(IEnumerable<int> tracks, string who)
        {
            var can = depart.Keys.OrderBy(x => x).ToList();
            return $"{name} は、{who}{string.Join("・", tracks.OrderBy(x => x).Select(x => $"{x}番線"))}から{dir.Label()}へ出発する進路がありません"
                + (can.Count > 0 ? $"（{dir.Label()}へ出られるのは {string.Join("・", can.Select(x => $"{x}番線"))}）" : "") + "。ほかの駅を発駅にしてください";
        }

        if (f.Depot != null)
        {
            var tracks = Depots().First(x => x.Name == f.Depot).Tracks;
            var ok = tracks.Where(depart.ContainsKey).OrderBy(x => x).ToList();
            return ok.Count > 0 ? (ok[0], null, false) : (null, $"{f.Depot} から{dir.Label()}へ出庫する番線がありません", false);
        }
        if (f.SplitFrom != null && f.Track is { } stt) // 解放: 元の列車が着く番線から、そのまま出る
            return depart.ContainsKey(stt) ? (stt, null, false) : (null, NoTrack([stt], $"{f.SplitFrom} が着く"), false);
        if (f.Track is { } t) return depart.ContainsKey(t) ? (t, null, false) : (null, NoTrack([t], $"{f.Train} が止まっている"), false);

        var fromDir = RouteEngine.TrainInfo(f.Train)?.Dir ?? dir;
        var arrive = f.Station.Routes[fromDir].Arrive.Keys.ToList();
        var good = arrive.Where(depart.ContainsKey).OrderBy(x => x).ToList();
        if (good.Count == 0) return (null, NoTrack(arrive, $"{f.Train} が着ける"), false);
        int? diaTrack = rail.DiaOf(f.Train)?.Stations.TryGetValue(f.Station.Id, out var ds) == true ? ds.Track : null;
        var pick = diaTrack is { } dt && good.Contains(dt) ? dt : good[0];
        return (pick, null, pick != diaTrack);
    }

    /// <summary>
    /// 連結できる相手: 終点（stationId）に停車し、特発臨が着いたあと（arrSec から90分以内）に着く列車で、
    /// 増結車のいる番線へ入換信号機で入れる番線に着くもの（着く早い順）。特発臨（dir の向き）もその番線に入れること。もう増結車（連＋列番）がある列車は除く。
    /// </summary>
    private List<object> CoupleCandidates(string stationId, double arrSec, Dir dir)
    {
        var list = new List<(double Wait, object Row)>();
        foreach (var d in rail.Dia.Values)
        {
            if (d.Number.StartsWith("連") || d.Number.StartsWith("解") || rail.DiaOf("連" + d.Number) != null) continue;
            if (d.Stops.FirstOrDefault(s => s.StationId == stationId) is not { Stop: "停" } s || s == d.Stops[0] || s.Track is not { } tk) continue;
            if (Sec(s.Arr ?? s.Dep) is not { } at || RouteEngine.TrainInfo(d.Number) is not { } info) continue;
            var wait = (at - arrSec + 86400) % 86400;
            if (wait > 90 * 60 || !RouteEngine.CanCoupleAt(rail, stationId, info.Dir, tk) || !rail.Stations[stationId].Routes[dir].Arrive.ContainsKey(tk)) continue;
            list.Add((wait, new { train = d.Number, type = d.Type, time = s.Arr ?? s.Dep, track = tk }));
        }
        return list.OrderBy(x => x.Wait).Take(20).Select(x => x.Row).ToList();
    }

    /// <summary>
    /// 出庫できる車庫: 所定のダイヤで、進路を扱う駅の中の、駅とは別の名前の所から出る列車（江原検 = 江ノ原信号場の 10〜13番線 など）。
    /// 名前ごとに、時刻表で使っている番線をまとめる。
    /// </summary>
    public List<(string Name, StationDef Station, List<int> Tracks)> Depots()
    {
        return _depots ??= rail.BaseDia.Values
            .Select(d => d.Stops.FirstOrDefault())
            .Where(s => s?.StationId is { } id && s.Track != null && rail.Stations.TryGetValue(id, out var st) && st.RouteControl && s.Name != st.Name)
            .GroupBy(s => (s!.Name, Id: s.StationId!))
            .Select(g => (g.Key.Name, rail.Stations[g.Key.Id], g.Select(s => s!.Track!.Value).Distinct().OrderBy(x => x).ToList()))
            .OrderBy(x => rail.Line.ZoneOfStation(x.Item2.Id)).ToList();
    }
    private List<(string Name, StationDef Station, List<int> Tracks)>? _depots;

    /// <summary>出庫できる車庫（画面の選択肢）。向きごとに出庫できる番線を添える。</summary>
    public List<object> DepotView() => Depots().Select(d => (object)new
    {
        name = d.Name, station = d.Station.Id, stationName = d.Station.Name,
        down = d.Tracks.Where(d.Station.Routes[Dir.Down].Depart.ContainsKey).ToList(),
        up = d.Tracks.Where(d.Station.Routes[Dir.Up].Depart.ContainsKey).ToList()
    }).ToList();

    /// <summary>元の列車の両数（引き継ぐ）: 運転士のクライアントが送る編成 → 自動運転の列車 → 時刻表の両数。分からなければ null。</summary>
    private int? CarsOf(string train)
    {
        var key = RailData.NormalizeTrain(train);
        if (players.Snapshot().FirstOrDefault(p => RailData.NormalizeTrain(p.TrainNumber) == key && p.CarModels.Length > 0) is { } p) return p.CarModels.Length;
        return AiCars?.Invoke(key) ?? rail.DiaOf(key)?.Cars;
    }

    private static string Suffix(string type) => type switch { "特急" => "A", "急行" => "B", "準急" => "C", "区急" => "D", "快急" => "K", _ => "" };

    private Dictionary<string, HashSet<string>>? _usualStops;

    /// <summary>種別がふだん止まる駅: 所定のダイヤで、その種別の列車の1割以上が止まる駅。回送は止まらない。</summary>
    private HashSet<string> UsualStops(string type)
    {
        if (type == "回送") return new();
        _usualStops ??= rail.BaseDia.Values
            .SelectMany(t => t.Stops.Where(s => s.StationId != null).Select(s => (Id: s.StationId!, t.Type, Stop: s.Stop == "停")))
            .GroupBy(x => x.Type)
            .ToDictionary(g => g.Key, g => g.GroupBy(x => x.Id).Where(s => s.Count(x => x.Stop) * 10 >= s.Count()).Select(s => s.Key).ToHashSet());
        return _usualStops.TryGetValue(type, out var set) ? set : new();
    }

    /// <summary>
    /// 隣の駅までの標準運転時分（秒）。同じ種別・向き・停車の組み合わせ → 同じ種別・向き → ほかの種別で停車の組み合わせが同じもの →
    /// ほかの種別で同じ区間、の順に探す。回送は特急の通過の時分を使う。見つからなければ 120秒。
    /// </summary>
    private int RunSeconds(string type, Dir dir, string fromId, string toId, bool fromStops, bool toStops)
    {
        var from = rail.Line.StationName(fromId);
        var to = rail.Line.StationName(toId);
        var rows = rail.RunTimes.Where(x => x.Dir == dir && x.From == from && x.To == to).ToList();
        if (rows.Count == 0) return 120;
        var t = type == "回送" ? "特急" : type;
        return (rows.FirstOrDefault(x => x.Type == t && x.FromStops == fromStops && x.ToStops == toStops)
            ?? rows.FirstOrDefault(x => x.FromStops == fromStops && x.ToStops == toStops && x.Type == "普通")
            ?? rows.FirstOrDefault(x => x.FromStops == fromStops && x.ToStops == toStops)
            ?? rows.FirstOrDefault(x => x.Type == t)
            ?? rows[0]).Seconds;
    }

    private static readonly Regex TimeRe = new(@"^(\d{1,2}):(\d{2})(?::(\d{2}))?$");

    /// <summary>"H:mm" "HH:mm:ss" → "HH:mm:ss"。読めなければ null。</summary>
    private static string? NormTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = TimeRe.Match(s.Normalize(NormalizationForm.FormKC).Trim());
        if (!m.Success) return null;
        int h = int.Parse(m.Groups[1].Value), mi = int.Parse(m.Groups[2].Value), se = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        if (h > 27 || mi > 59 || se > 59) return null;
        return $"{h % 24:00}:{mi:00}:{se:00}";
    }

    private static double? Sec(string? s)
    {
        if (NormTime(s) is not { } t) return null;
        var p = t.Split(':');
        return int.Parse(p[0]) * 3600 + int.Parse(p[1]) * 60 + int.Parse(p[2]);
    }

    private static string Hms(double sec)
    {
        var s = (int)Math.Round(sec) % 86400;
        return $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}";
    }
}
