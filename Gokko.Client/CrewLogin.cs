using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gokko.Client;

/// <summary>ログインしている乗務員（社員番号と、サーバーから受け取った合い札）。</summary>
public sealed record CrewSession(string Id, string Token);

/// 乗務員のログイン（Discord・移行のあいだは社員番号とパスワードも。乗務タブレットと同じサーバーの API）。
/// ログインすると合い札をもらう。合い札は参加（Join）と乗務タブレットに渡す。
/// 社員番号とパスワードでログインし、合い札をもらう。合い札は参加（Join）と乗務タブレットに渡す。
/// </summary>
internal static class CrewApi
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // /api/ で始まるパスはそのまま（Discord ログインの /api/auth/discord など）。ほかは乗務タブレットの /api/tid の下
    private static string Url(string path) => ServerAddress.Current.TrimEnd('/') + (path.StartsWith("/api/") ? "" : "/api/tid") + path;

    /// <summary>送って、返事の JSON を返す。断られたらサーバーの理由を、つながらなければその旨を例外で投げる。</summary>
    public static async Task<JsonNode> SendAsync(HttpMethod method, string path, object? body = null, string? token = null)
    {
        using var req = new HttpRequestMessage(method, Url(path));
        if (body != null) req.Content = JsonContent.Create(body);
        if (!string.IsNullOrEmpty(token)) req.Headers.Add("X-Crew-Token", token);
        HttpResponseMessage res;
        try
        {
            res = await Http.SendAsync(req);
        }
        catch
        {
            // 例外の文面には接続先のアドレスが入ることがあるので、画面には出さない
            throw new InvalidOperationException("サーバーに接続できません");
        }

        using (res)
        {
            JsonNode? json = null;
            try
            {
                json = JsonNode.Parse(await res.Content.ReadAsStringAsync());
            }
            catch (JsonException)
            {
            }

            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException(json?["message"]?.GetValue<string>() ?? $"うまくいきませんでした（{(int)res.StatusCode}）");
            return json ?? new JsonObject();
        }
    }

    /// <summary>乗務タブレットの URL（ログインの合い札を # の後ろに付ける。# の後ろはサーバーに送られず、開いた画面がすぐ消す）。</summary>
    public static string TabletUrl(string? train, CrewSession? crew)
    {
        var url = ServerAddress.Current.TrimEnd('/') + "/tablet.html" + (string.IsNullOrEmpty(train) ? "" : "?train=" + Uri.EscapeDataString(train));
        if (crew != null) url += $"#crew={Uri.EscapeDataString(crew.Id)}&token={Uri.EscapeDataString(crew.Token)}";
        return url;
    }

    public static void OpenBrowser(string url)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }
}

/// <summary>小さな入力画面の共通部分（縦に並べるだけ）。</summary>
internal abstract class CrewDialogBase : Form
{
    protected readonly TableLayoutPanel Layout = new() { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14), AutoSize = true };
    protected readonly Label Message = new() { ForeColor = Color.Firebrick, AutoSize = true, MaximumSize = new Size(400, 0) };

    protected CrewDialogBase(string title)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Yu Gothic UI", 10f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        Controls.Add(Layout);
    }

    protected void Row(string label, Control c)
    {
        c.Dock = DockStyle.Fill;
        Layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
        Layout.Controls.Add(c);
    }

    protected void Wide(Control c)
    {
        Layout.Controls.Add(c);
        Layout.SetColumnSpan(c, 2);
    }

    protected static Label Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(400, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 6, 0, 2) };

    protected static FlowLayoutPanel Buttons(params Control[] items)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        p.Controls.AddRange(items);
        return p;
    }

    /// <summary>ボタンを押したときの処理（その間はボタンを押せなくし、失敗したら理由を出す）。</summary>
    protected async Task Run(Button button, Func<Task> work)
    {
        Message.Text = "";
        button.Enabled = false;
        UseWaitCursor = true;
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Message.Text = ex.Message;
        }
        finally
        {
            UseWaitCursor = false;
            if (!IsDisposed) button.Enabled = true;
        }
    }
}

/// <summary>
/// ブラウザで Discord に入る（PC のクライアント用）。サーバーに始めてもらい、返ってきた URL をブラウザで開く。
/// 結果はサーバーが預かるので、poll の番号で2秒ごとに取りに行く（ループバックで受けない。紐づけの画面にいるあいだも待つ。30分で切れる）。
/// </summary>
internal static class DiscordLogin
{
    /// <param name="token">紐づけ（mode "link"）のとき、ログインしている本人の合い札。</param>
    public static async Task<JsonNode> RunAsync(string mode, string? token, Action<string> status, CancellationToken cancel)
    {
        var begin = await CrewApi.SendAsync(HttpMethod.Post, "/api/auth/discord/begin", new { mode, poll = true }, token);
        var url = begin["url"]?.GetValue<string>() ?? throw new InvalidOperationException("Discord のページを開けませんでした");
        var poll = begin["poll"]?.GetValue<string>() ?? throw new InvalidOperationException("Discord のページを開けませんでした");
        CrewApi.OpenBrowser(url);
        status("ブラウザで Discord に許可してください。まだ社員番号と紐づいていなければ、ブラウザの画面で紐づけてください…");
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(30);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(2000, cancel);
            var r = await CrewApi.SendAsync(HttpMethod.Post, "/api/auth/discord/poll", new { code = poll });
            var s = r["status"]?.GetValue<string>();
            if (s == "wait") continue;
            if (s is "ok" or "linked") return r;
            throw new InvalidOperationException(r["message"]?.GetValue<string>() ?? "Discord でログインできませんでした");
        }

        throw new InvalidOperationException("時間が切れました。もう一度やり直してください");
    }
}

/// <summary>
/// 起動したときのログイン。ログインしないとクライアントは使えない。
/// ・本筋は［Discord でログイン］（ブラウザが開く）。はじめての人は、そこから申請する（勤怠管理で承認される）
/// ・ログインを記憶していれば［ID:… で続ける］で入れる（記憶した合い札がまだ使えるときだけ出す）
/// ・移行のあいだは、社員番号とパスワードでも入れる。入ったら Discord との紐づけを勧める
/// </summary>
internal sealed class LoginDialog : CrewDialogBase
{
    private readonly TextBox _id = new() { PlaceholderText = "8桁（例: 10103200）", MaxLength = 12 };
    private readonly TextBox _password = new() { PlaceholderText = "例: k7mq-3xwp", UseSystemPasswordChar = true };
    private readonly CheckBox _remember = new() { Text = "ログインを記憶する", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
    private readonly ClientConfig _config;
    private readonly CancellationTokenSource _cancel = new();

    /// <summary>記憶していたパスワード（ログイン画面に入れておいたもの）。合わなかったときの知らせに使う。</summary>
    private readonly string _savedPassword;

    public CrewSession? Session { get; private set; }

    public LoginDialog(ClientConfig config) : base("乗務員ログイン")
    {
        _config = config;
        _savedPassword = config.LoadCrewPassword();
        _id.Text = config.CrewId;
        _password.Text = _savedPassword;
        _remember.Checked = config.RememberPassword;
        var resume = new Button { Text = "続ける", AutoSize = true, Visible = false };
        var discord = new Button { Text = "Discord でログイン", AutoSize = true, BackColor = Color.FromArgb(0x58, 0x65, 0xf2), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var login = new Button { Text = "ログイン", AutoSize = true };
        var pwNote = Note("移行のあいだは、社員番号とパスワードでも入れます。");
        Wide(Note("Discord でログインしてください（ブラウザが開きます）。まだ紐づけていない人は、そのあとのブラウザの画面で、社員番号とパスワードを入れればその場で紐づけられます（承認は要りません）。はじめての人は、そこから申請できます。"));
        Wide(Buttons(resume, discord));
        Wide(pwNote);
        Row("社員番号", _id);
        Row("パスワード", _password);
        Wide(Buttons(login));
        Row("", _remember);
        Wide(Message);
        AcceptButton = discord;
        FormClosed += (_, _) => _cancel.Cancel();

        // 外したら、記憶していたパスワードと合い札をすぐ消す
        _remember.CheckedChanged += (_, _) =>
        {
            _config.RememberPassword = _remember.Checked;
            if (!_remember.Checked)
            {
                _config.StoreCrewPassword("");
                _config.StoreCrewToken("");
            }

            _config.Save();
        };

        discord.Click += async (_, _) => await Run(discord, async () =>
        {
            var r = await DiscordLogin.RunAsync("login", null, s => Message.Text = s, _cancel.Token);
            Finish(new CrewSession(r["id"]!.GetValue<string>(), r["token"]!.GetValue<string>()), "");
        });

        login.Click += async (_, _) => await Run(login, async () =>
        {
            var password = _password.Text;
            JsonNode j;
            try
            {
                j = await CrewApi.SendAsync(HttpMethod.Post, "/login", new { id = _id.Text.Trim(), password, client = true }); // 合言葉は接続のときに確かめる（それまでは仮の合い札）
            }
            catch (InvalidOperationException ex) when (_savedPassword.Length > 0 && password == _savedPassword && ex.Message.Contains("ちがいます"))
            {
                // 記憶していたパスワードが合わない（出し直したなど）。新しいものを入れれば、記憶も入れ替わる
                throw new InvalidOperationException(ex.Message + "\n記憶していたパスワードが合いません。パスワードを出し直したときは、新しいパスワードを入れてログインしてください（記憶しているパスワードも入れ替わります）。");
            }

            var session = new CrewSession(j["id"]!.GetValue<string>(), j["token"]!.GetValue<string>());
            // Discord と紐づけていなければ勧める（紐づけたら、パスワードは使えなくなる）
            if (j["discordEnabled"]?.GetValue<bool>() == true && j["discordLinked"]?.GetValue<bool>() != true)
            {
                var until = j["linkUntil"] is { } lu ? $"\nパスワードでのログインは {lu.GetValue<DateTimeOffset>().ToLocalTime():M/d H:mm} までです。" : "";
                if (MessageBox.Show(this, $"ID:{session.Id} を Discord と紐づけますか？（ブラウザが開きます）\n紐づけると、次からは［Discord でログイン］だけで入れます。パスワードは使えなくなります。{until}",
                        "Discord と紐づける", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    await DiscordLogin.RunAsync("link", session.Token, s => Message.Text = s, _cancel.Token);
                    MessageBox.Show(this, "Discord と紐づけました。次からは［Discord でログイン］で入れます。", "Discord と紐づける");
                    password = ""; // もう使えないので記憶しない
                }
            }

            Finish(session, password);
        });

        resume.Click += (_, _) =>
        {
            if (resume.Tag is CrewSession s) Finish(s, _savedPassword);
        };

        Shown += async (_, _) =>
        {
            discord.Focus();
            // Discord が使えるか・パスワードでのログインがもう終わったか
            try
            {
                var c = await CrewApi.SendAsync(HttpMethod.Get, "/api/auth/discord");
                var enabled = c["enabled"]?.GetValue<bool>() == true;
                var closed = c["passwordClosed"]?.GetValue<bool>() == true;
                discord.Visible = enabled;
                if (!enabled)
                {
                    AcceptButton = login;
                    pwNote.Text = "社員番号とパスワードでログインしてください。";
                    (_id.Text.Length == 0 ? _id : _password.Text.Length == 0 ? _password : (Control)login).Focus();
                }
                else if (closed)
                {
                    foreach (var x in new Control[] { _id, _password, login }) x.Enabled = false;
                    pwNote.Text = "パスワードでのログインは終わりました。";
                }
                else if (c["passwordUntil"] is { } pu)
                {
                    pwNote.Text = $"社員番号とパスワードでのログインは {pu.GetValue<DateTimeOffset>().ToLocalTime():M/d H:mm} までです（ログインしたら Discord と紐づけてください）。";
                }
            }
            catch
            {
                // サーバーにつながらなければ、押したときに理由を出す
            }

            // 記憶した合い札がまだ使えれば［ID:… で続ける］を出す
            if (_config.LoadCrewToken() is { Length: > 0 } saved)
            {
                try
                {
                    var me = await CrewApi.SendAsync(HttpMethod.Get, "/crew/me", token: saved);
                    var id = me["id"]!.GetValue<string>();
                    resume.Text = $"ID:{id} で続ける";
                    resume.Tag = new CrewSession(id, saved);
                    resume.Visible = true;
                    AcceptButton = resume;
                    resume.Focus();
                }
                catch
                {
                    // 使えない（ログアウトした・紐づけを外されたなど）。新しくログインする
                }
            }
        };
    }

    /// <summary>ログインできた。社員番号と、記憶するなら合い札とパスワード（新しくなっていれば入れ替える）を保存する。</summary>
    private void Finish(CrewSession s, string password)
    {
        _config.CrewId = s.Id;
        _config.RememberPassword = _remember.Checked;
        _config.StoreCrewPassword(_remember.Checked ? password : "");
        _config.StoreCrewToken(_remember.Checked ? s.Token : "");
        _config.Save();
        Session = s;
        DialogResult = DialogResult.OK;
        Close();
    }
}
