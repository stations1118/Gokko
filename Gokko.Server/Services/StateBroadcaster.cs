using Gokko.Common;
using Gokko.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Gokko.Server.Services;

/// <summary>
/// 参加者全員へ状態を配信する。一定間隔（既定0.5秒）ごとに送るほか、特発が変わった瞬間にも送る。
/// 毎回「その人に必要な状態の全体」を送るので、クライアントは届いたもので置き換えるだけでよい。
/// 在線共有モードの参加者には、進路の判断（RouteEngine）と信号の連動（SignalCalculator）の結果も入れる。
/// </summary>
public sealed class StateBroadcaster : BackgroundService
{
    private readonly IHubContext<GameHub> _hub;
    private readonly PlayerRegistry _players;
    private readonly IncidentManager _incidents;
    private readonly GameClock _clock;
    private readonly GameData _data;
    private readonly RouteEngine _routes;
    private readonly SignalCalculator _signals;
    private readonly ServerOptions _options;
    private readonly ILogger<StateBroadcaster> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);

    private readonly CentralControl _central;
    private readonly ProtectionRadio _radio;
    private readonly CrewRelay _crew;
    private readonly TrainRadio _trainRadio;
    private readonly DispatcherProtection _dispatcherProtection;
    private readonly BlockSignals _blocks;
    private readonly OccupancyLog _occupancy;
    private readonly OnboardAudio _onboard;
    private readonly AiTrains _ai;
    private readonly ServerLoad _load;

    public StateBroadcaster(IHubContext<GameHub> hub, PlayerRegistry players, IncidentManager incidents, GameClock clock,
        GameData data, RouteEngine routes, SignalCalculator signals, ServerOptions options, CentralControl central, ProtectionRadio radio,
        CrewRelay crew, TrainRadio trainRadio, DispatcherProtection dispatcherProtection, BlockSignals blocks, OccupancyLog occupancy, OnboardAudio onboard,
        AiTrains ai, ServerLoad load, ILogger<StateBroadcaster> logger)
    {
        _ai = ai;
        _load = load;
        _onboard = onboard;
        _blocks = blocks;
        _occupancy = occupancy;
        _trainRadio = trainRadio;
        _dispatcherProtection = dispatcherProtection;
        _crew = crew;
        _central = central;
        _radio = radio;
        _hub = hub;
        _players = players;
        _incidents = incidents;
        _clock = clock;
        _data = data;
        _routes = routes;
        _signals = signals;
        _options = options;
        _logger = logger;
        _incidents.Changed += Trigger;
    }

    /// <summary>次の間隔を待たずに、すぐ配信する。</summary>
    public void Trigger()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // すでに配信待ち
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(_options.BroadcastIntervalMs, 100, 5000));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                _routes.Tick();
                var tickMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                BuildViews();
                var sendMs = await BroadcastAsync();
                // 負荷の記録（管理画面・logs/load-*.log）。全体と、そのうち進路の処理（Tick）と送り終わるのを待った時間
                _load.RecordBroadcast(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, tickMs, sendMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "配信でエラー");
            }
        }
    }

    /// <summary>
    /// 画面用の答え（在線位置の画面・ダイヤグラム）を作り置く（RouteEngine.View。画面の API が RouteEngine のロックを待たないように）。
    /// 失敗しても配信は止めない（画面の API は、作り置きがなければその場で作る）。
    /// </summary>
    private void BuildViews()
    {
        try
        {
            foreach (var (kind, ms) in _routes.BuildViews()) _load.RecordWork($"(作り置き) {kind}", ms); // 種類ごとに（どれが重いかを見るため）
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "画面用の答えの作り置きでエラー");
        }
    }

    /// <summary>全員へ配信する。戻り値は、送り終わるのを待った時間（ミリ秒。遅い回線の人がいると長くなる）。</summary>
    private async Task<double> BroadcastAsync()
    {
        var prepStarted = System.Diagnostics.Stopwatch.GetTimestamp(); // 内訳の記録（どこが重いかを見るため。RecordParts）
        var players = _players.Snapshot();
        _occupancy.Record(players); // 在線の記録（変わった列車だけ logs/occupancy-*.log へ）
        var conductors = _crew.Conductors().Where(c => !_players.IsJoined(c.ConnectionId)).ToList(); // 自動運転の車掌は運転士と同じに送る（下の運転士の配信）
        var riders = _crew.Riders();
        var dispatchers = _trainRadio.Dispatchers();
        // 接続している人の数: 運転士・車掌・司令員（にぎやかしの列車・継続乗務の在線は数えない）
        var playerCount = players.Count(p => !PlayerRegistry.IsVirtual(p.ConnectionId)) + conductors.Count + dispatchers.Count;
        if (players.Count == 0 && conductors.Count == 0 && riders.Count == 0 && dispatchers.Count == 0) return 0;
        var radioViews = _trainRadio.Views(); // 列車無線（その人から見た状態）
        var onboardViews = _onboard.Views(); // 列車ごとの放送装置（その人から見た状態）
        var conductorCount = conductors.Where(c => c.TrainNumber.Length > 0).GroupBy(c => c.TrainNumber).ToDictionary(g => g.Key, g => g.Count());
        var riderCount = riders.Where(c => c.TrainNumber.Length > 0).GroupBy(c => c.TrainNumber).ToDictionary(g => g.Key, g => g.Count());
        var driverStates = new Dictionary<string, ClientState>(); // 列番 → 運転士への配信（車掌へも同じものを送る）
        var conductorDeletes = new Dictionary<string, List<RouteKey>>(); // 列番 → 車掌へ送る進路の取り消し（運転士の位置で決めた取り消しは除く）

        // 同じ列番の運転士・車掌・添乗の通信状況（クライアントの「通信状況」で、相手のようすを見せる）
        var utcNow = DateTime.UtcNow;
        var crewByTrain = new Dictionary<string, List<(string Id, CrewPeerView View)>>();
        void AddPeer(string id, string train, CrewPeerView view)
        {
            if (train.Length == 0) return;
            if (!crewByTrain.TryGetValue(train, out var list)) crewByTrain[train] = list = new();
            list.Add((id, view));
        }
        int AgeMs(DateTime utc) => (int)Math.Clamp((utcNow - utc).TotalMilliseconds, 0, int.MaxValue);
        foreach (var p in players.Where(p => !PlayerRegistry.IsVirtual(p.ConnectionId)))
            AddPeer(p.ConnectionId, p.TrainNumber, new CrewPeerView
            {
                Role = CrewRole.Driver, TrainCrewConnected = p.TrainCrewConnected, InGame = p.InGame, ReportAgeMs = AgeMs(p.LastReportUtc),
                TraveledDistance = p.TraveledDistance, Link = p.Link
            });
        foreach (var (list, role) in new[] { (conductors, CrewRole.Conductor), (riders, CrewRole.Rider) })
            foreach (var c in list)
                AddPeer(c.ConnectionId, c.TrainNumber, new CrewPeerView
                {
                    Role = role, Passenger = c.Passenger, TrainCrewConnected = c.TrainCrewConnected, InGame = c.InGame, ReportAgeMs = AgeMs(c.LastReportUtc),
                    TraveledDistance = c.TraveledDistance, Link = c.Link
                });
        List<CrewPeerView> PeersOf(string id, string train) =>
            train.Length > 0 && crewByTrain.TryGetValue(train, out var list) && list.Count > 1
                ? list.Where(x => x.Id != id).Select(x => x.View).ToList()
                : new List<CrewPeerView>();

        var active = _incidents.Snapshot();
        var activeNames = active.Select(i => i.Crossing).ToList();
        var gameTime = _clock.NowText;
        var shared = players.Where(p => p.Mode == ShareMode.Shared && p.InGame).ToList();
        var leverStops = _routes.LeverStopSignals().Concat(_routes.ClearTimeStopSignals()).ToList(); // 方向てこの向きと、現示時素（踏切）で停止にする信号機
        var forced = _blocks.Forced(); // 強制進行にした閉塞信号機
        var sweep = _blocks.SweepActive(DateTime.UtcNow); // 10秒ごとに、停止にしていない閉塞信号機へ None を送り直す（赤の固定を解く）
        var (radioReceivers, radioSenders) = _radio.Evaluate(players); // 防護無線（受信している・発報している参加者）
        radioReceivers.UnionWith(_dispatcherProtection.Receivers(players)); // 司令員からの区間防護も、同じ警報で知らせる

        var loopStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var prepMs = Ms(prepStarted, loopStarted);
        var sends = new List<Task>(players.Count);
        double signalMs = 0, aiMs = 0, sendCallMs = 0;
        // 送信の呼び出し: SignalR はふつう、呼び出しの中で JSON にして書き込む（ほかの送信で混んでいるときは、あとで別のスレッドで書くので数えられない）
        void Send(string connectionId, ClientState state)
        {
            var sendStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            sends.Add(SendAsync(connectionId, state));
            sendCallMs += Ms(sendStarted);
        }
        foreach (var p in players)
        {
            // にぎやかしの列車（自動運転）は、信号・自分の進路・取り消し・出発要求・メッセージ・この先の駅の状態だけを使う（AiTrains.Receive）。
            // 使わないもの（位置の文字・近くの特発・ほかの列車の進路）は作らない（列車の数だけ毎回作っていた）
            var ai = p.ConnectionId.StartsWith(PlayerRegistry.AiPrefix);
            var state = new ClientState
            {
                DiaMode = _central.DiaMode,
                ProtectionRadioReceiving = radioReceivers.Contains(p.ConnectionId),
                ProtectionRadioSending = radioSenders.Contains(p.ConnectionId),
                TrainRadio = radioViews.GetValueOrDefault(p.ConnectionId),
                Onboard = onboardViews.GetValueOrDefault(p.ConnectionId),
                GameTime = gameTime,
                Position = ai ? null : _data.Line.DescribeZones(p.Zones),
                ActiveCrossings = activeNames,
                VisibleIncidents = ai ? new List<IncidentView>() : active
                    .Where(i => _data.IsNear(i.Crossing, p.TrackCircuits, p.Zones))
                    .Select(i => new IncidentView { Crossing = i.Crossing, Reason = i.Reason, StartedGameTime = i.StartedGameTime })
                    .ToList(),
                PlayerCount = playerCount
            };

            if (p.Mode == ShareMode.Shared)
            {
                var signalStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                // 信号: 自分以外の在線共有の列車がいる軌道回路から。
                // 同じ列番を入力している参加者（同じ列車に乗っている運転士と車掌など）の在線は、互いに見ない。
                // 2台のゲームは同じ列車を別々に動かしているので位置がずれ（実測で最大 180m）、相手の在線で前の信号が R になって、
                // 同じ列車に乗っているはずの相手の列車が止められてしまうため
                // 入換で動かしている列車（ゲームの中だけで X線へ走っている）は、ほかの列車の在線も方向てこも見ない（サーバーでは X線に止まったまま）
                var shuntMoving = p.ShuntMove == ShuntMoveState.Moving;
                var others = shared.Where(o => o.ConnectionId != p.ConnectionId && !SameTrain(o, p)).SelectMany(o => o.TrackCircuits).ToHashSet();
                if (shuntMoving) others.Clear();
                // 連結する列車には、番線で待っている増結車の在線を見せない（入換信号機で、増結車のいる番線へ入るため）
                others.ExceptWith(_routes.CouplingIgnored(p.ConnectionId));
                state.Signals = _signals.Calculate(others, p.GameRoutes);

                // 単線区間の閉塞信号機は、方向てこと逆向きなら停止に固定する。
                // 取り消した進路の信号機は、ゲームから進路が消えるのを待たずに、すぐ停止にする
                foreach (var name in (shuntMoving ? [] : leverStops).Concat(_routes.StopSignals(p.ConnectionId)))
                {
                    state.Signals.RemoveAll(s => s.Name == name);
                    state.Signals.Add(new SignalCommand { Name = name, Phase = "R" });
                }

                // 強制進行と定期解除（停止にする判断を全部入れたあとの結果から、停止にしていない閉塞信号機だけに None を入れる）
                _blocks.Apply(state.Signals, forced, sweep);
                signalMs += Ms(signalStarted);

                if (_routes.GetOutput(p.ConnectionId) is { } output)
                {
                    state.Routes = output.Routes;
                    state.DeleteRoutes = output.Deletes;
                    if (!ai) state.OtherRoutes = _routes.OtherRoutes(p.ConnectionId);
                    state.Plan = output.Plan;
                    state.CanRequestDeparture = output.CanRequestDeparture;
                    state.DepartureLabel = output.DepartureLabel;
                    state.RouteMessage = output.Message;
                }
                else
                {
                    state.RouteMessage = p.TrainNumber.Length == 0 ? "列車番号が分かりません" : "運転画面に入ると、進路の管理が始まります";
                }

                // 入換で動かすボタン（X線入換・X線停車中）
                if (!ai && _players.ShuntButtonOf(p.ConnectionId) is { } shunt)
                {
                    state.ShuntButtonLabel = shunt.Label;
                    state.ShuntButtonEnabled = shunt.Enabled;
                    state.ShuntCancelVisible = shunt.CanCancel;
                    if (_players.ShuntMessageOf(p.ConnectionId) is { } shuntMessage) state.RouteMessage = shuntMessage;
                }
            }

            // にぎやかしの列車: 配信をクライアントの代わりに受け取る（信号・進路）
            if (ai)
            {
                var aiStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                _ai.Receive(p.ConnectionId, state, p.Mode == ShareMode.Shared ? _routes.GetOutput(p.ConnectionId)?.PlanDetail : null);
                aiMs += Ms(aiStarted);
                continue;
            }

            if (p.TrainNumber.Length > 0 && !PlayerRegistry.IsVirtual(p.ConnectionId))
            {
                state.Conductors = conductorCount.GetValueOrDefault(p.TrainNumber);
                state.Riders = riderCount.GetValueOrDefault(p.TrainNumber);
                state.CrewPeers = PeersOf(p.ConnectionId, p.TrainNumber);
                if (p.Mode == ShareMode.Shared && driverStates.TryAdd(p.TrainNumber, state))
                    conductorDeletes[p.TrainNumber] = _routes.GetOutput(p.ConnectionId)?.ConductorDeletes ?? new List<RouteKey>();
            }

            Send(p.ConnectionId, state);
        }

        // 車掌・添乗: 同じ列番の運転士と同じ進路・信号・特発を送る（運転士がいなければ、時刻と特発だけ）
        foreach (var c in conductors.Concat(riders))
        {
            var rider = _crew.IsRider(c.ConnectionId);
            ClientState state;
            if (c.TrainNumber.Length > 0 && driverStates.TryGetValue(c.TrainNumber, out var d))
            {
                // 丸ごと複製する（あとで配信の項目が増えても、車掌に同じものが届くように）。運転士だけのものは消す。
                // 浅い複製なので、一覧の中身は書き換えず、プロパティごと差し替える（以前は JSON に書いて読み直していて、車掌の人数だけ重かった）
                state = d.Clone();
                state.ProtectionRadioSending = false;
                state.CanRequestDeparture = false;
                state.DeleteRoutes = conductorDeletes.GetValueOrDefault(c.TrainNumber) ?? new List<RouteKey>();
            }
            else
            {
                state = new ClientState
                {
                    DiaMode = _central.DiaMode, GameTime = gameTime, ActiveCrossings = activeNames, PlayerCount = playerCount,
                    RouteMessage = c.TrainNumber.Length == 0 ? "列車番号を入れてください" : $"列番 {c.TrainNumber} の運転士がいません"
                };
            }

            state.TrainRadio = radioViews.GetValueOrDefault(c.ConnectionId); // 列車無線は、車掌自身から見たもの（乗客として乗る添乗には届かない）
            state.Onboard = onboardViews.GetValueOrDefault(c.ConnectionId);
            state.CrewPeers = PeersOf(c.ConnectionId, c.TrainNumber); // 運転士の配信の複製なので、自分から見たものに置き換える
            if (rider && c.Passenger) state.ProtectionRadioReceiving = false; // 乗客には、運転台の防護無線の警報は聞かせない
            Send(c.ConnectionId, state);
        }

        // 司令員: 時刻・特発・列車無線・区間防護だけ
        if (dispatchers.Count > 0)
        {
            var protection = _dispatcherProtection.View();
            var sources = _radio.Sources(); // 運転士の F5 の発報（司令機の防護監視）
            foreach (var id in dispatchers.Keys)
                Send(id, new ClientState
                {
                    DiaMode = _central.DiaMode, GameTime = gameTime, ActiveCrossings = activeNames, PlayerCount = playerCount,
                    TrainRadio = radioViews.GetValueOrDefault(id), DispatcherProtection = protection, ProtectionSources = sources
                });
        }

        RecordParts(prepMs, Ms(loopStarted), signalMs, aiMs, sendCallMs);
        var waitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        await Task.WhenAll(sends);
        return System.Diagnostics.Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds;
    }

    /// <summary>
    /// 同じ列車か: 入力している列番（クライアントが送ってくる列番。サーバーが付ける接尾辞 X・Y・Z の前のもの）が同じ。
    /// 列番が空のときは、同じ列車とは見なさない。
    /// </summary>
    private static bool SameTrain(PlayerInfo a, PlayerInfo b) =>
        a.TrainNumber.Length > 0 && string.Equals(a.TrainNumber, b.TrainNumber, StringComparison.Ordinal);

    private static double Ms(long started, long? ended = null) =>
        System.Diagnostics.Stopwatch.GetElapsedTime(started, ended ?? System.Diagnostics.Stopwatch.GetTimestamp()).TotalMilliseconds;

    /// <summary>
    /// 配信1回の内訳を、"(配信) …" の名前で ServerLoad へ記録する（どこが重いかを見るため。回数は配信の回数になる）。
    /// 下ごしらえ: 1人ごとの組み立ての前（参加者の一覧・無線・通信状況など）。
    /// 信号: ほかの列車の在線を集めて、停止にする信号機を決めるまで（1人ごとの合計）。
    /// 自動運転: にぎやかしの列車が配信を受け取って進路などを決める分（合計）。
    /// 送信: SendAsync を呼んだ時間（JSON にして書き込む分。合計）。
    /// そのほか: 1人ごとの組み立ての残り（位置の文字・近くの特発・進路・車掌・司令員など）。
    /// </summary>
    private void RecordParts(double prepMs, double loopMs, double signalMs, double aiMs, double sendCallMs)
    {
        _load.RecordWork("(配信) 下ごしらえ", prepMs);
        _load.RecordWork("(配信) 信号", signalMs);
        _load.RecordWork("(配信) 自動運転", aiMs);
        _load.RecordWork("(配信) 送信", sendCallMs);
        _load.RecordWork("(配信) そのほか", Math.Max(0, loopMs - signalMs - aiMs - sendCallMs));
    }

    private async Task SendAsync(string connectionId, ClientState state)
    {
        try
        {
            await _hub.Clients.Client(connectionId).SendAsync(Protocol.ReceiveState, state);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "送信失敗 {ConnectionId}", connectionId);
        }
    }
}
