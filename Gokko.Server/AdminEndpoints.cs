using System.Security.Cryptography;
using System.Text;
using Gokko.Server.Services;

namespace Gokko.Server;

/// <summary>
/// 管理画面（wwwroot/admin.html）が使う API。ヘッダー X-Admin-Key に AdminKey を付けて呼ぶ。
/// AdminKey が未設定なら、すべて 503 で断る。
/// </summary>
public static class AdminEndpoints
{
    public sealed record TriggerRequest(string Crossing, string? Reason);

    public sealed record ReleaseRequest(string? Crossing);
    public sealed record ExtendRequest(string? Crossing, int? Seconds, bool? UntilRelease);

    public sealed record RandomRequest(bool? Enabled, int? RollIntervalSeconds, double? Probability, int? AutoReleaseSeconds, int? MaxConcurrent);

    /// <summary>Hour: 時だけの簡易設定（0〜23）。分・秒は今の日本時間のまま、時だけをずらした時刻にする。</summary>
    public sealed record ClockRequest(string? Time, bool? Running, bool? Clear, double? Speed, int? Hour = null);

    /// <param name="Kind">抑止の種類（"arrive" 場内・"depart" 出発。省くと両方）。</param>
    public sealed record TrainStationRequest(string Train, string Station, int? Track, bool? Hold, string? Kind);

    /// <param name="Train">後運用を差し替える列車。</param>
    /// <param name="Next">新しい後運用の列番。null なら、その列車が関わるいちばん新しい差し替えを取り消す。</param>
    public sealed record TurnRequest(string Train, string? Next, int? Direct = null, string? Station = null);

    /// <param name="Id">社員番号。</param>
    public sealed record CrewLoginRequest(string? Id, string? Password, string? Passphrase = null, bool? Client = null);

    /// <param name="Role">役職（司令員・信号扱い）。</param>
    /// <param name="On">出勤なら true（省略も出勤）、退勤なら false。</param>
    /// <param name="Station">信号扱いで扱う駅の ID（信号制御盤のある駅）。</param>
    public sealed record CrewRoleAttendRequest(string? Role, bool? On, string? Station);

    /// <param name="Id">社員番号を決めて作るときだけ（空なら自動で振る）。</param>
    public sealed record CrewCreateRequest(string? Name, int? JoinYear, string? Depot, string? Id);

    /// <param name="Id">今の社員番号。</param>
    /// <param name="NewId">新しい社員番号（変えないなら空か同じ番号）。</param>
    public sealed record CrewUpdateRequest(string? Id, string? NewId, string? Name, int? JoinYear, string? Depot);

    public sealed record CrewApplicationRequest(string? Key);

    public sealed record CrewAssignRequest(string Duty, string? User);
    public sealed record DutyFileRequest(string? File);
    public sealed record UnyoDutyRequest(string? From, string? To, int? MaxTransfer, string? Name);
    public sealed record DutyMoveRequest(List<string>? Trains, string? To, string? NewId, string? Name);
    public sealed record DutyRenameRequest(string? From, string? To, string? Name);

    public sealed record CrewRoleRequest(string? Id, string? Role, bool On);
    public sealed record CrewAttendRequest(string? Id, bool On);
    public sealed record CrewTakeRequest(string? Train);
    public sealed record CrewQrRequest(string? Origin);
    public sealed record CrewQrLoginRequest(string? Code);

    public sealed record StationModeRequest(string Station, bool Central, string? Mode = null);

    public sealed record PassingRequest(int? MarginSeconds, int? ToleranceSeconds);
    public sealed record ConnectionCutRequest(string? Key, bool Cut);
    public sealed record AiTrainRequest(string? Train, int? Cars, string? Station, string? Id);
    public sealed record AiTrainModeRequest(bool On);
    /// <summary>Unyos は運行番号（"24"・"24S"。前の形の数字も受ける）。</summary>
    public sealed record AiTrialRequest(List<System.Text.Json.JsonElement>? Unyos);

    /// <summary>駅の出発順序（Direction は使わない。上下まとめて1つの順序）。</summary>
    public sealed record OrderRequest(string Station, string? Direction, List<string>? Trains);

    public sealed record SectionOrderRequest(string A, string B, List<string>? Trains);

    /// <summary>駅扱い（強制進路）。Action は "arrive:1" "depart:2" "through" "shunt:3" など（◆・表のボタンが持っている値）。</summary>
    public sealed record ManualRequest(string Train, string Station, string Action, string? Label);

    public sealed record IdRequest(string Id);
    public sealed record ForceSignalRequest(string Name, bool On);
    public sealed record SweepRequest(bool On);

    public sealed record ModeRequest(bool Dia);

    /// <summary>手動進路（管理者用）。進路名を直接指定する。</summary>
    public sealed record ManualRouteRequest(string Train, string Interlock, string Route, bool Set);

    public sealed record OpenAtRequest(string Train, string Station, DateTimeOffset? Time);

    /// <summary>乗務の試験（crewtest.html）: 車掌の操作を、列番 Train の運転士のクライアントへ送る。</summary>
    public sealed record CrewTestRequest(string Train, string? Action, bool Emergency, bool ReOpen, bool Joukou, bool Buzzer);

    /// <summary>
    /// 終点で入換して折り返す列車の、引上線などの段の名前（ダイヤグラムの段。"5番線（引上線）" → "5"、"入換Y線" → "Y"）。
    /// 着いた番線からの shuntOut の最初のもの（折返に Via があればその線）。本線への入換など、段にならないものは null
    /// </summary>
    private static string? ShuntRow(RailData rail, DiaTrain t)
    {
        var end = t.Stops.LastOrDefault(s => s.StationId != null);
        if (end?.StationId is not { } sid || end.Track is not { } track || !rail.Stations.TryGetValue(sid, out var st)) return null;
        // ダイヤの折返に引き上げる線（X線など）が書かれていれば、その線
        var outs = st.Routes.Values.SelectMany(r => r.ShuntOuts).Where(s => s.From == track).ToList();
        var label = (rail.Turns.TurnOf(t)?.Via is { } via && outs.Any(s => s.Label == via) ? via : null) ?? outs.FirstOrDefault()?.Label;
        if (label == null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(label, @"^(\d+)番線");
        if (m.Success) return m.Groups[1].Value;
        m = System.Text.RegularExpressions.Regex.Match(label, @"([A-Z])線");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// 計画の線で引く範囲（時刻表の駅の番号）。From から To まで（To が null なら終点まで）。
    /// 打ち切った列車は打ち切った駅まで（Mid は、その駅で途中折返しする指定）。途中折返しで走り始める列車は、その駅から（Into はその指定）。
    /// </summary>
    private static (int From, int? To, TurnChanges.DirectTurn? Mid, TurnChanges.DirectTurn? Into) PlanCut(RailData rail, CentralControl central, DiaTrain t)
    {
        var mid = rail.Turns.DirectOf(t.Number) is { Midway: true } m ? m : null;
        var cutSt = mid?.Station ?? central.CutStation(t.Number);
        int? to = cutSt != null && t.Stops.FindIndex(s => s.StationId == cutSt) is var ci && ci >= 0 && ci < t.Stops.Count - 1 ? ci : null;
        var into = rail.Turns.MidwayInto(t.Number);
        var from = into != null && t.Stops.FindIndex(s => s.StationId == into.Station) is var fi && fi > 0 ? fi : 0;
        return (from, to, to != null ? mid : null, from > 0 ? into : null);
    }

    /// <summary>折り返しが路線の中の駅か: 列車の終点（時刻表の最後の駅）と、次の列車の始発が、同じ路線の駅。</summary>
    private static bool TurnsOnLine(RailData rail, DiaTrain t, string next) =>
        t.Stops.Count > 0 && t.Stops[^1].StationId is { } sid && rail.Line.ZoneOfStation(sid) >= 0
        && rail.Dia.TryGetValue(next, out var n) && n.Stops.Count > 0 && n.Stops[0].StationId == sid;

    /// <summary>画面向けの JSON の設定（camelCase）。要求のたびに作ると、そのたびに型の調べ直しが走るので、1つを使い回す。</summary>
    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static bool SameKey(string given, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));

    /// <summary>信号制御盤のある駅（盤のデータが入っている駅だけ。路線の順）。信号扱いの出勤の駅の枠に使う。並びは /api/relay/stations と同じ。</summary>
    private static List<(string Id, string Name)> RelayStations(RelayPanels panels, RailData rail, GameData data)
    {
        var ids = rail.Routes.Values.Select(r => r.StationId).Where(s => s != "").ToHashSet();
        var order = data.Line.StationIds.Where(ids.Contains).ToList();
        order.AddRange(ids.Except(order).Order());
        return order.Where(id => panels.Get(id) != null)
            .Select(id => (id, panels.Get(id)!.Data.Name is { Length: > 0 } n ? n : data.Line.StationName(id))).ToList();
    }

    public static void MapAdminEndpoints(this WebApplication app, ServerOptions options)
    {
        var admin = app.MapGroup("/api/admin").AddEndpointFilter(async (ctx, next) =>
        {
            if (string.IsNullOrEmpty(options.AdminKey))
                return Results.Problem("AdminKey が設定されていないため、管理機能は無効です", statusCode: 503);

            var given = ctx.HttpContext.Request.Headers["X-Admin-Key"].ToString();
            if (!SameKey(given, options.AdminKey))
                return Results.Problem("管理キーが違います", statusCode: 401);

            return await next(ctx);
        });

        // ダイヤグラムの設定画面（diasettings.html。他の参加者に公開する用）。ヘッダー X-Dia-Key に DiaPassword を付けて呼ぶ。
        // できるのはダイヤグラムの表示と操作と、ロープレ時刻の設定・停止・再開（特発の作動などは触れない。特発の解除と作動を続ける時間の変更はできる）
        // 司令員（勤怠管理で役職「司令員」を振った乗務員）は、合言葉なしでも、ログインの合い札（X-Crew-Token）で入れる（乗務メニュー・乗務タブレットから開いたとき）
        var dia = app.MapGroup("/api/dia").AddEndpointFilter(async (ctx, next) =>
        {
            var crew = ctx.HttpContext.RequestServices.GetRequiredService<CrewRoster>();
            if (crew.UserOfToken(ctx.HttpContext.Request.Headers["X-Crew-Token"].ToString()) is { } crewId && crew.HasRole(crewId, CrewRoster.DispatcherRole))
                return await next(ctx);

            if (string.IsNullOrEmpty(options.DiaPassword))
                return Results.Problem("DiaPassword が設定されていないため、この画面は使えません", statusCode: 503);

            var given = ctx.HttpContext.Request.Headers["X-Dia-Key"].ToString();
            if (!SameKey(given, options.DiaPassword))
                return Results.Problem("合言葉が違います", statusCode: 401);

            return await next(ctx);
        });

        MapDiagramControls(admin);
        MapDiagramControls(dia);
        dia.MapPost("/clock", SetClock);
        app.MapUntenkaEndpoints(dia); // 運転課（催物用の臨時列車）。適用・取消は司令用管理画面から

        // 司令員（勤怠管理で役職「司令員」を振った乗務員）がログインして使う。ヘッダー X-Crew-Token にログインの合い札（タブレットと同じログイン）。
        // /api/dispatch は diagrammarker.html を単体で開いたとき（ダイヤグラムの操作もできる）
        static async ValueTask<object?> DispatcherOnly(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
        {
            var crew = ctx.HttpContext.RequestServices.GetRequiredService<CrewRoster>();
            var token = ctx.HttpContext.Request.Headers["X-Crew-Token"].ToString();
            if (crew.UserOfToken(token) is not { } id)
                return Results.Json(new { message = token.Length > 0 ? "ログインが切れました。もう一度ログインしてください" : "ログインしてください", login = true }, statusCode: 401);
            if (!crew.HasRole(id, CrewRoster.DispatcherRole))
                return Results.Json(new { message = $"ID:{id} には司令員の役職がありません（勤怠管理で振ってもらってください）", login = true }, statusCode: 403);
            return await next(ctx);
        }
        MapDiagramControls(app.MapGroup("/api/dispatch").AddEndpointFilter(DispatcherOnly));
        // /api/diaview は diagramview.html（見るだけ。操作がないので、ログインなしで誰でも見られる）
        MapDiagramViews(app.MapGroup("/api/diaview"));

        // 司令用管理画面（ダイヤグラムの設定画面）からは、特発の解除と作動を続ける時間の変更ができる（作動はできない）
        dia.MapGet("/incidents", (IncidentManager incidents) => Results.Ok(new
        {
            incidents = incidents.Snapshot().OrderBy(i => i.StartedUtc).Select(i => new { crossing = i.Crossing, reason = i.Reason, source = i.Source, started = i.StartedUtc,
                releaseInSeconds = i.ReleaseAtUtc is { } ra ? Math.Max(0, (int)Math.Ceiling((ra - DateTime.UtcNow).TotalSeconds)) : (int?)null })
        }));
        dia.MapPost("/release", (ReleaseRequest req, IncidentManager incidents) =>
        {
            if (string.IsNullOrWhiteSpace(req.Crossing)) return Results.BadRequest(new { ok = false, message = "踏切を指定してください" });
            var released = incidents.Release([req.Crossing.Trim()], "司令用管理画面");
            return Results.Ok(new { ok = true, released = released.Count });
        });
        // 作動を続ける時間を変える（10秒ずつ増やす・減らす、解除まで動作）
        dia.MapPost("/extend", (ExtendRequest req, IncidentManager incidents) =>
        {
            if (string.IsNullOrWhiteSpace(req.Crossing)) return Results.BadRequest(new { ok = false, message = "踏切を指定してください" });
            return incidents.TryExtend(req.Crossing, req.Seconds ?? 0, req.UntilRelease == true, "司令用管理画面", out _, out var error)
                ? Results.Ok(new { ok = true })
                : Results.BadRequest(new { ok = false, message = error });
        });

        // 列車位置案内（passenger.html。お客様向け）。見るだけで、合言葉なしで誰でも見られる。信号場の発車案内は出さない
        app.MapGet("/api/passenger/train", (string train, RouteEngine engine) =>
            engine.PassengerTrain(train) is { } info ? Results.Ok(info) : Results.NotFound());
        app.MapGet("/api/passenger/positions", (RouteEngine engine, ResponseCache cache) =>
            cache.Get("passenger-positions", TimeSpan.FromSeconds(4), engine.PassengerPositions));
        app.MapGet("/api/passenger/departures/{id}", (string id, RailData rail, RouteEngine engine, ResponseCache cache) =>
            rail.Line.ZoneOfStation(id) >= 0 && !rail.Line.StationName(id).Contains("信号場")
                ? cache.Get("passenger-departures-" + id, TimeSpan.FromSeconds(4), () => engine.PassengerDepartures(id))
                : Results.NotFound());

        // 在線位置の画面（tid.html）。見るだけで、操作はできない。合言葉なしで誰でも見られる
        var view = app.MapGroup("/api/tid");

        // 乗務タブレット（tablet.html）: スタフの並びの時刻表・次の駅・進路の状態・行路
        // ヘッダー X-Crew-Token（ログインの合い札）があれば、その人の行路を出す。train がなければ、その人の行路の、いま乗る列車
        view.MapGet("/tablet", (HttpRequest http, string? train, RouteEngine engine, DutyRosters rosters, CrewRoster crew, PlayerRegistry players, TokuhatsuRinji rinji) =>
        {
            var roster = rosters.Current;
            var token = http.Headers["X-Crew-Token"].ToString();
            var id = crew.UserOfToken(token);
            if (token.Length > 0 && id == null) return Results.Json(new { message = "ログインが切れました。もう一度ログインしてください", logout = true }, statusCode: 401);
            // ゲームで運転している列車の列番が、自分の行路にないものに変わったら、その列車の行路に切り替える（重なってもよい）
            // 自分が運転している列車（参加者の一覧を丸ごと複製しない。1回の読み込みで2回複製していた）
            var mine = id == null ? new List<string>() : players.CrewTrains().Where(x => x.CrewId == id).Select(x => x.Train).ToList();
            if (mine.Count > 0) crew.FollowTrain(id!, mine[0], roster);
            var duties = id == null ? null : crew.DutiesOf(id).Select(roster.DutyById).OfType<Duty>().ToList();
            var t = string.IsNullOrEmpty(train) && duties != null ? engine.CurrentTrainOf(duties) : train;
            // 運転している列車が特発臨（行路にない列番）なら、その列車を出す
            if (string.IsNullOrEmpty(train) && mine.FirstOrDefault(rinji.IsRinji) is { } rp)
                t = rp;
            if (string.IsNullOrEmpty(t)) return Results.NotFound(new { message = id != null ? $"ID:{id} には、まだ行路が割り当てられていません" : "列車番号がありません" });
            if (id != null) crew.Seen(id, t);
            if (engine.TabletView(t, roster, duties, id) is not { } v) return Results.NotFound(new { message = $"{t} のダイヤはありません" });
            // 臨変で持っている行路（タブレットの行路の欄に「臨変」と出す）と、出勤しているか
            var node = System.Text.Json.JsonSerializer.SerializeToNode(v, WebJson)!;
            node["extras"] = System.Text.Json.JsonSerializer.SerializeToNode(id == null ? new List<string>() : crew.ExtrasOf(id));
            node["attended"] = id != null && crew.AttendedAt(id) != null;
            node["rinji"] = System.Text.Json.JsonSerializer.SerializeToNode(rinji.TabletNotice(t), WebJson); // この列車から立てた特発臨（列番を入れ直してもらう）
            return Results.Ok(node);
        });
        // 出勤（タブレットの［出勤］）: 行路がなければ、空いている行路をランダムに1つ割り当てる（所属を見る。前回と同じ行路は避ける）
        view.MapPost("/crew/attend", (HttpRequest http, CrewRoster crew, DutyRosters rosters) =>
        {
            if (crew.UserOfToken(http.Headers["X-Crew-Token"].ToString()) is not { } id) return Results.Json(new { message = "ログインしてください" }, statusCode: 401);
            var (ok, error) = crew.Attend(id, rosters.Current);
            return ok == null ? Results.BadRequest(new { message = error }) : Results.Ok(new { duties = ok.Duties, assigned = ok.Assigned, message = ok.Message });
        });
        // 退勤（タブレットの［退勤］）: 出勤を外し、担当していた行路（臨変も）を外す
        view.MapPost("/crew/leave", (HttpRequest http, CrewRoster crew) =>
        {
            if (crew.UserOfToken(http.Headers["X-Crew-Token"].ToString()) is not { } id) return Results.Json(new { message = "ログインしてください" }, statusCode: 401);
            var (released, error) = crew.Leave(id);
            return released == null ? Results.BadRequest(new { message = error })
                : Results.Ok(new { released, message = released.Count > 0 ? $"退勤しました。行路 {string.Join("・", released)} を外しました" : "退勤しました" });
        });
        // 司令員・信号扱いの出勤者（乗務メニュー・乗務タブレットの「出勤者」の欄）。名前だけを出す（社員番号は出さない。ログインなしでも見られる）
        // 合い札があれば、自分がどの役職で出勤しているか（mine）と、信号扱いで出勤している駅（myStation）も返す
        // 信号扱いは駅ごと: 盤のある駅（路線の順）を stations に並べ、駅ごとの出勤者を入れる（だれもいなければ空き）
        view.MapGet("/crew/role-attendance", (HttpRequest http, CrewRoster crew, RelayPanels panels, RailData rail, GameData data) =>
        {
            var id = crew.UserOfToken(http.Headers["X-Crew-Token"].ToString());
            var list = crew.RoleAttendees();
            var stations = RelayStations(panels, rail, data);
            object Person((string Id, string Name, DateTime Utc, string Station) p) =>
                new { name = p.Name.Length > 0 ? p.Name : "（名前なし）", since = p.Utc, me = p.Id == id, station = p.Station };
            return Results.Ok(new
            {
                roles = list.Select(r => new
                {
                    role = r.Role,
                    people = r.People.Select(Person),
                    stations = r.Role == CrewRoster.RelayKeyRole
                        ? stations.Select(s => new { id = s.Id, name = s.Name, people = r.People.Where(p => p.Station == s.Id).Select(Person) })
                        : null
                }),
                mine = id == null ? new List<string>() : list.Where(r => r.People.Any(p => p.Id == id)).Select(r => r.Role).ToList(),
                myStation = id == null ? null : list.First(r => r.Role == CrewRoster.RelayKeyRole).People.FirstOrDefault(p => p.Id == id).Station
            });
        });
        // 司令員・信号扱いの出勤・退勤（その役職の画面を開く前の「出勤」のポップ、出勤者の欄の［退勤］）。信号扱いは扱う駅（station）を付ける
        view.MapPost("/crew/role-attend", (HttpRequest http, CrewRoleAttendRequest req, CrewRoster crew, RelayPanels panels, RailData rail, GameData data) =>
        {
            if (crew.UserOfToken(http.Headers["X-Crew-Token"].ToString()) is not { } id) return Results.Json(new { message = "ログインしてください" }, statusCode: 401);
            var on = req.On != false;
            var st = on && req.Role == CrewRoster.RelayKeyRole ? RelayStations(panels, rail, data).FirstOrDefault(s => s.Id == req.Station) : default;
            if (on && req.Role == CrewRoster.RelayKeyRole && st.Id == null) return Results.BadRequest(new { message = "扱う駅を選んでください" });
            return crew.SetRoleAttend(id, req.Role, on, st.Id) is { } err ? Results.BadRequest(new { message = err })
                : Results.Ok(new { ok = true, message = !on ? $"{req.Role}を退勤しました" : st.Id != null ? $"{st.Name}の{req.Role}として出勤しました" : $"{req.Role}として出勤しました" });
        });
        // 列番を入れて開いたとき: 行路がなければ、その列車の行路を割り当てる（埋まっていれば臨変）
        view.MapPost("/crew/take", (HttpRequest http, CrewTakeRequest req, CrewRoster crew, DutyRosters rosters) =>
        {
            if (crew.UserOfToken(http.Headers["X-Crew-Token"].ToString()) is not { } id) return Results.Json(new { message = "ログインしてください" }, statusCode: 401);
            if (string.IsNullOrWhiteSpace(req.Train)) return Results.BadRequest(new { message = "列番を入れてください" });
            var (ok, error) = crew.TakeTrain(id, req.Train, rosters.Current);
            return ok == null ? Results.BadRequest(new { message = error }) : Results.Ok(new { duties = ok.Duties, assigned = ok.Assigned, message = ok.Message });
        });

        // ---- 乗務員のログイン（タブレット・クライアント共通）。本筋は Discord（DiscordEndpoints）。社員番号とパスワードは移行のあいだだけ ----
        IResult Bad(string message) => Results.BadRequest(new { message });
        // ログインした人への Discord の案内（紐づけていない人に、紐づけを勧める。linkUntil はパスワードで入れる最後の日時）
        (bool Enabled, bool Linked, DateTimeOffset? Until) LinkInfo(CrewRoster crew, string id) =>
            (options.Discord.Enabled, crew.InfoOf(id)?.Discord == true, options.Discord.PasswordUntil);

        // スマホでログインする QR コード（ログインしている本人に出す。中身は {origin}/menu.html#qr=使い捨ての番号）
        view.MapPost("/crew/qr", (HttpRequest http, CrewQrRequest req, CrewRoster crew) =>
        {
            if (crew.UserOfToken(http.Headers["X-Crew-Token"].ToString()) is not { } id) return Results.Json(new { message = "ログインしてください" }, statusCode: 401);
            // スマホから見える住所（PC の画面のアドレス）。http・https だけ
            if (!Uri.TryCreate(req.Origin, UriKind.Absolute, out var origin) || (origin.Scheme != "http" && origin.Scheme != "https"))
                return Results.BadRequest(new { message = "アドレスが正しくありません" });
            var (code, until) = crew.IssueQrCode(id);
            var url = $"{origin.GetLeftPart(UriPartial.Authority)}/menu.html#qr={code}";
            using var gen = new QRCoder.QRCodeGenerator();
            using var qr = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
            var svg = new QRCoder.SvgQRCode(qr).GetGraphic(6);
            return Results.Ok(new { id, url, svg, until });
        });
        view.MapPost("/login/qr", (CrewQrLoginRequest req, CrewRoster crew) =>
        {
            var (id, token, error) = crew.LoginByQrCode(req.Code);
            return error != null ? Bad(error) : Results.Ok(new { id, token });
        });
        // 乗務メニューから画面を開くときの引き継ぎ札（30秒・1回だけ）。URL を直接開いたときは札がないので、画面がログインを求める
        view.MapPost("/crew/handoff", (HttpRequest http, CrewRoster crew) =>
            crew.IssueHandoff(http.Headers["X-Crew-Token"].ToString()) is { } code
                ? Results.Ok(new { code })
                : Results.Json(new { message = "ログインしてください" }, statusCode: 401));
        view.MapPost("/login/handoff", (CrewQrLoginRequest req, CrewRoster crew) =>
        {
            var (id, token) = crew.RedeemHandoff(req.Code);
            return id != null ? Results.Ok(new { id, token }) : Results.Json(new { message = "引き継ぎの札が使えません（30秒で切れ、1回しか使えません）" }, statusCode: 401);
        });
        // ログインには参加の合言葉も要る（アカウントがあっても、合言葉を知らない人は入れない）。
        // PC のクライアント（client: true）は合言葉なしで入れるが、仮の合い札になる。合言葉を入れてサーバーに接続したら本物になる
        view.MapPost("/login", (CrewLoginRequest req, CrewRoster crew) =>
        {
            var client = req.Client == true;
            if (!client && !options.PassphraseMatches(req.Passphrase)) return Bad("合言葉が違います");
            var (id, token, error) = crew.Login(req.Id, req.Password, provisional: client);
            if (error != null) return Bad(error);
            var l = LinkInfo(crew, id!);
            return Results.Ok(new { id, token, discordEnabled = l.Enabled, discordLinked = l.Linked, linkUntil = l.Until });
        });
        // 合い札がまだ使えるか（クライアントの起動時）。Discord と紐づけているかも返す（紐づけていなければ、画面が紐づけを勧める）
        view.MapGet("/crew/me", (HttpRequest http, CrewRoster crew) =>
        {
            if (crew.InfoOf(crew.UserOfClientToken(http.Headers["X-Crew-Token"].ToString())) is not { } u) // クライアントの仮の合い札でもよい
                return Results.Json(new { message = "ログインしてください" }, statusCode: 401);
            var l = LinkInfo(crew, u.Id);
            return Results.Ok(new { id = u.Id, roles = u.Roles, discordEnabled = l.Enabled, discordLinked = l.Linked, linkUntil = l.Until });
        });
        view.MapPost("/logout", (HttpRequest http, CrewRoster crew) =>
        {
            crew.Logout(http.Headers["X-Crew-Token"].ToString());
            return Results.Ok(new { ok = true });
        });

        // 駅の接近案内（tablet-arrivals.html）: 駅へ向かってくる列車と到着番線を、到着する順に直近3列車まで（上り・下りごと）
        view.MapGet("/arrivals/stations", (ArrivalBoards boards) =>
            Results.Ok(boards.All.Select(w => new { id = w.Id, name = w.Name })));
        view.MapGet("/arrivals", (string? station, RouteEngine engine, ArrivalBoards boards) =>
            boards.Get(station) is { } w ? Results.Ok(engine.ArrivalBoard(w, 3)) : Results.NotFound(new { message = "その駅の接近案内はありません" }));
        // 監視している回路（確かめる用）
        view.MapGet("/arrivals/range", (string? station, ArrivalBoards boards) =>
            boards.Get(station) is { } w
                ? Results.Ok(new { station = w.Id, name = w.Name, basis = w.Basis, circuits = w.Range.OrderBy(c => w.Distance.GetValueOrDefault(c)).Select(c => new { name = c, distance = w.Distance.GetValueOrDefault(c) }) })
                : Results.NotFound(new { message = "その駅の接近案内はありません" }));

        // 行路の一覧（乗務タブレットで、行路を選んで見るため）。あとから来た人が空いている行路を見つけられるよう、運転士・車掌の枠も付ける
        // ・運転士: その行路の担当者（勤怠管理・出勤で割り当てた人）と、臨変で持っている人。いまゲームでその行路の列車を運転しているかも付ける
        // ・車掌: その行路のいま乗る列車に、運転士の許可を得て車掌として乗っている人
        // 名前（ユーザー名）だけを出す（社員番号は出さない。この一覧はログインしなくても見られる）
        // 全員に同じ答えなので、2秒は使い回す（タブレット・出勤簿が5秒ごとに読む。出勤・割り当てなどの操作のあとは ResponseCache.Clear で作り直す）
        view.MapGet("/duties", (DutyRosters rosters, CrewRoster crew, CrewRelay relay, PlayerRegistry players, RouteEngine engine, ResponseCache cache) => cache.Get("tid-duties", TimeSpan.FromSeconds(2), () =>
        {
            var roster = rosters.Current;
            string NameOf(string id, string name = "") => name.Length > 0 ? name : crew.InfoOf(id)?.Name is { Length: > 0 } n ? n : "（名前なし）";
            var assigned = crew.Assignments().ToDictionary(x => x.Duty, x => x.User);
            var extras = crew.Extras().ToLookup(x => x.Duty, x => x.User);
            var driving = players.CrewTrains()
                .GroupBy(p => p.CrewId).ToDictionary(g => g.Key, g => g.Select(p => RailData.NormalizeTrain(p.Train)).ToHashSet());
            var conductors = relay.ApprovedConductors().ToLookup(x => RailData.NormalizeTrain(x.Train));
            // 乗務の出勤（タブレットの［出勤］）。出勤簿で、出勤しているか・出勤していて行路のない人を出す
            var attended = crew.Attended();
            var currents = engine.CurrentTrainsOf(roster.Duties); // 行路1本ずつではなく、1回のロックでまとめて
            return new
            {
                file = roster.File,
                duties = roster.Duties.Select((d, di) =>
                {
                    var trains = d.Items.Where(x => x.Kind != "休憩").Select(x => x.Train).ToList();
                    var current = currents[di];
                    object Driver(string id, bool extra) => new { name = NameOf(id), extra, attended = attended.ContainsKey(id), onTrain = driving.TryGetValue(id, out var t) && trains.Any(t.Contains) };
                    var drivers = new List<object>();
                    if (assigned.TryGetValue(d.Id, out var a)) drivers.Add(Driver(a, false));
                    drivers.AddRange(extras[d.Id].Where(u => u != a).Distinct().Select(u => Driver(u, true)));
                    var onboard = current == null ? new List<object>() : conductors[current].Select(c => (object)new { name = NameOf(c.CrewId, c.Name) }).ToList();
                    return new { id = d.Id, trains, current, drivers, conductors = onboard };
                }),
                waiting = attended.Where(x => !crew.DutiesOf(x.Key).Any(d => roster.DutyById(d) != null)).OrderBy(x => x.Value)
                    .Select(x => new { name = NameOf(x.Key), since = x.Value }),
                problems = roster.Problems
            };
        }));

        view.MapGet("/timetable", (string? train, RouteEngine engine, GameClock clock) =>
            engine.Timetable(train ?? "", clock.Now) is { } t ? Results.Ok(t) : Results.NotFound(new { message = "この列車の時刻表はありません" }));

        // 閉塞信号機の番号と、守る軌道回路（軌道回路名つきの画面で、閉塞信号の位置に番号を出す）
        view.MapGet("/signals", (RailData rail) => Results.Ok(rail.Signals.Values
            .Where(s => !s.IsRepeater && s.Protects.Count > 0 && System.Text.RegularExpressions.Regex.IsMatch(s.Name, @"^(上り|下り)閉塞\d+$"))
            .Select(s => new { number = System.Text.RegularExpressions.Regex.Match(s.Name, @"\d+$").Value, direction = s.Name[..2], circuit = s.Protects[0] })));

        // 停目までの距離の表（クライアントの自動運転が乗務の初めに1回だけ読む。距離の計算はクライアント）。中身は起動時のデータで決まるので1回だけ作る
        Gokko.Common.StopTargetTable? stopTargets = null;
        app.MapGet("/api/stop-targets", (AiTrains ai, StopMarkers markers, RailData rail) =>
            Results.Ok(stopTargets ??= StopTargets.Build(ai, markers, rail)));

        // ロープレ時刻（画面で、サーバーの実際の時刻をロープレ時刻に直して出すため）。daySec はその日の秒（設定していなければ null）
        app.MapGet("/api/clock", (GameClock clock) => Results.Ok(new
        {
            serverUtc = DateTime.UtcNow,
            daySec = clock.Now?.TotalSeconds,
            speed = clock.Speed, // 止めているときも、前後の時刻は進む速さで直す（今がロープレ時刻の今になるように、5秒ごとに読み直す）
            running = clock.IsRunning
        }));

        // 在線位置の画面（tid.html）・信号制御盤・タブレットが、開いている画面ごとに1秒に1回読む（11/22 のデバッグ運転で1分に約650回、
        // 画面の API の時間の約6割）。出発順序の見込み（OrdersView）まで毎回計算し直していたので、答えを 0.5 秒使い回す
        // 中身の進路の部分は、配信のループが Tick のあとに作り置いたもの（RouteEngine.View。ロックを待たない）
        view.MapGet("/state", (RouteEngine engine, GameClock clock, RelayPanels panels, ResponseCache cache) => cache.Get("tid-state", TimeSpan.FromMilliseconds(500), () => new
        {
            gameTime = clock.NowText,
            tid = engine.View(RouteEngine.ViewTid),
            panels = panels.TidView(),   // 信号制御盤: 盤で扱っている駅と、盤で取っている進路
            orders = engine.View(RouteEngine.ViewOrders),
            sections = engine.View(RouteEngine.ViewSections)
        }));

        // ---- 勤怠管理（kintai.html）: 乗務員（社員番号）と、行路への割り当て ----
        // /api/kintai はヘッダー X-Kintai-Key に、管理キーか勤怠管理の鍵（KintaiKey。公開する用）を付けて呼ぶ。
        // 勤怠管理の鍵で入ったときは、乗務員の登録・編集・パスワード・役職（/api/admin/crew/...）は使えず、名簿も社員番号・所属だけを返す
        var kintai = app.MapGroup("/api/kintai").AddEndpointFilter(async (ctx, next) =>
        {
            var given = ctx.HttpContext.Request.Headers["X-Kintai-Key"].ToString();
            var full = given.Length > 0 && !string.IsNullOrEmpty(options.AdminKey) && SameKey(given, options.AdminKey);
            var limited = given.Length > 0 && !string.IsNullOrEmpty(options.KintaiKey) && SameKey(given, options.KintaiKey);
            // 司令員は、乗務メニューから鍵なしで入れる（ログインの合い札 X-Crew-Token。勤怠管理の鍵でできることに加えて、行路表も扱える）
            var dispatcher = false;
            if (!full)
            {
                var crew = ctx.HttpContext.RequestServices.GetRequiredService<CrewRoster>();
                var id = crew.UserOfToken(ctx.HttpContext.Request.Headers["X-Crew-Token"].ToString());
                dispatcher = id != null && crew.HasRole(id, CrewRoster.DispatcherRole);
                if (!limited && !dispatcher)
                    return Results.Problem(id == null ? "管理キーが違います" : "勤怠管理は、役職「司令員」の人だけが開けます", statusCode: 401);
            }
            ctx.HttpContext.Items[KintaiFull] = full;
            ctx.HttpContext.Items[KintaiDuty] = full || dispatcher; // 行路表（切り替え・運行番号から作る・行路を組む）は管理キーと司令員。勤怠管理の鍵ではできない
            return await next(ctx);
        });
        MapKintai(admin);
        MapKintai(kintai);
        // 行路を組む（列車を選んで、ほかの行路・新しい行路へ移す。行路名を変える。Data/Duties.{名前}.csv に書いて読む。管理キーと司令員）
        var dutyDataDir = Path.IsPathRooted(options.DataDirectory) ? options.DataDirectory : Path.Combine(AppContext.BaseDirectory, options.DataDirectory);
        foreach (var g in new[] { admin, kintai })
            g.MapPost("/duties/move", (HttpContext http, DutyMoveRequest req, DutyRosters rosters, RailData rail) =>
            {
                if (http.Items[KintaiDuty] is false) return Results.Json(new { message = DutyDenied }, statusCode: 403);
                string? file, error, target;
                lock (rosters) (file, error, target) = DutyEdit.Move(dutyDataDir, rail, rosters.Current, req.Trains ?? new(), req.To, req.NewId, req.Name);
                if (file == null) return Results.BadRequest(new { message = error });
                if (rosters.Load(file) is { } err) return Results.BadRequest(new { message = err });
                return Results.Ok(new { ok = true, file, target, message = $"{(req.Trains ?? new()).Count} 本を行路 {target} へ移し、{file} に書きました" });
            });
        foreach (var g in new[] { admin, kintai })
            g.MapPost("/duties/rename", (HttpContext http, DutyRenameRequest req, DutyRosters rosters, RailData rail, CrewRoster crew) =>
            {
                if (http.Items[KintaiDuty] is false) return Results.Json(new { message = DutyDenied }, statusCode: 403);
                string? file, error;
                lock (rosters) (file, error) = DutyEdit.Rename(dutyDataDir, rail, rosters.Current, req.From, req.To, req.Name);
                if (file == null) return Results.BadRequest(new { message = error });
                if (rosters.Load(file) is { } err) return Results.BadRequest(new { message = err });
                crew.RenameDuty(req.From!.Trim(), req.To!.Trim()); // 割り当て・臨変も新しい行路名へ
                return Results.Ok(new { ok = true, file, message = $"行路 {req.From!.Trim()} を {req.To!.Trim()} に変え、{file} に書きました" });
            });

        // ユーザーを作る（社員番号と、作ったパスワードを返す。本人に伝える。秘密の質問は本人が最初のログインで決める）
        admin.MapPost("/crew/users", (CrewCreateRequest req, CrewRoster crew) =>
        {
            var (id, password, error) = crew.AddUser(req.Name, req.JoinYear ?? 0, req.Depot, req.Id);
            return error != null ? Results.BadRequest(new { message = error }) : Results.Ok(new { id, password });
        });
        // 名簿を書き換える（社員番号・ユーザー名・入社年度・所属。割り当てとログインは引き継ぐ）
        admin.MapPost("/crew/users/update", (CrewUpdateRequest req, CrewRoster crew) =>
            crew.Update(req.Id, req.NewId, req.Name, req.JoinYear ?? 0, req.Depot) is { } err
                ? Results.BadRequest(new { message = err })
                : Results.Ok(new { ok = true }));
        // パスワードを出し直す（秘密の質問を忘れた人など）
        admin.MapPost("/crew/reset", (CrewLoginRequest req, CrewRoster crew) =>
        {
            var (password, error) = crew.ResetByAdmin(req.Id);
            return error != null ? Results.BadRequest(new { message = error }) : Results.Ok(new { password });
        });
        admin.MapPost("/crew/users/delete", (CrewLoginRequest req, CrewRoster crew) =>
            crew.RemoveUser(req.Id) ? Results.Ok(new { ok = true }) : Results.NotFound(new { message = "その人はいません" }));
        // Discord の紐づけを外す（Discord を作り直した人など。外すとログアウトする。本人は Discord で申請し直す。社員番号を入れれば、この人に戻せる）
        admin.MapPost("/crew/discord/unlink", (CrewLoginRequest req, CrewRoster crew) =>
            crew.UnlinkDiscord(req.Id) is { } err ? Results.BadRequest(new { message = err }) : Results.Ok(new { ok = true }));
        // 停止位置目標（Data/StopMarkers.*.csv）を読んだ中身を確かめる。
        // 何も付けなければ全部と読んだときのおかしな所。station と cars を付けると、その駅・番線・向きでその両数が止まる停目（dir は up・down、track は 1・X・引上 など）
        // ランカーブ（1駅間の走り方の記録）を、日・利用者・形式・駅・自動/手動で選んで返す。points=true で行も含める
        admin.MapGet("/run-curves", (RunCurves runs, string? date, string? crew, string? model, string? station, string? mode, bool? points, int? limit) =>
            Results.Ok(runs.Find(date, crew, model, station, mode, points == true, Math.Clamp(limit ?? 500, 1, 5000))));

        admin.MapGet("/stop-markers", (StopMarkers markers, string? station, string? dir, string? track, int? cars) =>
        {
            static object View(StopMarkers.Entry e) => new
            {
                dir = e.Dir == Dir.Up ? "up" : "down", station = e.Station, track = e.Track, label = e.Label, row = e.Row,
                markers = e.Markers.Select(m => new { cars = m.Cars, offset = m.Offset }),
            };
            if (station == null)
                return Results.Ok(new { problems = markers.Problems, count = markers.Entries.Count, entries = markers.Entries.Select(View) });

            var dirs = dir?.ToLowerInvariant() switch { "up" or "上り" => [Dir.Up], "down" or "下り" => [Dir.Down], _ => new[] { Dir.Up, Dir.Down } };
            return Results.Ok(new
            {
                results = dirs.Select(d => markers.Find(d, station, track) is { } e
                    ? (object)new { found = true, entry = View(e), choice = cars is { } n ? e.Choose(n) : null }
                    : new { found = false, dir = d == Dir.Up ? "up" : "down", station, track }),
            });
        });
        // Discord で申請した人（承認すると名簿に入る。社員番号を入れて申請した人は、その人に紐づく）
        admin.MapGet("/load", (ServerLoad load) => Results.Ok(load.View())); // サーバーの負荷（直近1時間）
        admin.MapGet("/crew/applications", (CrewRoster crew) => Results.Ok(new { applications = crew.Applications() }));
        admin.MapPost("/crew/applications/approve", (CrewApplicationRequest req, CrewRoster crew) =>
        {
            var (id, error) = crew.Approve(req.Key);
            return error != null ? Results.BadRequest(new { message = error }) : Results.Ok(new { id });
        });
        admin.MapPost("/crew/applications/reject", (CrewApplicationRequest req, CrewRoster crew) =>
            crew.Reject(req.Key) ? Results.Ok(new { ok = true }) : Results.NotFound(new { message = "その申請はありません" }));
        // 役職を振る・外す（司令員など）
        admin.MapPost("/crew/roles", (CrewRoleRequest req, CrewRoster crew) =>
            crew.SetRole(req.Id, req.Role, req.On) is { } err ? Results.BadRequest(new { message = err }) : Results.Ok(new { ok = true }));
        // 出勤を付ける・外す（乗務員タブから）
        admin.MapPost("/crew/attend", (CrewAttendRequest req, CrewRoster crew) =>
            crew.SetAttend(req.Id, req.On) is { } err ? Results.BadRequest(new { message = err }) : Results.Ok(new { ok = true }));

        // 勤怠管理で作った行路表の一覧と、消す（管理画面だけ。管理キーで。もとからある行路表は出さず、消さない）
        admin.MapGet("/duties/kintai-files", (DutyRosters rosters) =>
            Results.Ok(new { current = rosters.Current.File, files = rosters.KintaiFiles() }));
        admin.MapPost("/duties/delete", (DutyFileRequest req, DutyRosters rosters) =>
            rosters.Delete(req.File) is { } err ? Results.BadRequest(new { message = err }) : Results.Ok(new { ok = true }));

        MapAdminRest(admin);
    }

    private const string KintaiFull = "kintaiFull";
    private const string KintaiDuty = "kintaiDuty";
    private const string DutyDenied = "行路表を扱うのは、管理キーか司令員だけです";

    /// <summary>勤怠管理の、管理キーでも勤怠管理の鍵でも使えるもの（一覧・行路の割り当て・自動割り当て・臨変・行路表の切り替え）。</summary>
    private static void MapKintai(RouteGroupBuilder g)
    {
        g.MapGet("/crew", (HttpContext http, CrewRoster crew, DutyRosters rosters, RouteEngine engine) =>
        {
            var full = http.Items[KintaiFull] is not false; // /api/admin なら印がない（管理キー）
            var dutyEdit = http.Items[KintaiDuty] is not false; // 行路表を扱える（管理キー・司令員）
            var roster = rosters.Current;
            var users = crew.Users().Select(u =>
            {
                var (online, train, last) = crew.Presence(u.Id);
                return full
                    ? (object)new
                    {
                        id = u.Id, name = u.Name, joinYear = u.JoinYear, depot = u.Depot, discord = u.Discord, hasPassword = u.HasPassword, created = u.Created, roles = u.Roles,
                        duties = crew.DutiesOf(u.Id), extras = crew.ExtrasOf(u.Id), attended = crew.AttendedAt(u.Id), online, train, last
                    }
                    : new { id = u.Id, name = u.Name, joinYear = u.JoinYear, depot = u.Depot, duties = crew.DutiesOf(u.Id), extras = crew.ExtrasOf(u.Id), attended = crew.AttendedAt(u.Id), online, train, last };
            }).ToList();
            var duties = roster.Duties.Select(d => new
            {
                view = engine.DutyView(d, null),
                user = crew.AssignedTo(d.Id),
                auto = crew.IsAuto(d.Id)
            }).ToList();
            var extras = crew.Extras().Select(x => new { duty = x.Duty, user = x.User }).ToList(); // 臨変
            // 列車ごとの担当（この列車にはこの人が乗る）
            var trains = roster.Duties.SelectMany(d => d.Items.Where(x => x.Kind != "休憩").Select(x => new
            {
                train = x.Train, kind = x.Kind, duty = d.Id, user = crew.AssignedTo(d.Id), running = engine.IsRunning(x.Train)
            })).ToList();
            // 割り当ててあるのに、いまの行路表にない行路番号（行路表を切り替えたときなど。割り当ては消さずに残す）
            var missing = crew.Assignments().Where(a => roster.DutyById(a.Duty) == null).Select(a => new { duty = a.Duty, user = a.User }).ToList();
            return Results.Ok(new
            {
                full, users, duties, extras, trains, missing, problems = roster.Problems, hasDuties = roster.Duties.Count > 0,
                dutyFile = roster.File, dutyFileExists = roster.Exists, dutyEdit, dutyFiles = dutyEdit ? rosters.Files() : new List<string>(),
                depots = CrewRoster.Depots, roleNames = CrewRoster.RoleNames, joinYearFrom = CrewRoster.JoinYears.From, joinYearTo = CrewRoster.JoinYears.To
            });
        });
        g.MapPost("/crew/assign", (CrewAssignRequest req, CrewRoster crew, DutyRosters rosters) =>
        {
            // 外すのは、いまの行路表にない行路番号でもできる（行路表を切り替えて残った割り当てを片づける）
            if (!string.IsNullOrWhiteSpace(req.User) && rosters.Current.DutyById(req.Duty) == null)
                return Results.NotFound(new { message = $"行路 {req.Duty} はいまの行路表（{rosters.Current.File}）にありません" });
            return crew.Assign(req.Duty, req.User) is { } err ? Results.BadRequest(new { message = err }) : Results.Ok(new { ok = true });
        });
        // 全員の出勤を外す（割り当てはそのまま）
        g.MapPost("/crew/attend/clear", (CrewRoster crew) => Results.Ok(new { ok = true, count = crew.ClearAttendance() }));
        // 出勤していて行路のない人に、空いている行路をランダムに割り当てる（所属・前回の行路・列車の重なりを見る）
        g.MapPost("/crew/auto-assign", (CrewRoster crew, DutyRosters rosters) =>
            Results.Ok(new { ok = true, results = crew.AutoAssignAttended(rosters.Current).Select(x => new { user = x.User, duty = x.Duty }) }));
        // 臨変を外す
        g.MapPost("/crew/extra/remove", (CrewAssignRequest req, CrewRoster crew) =>
        {
            crew.RemoveExtra(req.Duty, req.User ?? "");
            return Results.Ok(new { ok = true });
        });
        // 行路のダイヤ（行路ごとの列車の線）と、どの行路にも入っていない列車（勤怠管理の「行路のダイヤ」タブ）
        g.MapGet("/duties/diagram", (DutyRosters rosters, RailData rail, CrewRoster crew) =>
        {
            var extras = crew.Extras();
            return Results.Ok(DutyDiagram.Build(rail, rosters.Current, crew.AssignedTo,
                duty => extras.Where(x => x.Duty == duty).Select(x => x.User).ToList()));
        });
        // 行路表を切り替える（Data/ の Duties*.csv から選ぶ。再起動しなくてよい。選んだものは再起動しても使う。割り当ては消さない）
        // どの行路表を使うかは管理者だけが決める（勤怠管理の鍵では断る）
        g.MapPost("/duties/load", (HttpContext http, DutyFileRequest req, DutyRosters rosters) =>
        {
            if (http.Items[KintaiDuty] is false) return Results.Json(new { message = DutyDenied }, statusCode: 403);
            if (rosters.Load(req.File) is { } err) return Results.BadRequest(new { message = err });
            var r = rosters.Current;
            return Results.Ok(new { ok = true, file = r.File, count = r.Duties.Count, problems = r.Problems.Count });
        });
        // 運行番号から行路を作る（時間を決めて、その間に館浜〜赤山町を走る列車を運行ごとの行路にする。Data/Duties.{名前}.csv に書いて読む）
        // 前の行路表の割り当て・臨変は外す（行路番号の中身が変わるため）。出勤している人には、そのまま自動で割り当てる
        g.MapPost("/duties/unyo", (HttpContext http, UnyoDutyRequest req, DutyRosters rosters, CrewRoster crew) =>
        {
            if (http.Items[KintaiDuty] is false) return Results.Json(new { message = DutyDenied }, statusCode: 403);
            if (UnyoDuties.ParseTime(req.From) is not { } from || UnyoDuties.ParseTime(req.To) is not { } to)
                return Results.BadRequest(new { message = "時間は 17:00 のように入れてください" });
            UnyoDuties.Result info;
            if (req.Name != null && UnyoDuties.FileNameFor(req.Name, rosters.DataDir).Error is { } nameErr) return Results.BadRequest(new { message = nameErr });
            try { info = UnyoDuties.Write(rosters.DataDir, rosters.Rail, from, to, Math.Clamp(req.MaxTransfer ?? 30, 5, 120) * 60, req.Name); }
            catch (Exception ex) { return Results.BadRequest(new { message = "行路を作れませんでした: " + ex.Message }); }
            if (rosters.Load(info.File) is { } err) return Results.BadRequest(new { message = err });
            var cleared = crew.ClearAssignments();
            var auto = crew.AutoAssignAttended(rosters.Current);
            return Results.Ok(new
            {
                ok = true, file = info.File, count = info.Duties, trains = info.Trains, express = info.Express,
                message = $"{info.Window} の行路を {info.Duties} 件作り、{info.File} に書きました（列車 {info.Trains} 本・うち駒列 {info.Express} 件）。"
                          + (cleared > 0 ? $"前の割り当て {cleared} 件を外しました。" : "")
                          + (auto.Count > 0 ? $"出勤している {auto.Count(x => x.Duty != null)} 名に割り当てました。" : "")
            });
        });
    }

    /// <summary>管理画面の残り（状態・乗務の試験など）。</summary>
    private static void MapAdminRest(RouteGroupBuilder admin)
    {

        admin.MapGet("/state", (GameData data, IncidentManager incidents, PlayerRegistry players, GameClock clock, RandomOptions random, ProtectionRadio radio, DispatcherProtection dispatcherProtection, TrainRadio trainRadio) =>
        {
            var now = DateTime.UtcNow;
            var active = incidents.Snapshot().ToDictionary(i => i.Crossing);
            return Results.Ok(new
            {
                serverTime = DateTimeOffset.UtcNow,
                clock = new { gameTime = clock.NowText, running = clock.IsRunning },
                radio = radio.View(), // 防護無線の発報中の一覧
                dispatcherProtection = dispatcherProtection.AdminView(), // 司令員からの区間防護
                dispatchers = trainRadio.Dispatchers().Values.Select(TrainRadio.DispatcherLabel).ToList(), // 参加中の司令員
                random,
                reasons = data.Reasons,
                // 理由ごとの自動復帰の秒数と重み（ランダムで選ばれる確率は、重み ÷ 重みの合計）
                reasonSpecs = data.ReasonSpecs.Select(r => new { name = r.Name, seconds = r.MinSeconds >= 0 ? r.SecondsText : null, weight = r.Weight,
                    percent = data.ReasonSpecs.Sum(x => x.Weight) is var tw && tw > 0 ? Math.Round(r.Weight / tw * 100, 1) : 0 }),
                crossings = data.Crossings.Select(c => new
                {
                    name = c,
                    location = data.DescribeCrossing(c),
                    // 特発を戻せる軌道回路（なければ駅間で判定）
                    restoreCircuits = data.CrossingCircuits.TryGetValue(c, out var rc) ? rc.OrderBy(x => x, StringComparer.Ordinal).ToList() : null,
                    incident = active.TryGetValue(c, out var i)
                        ? new
                        {
                            reason = i.Reason,
                            source = i.Source,
                            startedGameTime = i.StartedGameTime,
                            elapsedSeconds = (int)(now - i.StartedUtc).TotalSeconds,
                            // 自動で復帰するまでの残り秒数（自動復帰しないなら null）
                            releaseInSeconds = i.ReleaseAtUtc is { } ra ? Math.Max(0, (int)Math.Ceiling((ra - now).TotalSeconds)) : (int?)null
                        }
                        : null
                }),
                players = players.Snapshot().OrderBy(p => p.JoinedUtc).Select(p => new
                {
                    crewId = p.CrewId,
                    mode = p.Mode == Gokko.Common.ShareMode.Shared ? "在線共有" : "特発のみ",
                    trainNumber = p.TrainNumber,
                    trainCrewConnected = p.TrainCrewConnected,
                    inGame = p.InGame,
                    position = data.Line.DescribeZones(p.Zones),
                    direction = p.Direction,
                    lastReportSecondsAgo = (int)(now - p.LastReportUtc).TotalSeconds
                }),
                log = incidents.RecentLog().Select(l => new { utc = l.Utc, gameTime = l.GameTime, message = l.Message })
            });
        });

        // 乗務の試験（crewtest.html）: ブラウザを車掌の代わりにして、運転士のクライアント → ゲームへ車掌の操作を送る
        admin.MapGet("/crew-test/status", (string? train, CrewRelay relay) => Results.Ok(relay.StatusOf((train ?? "").Trim())));
        admin.MapPost("/crew-test/conductor", async (CrewTestRequest req, CrewRelay relay) =>
        {
            var input = new Gokko.Common.ConductorInput { Action = req.Action ?? "", AllClose = null, Emergency = req.Emergency, ReOpen = req.ReOpen, Joukou = req.Joukou, Buzzer = req.Buzzer };
            var message = await relay.FromTestAsync((req.Train ?? "").Trim(), input);
            return message == null ? Results.Ok(new { ok = true }) : Results.BadRequest(new { message });
        });

        admin.MapPost("/trigger", (TriggerRequest req, IncidentManager incidents) =>
            incidents.TryTrigger(req.Crossing ?? "", req.Reason ?? "", "管理画面", out var error)
                ? Results.Ok(new { ok = true })
                : Results.BadRequest(new { ok = false, message = error }));

        admin.MapPost("/release", (ReleaseRequest req, IncidentManager incidents) =>
        {
            var released = string.IsNullOrWhiteSpace(req.Crossing)
                ? incidents.ReleaseAll("管理画面")
                : incidents.Release([req.Crossing.Trim()], "管理画面");
            return Results.Ok(new { ok = true, released = released.Count });
        });

        admin.MapPost("/random", (RandomRequest req, RandomOptions random, IncidentManager incidents) =>
        {
            if (req.RollIntervalSeconds is { } interval) random.RollIntervalSeconds = Math.Clamp(interval, 1, 3600);
            if (req.Probability is { } prob) random.Probability = Math.Clamp(prob, 0.0, 1.0);
            if (req.AutoReleaseSeconds is { } auto) random.AutoReleaseSeconds = Math.Clamp(auto, 0, 86400);
            if (req.MaxConcurrent is { } max) random.MaxConcurrent = Math.Clamp(max, 1, 50);
            if (req.Enabled is { } enabled && enabled != random.Enabled)
            {
                random.Enabled = enabled;
                incidents.Note(enabled ? "ランダム特発: 開始" : "ランダム特発: 停止");
            }

            return Results.Ok(new { ok = true, random });
        });

        // ---- 進路・集中扱い ----
        admin.MapGet("/routes", (RouteEngine engine, CentralControl central, RailData rail) => Results.Ok(new
        {
            trains = engine.AdminView(),
            stations = rail.ControlStations.Select(s => new { id = s.Id, name = s.Name, central = central.IsCentral(s.Id), mode = central.ModeOf(s.Id), panel = central.IsPanelControlled(s.Id) }),
            passing = new { marginSeconds = rail.PassingMarginSeconds, toleranceSeconds = rail.PassingToleranceSeconds }
        }));

        // 進路を取り始める位置（信号機）の一覧（確かめる用）
        admin.MapGet("/route/approach", (int? margin, RouteEngine engine, RailData rail) => Results.Ok(new
        {
            enabled = rail.ApproachTriggerEnabled,
            margin = margin ?? rail.ApproachTriggerMargin,
            rows = engine.ApproachTable(margin ?? rail.ApproachTriggerMargin)
        }));

        admin.MapPost("/clock", SetClock);
    }

    /// <summary>ロープレ時刻の設定・停止・再開（管理画面と司令用管理画面で共通）。</summary>
    private static IResult SetClock(ClockRequest req, GameClock clock, IncidentManager incidents, CentralControl central, RailData rail, TokuhatsuRinji rinji)
    {
            // ロープレ時刻を設定・解除したら、手で入れたダイヤの指示を一度取り消す（駅の扱いは残す。一時停止・再開・速さでは消さない）
            void ResetDia()
            {
                var n = central.ResetManual();
                var t = rail.Turns.Reset();
                var r = rinji.Reset();
                if (r > 0) incidents.Note($"ダイヤのリセット: 特発臨 {r}本を取消");
                incidents.Note(n + t == 0
                    ? "ダイヤのリセット: 取り消す手動の設定はありません"
                    : $"ダイヤのリセット: 列車ごとの指示・出発順序・走行順 {n}件、後運用の差し替え {t}件を取消");
            }

            if (req.Clear == true)
            {
                clock.Clear();
                incidents.Note("ロープレ時刻: 解除");
                ResetDia();
                return Results.Ok(new { ok = true, gameTime = (string?)null });
            }

            // 早回しのシミュレーション用: 進む速さ（1 = 実時間と同じ）
            if (req.Speed is { } sp)
            {
                clock.SetSpeed(sp);
                incidents.Note($"ロープレ時刻: 進む速さ {clock.Speed}倍");
            }

            // 時だけの簡易設定: 分・秒は今の日本時間のまま（日本時間との差がちょうど何時間かになる）。
            // 日本時間とずれないように、進む速さは実時間と同じ（1倍）に戻す。時刻は1日の中で回るので、日付をまたぐ差でもそのまま
            if (req.Hour is { } hour)
            {
                if (hour is < 0 or > 23)
                    return Results.BadRequest(new { ok = false, message = "時は 0〜23 で入力してください" });
                var jst = DateTime.UtcNow.AddHours(9).TimeOfDay; // 日本時間（夏時間なし）
                var t = TimeSpan.FromHours(hour) + (jst - TimeSpan.FromHours(jst.Hours));
                if (clock.Speed != 1) clock.SetSpeed(1);
                clock.Set(t, true);
                var diff = hour - jst.Hours;
                incidents.Note($"ロープレ時刻: 時だけ {hour}時 に設定（{t:hh\\:mm\\:ss}、日本時間{(diff >= 0 ? "+" : "")}{diff}時間）");
                ResetDia();
                return Results.Ok(new { ok = true, gameTime = clock.NowText, running = clock.IsRunning, speed = clock.Speed });
            }

            if (!string.IsNullOrWhiteSpace(req.Time))
            {
                if (!TimeSpan.TryParse(req.Time.Trim(), out var t) || t < TimeSpan.Zero || t >= TimeSpan.FromDays(1))
                    return Results.BadRequest(new { ok = false, message = "時刻は 11:00 や 11:00:30 の形で入力してください" });

                clock.Set(t, req.Running ?? true);
                incidents.Note($"ロープレ時刻: {t:hh\\:mm\\:ss} に設定");
                ResetDia();
            }
            else if (req.Running is { } running)
            {
                clock.SetRunning(running);
            }

            return Results.Ok(new { ok = true, gameTime = clock.NowText, running = clock.IsRunning, speed = clock.Speed });
    }

    /// <summary>ダイヤグラムの表示と、そこからできる操作（管理画面と、ダイヤグラムの設定画面で共通）。</summary>
    private static void MapDiagramControls(RouteGroupBuilder g)
    {
        TokuhatsuRinjiEndpoints.MapTokuhatsuRinji(g); // 特発臨（運転整理のために、その場で立てる臨時列車）
        g.MapEventStops(); // 催物用臨停設定（EventStopEndpoints.cs）

        // にぎやかしの列車（サーバーだけで走る列車）。出す・消す・一覧
        // 一覧と、自動運転列車のアリ/ナシ（アリなら、ダイヤの列車を時刻に合わせて自動で出す。ユーザーの列車は出さない）
        g.MapGet("/ai-trains", (AiTrains ai, RailData rail) => Results.Ok(new
        {
            enabled = ai.Enabled,
            trains = ai.List(),
            candidates = ai.Candidates(), // 手で出せる列車（走っていない、これから発車する列車）
            trials = ai.Trials(),       // 試運転の運行番号ごとの列車（プルダウンの項目）
            trialsOn = rail.TrialsOn,   // 走らせる試運転の運行番号（空なら「なし」）
            carsProblems = ai.CarsProblems, // 両数の表（Data/UnyoCars.csv・TrainCars.csv）のおかしな所
        }));
        g.MapPost("/ai-trains/mode", (AiTrainModeRequest req, AiTrains ai) =>
        {
            ai.SetEnabled(req.On);
            return Results.Ok(new { ok = true, enabled = ai.Enabled });
        });
        // 試運転（所定の臨時列車）を走らせる運行番号。空なら1本も出さない
        g.MapPost("/ai-trains/trial", (AiTrialRequest req, AiTrains ai, RailData rail) =>
        {
            var message = ai.SetTrials((req.Unyos ?? new()).Select(u => u.ValueKind == System.Text.Json.JsonValueKind.String ? u.GetString() : u.GetRawText()).OfType<string>());
            return Results.Ok(new { ok = true, message, trialsOn = rail.TrialsOn });
        });
        g.MapPost("/ai-trains/add", (AiTrainRequest req, AiTrains ai) =>
        {
            if (string.IsNullOrWhiteSpace(req.Train)) return Results.BadRequest(new { ok = false, message = "列車番号を入れてください" });
            var (ok, message) = ai.Add(req.Train, req.Cars ?? 0, req.Station?.Trim());
            return ok ? Results.Ok(new { ok, message }) : Results.BadRequest(new { ok, message });
        });
        g.MapPost("/ai-trains/remove", (AiTrainRequest req, AiTrains ai) =>
        {
            if (req.Id == null) { ai.RemoveAll(); return Results.Ok(new { ok = true }); }
            return ai.Remove(req.Id) ? Results.Ok(new { ok = true }) : Results.NotFound(new { ok = false, message = "その列車はもういません" });
        });

        // 駅の扱い（列車扱い・集中扱い）と自動退避の数値（管理画面・ダイヤグラムの設定画面の両方から）
        g.MapPost("/route/station-mode", (StationModeRequest req, CentralControl central, RailData rail) =>
        {
            if (!rail.Stations.ContainsKey(req.Station)) return Results.BadRequest(new { ok = false, message = "駅IDが違います" });
            if (req.Mode is { } m)
            {
                if (m is not ("train" or "central" or "local")) return Results.BadRequest(new { ok = false, message = "扱いが違います" });
                central.SetMode(req.Station, m);
            }
            else central.SetCentral(req.Station, req.Central);
            return Results.Ok(new { ok = true });
        });

        // 接続待ちの組を切る・戻す（切った組は待たずに発車する。再起動で戻る）
        g.MapPost("/route/connection", (ConnectionCutRequest req, ConnectionTable connections, IncidentManager incidents) =>
        {
            if (!connections.SetCut(req.Key ?? "", req.Cut)) return Results.BadRequest(new { ok = false, message = "接続の組が見つかりません" });
            var p = (req.Key ?? "").Split('|');
            incidents.Note($"接続: {p.ElementAtOrDefault(1)} の {p.ElementAtOrDefault(2)} 待ちを" + (req.Cut ? "切りました（待たずに発車）" : "戻しました"));
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/route/passing", (PassingRequest req, RailData rail, IncidentManager incidents) =>
        {
            if (req.MarginSeconds is { } m) rail.PassingMarginSeconds = Math.Clamp(m, 0, 900);
            if (req.ToleranceSeconds is { } t) rail.PassingToleranceSeconds = Math.Clamp(t, 0, 900);
            incidents.Note($"待避の数値: 余裕 {rail.PassingMarginSeconds}秒、許容の遅れ {rail.PassingToleranceSeconds}秒");
            return Results.Ok(new { ok = true });
        });

        // 手動進路構成（管理画面・ダイヤグラムの設定画面。設定画面は取消のたびに「通告は完了したか？」を確かめる）: 駅ごとの全進路の一覧と、進路名を直接指定した開通・取消
        g.MapGet("/route/catalog", (RailData rail) => Results.Ok(rail.ControlStations.Select(s => new
        {
            id = s.Id,
            name = s.Name,
            interlock = s.Interlock,
            routes = rail.Routes.Values.Where(r => r.Interlock == s.Interlock)
                .OrderBy(r => r.Name, StringComparer.Ordinal)
                .Select(r => new { name = r.Name, from = r.From, to = r.To, kind = r.Kind })
        })));

        g.MapPost("/route/manual-route", (ManualRouteRequest req, RouteEngine engine) =>
            Results.Ok(new { ok = true, message = engine.ManualRoute(req.Train ?? "", req.Interlock ?? "", req.Route ?? "", req.Set) }));

        // 運転モード（フリー運転・ダイヤ運転）の切り替え
        g.MapPost("/mode", (ModeRequest req, CentralControl central) =>
        {
            central.SetDiaMode(req.Dia);
            return Results.Ok(new { ok = true });
        });

        // つながっている参加者を強制的に切断する（次の報告のときに接続を切る）
        // 防護無線の発報を解除する（押したまま離れた参加者など）
        g.MapPost("/radio/clear", (IdRequest req, ProtectionRadio radio, DispatcherProtection dispatcherProtection, StateBroadcaster broadcaster) =>
        {
            if (req.Id.StartsWith("区間防護:"))
            {
                if (!dispatcherProtection.Clear(req.Id["区間防護:".Length..])) return Results.NotFound(new { message = "その区間防護はもうありません" });
                broadcaster.Trigger();
                return Results.Ok(new { ok = true });
            }

            if (!radio.Clear(req.Id)) return Results.NotFound(new { message = "その発報はもうありません" });
            broadcaster.Trigger();
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/players/kick", (IdRequest req, PlayerRegistry players) =>
            players.Kick(req.Id) ? Results.Ok(new { ok = true }) : Results.NotFound(new { message = "その参加者はもういません" }));

        // 継続乗務で残している在線を消す
        g.MapPost("/continue/delete", (IdRequest req, PlayerRegistry players) =>
            players.RemoveGhost(req.Id) ? Results.Ok(new { ok = true }) : Results.NotFound(new { message = "その継続乗務申請はもうありません" }));

        // 閉塞信号機: 停止にしているもの（だれの在線か・方向てこ）と、強制進行にしているもの
        // 司令用管理画面が1秒ごとに読む（開いている画面の数だけ）ので、0.5 秒は同じ答えを使い回す（操作のあとは捨てる）
        g.MapGet("/signals/block", (BlockSignals blocks, PlayerRegistry players, RouteEngine engine, ResponseCache cache) => cache.Get("signals-block", TimeSpan.FromMilliseconds(500), () =>
        {
            var occupants = new Dictionary<string, List<string>>(); // 回路 → 在線している列車
            foreach (var p in players.Snapshot().Where(p => p.Mode == Gokko.Common.ShareMode.Shared && p.InGame))
                foreach (var c in p.TrackCircuits)
                {
                    if (!occupants.TryGetValue(c, out var l)) occupants[c] = l = new List<string>();
                    var train = (p.TrainNumber.Length > 0 ? p.TrainNumber : "（列番なし）") + (p.ConnectionId.StartsWith(PlayerRegistry.GhostPrefix) ? "（継続乗務）" : p.ConnectionId.StartsWith(PlayerRegistry.AiPrefix) ? "（にぎやかし）" : "");
                    if (!l.Contains(train)) l.Add(train);
                }

            var lever = engine.LeverStopSignals().ToHashSet();
            var forced = blocks.Forced();
            return new
            {
                signals = blocks.All.Select(s => new
                {
                    name = s.Name,
                    protects = s.StopProtects,
                    trains = s.StopProtects.SelectMany(c => occupants.GetValueOrDefault(c) ?? new List<string>()).Distinct().ToList(),
                    lever = lever.Contains(s.Name),
                    forced = forced.TryGetValue(s.Name, out var since) ? since : (DateTime?)null
                }).ToList(),
                sweepSeconds = (int)BlockSignals.SweepInterval.TotalSeconds,
                sweepEnabled = blocks.SweepEnabled
            };
        }));

        // 閉塞信号機を強制進行にする・やめる（サーバーからの停止を出さず、None を送り続ける）
        g.MapPost("/signals/force", (ForceSignalRequest req, BlockSignals blocks, StateBroadcaster broadcaster) =>
        {
            var r = blocks.SetForced((req.Name ?? "").Trim(), req.On);
            if (r == null) return Results.BadRequest(new { message = "閉塞信号機を選んでください" });
            broadcaster.Trigger();
            return Results.Ok(new { ok = true });
        });

        // 閉塞信号機の定期解除を入れる・切る（サーバーを止めずに。再起動すると入に戻る）
        g.MapPost("/signals/sweep", (SweepRequest req, BlockSignals blocks) =>
        {
            blocks.SetSweepEnabled(req.On);
            return Results.Ok(new { ok = true, sweepEnabled = blocks.SweepEnabled });
        });

        // 在線の記録（新しい順。サーバーの logs/occupancy-yyyyMMdd.log にも日ごとに残る）
        g.MapGet("/occupancy/log", (OccupancyLog occupancy) => Results.Ok(new { lines = occupancy.View() }));

        MapDiagramViews(g); // ダイヤグラムの表示（/diagram・/diagram/plan）

        // スジずらし: ◆を横にずらして、その駅の発車時刻を決める（time が null なら指定解除）
        g.MapPost("/route/open-at", (OpenAtRequest req, CentralControl central) =>
        {
            central.SetOpenAt(req.Train, req.Station, req.Time?.UtcDateTime);
            return Results.Ok(new { ok = true });
        });

        // 後運用の差し替え（next が null なら取消）。もともと next に折り返すはずだった列車は、train のもとの後運用を受け持つ。
        // direct（番線）を付けると直接折返: 入換で折り返す列車を、終点のその番線に着けて、そのまま後運用で出す（大道寺の 3番線）。
        // 着く列車と後運用の列車の両方に、その駅の番線を集中扱いで指定する。取消で指定も外す
        // その間に同じ番線を使うはずだった列車は、後運用のもとの番線（大道寺なら 4番線）へ移す
        g.MapPost("/route/turn", (TurnRequest req, RailData rail, CentralControl central, RouteEngine engine) =>
        {
            if (string.IsNullOrWhiteSpace(req.Train)) return Results.BadRequest(new { message = "列車を指定してください" });
            string? error;
            // 途中折返し（station と next と direct）: train を station で打ち切り、direct 番線で next に折り返す。next がもう走っていれば断る
            if (req.Station is { Length: > 0 } midSt && req.Direct is { } midTrack && !string.IsNullOrWhiteSpace(req.Next))
            {
                var nextNo = RailData.NormalizeTrain(req.Next);
                if (engine.IsRunning(nextNo)) return Results.BadRequest(new { message = $"{nextNo} はもう走っています" });
                if (rail.Turns.SetMid(req.Train, nextNo, midSt, midTrack, out error) is { } m)
                {
                    central.SetStop(m.Train, m.Station, "打ち切り");
                    central.SetTrack(m.Train, m.Station, m.Track);
                    central.SetTrack(m.Next, m.Station, m.Track);
                }
            }
            else if (req.Direct is { } track)
            {
                if (rail.Turns.SetDirect(req.Train, track, engine.DelayOf(RailData.NormalizeTrain(req.Train)),
                        (t, st) => central.TrackOverride(t, st) != null, out error) is { } d)
                {
                    central.SetTrack(d.Train, d.Station, d.Track);
                    central.SetTrack(d.Next, d.Station, d.Track);
                    foreach (var (mt, mk) in d.Moved) central.SetTrack(mt, d.Station, mk);
                }
            }
            else if (string.IsNullOrWhiteSpace(req.Next))
            {
                error = rail.Turns.Cancel(req.Train, out var undone);
                if (undone is { } u)
                {
                    if (u.Midway && central.StopOverride(u.Train, u.Station) == "打ち切り") central.SetStop(u.Train, u.Station, null);
                    if (central.TrackOverride(u.Train, u.Station) == u.Track) central.SetTrack(u.Train, u.Station, null);
                    if (central.TrackOverride(u.Next, u.Station) == u.Track) central.SetTrack(u.Next, u.Station, null);
                    foreach (var (mt, mk) in u.Moved)
                        if (central.TrackOverride(mt, u.Station) == mk) central.SetTrack(mt, u.Station, null);
                }
            }
            else error = rail.Turns.Set(req.Train, req.Next);
            return error != null ? Results.BadRequest(new { message = error }) : Results.Ok(new { ok = true });
        });

        // 進路を要求した記録（新しい順）
        g.MapGet("/route/log", (RouteEngine engine) => Results.Ok(new { lines = engine.RouteLogView() }));

        // 列車別設定)（列車ごとの、この先の駅の番線・状態・駅扱いの選択肢）
        // 司令用管理画面が1秒ごとに読む（1回 約6ms）ので、0.5 秒は同じ答えを使い回す（操作のあとは捨てる）
        g.MapGet("/trains", (RouteEngine engine, ResponseCache cache) => cache.Get("admin-trains", TimeSpan.FromMilliseconds(500), () => new { trains = engine.AdminView() }));

        // 臨時停車・臨時通過・打ち切り（kind: "停車" / "通過" / "打ち切り"、null で元に戻す）
        g.MapPost("/route/stop", (TrainStationRequest req, CentralControl central) =>
        {
            central.SetStop(req.Train, req.Station, req.Kind);
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/route/track", (TrainStationRequest req, CentralControl central) =>
        {
            central.SetTrack(req.Train, req.Station, req.Track);
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/route/hold", (TrainStationRequest req, CentralControl central) =>
        {
            var kind = req.Kind switch { "arrive" => HoldKind.Arrive, "depart" => HoldKind.Depart, _ => HoldKind.Both };
            central.SetHold(req.Train, req.Station, req.Hold ?? true, kind);
            return Results.Ok(new { ok = true });
        });

        // 駅扱い（強制進路）: 連動に問題がなければ、サーバーの抑止・待ちを無視して開通させる
        g.MapPost("/route/manual", (ManualRequest req, CentralControl central, RailData rail) =>
        {
            if (!rail.Stations.ContainsKey(req.Station)) return Results.BadRequest(new { ok = false, message = "駅IDが違います" });
            if (string.IsNullOrWhiteSpace(req.Action)) return Results.BadRequest(new { ok = false, message = "進路が指定されていません" });
            central.RequestManual(req.Train, req.Station, req.Action.Trim(), req.Label ?? req.Action);
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/route/section-order", (SectionOrderRequest req, CentralControl central, RailData rail) =>
        {
            if (!rail.IsSingleTrack(req.A ?? "", req.B ?? "")) return Results.BadRequest(new { ok = false, message = "単線区間ではありません" });
            central.SetSectionOrder(req.A!, req.B!, req.Trains?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList());
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/route/force", (TrainStationRequest req, CentralControl central) =>
        {
            central.Force(req.Train, req.Station);
            return Results.Ok(new { ok = true });
        });

        // 駅の出発順序（direction: "上り" / "下り"。trains が null なら、その向きを自動に戻す）。
        // direction がなければ、列車ごとの向きで振り分ける（trains も null なら上下とも自動に戻す）
        g.MapPost("/route/order", (OrderRequest req, CentralControl central, RailData rail, RouteEngine engine) =>
        {
            if (!rail.Stations.ContainsKey(req.Station)) return Results.BadRequest(new { ok = false, message = "駅IDが違います" });
            var trains = req.Trains?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            Dir? dir = req.Direction switch { "上り" => Dir.Up, "下り" => Dir.Down, _ => null };
            if (dir is { } d) { central.SetOrder(req.Station, d, trains); return Results.Ok(new { ok = true }); }
            foreach (var x in new[] { Dir.Down, Dir.Up })
            {
                var mine = trains?.Where(t => engine.DirOfTrain(t) == x).ToList();
                if (trains == null || mine!.Count > 0) central.SetOrder(req.Station, x, trains == null ? null : mine);
            }

            return Results.Ok(new { ok = true });
        });
    }

    /// <summary>ダイヤグラムの表示だけ（読むだけの画面と共通）。</summary>
    private static void MapDiagramViews(RouteGroupBuilder g)
    {
        // 開いている画面の数だけ（/api/admin・/api/dia・/api/diaview とも同じ答え）1秒に1回読まれ、作るのに約20ms かかる（11/22 のデバッグ運転）。
        // 画面は1秒ごとに読むので、1秒は同じ答えを使い回す（操作した直後は ResponseCache.Clear で作り直す）
        g.MapGet("/diagram", (RouteEngine engine, RailData rail, PlayerRegistry players, CentralControl central, ResponseCache cache, EventStops events) => cache.Get("diagram", TimeSpan.FromSeconds(1), () => new
        {
            now = DateTimeOffset.UtcNow,
            stations = rail.Line.StationIds.Select(id => new
            {
                id,
                name = rail.Line.StationName(id),
                y = rail.Line.ZoneOfStation(id),
                control = rail.Stations.TryGetValue(id, out var s) && s.RouteControl,
                // ダイヤグラムを番線ごとに分けたときの段（番線の競合を見るため）。Stations.json の diagramRows、なければ進路のある番線すべて
                tracks = !rail.Stations.TryGetValue(id, out var st) ? new List<string>()
                    : st.DiagramRows.Count > 0 ? st.DiagramRows.Select(r => r.Label).ToList()
                    : st.Routes.Values.SelectMany(r => r.Arrive.Keys.Concat(r.Depart.Keys)).Concat(st.TrackCircuit.Keys).Distinct().OrderBy(t => t).Select(t => t.ToString()).ToList()
            }),
            trains = engine.View(RouteEngine.ViewDiagram),
            orders = engine.View(RouteEngine.ViewOrders),
            sections = engine.View(RouteEngine.ViewSections),
            // これから進路を取る順番（取れずに待っている場内・出発）
            queue = engine.View(RouteEngine.ViewQueue),
            // 継続乗務申請（残している在線）と、出現待ちの列車
            continues = players.Ghosts().Select(x => new
            {
                id = x.Id,
                train = x.Info.TrainNumber,
                circuits = x.Info.TrackCircuits,
                since = x.SinceUtc,
                until = x.UntilUtc
            }),
            appear = engine.View(RouteEngine.ViewAppear),
            diaMode = central.DiaMode,
            turnVersion = rail.Turns.Version + rail.TrialVersion + events.Version + central.CutVersion, // 後運用を差し替えた・打ち切りを変えた・試運転の選択・催物用臨停設定を変えたら増える（計画の線を読み直す合図）
            // つながっている参加者（強制切断の一覧用）
            players = players.Snapshot().Where(p => !PlayerRegistry.IsVirtual(p.ConnectionId)).OrderBy(p => p.JoinedUtc).Select(p => new
            {
                id = p.ConnectionId,
                train = p.TrainNumber,
                typed = p.TrainNumberTyped,
                mode = p.Mode == Gokko.Common.ShareMode.Shared ? "在線共有" : "特発のみ",
                inGame = p.InGame,
                position = rail.Line.DescribeZones(p.Zones),
                appear = p.AppearReason
            }),
            // 駅の扱いと自動退避の数値
            stationModes = rail.ControlStations.Select(s => new { id = s.Id, name = s.Name, central = central.IsCentral(s.Id), mode = central.ModeOf(s.Id), panel = central.IsPanelControlled(s.Id) }),
            passing = new { marginSeconds = rail.PassingMarginSeconds, toleranceSeconds = rail.PassingToleranceSeconds },
            // 接続待ち（ダイヤの「(接)」の組。遅れ見込みの警告つき）
            connections = engine.View(RouteEngine.ViewConnections)
        }));

        // ダイヤグラムに薄く引く、時刻表（Timetable.json）の計画の線。大きいので、画面を開いたときに1回だけ読む。
        // 路線の駅だけ（y はゾーン）。時刻はロープレ時刻 "HH:mm:ss"（書かれていなければ null）
        // 全ダイヤから作るので、後運用の差し替え・打ち切り・試運転の選択・催物用臨停設定（/diagram の turnVersion と同じ数）が変わるまで使い回す
        // （変わると開いている画面が一斉に読み直すので、そのたびに作っていた）
        g.MapGet("/diagram/plan", (RailData rail, EventStops events, CentralControl central, ResponseCache cache) => cache.GetVersioned("diagram-plan",
            rail.Turns.Version + rail.TrialVersion + events.Version + central.CutVersion, () => new
        {
            // 折り返しの入換にかかる秒数（ゾーン → "から>へ" → 秒）
            shuntSeconds = rail.Stations.Values.Where(s => s.ShuntSeconds.Count > 0 && rail.Line.ZoneOfStation(s.Id) >= 0)
                .ToDictionary(s => rail.Line.ZoneOfStation(s.Id).ToString(), s => s.ShuntSeconds),
            trains = rail.Dia.Values.Where(t => !rail.IsIdleTrial(t)).Select(t => (t, cut: PlanCut(rail, central, t))).Select(x => new // 走らない試運転は引かない
            {
                train = x.t.Number,
                type = x.t.Type,
                next = x.cut.To == null ? x.t.Next?.Number : null,
                // 終点で折り返して次の列車になる（運行のつなぎ）。time はその発車・入換の時刻、how は「発」か「入換」
                // via は、入換で折り返すときに入る引上線などの段（TrackRoutes.json の shuntOut。着いた番線からの最初のもの。"5" や "Y"）
                // 路線の中の駅で折り返すときだけ（三郷より先など、路線の外で折り返す運行はつながない）
                // 後運用を差し替えていれば、差し替えたもの
                // 途中折返し: 打ち切った駅で、折り返して走る列車へつなぐ。打ち切っただけなら、つながない
                turn = x.cut.Mid is { } mid ? new { number = mid.Next, time = rail.DiaOf(mid.Next)?.Stops.FirstOrDefault(s => s.StationId == mid.Station) is { } ms ? ms.Dep ?? ms.Arr : null, how = (string?)"発", via = (string?)null }
                    : x.cut.To != null ? null
                    : rail.Turns.TurnOf(x.t) is { Kind: "折返", Number: { } tn } tt && TurnsOnLine(rail, x.t, tn) ? new { number = tn, time = tt.Time, how = tt.How, via = tt.How == "入換" ? ShuntRow(rail, x.t) : null } : null,
                // 催物用臨停設定で臨時停車になる駅は、着＝通過時刻 − 停車時間（止まる線にする）
                // 打ち切り・途中折返しで走らない区間は引かない（打ち切った駅まで・折り返して走り始める駅から）
                stops = x.t.Stops.Select((s, i) => (s, i, ev: events.Times(x.t, i)))
                    .Where(y => y.i >= x.cut.From && (x.cut.To == null || y.i <= x.cut.To))
                    .Where(y => y.s.StationId != null && rail.Line.ZoneOfStation(y.s.StationId) >= 0 && (y.s.Arr != null || y.s.Dep != null || y.i == x.cut.To))
                    .Select(y => new
                    {
                        y = rail.Line.ZoneOfStation(y.s.StationId!),
                        arr = y.i == x.cut.From && x.cut.From > 0 ? null : y.ev?.Arr ?? y.s.Arr ?? (y.i == x.cut.To ? y.s.Dep : null),
                        dep = y.i == x.cut.To ? null : y.s.Dep,
                        track = y.i == x.cut.To && x.cut.Mid is { } m1 ? m1.Track : y.i == x.cut.From && x.cut.Into is { } m2 ? m2.Track : y.s.Track
                    }).ToList()
            }).Where(t => t.stops.Count >= 2).ToList()
        }));
    }
}
