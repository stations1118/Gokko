using Gokko.Server.Services;

namespace Gokko.Server;

/// <summary>特発臨（司令用管理画面の「特発臨」の欄）の窓口。ダイヤグラムの操作と同じ入口（/api/dia・/api/admin・/api/dispatch）に置く。</summary>
public static class TokuhatsuRinjiEndpoints
{
    public sealed record CancelRequest(string? Number);

    public static void MapTokuhatsuRinji(RouteGroupBuilder g)
    {
        // 立てられる列車（駅の番線にいる列車）・立てた特発臨・行先にできる駅・種別
        g.MapGet("/rinji", (TokuhatsuRinji rinji, RailData rail) => Results.Ok(new
        {
            candidates = rinji.Candidates(),
            list = rinji.View(),
            depots = rinji.DepotView(), // 出庫できる車庫
            types = TokuhatsuRinji.Types,
            stations = rail.ControlStations.Select(s => new { id = s.Id, name = s.Name, zone = rail.Line.ZoneOfStation(s.Id) })
        }));

        // スジの案（標準運転時分で時刻を引く）
        g.MapPost("/rinji/draft", (TokuhatsuRinji.Request req, TokuhatsuRinji rinji) =>
        {
            var (plan, error) = rinji.Draft(req);
            return error != null ? Results.BadRequest(new { message = error }) : Results.Ok(plan);
        });

        // 立てる
        g.MapPost("/rinji", (TokuhatsuRinji.Request req, TokuhatsuRinji rinji) =>
            rinji.Create(req) is { } error ? Results.BadRequest(new { message = error }) : Results.Ok(new { ok = true }));

        // 取り消す
        g.MapPost("/rinji/cancel", (CancelRequest req, TokuhatsuRinji rinji) =>
            rinji.Cancel(req.Number) is { } error ? Results.BadRequest(new { message = error }) : Results.Ok(new { ok = true }));
    }
}
