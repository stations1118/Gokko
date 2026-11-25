using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Text;
using Gokko.Common;

namespace Gokko.Server.Services;

/// <summary>参加中のプレイヤー1人分の状態。読むときは <see cref="PlayerRegistry.Snapshot"/> の複製を使う。</summary>
public sealed class PlayerInfo
{
    public required string ConnectionId { get; set; }

    /// <summary>ログインしている乗務員の社員番号。</summary>
    public string CrewId { get; set; } = "";
    public DateTime JoinedUtc { get; init; }
    public string TrainNumber { get; set; } = "";
    public bool TrainCrewConnected { get; set; }
    public bool InGame { get; set; }
    public List<string> TrackCircuits { get; set; } = new();

    /// <summary>踏んでいる軌道回路から求めた、路線上の位置（駅=偶数、駅間=奇数）。</summary>
    public List<int> Zones { get; set; } = new();

    /// <summary>進行方向: 1=ゾーンの番号が大きくなる向き、-1=小さくなる向き、0=不明。</summary>
    public int Direction { get; set; }

    public double? LastCenter { get; set; }
    public DateTime LastReportUtc { get; set; }

    /// <summary>クライアントが測った通信状況（最後の報告。報告ごとに新しいものに置き換えるので、複製で共有してよい）。</summary>
    public LinkHealth? Link { get; set; }

    // ---- 在線共有モード ----
    public ShareMode Mode { get; set; }
    /// <summary>車両ごとの形式（運転士のクライアントが送る。列車位置案内の編成に出す）。</summary>
    public string[] CarModels { get; set; } = [];
    /// <summary>編成平均乗車率（%、表示用の丸め前）。取得できないときは null。</summary>
    public decimal? AverageOccupancy { get; set; }
    public List<StopInfo> Stops { get; set; } = new();
    public float TraveledDistance { get; set; }
    /// <summary>踏んでいる軌道回路が最後に変わった報告での、走った距離（m）と速度（km/h）。在線の記録に書き、回路の長さを出すのに使う。</summary>
    public float? CircuitsChangedDistance { get; set; }
    public float CircuitsChangedSpeed { get; set; }
    public float NextStationDistance { get; set; }
    public float Speed { get; set; }
    /// <summary>報告された速度（km/h。モードによらずいつも入れる。内線電話で、運転士が止まっているかを見る）。</summary>
    public float ReportedSpeed { get; set; }
    public HashSet<(string Interlock, string Route)> GameRoutes { get; set; } = new();
    public Dictionary<string, int> TrackChoices { get; set; } = new();
    public Dictionary<string, int> EndChoices { get; set; } = new();

    // ---- 継続乗務 ----

    /// <summary>継続乗務ボタンが押されているか（最後の報告）。</summary>
    public bool ContinueDuty { get; set; }

    /// <summary>列車番号を手入力したか（最後の報告）。ダイヤ運転中は、手入力していない列車を出現させない。</summary>
    public bool TrainNumberTyped { get; set; }


    /// <summary>出現待ちの理由（継続乗務の開始番線が違う）。あれば、サーバーの列車として扱わない。</summary>
    public string? AppearReason { get; set; }

    /// <summary>位置（ゾーン）が分かっていた最後の時刻。データにない回路を踏んでいる間は、前の位置を保つ。</summary>
    public DateTime ZonesUpdatedUtc { get; set; }

    /// <summary>
    /// 連結された増結車（ユーザーが運転している 連777 など）なら、連結した列車の列番（777）。親の列車が同じ軌道回路に入った時点で入る。
    /// この間は在線を出さない（<see cref="PlayerRegistry.Snapshot"/> で踏んでいる回路を空にする。その回路には親の列番が出る）。
    /// 運転画面を出る・列番が変わると外す。
    /// </summary>
    public string? CoupledInto { get; set; }

    /// <summary>この列車が引き継いだ在線の接続ID（継続乗務の在線を自動運転の列車が引き継いだとき）。進路・駅の状態をそこから移す。</summary>
    public string? TakesOver { get; set; }

    public PlayerInfo Clone()
    {
        var copy = (PlayerInfo)MemberwiseClone();
        copy.TrackCircuits = new List<string>(TrackCircuits);
        copy.Zones = new List<int>(Zones);
        copy.Stops = new List<StopInfo>(Stops);
        copy.GameRoutes = new HashSet<(string, string)>(GameRoutes);
        copy.TrackChoices = new Dictionary<string, int>(TrackChoices);
        copy.EndChoices = new Dictionary<string, int>(EndChoices);
        return copy;
    }
}

/// <summary>参加中のプレイヤーの一覧。SignalR の接続ごとに1人。</summary>
public sealed class PlayerRegistry(GameData data, CentralControl central)
{
    /// <summary>ダイヤ運転中に列車番号を手入力していない列車の、出現待ちの理由。</summary>
    public const string NeedTypedNumber = "ダイヤ運転中は列車番号を手入力してください（シナリオの列番は使いません）";

    // ---- 強制切断（管理画面・ダイヤグラムの設定画面から） ----
    private readonly ConcurrentDictionary<string, bool> _kicked = new();

    /// <summary>参加者を強制的に切断する（次の報告のときに接続を切る）。参加していなければ false。</summary>
    public bool Kick(string connectionId)
    {
        if (!_players.ContainsKey(connectionId)) return false;
        _kicked[connectionId] = true;
        Changed?.Invoke($"強制切断: {(_players.TryGetValue(connectionId, out var p) ? p.TrainNumber : "")}（{connectionId[..Math.Min(6, connectionId.Length)]}）");
        return true;
    }

    public bool IsKicked(string connectionId) => _kicked.ContainsKey(connectionId);

    private const int MaxCircuits = 200;
    private const int MaxNameLength = 64;

    private readonly ConcurrentDictionary<string, PlayerInfo> _players = new();

    public int Count => _players.Count;

    public void Add(string connectionId, ShareMode mode, string crewId = "")
    {
        var now = DateTime.UtcNow;
        _players[connectionId] = new PlayerInfo { ConnectionId = connectionId, CrewId = crewId, JoinedUtc = now, LastReportUtc = now, Mode = mode };
    }

    public bool IsJoined(string connectionId) => _players.ContainsKey(connectionId);

    /// <summary>退出。継続乗務ボタンを押したまま運転中だったら、在線を残す（つなぎ直して同じ番線から始めれば引き継ぐ）。</summary>
    public void Remove(string connectionId)
    {
        var kicked = _kicked.TryRemove(connectionId, out _);
        if (!_players.TryRemove(connectionId, out var p)) return;
        lock (p)
            if (!kicked && (p.ContinueDuty || central.DiaMode) && p.Mode == ShareMode.Shared && p.InGame && p.TrackCircuits.Count > 0 && p.TrainNumber.Length > 0
                && p.AppearReason == null && p.CoupledInto == null && !LeavesLine(p))
                AddGhost(p);
    }

    /// <summary>連結: 増結車（ユーザーが運転している列車）を、親の列車に連結したことにする（在線を消す）。参加していなければ false。</summary>
    public bool Couple(string connectionId, string parent)
    {
        if (!_players.TryGetValue(connectionId, out var p)) return false;
        string child;
        lock (p)
        {
            if (p.CoupledInto != null) return false;
            p.CoupledInto = parent;
            child = p.TrainNumber;
        }

        Changed?.Invoke($"連結: {child} は {parent} に連結したので、在線を消しました");
        return true;
    }

    public PlayerInfo? Get(string connectionId)
    {
        if (!_players.TryGetValue(connectionId, out var p)) return null;
        lock (p) return p.Clone();
    }

    /// <summary>参加中のプレイヤーと、継続乗務で残している在線（接続IDが <see cref="GhostPrefix"/> で始まる）。</summary>
    public List<PlayerInfo> Snapshot() => Snapshot(virtualTrains: true);

    /// <summary>
    /// 参加者と、継続乗務で残した在線だけの複製（自動運転の列車は入れない）。
    /// 自動運転の列車の周期（0.2秒）が、ユーザーの列車を調べるためだけに、自分の列車まで毎回複製しないようにする。
    /// </summary>
    public List<PlayerInfo> SnapshotWithoutVirtualTrains() => Snapshot(virtualTrains: false);

    private List<PlayerInfo> Snapshot(bool virtualTrains)
    {
        var list = new List<PlayerInfo>();
        foreach (var p in _players.Values)
        {
            PlayerInfo c;
            lock (p) c = p.Clone();
            // 連結された増結車は在線を出さない（親の列車の在線だけにする）
            if (c.CoupledInto != null) (c.TrackCircuits, c.Zones) = (new List<string>(), new List<int>());
            list.Add(c);
        }

        lock (_ghosts)
        {
            ExpireGhosts();
            list.AddRange(_ghosts.Values.Select(g => g.Info.Clone()));
        }

        if (virtualTrains && VirtualTrains?.Invoke() is { } ai) list.AddRange(ai);
        return list;
    }

    /// <summary>
    /// ログインしている乗務員が、運転画面で運転している列車（社員番号, 入力している列番）。継続乗務で残した在線も含む。
    /// 並びは <see cref="Snapshot"/> と同じ。画面の API が、これだけのために全員ぶんの複製（Snapshot）を作らないようにする。
    /// </summary>
    public List<(string CrewId, string Train)> CrewTrains()
    {
        var list = new List<(string, string)>();
        foreach (var p in _players.Values)
            lock (p)
                if (p.CrewId.Length > 0 && p.InGame && p.TrainNumber.Length > 0) list.Add((p.CrewId, p.TrainNumber));
        lock (_ghosts)
        {
            ExpireGhosts();
            foreach (var g in _ghosts.Values)
                if (g.Info.CrewId.Length > 0 && g.Info.InGame && g.Info.TrainNumber.Length > 0) list.Add((g.Info.CrewId, g.Info.TrainNumber));
        }
        return list;
    }

    // ---- にぎやかしの列車（AiTrains。接続IDが AiPrefix で始まる） ----

    public const string AiPrefix = "AI/";

    /// <summary>にぎやかしの列車の在線（AiTrains が入れる）。Snapshot に混ぜる。</summary>
    public Func<IEnumerable<PlayerInfo>>? VirtualTrains { get; set; }

    /// <summary>人のいない参加者（継続乗務の残した在線・にぎやかしの列車）か。</summary>
    public static bool IsVirtual(string connectionId) => connectionId.StartsWith(GhostPrefix) || connectionId.StartsWith(AiPrefix);

    // ---- 継続乗務: 運転画面を出たあとも、最後の在線位置を5分間残す ----

    public const string GhostPrefix = "継続/";
    public static readonly TimeSpan GhostLife = TimeSpan.FromMinutes(5);

    public sealed record Ghost(string Id, string Owner, PlayerInfo Info, DateTime SinceUtc, DateTime UntilUtc);

    private readonly Dictionary<string, Ghost> _ghosts = new();

    public event Action<string>? Changed;

    public List<Ghost> Ghosts()
    {
        lock (_ghosts)
        {
            ExpireGhosts();
            return _ghosts.Values.OrderBy(g => g.SinceUtc).ToList();
        }
    }

    /// <summary>継続乗務の在線が、管理画面・ダイヤグラムの設定画面から消されたとき（自動運転の列車が引き継ぐことがある）。</summary>
    public event Action<Ghost>? GhostDeleted;

    /// <summary>継続乗務の在線を消す（管理画面・ダイヤグラムの設定画面から）。</summary>
    public bool RemoveGhost(string id)
    {
        Ghost? g;
        lock (_ghosts)
            if (!_ghosts.Remove(id, out g)) return false;
        Changed?.Invoke($"継続乗務: {g.Info.TrainNumber} の在線を消しました");
        GhostDeleted?.Invoke(g);
        return true;
    }

    /// <summary>継続乗務の在線を、引き継ぐために外す（消したことにはしない）。引き継げなければ <see cref="RestoreGhost"/> で戻す。</summary>
    public Ghost? TakeGhost(string id)
    {
        lock (_ghosts)
            return _ghosts.Remove(id, out var g) ? g : null;
    }

    /// <summary><see cref="TakeGhost"/> で外した在線を戻す。</summary>
    public void RestoreGhost(Ghost g)
    {
        lock (_ghosts) _ghosts[g.Id] = g;
    }

    private void ExpireGhosts()
    {
        if (central.DiaMode) return; // ダイヤ運転中は時間で消さない（管理画面・ダイヤグラムの設定画面から消す）
        var now = DateTime.UtcNow;
        foreach (var g in _ghosts.Values.Where(g => g.UntilUtc <= now).ToList())
        {
            _ghosts.Remove(g.Id);
            Changed?.Invoke($"継続乗務: {g.Info.TrainNumber} は5分たったので在線を消しました");
        }
    }

    /// <summary>
    /// 線区の外（赤山町の先、三郷方面）へ出ていく列車か。上り列車（列番の数字の最後の桁が偶数）が、赤山町の1番線・2番線、
    /// またはそれより三郷寄り（赤山町〜三郷の駅間・三郷・路線データにない回路）にだけいるとき。
    /// こういう列車がシナリオを終えたときは、継続乗務の在線を残さない（ダイヤ運転中も。その先へ出て行った扱い）。
    /// 2番線は下りの通過（新大路方から入ってくる列車）も使うので、上り列車に限る
    /// </summary>
    private bool LeavesLine(PlayerInfo p)
    {
        if (p.TrackCircuits.Count == 0) return false;
        var body = Regex.Replace(Regex.Replace(p.TrainNumber.Trim(), @"#\d+$", ""), "[XYZ]$", "", RegexOptions.IgnoreCase);
        var digits = Regex.Match(body, @"(\d+)(?!.*\d)");
        if (!digits.Success || int.Parse(digits.Groups[1].Value[^1..]) % 2 != 0) return false; // 上り（偶数）だけ

        var exitZone = data.Line.ZoneOfStation(ExitStation);
        if (exitZone < 0) return false;
        return p.TrackCircuits.All(c => ExitTrackCircuits.Contains(c) || (data.Line.ZoneOfCircuit(c) is not { } z || z < exitZone));
    }

    /// <summary>線区の外へ出る駅（赤山町）と、出ていく列車が使う番線の回路（1番線・2番線）。</summary>
    private const string ExitStation = "TH58";
    private static readonly HashSet<string> ExitTrackCircuits = ["TH58_2RAT", "TH58_2RBT"];

    private void AddGhost(PlayerInfo from)
    {
        var now = DateTime.UtcNow;
        var id = $"{GhostPrefix}{from.ConnectionId}/{now.Ticks}";
        var info = from.Clone();
        info.ConnectionId = id;
        info.AverageOccupancy = null;
        info.InGame = true;
        info.TrainCrewConnected = true;
        info.Speed = 0;
        info.ContinueDuty = false;
        info.AppearReason = null;
        info.TakesOver = null;
        lock (_ghosts)
        {
            foreach (var old in _ghosts.Values.Where(g => g.Owner == from.ConnectionId).ToList()) _ghosts.Remove(old.Id);
            _ghosts[id] = new Ghost(id, from.ConnectionId, info, now, central.DiaMode ? DateTime.MaxValue : now + GhostLife);
        }

        Changed?.Invoke(central.DiaMode ? $"継続乗務: {info.TrainNumber} の在線を残します（ダイヤ運転中は消すまで残す）" : $"継続乗務: {info.TrainNumber} の在線を5分間残します");
    }

    /// <summary>
    /// 運転画面に入った列車が、継続乗務の在線を引き継ぐか。同じ番線（踏んでいる軌道回路が重なる）なら、残した在線を消して
    /// 新しいシナリオの列番の列車として始める（出現待ちにしない）。
    /// 自分が残した在線があるのに番線が違えば「開始番線が違う」として出現待ちにする。
    /// </summary>
    private void MatchGhost(PlayerInfo p, string number)
    {
        lock (_ghosts)
        {
            ExpireGhosts();
            var same = _ghosts.Values.Where(g => g.Info.TrackCircuits.Any(p.TrackCircuits.Contains))
                .OrderByDescending(g => g.Owner == p.ConnectionId).FirstOrDefault();
            if (same != null)
            {
                _ghosts.Remove(same.Id);
                p.AppearReason = null;
                Changed?.Invoke($"継続乗務: {same.Info.TrainNumber} の在線を {number} が引き継ぎました");
                return;
            }

            var mine = _ghosts.Values.FirstOrDefault(g => g.Owner == p.ConnectionId);
            p.AppearReason = mine != null ? $"開始番線が違う（継続乗務 {mine.Info.TrainNumber} の在線が残っています）" : null;
        }
    }

    /// <summary>クライアントの報告を反映する。参加していない接続なら false。</summary>
    public bool ApplyReport(string connectionId, ClientReport report)
    {
        if (!_players.TryGetValue(connectionId, out var p)) return false;

        var circuits = (report.TrackCircuits ?? new List<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => Truncate(c.Trim()))
            .Distinct()
            .Take(MaxCircuits)
            .ToList();
        var zones = report.InGame ? data.Line.ZonesOf(circuits) : new List<int>();

        lock (p)
        {
            // 継続乗務: ボタンを押したまま運転画面を出たら、最後の在線を残す。運転画面に入ったら、残した在線を引き継ぐか確かめる
            var wasIn = p.InGame && p.TrackCircuits.Count > 0;
            var circuitsChanged = !p.TrackCircuits.ToHashSet(StringComparer.Ordinal).SetEquals(circuits);
            var nowIn = report.InGame && circuits.Count > 0;
            // ダイヤ運転中は、継続乗務ボタンによらず残す（出現待ちだった列車は、在線がないので残さない）
            if (wasIn && !nowIn && p.Mode == ShareMode.Shared && (p.ContinueDuty || report.ContinueDuty || central.DiaMode) && p.TrainNumber.Length > 0
                && p.AppearReason == null && p.CoupledInto == null && !LeavesLine(p))
                AddGhost(p);
            p.ContinueDuty = report.ContinueDuty;
            p.TrainNumberTyped = report.TrainNumberTyped;
            var number = Truncate(NormalizeName(report.TrainNumber));
            // 連結された増結車: 運転画面を出た・列番が変わったら、ふつうの列車に戻す
            if (p.CoupledInto != null && (!nowIn || number != p.TrainNumber)) p.CoupledInto = null;
            // ダイヤ運転中に列車番号を手入力していなければ、出現待ち（残した在線は引き継がず、消しも踏みつぶしもしない）
            var mustType = central.DiaMode && p.Mode == ShareMode.Shared && !report.TrainNumberTyped;
            if (!nowIn) p.AppearReason = null;
            else if (mustType) p.AppearReason = NeedTypedNumber;
            else if (p.Mode == ShareMode.Shared && (!wasIn || p.AppearReason != null))
            {
                p.TrackCircuits = circuits;
                MatchGhost(p, number);
            }

            p.TrainNumber = number;
            p.TrainCrewConnected = report.TrainCrewConnected;
            p.InGame = report.InGame;
            p.TrackCircuits = circuits;
            p.LastReportUtc = DateTime.UtcNow;
            p.Link = report.Link;
            p.ReportedSpeed = report.Speed;

            // データにない回路だけを踏んでいるときは、前の位置を保つ（運転画面を出たら消す）
            if (zones.Count > 0 || !report.InGame || circuits.Count == 0)
            {
                p.Zones = zones;
                p.ZonesUpdatedUtc = DateTime.UtcNow;
            }

            if (p.Mode == ShareMode.Shared)
            {
                p.CarModels = report.InGame && report.CarModels is { Length: <= 64 } cars ? cars : [];
                p.AverageOccupancy = report.TrainCrewConnected && p.CarModels.Length > 0 && report.AverageOccupancy is >= 0
                    ? report.AverageOccupancy : null;
                p.Stops = (report.Stops ?? new List<StopInfo>()).Take(100).ToList();
                p.TraveledDistance = report.TraveledDistance;
                // 回路が変わった報告の距離（同じ報告の値なので、時計のずれがない）
                if (circuitsChanged) { p.CircuitsChangedDistance = report.TraveledDistance; p.CircuitsChangedSpeed = report.Speed; }
                p.NextStationDistance = report.NextStationDistance;
                p.Speed = report.Speed;
                p.GameRoutes = (report.GameRoutes ?? new List<RouteKey>()).Take(200)
                    .Select(r => (r.Interlocking, r.RouteName)).ToHashSet();
                p.TrackChoices = (report.TrackChoices ?? new Dictionary<string, int>())
                    .Take(50).ToDictionary(kv => kv.Key, kv => kv.Value);
                p.EndChoices = (report.EndChoices ?? new Dictionary<string, int>())
                    .Take(50).ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            zones = p.Zones;

            // 位置が動いたら、その向きを進行方向とする。止まっている間は、直前の向きを保つ
            if (zones.Count > 0)
            {
                var center = zones.Average();
                if (p.LastCenter is { } last && Math.Abs(center - last) > 1e-9) p.Direction = center > last ? 1 : -1;
                p.LastCenter = center;
            }
        }

        return true;
    }

    private static string NormalizeName(string? name)
    {
        return (name ?? "").Normalize(NormalizationForm.FormKC).Trim();
    }

    private static string Truncate(string s) => s.Length <= MaxNameLength ? s : s[..MaxNameLength];
}
