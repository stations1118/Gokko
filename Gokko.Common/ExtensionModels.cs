namespace Gokko.Common;

/// <summary>
/// 内線電話の取り決め（列車無線とは別の回線）。駅（信号制御盤）・司令員・運転士（停車中だけ）・車掌がかけ合う。
/// 音声の形式は列車無線と同じ（<see cref="TrainRadioFormat"/>。Opus 16kHz・40ms）。パケットも <see cref="TrainRadioPacket"/> を使い、
/// SessionId に内線の通話の番号を入れる。
/// </summary>
public static class ExtensionProtocol
{
    // ---- PC のクライアント → サーバー（/hub） ----

    /// <summary>番号にかける。引数 string（内線番号）、戻り値 <see cref="ExtensionResult"/>。</summary>
    public const string Dial = "ExtDial";

    /// <summary>着信に出る。引数 long（通話の番号）、戻り値 <see cref="ExtensionResult"/>。</summary>
    public const string Answer = "ExtAnswer";

    /// <summary>切る（呼出中なら取り消す）。引数 long（通話の番号）、戻り値 <see cref="ExtensionResult"/>。</summary>
    public const string Hangup = "ExtHangup";

    /// <summary>保留にする・戻す。引数 long（通話の番号）・bool（保留にするか）、戻り値 <see cref="ExtensionResult"/>。</summary>
    public const string Hold = "ExtHold";

    /// <summary>転送する（相手を、別の番号へ回す。自分は抜ける）。引数 long（通話の番号）・string（内線番号）、戻り値 <see cref="ExtensionResult"/>。</summary>
    public const string Transfer = "ExtTransfer";

    /// <summary>司令員: 全駅を一斉に呼び出す。戻り値 <see cref="ExtensionResult"/>。</summary>
    public const string Page = "ExtPage";

    /// <summary>音声 40ms 分を送る（応答を待たない）。引数 <see cref="TrainRadioPacket"/>（SessionId は内線の通話の番号）。</summary>
    public const string SendAudio = "ExtSendAudio";

    // ---- サーバー → クライアント ----

    /// <summary>内線の状態。引数 <see cref="ExtensionState"/>。変わった瞬間と、1秒ごと。</summary>
    public const string ReceiveState = "ReceiveExtensionState";

    /// <summary>内線の音声。引数 <see cref="TrainRadioPacket"/>。</summary>
    public const string ReceiveAudio = "ReceiveExtensionAudio";

    // ---- ブラウザ（/extension。信号制御盤の電話機・スマホの携帯電話） ----

    public const string HubPath = "/extension";

    /// <summary>
    /// ブラウザ → サーバー: 参加する。引数 string（ログインの合い札）・string（"station" か "handset"）・string（駅ID。station のときだけ）、
    /// 戻り値 <see cref="ExtensionResult"/>。
    /// station: その駅の電話機になる（「信号扱い」の役職が要る）。handset: 同じ社員番号で PC から参加している本人の受話器になる。
    /// ほかの操作は PC と同じ名前のメソッド。
    /// </summary>
    public const string Join = "ExtJoin";
}

/// <summary>内線の操作の結果。</summary>
public sealed class ExtensionResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>電話帳の1件（司令・駅・運転士・車掌）。</summary>
public sealed class ExtensionEntry
{
    /// <summary>内線番号（運転士・車掌なら社員番号）。</summary>
    public string Number { get; set; } = "";

    /// <summary>「司令」「津崎駅」「1040A 運転士」など。</summary>
    public string Label { get; set; } = "";

    /// <summary>"dispatcher"・"station"・"driver"・"conductor"。</summary>
    public string Kind { get; set; } = "";

    /// <summary>出られる人がいるか（いなければかけられない）。</summary>
    public bool Present { get; set; }

    public override string ToString() => $"{Number}  {Label}";
}

/// <summary>通話に出ている人（音声の宛先を見分ける）。</summary>
public sealed class ExtensionParty
{
    /// <summary>接続 ID（画面には出さない）。</summary>
    public string Id { get; set; } = "";

    /// <summary>「津崎駅」「司令」「1040A 運転士」など。</summary>
    public string Label { get; set; } = "";

    /// <summary>保留にしている。</summary>
    public bool Held { get; set; }
}

/// <summary>自分が関わる内線の通話1件。</summary>
public sealed class ExtensionCall
{
    public long Id { get; set; }

    /// <summary>
    /// 自分から見た状態。"incoming"（着信中。自分の回線が鳴っている）・"outgoing"（呼出中。相手が出るのを待っている）・
    /// "talking"（通話中）・"held"（自分が保留にしている）。
    /// </summary>
    public string Status { get; set; } = "";

    /// <summary>一斉呼出（司令から全駅へ）。</summary>
    public bool IsPage { get; set; }

    /// <summary>相手の名前（「津崎駅」「司令」「全駅 一斉」など）。</summary>
    public string Peer { get; set; } = "";

    /// <summary>相手の内線番号（わかるときだけ）。</summary>
    public string PeerNumber { get; set; } = "";

    /// <summary>相手が保留にしている（保留音を流す）。</summary>
    public bool PeerHeld { get; set; }

    /// <summary>転送されてきた（「○○から転送」）。なければ空。</summary>
    public string TransferredBy { get; set; } = "";

    /// <summary>通話に出ている人（自分も含む）。</summary>
    public List<ExtensionParty> Parties { get; set; } = new();

    /// <summary>この状態になってからの秒数（通話時間の表示）。</summary>
    public int Seconds { get; set; }
}

/// <summary>内線の状態（その電話機から見たもの）。</summary>
public sealed class ExtensionState
{
    public long Revision { get; set; }

    /// <summary>自分の接続 ID（届いた音声が自分のものか見分ける）。スマホの受話器なら、結んでいる PC の接続 ID。</summary>
    public string SelfId { get; set; } = "";

    /// <summary>自分の回線の名前（「司令」「津崎駅」「1040A 運転士」）。</summary>
    public string Label { get; set; } = "";

    /// <summary>自分の内線番号（運転士・車掌なら社員番号）。</summary>
    public string Number { get; set; } = "";

    /// <summary>"dispatcher"・"station"・"driver"・"conductor"。使えないときは空。</summary>
    public string Kind { get; set; } = "";

    /// <summary>かけたり出たりできるか。false のときは <see cref="Reason"/> にわけ（「走行中は使えません」など）。</summary>
    public bool CanUse { get; set; }

    public string Reason { get; set; } = "";

    /// <summary>スマホを受話器にしている（このあいだ PC のマイクは使わない。音声はスマホへ届く）。</summary>
    public bool HandsetLinked { get; set; }

    /// <summary>自分が関わる通話（着信・呼出・通話・保留）。</summary>
    public List<ExtensionCall> Calls { get; set; } = new();

    /// <summary>電話帳（ワンタッチのボタン。司令と駅だけ）。</summary>
    public List<ExtensionEntry> Directory { get; set; } = new();

    /// <summary>乗務員の一覧（いま参加している運転士・車掌。番号は社員番号。列番の順）。ワンタッチには載せない。</summary>
    public List<ExtensionEntry> Crew { get; set; } = new();
}
