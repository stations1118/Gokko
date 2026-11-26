using System.Security.Cryptography;
using System.Text;
using Gokko.Common;
using Gokko.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace Gokko.Server.Hubs;

/// <summary>ユーザークライアントとの接続口（/hub）。メソッド名は Protocol と一致させる。</summary>
public sealed partial class GameHub(
    PlayerRegistry players,
    IncidentManager incidents,
    GameData data,
    StateBroadcaster broadcaster,
    RouteEngine routes,
    ProtectionRadio radio,
    TrainRadio trainRadio,
    DispatcherProtection dispatcherProtection,
    RadioPublisher radioPublisher,
    RadioPhones radioPhones,
    CrewRelay relay,
    OnboardAudio onboard,
    ServerOptions options,
    CrewRoster crew,
    RunCurves runCurves,
    ILogger<GameHub> logger) : Hub
{
    [HubMethodName(Protocol.Join)]
    public JoinResult Join(JoinRequest request)
    {
        if (request.ProtocolVersion != Protocol.Version)
        {
            return new JoinResult
            {
                Message = $"クライアントの版が違います（サーバー: {Protocol.Version}、クライアント: {request.ProtocolVersion}）。最新のクライアントを使ってください"
            };
        }

        if (!PasswordMatches(request.Password))
            return new JoinResult { Message = "合言葉が違います" };

        // 乗務員のログインが要る（社員番号とパスワードでログインして受け取った合い札）
        if (crew.UserOfClientToken(request.CrewToken) is not { } crewId)
            return new JoinResult { Message = "ログインが切れました。クライアントを起動し直して、社員番号とパスワードでログインしてください" };
        crew.ConfirmToken(request.CrewToken); // 合言葉が合ったので、クライアントの仮の合い札を本物にする（タブレットなどでも使えるようになる）

        // 司令員は在線として数えない（TRAIN CREW なしで参加し、列車無線と区間防護を扱う）。役職がなければ断る
        if (request.Role == CrewRole.Dispatcher)
        {
            if (!crew.HasRole(crewId, CrewRoster.DispatcherRole))
                return new JoinResult { Message = "司令員の役職がありません。勤怠管理で「司令員」を振ってもらってください" };
            if (!trainRadio.IsDispatcher(Context.ConnectionId))
            {
                trainRadio.AddDispatcher(Context.ConnectionId, crewId);
                logger.LogInformation("参加 {ConnectionId} ID:{CrewId}（司令員）", Context.ConnectionId, crewId);
            }

            broadcaster.Trigger();
            _ = PublishRadioStateAsync();
            return new JoinResult { Ok = true, Crossings = data.Crossings.ToList(), Onboard = true };
        }

        // 車掌は在線として数えない（同じ列番の運転士と同じ進路・信号を受け取るだけ）
        if (request.Role == CrewRole.Conductor)
        {
            if (!relay.IsConductor(Context.ConnectionId))
            {
                relay.AddConductor(Context.ConnectionId, crewId);
                logger.LogInformation("参加 {ConnectionId}（車掌）", Context.ConnectionId);
            }

            broadcaster.Trigger();
            return new JoinResult { Ok = true, Crossings = data.Crossings.ToList(), Onboard = true };
        }

        // 添乗も在線として数えない（同じ列番の運転士と同じ進路・信号を受け取り、運転士の操作を映すだけ）
        if (request.Role == CrewRole.Rider)
        {
            if (!relay.IsRider(Context.ConnectionId))
            {
                relay.AddRider(Context.ConnectionId, crewId, request.Passenger);
                logger.LogInformation("参加 {ConnectionId}（添乗{Passenger}）", Context.ConnectionId, request.Passenger ? "・乗客" : "");
            }

            broadcaster.Trigger();
            return new JoinResult { Ok = true, Crossings = data.Crossings.ToList(), Onboard = true };
        }

        var first = !players.IsJoined(Context.ConnectionId);
        if (first)
        {
            players.Add(Context.ConnectionId, request.Mode, crewId);
            logger.LogInformation("参加 {ConnectionId} ID:{CrewId}（{Mode}・{Count}人）", Context.ConnectionId, crewId, request.Mode, players.Count);
        }

        broadcaster.Trigger();
        return new JoinResult { Ok = true, Crossings = data.Crossings.ToList(), Onboard = true };
    }

    [HubMethodName(Protocol.Report)]
    public bool Report(ClientReport report)
    {
        // 管理画面・ダイヤグラムの設定画面から強制切断された参加者は、ここで接続を切る
        if (players.IsKicked(Context.ConnectionId))
        {
            logger.LogInformation("強制切断 {ConnectionId}", Context.ConnectionId);
            Context.Abort();
            return false;
        }

        if (trainRadio.IsDispatcher(Context.ConnectionId)) return true; // 司令員の報告は、つながっている合図だけ

        if (relay.IsConductor(Context.ConnectionId))
        {
            relay.UpdateConductor(Context.ConnectionId, report);
            // 自動運転の車掌（同じ列番に運転士がいないとき）: その間だけ運転士と同じに在線に数える（進路・信号・自動運転列車の引き継ぎ）
            var conn = Context.ConnectionId;
            var train = (report.TrainNumber ?? "").Trim();
            var drives = report.AutoDriving && train.Length > 0 && !players.SnapshotWithoutVirtualTrains().Any(p => p.ConnectionId != conn && p.TrainNumber == train);
            if (drives)
            {
                if (!players.IsJoined(conn))
                {
                    players.Add(conn, ShareMode.Shared, relay.ConductorCrew(conn) ?? "");
                    logger.LogInformation("自動運転 {ConnectionId}（車掌が {Train} を運転）", conn, train);
                }
                players.ApplyReport(conn, report);
                broadcaster.Trigger();
            }
            else if (players.IsJoined(conn))
            {
                players.Remove(conn);
                radio.Remove(conn);
                logger.LogInformation("自動運転おわり {ConnectionId}（車掌に戻る）", conn);
                broadcaster.Trigger();
            }
            return true;
        }

        if (relay.IsRider(Context.ConnectionId))
        {
            relay.UpdateRider(Context.ConnectionId, report);
            return true;
        }

        return players.ApplyReport(Context.ConnectionId, report);
    }

    /// <summary>防護無線の発報・解除（トグル）。押した時点の位置を一緒に受け取り、その位置で発報する。受け付けたら true。</summary>
    [HubMethodName(Protocol.ToggleProtectionRadio)]
    public bool ToggleProtectionRadio(ClientReport report)
    {
        if (!players.IsJoined(Context.ConnectionId)) return false;
        if (report != null) players.ApplyReport(Context.ConnectionId, report);
        var accepted = radio.Toggle(Context.ConnectionId);
        broadcaster.Trigger();
        return accepted;
    }

    /// <summary>復帰ボタン。自分のいる区間で作動中の特発（理由が表示されているもの）だけを解除する。</summary>
    [HubMethodName(Protocol.Restore)]
    public RestoreResult Restore()
    {
        var p = players.Get(Context.ConnectionId);
        if (p == null) return new RestoreResult { Message = "サーバーに参加していません" };

        var targets = incidents.Snapshot().Where(i => data.IsNear(i.Crossing, p.TrackCircuits, p.Zones)).Select(i => i.Crossing).ToList();
        if (targets.Count == 0) return new RestoreResult { Message = "この区間に、作動中の特発はありません" };

        var who = string.IsNullOrEmpty(p.TrainNumber) ? "列番未設定" : $"列番 {p.TrainNumber}";
        var released = incidents.Release(targets, who);
        return new RestoreResult
        {
            Released = released.Count,
            Message = released.Count > 0 ? $"復帰しました: {string.Join("、", released.Select(r => r.Crossing))}" : "すでに復帰されています"
        };
    }

    /// <summary>出発要求ボタン（在線共有モード）。</summary>
    /// <summary>ランカーブ（1駅間の走り方。gzip した JSON）を受け取って、日ごとのファイルに足す。運転士と車掌（自動運転）だけ。</summary>
    [HubMethodName(Protocol.UploadRunCurve)]
    public bool UploadRunCurve(byte[] data)
    {
        var crewId = players.Get(Context.ConnectionId)?.CrewId is { Length: > 0 } id ? id : relay.ConductorCrew(Context.ConnectionId);
        return crewId is { Length: > 0 } && runCurves.Add(crewId, data);
    }

    [HubMethodName(Protocol.RequestDeparture)]
    public string RequestDeparture()
    {
        // 車掌・添乗は出せない（自動運転の車掌は運転士と同じに在線に数えているので出せる）
        if ((relay.IsConductor(Context.ConnectionId) && !players.IsJoined(Context.ConnectionId)) || relay.IsRider(Context.ConnectionId)) return "出発要求は運転士が出します";
        var message = routes.RequestDeparture(Context.ConnectionId);
        broadcaster.Trigger();
        return message;
    }

    /// <summary>入換で動かすボタン（X線入換・X線停車中。在線共有モード）。</summary>
    [HubMethodName(Protocol.ShuntMove)]
    public string ShuntMove()
    {
        if (relay.IsConductor(Context.ConnectionId) || relay.IsRider(Context.ConnectionId)) return "入換で動かすのは運転士が扱います";
        var message = players.PressShunt(Context.ConnectionId);
        broadcaster.Trigger();
        return message;
    }

    /// <summary>入換で動かすのを取り消す（取消ボタン。在線共有モード）。</summary>
    [HubMethodName(Protocol.ShuntCancel)]
    public string ShuntCancel()
    {
        if (relay.IsConductor(Context.ConnectionId) || relay.IsRider(Context.ConnectionId)) return "入換で動かすのは運転士が扱います";
        var message = players.CancelShunt(Context.ConnectionId);
        broadcaster.Trigger();
        return message;
    }

    /// <summary>列車番号の確定（在線共有モード）。ダイヤにある列車か調べる。</summary>
    [HubMethodName(Protocol.CheckTrain)]
    public TrainCheckResult CheckTrain(string train) => routes.CheckTrain(train ?? "");

    /// <summary>TRAIN CREW の SetRoute の結果（在線共有モード）。</summary>
    [HubMethodName(Protocol.RouteResult)]
    public void RouteResult(string message)
    {
        // 車掌・添乗のゲームの返事は、運転士の進路の判断に混ぜない（自動運転の車掌は運転士と同じに数えているので混ぜる）
        if ((relay.IsConductor(Context.ConnectionId) && !players.IsJoined(Context.ConnectionId)) || relay.IsRider(Context.ConnectionId)) return;
        if (!string.IsNullOrEmpty(message) && message.Length < 200) routes.ReportResult(Context.ConnectionId, message);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var wasRadioMember = trainRadio.IsDispatcher(Context.ConnectionId) || relay.IsConductor(Context.ConnectionId) || relay.IsRider(Context.ConnectionId)
                             || players.IsJoined(Context.ConnectionId);
        dispatcherProtection.Remove(Context.ConnectionId);
        trainRadio.Remove(Context.ConnectionId);
        onboard.Remove(Context.ConnectionId);
        relay.RemoveConductor(Context.ConnectionId);
        relay.RemoveRider(Context.ConnectionId);
        if (players.IsJoined(Context.ConnectionId))
        {
            players.Remove(Context.ConnectionId);
            radio.Remove(Context.ConnectionId);
            broadcaster.Trigger();
            logger.LogInformation("退出 {ConnectionId}（{Count}人）", Context.ConnectionId, players.Count);
        }

        if (wasRadioMember)
        {
            broadcaster.Trigger();
            _ = PublishRadioStateAsync();
            _ = onboard.PublishAsync();
        }

        return base.OnDisconnectedAsync(exception);
    }

    // ---- 列車無線 ----

    /// <summary>列車無線の状態を、すぐ全員に送る（配信の0.5秒を待たない）。</summary>
    private Task PublishRadioStateAsync() => radioPublisher.PublishAsync();

    private async Task<TrainRadioResult> RadioAsync(TrainRadioResult result)
    {
        await PublishRadioStateAsync();
        return result;
    }

    [HubMethodName(Protocol.RequestRadioCall)]
    public Task<TrainRadioResult> RequestRadioCall() => RadioAsync(trainRadio.RequestCall(Context.ConnectionId));

    [HubMethodName(Protocol.CancelRadioCall)]
    public Task<TrainRadioResult> CancelRadioCall() => RadioAsync(trainRadio.CancelRequest(Context.ConnectionId));

    [HubMethodName(Protocol.AnswerRadioCall)]
    public Task<TrainRadioResult> AnswerRadioCall(long requestId, bool privateCall) => RadioAsync(trainRadio.Answer(Context.ConnectionId, requestId, privateCall));

    [HubMethodName(Protocol.StartRadioBroadcast)]
    public Task<TrainRadioResult> StartRadioBroadcast() => RadioAsync(trainRadio.StartBroadcast(Context.ConnectionId));

    [HubMethodName(Protocol.CallRadioTrain)]
    public Task<TrainRadioResult> CallRadioTrain(string targetId) => RadioAsync(trainRadio.CallTrain(Context.ConnectionId, targetId));

    [HubMethodName(Protocol.AcceptDispatcherCall)]
    public Task<TrainRadioResult> AcceptDispatcherCall(long offerId) => RadioAsync(trainRadio.AcceptOffer(Context.ConnectionId, offerId));

    [HubMethodName(Protocol.RespondRadioBroadcast)]
    public Task<TrainRadioResult> RespondRadioBroadcast(long callId) => RadioAsync(trainRadio.RespondBroadcast(Context.ConnectionId, callId));

    [HubMethodName(Protocol.CancelDispatcherCall)]
    public Task<TrainRadioResult> CancelDispatcherCall(long offerId) => RadioAsync(trainRadio.CancelOffer(Context.ConnectionId, offerId));

    [HubMethodName(Protocol.SelectRadioResponder)]
    public Task<TrainRadioResult> SelectRadioResponder(long callId, string responder) =>
        RadioAsync(trainRadio.ChangeMode(Context.ConnectionId, callId, true, responder));

    [HubMethodName(Protocol.ChangeRadioMode)]
    public Task<TrainRadioResult> ChangeRadioMode(long callId, bool privateCall) => RadioAsync(trainRadio.ChangeMode(Context.ConnectionId, callId, privateCall));

    [HubMethodName(Protocol.EndRadioCall)]
    public Task<TrainRadioResult> EndRadioCall(long callId) => RadioAsync(trainRadio.End(Context.ConnectionId, callId));

    [HubMethodName(Protocol.SendRadioAudio)]
    public async Task SendRadioAudio(TrainRadioPacket packet)
    {
        if (packet == null) return;
        if (radioPhones.HasPhone(Context.ConnectionId)) return; // スマホを受話器にしているあいだは、PC のマイクの音は送らない（声が二重にならないように）
        await radioPublisher.SendAudioAsync(trainRadio.Route(Context.ConnectionId, packet, out var toMonitors), packet, toMonitors);
    }

    // ---- 列車ごとの放送装置（車内放送・車外放送・インターホン） ----

    [HubMethodName(Protocol.SetOnboardBroadcast)]
    public TrainRadioResult SetOnboardBroadcast(OnboardChannel channel) => onboard.SetBroadcast(Context.ConnectionId, channel);

    [HubMethodName(Protocol.SetIntercom)]
    public TrainRadioResult SetIntercom(bool on) => onboard.SetIntercom(Context.ConnectionId, on);

    [HubMethodName(Protocol.SendOnboardAudio)]
    public async Task SendOnboardAudio(OnboardPacket packet)
    {
        if (packet == null) return;
        await onboard.SendAudioAsync(onboard.Route(Context.ConnectionId, packet), packet);
    }

    [HubMethodName(Protocol.SetDispatcherProtection)]
    public TrainRadioResult SetDispatcherProtection(bool active, string? firstStation, string? lastStation)
    {
        var result = dispatcherProtection.Set(Context.ConnectionId, active, firstStation, lastStation);
        broadcaster.Trigger();
        return result;
    }

    private bool PasswordMatches(string? given)
    {
        if (string.IsNullOrEmpty(options.Password)) return true;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(given ?? ""), Encoding.UTF8.GetBytes(options.Password));
    }
}
