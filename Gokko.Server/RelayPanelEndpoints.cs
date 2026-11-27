using System.Text;
using Gokko.Server.Services;

namespace Gokko.Server;

/// <summary>信号制御盤（relaypanel.html）の窓口。ログインした人は誰でも、てこ・ボタンを扱える。鍵は「信号扱い」の役職の人だけ。</summary>
public static class RelayPanelEndpoints
{
    public sealed record RelayOp(string? Op, string? Id, string? Dir);

    public static void MapRelayPanelEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/relay");

        // 盤を選ぶ画面の駅の一覧（連動装置のある駅を路線の順に。盤のデータがまだない駅は ready = false で「準備中」）
        g.MapGet("/stations", (RailData rail, GameData data, RelayPanels panels) =>
        {
            var ids = rail.Routes.Values.Select(r => r.StationId).Where(s => s != "").ToHashSet();
            var order = data.Line.StationIds.Where(ids.Contains).ToList();
            order.AddRange(ids.Except(order).Order());
            return Results.Ok(order.Select(id => new
            {
                id,
                name = panels.Get(id)?.Data.Name is { Length: > 0 } n ? n : data.Line.StationName(id),
                ready = panels.Get(id) is { Data.Screen: "" or "denshirendo" },
                screen = panels.Get(id)?.Data.Screen ?? "",   // "denshirendo" = 電子連動装置の駅（選ぶ画面は denshirendo.html を開く）
            }));
        });

        // 駅のデータ（絵の位置と連動のデータ）
        g.MapGet("/{st}/station", (string st, RelayPanels panels) =>
            panels.Get(st) is { } s ? Results.Text(s.DataJson, "application/json", Encoding.UTF8) : Results.NotFound(new { message = $"駅 {st} の盤はありません" }));

        // 盤の状態を、変わるたびに送る（Server-Sent Events）。出来事（てこ・ボタン・鍵・転換・接近）は、音を鳴らすのに使う
        g.MapGet("/{st}/events", async (string st, HttpContext ctx, RelayPanels panels) =>
        {
            if (panels.Get(st) is not { } s) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var ct = ctx.RequestAborted;
            long seq = s.CurrentSeq, sent = -1;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var changed = s.Changed;
                    var (version, lastSeq, json) = s.Read(seq);
                    if (version != sent)
                    {
                        await ctx.Response.WriteAsync("data: " + json + "\n\n", ct);
                        sent = version;
                        seq = lastSeq;
                    }
                    else await ctx.Response.WriteAsync(": ping\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    await Task.WhenAny(changed, Task.Delay(15000, ct));
                }
            }
            catch (OperationCanceledException) { }
        });

        // 操作。鍵は、ヘッダー X-Crew-Token（社員番号でログインした合い札）の人に役職があるときだけ
        g.MapPost("/{st}/op", (string st, RelayOp req, HttpRequest http, RelayPanels panels, CrewRoster crew) =>
        {
            if (panels.Get(st) is not { } s) return Results.NotFound(new { ok = false, message = $"駅 {st} の盤はありません" });
            var crewId = crew.UserOfToken(http.Headers["X-Crew-Token"].ToString());
            var keyAllowed = crewId != null && crew.HasRole(crewId, CrewRoster.RelayKeyRole);
            // 盤はログインした人だけが扱える（URL を直接開いた人が勝手に触らないように）。鍵は、さらに「信号扱い」の役職が要る
            if (crewId == null) return Results.Ok(new { ok = false, message = "盤を扱うには、社員番号でログインしてください" });
            var error = s.Operate(req.Op ?? "", req.Id ?? "", req.Dir, keyAllowed, crewId);
            return Results.Ok(new { ok = error == null, message = error });
        });
    }
}
