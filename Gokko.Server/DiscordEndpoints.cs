using Gokko.Server.Services;

namespace Gokko.Server;

/// <summary>
/// 乗務員の Discord ログイン（/api/auth/discord/...）。流れ:
/// 1. 画面かクライアントが begin を呼び、返ってきた URL（Discord のページ）を開く。紐づけのときは X-Crew-Token も付ける
/// 2. 本人が Discord で許可すると、Discord が callback に戻す。サーバーが Discord のユーザー ID を読み、HMAC にして名簿と照らす
/// 3. 画面には、もとの画面#discord=引き継ぎ札（30秒・1回だけ。合い札に替える）で戻す。クライアントは poll で結果を取りに来る
///    まだ紐づいていない Discord なら discord.html へ。社員番号とパスワードが分かる人は、その場で紐づける（承認なし）。
///    はじめての人は申請する（勤怠管理で承認すると名簿に入る）。クライアントは、そのあいだ poll で待つ
/// </summary>
public static class DiscordEndpoints
{
    public sealed record BeginRequest(string? Mode, string? ReturnTo, bool? Poll, string? Passphrase = null);
    public sealed record PollRequest(string? Code);
    public sealed record LinkRequest(string? Ticket, string? Id, string? Password);
    public sealed record ApplyRequest(string? Ticket, string? Name, int? JoinYear, string? Depot, string? ExistingId);

    /// <summary>戻る画面は、このサーバーの中のパスだけ（ほかのサイトへ飛ばさない）。</summary>
    private static string SafeReturn(string? r) =>
        r is { Length: > 1 and <= 200 } && r[0] == '/' && r[1] != '/' && r[1] != '\\' && !r.Contains('#') && !r.Any(char.IsControl) ? r : "/menu.html";

    private static string Page(string fragment) => "/discord.html#" + fragment;

    private static string Msg(string message, string returnTo) =>
        Page("msg=" + Uri.EscapeDataString(message) + "&return=" + Uri.EscapeDataString(returnTo));

    public static void MapDiscordEndpoints(this WebApplication app, ServerOptions options)
    {
        var g = app.MapGroup("/api/auth/discord");
        IResult Expired() => Results.Json(new { message = "申請の画面の期限が切れました（30分）。もう一度 Discord でログインしてください" }, statusCode: 404);
        IResult Off() =>Results.Json(new { message = "Discord ログインは、このサーバーでは使えません" }, statusCode: 503);

        g.MapGet("", (CrewRoster crew) => Results.Ok(new
        {
            enabled = options.Discord.Enabled, passwordUntil = options.Discord.PasswordUntil, passwordClosed = crew.PasswordClosed,
            passphrase = !string.IsNullOrEmpty(options.Password), // ログインに参加の合言葉が要るか
        }));

        g.MapPost("/begin", (HttpRequest http, BeginRequest req, DiscordAuth discord, CrewRoster crew) =>
        {
            if (!discord.Enabled) return Off();
            var link = req.Mode == "link";
            // ログインには参加の合言葉も要る（紐づけは、もうログインしている本人なので要らない）。
            // PC のクライアント（poll）は合言葉なしで入れるが、仮の合い札になる（合言葉を入れてサーバーに接続したら本物になる）
            if (!link && req.Poll != true && !options.PassphraseMatches(req.Passphrase)) return Results.BadRequest(new { message = "合言葉が違います" });
            string? user = null;
            if (link)
            {
                var given = http.Headers["X-Crew-Token"].ToString();
                // クライアント（poll）は、パスワードでログインした直後（仮の合い札）にも紐づけを勧める
                if ((req.Poll == true ? crew.UserOfClientToken(given) : crew.UserOfToken(given)) is not { } id) return Results.Json(new { message = "ログインしてください" }, statusCode: 401);
                if (crew.InfoOf(id)?.Discord == true) return Results.BadRequest(new { message = "もう Discord と紐づけてあります" });
                user = id;
            }

            var (url, poll) = discord.Begin(link ? DiscordAuth.Mode.Link : DiscordAuth.Mode.Login, user, SafeReturn(req.ReturnTo), req.Poll == true);
            return url == null ? Results.Json(new { message = "混み合っています。しばらくしてからにしてください" }, statusCode: 503) : Results.Ok(new { url, poll });
        });

        // Discord から戻ってくるところ（Developer Portal の Redirects に登録する URL）
        g.MapGet("/callback", async (string? code, string? state, string? error, DiscordAuth discord, CrewRoster crew, ILogger<DiscordAuth> log) =>
        {
            if (!discord.Enabled) return Off();
            var p = discord.TakeState(state);
            if (p == null) return Results.Redirect(Msg("この画面は使えません（10分で切れ、1回しか使えません）。もう一度、はじめからやり直してください", "/menu.html"));

            string? Done(string status, string? id = null, string? token = null, string? message = null)
            {
                if (p.Poll != null) discord.SetPoll(p.Poll, status, id, token, message);
                return p.Poll != null && status is "ok" or "linked" ? Msg((message ?? "") + "。この画面を閉じて、クライアントに戻ってください", "") : null;
            }

            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            {
                const string m = "Discord で許可されませんでした";
                Done("error", message: m);
                return Results.Redirect(Msg(m, p.ReturnTo));
            }

            if (await discord.Identify(code) is not { } me)
            {
                const string m = "Discord から本人を確かめられませんでした。もう一度やり直してください";
                Done("error", message: m);
                return Results.Redirect(Msg(m, p.ReturnTo));
            }

            var hash = discord.HashId(me.Id);
            if (p.Mode == DiscordAuth.Mode.Link)
            {
                if (crew.LinkDiscord(p.LinkUser!, hash) is { } err)
                {
                    Done("error", message: err);
                    return Results.Redirect(Msg(err, p.ReturnTo));
                }

                log.LogInformation("Discord: ID:{Id} を紐づけました", p.LinkUser);
                return Results.Redirect(Done("linked", p.LinkUser, message: $"ID:{p.LinkUser} を Discord と紐づけました") ?? p.ReturnTo + "#discordLinked=1");
            }

            var (uid, token) = crew.LoginByDiscord(hash, provisional: p.Poll != null);
            if (uid != null)
            {
                if (Done("ok", uid, token, $"ID:{uid} でログインしました") is { } page) return Results.Redirect(page);
                var handoff = crew.IssueHandoff(token);
                return Results.Redirect(p.ReturnTo + "#discord=" + handoff);
            }

            if (crew.HasApplication(hash))
            {
                const string m = "申請は受け付けています。勤怠管理の担当者が承認するまで、お待ちください";
                Done("error", message: m);
                return Results.Redirect(Msg(m, p.ReturnTo));
            }

            // 名簿にいない人: 申請の画面へ（社員番号とパスワードが分かる人は、その場で紐づけられる。クライアントはそのあいだ待つ）
            var ticket = discord.IssueTicket(hash, me.Name, p.Poll, p.ReturnTo);
            return Results.Redirect(Page("apply=" + ticket));
        });

        // PC のクライアントが結果を取りに来る（2秒ごとくらい）
        g.MapPost("/poll", (PollRequest req, DiscordAuth discord) =>
            discord.TakePoll(req.Code) is { } r
                ? Results.Ok(new { status = r.Status, id = r.Id, token = r.Token, message = r.Message })
                : Results.Json(new { status = "error", message = "時間が切れました。もう一度やり直してください" }, statusCode: 404));

        // 申請（名簿にいない人）
        g.MapGet("/apply", (string? ticket, DiscordAuth discord, CrewRoster crew) =>
            discord.PeekTicket(ticket) is { } t
                ? Results.Ok(new
                {
                    discordName = t.DiscordName, depots = CrewRoster.Depots, joinYearFrom = CrewRoster.JoinYears.From, joinYearTo = CrewRoster.JoinYears.To,
                    passwordOpen = !crew.PasswordClosed, // 社員番号とパスワードで、その場で紐づけられるか
                    client = t.Poll != null, returnTo = t.Poll != null ? null : t.ReturnTo
                })
                : Expired());
        g.MapPost("/apply", (ApplyRequest req, DiscordAuth discord, CrewRoster crew, ILogger<DiscordAuth> log) =>
        {
            if (discord.PeekTicket(req.Ticket) is not { } t) return Expired();
            if (crew.Apply(t.Hash, t.DiscordName, req.Name, req.JoinYear ?? 0, req.Depot, req.ExistingId) is { } err) return Results.BadRequest(new { message = err });
            discord.DropTicket(req.Ticket!);
            const string m = "申請しました。勤怠管理の担当者が承認したら、Discord でログインできます";
            if (t.Poll != null) discord.SetPoll(t.Poll, "apply", message: m);
            log.LogInformation("Discord: 申請がありました");
            return Results.Ok(new { message = m });
        });
        // 社員番号とパスワードが分かる人は、承認なしで、その場で Discord と紐づける（パスワードでのログインの期限まで）
        g.MapPost("/apply/link", (LinkRequest req, DiscordAuth discord, CrewRoster crew, ILogger<DiscordAuth> log) =>
        {
            if (discord.PeekTicket(req.Ticket) is not { } t) return Expired();
            var (id, token, error) = crew.Login(req.Id, req.Password, provisional: t.Poll != null); // クライアントから来た人は仮の合い札
            if (error != null) return Results.BadRequest(new { message = error });
            if (crew.LinkDiscord(id!, t.Hash) is { } err)
            {
                crew.Logout(token);
                return Results.BadRequest(new { message = err });
            }

            discord.DropTicket(req.Ticket!);
            log.LogInformation("Discord: ID:{Id} を紐づけました（申請の画面から）", id);
            var m = $"ID:{id} を Discord と紐づけました。次からは［Discord でログイン］で入れます";
            if (t.Poll != null)
            {
                discord.SetPoll(t.Poll, "ok", id, token, m);
                return Results.Ok(new { message = m + "。この画面を閉じて、クライアントに戻ってください" });
            }

            // ブラウザの画面: もとの画面へ、ログインして戻る
            return Results.Ok(new { message = m, next = t.ReturnTo + "#discord=" + crew.IssueHandoff(token) });
        });
    }
}
