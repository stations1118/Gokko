namespace Gokko.Common;

/// <summary>接続のモード。</summary>
public enum ShareMode
{
    /// <summary>踏切の特発だけを共有する。他列車の表示・進路・信号はゲームのまま。</summary>
    CrossingOnly = 0,

    /// <summary>在線を共有する（集中モード）。他列車を消し、進路はサーバーが管理し、他の参加者の在線で信号を落とす。</summary>
    Shared = 1
}

/// <summary>参加の要求。</summary>
public sealed class JoinRequest
{
    public int ProtocolVersion { get; set; }

    /// <summary>サーバーに設定した合言葉。サーバー側が空なら何でもよい。</summary>
    public string Password { get; set; } = "";

    public ShareMode Mode { get; set; }

    /// <summary>乗務の役割。車掌は在線共有のモードで、同じ列番の運転士の列車に乗る（在線として数えない）。</summary>
    public CrewRole Role { get; set; }

    /// <summary>乗務員のログインの合い札（社員番号とパスワードでログインして受け取ったもの）。ないと参加できない。</summary>
    public string CrewToken { get; set; } = "";

    /// <summary>添乗: 乗客として乗る（放送だけが聞こえる。インターホン・列車無線は聞こえない）。報告の <see cref="ClientReport.Passenger"/> で切り替えられる。</summary>
    public bool Passenger { get; set; }
}

/// <summary>参加の結果。</summary>
public sealed class JoinResult
{
    public bool Ok { get; set; }

    /// <summary>拒否されたときの理由。</summary>
    public string Message { get; set; } = "";

    /// <summary>サーバーが扱う踏切名の一覧。クライアントは、ここに載る踏切の特発を同期する。</summary>
    public List<string> Crossings { get; set; } = new();

    /// <summary>
    /// サーバーが添乗と列車ごとの放送装置（車内放送・車外放送・インターホン）を扱えるか。
    /// 古いサーバーは送ってこない（false）ので、クライアントは添乗をやめ、放送装置を隠す。
    /// </summary>
    public bool Onboard { get; set; }
}

/// <summary>クライアントからサーバーへ、0.5秒ごとに送る自分の状態。</summary>
public sealed class ClientReport
{
    /// <summary>列車番号（入力欄の値。空ならゲームのダイヤ名）。</summary>
    public string TrainNumber { get; set; } = "";

    /// <summary>TRAIN CREW に接続できているか。</summary>
    public bool TrainCrewConnected { get; set; }

    /// <summary>運転画面（プレイ中・一時停止中）にいるか。</summary>
    public bool InGame { get; set; }

    /// <summary>自列車が踏んでいる軌道回路（他の列車の分は含めない）。</summary>
    public List<string> TrackCircuits { get; set; } = new();

    /// <summary>自列車の車両ごとの形式（ゲームの CarModel。並びのまま）。在線共有モードの運転士だけが送る。列車位置案内の編成に出す。</summary>
    public string[] CarModels { get; set; } = [];

    // ---- 以下は在線共有モードのときだけ使う ----

    /// <summary>編成平均乗車率（%、表示用の丸め前）。取得できないときは null。</summary>
    public decimal? AverageOccupancy { get; set; }

    /// <summary>自列車の停車駅の情報（ゲームの stationList）。時刻は送らない。</summary>
    public List<StopInfo> Stops { get; set; } = new();

    /// <summary>自列車が始発から走った距離（m。ゲームの myTrainData.TotalLength）。</summary>
    public float TraveledDistance { get; set; }

    /// <summary>次の駅までの残り距離（m。ゲームの nextStaDistance）。</summary>
    public float NextStationDistance { get; set; }

    public float Speed { get; set; }

    /// <summary>ゲームに今設定されている、自列車の進路。</summary>
    public List<RouteKey> GameRoutes { get; set; } = new();

    /// <summary>ユーザーが選んだ番線（駅ID → 番線）。</summary>
    public Dictionary<string, int> TrackChoices { get; set; } = new();

    /// <summary>ユーザーが「ここで打ち切り・入換」を選んだ駅（駅ID → 番線）。その駅を終着にして、入りきったら入換（引上げ）を要求できる。</summary>
    public Dictionary<string, int> EndChoices { get; set; } = new();

    /// <summary>
    /// 継続乗務ボタンが押されているか。押している間に運転画面を出ると、サーバーは最後の在線位置を5分間残す
    /// （同じ番線から次のシナリオを始めると、その列番の列車として続ける）。
    /// </summary>
    public bool ContinueDuty { get; set; }

    /// <summary>列車番号を入力欄で手入力したか（false ならシナリオの列番）。ダイヤ運転中は、手入力していない列車を出現させない。</summary>
    public bool TrainNumberTyped { get; set; }

    /// <summary>添乗: 乗客として乗っているか（「乗客として乗る」のチェック）。</summary>
    public bool Passenger { get; set; }

    /// <summary>自分の通信状況（同じ列番の相手の「通信状況」に出す）。</summary>
    public LinkHealth? Link { get; set; }
}

/// <summary>
/// クライアントの通信状況（クライアントが測ったもの）。同じ列番の運転士・車掌・添乗どうしで見せ合う。
/// 分からないときは -1。
/// </summary>
public sealed class LinkHealth
{
    /// <summary>サーバー（/hub）への報告の往復の時間（ミリ秒）。</summary>
    public int ServerRttMs { get; set; } = -1;

    /// <summary>中継（/crew）との往復の時間（ミリ秒）。中継につながっていなければ -1。</summary>
    public int CrewRttMs { get; set; } = -1;

    /// <summary>ゲーム（TRAIN CREW）から最後にデータが届いてからの時間（ミリ秒）。ゲームにつながっていなければ -1。</summary>
    public int GameDataAgeMs { get; set; } = -1;

    /// <summary>相手の操作（運転士へは車掌の、車掌・添乗へは運転士の）が最後に届いてからの時間（ミリ秒）。届いていなければ -1。</summary>
    public int PeerInputAgeMs { get; set; } = -1;

    /// <summary>中継で乗務に入っているか（車掌は運転士に許可されているか）。</summary>
    public bool CrewJoined { get; set; }
}

/// <summary>同じ列番の相手1人分の通信状況（サーバーから見たもの）。</summary>
public sealed class CrewPeerView
{
    public CrewRole Role { get; set; }

    /// <summary>添乗: 乗客として乗っている。</summary>
    public bool Passenger { get; set; }

    public bool TrainCrewConnected { get; set; }
    public bool InGame { get; set; }

    /// <summary>サーバーに最後に報告が届いてからの時間（ミリ秒）。ふだんは 0.5 秒ほど。</summary>
    public int ReportAgeMs { get; set; }

    /// <summary>始発から走った距離（m。位置のずれを見る）。</summary>
    public float TraveledDistance { get; set; }

    /// <summary>相手のクライアントが測った通信状況。</summary>
    public LinkHealth? Link { get; set; }
}

/// <summary>停車駅1つ分（ゲームの stationList の要素から、時刻を除いたもの）。</summary>
public sealed class StopInfo
{
    public string Name { get; set; } = "";

    /// <summary>ゲームの stopType（StopForPassenger / StopForOperation / それ以外は通過）。</summary>
    public string StopType { get; set; } = "";

    /// <summary>始発からの距離（m）。</summary>
    public float Distance { get; set; }
}

/// <summary>連動装置名と進路名の組。</summary>
public sealed class RouteKey
{
    public string Interlocking { get; set; } = "";
    public string RouteName { get; set; } = "";

    public override string ToString() => $"{Interlocking}/{RouteName}";
}

/// <summary>サーバーからクライアントへ配信する状態。クライアントごとに中身が違う（理由の表示範囲など）。</summary>
public sealed class ClientState
{
    public DateTimeOffset ServerTime { get; set; }

    /// <summary>ダイヤ運転中か（false ならフリー運転）。ダイヤ運転中は列車番号の手入力が要り、継続乗務は常に入。</summary>
    public bool DiaMode { get; set; }

    /// <summary>自列車が防護無線の受信範囲にいる（どこかで発報中）。クライアントは警報音を鳴らす。</summary>
    public bool ProtectionRadioReceiving { get; set; }

    /// <summary>自分が防護無線を発報中か。</summary>
    public bool ProtectionRadioSending { get; set; }

    /// <summary>ロープレ上の時刻（"HH:mm:ss"）。管理画面で設定するまでは null。</summary>
    public string? GameTime { get; set; }

    /// <summary>サーバーが判断した自分の現在地（"館浜〜駒野" など）。分からなければ null。</summary>
    public string? Position { get; set; }

    /// <summary>作動中の特発の踏切名。全員に同じものが届く。クライアントはこの通りにゲームへ反映する。</summary>
    public List<string> ActiveCrossings { get; set; } = new();

    /// <summary>作動中の特発のうち、自分のいる区間のもの（理由つき）。当該区間に入ったときだけ入る。</summary>
    public List<IncidentView> VisibleIncidents { get; set; } = new();

    /// <summary>参加している人数。</summary>
    public int PlayerCount { get; set; }

    /// <summary>運転士へ: 自分の列車に乗っている車掌の人数。</summary>
    public int Conductors { get; set; }

    /// <summary>車掌へ: 同じ列番の運転士が参加しているか。いなければ、進路・信号は届かない。</summary>
    public bool DriverPresent { get; set; }

    /// <summary>運転士・車掌へ: 自分の列車に添乗している人数。</summary>
    public int Riders { get; set; }

    /// <summary>同じ列番の、自分以外の運転士・車掌・添乗の通信状況（クライアントの「通信状況」に出す）。</summary>
    public List<CrewPeerView> CrewPeers { get; set; } = new();

    /// <summary>列車ごとの放送装置の状態（その人から見たもの）。列番がなければ null。</summary>
    public OnboardState? Onboard { get; set; }

    /// <summary>列車無線の状態（その人から見たもの）。</summary>
    public TrainRadioState? TrainRadio { get; set; }

    /// <summary>司令員へ: 区間防護の状態。ほかの人には null。</summary>
    public DispatcherProtectionState? DispatcherProtection { get; set; }

    /// <summary>司令員へ: 運転士の F5（防護無線）の発報の一覧。1件でもあれば、司令機の赤ランプを点滅させる。</summary>
    public List<ProtectionSource> ProtectionSources { get; set; } = new();

    // ---- 以下は在線共有モードのときだけ入る ----

    /// <summary>ゲームに開通させておく進路（送る順）。クライアントは、ゲームにないものを SetRoute で送る。</summary>
    public List<RouteCommand> Routes { get; set; } = new();

    /// <summary>取り消す進路。クライアントは、ゲームにあれば DeleteRoute2 で送る。</summary>
    public List<RouteKey> DeleteRoutes { get; set; } = new();

    /// <summary>
    /// ほかの参加者の列車の、開通している進路（その列車のゲームで開通を確かめ、列車がまだ入っていないもの）。
    /// クライアントは自分のゲームにも設定し（対向列車の出発が開くのが見えるように）、ここからなくなったら取り消す。
    /// 自分のゲームにはその列車がいないので、ゲームの自動解除はあてにしない。
    /// </summary>
    public List<RouteCommand> OtherRoutes { get; set; } = new();

    /// <summary>他の参加者の在線で落とす信号の現示。ここにない信号は、ゲームの現示に戻す（None を送る）。</summary>
    public List<SignalCommand> Signals { get; set; } = new();

    /// <summary>自列車の、この先の駅ごとの番線と状態。</summary>
    public List<StationPlanView> Plan { get; set; } = new();

    /// <summary>出発要求ボタンを押せるか（駅に止まっていて、出発進路がまだのとき）。</summary>
    public bool CanRequestDeparture { get; set; }

    /// <summary>出発要求ボタンの表示（"出発要求"、引上線などにいるときは "入換要求"）。</summary>
    public string DepartureLabel { get; set; } = "出発要求";

    /// <summary>進路についての、ユーザーへのお知らせ。</summary>
    public string? RouteMessage { get; set; }

    /// <summary>
    /// 入換で動かすボタン（X線入換など）の表示。null なら出さない。X線に止まっていると「X線入換」、押したあとは取り消し、
    /// ゲームの中だけで動かしている間は「X線停車中」（押すと、残した在線につなぎ直す）。
    /// </summary>
    public string? ShuntButtonLabel { get; set; }

    /// <summary>入換で動かすボタンを押せるか。</summary>
    public bool ShuntButtonEnabled { get; set; }

    /// <summary>
    /// 浅い複製（サーバーが、運転士への配信を車掌・添乗へ回すときに使う）。一覧は複製元と同じものを指すので、
    /// 中身を書き換えず、プロパティごと差し替えること。項目が増えても、そのまま複製される。
    /// </summary>
    public ClientState Clone() => (ClientState)MemberwiseClone();
}

/// <summary>理由つきの特発の情報。</summary>
public sealed class IncidentView
{
    public string Crossing { get; set; } = "";
    public string Reason { get; set; } = "";

    /// <summary>作動したロープレ時刻（設定されていれば）。</summary>
    public string? StartedGameTime { get; set; }
}

/// <summary>復帰ボタンの結果。</summary>
public sealed class RestoreResult
{
    public int Released { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// 進路の開通指示。TRAIN CREW の SetRoute の引数にそのまま対応する
/// （route_manual モードで、[連動装置名, 進路名, LED表示, 列車番号, 停車/運転停車/通過]）。
/// </summary>
public sealed class RouteCommand
{
    /// <summary>
    /// 指示の番号（サーバーが振る）。クライアントは、一度ゲームで開通を確認した番号の指示は二度と送らない
    /// （ゲームが列車の通過で自動解除した進路を、送り直して開通させてしまわないため）。
    /// </summary>
    public long Id { get; set; }

    public string Interlocking { get; set; } = "";
    public string RouteName { get; set; } = "";

    /// <summary>進路表示器（LED）に出す値（場内・入換・誘導の進路）。</summary>
    public string Indicator { get; set; } = "";

    /// <summary>
    /// 出発信号機の進路なら true（列選）。Indicator が空でなければ、それ（ダイヤの種別表記から作った列選の文字）を送る。
    /// 空なら、クライアントがゲームの種別から作った列選の文字
    /// （普通・準急・区急・急行・快速急行、特急は D特 など）を送る。
    /// </summary>
    public bool UseTrainType { get; set; }

    public string TrainNumber { get; set; } = "";

    /// <summary>"停車"、"運転停車"、"通過" のどれか。</summary>
    public string StopType { get; set; } = "停車";
}

/// <summary>列車番号の確定の結果。</summary>
public sealed class TrainCheckResult
{
    /// <summary>ダイヤ（列車一覧）にある列車か。なければ、種別ごとの所定の番線で進路を作る。</summary>
    public bool Found { get; set; }

    public string Message { get; set; } = "";
}

/// <summary>信号機の現示の指示（SetSignalPhases の1組）。Phase は R / YY / Y / YG / G、None（上限を外してゲームの現示に戻す）。</summary>
public sealed class SignalCommand
{
    public string Name { get; set; } = "";
    public string Phase { get; set; } = "R";
}

/// <summary>この先の駅1つ分の、番線と状態。</summary>
public sealed class StationPlanView
{
    public string StationId { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>"停車" / "運転停車" / "通過"。</summary>
    public string StopType { get; set; } = "";

    /// <summary>使う番線（通過なら null）。</summary>
    public int? Track { get; set; }

    /// <summary>選べる番線。</summary>
    public List<int> TrackOptions { get; set; } = new();

    /// <summary>選ぶとこの駅で打ち切り、入換（引上げ）ができる番線（「◯番線（打ち切り・入換）」として出す）。</summary>
    public List<int> EndOptions { get; set; } = new();

    /// <summary>行き止まりの番線（入るとこの駅で打ち切り。「◯番線（打ち切り）」と出す）。</summary>
    public List<int> TurnbackTracks { get; set; } = new();

    /// <summary>番線を使わずに通過もできる駅か（江ノ原信号場）。番線の欄に「通過」も出す。</summary>
    public bool CanPass { get; set; }

    /// <summary>運転士の「打ち切り・入換」の選択で、この駅が終着になっているか。</summary>
    public bool EndHere { get; set; }

    /// <summary>番線の決め方（所定 / 選択 / 待避 / 集中）。</summary>
    public string TrackSource { get; set; } = "";

    /// <summary>番線が確定して、もう選べないか。</summary>
    public bool Locked { get; set; }

    /// <summary>運転士に見せる状態: 「出発要求待ち」（入換なら「入換要求待ち」）「進路確定」「進路未確定」の3段階だけ。</summary>
    public string Status { get; set; } = "";

    /// <summary>詳しい状態（待ちの理由など。管理・試験用。クライアントの画面には出さない）。</summary>
    public string Detail { get; set; } = "";
}
