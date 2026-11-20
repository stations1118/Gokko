using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gokko.Server.Services;

/// <summary>
/// 乗務員（ユーザー）と、行路への割り当て（勤怠管理）。Data/Crew.json に保存し、サーバーを再起動しても残す。
/// ・ユーザーは社員番号（8桁）で見分ける。ログインも社員番号で、画面に出すのも社員番号（ID:10103200）。ユーザー名は名簿に載せるだけ
///   社員番号 = 元号（1 令和・9 平成・8 昭和）＋ 入社年度の和暦2桁 ＋ 連番5桁（01000〜99999 から空きをランダムに。所属は入社後に決まるので、番号では分けない）
///   例: 10103200 = 令和元年度（2019年度）入社
/// ・ログインは Discord（DiscordAuth）。Discord のユーザー ID はそのまま持たず、鍵つきのハッシュ（HMAC）だけを保存する。1つの Discord に社員番号1つ
///   新しい人は Discord でログインして申請し、勤怠管理で承認すると名簿に入る（申請の Discord のユーザー名は、承認・却下したら捨てる）
/// ・移行のあいだは、社員番号とパスワードでもログインできる（PasswordUntil まで）。パスワードでログインした人には、Discord との紐づけを勧める。
///   紐づけたら、その人のパスワードは消す。パスワードはサーバーが作って一度だけ見せ、保存するのはハッシュだけ。忘れたら勤怠管理で出し直す
///   （秘密の質問はやめた。前の版で保存した質問と答えのハッシュは、次に保存するときに消える）
/// ・ログインすると合い札（トークン）を渡し、タブレット・クライアントはそれで読みに来る。パスワードを出し直すと、それまでの合い札は使えなくなる
/// ・パスワードを続けて間違えたら、しばらく受け付けない
/// ・行路（Data/Duties.csv）1つに、ユーザーを1人割り当てる。タブレットが読みに来た時刻を覚えておき、「ログイン中」と出す
/// ・タブレットで［出勤］すると、行路のない人に空いている行路をランダムに1つ割り当てる（所属が合うもの・前回と違うもの・ほかの人と列車が重ならないもの）。
///   列番を入れて開いたら、その列車の行路を割り当てる。ほかの人がもう持っていれば、あとから来た人を「臨変」として重ねて持たせる
/// </summary>
public sealed class CrewRoster
{
    public const string FileName = "Crew.json";

    /// <summary>振れる役職。「司令員」は、クライアントで司令として参加できる（列車無線の応答・一斉通告・区間防護）。</summary>
    public const string DispatcherRole = "司令員";
    /// <summary>「信号扱い」は、信号制御盤の鍵（解放の鍵など）を刺して回せる。てこ・ボタンは、盤を開いてログインした人なら誰でも扱える。</summary>
    public const string RelayKeyRole = "信号扱い";
    public static readonly string[] RoleNames = [DispatcherRole, RelayKeyRole];

    /// <summary>所属（乗務区）。社員番号には関係しない。</summary>
    public static readonly string[] Depots = ["道列", "駒列"];

    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(20);
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockTime = TimeSpan.FromMinutes(10);
    private const string PasswordChars = "abcdefghjkmnpqrstuvwxyz23456789"; // 見間違えやすい i l o 0 1 を除く

    private readonly object _lock = new();
    private readonly string _path;
    private readonly ILogger<CrewRoster>? _log;
    private List<CrewUser> _users = new();
    private Dictionary<string, string> _assign = new(); // 行路番号 → 社員番号
    private Dictionary<string, DateTime> _attend = new(); // 出勤した人（社員番号 → 出勤した時刻 UTC）
    private Dictionary<string, Dictionary<string, RoleAttend>> _roleAttend = new(); // 役職の出勤（司令員・信号扱い → 社員番号 → 出勤した時刻・駅）。乗務の出勤とは別
    private Dictionary<string, string> _lastDuty = new(); // 社員番号 → 前回割り当てた行路番号（自動割り当てで同じ行路を避ける）
    private HashSet<string> _auto = new(); // 自動で割り当てた行路番号（手で選び直すと消える）
    private List<CrewApplication> _applications = new(); // Discord で申請した新しい人
    private List<ExtraAssign> _extras = new(); // 臨変（すでにほかの人が持っている行路を、あとから重ねて持った人）
    private readonly Dictionary<string, (DateTime Utc, string Train)> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Count, DateTime Until)> _failures = new(StringComparer.OrdinalIgnoreCase);

    public sealed class CrewUser
    {
        /// <summary>社員番号（8桁）。</summary>
        public string Id { get; set; } = "";

        /// <summary>ユーザー名（名簿に載せるだけ。画面には社員番号を出す）。</summary>
        public string Name { get; set; } = "";

        /// <summary>入社年度（西暦）。</summary>
        public int JoinYear { get; set; }

        /// <summary>所属（道列・駒列）。</summary>
        public string Depot { get; set; } = "";

        /// <summary>役職（「司令員」など）。勤怠管理で振る。</summary>
        public List<string> Roles { get; set; } = new();

        public string Salt { get; set; } = "";
        public string PasswordHash { get; set; } = "";

        /// <summary>紐づけた Discord のユーザー ID の HMAC（DiscordAuth.HashId）。紐づけていなければ空。</summary>
        public string DiscordHash { get; set; } = "";
        public List<string> TokenHashes { get; set; } = new();

        /// <summary>
        /// 仮の合い札（TokenHashes のうち、PC のクライアントで合言葉なしにログインしたもの）。
        /// クライアントが合言葉を入れてサーバーに接続したら本物になる（ConfirmToken）。それまでは画面（タブレットなど）では使えない。
        /// </summary>
        public List<string> PendingTokenHashes { get; set; } = new();
        public DateTime CreatedUtc { get; set; }
    }

    /// <summary>Discord で申請した新しい人（勤怠管理で承認すると名簿に入る）。</summary>
    public sealed class CrewApplication
    {
        /// <summary>申請の番号（承認・却下で使う）。</summary>
        public string Key { get; set; } = "";
        public string DiscordHash { get; set; } = "";

        /// <summary>Discord のユーザー名（担当者が本人か確かめる用。承認・却下したら消える）。</summary>
        public string DiscordName { get; set; } = "";
        public string Name { get; set; } = "";
        public int JoinYear { get; set; }
        public string Depot { get; set; } = "";

        /// <summary>もう社員番号を持っている人が入れた社員番号（承認すると、その人に紐づける）。新しい人は空。</summary>
        public string ExistingId { get; set; } = "";
        public DateTime Utc { get; set; }
    }

    /// <summary>臨変: 行路 Duty を、担当者とは別に User も持つ（列番を入れて乗ったら、その列車の行路がもう埋まっていたとき）。</summary>
    public sealed class ExtraAssign
    {
        public string Duty { get; set; } = "";
        public string User { get; set; } = "";
        public DateTime Utc { get; set; }
    }

    private sealed class FileShape
    {
        public List<CrewUser> Users { get; set; } = new();
        public Dictionary<string, string> Assignments { get; set; } = new();
        public Dictionary<string, DateTime> Attendance { get; set; } = new();
        public Dictionary<string, string> LastDuties { get; set; } = new();
        public List<string> AutoAssigned { get; set; } = new();
        public List<ExtraAssign> Extras { get; set; } = new();
        public Dictionary<string, Dictionary<string, RoleAttend>> RoleAttendance { get; set; } = new();
        public List<CrewApplication> Applications { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public CrewRoster(string dataDir, ILogger<CrewRoster>? log = null)
    {
        _path = Path.Combine(dataDir, FileName);
        _log = log;
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<FileShape>(File.ReadAllText(_path), Json) is { } f)
            {
                _users = f.Users.Where(u => !string.IsNullOrWhiteSpace(u.Name)).ToList();

                // 社員番号のなかったころのユーザーには、作った年度・道列で番号を振る（割り当ては、ユーザー名から社員番号に置き換える）
                var oldNames = _users.ToDictionary(u => u, u => u.Name);
                var changed = false;
                foreach (var u in _users)
                {
                    if (IsEmployeeId(u.Id) && _users.Count(x => x.Id == u.Id) == 1) continue;
                    if (u.JoinYear == 0) u.JoinYear = FiscalYear(u.CreatedUtc == default ? DateTime.UtcNow : u.CreatedUtc);
                    if (!Depots.Contains(u.Depot)) u.Depot = Depots[0];
                    u.Id = "";
                    u.Id = NewEmployeeId(u.JoinYear) ?? "";
                    changed = true;
                }

                _assign = new Dictionary<string, string>();
                foreach (var (duty, who) in f.Assignments)
                {
                    var u = _users.FirstOrDefault(x => x.Id == who) ?? _users.FirstOrDefault(x => string.Equals(oldNames[x], who, StringComparison.OrdinalIgnoreCase));
                    if (u == null) continue;
                    _assign[duty] = u.Id;
                    if (u.Id != who) changed = true;
                }

                var ids = _users.Select(u => u.Id).ToHashSet();
                _attend = f.Attendance.Where(x => ids.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
                _lastDuty = f.LastDuties.Where(x => ids.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
                _auto = f.AutoAssigned.Where(_assign.ContainsKey).ToHashSet();
                _extras = f.Extras.Where(x => ids.Contains(x.User) && x.Duty.Length > 0).ToList();
                _roleAttend = f.RoleAttendance.Where(x => RoleNames.Contains(x.Key))
                    .ToDictionary(x => x.Key, x => x.Value.Where(a => _users.Any(u => u.Id == a.Key && u.Roles.Contains(x.Key))).ToDictionary(a => a.Key, a => a.Value));
                _applications = f.Applications.Where(x => x.Key.Length > 0 && x.DiscordHash.Length > 0).ToList();

                if (changed) Save();
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "{File} を読めませんでした（空で始めます）", FileName);
        }
    }

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new FileShape
            {
                Users = _users, Assignments = _assign, Attendance = _attend, LastDuties = _lastDuty,
                AutoAssigned = _auto.OrderBy(x => x, StringComparer.Ordinal).ToList(), Extras = _extras, RoleAttendance = _roleAttend, Applications = _applications
            }, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "{File} に保存できませんでした", FileName);
        }
    }

    /// <summary>社員番号（「ID:」付き・全角でもよい）か、ユーザー名で探す。</summary>
    private CrewUser? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var id = NormalizeId(key);
        return _users.FirstOrDefault(u => u.Id == id)
               ?? _users.FirstOrDefault(u => string.Equals(u.Name, key.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    // ---- 社員番号 ----

    private static readonly TimeZoneInfo Jst = FindJst();

    private static TimeZoneInfo FindJst()
    {
        foreach (var id in new[] { "Tokyo Standard Time", "Asia/Tokyo" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
                // 次の名前で探す
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");
    }

    /// <summary>その日の年度（4月はじまり、日本時間）。</summary>
    public static int FiscalYear(DateTime utc)
    {
        var t = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Jst);
        return t.Month >= 4 ? t.Year : t.Year - 1;
    }

    /// <summary>選べる入社年度（昭和40年度〜今年度。定年65歳でも、15歳で入った人までまだ在籍している範囲）。</summary>
    public static (int From, int To) JoinYears => (1965, FiscalYear(DateTime.UtcNow));

    /// <summary>入力された社員番号をならす（全角→半角、「ID:」・空白・ハイフンを除く）。</summary>
    public static string NormalizeId(string? s)
    {
        var t = (s ?? "").Normalize(NormalizationForm.FormKC).Trim();
        if (t.StartsWith("ID", StringComparison.OrdinalIgnoreCase)) t = t[2..].TrimStart(':', ' ');
        return t.Replace("-", "").Replace(" ", "");
    }

    public static bool IsEmployeeId(string? s) => s is { Length: 8 } && s.All(char.IsAsciiDigit) && s[0] != '0';

    /// <summary>社員番号の上3桁（元号＋和暦の年）。令和元年度 = 101、平成元年度 = 901、昭和63年度 = 863（2019年度は令和元年度、1989年度は平成元年度とする）。</summary>
    private static string EraPrefix(int year) => year >= 2019 ? $"1{year - 2018:D2}" : year >= 1989 ? $"9{year - 1988:D2}" : $"8{year - 1925:D2}";

    /// <summary>新しい社員番号（その年度の範囲から、空いている番号をランダムに）。空きがなければ null。</summary>
    private string? NewEmployeeId(int year)
    {
        var prefix = EraPrefix(year);
        const int lo = 1000;
        for (var i = 0; i < 5000; i++)
        {
            var id = prefix + (lo + RandomNumberGenerator.GetInt32(100000 - lo)).ToString("D5");
            if (_users.All(u => u.Id != id)) return id;
        }

        return null;
    }

    private static string? CheckProfile(int year, string? depot)
    {
        var (from, to) = JoinYears;
        if (year < from || year > to) return $"入社年度は {from}〜{to} の間で選んでください";
        if (!Depots.Contains(depot)) return "道列か駒列を選んでください";
        return null;
    }

    /// <summary>名簿の1人分（画面に出す分）。</summary>
    public sealed record CrewInfo(string Id, string Name, int JoinYear, string Depot, bool Discord, bool HasPassword, DateTime Created, IReadOnlyList<string> Roles);

    private static CrewInfo Info(CrewUser u) => new(u.Id, u.Name, u.JoinYear, u.Depot, u.DiscordHash.Length > 0, u.PasswordHash.Length > 0, u.CreatedUtc, u.Roles.ToList());

    /// <summary>パスワードでログインできる最後の日時（設定の Gokko:Discord:PasswordUntil）。null なら期限なし。</summary>
    public DateTimeOffset? PasswordUntil { get; set; }

    public bool PasswordClosed => PasswordUntil is { } t && DateTimeOffset.UtcNow >= t;

    // ---- パスワードのハッシュ ----

    private static string NewPassword()
    {
        var s = new StringBuilder();
        for (var i = 0; i < 8; i++)
        {
            if (i == 4) s.Append('-');
            s.Append(PasswordChars[RandomNumberGenerator.GetInt32(PasswordChars.Length)]);
        }

        return s.ToString(); // 例: k7mq-3xwp
    }

    private static string Hash(string text, string salt) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(text), Convert.FromBase64String(salt), 100_000, HashAlgorithmName.SHA256, 32));

    private static bool SameHash(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static string NormalizePassword(string? p) => (p ?? "").Trim().ToLowerInvariant().Replace(" ", "");

    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    /// <summary>新しいパスワードを決め、これまでの合い札を捨てる。新しいパスワードを返す。</summary>
    private static string ResetPassword(CrewUser u)
    {
        var pw = NewPassword();
        u.Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        u.PasswordHash = Hash(pw.Replace("-", ""), u.Salt);
        u.TokenHashes.Clear();
        u.PendingTokenHashes.Clear();
        return pw;
    }

    /// <param name="provisional">仮の合い札にする（PC のクライアントで、合言葉なしにログインしたとき）</param>
    private static string IssueToken(CrewUser u, bool provisional = false)
    {
        var t = NewToken();
        u.TokenHashes.Add(TokenHash(t));
        if (provisional) u.PendingTokenHashes.Add(TokenHash(t));
        while (u.TokenHashes.Count > 5) u.TokenHashes.RemoveAt(0); // 5台まで
        u.PendingTokenHashes.RemoveAll(h => !u.TokenHashes.Contains(h));
        return t;
    }

    private string? Locked(string key) =>
        _failures.TryGetValue(key, out var f) && f.Count >= MaxFailures && DateTime.UtcNow < f.Until
            ? $"続けて間違えたため、{Math.Ceiling((f.Until - DateTime.UtcNow).TotalMinutes)}分ほど受け付けません"
            : null;

    private void Fail(string key)
    {
        var f = _failures.GetValueOrDefault(key);
        if (f.Count >= MaxFailures && DateTime.UtcNow >= f.Until) f = default;
        _failures[key] = (f.Count + 1, DateTime.UtcNow + LockTime);
    }

    private static string? CheckName(string n) =>
        n.Length == 0 ? "ユーザー名を入れてください"
        : n.Length > 20 ? "ユーザー名は20文字までです"
        : IsEmployeeId(NormalizeId(n)) ? "社員番号と同じ形のユーザー名は使えません"
        : null;

    // ---- 登録・ログイン・出し直し ----

    /// <summary>新しいユーザーを作る（名簿にはまだ入れない）。id を指定したら、その社員番号にする。</summary>
    private (CrewUser? User, string? Error) Create(string? name, int year, string? depot, string? id)
    {
        var n = (name ?? "").Trim();
        if (CheckName(n) is { } e1) return (null, e1);
        if (CheckProfile(year, depot) is { } e2) return (null, e2);
        if (_users.Any(u => string.Equals(u.Name, n, StringComparison.OrdinalIgnoreCase))) return (null, $"{n} はもう使われています");
        string? newId;
        if (string.IsNullOrWhiteSpace(id))
        {
            newId = NewEmployeeId(year);
            if (newId == null) return (null, "この年度の社員番号に空きがありません");
        }
        else
        {
            newId = NormalizeId(id);
            if (!IsEmployeeId(newId)) return (null, "社員番号は、0 で始まらない8桁の数字にしてください");
            if (_users.Any(u => u.Id == newId)) return (null, $"ID:{newId} はもう使われています");
        }

        return (new CrewUser { Id = newId, Name = n, JoinYear = year, Depot = depot!, CreatedUtc = DateTime.UtcNow }, null);
    }

    /// <summary>勤怠管理の画面でユーザーを作る。id を空にすると自動で振る。社員番号とパスワードを返す（移行のあいだだけ使える。本人が Discord と紐づける）。</summary>
    public (string? Id, string? Password, string? Error) AddUser(string? name, int year, string? depot, string? id)
    {
        lock (_lock)
        {
            var (u, error) = Create(name, year, depot, id);
            if (u == null) return (null, null, error);
            var pw = ResetPassword(u);
            _users.Add(u);
            Save();
            return (u.Id, pw, null);
        }
    }

    /// <summary>ログイン（社員番号とパスワード。移行のあいだだけ）。社員番号と合い札を返す。</summary>
    /// <param name="provisional">仮の合い札にする（PC のクライアントから。合言葉を入れて接続したら本物になる）</param>
    public (string? Id, string? Token, string? Error) Login(string? id, string? password, bool provisional = false)
    {
        if (PasswordClosed) return (null, null, "パスワードでのログインは終わりました。Discord でログインしてください");
        lock (_lock)
        {
            var key = "login:" + NormalizeId(id);
            if (Locked(key) is { } l) return (null, null, l);
            var u = Find(id);
            if (u != null && u.PasswordHash.Length == 0 && u.DiscordHash.Length > 0) return (null, null, "この社員番号は Discord と紐づけてあります。Discord でログインしてください");
            if (u == null || u.PasswordHash.Length == 0 || !SameHash(Hash(NormalizePassword(password).Replace("-", ""), u.Salt), u.PasswordHash))
            {
                Fail(key);
                return (null, null, "社員番号かパスワードがちがいます");
            }

            _failures.Remove(key);
            var token = IssueToken(u, provisional);
            Save();
            return (u.Id, token, null);
        }
    }

    // ---- Discord ----

    /// <summary>その Discord（HMAC）に紐づいた人の社員番号。いなければ null。</summary>
    public string? UserOfDiscord(string hash)
    {
        lock (_lock) return _users.FirstOrDefault(u => u.DiscordHash == hash)?.Id;
    }

    /// <summary>Discord でログインする（紐づけてある人）。合い札を返す。</summary>
    public (string? Id, string? Token) LoginByDiscord(string hash, bool provisional = false)
    {
        lock (_lock)
        {
            if (hash.Length == 0 || _users.FirstOrDefault(u => u.DiscordHash == hash) is not { } u) return (null, null);
            var token = IssueToken(u, provisional);
            Save();
            return (u.Id, token);
        }
    }

    /// <summary>
    /// ログインしている本人に Discord を紐づける。紐づけたら、その人のパスワードは消す（合い札はそのまま）。
    /// ほかの人に紐づいている Discord なら断る。うまくいかなければ理由を返す。
    /// </summary>
    public string? LinkDiscord(string id, string hash)
    {
        lock (_lock)
        {
            if (Find(id) is not { } u) return "ユーザーがいません";
            if (_users.Any(x => x != u && x.DiscordHash == hash)) return "この Discord アカウントは、ほかの社員番号に紐づいています";
            u.DiscordHash = hash;
            u.Salt = "";
            u.PasswordHash = "";
            _failures.Remove("login:" + u.Id);
            _applications.RemoveAll(a => a.DiscordHash == hash); // 申請していたら、もう要らない
            Save();
            return null;
        }
    }

    /// <summary>勤怠管理から Discord の紐づけを外す（Discord を作り直した人など。外したら、本人は Discord で申請し直す）。</summary>
    public string? UnlinkDiscord(string? id)
    {
        lock (_lock)
        {
            if (Find(id) is not { } u) return "その人はいません";
            u.DiscordHash = "";
            u.TokenHashes.Clear();
            u.PendingTokenHashes.Clear();
            Save();
            return null;
        }
    }

    /// <summary>その Discord の申請（承認待ち）があるか。</summary>
    public bool HasApplication(string hash)
    {
        lock (_lock) return _applications.Any(a => a.DiscordHash == hash);
    }

    /// <summary>
    /// Discord でログインした、まだ名簿にいない人の申請。existingId に社員番号を入れたら、承認でその人に紐づける（パスワードを失くした人など）。
    /// うまくいかなければ理由を返す。
    /// </summary>
    public string? Apply(string hash, string discordName, string? name, int year, string? depot, string? existingId)
    {
        var n = (name ?? "").Trim();
        lock (_lock)
        {
            if (_users.Any(u => u.DiscordHash == hash)) return "この Discord アカウントは、もう社員番号に紐づいています。Discord でログインしてください";
            var ex = "";
            if (!string.IsNullOrWhiteSpace(existingId))
            {
                if (Find(existingId) is not { } eu || eu.Id != NormalizeId(existingId)) return "その社員番号はありません";
                if (eu.DiscordHash.Length > 0) return "その社員番号は、もう Discord と紐づいています";
                ex = eu.Id;
            }
            else
            {
                if (CheckName(n) is { } e1) return e1;
                if (CheckProfile(year, depot) is { } e2) return e2;
                if (_users.Any(u => string.Equals(u.Name, n, StringComparison.OrdinalIgnoreCase))) return $"{n} はもう使われています";
            }

            if (_applications.Count >= 50 && _applications.All(a => a.DiscordHash != hash)) return "申請が多すぎます。しばらくしてからにしてください";
            _applications.RemoveAll(a => a.DiscordHash == hash); // 出し直したら、前の申請と入れ替える
            _applications.Add(new CrewApplication
            {
                Key = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)), DiscordHash = hash,
                DiscordName = discordName.Length > 40 ? discordName[..40] : discordName,
                Name = ex.Length > 0 ? "" : n, JoinYear = ex.Length > 0 ? 0 : year, Depot = ex.Length > 0 ? "" : depot!, ExistingId = ex, Utc = DateTime.UtcNow
            });
            Save();
            return null;
        }
    }

    public sealed record ApplicationInfo(string Key, string DiscordName, string Name, int JoinYear, string Depot, string ExistingId, string ExistingName, DateTime Utc);

    /// <summary>申請の一覧（古い順）。</summary>
    public List<ApplicationInfo> Applications()
    {
        lock (_lock)
            return _applications.OrderBy(a => a.Utc)
                .Select(a => new ApplicationInfo(a.Key, a.DiscordName, a.Name, a.JoinYear, a.Depot, a.ExistingId, Find(a.ExistingId)?.Name ?? "", a.Utc)).ToList();
    }

    /// <summary>申請を承認する（新しい人は名簿に入れる。社員番号を入れた人は、その人に紐づける）。社員番号を返す。</summary>
    public (string? Id, string? Error) Approve(string? key)
    {
        lock (_lock)
        {
            if (_applications.FirstOrDefault(a => a.Key == key) is not { } a) return (null, "その申請はありません");
            if (_users.Any(u => u.DiscordHash == a.DiscordHash)) { _applications.Remove(a); Save(); return (null, "この Discord は、もうほかの社員番号に紐づいています"); }
            CrewUser u;
            if (a.ExistingId.Length > 0)
            {
                if (Find(a.ExistingId) is not { } eu) return (null, $"ID:{a.ExistingId} はもういません");
                if (eu.DiscordHash.Length > 0) return (null, $"ID:{eu.Id} は、もう Discord と紐づいています（先に紐づけを外してください）");
                u = eu;
                u.Salt = "";
                u.PasswordHash = "";
            }
            else
            {
                var (nu, error) = Create(a.Name, a.JoinYear, a.Depot, null);
                if (nu == null) return (null, error);
                u = nu;
                _users.Add(u);
            }

            u.DiscordHash = a.DiscordHash;
            _applications.Remove(a);
            Save();
            return (u.Id, null);
        }
    }

    /// <summary>申請を断る（消す）。</summary>
    public bool Reject(string? key)
    {
        lock (_lock)
        {
            if (_applications.RemoveAll(a => a.Key == key) == 0) return false;
            Save();
            return true;
        }
    }

    // ---- スマホでログインする QR コード（乗務メニューの［スマホでログイン］。パスワードなしで、同じ人としてログインする） ----

    private static readonly TimeSpan QrLife = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, (string User, DateTime Until)> _qr = new(StringComparer.Ordinal); // 使い捨ての番号 → 社員番号（サーバーの中だけ）

    /// <summary>ログインしている本人に、スマホでログインする使い捨ての番号を出す（5分・1回だけ使える）。</summary>
    public (string Code, DateTime UntilUtc) IssueQrCode(string id)
    {
        lock (_lock)
        {
            foreach (var k in _qr.Where(x => x.Value.Until <= DateTime.UtcNow || x.Value.User == id).Select(x => x.Key).ToList()) _qr.Remove(k); // 古いもの・前に出したもの
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var until = DateTime.UtcNow + QrLife;
            _qr[code] = (id, until);
            return (code, until);
        }
    }

    /// <summary>QR コードの番号でログインする（1回使うと消える）。社員番号と合い札を返す。</summary>
    public (string? Id, string? Token, string? Error) LoginByQrCode(string? code)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(code) || !_qr.Remove(code, out var q) || q.Until <= DateTime.UtcNow)
                return (null, null, "この QR コードは使えません（5分で切れ、1回しか使えません）。もう一度出してください");
            if (Find(q.User) is not { } u) return (null, null, "ユーザーがいません");
            var token = IssueToken(u);
            Save();
            return (u.Id, token, null);
        }
    }

    // 乗務メニューから画面を開くときの引き継ぎ札（画面#handoff=札）。30秒で切れ、1回だけ使える。
    // 札を合い札に替えると、メニューと同じ合い札を返す（新しい合い札は作らない。1人5台までの枠を減らさないため）
    private static readonly TimeSpan HandoffLife = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, (string Token, DateTime Until)> _handoff = new(StringComparer.Ordinal); // 札 → 合い札（サーバーの中だけ）

    /// <summary>引き継ぎ札を出す（ログインしている本人の合い札から）。合い札が使えなければ null。</summary>
    public string? IssueHandoff(string? token)
    {
        if (UserOfToken(token) == null) return null;
        lock (_lock)
        {
            foreach (var k in _handoff.Where(x => x.Value.Until <= DateTime.UtcNow).Select(x => x.Key).ToList()) _handoff.Remove(k);
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _handoff[code] = (token!, DateTime.UtcNow + HandoffLife);
            return code;
        }
    }

    /// <summary>引き継ぎ札を合い札に替える（1回使うと消える）。社員番号と合い札を返す。</summary>
    public (string? Id, string? Token) RedeemHandoff(string? code)
    {
        (string Token, DateTime Until) h;
        lock (_lock)
            if (string.IsNullOrEmpty(code) || !_handoff.Remove(code, out h) || h.Until <= DateTime.UtcNow) return (null, null);
        return UserOfToken(h.Token) is { } id ? (id, h.Token) : (null, null);
    }

    /// <summary>合い札から、ログインしている人の社員番号。使えない合い札・仮の合い札なら null。</summary>
    public string? UserOfToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var h = TokenHash(token);
        lock (_lock) return _users.FirstOrDefault(u => u.TokenHashes.Contains(h) && !u.PendingTokenHashes.Contains(h))?.Id;
    }

    /// <summary>
    /// PC のクライアントの合い札から、社員番号（仮の合い札でもよい）。クライアントの起動時（/crew/me）と、
    /// 合言葉を確かめたあとの参加（GameHub・CrewHub の Join）だけで使う。
    /// </summary>
    public string? UserOfClientToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var h = TokenHash(token);
        lock (_lock) return _users.FirstOrDefault(u => u.TokenHashes.Contains(h))?.Id;
    }

    /// <summary>仮の合い札を本物にする（クライアントが合言葉を入れて参加できたとき）。</summary>
    public void ConfirmToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        var h = TokenHash(token);
        lock (_lock)
        {
            var changed = false;
            foreach (var u in _users) changed |= u.PendingTokenHashes.Remove(h);
            if (changed) Save();
        }
    }

    /// <summary>ログアウト（その合い札を捨てる）。</summary>
    public void Logout(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        var h = TokenHash(token);
        lock (_lock)
        {
            foreach (var u in _users.Where(u => u.TokenHashes.Remove(h))) u.PendingTokenHashes.Remove(h);
            Save();
        }
    }

    /// <summary>
    /// 勤怠管理の画面から名簿を書き換える（社員番号・ユーザー名・入社年度・所属）。
    /// 社員番号を変えても、行路の割り当てとログイン（合い札）はそのまま引き継ぐ。うまくいかなければ理由を返す。
    /// </summary>
    public string? Update(string? id, string? newId, string? name, int year, string? depot)
    {
        lock (_lock)
        {
            if (Find(id) is not { } u) return "その人はいません";
            var n = (name ?? "").Trim();
            if (CheckName(n) is { } e1) return e1;
            if (CheckProfile(year, depot) is { } e2) return e2;
            if (_users.Any(x => x != u && string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase))) return $"{n} はもう使われています";
            var nid = string.IsNullOrWhiteSpace(newId) ? u.Id : NormalizeId(newId);
            if (!IsEmployeeId(nid)) return "社員番号は、0 で始まらない8桁の数字にしてください";
            if (_users.Any(x => x != u && x.Id == nid)) return $"ID:{nid} はもう使われています";

            if (nid != u.Id)
            {
                foreach (var d in _assign.Where(x => x.Value == u.Id).Select(x => x.Key).ToList()) _assign[d] = nid;
                if (_seen.Remove(u.Id, out var seen)) _seen[nid] = seen;
                if (_attend.Remove(u.Id, out var at)) _attend[nid] = at;
                if (_lastDuty.Remove(u.Id, out var ld)) _lastDuty[nid] = ld;
                foreach (var x in _extras.Where(x => x.User == u.Id)) x.User = nid;
                _failures.Remove("login:" + u.Id);
                u.Id = nid;
            }

            u.Name = n;
            u.JoinYear = year;
            u.Depot = depot!;
            Save();
            return null;
        }
    }

    /// <summary>勤怠管理の画面からパスワードを出し直す（移行のあいだだけ使える。それまでの合い札は使えなくなる）。</summary>
    public (string? Password, string? Error) ResetByAdmin(string? id)
    {
        lock (_lock)
        {
            if (Find(id) is not { } u) return (null, "その人はいません");
            var pw = ResetPassword(u);
            _failures.Remove("login:" + u.Id);
            Save();
            return (pw, null);
        }
    }

    // ---- 一覧・割り当て ----

    /// <summary>登録されている人の社員番号（社員番号かユーザー名で探す）。なければ null。</summary>
    public string? User(string? key)
    {
        lock (_lock) return Find(key)?.Id;
    }

    /// <summary>その人の名簿（社員番号かユーザー名で探す）。</summary>
    public CrewInfo? InfoOf(string? key)
    {
        lock (_lock) return Find(key) is { } u ? Info(u) : null;
    }

    /// <summary>その人にその役職があるか。</summary>
    public bool HasRole(string? id, string role)
    {
        lock (_lock) return Find(id) is { } u && u.Roles.Contains(role);
    }

    /// <summary>役職を振る・外す（勤怠管理の画面から）。うまくいかなければ理由を返す。</summary>
    public string? SetRole(string? id, string? role, bool on)
    {
        lock (_lock)
        {
            if (Find(id) is not { } u) return "その人はいません";
            if (!RoleNames.Contains(role)) return "その役職はありません";
            if (on == u.Roles.Contains(role!)) return null;
            if (on) u.Roles.Add(role!); else u.Roles.Remove(role!);
            if (!on && _roleAttend.TryGetValue(role!, out var att)) att.Remove(u.Id); // 役職を外したら、その役職の出勤も外す
            Save();
            return null;
        }
    }

    /// <summary>名簿（社員番号の順）。</summary>
    public List<CrewInfo> Users()
    {
        lock (_lock) return _users.OrderBy(u => u.Id, StringComparer.Ordinal).Select(Info).ToList();
    }

    /// <summary>ユーザーを消す（割り当てていた行路は、担当なしに戻す）。</summary>
    public bool RemoveUser(string? id)
    {
        lock (_lock)
        {
            if (Find(id) is not { } u) return false;
            _users.Remove(u);
            foreach (var d in _assign.Where(x => x.Value == u.Id).Select(x => x.Key).ToList()) { _assign.Remove(d); _auto.Remove(d); }
            _seen.Remove(u.Id);
            _attend.Remove(u.Id);
            _lastDuty.Remove(u.Id);
            foreach (var att in _roleAttend.Values) att.Remove(u.Id);
            _extras.RemoveAll(x => x.User == u.Id);
            Save();
            return true;
        }
    }

    /// <summary>行路に人を割り当てる（user は社員番号。空なら担当なし）。うまくいかなければ理由を返す。</summary>
    public string? Assign(string duty, string? user)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(user))
            {
                _assign.Remove(duty);
            }
            else
            {
                if (Find(user) is not { } u) return $"{user} という社員番号の人はいません";
                _assign[duty] = u.Id;
                _lastDuty[u.Id] = duty;
                _extras.RemoveAll(x => x.Duty == duty && x.User == u.Id); // 担当になったら臨変ではなくなる
            }

            _auto.Remove(duty); // 手で選んだら、自動の印を外す

            Save();
        }

        return null;
    }

    /// <summary>行路の担当者の社員番号（いなければ null）。</summary>
    public string? AssignedTo(string duty)
    {
        lock (_lock) return _assign.GetValueOrDefault(duty);
    }

    /// <summary>すべての割り当て（行路番号 → 社員番号。行路番号の順）。</summary>
    public List<(string Duty, string User)> Assignments()
    {
        lock (_lock) return _assign.OrderBy(x => x.Key, DutyRoster.IdOrder).Select(x => (x.Key, x.Value)).ToList();
    }

    /// <summary>その人（社員番号）が担当する行路の番号（行路番号の順。臨変で持っている行路はうしろに）。</summary>
    public List<string> DutiesOf(string id)
    {
        lock (_lock) return DutiesOfLocked(id);
    }

    // ---- 出勤・自動割り当て・臨変 ----

    /// <summary>出勤しているか（出勤した時刻 UTC。していなければ null）。</summary>
    public DateTime? AttendedAt(string id)
    {
        lock (_lock) return _attend.TryGetValue(id, out var t) ? t : null;
    }

    /// <summary>その行路を自動で割り当てたか。</summary>
    public bool IsAuto(string duty)
    {
        lock (_lock) return _auto.Contains(duty);
    }

    /// <summary>その人が臨変で持っている行路。</summary>
    public List<string> ExtrasOf(string id)
    {
        lock (_lock) return _extras.Where(x => x.User == id).Select(x => x.Duty).ToList();
    }

    /// <summary>すべての臨変（行路番号の順）。</summary>
    public List<(string Duty, string User)> Extras()
    {
        lock (_lock) return _extras.OrderBy(x => x.Duty, DutyRoster.IdOrder).ThenBy(x => x.Utc).Select(x => (x.Duty, x.User)).ToList();
    }

    /// <summary>臨変を外す。</summary>
    public void RemoveExtra(string duty, string user)
    {
        lock (_lock)
        {
            if (_extras.RemoveAll(x => x.Duty == duty && x.User == NormalizeId(user)) > 0) Save();
        }
    }

    /// <summary>出勤を付ける・外す（勤怠管理の画面から）。うまくいかなければ理由を返す。</summary>
    public string? SetAttend(string? id, bool on)
    {
        lock (_lock)
        {
            if (Find(id) is not { } u) return "その人はいません";
            if (on) _attend.TryAdd(u.Id, DateTime.UtcNow); else _attend.Remove(u.Id);
            Save();
            return null;
        }
    }

    /// <summary>
    /// 退勤（タブレットの［退勤］）。出勤を外し、担当していた行路と臨変も外す（外した行路は未割当になり、ほかの人に割り当てられる）。
    /// 外した行路番号を返す。
    /// </summary>
    public (List<string>? Released, string? Error) Leave(string id)
    {
        lock (_lock)
        {
            if (Find(id) is not { } u) return (null, "ユーザーがいません");
            var released = DutiesOfLocked(u.Id);
            foreach (var d in _assign.Where(x => x.Value == u.Id).Select(x => x.Key).ToList()) { _assign.Remove(d); _auto.Remove(d); }
            _extras.RemoveAll(x => x.User == u.Id);
            _attend.Remove(u.Id);
            Save();
            return (released, null);
        }
    }

    /// <summary>全員の出勤を外す（運転会のおわりに。司令員・信号扱いの出勤も）。割り当てはそのまま。</summary>
    public int ClearAttendance()
    {
        lock (_lock)
        {
            var n = _attend.Count + _roleAttend.Values.Sum(x => x.Count);
            _attend.Clear();
            _roleAttend.Clear();
            Save();
            return n;
        }
    }

    // ---- 司令員・信号扱いの出勤（乗務の出勤・行路とは別。その役職の画面を開く前に、タブレット・乗務メニューで出勤する） ----

    /// <summary>役職の出勤1件（出勤した時刻と、信号扱いなら扱う駅の ID）。</summary>
    public sealed class RoleAttend
    {
        public DateTime Utc { get; set; }
        public string Station { get; set; } = "";
    }

    /// <summary>
    /// 役職の出勤を付ける・外す。その役職がない人は出勤できない。うまくいかなければ理由を返す。
    /// 信号扱いは駅ごと（station が要る。同じ駅に何人でもよい。ほかの駅に出勤していれば、その駅に移る）。駅があるかは呼ぶ側で確かめる。
    /// </summary>
    public string? SetRoleAttend(string? id, string? role, bool on, string? station = null)
    {
        lock (_lock)
        {
            if (Find(id) is not { } u) return "その人はいません";
            if (role == null || !RoleNames.Contains(role)) return "その役職はありません";
            if (!_roleAttend.TryGetValue(role, out var att)) _roleAttend[role] = att = new();
            if (!on)
            {
                if (!att.Remove(u.Id)) return null;
                Save();
                return null;
            }

            if (!u.Roles.Contains(role)) return $"{role}の役職がありません";
            var st = (station ?? "").Trim();
            if (role == RelayKeyRole)
            {
                if (st.Length == 0) return "扱う駅を選んでください";
            }
            else st = "";

            if (att.TryGetValue(u.Id, out var cur) && cur.Station == st) return null;
            att[u.Id] = new RoleAttend { Utc = cur?.Utc ?? DateTime.UtcNow, Station = st };
            Save();
            return null;
        }
    }

    /// <summary>役職ごとの出勤者（役職の並び → 出勤した順）。</summary>
    public List<(string Role, List<(string Id, string Name, DateTime Utc, string Station)> People)> RoleAttendees()
    {
        lock (_lock)
            return RoleNames.Select(r => (r, _roleAttend.TryGetValue(r, out var att)
                ? att.OrderBy(x => x.Value.Utc).Select(x => (x.Key, Find(x.Key)?.Name ?? "", x.Value.Utc, x.Value.Station)).ToList()
                : new List<(string, string, DateTime, string)>())).ToList();
    }

    /// <summary>行路の割り当てと臨変をすべて外す（運行番号から行路を作り直したとき）。外した数を返す。出勤はそのまま。</summary>
    public int ClearAssignments()
    {
        lock (_lock)
        {
            var n = _assign.Count + _extras.Count;
            _assign.Clear();
            _auto.Clear();
            _extras.Clear();
            Save();
            return n;
        }
    }

    /// <summary>行路番号を変えたとき（勤怠管理の「行路を組む」）: 割り当て・臨変・前回の行路を新しい行路番号へ移す。</summary>
    public void RenameDuty(string from, string to)
    {
        lock (_lock)
        {
            if (_assign.Remove(from, out var user)) _assign[to] = user;
            if (_auto.Remove(from)) _auto.Add(to);
            foreach (var x in _extras.Where(x => x.Duty == from)) x.Duty = to;
            foreach (var k in _lastDuty.Where(x => x.Value == from).Select(x => x.Key).ToList()) _lastDuty[k] = to;
            Save();
        }
    }

    /// <summary>
    /// 運転している列車の列番が、自分の行路にないものに変わったとき（赤山町の乗り継ぎ・差し替えなど）: その列車の行路に切り替える。
    /// もとの行路（臨変も）は外す。切り替え先をほかの人が持っていれば臨変で持つ（重なってもよい）。切り替えた行路番号を返す（何もしなければ null）。
    /// </summary>
    public string? FollowTrain(string id, string train, DutyRoster roster)
    {
        var key = RailData.NormalizeTrain(train);
        if (key.Length == 0 || roster.DutyOfTrain(key) is not { } duty) return null;
        lock (_lock)
        {
            if (Find(id) is not { } u) return null;
            var mine = DutiesOfLocked(u.Id).Select(roster.DutyById).OfType<Duty>().ToList();
            if (mine.Any(d => d.Items.Any(x => x.Kind != "休憩" && x.Train == key))) return null;
            foreach (var d in _assign.Where(x => x.Value == u.Id).Select(x => x.Key).ToList()) { _assign.Remove(d); _auto.Remove(d); }
            _extras.RemoveAll(x => x.User == u.Id);
            if (_assign.TryGetValue(duty.Id, out var other) && other != u.Id)
                _extras.Add(new ExtraAssign { Duty = duty.Id, User = u.Id, Utc = DateTime.UtcNow });
            else
            {
                _assign[duty.Id] = u.Id;
                _auto.Add(duty.Id);
            }

            _lastDuty[u.Id] = duty.Id;
            Save();
            return duty.Id;
        }
    }

    /// <summary>行路番号から、その行路の所属（道・駒）。どちらも入っていなければ null。</summary>
    private static string? DepotOfDuty(string duty) => Depots.Select(d => d[..1]).FirstOrDefault(duty.Contains);

    /// <summary>いま誰か（担当・臨変）が持っている行路の列車（自動割り当てで、同じ列車を2人に持たせないため）。</summary>
    private HashSet<string> TakenTrains(DutyRoster roster) =>
        _assign.Keys.Concat(_extras.Select(x => x.Duty)).Select(roster.DutyById).OfType<Duty>()
            .SelectMany(d => d.Items.Where(x => x.Kind != "休憩").Select(x => x.Train)).ToHashSet();

    /// <summary>
    /// u に空いている行路を1つランダムに割り当てる（ロックの中で呼ぶ）。選べなければ null。
    /// ・誰も持っていない行路で、ほかの人が持っている行路と列車が重ならないもの
    /// ・所属（道列 → 「道」の入った行路）が合うものから。所属の字がない行路（試1 など）は、合うものがないときだけ
    /// ・前回と同じ行路は、ほかに選べるものがないときだけ
    /// </summary>
    private string? PickFor(CrewUser u, DutyRoster roster)
    {
        var taken = TakenTrains(roster);
        var free = roster.Duties.Where(d => !_assign.ContainsKey(d.Id) && !_extras.Any(x => x.Duty == d.Id)
                                            && !d.Items.Any(x => x.Kind != "休憩" && taken.Contains(x.Train))).ToList();
        var mine = u.Depot.Length > 0 ? u.Depot[..1] : "";
        var tiers = new[]
        {
            free.Where(d => DepotOfDuty(d.Id) == mine).ToList(),
            free.Where(d => DepotOfDuty(d.Id) == null).ToList()
        };
        var last = _lastDuty.GetValueOrDefault(u.Id);
        foreach (var tier in tiers)
        {
            if (tier.Count == 0) continue;
            var pool = tier.Where(d => d.Id != last).ToList();
            if (pool.Count == 0) pool = tier;
            var pick = pool[RandomNumberGenerator.GetInt32(pool.Count)].Id;
            _assign[pick] = u.Id;
            _auto.Add(pick);
            _lastDuty[u.Id] = pick;
            return pick;
        }

        return null;
    }

    public sealed record AttendResult(string Id, List<string> Duties, string? Assigned, string Message);

    /// <summary>
    /// 出勤（タブレットの［出勤］）。出勤を付け、まだ行路がなければ空いている行路をランダムに1つ割り当てる（所属を見る）。
    /// </summary>
    public (AttendResult? Ok, string? Error) Attend(string id, DutyRoster roster)
    {
        lock (_lock)
        {
            if (Find(id) is not { } u) return (null, "ユーザーがいません");
            _attend.TryAdd(u.Id, DateTime.UtcNow);
            string? picked = null;
            var has = HasDuty(u.Id, roster);
            if (!has) picked = PickFor(u, roster);
            Save();
            var duties = DutiesOfLocked(u.Id);
            var msg = picked != null ? $"出勤しました。行路 {picked} が割り当てられました"
                : has ? $"出勤しました（行路 {string.Join("・", duties)}）"
                : "出勤しました。空いている行路がないため、行路はまだ割り当てられていません（勤怠管理の担当者に頼むか、列番を入れて開いてください）";
            return (new AttendResult(u.Id, duties, picked, msg), null);
        }
    }

    /// <summary>
    /// 列番を入れて開いたとき: まだ行路がなければ、その列車を受け持つ行路を割り当てる（出勤も付ける）。
    /// 空いている行路（ほかの人と列車が重ならないもの。所属が合うものから）があればそれ。なければ、その列車の最初の行路を臨変で持つ。
    /// すでに行路があれば何もしない（Assigned は null）。
    /// </summary>
    public (AttendResult? Ok, string? Error) TakeTrain(string id, string train, DutyRoster roster)
    {
        var key = RailData.NormalizeTrain(train);
        var candidates = roster.Duties.Where(d => d.Items.Any(x => x.Kind != "休憩" && x.Train == key)).ToList();
        lock (_lock)
        {
            if (Find(id) is not { } u) return (null, "ユーザーがいません");
            var has = HasDuty(u.Id, roster);
            if (has || candidates.Count == 0)
            {
                return (new AttendResult(u.Id, DutiesOfLocked(u.Id), null,
                    has ? "" : $"{key} を受け持つ行路は、行路表にありません"), null);
            }

            _attend.TryAdd(u.Id, DateTime.UtcNow);
            var taken = TakenTrains(roster);
            var mine = u.Depot.Length > 0 ? u.Depot[..1] : "";
            var free = candidates.Where(d => !_assign.ContainsKey(d.Id) && !_extras.Any(x => x.Duty == d.Id)
                                             && !d.Items.Any(x => x.Kind != "休憩" && taken.Contains(x.Train)))
                .OrderBy(d => DepotOfDuty(d.Id) == mine ? 0 : DepotOfDuty(d.Id) == null ? 1 : 2).ToList();
            string msg, picked;
            if (free.Count > 0)
            {
                picked = free[0].Id;
                _assign[picked] = u.Id;
                _auto.Add(picked);
                _lastDuty[u.Id] = picked;
                msg = $"{key} の行路 {picked} が割り当てられました";
            }
            else
            {
                // もうほかの人が持っている（列車が重なる）: あとから来たこの人を臨変にする
                // （その列車の行路のうち、人が持っている行路を重ねて持つ。所属が合うものから）
                picked = candidates.OrderBy(d => _assign.ContainsKey(d.Id) ? 0 : 1)
                    .ThenBy(d => DepotOfDuty(d.Id) == mine ? 0 : DepotOfDuty(d.Id) == null ? 1 : 2).First().Id;
                _extras.Add(new ExtraAssign { Duty = picked, User = u.Id, Utc = DateTime.UtcNow });
                msg = $"{key} の行路 {picked} はほかの人が持っているため、臨変として割り当てました";
            }

            Save();
            return (new AttendResult(u.Id, DutiesOfLocked(u.Id), picked, msg), null);
        }
    }

    /// <summary>勤怠管理の［出勤者に自動で割り当てる］: 出勤していて、まだ行路がない人に、出勤した順に1つずつ割り当てる。</summary>
    public List<(string User, string? Duty)> AutoAssignAttended(DutyRoster roster)
    {
        lock (_lock)
        {
            var result = new List<(string, string?)>();
            foreach (var (uid, _) in _attend.OrderBy(x => x.Value))
            {
                if (Find(uid) is not { } u) continue;
                if (HasDuty(u.Id, roster)) continue;
                result.Add((u.Id, PickFor(u, roster)));
            }

            if (result.Count > 0) Save();
            return result;
        }
    }

    /// <summary>いまの行路表にある行路を持っているか（担当か臨変。ロックの中で呼ぶ）。</summary>
    private bool HasDuty(string id, DutyRoster roster) =>
        _assign.Any(x => x.Value == id && roster.DutyById(x.Key) != null) || _extras.Any(x => x.User == id && roster.DutyById(x.Duty) != null);

    private List<string> DutiesOfLocked(string id) =>
        _assign.Where(x => x.Value == id).Select(x => x.Key).OrderBy(x => x, DutyRoster.IdOrder)
            .Concat(_extras.Where(x => x.User == id).Select(x => x.Duty)).Distinct().ToList();

    /// <summary>タブレットが読みに来た（ログイン中の目安）。</summary>
    public void Seen(string id, string train)
    {
        lock (_lock) _seen[id] = (DateTime.UtcNow, train);
    }

    /// <summary>今タブレットでログインしているか（最後に読みに来てから20秒以内）と、見ている列車。</summary>
    public (bool Online, string? Train, DateTime? LastUtc) Presence(string id)
    {
        lock (_lock)
        {
            if (!_seen.TryGetValue(id, out var s)) return (false, null, null);
            return (DateTime.UtcNow - s.Utc < OnlineWindow, s.Train, s.Utc);
        }
    }
}
