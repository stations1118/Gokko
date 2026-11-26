using Gokko.Common;
using Gokko.Server;
using Gokko.Server.Hubs;
using Gokko.Server.Services;
using Microsoft.AspNetCore.SignalR;

// 設定・データ・管理画面は、exe と同じ場所から読む（systemd などで作業フォルダが違っても動くように）
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

var options = builder.Configuration.GetSection("Gokko").Get<ServerOptions>() ?? new ServerOptions();
var dataDir = Path.IsPathRooted(options.DataDirectory)
    ? options.DataDirectory
    : Path.Combine(AppContext.BaseDirectory, options.DataDirectory);

// データファイルの誤りは、起動時に止めて知らせる
var data = GameData.Load(dataDir);
var rail = RailData.Load(dataDir, data.Line);
// 踏切の特発を戻せる軌道回路（上下別の閉塞で）
var crossingNotes = new List<string>();
data.SetCrossingCircuits(CrossingBlocks.Load(Path.Combine(dataDir, "CrossingBlocks.csv"), data.Line, rail, data, crossingNotes));
// 停止位置目標（何両停目がどこにあるか。Data/StopMarkers.up.csv・StopMarkers.down.csv）
var stopMarkers = StopMarkers.Load(dataDir, rail.BaseDia.Values.SelectMany(d => d.Stops).Select(s => s.Name));

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(options.Random); // 管理画面から書き換える（運転会中だけ有効。再起動で appsettings の値に戻る）
builder.Services.AddSingleton(data);
builder.Services.AddSingleton(rail);
builder.Services.AddSingleton(stopMarkers); // 停止位置目標（将来の車掌単独乗務・自動運転列車の停止位置に使う）
builder.Services.AddSingleton(sp => new DutyRosters(dataDir, rail, sp.GetService<ILogger<DutyRosters>>())); // 行路表（Data/Duties*.csv から勤怠管理で選ぶ。既定は Duties.csv。なければ空）
builder.Services.AddSingleton(sp => new CrewRoster(dataDir, sp.GetService<ILogger<CrewRoster>>()) { PasswordUntil = options.Discord.PasswordUntil }); // 乗務員と行路の割り当て（Data/Crew.db）
builder.Services.AddSingleton<DiscordAuth>(); // 乗務員の Discord ログイン
builder.Services.AddSingleton<PlayerRegistry>();
builder.Services.AddSingleton<GameClock>();
builder.Services.AddSingleton<IncidentManager>();
builder.Services.AddSingleton<RunTimeModel>();
builder.Services.AddSingleton<SignalCalculator>();
builder.Services.AddSingleton<CentralControl>();
builder.Services.AddSingleton<TokuhatsuRinji>(); // 特発臨（運転整理のために司令員がその場で立てる臨時列車。メモリだけ）
builder.Services.AddSingleton<EventStops>(); // 催物用臨停設定（司令用管理画面。再起動で消える）
builder.Services.AddSingleton<ConnectionTable>(); // 接続待ちの組（ダイヤの「(接)」から）
builder.Services.AddSingleton<RouteEngine>();
builder.Services.AddSingleton<ProtectionRadio>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RadioPhones>(); // スマホの列車無線（受話器）
builder.Services.AddSingleton<RadioMonitors>(); // 列車無線のモニター（受信だけ。radio-monitor.html）
builder.Services.AddSingleton<TrainRadio>();
builder.Services.AddSingleton<DispatcherProtection>();
builder.Services.AddSingleton<RadioPublisher>();
builder.Services.AddSingleton<CrewRelay>();
builder.Services.AddSingleton<OnboardAudio>(); // 列車ごとの放送装置（車内放送・車外放送・インターホン）
builder.Services.AddSingleton<BlockSignals>(); // 閉塞信号機の定期解除と強制進行
builder.Services.AddSingleton<OccupancyLog>(); // 在線の記録（logs/occupancy-*.log）
builder.Services.AddSingleton(sp => new AiTrains(dataDir, rail, sp.GetRequiredService<PlayerRegistry>(), sp.GetRequiredService<GameClock>(),
    sp.GetRequiredService<RouteEngine>(), sp.GetRequiredService<CentralControl>(), sp.GetRequiredService<IncidentManager>(),
    sp.GetRequiredService<CrewRoster>(), sp.GetRequiredService<DutyRosters>(), sp.GetRequiredService<CrewRelay>(), sp.GetRequiredService<ProtectionRadio>(),
    sp.GetRequiredService<DispatcherProtection>(), sp.GetService<ILogger<AiTrains>>())); // にぎやかしの列車（サーバーだけで走る。Data/AiTrains.json）
builder.Services.AddSingleton(sp => new TempTrains(dataDir, rail, sp.GetService<ILogger<TempTrains>>())); // 催物用の臨時列車（Data/TempTrains.json。運転課で作り、司令用管理画面で適用）
builder.Services.AddSingleton<ServerLoad>(); // サーバーの負荷（管理画面・logs/load-*.log）
builder.Services.AddSingleton<ResponseCache>(); // 何人もの画面が読みに来る API の答えを、少しのあいだ使い回す（ダイヤグラム）
builder.Services.AddSingleton<StateBroadcaster>();
builder.Services.AddSingleton<RelayPanels>(); // 信号制御盤（relaypanel.html）。盤の状態はサーバーだけが持ち、開いている全員に配る
builder.Services.AddSingleton(sp => new ArrivalBoards(dataDir, sp.GetRequiredService<IWebHostEnvironment>(), rail, sp.GetService<ILogger<ArrivalBoards>>())); // 駅の接近案内（tablet-arrivals.html）で見る回路
builder.Services.AddHostedService(sp => sp.GetRequiredService<StateBroadcaster>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<AiTrains>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerLoad>());
builder.Services.AddHostedService<RandomTokuhatsuService>();
builder.Services.AddHostedService<PhoneRadioTicker>();
builder.Services.AddSingleton(sp => new ExtensionBook(dataDir, sp.GetService<ILogger<ExtensionBook>>())); // 内線番号（Data/Extensions.json。管理画面で決める）
builder.Services.AddSingleton<ExtensionExchange>(); // 内線電話（駅・司令・乗務員。列車無線とは別の回線）
builder.Services.AddSingleton<ExtensionPublisher>();
builder.Services.AddHostedService<ExtensionTicker>();

builder.Services.AddSignalR(o =>
{
    o.KeepAliveInterval = TimeSpan.FromSeconds(10);
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    o.MaximumReceiveMessageSize = 64 * 1024;
    o.AddFilter<HubTiming>(); // ハブの呼び出しごとの時間（サーバーの負荷）
    o.AddFilter<DispatcherActivityHubFilter>(); // 司令員の操作を覚える（30分操作がなければ自動で退勤）
});

var app = builder.Build();

app.Logger.LogInformation("データ: 踏切 {Crossings}件、区間設定 {Sections}件、理由 {Reasons}件（{Dir}）",
    data.Crossings.Count, data.CrossingZones.Count, data.Reasons.Count, dataDir);
foreach (var n in crossingNotes) app.Logger.LogWarning("{Note}", n);
app.Logger.LogInformation("踏切の閉塞: {Count}件（CrossingBlocks.csv）", data.CrossingCircuits.Count);
app.Logger.LogInformation("停止位置目標: 上り {Up}件、下り {Down}件", stopMarkers.Entries.Count(e => e.Dir == Dir.Up), stopMarkers.Entries.Count(e => e.Dir == Dir.Down));
foreach (var n in stopMarkers.Problems) app.Logger.LogWarning("停止位置目標: {Note}", n);
app.Logger.LogInformation("進路: 駅 {Stations}件（うち扱う駅 {Control}件）、進路 {Routes}件、信号機 {Signals}件、標準運転時分 {RunTimes}区間",
    rail.Stations.Count, rail.ControlStations.Count(), rail.Routes.Count, rail.Signals.Count, rail.RunTimes.Count);

// 集中扱いの操作は、記録に残す
var central = app.Services.GetRequiredService<CentralControl>();
var incidentLog = app.Services.GetRequiredService<IncidentManager>();
central.Changed += message => incidentLog.Note(message);
rail.Turns.Changed += message => incidentLog.Note(message);
app.Services.GetRequiredService<EventStops>().Changed += message => incidentLog.Note(message);
app.Services.GetRequiredService<PlayerRegistry>().Changed += message => incidentLog.Note(message);
// 催物用の臨時列車: 起動したときに、適用していたものを重ね直す。取り消したら、走っている自動運転の列車も消す
var aiTrains = app.Services.GetRequiredService<AiTrains>();
app.Services.GetRequiredService<TempTrains>().Removed += numbers => aiTrains.RemoveNumbers(numbers);
var crewRoster = app.Services.GetRequiredService<CrewRoster>();
crewRoster.DispatcherAutoLeft += id => incidentLog.Note($"司令員 ID:{id} を自動で退勤させました（{(int)CrewRoster.DispatcherIdleLimit.TotalMinutes}分操作なし）");
if (string.IsNullOrEmpty(options.AdminKey))
    app.Logger.LogWarning("Gokko:AdminKey が未設定です。管理画面は使えません");
if (string.IsNullOrEmpty(options.DiaPassword))
    app.Logger.LogInformation("Gokko:DiaPassword が未設定です。司令用管理画面（diasettings.html）は使えません");
if (!options.Discord.Enabled)
    app.Logger.LogInformation("Gokko:Discord が未設定です。乗務員は社員番号とパスワードでだけログインできます");
else if (options.Discord.PasswordUntil is { } until)
    app.Logger.LogInformation("パスワードでのログインは {Until} まで", until);
if (string.IsNullOrEmpty(options.Password))
    app.Logger.LogWarning("Gokko:Password が未設定です。URL を知っていれば誰でも参加できます");

// 画面の API ごとの時間（サーバーの負荷）。操作のあとは、使い回している答えを捨てる
app.UseLoadTiming(app.Services.GetRequiredService<ServerLoad>(), app.Services.GetRequiredService<ResponseCache>());

// 司令員の操作（画面の GET 以外）を覚える。30分操作がなければ自動で退勤（CrewRoster.DispatcherIdleLimit）
app.UseDispatcherActivity(crewRoster);

// 管理画面・ダイヤグラムの画面は、差し替えたらすぐ反映されるように、毎回確かめさせる（古い diapanel.js が残らないように）
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
});
app.MapGet("/", () => Results.Redirect("/admin.html"));
app.MapGet("/healthz", (PlayerRegistry players, IncidentManager incidents) =>
    Results.Ok(new { ok = true, players = players.Count, activeIncidents = incidents.ActiveCount, protocol = Protocol.Version }));
app.MapHub<GameHub>(Protocol.HubPath);
app.MapHub<CrewHub>(CrewProtocol.HubPath);
app.MapHub<PhoneHub>(Protocol.PhoneHubPath);
app.MapHub<RadioMonitorHub>(RadioMonitorHub.Path);
app.MapHub<ExtensionHub>(ExtensionProtocol.HubPath);
app.MapHub<OnboardPhoneHub>(OnboardPhoneHub.Path); // 乗務タブレット（スマホ）のマイクで放送装置に話す
app.MapAdminEndpoints(options);
app.MapDiscordEndpoints(options);
app.MapExtensionEndpoints(options);
app.MapOnboardEndpoints(); // 乗務タブレット（スマホ）から放送装置を扱う
app.MapRelayPanelEndpoints();
app.Services.GetRequiredService<RelayPanels>(); // 起動時に駅のデータを読む（誤りをすぐ記録に出す）

// 特発臨: 自動運転の列車は、発駅で特発臨の列番に変わる。走り出してから取り消したら、自動運転の列車を消す
var rinji = app.Services.GetRequiredService<TokuhatsuRinji>();
var rinjiAi = app.Services.GetRequiredService<AiTrains>();
rinjiAi.Rinji = rinji;
rinji.Changed += message => incidentLog.Note(message);
rinji.Removed += numbers => rinjiAi.RemoveNumbers(numbers);

app.Run();
