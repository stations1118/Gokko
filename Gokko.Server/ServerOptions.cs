namespace Gokko.Server;

/// <summary>appsettings.json の "Gokko" の設定。環境変数（Gokko__AdminKey など）でも上書きできる。</summary>
public sealed class ServerOptions
{
    /// <summary>参加の合言葉。空なら誰でも参加できる。乗務員のログイン（パスワード・Discord）にも、この合言葉が要る。</summary>
    public string Password { get; set; } = "";

    /// <summary>参加の合言葉が合っているか（合言葉が空なら、いつも合う）。</summary>
    public bool PassphraseMatches(string? given) =>
        string.IsNullOrEmpty(Password) || System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(given ?? ""), System.Text.Encoding.UTF8.GetBytes(Password));

    /// <summary>管理画面の鍵。空なら管理機能は無効（安全のため）。</summary>
    public string AdminKey { get; set; } = "";

    /// <summary>
    /// 司令用管理画面（diasettings.html）の合言葉。他の参加者に公開する用で、管理キーとは別。
    /// できるのはダイヤグラムの操作（番線・発車順・進路取消・抑止解除・開通）だけ。空ならこの画面は無効。
    /// </summary>
    public string DiaPassword { get; set; } = "";

    /// <summary>
    /// 勤怠管理（kintai.html）を公開する用の鍵。管理キーとは別。この鍵で入ると「乗務員」タブは出ず、
    /// できるのは行路の割り当て・自動割り当て・臨変と、列車ごとの担当を見ることだけ（どの行路表を使うかは管理キーだけ）。
    /// 既定は「Kintai」（公開する用なので秘密にはしない）。空にすると、この鍵では入れない。
    /// </summary>
    public string KintaiKey { get; set; } = "Kintai";

    /// <summary>データファイルのフォルダ。相対パスなら exe の場所から。</summary>
    public string DataDirectory { get; set; } = "Data";

    /// <summary>状態を配信する間隔（ミリ秒）。特発が変わったときは、これを待たずにすぐ配信する。</summary>
    public int BroadcastIntervalMs { get; set; } = 500;

    public RandomOptions Random { get; set; } = new();

    public DiscordOptions Discord { get; set; } = new();
}

/// <summary>
/// 乗務員の Discord ログイン（OAuth2 の認可コード。スコープは identify だけで、メールアドレスは取らない）。
/// ClientSecret・IdKey は秘密。appsettings.json には書かず、環境変数（Gokko__Discord__ClientSecret など。本番は /etc/gokko.env）で渡す。
/// ClientId・ClientSecret・RedirectUri・IdKey がそろわなければ、Discord ログインは使えない（パスワードだけ）。
/// </summary>
public sealed class DiscordOptions
{
    /// <summary>Discord の Developer Portal で作ったアプリの Client ID。</summary>
    public string ClientId { get; set; } = "";

    /// <summary>そのアプリの Client Secret（秘密）。</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>戻り先。Developer Portal の Redirects に登録したものと同じにする（例: https://サーバー/api/auth/discord/callback）。</summary>
    public string RedirectUri { get; set; } = "";

    /// <summary>
    /// Discord のユーザー ID をそのまま保存しないための鍵（秘密。長いランダムな文字列）。Crew.json には、この鍵で作った HMAC だけを書く。
    /// 変えると、紐づけがすべて外れる。
    /// </summary>
    public string IdKey { get; set; } = "";

    /// <summary>パスワードでログインできる最後の日時（例: 2026-11-27T00:00:00+09:00）。過ぎたら Discord だけ。空なら期限なし。</summary>
    public DateTimeOffset? PasswordUntil { get; set; }

    public bool Enabled => ClientId.Length > 0 && ClientSecret.Length > 0 && RedirectUri.Length > 0 && IdKey.Length > 0;
}

/// <summary>
/// ランダム特発の設定。対象は「参加者の誰かの、現在の駅間と進行方向の先」の踏切。
/// 運転会の途中でも、管理画面から一部の値を変えられる。
/// </summary>
public sealed class RandomOptions
{
    /// <summary>起動時にランダム特発を動かすか。管理画面で切り替えられる。</summary>
    public bool Enabled { get; set; }

    public int RollIntervalSeconds { get; set; } = 47;
    public double Probability { get; set; } = 0.1;

    /// <summary>復帰されないとき、自動で解除するまでの秒数（手動で作動させたものにも効く）。0 なら自動で解除しない。</summary>
    public int AutoReleaseSeconds { get; set; } = 360;

    /// <summary>路線全体で、同時に作動させておく踏切の最大数（ランダム分のみ。手動は制限しない）。</summary>
    public int MaxConcurrent { get; set; } = 1;

    public bool AvoidRepeat { get; set; } = true;
    public int SectionsAhead { get; set; } = 1;
    public int SectionsBehind { get; set; }
    public bool FallbackToAllCrossings { get; set; }
}
