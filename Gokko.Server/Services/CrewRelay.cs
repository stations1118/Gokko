using System.Collections.Concurrent;
using Gokko.Common;
using Gokko.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Gokko.Server.Services;

/// <summary>配信（/hub）に参加している車掌1人分。在線としては数えない。</summary>
public sealed class ConductorInfo
{
    public required string ConnectionId { get; init; }

    /// <summary>ログインしている乗務員の社員番号。</summary>
    public string CrewId { get; init; } = "";
    public DateTime JoinedUtc { get; init; }
    public string TrainNumber { get; set; } = "";
    public bool InGame { get; set; }
    public float TraveledDistance { get; set; }
    public DateTime LastReportUtc { get; set; }

    /// <summary>添乗: 乗客として乗っている（放送だけが聞こえる）。車掌では使わない。</summary>
    public bool Passenger { get; set; }

    public bool TrainCrewConnected { get; set; }

    /// <summary>クライアントが測った通信状況（最後の報告）。</summary>
    public LinkHealth? Link { get; set; }
}

/// <summary>
/// 運転士と車掌の組み合わせ。
/// ・配信（/hub）に車掌として参加した人を覚える（<see cref="PlayerRegistry"/> には入れないので、在線・進路・信号には関わらない）
/// ・中継（/crew）: 運転士の操作を同じ列番の車掌へ、車掌の操作を運転士へそのまま送る（車掌は運転士の許可を得てから）
/// サーバーには何も記録しない（運転の記録は、各自のクライアントの exe のフォルダにだけ残す）。
/// </summary>
public sealed class CrewRelay(IHubContext<CrewHub> hub, ILogger<CrewRelay> logger)
{
    // ---- 配信（/hub）の車掌 ----
    private readonly ConcurrentDictionary<string, ConductorInfo> _conductors = new();

    public void AddConductor(string connectionId, string crewId = "") =>
        _conductors[connectionId] = new ConductorInfo { ConnectionId = connectionId, CrewId = crewId, JoinedUtc = DateTime.UtcNow, LastReportUtc = DateTime.UtcNow };

    public bool IsConductor(string connectionId) => _conductors.ContainsKey(connectionId);

    /// <summary>車掌の社員番号（車掌でなければ null）。</summary>
    public string? ConductorCrew(string connectionId) => _conductors.TryGetValue(connectionId, out var c) ? c.CrewId : null;

    public void UpdateConductor(string connectionId, ClientReport report)
    {
        if (!_conductors.TryGetValue(connectionId, out var c)) return;
        lock (c)
        {
            c.TrainNumber = (report.TrainNumber ?? "").Trim();
            if (c.TrainNumber.Length > 64) c.TrainNumber = c.TrainNumber[..64];
            c.InGame = report.InGame;
            c.TraveledDistance = report.TraveledDistance;
            c.TrainCrewConnected = report.TrainCrewConnected;
            c.Link = report.Link;
            c.LastReportUtc = DateTime.UtcNow;
        }
    }

    public void RemoveConductor(string connectionId) => _conductors.TryRemove(connectionId, out _);

    public List<ConductorInfo> Conductors() => Copy(_conductors);

    private static List<ConductorInfo> Copy(ConcurrentDictionary<string, ConductorInfo> from)
    {
        var list = new List<ConductorInfo>();
        foreach (var c in from.Values)
            lock (c)
                list.Add(new ConductorInfo
                {
                    ConnectionId = c.ConnectionId, CrewId = c.CrewId, JoinedUtc = c.JoinedUtc, TrainNumber = c.TrainNumber, InGame = c.InGame,
                    TraveledDistance = c.TraveledDistance, LastReportUtc = c.LastReportUtc, Passenger = c.Passenger,
                    TrainCrewConnected = c.TrainCrewConnected, Link = c.Link
                });
        return list;
    }

    // ---- 配信（/hub）の添乗 ----
    // 車掌と同じく、同じ列番の運転士の進路・信号を受け取るだけ（在線として数えない）。車掌とは別に覚える
    // （車掌を数えるところ・列車無線・内線電話に、そのまま混ざらないように）
    private readonly ConcurrentDictionary<string, ConductorInfo> _riders = new();

    public void AddRider(string connectionId, string crewId, bool passenger) =>
        _riders[connectionId] = new ConductorInfo
        {
            ConnectionId = connectionId, CrewId = crewId, JoinedUtc = DateTime.UtcNow, LastReportUtc = DateTime.UtcNow, Passenger = passenger
        };

    public bool IsRider(string connectionId) => _riders.ContainsKey(connectionId);

    public void UpdateRider(string connectionId, ClientReport report)
    {
        if (!_riders.TryGetValue(connectionId, out var c)) return;
        lock (c)
        {
            c.TrainNumber = (report.TrainNumber ?? "").Trim();
            if (c.TrainNumber.Length > 64) c.TrainNumber = c.TrainNumber[..64];
            c.InGame = report.InGame;
            c.TraveledDistance = report.TraveledDistance;
            c.Passenger = report.Passenger;
            c.TrainCrewConnected = report.TrainCrewConnected;
            c.Link = report.Link;
            c.LastReportUtc = DateTime.UtcNow;
        }
    }

    public void RemoveRider(string connectionId) => _riders.TryRemove(connectionId, out _);

    public List<ConductorInfo> Riders() => Copy(_riders);

    /// <summary>
    /// 社員番号の人が、その列番に車掌として許可されて乗っているか（中継 /crew の側で）。
    /// 放送装置は、許可された車掌だけに使わせる（列番を入れただけの人に、インターホンを聞かせないため）。
    /// </summary>
    public bool IsApprovedConductor(string crewId, string train) =>
        crewId.Length > 0 && train.Length > 0
                          && _members.Values.Any(x => x.Role == CrewRole.Conductor && x.Approval == ApprovalState.Approved && x.CrewId == crewId && x.TrainNumber == train);

    // ---- 中継（/crew） ----
    //
    // 車掌は、運転士の許可を得てから乗務に入る（いきなり乗り込まれると、運転士が戸惑うため）。
    // ・運転士が「車掌乗務を許可」を入れているときだけ、車掌の申し込みを運転士に届ける（「○○さんが乗務します。よろしいですか？」）
    // ・車掌が列番を確定したとき、または先に列番を入れて待っていた車掌の列車に、運転士が入った・許可を入れたときに申し込む
    // ・断られたら車掌に知らせる。車掌がもう一度［確定］を押すと、また申し込む
    // ・許可するまで、車掌は中継のグループに入れない（運転士の操作は届かず、車掌の操作も運転士に届かない）
    // ・一度許可した運転士と車掌（社員番号）の組み合わせは、同じ列番なら、つなぎ直しても聞き直さない

    private sealed class Member
    {
        public required string ConnectionId { get; init; }
        public CrewRole Role { get; set; }
        public string TrainNumber { get; set; } = "";
        public long LastSeq { get; set; } = -1;

        /// <summary>社員番号とユーザー名（ログインの合い札から）。</summary>
        public string CrewId { get; set; } = "";
        public string Name { get; set; } = "";

        /// <summary>運転士: 「車掌乗務を許可」が入っているか。</summary>
        public bool AllowConductor { get; set; }

        /// <summary>車掌: 許可の状態。</summary>
        public ApprovalState Approval { get; set; }

        /// <summary>車掌: 運転士に送った申し込みの番号（確かめている間だけ）。</summary>
        public string RequestId { get; set; } = "";
    }

    private readonly ConcurrentDictionary<string, Member> _members = new();

    /// <summary>許可した組み合わせ（列番・運転士の社員番号・車掌の社員番号）。</summary>
    private readonly HashSet<(string Train, string Driver, string Conductor)> _approved = new();

    private static string Group(string train, CrewRole role) => $"{role switch { CrewRole.Driver => "d", CrewRole.Rider => "r", _ => "c" }}:{train}";

    private readonly object _joinLock = new();

    private static string Who(string name, string id) =>
        name.Length > 0 ? $"{name}さん" : id.Length > 0 ? $"ID:{id} さん" : "車掌";

    /// <summary>
    /// 乗務に入る。1列車には運転士1人・車掌1人まで（同じ列番・同じ役割に、ほかの人がもう入っていたら断る）。添乗は何人でもよく、許可も要らない。
    /// 車掌は、入っても運転士の許可を得るまでは中継のグループに入らない。
    /// 断ったら理由を返し、前の列番からも抜ける。入れたら null。
    /// </summary>
    public async Task<string?> JoinAsync(string connectionId, CrewJoinRequest req, string crewId, string name)
    {
        var role = req.Role;
        var train = (req.TrainNumber ?? "").Trim();
        if (train.Length > 64) train = train[..64];
        var m = _members.GetOrAdd(connectionId, id => new Member { ConnectionId = id });
        string oldTrain;
        string? rejected = null;
        string? cancelRequest = null;
        lock (_joinLock)
        lock (m)
        {
            oldTrain = m.TrainNumber;
            m.CrewId = crewId;
            m.Name = name;
            if (role == CrewRole.Driver) m.AllowConductor = req.AllowConductor;
            if (oldTrain == train && m.Role == role)
            {
                // 同じ列番で入り直し: 断られた車掌は、もう一度申し込む（［確定］を押し直した）
                if (role == CrewRole.Conductor && m.Approval == ApprovalState.Declined) m.Approval = ApprovalState.Waiting;
                else if (role != CrewRole.Driver || !req.AllowConductor) return null;
            }
            else
            {
                if (train.Length > 0 && role != CrewRole.Rider && _members.Values.Any(x => x.ConnectionId != connectionId && x.TrainNumber == train && x.Role == role
                                                                 && !(x.Role == CrewRole.Conductor && x.Approval == ApprovalState.Declined)))
                {
                    rejected = $"列番 {train} には、もう{(role == CrewRole.Driver ? "運転士" : "車掌")}が乗っています（1列車に運転士1人・車掌1人まで）";
                    train = "";
                }

                if (m.Role == CrewRole.Conductor && m.RequestId.Length > 0) cancelRequest = m.RequestId;
                m.Role = role;
                m.TrainNumber = train;
                m.LastSeq = -1;
                m.Approval = ApprovalState.Waiting;
                m.RequestId = "";
            }
        }

        if (oldTrain != train || role == CrewRole.Driver)
        {
            if (oldTrain != train)
            {
                foreach (var r in new[] { CrewRole.Driver, CrewRole.Conductor, CrewRole.Rider })
                    if (oldTrain.Length > 0) await hub.Groups.RemoveFromGroupAsync(connectionId, Group(oldTrain, r));
                // 運転士と添乗は、すぐ中継のグループに入る（添乗は運転士の操作を受け取るだけ。許可は要らない）
                if (train.Length > 0 && role is CrewRole.Driver or CrewRole.Rider) await hub.Groups.AddToGroupAsync(connectionId, Group(train, role));
                logger.LogInformation("乗務 {Role} {Train} ID:{CrewId}（{Id}）", role switch { CrewRole.Driver => "運転士", CrewRole.Rider => "添乗", _ => "車掌" }, train, crewId,
                    connectionId[..Math.Min(6, connectionId.Length)]);
            }

            if (cancelRequest != null && oldTrain.Length > 0) await CancelRequestAsync(oldTrain, cancelRequest);
            if (oldTrain.Length > 0 && oldTrain != train) await DriverGoneAsync(oldTrain, role, connectionId);
            if (oldTrain.Length > 0 && oldTrain != train) await SendStatusAsync(oldTrain);
            if (train.Length > 0) await SendStatusAsync(train);
        }

        if (train.Length > 0) await EvaluateAsync(train);
        return rejected;
    }

    /// <summary>運転士が「車掌乗務を許可」を入・切した。入れたら、待っている車掌の申し込みを届ける。</summary>
    public async Task SetAllowAsync(string connectionId, bool allow)
    {
        if (!_members.TryGetValue(connectionId, out var m)) return;
        string train;
        lock (m)
        {
            if (m.Role != CrewRole.Driver) return;
            m.AllowConductor = allow;
            train = m.TrainNumber;
        }

        if (train.Length > 0) await EvaluateAsync(train);
    }

    /// <summary>運転士の返事。</summary>
    public async Task AnswerAsync(string connectionId, ConductorAnswer answer)
    {
        if (!_members.TryGetValue(connectionId, out var d)) return;
        string train, driverId, driverName;
        lock (d)
        {
            if (d.Role != CrewRole.Driver || d.TrainNumber.Length == 0) return;
            (train, driverId, driverName) = (d.TrainNumber, d.CrewId, d.Name);
        }

        var c = _members.Values.FirstOrDefault(x => x.Role == CrewRole.Conductor && x.RequestId.Length > 0 && x.RequestId == answer.RequestId);
        if (c == null) return;
        lock (_joinLock)
        lock (c)
        {
            if (c.TrainNumber != train || c.Approval != ApprovalState.Asking) return;
            c.RequestId = "";
            c.Approval = answer.Accept ? ApprovalState.Approved : ApprovalState.Declined;
            if (answer.Accept) _approved.Add((train, driverId, c.CrewId));
        }

        logger.LogInformation("乗務 {Train} 車掌 ID:{Conductor} を運転士 ID:{Driver} が{Answer}", train, c.CrewId, driverId, answer.Accept ? "許可" : "断った");
        if (answer.Accept)
        {
            await ApprovedAsync(c, train, $"運転士の{Who(driverName, driverId)}が乗務を許可しました。列番 {train} に乗務します");
        }
        else
        {
            await SendApprovalAsync(c.ConnectionId, ApprovalState.Declined,
                $"運転士の{Who(driverName, driverId)}が、列番 {train} への乗務を断りました。列番を確かめて、もう一度［確定］を押すと、また申し込みます");
        }
    }

    /// <summary>
    /// その列番の、待っている車掌の申し込みを進める（運転士がいない・許可が切れている・確かめている・許可済みの組み合わせ）。
    /// </summary>
    private async Task EvaluateAsync(string train)
    {
        var toDriver = new List<(string Driver, ConductorRequest Request)>();
        var toConductor = new List<(string Id, ApprovalState State, string Message)>();
        var approve = new List<(Member C, string Message)>();
        lock (_joinLock)
        {
            var driver = _members.Values.FirstOrDefault(x => x.Role == CrewRole.Driver && x.TrainNumber == train);
            foreach (var c in _members.Values.Where(x => x.Role == CrewRole.Conductor && x.TrainNumber == train))
            lock (c)
            {
                if (c.Approval != ApprovalState.Waiting) continue;
                if (driver == null)
                {
                    toConductor.Add((c.ConnectionId, ApprovalState.Waiting, $"列番 {train} の運転士が、まだ乗務に入っていません。運転士が入るのを待っています"));
                }
                else if (_approved.Contains((train, driver.CrewId, c.CrewId)))
                {
                    c.Approval = ApprovalState.Approved;
                    approve.Add((c, $"列番 {train} に乗務します（運転士の{Who(driver.Name, driver.CrewId)}の許可済み）"));
                }
                else if (!driver.AllowConductor)
                {
                    toConductor.Add((c.ConnectionId, ApprovalState.Waiting, $"運転士の{Who(driver.Name, driver.CrewId)}が「車掌乗務を許可」を押すのを待っています"));
                }
                else
                {
                    c.Approval = ApprovalState.Asking;
                    c.RequestId = Guid.NewGuid().ToString("N");
                    toDriver.Add((driver.ConnectionId, new ConductorRequest { RequestId = c.RequestId, TrainNumber = train, CrewId = c.CrewId, Name = c.Name }));
                    toConductor.Add((c.ConnectionId, ApprovalState.Asking, $"運転士の{Who(driver.Name, driver.CrewId)}に、乗務してよいか確かめています…"));
                }
            }
        }

        foreach (var (c, message) in approve) await ApprovedAsync(c, train, message);
        foreach (var (id, state, message) in toConductor) await SendApprovalAsync(id, state, message);
        foreach (var (driver, request) in toDriver)
            await TrySendAsync(() => hub.Clients.Client(driver).SendAsync(CrewProtocol.ReceiveConductorRequest, request));
    }

    private async Task ApprovedAsync(Member c, string train, string message)
    {
        await hub.Groups.AddToGroupAsync(c.ConnectionId, Group(train, CrewRole.Conductor));
        await SendApprovalAsync(c.ConnectionId, ApprovalState.Approved, message);
        await SendStatusAsync(train);
    }

    /// <summary>
    /// 運転士がその列番から抜けた: 乗っていた・確かめていた車掌は、待ちに戻す（同じ運転士がつなぎ直したら、許可済みの組み合わせとしてすぐ戻る）。
    /// </summary>
    private async Task DriverGoneAsync(string train, CrewRole role, string connectionId)
    {
        if (role != CrewRole.Driver) return;
        if (_members.Values.Any(x => x.ConnectionId != connectionId && x.Role == CrewRole.Driver && x.TrainNumber == train)) return;
        var back = new List<string>();
        lock (_joinLock)
            foreach (var c in _members.Values.Where(x => x.Role == CrewRole.Conductor && x.TrainNumber == train))
            lock (c)
            {
                if (c.Approval is not (ApprovalState.Approved or ApprovalState.Asking)) continue;
                c.Approval = ApprovalState.Waiting;
                c.RequestId = "";
                back.Add(c.ConnectionId);
            }

        foreach (var id in back) await hub.Groups.RemoveFromGroupAsync(id, Group(train, CrewRole.Conductor));
        await EvaluateAsync(train);
    }

    private Task CancelRequestAsync(string train, string requestId)
    {
        var driver = _members.Values.FirstOrDefault(x => x.Role == CrewRole.Driver && x.TrainNumber == train);
        return driver == null
            ? Task.CompletedTask
            : TrySendAsync(() => hub.Clients.Client(driver.ConnectionId).SendAsync(CrewProtocol.ReceiveRequestCancelled, requestId));
    }

    private Task SendApprovalAsync(string connectionId, ApprovalState state, string message) =>
        TrySendAsync(() => hub.Clients.Client(connectionId).SendAsync(CrewProtocol.ReceiveApproval, new CrewApproval { State = state, Message = message }));

    private async Task TrySendAsync(Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "乗務の知らせを送れなかった");
        }
    }

    public async Task LeaveAsync(string connectionId)
    {
        if (!_members.TryRemove(connectionId, out var m)) return;
        string train, requestId;
        CrewRole role;
        lock (m) (train, role, requestId) = (m.TrainNumber, m.Role, m.RequestId);

        if (train.Length == 0) return;
        if (role == CrewRole.Conductor && requestId.Length > 0) await CancelRequestAsync(train, requestId);
        await DriverGoneAsync(train, role, connectionId);
        await SendStatusAsync(train);
    }

    /// <summary>運転士の操作を、同じ列番の（許可された）車掌と添乗へ送る。</summary>
    public Task FromDriverAsync(string connectionId, DriverInput input)
    {
        if (!_members.TryGetValue(connectionId, out var m)) return Task.CompletedTask;
        string train;
        lock (m)
        {
            if (m.Role != CrewRole.Driver || m.TrainNumber.Length == 0 || input.Seq <= m.LastSeq) return Task.CompletedTask;
            m.LastSeq = input.Seq;
            train = m.TrainNumber;
        }

        return hub.Clients.Groups(Group(train, CrewRole.Conductor), Group(train, CrewRole.Rider)).SendAsync(CrewProtocol.ReceiveDriverInput, input);
    }

    /// <summary>車掌の操作を、同じ列番の運転士へ送る（許可されていなければ送らない）。</summary>
    public Task FromConductorAsync(string connectionId, ConductorInput input)
    {
        if (!_members.TryGetValue(connectionId, out var m)) return Task.CompletedTask;
        string train;
        lock (m)
        {
            if (m.Role != CrewRole.Conductor || m.TrainNumber.Length == 0 || m.Approval != ApprovalState.Approved) return Task.CompletedTask;
            train = m.TrainNumber;
        }

        if (!ConductorInput.Actions.Contains(input.Action ?? "-")) return Task.CompletedTask;
        // ドアの開け閉めは添乗にも送る（運転士・車掌のゲームと同時にドアを動かす。閉まりきってから合わせると、発車が遅れる）
        if (input.Action is { Length: > 0 } a && (a.StartsWith("DoorOpn") || a.StartsWith("DoorCls")))
            return hub.Clients.Groups(Group(train, CrewRole.Driver), Group(train, CrewRole.Rider)).SendAsync(CrewProtocol.ReceiveConductorInput, input);
        return hub.Clients.Group(Group(train, CrewRole.Driver)).SendAsync(CrewProtocol.ReceiveConductorInput, input);
    }

    /// <summary>
    /// 乗務の試験（管理キーの要る crewtest.html から）: 車掌の操作を、列番の運転士へそのまま送る（車掌が乗っていなくても）。
    /// 送れなければ理由を返す。
    /// </summary>
    public async Task<string?> FromTestAsync(string train, ConductorInput input)
    {
        if (train.Length == 0) return "列番を入れてください";
        if (!ConductorInput.Actions.Contains(input.Action ?? "-")) return "送れない操作です";
        if (!Members(train).Any(x => x.Role == CrewRole.Driver)) return $"列番 {train} の運転士が中継にいません（運転士のクライアントを在線共有でつなぎ、列番を確定してください）";
        await hub.Clients.Group(Group(train, CrewRole.Driver)).SendAsync(CrewProtocol.ReceiveConductorInput, input);
        return null;
    }

    private List<Member> Members(string train) => _members.Values.Where(x => x.TrainNumber == train).ToList();

    /// <summary>同じ列番の顔ぶれ（車掌は、許可されて乗務に入っている人だけ数える）。</summary>
    public CrewStatus StatusOf(string train)
    {
        var list = Members(train);
        return new CrewStatus
        {
            TrainNumber = train,
            Drivers = list.Count(x => x.Role == CrewRole.Driver),
            Conductors = list.Count(x => x.Role == CrewRole.Conductor && x.Approval == ApprovalState.Approved),
            Riders = list.Count(x => x.Role == CrewRole.Rider)
        };
    }

    private async Task SendStatusAsync(string train)
    {
        var status = StatusOf(train);
        try
        {
            await hub.Clients.Groups(Group(train, CrewRole.Driver), Group(train, CrewRole.Conductor), Group(train, CrewRole.Rider)).SendAsync(CrewProtocol.ReceiveStatus, status);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "乗務の顔ぶれを送れなかった");
        }
    }

    /// <summary>許可されて車掌として乗っている人（列番・社員番号・ユーザー名）。乗務タブレットの行路一覧の車掌の枠に出す。</summary>
    public List<(string Train, string CrewId, string Name)> ApprovedConductors() =>
        _members.Values.Where(x => x.Role == CrewRole.Conductor && x.Approval == ApprovalState.Approved && x.TrainNumber.Length > 0)
            .Select(x => (x.TrainNumber, x.CrewId, x.Name)).ToList();

    public string? TrainOf(string connectionId) => _members.TryGetValue(connectionId, out var m) ? m.TrainNumber : null;
}
