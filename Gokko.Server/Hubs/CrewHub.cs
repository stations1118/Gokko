using System.Security.Cryptography;
using System.Text;
using Gokko.Common;
using Gokko.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace Gokko.Server.Hubs;

/// <summary>運転士と車掌の間の中継（/crew）。配信（/hub）とは別の接続。メソッド名は CrewProtocol と一致させる。</summary>
public sealed class CrewHub(CrewRelay relay, CrewRoster crew, ServerOptions options) : Hub
{
    private const string JoinedKey = "joined";

    [HubMethodName(CrewProtocol.Join)]
    public async Task<JoinResult> Join(CrewJoinRequest request)
    {
        if (request.ProtocolVersion != Protocol.Version)
            return new JoinResult { Message = $"クライアントの版が違います（サーバー: {Protocol.Version}、クライアント: {request.ProtocolVersion}）" };
        if (!PasswordMatches(request.Password)) return new JoinResult { Message = "合言葉が違います" };
        // 社員番号とユーザー名（運転士に「○○さんが乗務します」と出すため）
        if (crew.InfoOf(crew.UserOfClientToken(request.CrewToken)) is not { } me)
            return new JoinResult { Message = "ログインが切れました。クライアントを起動し直して、社員番号とパスワードでログインしてください" };
        crew.ConfirmToken(request.CrewToken); // 合言葉が合ったので、クライアントの仮の合い札を本物にする
        Context.Items[JoinedKey] = true;
        var rejected = await relay.JoinAsync(Context.ConnectionId, request, me.Id, me.Name);
        // 入った本人にも今の顔ぶれを返す
        var train = relay.TrainOf(Context.ConnectionId) ?? "";
        await Clients.Caller.SendAsync(CrewProtocol.ReceiveStatus, relay.StatusOf(train));
        return rejected == null ? new JoinResult { Ok = true } : new JoinResult { Message = rejected };
    }

    [HubMethodName(CrewProtocol.SendDriverInput)]
    public Task SendDriverInput(DriverInput input) =>
        Joined && input != null ? relay.FromDriverAsync(Context.ConnectionId, input) : Task.CompletedTask;

    [HubMethodName(CrewProtocol.SendConductorInput)]
    public Task SendConductorInput(ConductorInput input) =>
        Joined && input != null ? relay.FromConductorAsync(Context.ConnectionId, input) : Task.CompletedTask;

    [HubMethodName(CrewProtocol.SetConductorAllowed)]
    public Task SetConductorAllowed(bool allow) =>
        Joined ? relay.SetAllowAsync(Context.ConnectionId, allow) : Task.CompletedTask;

    [HubMethodName(CrewProtocol.AnswerConductor)]
    public Task AnswerConductor(ConductorAnswer answer) =>
        Joined && answer != null ? relay.AnswerAsync(Context.ConnectionId, answer) : Task.CompletedTask;

    /// <summary>中継との往復の時間を測る（クライアントの「通信状況」）。</summary>
    [HubMethodName(CrewProtocol.Ping)]
    public bool Ping() => true;

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await relay.LeaveAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    private bool Joined => Context.Items.ContainsKey(JoinedKey);

    private bool PasswordMatches(string? given)
    {
        if (string.IsNullOrEmpty(options.Password)) return true;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(given ?? ""), Encoding.UTF8.GetBytes(options.Password));
    }
}
