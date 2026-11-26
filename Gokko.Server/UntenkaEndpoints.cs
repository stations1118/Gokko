using Gokko.Server.Services;

namespace Gokko.Server;

/// <summary>
/// 運転課の画面（untenka.html）の窓口。催物用の臨時列車を作り、申請・承認する。
/// ヘッダー X-Crew-Token にログインの合い札（乗務タブレットと同じログイン）。ログインした乗務員なら誰でも作って申請でき、
/// 承認・差戻しは役職「運転課課長代理」の人だけ。適用・取消は司令用管理画面（/api/dia/temptrains。AdminEndpoints と同じ鍵）
/// </summary>
public static class UntenkaEndpoints
{
    public sealed record SaveRequest(string? Id, string? Name, List<TempTrains.TrainSpec>? Trains);
    public sealed record CheckRequest(string? Id, List<TempTrains.TrainSpec>? Trains);
    public sealed record ActRequest(string? Id, string? Action, string? Comment);
    public sealed record UnyoRunRequest(string? Id, string? Unyo, bool On);

    public static void MapUntenkaEndpoints(this WebApplication app, RouteGroupBuilder dia)
    {
        var g = app.MapGroup("/api/untenka").AddEndpointFilter(async (ctx, next) =>
        {
            var crew = ctx.HttpContext.RequestServices.GetRequiredService<CrewRoster>();
            var token = ctx.HttpContext.Request.Headers["X-Crew-Token"].ToString();
            if (crew.UserOfToken(token) is null)
                return Results.Json(new { message = token.Length > 0 ? "ログインが切れました。もう一度ログインしてください" : "ログインしてください", login = true }, statusCode: 401);
            return await next(ctx);
        });

        static string Me(HttpContext ctx, CrewRoster crew) => crew.UserOfToken(ctx.Request.Headers["X-Crew-Token"].ToString())!;

        // 入力に使う決まりごと（駅・番線・種別・標準運転時分）。画面を開いたときに1回だけ読む
        g.MapGet("/base", (HttpContext ctx, CrewRoster crew, RailData rail, TempTrains temp) =>
        {
            var id = Me(ctx, crew);
            var downInc = temp.DownIncreasing();
            // 駅ごとの番線: 所定のダイヤで使っている番線と、向き・停車/通過ごとにいちばん多い番線
            var uses = new List<(string St, string Key, int Track)>();
            foreach (var d in rail.BaseDia.Values)
            {
                if (RouteEngine.TrainInfo(d.Number) is not { } info) continue;
                foreach (var s in d.Stops)
                    if (s.StationId != null && s.Track is { } tr)
                        uses.Add((s.StationId, (info.Dir == Dir.Down ? "下" : "上") + (s.Stop == "通" ? "通" : "停"), tr));
            }
            var byStation = uses.ToLookup(u => u.St);
            // 駅ごとの所定停車の種別: その駅を通るその種別の列車のうち、半分以上が止まる種別（種別を選んだときに停車のチェックを入れる）
            var stopTypes = rail.BaseDia.Values
                .SelectMany(d => d.Stops.Skip(1).Take(d.Stops.Count - 2) // 始発・終点は数えない（検車区で終わる列車などで、ふだん止まらない駅が「停車」にならないように）
                    .Where(s => s.StationId != null).Select(s => (St: s.StationId!, d.Type, Stop: s.Stop == "停")))
                .GroupBy(x => (x.St, x.Type))
                .Where(g => g.Count(x => x.Stop) * 2 >= g.Count())
                .ToLookup(g => g.Key.St, g => g.Key.Type);
            // 駅は、路線の外（大路〜三郷の手前など、時刻表に載っている駅）も含めた並び
            var stations = temp.Stations.Select(st => new
            {
                id = st.Id,
                name = st.Name,
                zone = st.Zone,
                control = st.Control,
                tracks = byStation[st.Id].Select(u => u.Track)
                    .Concat(rail.Stations.TryGetValue(st.Id, out var def) ? def.TrackCircuit.Keys : Enumerable.Empty<int>()).Distinct().Order().ToList(),
                def = byStation[st.Id].GroupBy(u => u.Key).ToDictionary(x => x.Key, x => x.GroupBy(u => u.Track).OrderByDescending(y => y.Count()).First().Key),
                stopTypes = stopTypes[st.Id].ToList(),
                // 向きごとに入れる番線（進路のデータから。運転整理用のダイヤグラムの番線の選び方と同じ材料）。
                // arr: 場内で入れる番線、dep: 出発できる番線、turn: 行き止まり（打ち切りに使える）、through: 番線を使わずに通過できるか
                rules = rail.Stations.TryGetValue(st.Id, out var sd) && sd.RouteControl
                    ? new[] { Dir.Down, Dir.Up }.ToDictionary(d => d == Dir.Down ? "下り" : "上り", d => new
                    {
                        arr = sd.Routes[d].Arrive.Keys.Order().ToList(),
                        dep = sd.Routes[d].Depart.Keys.Order().ToList(),
                        turn = sd.Routes[d].Turnback.Order().ToList(),
                        through = sd.Routes[d].Through.Count > 0,
                        // 終点で入換するとき: 着いた番線（from）から入れる線（label）。その線から出る向きの入換で入れる番線は shunts
                        shuntOuts = sd.Routes[d].ShuntOuts.Select(o => new { from = o.From, label = o.Label }).Distinct().ToList(),
                        shunts = sd.Routes[d].Shunts.Select(s => new { from = s.FromLabel, to = s.To }).Distinct().ToList(),
                        // この向きの列車が、増結車のいる番線へ入って連結できる番線
                        couple = sd.Routes[d].Arrive.Keys.Where(k => RouteEngine.CanCoupleAt(rail, st.Id, d, k)).Order().ToList()
                    })
                    : null
            }).ToList();
            var types = TempTrains.Types.Select(t => new
            {
                type = t.Type,
                suffix = t.Suffix,
                labels = rail.BaseDia.Values.Where(d => d.Type == t.Type && d.TypeLabel.Length > 0).GroupBy(d => d.TypeLabel)
                    .OrderByDescending(x => x.Count()).Select(x => x.Key).ToList()
            });
            var runTimes = rail.RunTimes.Select(r => new
            {
                type = r.Type, dir = r.Dir == Dir.Down ? "下り" : "上り", from = temp.StationIdByName(r.From), to = temp.StationIdByName(r.To),
                fromStop = r.FromStops, toStop = r.ToStops, seconds = r.Seconds
            }).Where(r => r.from != null && r.to != null);
            return Results.Ok(new
            {
                me = new { id, name = crew.InfoOf(id)?.Name ?? "", approver = crew.HasRole(id, CrewRoster.PlanningRole) },
                downIncreasing = downInc,
                stations, types, runTimes,
                unyoUsed = rail.BaseDia.Values.Select(d => d.Unyo).Where(u => u != null).Distinct().Order(Gokko.Common.Unyo.Comparer)
            });
        });

        // ダイヤグラムに薄く引く、所定の時刻表の線（所定のダイヤは変わらないので、最初に1回だけ作って使い回す）。
        // y はゾーン、時刻は "HH:mm:ss"。singles は単線区間（ゾーンの組）
        object? planCache = null;
        g.MapGet("/plan", (RailData rail, TempTrains temp) => Results.Ok(planCache ??= new
        {
            singles = rail.SingleTrackSections.Select(p => new[] { rail.Line.ZoneOfStation(p.Item1), rail.Line.ZoneOfStation(p.Item2) }).Where(z => z[0] >= 0 && z[1] >= 0).ToList(),
            // 駅間の最速タイム（駅 ID の組 → 秒。所定の時刻表でいちばん短い運転時間）。◆をずらすとき、これより短くしない
            minRuns = temp.MinRuns.Select(x => new { from = x.Key.From, to = x.Key.To, sec = x.Value }).ToList(),
            trains = rail.BaseDia.Values.Select(t => new
            {
                train = t.Number,
                type = t.Type,
                trial = RailData.IsTrial(t), // 試運転（画面では「試運転を表示」のときだけ出す）
                turn = t.Turn is { Kind: "折返", Number: { } tn } ? tn : t.Next?.Number, // 運行のつなぎ（折り返し・途中で列番が変わる列車）
                // 入換して折り返す列車: 入換の時刻と入る線（引上線などの名前。null なら後運用の発車の番線へそのまま移る）。図で入換先の段に線を引く
                shunt = t.Turn is { Kind: "折返", How: "入換" } st ? new { time = st.Time, via = st.Via ?? DefaultVia(rail, t) } : null,
                // 路線の外の駅も、駅の並びにあれば描く（描ける端まで）
                stops = t.Stops.Where(s => temp.ZoneOf(s.StationId) != null && (s.Arr != null || s.Dep != null))
                    .Select(s => new { y = temp.ZoneOf(s.StationId)!.Value, st = s.StationId, stop = s.Stop, arr = s.Arr, dep = s.Dep, track = s.Track }).ToList()
            }).Where(t => t.stops.Count >= 2).ToList()
        }));

        g.MapGet("/groups", (TempTrains temp) => Results.Ok(new { groups = temp.Groups().OrderBy(x => x.Id.Length).ThenBy(x => x.Id) }));

        g.MapPost("/check", (CheckRequest req, TempTrains temp) =>
        {
            var (errors, warnings) = temp.Check(req.Trains?.Select(Copy).ToList() ?? new(), req.Id);
            return Results.Ok(new { errors, warnings });
        });

        g.MapPost("/groups", (SaveRequest req, HttpContext ctx, CrewRoster crew, TempTrains temp) =>
        {
            var me = Me(ctx, crew);
            var (id, error, warnings) = temp.Save(req.Id, req.Name ?? "", req.Trains?.Select(Copy).ToList() ?? new(), me, crew.InfoOf(me)?.Name ?? "");
            return error != null ? Results.BadRequest(new { ok = false, message = error, warnings }) : Results.Ok(new { ok = true, id, warnings });
        });

        g.MapPost("/act", (ActRequest req, HttpContext ctx, CrewRoster crew, TempTrains temp, IncidentManager log) =>
        {
            var me = Me(ctx, crew);
            var name = temp.Get(req.Id)?.Name;
            var error = temp.Act(req.Id, req.Action ?? "", me, crew.HasRole(me, CrewRoster.PlanningRole), req.Comment);
            if (error != null) return Results.BadRequest(new { ok = false, message = error });
            var word = req.Action switch { "submit" => "申請", "withdraw" => "取り下げ", "approve" => "承認", "reject" => "差戻し", "delete" => "削除", _ => req.Action };
            log.Note($"運転課: 臨時列車「{name}」を{word}しました（ID:{me}）");
            return Results.Ok(new { ok = true });
        });

        // ---- 司令用管理画面: 承認したグループの適用・取消 ----
        dia.MapGet("/temptrains", (TempTrains temp) => Results.Ok(new
        {
            groups = temp.Groups().Where(x => x.Status == TempTrains.Approved || x.Applied).Select(x => new
            {
                id = x.Id, name = x.Name, applied = x.Applied, owner = x.OwnerName, approvedBy = x.ApprovedBy, off = x.OffUnyo,
                trains = x.Trains.Select(t => new
                {
                    unyo = t.Unyo, number = t.Number, type = t.Type, dest = t.Dest,
                    from = t.Stops.FirstOrDefault()?.St is { } f ? temp.StationName(f) : "", dep = t.Stops.FirstOrDefault()?.Dep,
                    to = t.Stops.LastOrDefault()?.St is { } l ? temp.StationName(l) : "", arr = t.Stops.LastOrDefault()?.Arr ?? t.Stops.LastOrDefault()?.Dep
                })
            })
        }));
        dia.MapPost("/temptrains", (ActRequest req, TempTrains temp, IncidentManager log) =>
        {
            var on = req.Action != "cancel";
            var name = temp.Get(req.Id)?.Name;
            var error = temp.Apply(req.Id, on);
            if (error != null) return Results.BadRequest(new { ok = false, message = error });
            log.Note($"司令用管理画面: 催物用臨時列車「{name}」を{(on ? "適用" : "取消")}しました");
            return Results.Ok(new { ok = true });
        });
        // グループの運行番号ごとに、走らせる・走らせない
        dia.MapPost("/temptrains/unyo", (UnyoRunRequest req, TempTrains temp, IncidentManager log) =>
        {
            var name = temp.Get(req.Id)?.Name;
            var error = temp.SetUnyo(req.Id, req.Unyo, req.On);
            if (error != null) return Results.BadRequest(new { ok = false, message = error });
            log.Note($"司令用管理画面: 催物用臨時列車「{name}」の運行 {req.Unyo} を{(req.On ? "走らせる" : "走らせない")}にしました");
            return Results.Ok(new { ok = true });
        });
    }

    // 入換の線が書かれていないとき: 終点で着いた番線から、その向きで入れる最初の線（入換の既定。なければ null）
    private static string? DefaultVia(RailData rail, DiaTrain t)
    {
        var end = t.Stops.LastOrDefault(s => s.StationId != null);
        if (end?.StationId is not { } id || end.Track is not { } tr || RouteEngine.TrainInfo(t.Number) is not { } info
            || !rail.Stations.TryGetValue(id, out var sd) || !sd.RouteControl) return null;
        return sd.Routes[info.Dir].ShuntOuts.FirstOrDefault(o => o.From == tr)?.Label;
    }

    // 受け取った列車をそのまま持たない（保存するのは自分で作った物だけ）
    private static TempTrains.TrainSpec Copy(TempTrains.TrainSpec t) => new()
    {
        Unyo = t.Unyo ?? "", Number = t.Number ?? "", Type = t.Type ?? "", Label = t.Label ?? "", Dest = t.Dest ?? "",
        TurnHow = t.TurnHow ?? "", TurnNext = t.TurnNext ?? "", TurnTime = t.TurnTime, TurnVia = t.TurnVia ?? "", Cars = t.Cars,
        Stops = (t.Stops ?? new()).Take(80).Select(s => new TempTrains.StopSpec { St = s.St ?? "", Stop = s.Stop ?? "通", Arr = s.Arr, Dep = s.Dep, Track = s.Track }).ToList()
    };
}
