using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gokko.Common;

namespace Gokko.Client;

/// <summary>
/// 画面で入力した値。%APPDATA%\GokkoClient\client.json に保存し、次回の起動時に読み込む。
/// 合言葉もそのまま保存される（運転会の合言葉程度のものを想定）。
/// 乗務員のパスワードとログインの合い札は、記憶するときだけ DPAPI で暗号化して保存する（CrewPasswordProtected・CrewTokenProtected）。
/// クライアントの exe を入れ替えても、この場所は消えない。
/// </summary>
public sealed class ClientConfig
{
    /// <summary>乗車率の入力単位。75 が 75% なら false、0.75 が 75% の環境では true。</summary>
    public bool OccupancyRateIsRatio { get; set; }

    public string Password { get; set; } = "";
    public string TrainNumber { get; set; } = "";

    /// <summary>最後にログインした社員番号（次のログインで最初から入れておく）。</summary>
    public string CrewId { get; set; } = "";

    /// <summary>ログインを記憶するか（ログイン画面のチェック。合い札と、移行のあいだはパスワードも）。</summary>
    public bool RememberPassword { get; set; } = true;

    /// <summary>
    /// 記憶したパスワード（DPAPI の CurrentUser で暗号化して Base64 にしたもの。平文では置かない）。
    /// 同じ PC の同じ Windows ユーザーでしか戻せない。空なら記憶していない。
    /// </summary>
    public string CrewPasswordProtected { get; set; } = "";

    /// <summary>
    /// 記憶したログインの合い札（Discord でログインしたときなど。パスワードと同じく DPAPI で暗号化）。
    /// 次の起動で「ID:… で続ける」に使う。空なら記憶していない。
    /// </summary>
    public string CrewTokenProtected { get; set; } = "";

    private static readonly byte[] PasswordEntropy = Encoding.UTF8.GetBytes("Gokko.Client.CrewPassword");
    private static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes("Gokko.Client.CrewToken");

    /// <summary>記憶したパスワードを戻す。記憶していない・戻せない（別の Windows ユーザーなど）ときは空。</summary>
    public string LoadCrewPassword() => Unprotect(CrewPasswordProtected, PasswordEntropy);

    /// <summary>パスワードを暗号化して記憶する（空なら消す）。保存（Save）は呼ぶ側で行う。</summary>
    public void StoreCrewPassword(string password) => CrewPasswordProtected = Protect(password, PasswordEntropy);

    /// <summary>記憶した合い札を戻す（なければ空）。</summary>
    public string LoadCrewToken() => Unprotect(CrewTokenProtected, TokenEntropy);

    /// <summary>合い札を暗号化して記憶する（空なら消す）。保存（Save）は呼ぶ側で行う。</summary>
    public void StoreCrewToken(string token) => CrewTokenProtected = Protect(token, TokenEntropy);

    private static string Unprotect(string stored, byte[] entropy)
    {
        if (stored.Length == 0) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), entropy, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return "";
        }
    }

    private static string Protect(string value, byte[] entropy)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), entropy, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return ""; // 暗号化できなければ記憶しない（平文では置かない）
        }
    }

    /// <summary>接続のモード。まだ選んでいなければ null（初めて接続するときに選ぶ）。</summary>
    public ShareMode? Mode { get; set; }

    /// <summary>乗務の役割（運転士・車掌）。車掌は在線共有のモードだけ。</summary>
    public CrewRole Role { get; set; }

    /// <summary>添乗: 乗客として乗る（放送だけが聞こえる。インターホン・列車無線・内線電話は聞こえない）。</summary>
    public bool Passenger { get; set; }

    /// <summary>放送装置のポップの位置と大きさ（"x,y,幅,高さ"）。空なら画面の右下に出す。</summary>
    public string OnboardPopupBounds { get; set; } = "";

    /// <summary>放送装置のポップを、列車に乗ったときに自動で出すか（閉じるボタンで隠したら false）。</summary>
    public bool OnboardPopupOpen { get; set; } = true;

    /// <summary>放送装置のキー（"Inside"・"Outside"・"Intercom" → キーかコントローラーのボタン。"N|Joy0:5" のように両方も可）。押すたびに入・切。</summary>
    public Dictionary<string, string> OnboardKeys { get; set; } = new();

    /// <summary>乗務タブレットの窓の位置と大きさ（"x,y,幅,高さ"）。空なら画面の右に出す。</summary>
    public string TabletBounds { get; set; } = "";

    /// <summary>乗務タブレットの窓を、いつも最前面に出すか。</summary>
    public bool TabletTopMost { get; set; } = true;

    /// <summary>乗務タブレットの窓の拡大率（1.0 = 100%）。</summary>
    public double TabletZoom { get; set; } = 1.0;

    /// <summary>ロープレ時計の窓の位置と大きさ（"x,y,幅,高さ"）。空なら画面の右上に出す。</summary>
    public string ClockBounds { get; set; } = "";

    /// <summary>ロープレ時計の窓を、いつも最前面に出すか。</summary>
    public bool ClockTopMost { get; set; } = true;

    /// <summary>ロープレ時計の窓を開いていたか（次の起動でも開く）。</summary>
    public bool ClockOpen { get; set; }

    /// <summary>車掌用操作盤の位置と大きさ（"x,y,幅,高さ"）。空なら画面の右上に出す。</summary>
    public string ConductorPanelBounds { get; set; } = "";

    /// <summary>運転士: ゲームでブザーに使っているキー・ボタン（"N"、"Joy0:5"、両方なら "N|Joy0:5"）。押している間、車掌のゲームでもブザーを鳴らす。</summary>
    public string DriverBuzzerKey { get; set; } = "N";

    /// <summary>運転士: ゲームで使っているキー・コントローラーのボタン（ボタン名 → "N" や "Joy0:5"、両方なら "N|Joy0:5"）。押すと車掌のゲームでも同じボタンを押す。</summary>
    public Dictionary<string, string> DriverKeys { get; set; } = new();

    /// <summary>車掌: 運転士の列車との位置のずれを、ノッチを少し足し引きして縮める（制限速度・ATS は超えない）。</summary>
    public bool CrewCatchUp { get; set; } = true;

    /// <summary>車掌: 寄せ方（"time" タイミング優先: 運転士のノッチに1〜2段足し引きし、同じ時刻に止まる。"distance" 距離優先: 力行の一番強い段から B7 までを使って、ずれを詰める）。</summary>
    public string CrewCatchUpMode { get; set; } = "time";

    /// <summary>車掌用操作盤のキーの割り当て（操作の名前 → Keys の名前）。空なら既定（上の段の数字 2〜0）。</summary>
    public Dictionary<string, string> ConductorKeys { get; set; } = new();

    /// <summary>車掌のキーを、前の既定値（D・E・F など）から今の既定値へ移したか。</summary>
    public bool ConductorKeysMoved { get; set; }

    /// <summary>列車無線の音の設定。</summary>
    public RadioSettings Radio { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GokkoClient", "client.json");

    public static ClientConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(FilePath)) ?? new ClientConfig();
        }
        catch
        {
            // 壊れていたら既定値で始める
        }

        return new ClientConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 保存できなくても動作には影響しない
        }
    }
}

/// <summary>列車無線の音の設定（音の設定の画面で変える）。</summary>
public sealed class RadioSettings
{
    /// <summary>受話音量（0〜1）: 自分が話者として通話しているときの相手の声。無線機のつまみ。チャイム・呼出音もこの大きさ。</summary>
    public float Volume { get; set; } = 0.8f;

    /// <summary>聞いているだけのときの音量（0〜1）: 自分が当事者でない通話（ほかの人の共通通話・聞いているだけの一斉通告など）の声。</summary>
    public float ListenVolume { get; set; } = 0.8f;

    /// <summary>列車の放送装置: 車内放送・車外放送を聞く音量（0〜2。列車無線の受話音量とは別）。</summary>
    public float BroadcastVolume { get; set; } = 0.8f;

    /// <summary>列車の放送装置: インターホン（連絡）を聞く音量（0〜2）。</summary>
    public float IntercomVolume { get; set; } = 0.8f;

    /// <summary>列車の放送装置: 送る声の音量（0〜2。マイクの感度をかけたあとに、さらにかける。放送・連絡で共通）。</summary>
    public float OnboardSendGain { get; set; } = 1f;

    /// <summary>列車の放送装置: 自分が話した声を、スピーカー越しの音にして自分でも聞く（モニター。音量の確認用）。</summary>
    public bool OnboardMonitor { get; set; }

    /// <summary>マイクの感度（ブースト。倍率 0.25〜10）。</summary>
    public float MicGain { get; set; } = 1f;

    /// <summary>自動で音量をそろえる（AGC）。小さい声を持ち上げ、大きすぎる声は抑える。</summary>
    public bool AutoGain { get; set; } = true;

    /// <summary>この大きさより小さい音は送らない（0〜0.3。0 なら切らない）。</summary>
    public float Gate { get; set; } = 0.01f;

    /// <summary>入力デバイス（マイク）の ID。空なら Windows の既定のマイク。</summary>
    public string InputDeviceId { get; set; } = "";

    /// <summary>エコー除去（Windows の通話用の音声処理を使う）。</summary>
    public bool EchoCancel { get; set; } = true;

    /// <summary>無線風の音にする（受話の音）。</summary>
    public bool RadioEffect { get; set; } = true;

    /// <summary>無線風の強さ（0〜1）。</summary>
    public float EffectStrength { get; set; } = 0.6f;

    /// <summary>内線電話（卓上の電話機）のウィンドウの大きさ（倍率 0.6〜2）。</summary>
    public float ExtPhoneScaleDesk { get; set; } = 1f;

    /// <summary>内線電話（携帯）のウィンドウの大きさ（倍率 0.6〜2）。</summary>
    public float ExtPhoneScaleKeitai { get; set; } = 1f;

    /// <summary>人ごとの音量（社員番号 → 倍率 0〜2）。</summary>
    public Dictionary<string, float> SpeakerVolumes { get; set; } = new();

    /// <summary>人ごとの音量の画面に出す名前（社員番号 → ユーザー名）。</summary>
    public Dictionary<string, string> SpeakerNames { get; set; } = new();

    /// <summary>最後に聞いたときの列番と役割（社員番号 → 「1040列車（運転士）」など）。</summary>
    public Dictionary<string, string> SpeakerLabels { get; set; } = new();

    /// <summary>無線機の窓を最前面に固定する。</summary>
    public bool TopMost { get; set; }

    /// <summary>無線機の窓の大きさ（もとの大きさに対する倍率。運転士・車掌の無線機と司令機で別々）。</summary>
    public float DriverScale { get; set; } = 1f;
    public float DispatcherScale { get; set; } = 1f;
}
