namespace Gokko.Common;

/// <summary>乗務の役割。運転士も車掌も、ゲームは運転士モードで遊ぶ。</summary>
public enum CrewRole
{
    /// <summary>運転士（ふつうの在線共有の参加者）。</summary>
    Driver = 0,

    /// <summary>
    /// 車掌。同じ列番の運転士の列車に乗る。サーバーは在線として数えず、運転士と同じ進路・信号を届ける。
    /// 車掌のゲームの列車は、運転士のノッチで動かす（TrainCrewInput）。
    /// </summary>
    Conductor = 1,

    /// <summary>
    /// 司令員。勤怠管理で「司令員」の役職を振られた人だけが選べる。TRAIN CREW なしで参加し、在線として数えない。
    /// 列車無線の呼出に応答する・一斉通告・区間防護を扱う。
    /// </summary>
    Dispatcher = 2,

    /// <summary>
    /// 添乗。同じ列番の運転士の列車に乗って、運転士の操作を映すだけ（車掌スイッチなどは扱えない。車掌の読み取り専用版）。
    /// 運転士の許可は要らず、何人でも乗れる。在線として数えない。「乗客として乗る」なら、放送だけが聞こえる。
    /// </summary>
    Rider = 3
}

/// <summary>
/// 運転士と車掌の間の中継（/crew）。配信（/hub）とは別の接続にして、0.5秒ごとの配信の遅れの揺れを受けないようにする。
/// </summary>
public static class CrewProtocol
{
    /// <summary>接続先のパス（https://example.duckdns.org/crew）。</summary>
    public const string HubPath = "/crew";

    // ---- クライアント → サーバー ----

    /// <summary>乗務に入る（列番が変わったら呼び直す）。引数 <see cref="CrewJoinRequest"/>、戻り値 <see cref="JoinResult"/>。</summary>
    public const string Join = "CrewJoin";

    /// <summary>運転士の操作（変わったとき、および0.5秒ごと）。引数 <see cref="DriverInput"/>。同じ列番の車掌へ送る。</summary>
    public const string SendDriverInput = "SendDriverInput";

    /// <summary>車掌の操作（ドア・ブザー）。引数 <see cref="ConductorInput"/>。同じ列番の運転士へ送る。</summary>
    public const string SendConductorInput = "SendConductorInput";

    /// <summary>運転士: 「車掌乗務を許可」の入・切。引数 bool。入のときだけ、車掌の乗務の申し込みが届く。</summary>
    public const string SetConductorAllowed = "SetConductorAllowed";

    /// <summary>運転士: 車掌の乗務の申し込みへの返事。引数 <see cref="ConductorAnswer"/>。</summary>
    public const string AnswerConductor = "AnswerConductor";

    /// <summary>中継との往復の時間を測る（何もせずに返す）。戻り値 bool。</summary>
    public const string Ping = "CrewPing";

    // ---- サーバー → クライアント ----

    /// <summary>運転士の操作が届いた（車掌へ）。引数 <see cref="DriverInput"/>。</summary>
    public const string ReceiveDriverInput = "ReceiveDriverInput";

    /// <summary>車掌の操作が届いた（運転士へ）。引数 <see cref="ConductorInput"/>。</summary>
    public const string ReceiveConductorInput = "ReceiveConductorInput";

    /// <summary>相手の顔ぶれが変わった。引数 <see cref="CrewStatus"/>。</summary>
    public const string ReceiveStatus = "ReceiveCrewStatus";

    /// <summary>車掌の乗務の申し込みが届いた（運転士へ）。引数 <see cref="ConductorRequest"/>。</summary>
    public const string ReceiveConductorRequest = "ReceiveConductorRequest";

    /// <summary>申し込みが取り消された（車掌が抜けた。運転士へ）。引数 string（申し込みの番号）。</summary>
    public const string ReceiveRequestCancelled = "ReceiveRequestCancelled";

    /// <summary>乗務の許可の状態（車掌へ）。引数 <see cref="CrewApproval"/>。</summary>
    public const string ReceiveApproval = "ReceiveCrewApproval";
}

public sealed class CrewJoinRequest
{
    public int ProtocolVersion { get; set; }
    public string Password { get; set; } = "";
    public CrewRole Role { get; set; }
    public string TrainNumber { get; set; } = "";

    /// <summary>乗務員のログインの合い札（社員番号とユーザー名を、相手に知らせるため）。</summary>
    public string CrewToken { get; set; } = "";

    /// <summary>運転士: 「車掌乗務を許可」が入っているか（つなぎ直したときも引き継ぐ）。</summary>
    public bool AllowConductor { get; set; }
}

/// <summary>車掌の乗務の申し込み（運転士へ）。</summary>
public sealed class ConductorRequest
{
    /// <summary>申し込みの番号（返事に付けて返す）。</summary>
    public string RequestId { get; set; } = "";

    public string TrainNumber { get; set; } = "";

    /// <summary>車掌の社員番号。</summary>
    public string CrewId { get; set; } = "";

    /// <summary>車掌のユーザー名。</summary>
    public string Name { get; set; } = "";
}

/// <summary>運転士の返事。</summary>
public sealed class ConductorAnswer
{
    public string RequestId { get; set; } = "";
    public bool Accept { get; set; }
}

/// <summary>乗務の許可の状態。</summary>
public enum ApprovalState
{
    /// <summary>運転士がいない、または「車掌乗務を許可」が切れている。</summary>
    Waiting = 0,

    /// <summary>運転士に確かめている。</summary>
    Asking = 1,

    /// <summary>許可された（乗務に入っている）。</summary>
    Approved = 2,

    /// <summary>断られた（もう一度［確定］を押すと、また申し込む）。</summary>
    Declined = 3
}

/// <summary>乗務の許可の状態（車掌へ）。</summary>
public sealed class CrewApproval
{
    public ApprovalState State { get; set; }

    /// <summary>車掌に見せる一言。</summary>
    public string Message { get; set; } = "";
}

/// <summary>同じ列番の乗務の顔ぶれ。</summary>
public sealed class CrewStatus
{
    public string TrainNumber { get; set; } = "";
    public int Drivers { get; set; }
    public int Conductors { get; set; }

    /// <summary>添乗している人数。</summary>
    public int Riders { get; set; }
}

/// <summary>運転士の操作と位置（TrainCrewInput の GetTrainState と、ゲームの通信から）。</summary>
public sealed class DriverInput
{
    /// <summary>送った順の番号（古いものが後から届いたら捨てる）。</summary>
    public long Seq { get; set; }

    /// <summary>運転士のパソコンの時計（ミリ秒）。</summary>
    public long ClientMs { get; set; }

    /// <summary>マスコン（ゲームの Pnotch）。</summary>
    public int Power { get; set; }

    /// <summary>ブレーキ（ゲームの Bnotch）。</summary>
    public int Brake { get; set; }

    /// <summary>レバーサー（1 前進、0 中立、-1 後進）。</summary>
    public int Reverser { get; set; }

    public float Speed { get; set; }

    /// <summary>始発から走った距離（m。ゲームの通信の値。0.5秒ごとに更新）。</summary>
    public float TraveledDistance { get; set; }

    public float NextStationDistance { get; set; }

    /// <summary>次の駅の名前（残り距離どうしを比べてよいかの確認に使う）。</summary>
    public string NextStation { get; set; } = "";

    /// <summary>運転士のゲームで、全部のドアが閉まっているか。</summary>
    public bool AllClose { get; set; }

    /// <summary>運転士がブザーを押しているか（ブザーのキーを押している間。車掌のゲームでも、押している間だけ鳴らす）。</summary>
    public bool Buzzer { get; set; }

    /// <summary>
    /// 運転士がキーで扱ったボタン（"ボタン名:1" で押した、"ボタン名:0" で離した）。ふだんは ""。車掌のゲームでも同じボタンを押す・離す。
    /// 送ってよいボタンは <see cref="Actions"/> だけ（車掌のクライアントが確かめる）。
    /// </summary>
    public string Action { get; set; } = "";

    /// <summary>運転士がキーで扱えるボタン（TrainCrewInput の InputAction の名前 → 表示名）。</summary>
    public static readonly IReadOnlyDictionary<string, string> Actions = new Dictionary<string, string>
    {
        ["GradientStart"] = "勾配起動", ["SnowBrake"] = "耐雪ブレーキ", ["SafetyBrakeSw"] = "保安ブレーキ", ["EBReset"] = "EB リセット",
        ["HeadLightSw"] = "前照灯", ["LightLow"] = "前灯減光", ["HornAir"] = "空笛", ["HornEle"] = "電笛", ["Wiper"] = "ワイパー",
        // ドア: 運転士がゲームのドアのキー（体のある側のドアを扱う1つのキー。PgUp・PgDn など）で開け閉めしたことを、添乗に知らせる
        // （ゲームのキーと同じキーを当てる）。ゲームのボタンの名前ではないので、受け取った添乗は
        // 「閉＝左右とも閉める」「開＝駅の開く側を開ける」に置き換える（運転士はふつうホーム側に体を置いて開ける）。車掌は使わない
        [DoorClose] = "ドア閉", [DoorOpen] = "ドア開"
    };

    /// <summary>
    /// 運転台の車掌スイッチ（運転士の車掌用操作盤）の操作を、Action に "盤/ボタン名:1" の形で載せる（車掌から届く操作と同じように、
    /// 車掌・添乗のゲームで扱う）。ボタン名はドア・NFB（車掌の操作と同じ）、ReOpenSW・JoukouSokusin、<see cref="PanelEmergency"/>。
    /// </summary>
    public const string PanelPrefix = "盤/";

    /// <summary>運転台の車掌スイッチの非常ブレーキ（車掌・添乗のゲームでは ATO のブレーキ -8）。</summary>
    public const string PanelEmergency = "Emergency";

    /// <summary>運転士がドアを閉めた（添乗に知らせる）。</summary>
    public const string DoorClose = "DoorClose";

    /// <summary>運転士がドアを開けた（添乗に知らせる）。</summary>
    public const string DoorOpen = "DoorOpen";
}

/// <summary>
/// 車掌の操作（車掌用操作盤から）。ドアは「押した」操作として、非常ブレーキ・再開閉・乗降促進・ブザーは「今の状態」として送る
/// （状態は1秒ごとにも送るので、途中で1つ落ちても運転士のゲームは車掌に合う）。
/// </summary>
public sealed class ConductorInput
{
    /// <summary>
    /// 送ってよい操作。"" は操作なし（状態だけ知らせる。1秒ごと）。
    /// 左右のドア（TrainCrewInput の InputAction の名前）。DoorOpn・DoorCls は「体のある側」だけを扱うので使わない。
    /// door34sw は 3/4閉、doorCutSw は羽衣橋（ドアカット）の NFB を押す。
    /// </summary>
    public static readonly IReadOnlySet<string> Actions = new HashSet<string>
    {
        "", "DoorOpnLeft", "DoorClsLeft", "DoorOpnRight", "DoorClsRight", "door34sw", "doorCutSw"
    };

    /// <summary><see cref="Actions"/> のどれか。</summary>
    public string Action { get; set; } = "";

    /// <summary>車掌の非常ブレーキが入っているか（運転士のゲームでは ATO のブレーキ -8 として重ねる）。</summary>
    public bool Emergency { get; set; }

    /// <summary>再開閉スイッチを押しているか。</summary>
    public bool ReOpen { get; set; }

    /// <summary>乗降促進を押しているか。</summary>
    public bool Joukou { get; set; }

    /// <summary>ブザーを押しているか（押している間だけ鳴る）。</summary>
    public bool Buzzer { get; set; }

    /// <summary>車掌のゲームで、全部のドアが閉まっているか（null は分からない: 試験ページから。運転士のゲームのドアを合わせ直さない）。</summary>
    public bool? AllClose { get; set; }

    /// <summary>車掌のゲームの、始発から走った距離（運転士とのずれを見る）。</summary>
    public float TraveledDistance { get; set; }
}
