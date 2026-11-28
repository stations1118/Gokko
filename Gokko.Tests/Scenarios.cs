using System.Net.Http.Json;
using System.Text.Json;
using Gokko.Common;
using Microsoft.AspNetCore.SignalR.Client;

namespace Gokko.Tests;

/// <summary>
/// 試験で走らせる場面。時間帯（始める時刻・長さ）と、途中で入れる司令員の操作の並び。
/// 操作は、始めてからのロープレ時刻の分（At）になったら、司令用管理画面と同じ API で入れる。
/// 場面を足すときは、<see cref="All"/> に足す。直したバグの場面も、ここに足す。
/// </summary>
/// <param name="KnownIssue">
/// まだ直していない既知の不具合を再現する場面なら、その説明。破った決まりは「分かっている」として出し、試験は落とさない。
/// 直って破らなくなったら、試験が知らせるので、この説明を外す（外すと、次に破ったときに落ちる）。
/// </param>
public sealed record Scenario(string Name, string Slice, int Minutes, string About, List<Step> Steps, string? KnownIssue = null)
{
    /// <summary>記録を置くフォルダの名前。</summary>
    public string Folder => Slice.Replace(":", "") + (Steps.Count > 0 ? "-" + Name : "");

    /// <summary>ダイヤどおり（何も操作しない）の時間帯: 朝の出庫・朝ラッシュ・昼・夕ラッシュ・夜の入庫。</summary>
    private static readonly string[] PlainSlices = ["05:00", "07:30", "12:00", "17:30", "22:30"];

    public static Scenario Plain(string slice, int minutes) => new("ダイヤどおり", slice, minutes, "何も操作しない", new());

    /// <summary>既定で走らせる場面。</summary>
    public static List<Scenario> All(int minutes)
    {
        var list = PlainSlices.Select(s => Plain(s, minutes)).ToList();
        list.AddRange(Operations());
        return list;
    }

    /// <summary>司令員の操作を入れる場面。</summary>
    public static List<Scenario> Operations() =>
    [
        new("抑止して外す", "17:30", 40, "走っている列車3本を先の駅で抑止し、6分後に外す。外したあと、詰まりが残らない",
        [
            new(3, "3本を、2つ先の駅で抑止する", async d =>
            {
                foreach (var (train, st) in (await d.UpcomingAsync(minStations: 4)).Take(3))
                {
                    await d.PostAsync("route/hold", new { train, station = st[1].Id, hold = true });
                    d.Remember("held", train, st[1].Id);
                    d.Log($"{train} を {st[1].Name} で抑止");
                }
            }),
            new(9, "抑止を外す", async d =>
            {
                foreach (var (train, station) in d.Recall("held"))
                {
                    await d.PostAsync("route/hold", new { train, station, hold = false });
                    d.Log($"{train} の抑止を外す");
                }
            })
        ]),

        // 単線の交換駅では変えない: 対向列車のいる番線に変えると、指示どおりに両方が動けなくなる（司令員が見て決める所なので、試験では触らない）
        new("番線を変える", "12:00", 40, "複線の駅で、これから着く駅の番線を、選べるほかの番線に変える（3回、2本ずつ）",
        [
            new(2, "2本の番線を変える", d => ChangeTracksAsync(d, 2)),
            new(8, "2本の番線を変える", d => ChangeTracksAsync(d, 2)),
            new(14, "2本の番線を変える", d => ChangeTracksAsync(d, 2))
        ]),

        new("臨時停車と打ち切り", "07:30", 40, "通過する駅に臨時停車を入れ、1本を途中の駅で打ち切る",
        [
            new(2, "3本に臨時停車、1本を打ち切り", async d =>
            {
                var trains = await d.UpcomingAsync(minStations: 4);
                // 臨時停車: これから通過する駅（まだ進路を取っていない駅）
                var n = 0;
                foreach (var (train, st) in trains)
                {
                    var pass = st.Skip(1).FirstOrDefault(s => s.StopType == "通過" && !s.Locked);
                    if (pass == null) continue;
                    await d.PostAsync("route/stop", new { train, station = pass.Id, kind = "停車" });
                    d.Log($"{train} を {pass.Name} に臨時停車");
                    if (++n >= 3) break;
                }
                // 打ち切り: 終着の2つ手前より前の、止まる駅
                foreach (var (train, st) in trains.AsEnumerable().Reverse())
                {
                    var cut = st.Skip(1).Take(st.Count - 3).FirstOrDefault(s => s.StopType != "通過" && !s.Locked);
                    if (cut == null) continue;
                    await d.PostAsync("route/stop", new { train, station = cut.Id, kind = "打ち切り" });
                    d.Log($"{train} を {cut.Name} で打ち切り");
                    break;
                }
            })
        ]),

        new("時刻を飛ばす", "17:30", 40, "走らせている途中で、ロープレ時刻を1時間半先に飛ばす（列車を途中の駅から出し直す）",
        [
            new(5, "ロープレ時刻を 19:00 にする", async d =>
            {
                await d.PostAsync("clock", new { time = "19:00:00", running = true });
                d.Log("ロープレ時刻を 19:00 に設定");
            })
        ], KnownIssue: "時刻を飛ばしても、前から走っていた自動運転列車は残る。新しい時刻の列車が途中の駅から出てくるので、列車が倍になって大道寺・新野崎のまわりで詰まる"),

        // 11/28 本番: ユーザーが 1325A（三郷から来る下り）を、ゲームで始められる赤山町3番線で 13:30 に始めた。三郷から来る 1341 が
        // 赤山町3番線に入れず、単線区間の三郷〜赤山町・赤山町〜西赤山で待ち合って詰まった。
        // 1325A は、自動運転の 1325A が三郷から走って赤山町に着くまで出現待ち（在線も出さない）にし、着いたら引き継ぐ
        new("赤山町で先に始める", "13:30", 35, "三郷から来る 1325A を、ユーザーが赤山町3番線で先に始める（1341 が三郷から来る前）。自動運転の 1325A が三郷から着くまで出現待ちにし、着いたら引き継ぐ",
        [
            new(0.5, "ユーザーの 1325A を赤山町3番線で始める", async d =>
            {
                await d.JoinPlayerAsync("1325A", ["TH58_9LCT"]);
                d.Log("1325A（ユーザーの代わり）が赤山町3番線で運転画面に入った");
            }),
            new(5, "1325A が出現待ちになっている", async d =>
            {
                var reason = await d.AppearReasonAsync("1325A");
                if (reason?.StartsWith("三郷から来る自動運転の", StringComparison.Ordinal) != true)
                    throw new InvalidOperationException($"1325A が赤山町で待っていません（出現待ちの理由: {reason ?? "なし"}）");
                d.Log($"1325A は出現待ち: {reason}");
            }),
            new(24, "1325A を引き継いだ（自動運転の 1325A が三郷から着いた）", async d =>
            {
                if (await d.AppearReasonAsync("1325A") is { } reason) throw new InvalidOperationException($"1325A がまだ出現待ちです: {reason}");
                if (await d.AiTrainsAsync() is var ai && ai.Contains("1325A")) throw new InvalidOperationException("自動運転の 1325A が残っています");
                d.Log("1325A を引き継いだ。ユーザーの代わりは運転画面を出る（継続乗務の在線を残し、3分で自動運転の列車が引き継ぐ）");
                d.LeavePlayer();
            })
        ]),

        new("赤山町で確定してから遅れて入る", "13:30", 35, "1325A の列番を確定しただけで、運転画面にはまだ入らない。自動運転の 1325A は三郷から時刻どおり出て、赤山町で待つ。着いたあとでユーザーが赤山町で入ったら、すぐ引き継ぐ",
        [
            new(0.5, "1325A の列番を確定する（運転画面にはまだ入らない）", async d =>
            {
                await d.JoinPlayerAsync("1325A", [], inGame: false);
                d.Log("1325A を確定した（運転画面の外）");
            }),
            new(20.5, "自動運転の 1325A が三郷から時刻どおり出ている", async d =>
            {
                if (!(await d.AiTrainsAsync()).Contains("1325A")) throw new InvalidOperationException("自動運転の 1325A が出ていません（確定した列番なので出さないまま）");
                d.Log($"自動運転の 1325A: {await d.AiStatusAsync("1325A")}");
            }),
            new(23, "ユーザーが赤山町3番線で運転画面に入る", async d =>
            {
                d.Log($"自動運転の 1325A: {await d.AiStatusAsync("1325A")}");
                d.EnterPlayer(["TH58_9LCT"]);
                d.Log("1325A（ユーザーの代わり）が赤山町3番線で運転画面に入った");
            }),
            new(26, "1325A を引き継いだ", async d =>
            {
                if (await d.AppearReasonAsync("1325A") is { } reason) throw new InvalidOperationException($"1325A がまだ出現待ちです: {reason}");
                if ((await d.AiTrainsAsync()).Contains("1325A")) throw new InvalidOperationException($"自動運転の 1325A が残っています: {await d.AiStatusAsync("1325A")}");
                d.Log("1325A を引き継いだ。ユーザーの代わりは運転画面を出る");
                d.LeavePlayer();
            })
        ]),

        new("赤山町で確定したまま来ない", "13:30", 35, "1325A の列番を確定したまま、運転画面に入らない。自動運転の 1325A は赤山町で発車時刻から3分待ち、そのあと自動運転のまま先へ行く（3番線をふさぎ続けない）",
        [
            new(0.5, "1325A の列番を確定する（運転画面には入らない）", async d =>
            {
                await d.JoinPlayerAsync("1325A", [], inGame: false);
                d.Log("1325A を確定した（運転画面の外）");
            }),
            new(23, "自動運転の 1325A が赤山町でユーザーを待っている", async d =>
            {
                var status = await d.AiStatusAsync("1325A");
                if (status?.Contains("ユーザーの列車（1325A）を待っています") != true) throw new InvalidOperationException($"自動運転の 1325A が赤山町で待っていません: {status ?? "走っていない"}");
                d.Log($"自動運転の 1325A: {status}");
            }),
            new(30, "自動運転の 1325A が待つのをやめて先へ行った", async d =>
            {
                var status = await d.AiStatusAsync("1325A");
                if (status?.Contains("待っています") == true) throw new InvalidOperationException($"自動運転の 1325A がまだ赤山町で待っています: {status}");
                d.Log($"自動運転の 1325A: {status ?? "もういない"}");
            })
        ]),

        new("自動運転の切り入れ", "12:00", 30, "自動運転列車をナシにして全部消し、1分後にアリに戻す",
        [
            new(5, "自動運転列車をナシにする", async d => { await d.PostAsync("ai-trains/mode", new { on = false }); d.Log("ナシにした"); }),
            new(6, "自動運転列車をアリに戻す", async d => { await d.PostAsync("ai-trains/mode", new { on = true }); d.Log("アリに戻した"); })
        ])
    ];

    /// <summary>これから着く駅（まだ進路を取っていない・番線を2つ以上選べる）の番線を、ほかの番線に変える。</summary>
    private static async Task ChangeTracksAsync(Driver d, int count)
    {
        var n = 0;
        foreach (var (train, st) in await d.UpcomingAsync(minStations: 3))
        {
            if (d.Recall("tracked").Any(x => x.Train == train)) continue;
            var s = st.Skip(1).FirstOrDefault(x => d.DoubleTrack.Contains(x.Id) && !x.Locked && x.Track is { } t && x.Options.Any(o => o != t));
            if (s == null) continue;
            var to = s.Options.First(o => o != s.Track);
            await d.PostAsync("route/track", new { train, station = s.Id, track = to });
            d.Remember("tracked", train, s.Id);
            d.Log($"{train} の {s.Name} を {s.Track}番線 → {to}番線");
            if (++n >= count) break;
        }
    }
}

/// <summary>場面の途中で入れる操作。At は、始めてからのロープレ時刻の分。</summary>
public sealed record Step(double At, string Text, Func<Driver, Task> Do);

/// <summary>列車別設定の1駅ぶん（/routes の stations）。</summary>
public sealed record StationInfo(string Id, string Name, string StopType, int? Track, List<int> Options, bool Locked);

/// <summary>場面の操作から使う道具（司令用管理画面と同じ API を呼ぶ）。</summary>
public sealed class Driver(HttpClient http, string url, Action<string> log, IReadOnlySet<string> doubleTrack) : IAsyncDisposable
{
    private FakePlayer? _player;

    /// <summary>ゲームをつないだ列車の代わりを1本出す（乗務員を作ってログインし、クライアントと同じハブに、その回路に止まっていると報告し続ける）。</summary>
    public async Task JoinPlayerAsync(string train, List<string> circuits, bool inGame = true)
    {
        var created = await (await http.PostAsJsonAsync($"{url}/api/admin/crew/users", new { name = "試験の運転士", joinYear = 2020, depot = "道列" }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        var login = await (await http.PostAsJsonAsync($"{url}/api/tid/login",
                new { id = created.GetProperty("id").GetString(), password = created.GetProperty("password").GetString(), client = true }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        _player = await FakePlayer.StartAsync(url, login.GetProperty("token").GetString() ?? "", train, circuits, inGame);
    }

    /// <summary>列番を確定しただけのユーザーの代わりが、その回路で運転画面に入る。</summary>
    public void EnterPlayer(List<string> circuits) => _player?.Enter(circuits);

    /// <summary>その列番の自動運転の列車の状態（走っていなければ null）。</summary>
    public async Task<string?> AiStatusAsync(string train)
    {
        var ai = await http.GetFromJsonAsync<JsonElement>($"{url}/api/admin/ai-trains");
        return ai.GetProperty("trains").EnumerateArray().Where(t => t.GetProperty("train").GetString() == train)
            .Select(t => t.GetProperty("status").GetString()).FirstOrDefault();
    }

    /// <summary>ゲームをつないだ列車の代わりが、運転画面を出る（つながったまま）。</summary>
    public void LeavePlayer() => _player?.Leave();

    /// <summary>その列番の出現待ちの理由（出現待ちでなければ null）。</summary>
    public async Task<string?> AppearReasonAsync(string train)
    {
        var dia = await http.GetFromJsonAsync<JsonElement>($"{url}/api/admin/diagram");
        return dia.GetProperty("appear").EnumerateArray().Where(a => a.GetProperty("train").GetString() == train)
            .Select(a => a.GetProperty("reason").GetString()).FirstOrDefault();
    }

    /// <summary>走っている自動運転の列車の列番。</summary>
    public async Task<HashSet<string>> AiTrainsAsync()
    {
        var ai = await http.GetFromJsonAsync<JsonElement>($"{url}/api/admin/ai-trains");
        return ai.GetProperty("trains").EnumerateArray().Select(t => t.GetProperty("train").GetString() ?? "").ToHashSet();
    }

    public async ValueTask DisposeAsync()
    {
        if (_player != null) await _player.DisposeAsync();
    }

    /// <summary>複線の駅（Stations.json の track が「複線」）。</summary>
    public IReadOnlySet<string> DoubleTrack => doubleTrack;

    private readonly Dictionary<string, List<(string Train, string Station)>> _memo = new();

    public void Log(string text) => log(text);

    public void Remember(string key, string train, string station) =>
        (_memo.TryGetValue(key, out var l) ? l : _memo[key] = new()).Add((train, station));

    public List<(string Train, string Station)> Recall(string key) => _memo.GetValueOrDefault(key) ?? new();

    public async Task PostAsync(string path, object body)
    {
        var res = await http.PostAsJsonAsync($"{url}/api/admin/{path}", body);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"{path} が {(int)res.StatusCode} を返しました");
    }

    /// <summary>走っている列車と、この先の駅（列車別設定と同じもの）。この先の駅が minStations 以上ある列車だけ。列番の順。</summary>
    public async Task<List<(string Train, List<StationInfo> Stations)>> UpcomingAsync(int minStations)
    {
        var routes = await http.GetFromJsonAsync<JsonElement>($"{url}/api/admin/routes");
        var list = new List<(string, List<StationInfo>)>();
        foreach (var t in routes.GetProperty("trains").EnumerateArray())
        {
            var stations = t.GetProperty("stations").EnumerateArray().Select(s => new StationInfo(
                s.GetProperty("stationId").GetString() ?? "", s.GetProperty("name").GetString() ?? "", s.GetProperty("stopType").GetString() ?? "",
                s.GetProperty("track").ValueKind == JsonValueKind.Number ? s.GetProperty("track").GetInt32() : null,
                s.GetProperty("options").EnumerateArray().Select(o => o.GetInt32()).ToList(),
                s.GetProperty("locked").GetBoolean())).ToList();
            if (stations.Count >= minStations) list.Add((t.GetProperty("train").GetString() ?? "", stations));
        }
        return list;
    }
}

/// <summary>
/// ゲームをつないだ列車の代わり。クライアントと同じハブ（/hub）に参加して、0.5秒ごとに「この列番で、この回路に止まっている」と報告する。
/// 走りはしない（止まったまま）。Leave で運転画面を出たことにする。
/// </summary>
public sealed class FakePlayer : IAsyncDisposable
{
    private readonly HubConnection _hub;
    private readonly CancellationTokenSource _stop = new();
    private readonly ClientReport _report;
    private Task? _loop;

    private FakePlayer(HubConnection hub, ClientReport report) => (_hub, _report) = (hub, report);

    public static async Task<FakePlayer> StartAsync(string url, string crewToken, string train, List<string> circuits, bool inGame = true)
    {
        var hub = new HubConnectionBuilder().WithUrl(url + Protocol.HubPath).Build();
        await hub.StartAsync();
        var join = await hub.InvokeAsync<JoinResult>(Protocol.Join, new JoinRequest
        {
            ProtocolVersion = Protocol.Version, Mode = ShareMode.Shared, Role = CrewRole.Driver, CrewToken = crewToken
        });
        if (!join.Ok) throw new InvalidOperationException($"参加できませんでした: {join.Message}");
        var p = new FakePlayer(hub, new ClientReport
        {
            TrainNumber = train, TrainNumberTyped = true, TrainCrewConnected = true, InGame = inGame, TrackCircuits = circuits, Speed = 0
        });
        p._loop = p.LoopAsync();
        return p;
    }

    public void Enter(List<string> circuits)
    {
        lock (_report) (_report.InGame, _report.TrackCircuits) = (true, circuits);
    }

    public void Leave()
    {
        lock (_report) (_report.InGame, _report.TrackCircuits) = (false, new List<string>());
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                ClientReport r;
                lock (_report) r = new ClientReport
                {
                    TrainNumber = _report.TrainNumber, TrainNumberTyped = true, TrainCrewConnected = true, InGame = _report.InGame,
                    TrackCircuits = _report.TrackCircuits.ToList(), Speed = 0
                };
                await _hub.InvokeAsync<bool>(Protocol.Report, r, _stop.Token);
                await Task.Delay(500, _stop.Token);
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(500); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop != null) try { await _loop; } catch { }
        try { await _hub.DisposeAsync(); } catch { }
    }
}
