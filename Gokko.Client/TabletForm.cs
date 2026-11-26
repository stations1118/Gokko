using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Gokko.Client;

/// <summary>
/// 乗務タブレット（サーバーの tablet.html）を開く、クライアントの中の窓（WebView2）。
/// ・上の帯の「最前面」を入れると、ゲームを操作していても手前に出たままにする（入・切は覚えておく）
/// ・位置と大きさ・拡大率（－ ＋。Ctrl＋ホイールでも）も覚えておき、次に開くときも同じにする
/// ・WebView2 の実行環境（Windows 10/11 の Edge の部品）がなければ、ふつうのブラウザで開く（呼ぶ側で）
/// </summary>
internal sealed class TabletForm : Form
{
    // 運転士の画面なので今風に
    private static readonly Color Bar = Color.FromArgb(0x26, 0x35, 0x4b);
    private static readonly Color Fore = Color.FromArgb(0xe8, 0xee, 0xf6);

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(0x1b, 0x25, 0x33) };
    private readonly CheckBox _topMost = new() { Text = "最前面", AutoSize = true, ForeColor = Fore, Margin = new Padding(8, 5, 4, 0) };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.FromArgb(0x93, 0xa3, 0xb8), Margin = new Padding(12, 6, 0, 0) };
    private readonly Button _zoomLabel = new()
    {
        Text = "100%", AutoSize = false, Size = new Size(52, 24), FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(0xe8, 0xee, 0xf6),
        BackColor = Color.FromArgb(0x26, 0x35, 0x4b), Margin = new Padding(2, 1, 0, 0), Cursor = Cursors.Hand
    };
    private readonly ClientConfig _config;
    private string _url;
    private bool _ready;

    public TabletForm(ClientConfig config, string url)
    {
        _config = config;
        _url = url;
        Text = "乗務タブレット";
        Font = new Font("Yu Gothic UI", 9.5f);
        BackColor = Bar;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(360, 300);
        ShowInTaskbar = true;

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, BackColor = Bar, WrapContents = false, Padding = new Padding(4, 2, 4, 2) };
        var reload = new Button
        {
            Text = "再読み込み", AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Fore, BackColor = Color.FromArgb(0x33, 0x44, 0x5c),
            Margin = new Padding(8, 1, 0, 0), Cursor = Cursors.Hand
        };
        reload.FlatAppearance.BorderSize = 0;
        reload.Click += (_, _) => { if (_ready) _web.CoreWebView2.Reload(); };
        _topMost.Checked = config.TabletTopMost;
        _topMost.CheckedChanged += (_, _) =>
        {
            TopMost = _topMost.Checked;
            _config.TabletTopMost = _topMost.Checked;
            _config.Save();
        };
        // 拡大率（－・＋で10%ずつ。数字を押すと100%に戻す。Ctrl＋ホイールでも変わる）。次に開くときも同じ拡大率
        Button Small(string text, string tip)
        {
            var x = new Button
            {
                Text = text, AutoSize = false, Size = new Size(text.Length > 2 ? 52 : 26, 24), FlatStyle = FlatStyle.Flat, ForeColor = Fore,
                BackColor = Color.FromArgb(0x33, 0x44, 0x5c), Margin = new Padding(text.Length > 2 ? 2 : 6, 1, 0, 0), Cursor = Cursors.Hand
            };
            x.FlatAppearance.BorderSize = 0;
            new ToolTip().SetToolTip(x, tip);
            return x;
        }

        var zoomOut = Small("－", "小さく");
        var zoomIn = Small("＋", "大きく");
        zoomOut.Margin = new Padding(14, 1, 0, 0);
        zoomOut.Click += (_, _) => SetZoom(_web.ZoomFactor - 0.1);
        zoomIn.Click += (_, _) => SetZoom(_web.ZoomFactor + 0.1);
        _zoomLabel.Click += (_, _) => SetZoom(1.0);
        new ToolTip().SetToolTip(_zoomLabel, "押すと100%に戻す");
        _web.ZoomFactorChanged += (_, _) => ShowZoom();
        bar.Controls.AddRange([_topMost, reload, zoomOut, _zoomLabel, zoomIn, _status]);
        Controls.Add(_web);
        Controls.Add(bar);
        TopMost = config.TabletTopMost;

        // 位置と大きさ（なければ画面の右の半分ほど）
        var b = config.TabletBounds.Split(',');
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        var r = b.Length == 4 && b.All(x => int.TryParse(x, out _))
            ? new Rectangle(int.Parse(b[0]), int.Parse(b[1]), int.Parse(b[2]), int.Parse(b[3]))
            : new Rectangle(screen.Right - Math.Min(900, screen.Width / 2), screen.Top, Math.Min(900, screen.Width / 2), screen.Height);
        // 画面の外（外したモニター）なら、見える所へ戻す
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))) r.Location = new Point(screen.Left + 40, screen.Top + 40);
        Bounds = r;

        Shown += async (_, _) => await StartAsync();
        FormClosing += (_, _) => SaveBounds();
    }

    /// <summary>WebView2 を用意する（使えなければ false を返し、呼ぶ側がふつうのブラウザで開く）。</summary>
    public static bool Available
    {
        get
        {
            try
            {
                return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch
            {
                return false;
            }
        }
    }

    private async Task StartAsync()
    {
        try
        {
            // 閲覧データ（タブレットのログインなど）は %LOCALAPPDATA%\GokkoClient\WebView2 に置く（exe の隣に作らない）
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GokkoClient", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, folder);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            // タブレットの中のリンクで別の窓を開こうとしたら、この窓の中で開く
            _web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                _web.CoreWebView2.Navigate(e.Uri);
            };
            _web.CoreWebView2.DocumentTitleChanged += (_, _) => Text = _web.CoreWebView2.DocumentTitle is { Length: > 0 } t ? t : "乗務タブレット";
            _web.CoreWebView2.NavigationCompleted += (_, e) => _status.Text = e.IsSuccess ? "" : "開けませんでした（サーバーにつながっているか確かめてください）";
            _ready = true;
            SetZoom(_config.TabletZoom);
            _web.CoreWebView2.Navigate(_url);
        }
        catch (Exception)
        {
            _status.Text = "WebView2 を使えません。ふつうのブラウザで開いてください";
        }
    }

    /// <summary>もう開いている窓で、別の列番（URL）を開き直す。同じなら手前に出すだけ。</summary>
    public void Open(string url)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        if (url == _url) return;
        _url = url;
        if (_ready) _web.CoreWebView2.Navigate(url);
    }

    /// <summary>手前に出さずに、別の URL を開く（ログインした状態に切り替えるときなど）。まだ用意中なら、用意できたときに開く。</summary>
    public void Navigate(string url)
    {
        _url = url;
        if (_ready) _web.CoreWebView2.Navigate(url);
    }

    /// <summary>拡大率を変える（50%〜200%）。覚えておき、次に開くときも同じにする。</summary>
    private void SetZoom(double zoom)
    {
        zoom = Math.Round(Math.Clamp(zoom, 0.5, 2.0), 2);
        if (!_ready) return;
        if (Math.Abs(_web.ZoomFactor - zoom) > 0.001) _web.ZoomFactor = zoom; // 変わったら ZoomFactorChanged で表示と記憶を合わせる
        ShowZoom();
    }

    private void ShowZoom()
    {
        var z = Math.Round(_web.ZoomFactor, 2);
        _zoomLabel.Text = $"{Math.Round(z * 100)}%";
        if (Math.Abs(_config.TabletZoom - z) < 0.001) return;
        _config.TabletZoom = z;
        _config.Save();
    }

    private void SaveBounds()
    {
        var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _config.TabletBounds = $"{r.X},{r.Y},{r.Width},{r.Height}";
        _config.Save();
    }
}
