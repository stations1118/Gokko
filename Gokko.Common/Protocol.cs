namespace Gokko.Common;

/// <summary>
/// サーバーとユーザークライアントの間の取り決め（SignalR のメソッド名など）。
/// 両方で同じものを参照する。中身を変えて互換性がなくなるときは Version を上げる。
/// </summary>
public static class Protocol
{
    /// <summary>通信の版。サーバーと違うクライアントは、接続時に拒否される。</summary>
    public const int Version = 13;

    /// <summary>SignalR の接続先のパス（https://example.duckdns.org/hub）。</summary>
    public const string HubPath = "/hub";

    // ---- クライアント → サーバー ----

    /// <summary>参加する。引数 <see cref="JoinRequest"/>、戻り値 <see cref="JoinResult"/>。最初に1回呼ぶ。</summary>
    public const string Join = "Join";

    /// <summary>自分の状態を知らせる。引数 <see cref="ClientReport"/>、戻り値 bool（false なら参加し直す）。0.5秒ごと。</summary>
    public const string Report = "Report";

    /// <summary>復帰ボタン。自分のいる区間で作動中の特発を解除する。戻り値 <see cref="RestoreResult"/>。</summary>
    public const string Restore = "Restore";

    /// <summary>出発要求ボタン（在線共有モード）。駅に止まっている自列車の出発進路を要求する。戻り値 string（結果のお知らせ）。</summary>
    public const string RequestDeparture = "RequestDeparture";

    /// <summary>クライアント → サーバー: ランカーブ（1駅間の走り方。RunCurve の JSON を gzip したもの）。駅に止まったときに1回だけ。</summary>
    public const string UploadRunCurve = "UploadRunCurve";

    /// <summary>入換で動かすボタン（X線入換・X線停車中。在線共有モード）。戻り値 string（結果のお知らせ）。</summary>
    public const string ShuntMove = "ShuntMove";

    /// <summary>TRAIN CREW の SetRoute の結果を知らせる（在線共有モード）。引数 string（"連動装置_進路 is OK" など）。</summary>
    public const string RouteResult = "RouteResult";

    /// <summary>列車番号の確定（在線共有モード）。引数 string（列車番号）、戻り値 <see cref="TrainCheckResult"/>。ダイヤにある列車か調べる。</summary>
    public const string CheckTrain = "CheckTrain";

    /// <summary>防護無線の発報・解除（トグル）。引数 <see cref="ClientReport"/>（押した時点の位置）、戻り値 bool（受け付けたか）。</summary>
    public const string ToggleProtectionRadio = "ToggleProtectionRadio";

    // ---- 列車無線（運転士・車掌は司令を呼び出す。司令員が応答すると、その2人だけがマイクを開ける） ----

    /// <summary>運転士・車掌: 司令を呼び出す（F6）。戻り値 <see cref="TrainRadioResult"/>。</summary>
    public const string RequestRadioCall = "RequestRadioCall";

    /// <summary>運転士・車掌: 呼出を取り消す。戻り値 <see cref="TrainRadioResult"/>。</summary>
    public const string CancelRadioCall = "CancelRadioCall";

    /// <summary>司令員: 呼出に応答する。引数 long（呼出の番号）・bool（個別通話か）、戻り値 <see cref="TrainRadioResult"/>。</summary>
    public const string AnswerRadioCall = "AnswerRadioCall";

    /// <summary>司令員: 一斉通告を始める（自分だけが話し、全員に聞こえる）。戻り値 <see cref="TrainRadioResult"/>。</summary>
    public const string StartRadioBroadcast = "StartRadioBroadcast";

    /// <summary>司令員: 列車を指定して個別に呼び出す。引数 string（相手の接続 ID）、戻り値 <see cref="TrainRadioResult"/>。</summary>
    public const string CallRadioTrain = "CallRadioTrain";

    /// <summary>運転士・車掌: 司令員からの個別呼出に応答する。引数 long（呼出の番号）、戻り値 <see cref="TrainRadioResult"/>。</summary>
    public const string AcceptDispatcherCall = "AcceptDispatcherCall";

    /// <summary>運転士・車掌: 一斉通告に応答して話す（取消は <see cref="EndRadioCall"/>。通告は聞こえたまま）。引数 long（一斉通告の通話の番号）、戻り値 <see cref="TrainRadioResult"/>。</summary>
    public const string RespondRadioBroadcast = "RespondRadioBroadcast";

    /// <summary>司令員からの個別呼出を取り消す（どちらからでも）。引数 long（呼出の番号）、戻り値 <see cref="TrainRadioResult"/>。</summary>
    public const string CancelDispatcherCall = "CancelDispatcherCall";

    /// <summary>司令員: 通話中に共通・個別を切り替える。引数 long（通話の番号）・bool（個別通話か）、戻り値 <see cref="TrainRadioResult"/>。</summary>
    public const string ChangeRadioMode = "ChangeRadioMode";
    public const string SelectRadioResponder = "SelectRadioResponder";

    /// <summary>通話を終える（当事者のどちらからでも）。引数 long（通話の番号）、戻り値 <see cref="TrainRadioResult"/>。</summary>
    public const string EndRadioCall = "EndRadioCall";

    /// <summary>音声 40ms 分を送る（応答を待たない）。引数 <see cref="TrainRadioPacket"/>。</summary>
    public const string SendRadioAudio = "SendRadioAudio";

    /// <summary>司令員: 区間防護の発報・解除。引数 bool（発報するか）・string（開始駅）・string（終了駅）、戻り値 <see cref="TrainRadioResult"/>。</summary>
    public const string SetDispatcherProtection = "SetDispatcherProtection";

    // ---- 列車ごとの放送装置（車内放送・車外放送・インターホン。同じ列番の運転士・車掌・添乗の中だけ） ----
    // 古いサーバーにはない（JoinResult.Onboard が false）。通信の版は上げない（古いクライアントはこれを使わないだけ）

    /// <summary>運転士・車掌: 車内放送・車外放送を流す・止める。引数 <see cref="OnboardChannel"/>（None で止める）、戻り値 <see cref="TrainRadioResult"/>。</summary>
    public const string SetOnboardBroadcast = "SetOnboardBroadcast";

    /// <summary>運転士・車掌: インターホンの入・切。引数 bool、戻り値 <see cref="TrainRadioResult"/>。</summary>
    public const string SetIntercom = "SetIntercom";

    /// <summary>放送装置の音声 40ms 分を送る（応答を待たない）。引数 <see cref="OnboardPacket"/>。</summary>
    public const string SendOnboardAudio = "SendOnboardAudio";

    /// <summary>サーバー → クライアント: 放送装置の音声。引数 <see cref="OnboardPacket"/>。</summary>
    public const string ReceiveOnboardAudio = "ReceiveOnboardAudio";

    /// <summary>サーバー → クライアント: 放送装置の状態がすぐ変わった（配信の ClientState.Onboard と同じもの）。引数 <see cref="OnboardState"/>。</summary>
    public const string ReceiveOnboardState = "ReceiveOnboardState";

    // ---- サーバー → クライアント ----

    /// <summary>状態の配信。引数 <see cref="ClientState"/>。0.5秒ごと、および特発が変わった瞬間。</summary>
    public const string ReceiveState = "ReceiveState";

    /// <summary>列車無線の状態が変わった。引数 <see cref="TrainRadioState"/>（配信の ClientState.TrainRadio と同じもの）。</summary>
    public const string ReceiveRadioState = "ReceiveRadioState";

    /// <summary>列車無線の音声。引数 <see cref="TrainRadioPacket"/>。</summary>
    public const string ReceiveRadioAudio = "ReceiveRadioAudio";

    // ---- スマホの列車無線（/phone。ブラウザから JSON で。PC のクライアントで参加している本人の受話器として結ぶ） ----

    /// <summary>スマホの列車無線の接続先のパス。</summary>
    public const string PhoneHubPath = "/phone";

    /// <summary>スマホ → サーバー: 参加する。引数 string（ログインの合い札）、戻り値 <see cref="PhoneJoinResult"/>。</summary>
    public const string PhoneJoin = "Join";

    /// <summary>スマホ → サーバー: 司令を呼び出す・取り消す・通話を終える・音声を送る（PC の同じ名前のメソッドと同じ引数）。</summary>
    public const string PhoneRequestCall = RequestRadioCall;
    public const string PhoneCancelCall = CancelRadioCall;
    public const string PhoneEndCall = EndRadioCall;
    public const string PhoneRespondBroadcast = RespondRadioBroadcast;
    public const string PhoneSendAudio = SendRadioAudio;

    /// <summary>サーバー → スマホ: 結んでいる相手と列車無線の状態。引数 <see cref="PhoneRadioStatus"/>。1秒ごと、および変わった瞬間。</summary>
    public const string ReceivePhoneStatus = "ReceivePhoneStatus";

    /// <summary>サーバー → スマホ: 列車無線の音声（<see cref="ReceiveRadioAudio"/> と同じ）。</summary>
    public const string PhoneReceiveAudio = ReceiveRadioAudio;
}
