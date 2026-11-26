using System.Text.RegularExpressions;
using Gokko.Common;

namespace Gokko.Client;

/// <summary>
/// ユーザークライアントの画面。
/// ・合言葉と列車番号を入れて接続する。接続のモード（特発だけ／在線を共有＝集中モード）を選ぶ
/// ・サーバーと TRAIN CREW の接続状態、ロープレ時刻、現在地を表示する
/// ・「特発」タブ: 自分のいる区間で特発が作動しているときだけ、理由と復帰ボタンを表示する
/// ・「進路」タブ（在線共有のみ）: この先の駅ごとの番線（選べる）と状態、出発要求ボタン
/// 0.5秒ごとに「ゲームの状態をサーバーへ送る → サーバーの指示をゲームへ反映する」を繰り返す。
/// </summary>
public sealed partial class MainForm : Form
{
    private static readonly Color Ok = Color.ForestGreen;
    private static readonly Color Wait = Color.DarkOrange;
    private static readonly Color Ng = Color.Gray;

    private readonly ClientConfig _config;

    /// <summary>起動時に選んだモード（変えるときは［モード選択へ］で、モードを選ぶ画面に戻る）。</summary>
    private readonly ShareMode _mode;

    /// <summary>乗務の役割（起動時に選ぶ）。車掌は在線共有のモードで、同じ列番の運転士の列車に乗る。</summary>
    private readonly CrewRole _role;

    /// <summary>乗務の中継（在線共有のモードだけ）。運転士と車掌の間のノッチ・ドアなど。車掌なら車掌用操作盤を出す（本体には出さない）。</summary>
    private readonly CrewController? _crewPanel;
    private readonly GameLink _game = new();
    private readonly ServerLink _server = new();
    private readonly EmergencyLightSync _lights = new();
    private readonly RouteApplier _routes = new();
    private readonly SignalApplier _signals = new();
    private readonly OtherRouteApplier _otherRoutes = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly Dictionary<string, int> _trackChoices = new();
    /// <summary>「◯番線（打ち切り・入換）」を選んだ駅（駅ID → 番線）。</summary>
    private readonly Dictionary<string, int> _endChoices = new();

    /// <summary>番線を選んだときの列車番号（列車が替わったら、選んだ番線を捨てる）。</summary>
    private string _choicesTrain = "";

    private readonly TextBox _passwordBox = new() { UseSystemPasswordChar = true };
    private readonly TextBox _trainBox = new() { PlaceholderText = "空欄ならシナリオの列番" };
    private readonly Button _confirmButton = new() { Text = "確定" };
    private readonly Button _connectButton = new() { Text = "接続" };
    private readonly Button _gameButton = new() { Text = "ゲーム切断" };
    private readonly Button _tabletButton = new() { Text = "乗務タブレット" };
    private readonly Button _menuButton = new() { Text = "ブラウザメニュー" }; // 乗務メニューを、ふつうのブラウザで開く
    private readonly Button _clockButton = new() { Text = "ロープレ時計" }; // ロープレ時刻の時計を、別の小さな窓で開く・閉じる
    private readonly Button _backButton = new() { Text = "モード選択へ" };

    /// <summary>通信状況（自分と、同じ列番の相手）。悪くなったら記録し、途切れたらお知らせする。</summary>
    private readonly LinkMonitor _link;
    private readonly Button _linkButton = new() { Text = "● 通信状況" };
    private LinkStatusForm? _linkForm;

    /// <summary>［モード選択へ］で閉じたか（閉じたあと、ログインしたまま、モードを選ぶ画面に戻る）。</summary>
    public bool ReturnToStart { get; private set; }

    /// <summary>確定した列車番号（空ならシナリオの列番）。入力欄を書き換えただけでは変わらない。</summary>
    private string _confirmedTrain = "";

    /// <summary>ダイヤにあるか、サーバーに確かめた列車番号（同じ番号は1回だけ確かめる）。</summary>
    private string _checkedTrain = "";
    private bool _checking;
    private readonly Label _modeLabel = new();
    private readonly Label _serverLabel = new();
    private readonly Label _gameLabel = new();
    private readonly Label _infoLabel = new();
    private readonly Label _incidentLabel = new();
    private readonly Button _restoreButton = new() { Text = "復帰" };
    private readonly Label _messageLabel = new();
    private readonly TabControl _tabs = new();
    private readonly TabPage _routeTab = new() { Text = "進路" };
    private readonly DataGridView _planGrid = new();
    private readonly Button _departButton = new() { Text = "出発要求" };

    // 入換で動かす（X線入換・E線入換）: X線に止まっていると「X線入換」、ゲームの中だけで走っている間は「X線停車中」（E線も同じ）。サーバーが出すときだけ見せる
    private readonly Button _shuntButton = new() { Text = "X線入換", Visible = false };

    // 入換で動かしている途中（X線入換を押してから、つなぎ直すまで）だけ出す、取り消しのボタン
    private readonly Button _shuntCancelButton = new() { Text = "取消", Visible = false };

    // 進路タブの下のボタンの段（入換の2行の表示が入るよう、高さを変える）
    private readonly RowStyle _routeBottomRow = new(SizeType.Absolute, 44);

    // 継続乗務: 押している間に運転画面を出ると、サーバーが最後の在線位置を5分間残す（同じ番線から次のシナリオを始めると、その列番で続ける）
    private readonly CheckBox _continueButton = new() { Text = "継続乗務", Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter };
    private readonly CheckBox _allowConductorButton = new() { Text = "車掌乗務を許可", Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Dictionary<string, CrewPromptForm> _requestForms = new(); // 運転士: 車掌の申し込みの窓（申し込みの番号ごと）
    private readonly Label _routeMessage = new();

    private IReadOnlyList<string>? _syncedCrossings;
    private bool _busy;
    private bool _closing;
    private bool _wasGameConnected;
    private string _lastScreen = "";
    private bool _inScenario; // シナリオ中（運転画面・一時停止・読み込み）。ゲームとの接続が切れても、メニューなどを見るまでは true のまま
    private DateTime? _gameLostAt; // ゲームとの接続が切れた時刻（つながっていれば null）
    private bool _lostRestarted; // 切れている間に、見張りで張り直したか
    private DateTime _nextGameWatch = DateTime.MinValue; // 見張りで次につなぎ直してよい時刻
    private ShareMode? _modeApplied; // ゲームに送ったモード（他列車の表示・進路の手動）
    private DateTime _messageUntil = DateTime.MinValue;
    private string _planKey = "";
    private bool _updatingPlan; // 画面の更新で番線を書き換えている間は、ユーザーの選択として扱わない
    private readonly CrewSession _crew; // ログインしている乗務員（社員番号と合い札）

    public MainForm(ClientConfig config, ShareMode mode, CrewSession crew, CrewRole role = CrewRole.Driver)
    {
        _config = config;
        _mode = mode;
        _crew = crew;
        _role = mode == ShareMode.Shared ? role : CrewRole.Driver;
        if (mode == ShareMode.Shared)
        {
            _crewPanel = new CrewController(_role, _game, _config);
            _crewPanel.Message += ShowMessage;
            _crewPanel.RunRecorded += curve => _ = _server.UploadRunCurveAsync(curve);
            _crewPanel.ConductorRequested += r => BeginInvoke(() => ShowConductorRequest(r));
            _crewPanel.RequestCancelled += id => BeginInvoke(() => CancelConductorRequest(id));
            _crewPanel.ApprovalReceived += a => BeginInvoke(() => ShowApproval(a));
        }

        Text = (_role == CrewRole.Conductor ? "特発共有クライアント（車掌）" : _role == CrewRole.Rider ? "特発共有クライアント（添乗）" : mode == ShareMode.Shared ? "特発共有クライアント（在線共有）" : "特発共有クライアント（特発のみ）") + $"  ID:{crew.Id}";
        ClientSize = new Size(560, 640);
        MinimumSize = new Size(480, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Yu Gothic UI", 10f);

        _passwordBox.Text = _config.Password;
        _trainBox.Text = _config.TrainNumber;
        _confirmedTrain = _config.TrainNumber.Trim();

        _connectButton.Click += async (_, _) => await ToggleConnectionAsync();
        _gameButton.Click += async (_, _) => await ToggleGameAsync();
        _tabletButton.Click += (_, _) => OpenTablet();
        _menuButton.Click += (_, _) => OpenMenuInBrowser();
        _clockButton.Click += (_, _) => ToggleClock();
        _link = new LinkMonitor(_role, _crewPanel != null);
        _link.Notice += ShowMessage;
        _linkButton.Click += (_, _) => OpenLinkStatus();
        _backButton.Click += (_, _) => ReturnToModeSelect();
        _confirmButton.Click += async (_, _) => await ConfirmTrainAsync();
        _trainBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await ConfirmTrainAsync();
        };
        _trainBox.TextChanged += (_, _) => UpdateTrainBoxColor();

        _incidentLabel.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        _incidentLabel.TextAlign = ContentAlignment.MiddleLeft;
        _incidentLabel.Dock = DockStyle.Fill;

        _restoreButton.Font = new Font(Font.FontFamily, 22f, FontStyle.Bold);
        _restoreButton.FlatStyle = FlatStyle.Flat;
        _restoreButton.Dock = DockStyle.Fill;
        _restoreButton.Click += async (_, _) => await RestoreAsync();

        _departButton.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);
        _departButton.Click += async (_, _) =>
        {
            _departButton.Enabled = false;
            ShowMessage(await _server.RequestDepartureAsync());
        };

        _shuntButton.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        _shuntButton.Click += async (_, _) =>
        {
            _shuntButton.Enabled = false;
            ShowMessage(await _server.ShuntMoveAsync());
        };

        _shuntCancelButton.Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold);
        _shuntCancelButton.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, "入換をやめますか？\n残した在線は、ふつうの継続乗務の在線に戻ります。", "入換の取消",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            _shuntCancelButton.Enabled = false;
            ShowMessage(await _server.ShuntCancelAsync());
            _shuntCancelButton.Enabled = true;
        };

        Controls.Add(BuildLayout());
        if (_role != CrewRole.Driver) _continueButton.Enabled = false; // 継続乗務は運転士が扱う

        // ゲームからの返事（SetRoute の結果）はサーバーへ知らせる
        _game.ApiMessage += (title, message) =>
        {
            if (title == "SetRouteState" && _server.Mode == ShareMode.Shared) _server.SendRouteResult(message);
        };

        _timer.Tick += OnTick;
        _timer.Start();
        StartProtectionRadio();
        StartTrainRadio();
        StartOnboard();
        _game.Start();
        UpdateView(null);
        if (_config.ClockOpen) Shown += (_, _) => ShowClock(); // 前に開いていたら、ロープレ時計も開く

        // 開発用: 環境変数 GOKKO_AUTOCONNECT=1 なら、起動時にそのまま接続する（GOKKO_PASSWORD。モードは GOKKO_MODE）
        if (Environment.GetEnvironmentVariable("GOKKO_AUTOCONNECT") == "1")
        {
            if (Environment.GetEnvironmentVariable("GOKKO_PASSWORD") is { } pw) _passwordBox.Text = pw;
            Shown += async (_, _) => await ToggleConnectionAsync();
        }
    }

    private Control BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(string label, Control control, float height = 32)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
            control.Dock = DockStyle.Fill;
            layout.Controls.Add(control);
        }

        void AddWide(Control control, SizeType type, float height)
        {
            layout.RowStyles.Add(new RowStyle(type, height));
            control.Dock = DockStyle.Fill;
            layout.Controls.Add(control);
            layout.SetColumnSpan(control, 2);
        }

        AddRow("合言葉", _passwordBox);
        var trainRow = new TableLayoutPanel { ColumnCount = 2, Margin = Padding.Empty };
        trainRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        trainRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        _trainBox.Dock = DockStyle.Fill;
        _confirmButton.Dock = DockStyle.Fill;
        _confirmButton.Margin = new Padding(4, 2, 0, 2);
        trainRow.Controls.Add(_trainBox, 0, 0);
        trainRow.Controls.Add(_confirmButton, 1, 0);
        AddRow("列車番号", trainRow, 34);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _connectButton.Size = new Size(100, 30);
        _gameButton.Size = new Size(100, 30);
        _gameButton.Margin = new Padding(8, 3, 0, 3);
        buttons.Controls.AddRange([_connectButton, _gameButton]);
        // 運転士（在線共有）: ゲームで使っているキー（ブザー・勾配起動など）。押すと、乗っている車掌のゲームでも同じボタンを押す
        if (_crewPanel != null && _role == CrewRole.Driver)
        {
            var driverKeys = new Button { Size = new Size(120, 30), Margin = new Padding(8, 3, 0, 3), Text = "運転士のキー" };
            driverKeys.Click += (_, _) =>
            {
                using var dialog = new DriverKeyDialog(_crewPanel.DriverBuzzerBindings, _crewPanel.DriverKeys);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _crewPanel.SetDriverKeys(dialog.Result);
                ShowMessage("運転士のキーを保存しました（押すと、乗っている車掌のゲームでも同じボタンを押します）");
            };
            buttons.Controls.Add(driverKeys);

            // 運転台の車掌スイッチ（自分のゲームのドア・ブザーを扱う。ワンマンや、運転台側からドアを扱うとき）
            var driverPanel = new Button { Size = new Size(110, 30), Margin = new Padding(8, 3, 0, 3), Text = "車掌スイッチ" };
            driverPanel.Click += (_, _) => _crewPanel.ToggleDriverPanel();
            buttons.Controls.Add(driverPanel);
        }

        AddWide(buttons, SizeType.Absolute, 40);

        // 画面を開くボタンは、接続のボタンとは別の段に（乗務タブレットはクライアントの中の窓、ブラウザメニューはふつうのブラウザ）
        var screens = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _tabletButton.Size = new Size(130, 30);
        _tabletButton.Margin = new Padding(0, 3, 0, 3);
        _menuButton.Size = new Size(130, 30);
        _menuButton.Margin = new Padding(8, 3, 0, 3);
        _clockButton.Size = new Size(130, 30);
        _clockButton.Margin = new Padding(8, 3, 0, 3);
        screens.Controls.AddRange([_tabletButton, _menuButton, _clockButton]);
        AddWide(screens, SizeType.Absolute, 40);

        // 今のモードと、モードを選ぶ画面に戻るボタン（クライアントを起動し直さずに、運転士・車掌・司令などを選び直す）
        var modeRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _modeLabel.AutoSize = true;
        _modeLabel.Margin = new Padding(0, 7, 0, 0);
        _backButton.Size = new Size(110, 30);
        _backButton.Margin = new Padding(10, 3, 0, 3);
        _linkButton.Size = new Size(110, 30);
        _linkButton.Margin = new Padding(8, 3, 0, 3);
        _linkButton.ForeColor = Ng;
        modeRow.Controls.AddRange([_modeLabel, _backButton, _linkButton]);
        AddWide(modeRow, SizeType.Absolute, 38);

        foreach (var l in new[] { _serverLabel, _gameLabel, _infoLabel, _messageLabel })
        {
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.AutoEllipsis = true;
        }

        AddWide(_serverLabel, SizeType.Absolute, 26);
        AddWide(_gameLabel, SizeType.Absolute, 26);
        AddWide(_infoLabel, SizeType.Absolute, 26);

        // 特発タブ
        var incidentPage = new TabPage { Text = "特発", Padding = new Padding(6) };
        var incidentLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        incidentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        incidentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        var incidentBox = new GroupBox { Text = "特発（当該区間に入ると表示）", Padding = new Padding(8), Dock = DockStyle.Fill };
        incidentBox.Controls.Add(_incidentLabel);
        incidentLayout.Controls.Add(incidentBox);
        incidentLayout.Controls.Add(_restoreButton);
        incidentPage.Controls.Add(incidentLayout);
        // 窓が低くて入りきらないときは、タブをスクロールする（特発の表示と［復帰］がつぶれない高さを残す）
        incidentPage.AutoScroll = true;
        incidentPage.AutoScrollMinSize = new Size(0, 230);

        // 進路タブ
        _routeTab.Padding = new Padding(6);
        var routeLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        routeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        routeLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        routeLayout.RowStyles.Add(_routeBottomRow);
        _routeMessage.Dock = DockStyle.Fill;
        _routeMessage.TextAlign = ContentAlignment.MiddleLeft;
        _routeMessage.AutoEllipsis = true;
        routeLayout.Controls.Add(_routeMessage);
        BuildPlanGrid();
        routeLayout.Controls.Add(_planGrid);
        _departButton.Dock = DockStyle.Fill;
        _continueButton.Dock = DockStyle.Fill;
        _continueButton.Font = new Font(Font.FontFamily, 10f, FontStyle.Bold);
        _continueButton.CheckedChanged += (_, _) =>
        {
            _continueButton.BackColor = _continueButton.Checked ? Color.DarkOrange : SystemColors.Control;
            _continueButton.ForeColor = _continueButton.Checked ? Color.White : SystemColors.ControlText;
            ShowMessage(_continueButton.Checked
                ? "継続乗務: このまま運転画面を出ると、在線位置を5分間残します。同じ番線から次のシナリオを始めると、この列番で続けます"
                : "継続乗務を取り消しました");
        };
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        // 行の高さを決めておく（決めていないと、ボタンが欄より高く（100px）なり、真ん中に描く文字が欄の外に出て見えなくなっていた）
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // 運転士: 「車掌乗務を許可」を入れているときだけ、車掌の乗務の申し込みが届く（車掌・特発のみのモードでは出さない）
        _allowConductorButton.Dock = DockStyle.Fill;
        _allowConductorButton.Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold);
        _allowConductorButton.CheckedChanged += (_, _) =>
        {
            _allowConductorButton.BackColor = _allowConductorButton.Checked ? Color.SeaGreen : SystemColors.Control;
            _allowConductorButton.ForeColor = _allowConductorButton.Checked ? Color.White : SystemColors.ControlText;
            _crewPanel?.SetAllowConductor(_allowConductorButton.Checked);
            ShowMessage(_allowConductorButton.Checked
                ? "車掌乗務を許可: 車掌が乗務を申し込むと、乗せてよいか確かめる窓が出ます"
                : "車掌乗務の許可を切りました（新しい申し込みは届きません。乗っている車掌はそのまま）");
        };
        var showAllow = _crewPanel != null && _role == CrewRole.Driver;
        bottom.ColumnCount = showAllow ? 3 : 2;
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, showAllow ? 50 : 72));
        if (showAllow) bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, showAllow ? 24 : 28));
        // 出発要求の欄の右に、入換で動かすボタン（出すときだけ）
        var departCell = new Panel { Dock = DockStyle.Fill, Margin = new Padding(3) };
        _shuntButton.Dock = DockStyle.Right;
        _shuntButton.Width = 130;
        departCell.Controls.Add(_departButton);
        departCell.Controls.Add(_shuntButton);
        _shuntCancelButton.Dock = DockStyle.Right;
        _shuntCancelButton.Width = 64;
        departCell.Controls.Add(_shuntCancelButton); // 後に足したものから右に寄せるので、取消がいちばん右
        bottom.Controls.Add(departCell, 0, 0);
        if (showAllow) bottom.Controls.Add(_allowConductorButton, 1, 0);
        bottom.Controls.Add(_continueButton, showAllow ? 2 : 1, 0);
        routeLayout.Controls.Add(bottom);
        _routeTab.Controls.Add(routeLayout);
        // 窓が低くて入りきらないときは、タブをスクロールする（進路の表と下のボタンがつぶれない高さを残す）。
        // 最小の窓（480×560）でも下のボタン（出発要求・X線入換・継続乗務）が見えるよう、表のほうを縮める
        // （250 にしていたときは、最小の窓でタブの見える高さが 205〜216 しかなく、下のボタンがスクロールしないと見えなかった）
        _routeTab.AutoScroll = true;
        _routeTab.AutoScrollMinSize = new Size(0, 170);

        _tabs.TabPages.Add(incidentPage);
        _tabs.TabPages.Add(_routeTab);
        _tabs.TabPages.Add(BuildTrainRadioTab());
        if (_mode == ShareMode.Shared) _tabs.TabPages.Add(BuildOnboardTab()); // 列車ごとの放送装置（在線共有のときだけ。同じ列番の中で使う）
        AddWide(_tabs, SizeType.Percent, 100);
        AddWide(_messageLabel, SizeType.Absolute, 26);
        return layout;
    }

    private void BuildPlanGrid()
    {
        _planGrid.Dock = DockStyle.Fill;
        _planGrid.AllowUserToAddRows = false;
        _planGrid.AllowUserToDeleteRows = false;
        _planGrid.AllowUserToResizeRows = false;
        _planGrid.RowHeadersVisible = false;
        _planGrid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _planGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _planGrid.EditMode = DataGridViewEditMode.EditOnEnter;
        _planGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Station", HeaderText = "駅", ReadOnly = true, FillWeight = 22 });
        _planGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Stop", HeaderText = "停車", ReadOnly = true, FillWeight = 14 });
        _planGrid.Columns.Add(new DataGridViewComboBoxColumn { Name = "Track", HeaderText = "番線", FillWeight = 18, FlatStyle = FlatStyle.Flat });
        _planGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "決め方", ReadOnly = true, FillWeight = 14 });
        _planGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "状態", ReadOnly = true, FillWeight = 50 });
        _planGrid.DataError += (_, e) => e.ThrowException = false;
        _planGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_planGrid.IsCurrentCellDirty) _planGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        // 番線の欄の上でマウスのホイールを回しても、番線が変わらないようにする
        // （クリックして選択中になった欄は、ホイールで値が変わり、そのまま「選んだ番線」として送られてしまうため）
        _planGrid.EditingControlShowing += (_, e) =>
        {
            if (e.Control is not ComboBox combo) return;
            combo.MouseWheel -= IgnoreWheel;
            combo.MouseWheel += IgnoreWheel;
        };
        _planGrid.CellValueChanged += (_, e) =>
        {
            if (_updatingPlan || e.RowIndex < 0 || _planGrid.Columns[e.ColumnIndex].Name != "Track") return;
            var row = _planGrid.Rows[e.RowIndex];
            if (row.Tag is not string stationId || row.Cells["Track"].Value is not string text) return;
            if (text == "通過")
            {
                _trackChoices.Remove(stationId);
                _endChoices.Remove(stationId);
            }
            else if (Regex.Match(text, @"^(\d+)番線") is { Success: true } m && int.TryParse(m.Groups[1].Value, out var t))
            {
                _trackChoices[stationId] = t;
                // 「◯番線（打ち切り・入換）」: この駅で打ち切って、入りきったら入換（引上げ）を要求できるようにする
                if (text.Contains(EndShuntSuffix)) _endChoices[stationId] = t;
                else _endChoices.Remove(stationId);
            }
        };
    }

    // =====================================================================================
    // 接続・モード
    // =====================================================================================

    private async Task ToggleConnectionAsync()
    {
        _connectButton.Enabled = false;
        try
        {
            if (_server.Status == ServerStatus.Stopped || _server.Status == ServerStatus.Rejected)
            {
                _config.Password = _passwordBox.Text;
                _confirmedTrain = _trainBox.Text.Trim(); // 接続するときは、入力欄の列番で確定する
                _config.TrainNumber = _confirmedTrain;
                _config.Save();
                _checkedTrain = "";
                UpdateTrainBoxColor();
                _routes.Reset();
                _trackChoices.Clear();
                _endChoices.Clear();
                _server.Passenger = _role == CrewRole.Rider && _config.Passenger;
                _server.Start(ServerAddress.Current, _config.Password, _mode, _crew.Token, _role);
                _crewPanel?.Start(ServerAddress.Current, _config.Password, _crew.Token);
            }
            else
            {
                // 運転会から抜けたら、特発・進路・信号を戻す（他列車の非表示と進路の手動は、アプリを閉じるまで続ける）
                _voiceConsole?.StopForDisconnect();
                _extPhone?.StopForDisconnect();
                _onboardAudio?.Suspend();
                await _server.StopAsync();
                foreach (var f in _requestForms.Values.ToList()) f.CloseSilently(); // 抜けたら、届いていた申し込みの窓も閉じる
                _requestForms.Clear();
                if (_crewPanel != null) await _crewPanel.StopAsync();
                await ClearSharedStateAsync();
            }
        }
        finally
        {
            _connectButton.Enabled = true;
        }
    }

    /// <summary>
    /// 在線共有モードの間は、「他列車の非表示」と「進路の手動」を常に入れる
    /// （TRAIN CREW に接続したとき、メニュー・読み込み画面に入ったとき、読み込みが終わって運転画面に入ったときに送る）。
    /// </summary>
    private async Task ApplyGameModesAsync(GameState? game, bool connected)
    {
        // サーバーへの接続を待たない（シナリオを始める前に他列車を消すため）
        var shared = _mode == ShareMode.Shared;
        var screen = connected ? game?.Screen ?? "" : "";
        // メニュー・読み込み画面に入ったときと、そこから運転画面に入ったとき（シナリオの読み込みが終わったとき）に送る。
        // シナリオを読み込むと、ゲームが進路の自動に戻っていることがあるため。
        // 一時停止からの再開では送らない。route_manual を送り直すと、ゲームは設定済みの進路を消すため
        var screenChanged = screen != _lastScreen
                            && (screen is "Menu" or "MainGame_Loading"
                                || screen == "MainGame" && _lastScreen is not ("MainGame_Pause" or ""));
        var reconnected = connected && !_wasGameConnected;
        // シナリオを閉じた（運転画面からメニュー・タイトル・結果画面へ）: ゲームとの接続を張り直し、送った進路・信号の記録を捨てる。
        // ゲーム側に溜まった要求や古い進路の記録で、必要な進路が送られなくなるのを防ぐ（次のシナリオでは、まっさらな状態から始める）
        var closedScenario = _lastScreen is "MainGame" or "MainGame_Pause" && screen is "Menu" or "Title" or "Result";
        if (closedScenario) _game.Reconnect();
        if (closedScenario || reconnected)
        {
            _routes.Reset();
            _signals.Reset();
            _otherRoutes.Reset();
        }
        _wasGameConnected = connected;
        _lastScreen = screen;
        if (!connected)
        {
            _modeApplied = null;
            _onemanApplied = false;
            return;
        }

        try
        {
            // ワンマン運転の強制（共有のモードによらず）。ユーザーの設定漏れがあっても、ゲームをワンマンにする
            if (!_onemanApplied || screenChanged || reconnected)
            {
                await SendOnemanAsync(true);
                _onemanApplied = true;
            }

            if (shared && (_modeApplied != ShareMode.Shared || screenChanged || reconnected))
            {
                await _game.SendCommandAsync("mode_req", ["hide_other"]);
                await _game.SendCommandAsync("mode_req", ["route_manual"]);
                _modeApplied = ShareMode.Shared;
            }
        }
        catch
        {
            // 次の周期で送り直す
        }
    }

    /// <summary>切断したとき: 作動させた特発、自列車の進路、落とした信号を戻す。</summary>
    private async Task ClearSharedStateAsync()
    {
        await _lights.ClearAllAsync(_game);
        if (_mode != ShareMode.Shared) return;
        await _routes.ClearAllAsync(_game, EffectiveTrainNumber(_game.Latest));
        await _signals.ClearAllAsync(_game);
        await _otherRoutes.ClearAllAsync(_game);
    }

    private bool _onemanApplied;

    /// <summary>
    /// ワンマン運転の強制指定（ゲームの API の mode 要求 forceoneman_on / forceoneman_off）。
    /// mode_request と mode_req の両方で送る（どちらか片方が効けばよい）。
    /// </summary>
    private async Task SendOnemanAsync(bool on)
    {
        var arg = on ? "forceoneman_on" : "forceoneman_off";
        await _game.SendCommandAsync("mode_req", [arg]);
        await _game.SendCommandAsync("mode_request", [arg]);
    }

    /// <summary>在線共有モードで変えたゲームの設定を元に戻す（進路・信号・他列車の表示・進路の自動）。</summary>
    private async Task LeaveGameModesAsync()
    {
        await ClearSharedStateAsync();
        if (_modeApplied != ShareMode.Shared) return;
        try
        {
            if (_game.IsConnected)
            {
                await _game.SendCommandAsync("mode_req", ["route_auto"]);
                await _game.SendCommandAsync("mode_req", ["show_other"]);
            }
        }
        catch
        {
            // 接続が切れていれば送れない
        }

        _modeApplied = null;
    }

    /// <summary>
    /// TRAIN CREW との接続を手で切る・つなぎ直す。切るときは、このソフトが変えたゲームの設定（進路・信号・他列車の表示・進路の自動・ワンマン）を戻してから切る。
    /// つなぎ直すと、まっさらな状態から送り直す
    /// </summary>
    private async Task ToggleGameAsync()
    {
        _gameButton.Enabled = false;
        try
        {
            // ボタンの表示（つながっていれば「ゲーム切断」）に合わせる。
            // つながっていないとき（自動でつなぎ直している途中も）は、いちから張り直す
            if (_game.IsConnected)
            {
                await _otherRoutes.ClearAllAsync(_game);
                await LeaveGameModesAsync();
                if (_onemanApplied && _game.IsConnected)
                    try { await SendOnemanAsync(false); } catch { }
                _game.Stop();
                _onemanApplied = false;
                _routes.Reset();
                _signals.Reset();
                _otherRoutes.Reset();
                ShowMessage("TRAIN CREW との接続を切りました（ゲームの設定は元に戻しました）");
            }
            else
            {
                RestartGame();
                ShowMessage("TRAIN CREW につなぎ直します");
            }
        }
        finally
        {
            _gameButton.Enabled = true;
            UpdateGameButton();
        }
    }

    /// <summary>ゲームとの接続を、いちから張り直す（手で切っていても）。送った進路・信号の記録は捨てて、まっさらな状態から送り直す</summary>
    private void RestartGame()
    {
        _routes.Reset();
        _signals.Reset();
        _otherRoutes.Reset();
        _gameLostAt = null;
        _game.Restart();
    }

    /// <summary>「ゲーム切断」ボタンの文字を、実際の接続の状態に合わせる（つながっていれば「ゲーム切断」、そうでなければ「ゲーム接続」）。</summary>
    private void UpdateGameButton()
    {
        if (!_gameButton.Enabled) return; // 切る・つなぐ処理の途中
        _gameButton.Text = _game.IsConnected ? "ゲーム切断" : "ゲーム接続";
    }

    /// <summary>
    /// シナリオ中の見張り: ゲームとの接続が切れたまま（10秒）か、つながっているのにデータが来ない（10秒）なら、自動でつなぎ直す。
    /// 手で「ゲーム切断」を押したあとは、つなぎ直さない。
    /// </summary>
    private void WatchGame(GameState? game, bool connected)
    {
        if (connected && game != null)
        {
            if (game.Screen is "MainGame" or "MainGame_Pause" or "MainGame_Loading") _inScenario = true;
            else if (game.Screen.Length > 0) _inScenario = false; // メニュー・タイトル・結果画面など
        }

        if (connected)
        {
            _gameLostAt = null;
            _lostRestarted = false;
        }
        else _gameLostAt ??= DateTime.UtcNow;

        if (!_inScenario || _game.Stopped || DateTime.UtcNow < _nextGameWatch) return;
        // 切れたままのときは、1回だけ張り直す（そのあとも 3秒おきに自動でつなぎに行く。ゲームを閉じたときに、知らせを出し続けないように）
        var lost = !connected && !_lostRestarted && DateTime.UtcNow - _gameLostAt > TimeSpan.FromSeconds(10);
        // 運転画面では 0.1 秒おきに届く。読み込み中は数秒あくことがあるので、見ない
        var silent = connected && game?.Screen != "MainGame_Loading" && _game.SecondsSinceData > 10;
        if (!lost && !silent) return;

        _nextGameWatch = DateTime.UtcNow.AddSeconds(15); // つなぎ直しの途中で、また張り直さないように
        RestartGame();
        if (lost) _lostRestarted = true;
        ShowMessage(silent
            ? "TRAIN CREW からデータが届かないため、つなぎ直します"
            : "TRAIN CREW との接続が切れていたため、つなぎ直します");
    }

    /// <summary>［ブラウザメニュー］: 乗務メニュー（menu.html）を、ふつうのブラウザで開く（クライアントのログインのまま）。</summary>
    private void OpenMenuInBrowser()
    {
        try
        {
            CrewApi.OpenBrowser(CrewApi.MenuUrl(WebCrew));
            if (WebCrew == null) ShowMessage("サーバーに接続してから押すと、ログインしたまま開きます（今はログインせずに開きました）");
        }
        catch
        {
            ShowMessage("ブラウザを開けませんでした");
        }
    }

    private TabletForm? _tabletForm;
    private RoleplayClockForm? _clockForm;
    private bool _closingClock; // メイン画面を閉じるので時計も閉じる（開いていたことは覚えたまま）

    /// <summary>［ロープレ時計］: 開いていなければ開き、開いていれば閉じる。</summary>
    private void ToggleClock()
    {
        if (_clockForm is { IsDisposed: false })
        {
            _clockForm.Close();
            return;
        }

        ShowClock();
        ShowMessage("ロープレ時計を開きました（つかんで移動・右下の角で大きさ・右クリックで最前面の入・切）");
    }

    private void ShowClock()
    {
        if (_clockForm is { IsDisposed: false }) { _clockForm.BringUp(); return; }
        _clockForm = new RoleplayClockForm(_config, () => _server.HasFreshState, () => _server.State?.GameTime);
        _clockForm.FormClosed += (_, _) =>
        {
            _clockForm = null;
            if (_closingClock) return;
            _config.ClockOpen = false;
            _config.Save();
        };
        _clockForm.Show();
        _config.ClockOpen = true;
        _config.Save();
    }

    // クライアントのログインの合い札は、合言葉を入れてサーバーに接続できるまでは「仮」で、タブレット・メニューでは使えない。
    // 接続できたら本物になるので、それまでに開いた乗務タブレットの窓は、接続できた時にログインした状態で開き直す
    private bool _tokenConfirmed;
    private bool _tabletNeedsLogin;

    /// <summary>タブレット・メニューに渡すログイン（まだ接続していなければ null）。</summary>
    private CrewSession? WebCrew => _tokenConfirmed || _server.IsJoined ? _crew : null;

    /// <summary>接続できたら（合い札が本物になったら）、ログインせずに開いていた乗務タブレットの窓を、ログインした状態で開き直す。</summary>
    private void CheckTabletLogin()
    {
        if (_tokenConfirmed || !_server.IsJoined) return;
        _tokenConfirmed = true;
        if (_tabletNeedsLogin && _tabletForm is { IsDisposed: false })
            _tabletForm.Navigate(CrewApi.TabletUrl(EffectiveTrainNumber(_game.Latest), _crew, remember: true));
        _tabletNeedsLogin = false;
    }

    /// <summary>
    /// 乗務タブレット（サーバーの tablet.html）を、自分の列番を付けて開く。列番がなければ、列車を選ぶ画面を開く。
    /// クライアントの中の窓（WebView2。最前面にできる）で開く。WebView2 が使えなければ、ふつうのブラウザで開く。
    /// </summary>
    private void OpenTablet()
    {
        var train = EffectiveTrainNumber(_game.Latest);
        var url = CrewApi.TabletUrl(train, WebCrew, remember: true); // ログインしたまま開く（窓の中でメニューに行っても入り直さなくてよい）
        if (WebCrew == null)
        {
            _tabletNeedsLogin = true;
            ShowMessage("まだサーバーに接続していないので、ログインせずに開きました（接続できたら、ログインした状態に切り替わります）");
        }
        try
        {
            if (TabletForm.Available)
            {
                if (_tabletForm is { IsDisposed: false })
                {
                    _tabletForm.Open(url);
                }
                else
                {
                    _tabletForm = new TabletForm(_config, url);
                    _tabletForm.FormClosed += (_, _) => _tabletForm = null;
                    _tabletForm.Show();
                }

                return;
            }

            CrewApi.OpenBrowser(url);
            ShowMessage("WebView2 が見つからないので、ふつうのブラウザで開きました（最前面にはできません）");
        }
        catch
        {
            ShowMessage("ブラウザを開けませんでした");
        }
    }

    /// <summary>［モード選択へ］: 今のモードで開いているものを閉じて、モードを選ぶ画面に戻る（ログインはそのまま）。</summary>
    private void ReturnToModeSelect()
    {
        if (_closing) return;
        if (_server.Status is ServerStatus.Connecting or ServerStatus.Connected
            && MessageBox.Show(this,
                "サーバーとの接続を切って、モードを選ぶ画面に戻りますか？\n（作動させた特発・進路・信号は元に戻します。ログインはそのままです）",
                "モード選択へ", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        ReturnToStart = true;
        Close();
    }

    private async Task RestoreAsync()
    {
        _restoreButton.Enabled = false;
        var result = await _server.RestoreAsync();
        ShowMessage(result.Message);
    }

    // =====================================================================================
    // 毎周期の処理
    // =====================================================================================

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_busy || _closing) return;
        _busy = true;
        try
        {
            var game = _game.Latest;
            var gameConnected = _game.IsConnected;
            _game.PlayerTrainNumber = _confirmedTrain;
            var trainNumber = EffectiveTrainNumber(game);
            var shared = _mode == ShareMode.Shared;

            // 列車が替わった・運転画面を出た（シナリオを開き直した）ら、前の列車で選んだ番線は捨てる
            // （残っていると、次の列車の番線がダイヤや所定ではなく、前の選択になってしまう）
            var driving = gameConnected && game?.InGame == true;
            if (trainNumber != _choicesTrain || !driving)
            {
                _choicesTrain = trainNumber;
                _trackChoices.Clear();
                _endChoices.Clear();
            }

            // 使う列番（確定した列番、空ならシナリオの列番）が変わったら、ダイヤにあるか確かめる
            if (shared && _server.IsJoined && trainNumber.Length > 0 && trainNumber != _checkedTrain && !_checking)
                _ = CheckTrainAsync(trainNumber, quietWhenFound: true);

            WatchGame(game, gameConnected);
            await ApplyGameModesAsync(game, gameConnected);

            // ゲームの状態をサーバーへ（応答は待たない）
            if (_server.IsJoined)
            {
                _crewPanel?.SetTrain(trainNumber);
                _ = _server.ReportAsync(CreateReport(game, gameConnected, trainNumber));
            }

            // サーバーの指示をゲームへ
            var state = _server.State;
            if (!ReferenceEquals(_syncedCrossings, _server.Crossings))
            {
                _syncedCrossings = _server.Crossings;
                _lights.SetCrossings(_syncedCrossings);
            }

            await _lights.TickAsync(_game, state == null ? null : new HashSet<string>(state.ActiveCrossings));
            if (shared && _server.IsJoined && _modeApplied == ShareMode.Shared)
            {
                await _routes.TickAsync(_game, state, trainNumber, _otherRoutes.Mirrored);
                await _signals.TickAsync(_game, state);
                await _otherRoutes.TickAsync(_game, state);
            }

            _link.Update(_server, _game, _crewPanel, state);
            UpdateView(state);
            UpdateLinkView();
        }
        finally
        {
            _busy = false;
        }
    }

    // =====================================================================================
    // 通信状況
    // =====================================================================================

    private void OpenLinkStatus()
    {
        if (_linkForm is { IsDisposed: false })
        {
            if (_linkForm.WindowState == FormWindowState.Minimized) _linkForm.WindowState = FormWindowState.Normal;
            _linkForm.Activate();
            return;
        }

        _linkForm = new LinkStatusForm { Owner = this };
        // 本体の右に出す（画面からはみ出すなら左に）
        var area = Screen.FromControl(this).WorkingArea;
        var x = Right + 4 + _linkForm.Width <= area.Right ? Right + 4 : Math.Max(area.Left, Left - 4 - _linkForm.Width);
        _linkForm.Location = new Point(x, Math.Max(area.Top, Top));
        _linkForm.FormClosed += (_, _) => _linkForm = null;
        _linkForm.ShowItems(_link.Items, _link.LogPath);
        _linkForm.Show();
    }

    private void UpdateLinkView()
    {
        var color = LinkStatusForm.ColorOf(_link.Worst);
        if (_linkButton.ForeColor != color) _linkButton.ForeColor = color;
        if (_linkForm is { IsDisposed: false }) _linkForm.ShowItems(_link.Items, _link.LogPath);
    }

    private string EffectiveTrainNumber(GameState? game)
    {
        return _confirmedTrain.Length > 0 ? _confirmedTrain : game?.DiaName ?? "";
    }

    // ---- 車掌の乗務の許可 ----

    /// <summary>運転士: 車掌の乗務の申し込み。「○○さんが乗務します。よろしいですか？」と確かめる。</summary>
    private void ShowConductorRequest(ConductorRequest r)
    {
        if (_closing || _requestForms.ContainsKey(r.RequestId)) return;
        var who = r.Name.Length > 0 ? $"{r.Name}さん" : $"ID:{r.CrewId} さん";
        var form = new CrewPromptForm($"車掌乗務 {r.TrainNumber}", $"{who}が、列番 {r.TrainNumber} に車掌として乗務します。よろしいですか？", yesNo: true);
        _requestForms[r.RequestId] = form;
        form.Answered += accept =>
        {
            _requestForms.Remove(r.RequestId);
            _crewPanel?.AnswerConductor(r.RequestId, accept);
            ShowMessage(accept ? $"{who}の車掌乗務を許可しました" : $"{who}の車掌乗務を断りました");
        };
        form.Show();
        System.Media.SystemSounds.Asterisk.Play();
    }

    /// <summary>運転士: 申し込みが取り消された（車掌が抜けた・列番を変えた）。</summary>
    private void CancelConductorRequest(string requestId)
    {
        if (!_requestForms.Remove(requestId, out var form)) return;
        form.CloseSilently();
        ShowMessage("車掌の乗務の申し込みが取り消されました");
    }

    private ApprovalState? _lastApproval;

    /// <summary>車掌: 乗務の許可の状態。断られたら窓で知らせる。</summary>
    private void ShowApproval(CrewApproval a)
    {
        if (_closing) return;
        ShowMessage(a.Message);
        if (a.State == ApprovalState.Declined && _lastApproval != ApprovalState.Declined)
        {
            new CrewPromptForm("車掌乗務", a.Message, yesNo: false).Show();
            System.Media.SystemSounds.Exclamation.Play();
        }

        _lastApproval = a.State;
    }

    /// <summary>「確定」ボタン: 入力欄の列番を使う列番にする（空ならシナリオの列番）。ダイヤになければ知らせる。</summary>
    private async Task ConfirmTrainAsync()
    {
        _confirmedTrain = _trainBox.Text.Trim();
        _config.TrainNumber = _confirmedTrain;
        _config.Save();
        UpdateTrainBoxColor();
        if (_role == CrewRole.Conductor) _crewPanel?.Reconfirm(); // 断られていたら、また運転士に申し込む

        // ゲームにつながっていなければ、つなぎ直す（手で切っていても。列番を決めたら、すぐ運転に入れるように）
        var reconnecting = !_game.IsConnected;
        if (reconnecting)
        {
            RestartGame();
            UpdateGameButton();
        }

        var note = reconnecting ? "。TRAIN CREW につなぎ直します" : "";
        var train = EffectiveTrainNumber(_game.Latest);
        if (train.Length == 0)
        {
            ShowMessage("列番を確定しました（シナリオの列番を使います。ゲームに接続すると分かります）" + note);
            return;
        }

        if (_mode != ShareMode.Shared || !_server.IsJoined)
        {
            ShowMessage($"列番を {train} に確定しました" + (_mode == ShareMode.Shared ? "（サーバーに接続すると、ダイヤにあるか確かめます）" : "") + note);
            _checkedTrain = "";
            return;
        }

        if (reconnecting) ShowMessage("TRAIN CREW につなぎ直します");
        await CheckTrainAsync(train, quietWhenFound: false);
    }

    /// <summary>ダイヤにある列番か、サーバーに確かめる。なければ「種別進路で構成します」と知らせる。</summary>
    private async Task CheckTrainAsync(string train, bool quietWhenFound)
    {
        _checking = true;
        try
        {
            var result = await _server.CheckTrainAsync(train);
            if (result == null) return; // 送れなければ、次の周期で確かめ直す
            _checkedTrain = train;
            if (result.Found)
            {
                if (!quietWhenFound) ShowMessage(result.Message);
                return;
            }

            ShowMessage(result.Message);
            BeginInvoke(() => MessageBox.Show(this, result.Message, "列車番号", MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>入力欄を書き換えて、まだ確定していなければ色を付ける。</summary>
    private void UpdateTrainBoxColor()
    {
        var pending = _trainBox.Text.Trim() != _confirmedTrain;
        _trainBox.BackColor = pending ? Color.FromArgb(255, 245, 205) : SystemColors.Window;
        _confirmButton.BackColor = pending ? Color.Gold : SystemColors.Control;
    }

    // =====================================================================================
    // 表示
    // =====================================================================================

    /// <summary>
    /// 入換で動かすボタン（X線入換・X線停車中など）と取消ボタンを出す。label が null なら出さない。
    /// 入換の途中（cancel）は、出発要求を隠してボタンを欄いっぱいに広げ、2行の表示（「X線入換中」と次にすること）を出す。押せないときは灰色。
    /// </summary>
    private void ApplyShunt(string? label, bool enabled, bool cancel)
    {
        var busy = label != null && cancel;
        _shuntButton.Visible = label != null;
        _shuntCancelButton.Visible = busy;
        _departButton.Visible = !busy;
        var twoLines = label?.Contains('\n') == true;
        if (label == null)
        {
            _routeBottomRow.Height = 44;
            return;
        }

        var size = Font.Size * (twoLines ? 0.85f : 1.1f);
        if (Math.Abs(_shuntButton.Font.Size - size) > 0.01f) _shuntButton.Font = new Font(Font.FontFamily, size, FontStyle.Bold);
        _routeBottomRow.Height = twoLines ? Math.Max(44, 2 * _shuntButton.Font.Height + 22) : 44; // 2行が入る高さ（高 DPI では文字が大きくなる）
        if (busy) _shuntCancelButton.Width = Math.Max(48, TextRenderer.MeasureText(_shuntCancelButton.Text, _shuntCancelButton.Font).Width + 12);
        _shuntButton.Text = label;
        _shuntButton.Dock = busy ? DockStyle.Fill : DockStyle.Right;
        if (!busy)
        {
            // 文字が入りきる幅にする（高 DPI で文字が大きいと 130 に入らないことがある）。出発要求のぶん（60）は残す
            var width = Math.Max(130, TextRenderer.MeasureText(label, _shuntButton.Font).Width + 20);
            _shuntButton.Width = Math.Min(width, Math.Max(130, (_shuntButton.Parent?.Width ?? width + 60) - 60));
        }

        _shuntButton.Enabled = enabled;
        _shuntButton.BackColor = enabled ? Color.DarkOrange : SystemColors.Control;
        _shuntButton.ForeColor = enabled ? Color.White : SystemColors.GrayText;
    }

    private void UpdateView(ClientState? state)
    {
        CheckTabletLogin();
        var running = _server.Status is ServerStatus.Connecting or ServerStatus.Connected;
        _connectButton.Text = running ? "切断" : "接続";
        _passwordBox.Enabled = !running;
        (_modeLabel.Text, _modeLabel.ForeColor) = _role == CrewRole.Conductor
            ? ("モード: 車掌として乗る（在線共有）", Color.DarkGreen)
            : _role == CrewRole.Rider
            ? (_config.Passenger ? "モード: 添乗（乗客として乗る）" : "モード: 添乗（在線共有）", Color.MediumPurple)
            : _mode == ShareMode.Shared
            ? ("モード: 在線を共有（集中モード）", Color.Firebrick)
            : ("モード: 特発だけを共有", Color.SteelBlue);

        (_serverLabel.Text, _serverLabel.ForeColor) = _server.Status switch
        {
            ServerStatus.Connected when _server.StateAgeMs > 2000 =>
                ($"● サーバー: 接続済み（配信が {_server.StateAgeMs / 1000.0:0.0}秒 届いていません）", Wait),
            ServerStatus.Connected => ($"● サーバー: 接続済み（{state?.PlayerCount ?? 0}人）" + (_server.RttMs >= 0 ? $"　往復 {_server.RttMs} ms" : ""),
                _server.RttMs > 300 ? Wait : Ok),
            ServerStatus.Connecting => ("● サーバー: 接続中…" + (_server.Message.Length > 0 ? $"（{_server.Message}）" : ""), Wait),
            ServerStatus.Rejected => ($"● サーバー: 拒否されました（{_server.Message}）", Color.Firebrick),
            _ => ("● サーバー: 未接続", Ng)
        };

        UpdateGameButton();
        var game = _game.Latest;
        if (_game.Stopped)
            (_gameLabel.Text, _gameLabel.ForeColor) = ("● TRAIN CREW: 切断中（「ゲーム接続」でつなぎ直します）", Ng);
        else if (!_game.IsConnected)
            (_gameLabel.Text, _gameLabel.ForeColor) = ("● TRAIN CREW: 未接続（自動でつなぎ直しています）", Ng);
        else if (_game.SecondsSinceData > 2)
            (_gameLabel.Text, _gameLabel.ForeColor) = ($"● TRAIN CREW: 接続済み（データが {_game.SecondsSinceData:0.0}秒 届いていません）", Wait);
        else if (game?.InGame == true)
            (_gameLabel.Text, _gameLabel.ForeColor) = ($"● TRAIN CREW: 接続済み（運転中 {EffectiveTrainNumber(game)}）", Ok);
        else
            (_gameLabel.Text, _gameLabel.ForeColor) = ("● TRAIN CREW: 接続済み（運転画面の外）", Wait);

        _infoLabel.Text = $"ロープレ時刻 {state?.GameTime ?? "--:--:--"}　現在地 {state?.Position ?? "不明"}"
                          + (_crewPanel?.Summary ?? "")
                          + (state?.ProtectionRadioSending == true ? "　◆防護無線 発報中（F5 で解除）" : "");

        // 特発
        var visible = state?.VisibleIncidents ?? new List<IncidentView>();
        if (visible.Count > 0)
        {
            _incidentLabel.ForeColor = Color.Firebrick;
            _incidentLabel.Text = string.Join("\n", visible.Select(v =>
                $"◆ {v.Crossing}\n　{v.Reason}" + (v.StartedGameTime != null ? $"（{v.StartedGameTime}）" : "")));
        }
        else
        {
            _incidentLabel.ForeColor = SystemColors.GrayText;
            _incidentLabel.Text = "この区間に、作動中の特発はありません";
        }

        var canRestore = visible.Count > 0 && _server.IsJoined;
        _restoreButton.Enabled = canRestore;
        _restoreButton.BackColor = canRestore ? Color.Firebrick : SystemColors.Control;
        _restoreButton.ForeColor = canRestore ? Color.White : SystemColors.GrayText;

        // 進路
        var shared = _server.IsJoined && _server.Mode == ShareMode.Shared;
        _routeTab.Enabled = shared;
        if (!shared)
        {
            _routeMessage.Text = "進路は、在線を共有するモードで接続したときに使えます";
            _planGrid.Rows.Clear();
            _planKey = "";
            _departButton.Enabled = false;
            ApplyShunt(null, false, false);
        }
        else
        {
            // ダイヤ運転中は、列車から進路に触れない（番線はダイヤどおり。選んだ番線も送らない）
            _diaLock = state?.DiaMode == true;
            if (_diaLock) { _trackChoices.Clear(); _endChoices.Clear(); }
            _routeMessage.Text = state?.RouteMessage ?? (_diaLock ? "ダイヤ運転中は、番線・進路はダイヤどおりに自動で扱います（列車からは変えられません）"
                : state?.Plan.Count > 0 ? "番線は、手前の駅を通過するまで選べます" : "この先に、進路を扱う駅はありません");
            UpdatePlan(state?.Plan ?? new List<StationPlanView>());
            // 出発要求は運転士が出す（車掌でも、自動運転の間はサーバーが運転士と同じに数えているので出せる）
            _departButton.Enabled = state?.CanRequestDeparture == true && (_role == CrewRole.Driver || (_role == CrewRole.Conductor && _crewPanel?.AutoDriving == true));
            _departButton.Text = state?.DepartureLabel ?? "出発要求";
            _departButton.BackColor = _departButton.Enabled ? Color.SeaGreen : SystemColors.Control;
            _departButton.ForeColor = _departButton.Enabled ? Color.White : SystemColors.GrayText;
            // 入換で動かすのは運転士が扱う
            ApplyShunt(_role == CrewRole.Driver ? state?.ShuntButtonLabel : null, state?.ShuntButtonEnabled == true, state?.ShuntCancelVisible == true);
        }

        // ダイヤ運転中は継続乗務を常に入（ボタンは押せない）。フリー運転に戻ったら、元の入・切に戻す
        var dia = shared && state?.DiaMode == true;
        _ = SyncGameClockAsync(dia, state?.GameTime);
        if (dia != _diaShown)
        {
            _diaShown = dia;
            if (dia) { _continueBefore = _continueButton.Checked; _continueButton.Checked = true; }
            else _continueButton.Checked = _continueBefore;
            _continueButton.Enabled = !dia && _role == CrewRole.Driver;
        }

        if (DateTime.UtcNow > _messageUntil) _messageLabel.Text = "";
    }

    private bool _diaShown;
    private bool _diaLock; // ダイヤ運転中（番線を選べない）
    private bool _continueBefore;
    private bool _clockOn;            // ゲームの実時間モードをオンにしてある
    private int? _clockSentOffset;    // 最後に送った補正（秒。0〜86399）。null なら次に送り直す

    /// <summary>
    /// ダイヤ運転中は、ゲームの時計をロープレ時刻に合わせる（実時間モード ＋ 時刻の補正 = ロープレ時刻 − このPCの今の時刻。0〜24時間のプラスで送る）。
    /// 補正が、最後に送った値から2秒以上ずれたとき（管理画面で時刻を変えたときなど）・ゲームにつなぎ直したときに送り直す。
    /// ロープレ時刻は秒で切り捨てて届くので、計算した補正は毎回1秒ほどの幅でぶれる。少しのずれで送り直すと、
    /// そのたびにゲームの時計が1秒戻ったり進んだりする（24秒→25秒→24秒…）ので、小さいずれでは送らない。
    /// フリー運転に戻ったら実時間モードを外す。コマンドは mode_request と mode_req の両方で送る
    /// </summary>
    private async Task SyncGameClockAsync(bool dia, string? gameTime)
    {
        if (!_game.IsConnected) { _clockOn = false; _clockSentOffset = null; return; }
        if (!(dia && TimeSpan.TryParse(gameTime, out var roleplay)))
        {
            if (!_clockOn) return;
            _clockOn = false;
            _clockSentOffset = null;
            try
            {
                await _game.SendCommandAsync("mode_req", ["realtimemode_off"]);
                await _game.SendCommandAsync("mode_request", ["realtimemode_off"]);
            }
            catch
            {
                _clockOn = true; // 次の周期で送り直す
            }
            return;
        }

        // 補正はいつもプラス（0〜24時間）で送る。マイナスで送ると、ゲームの時計が日付を戻る向きにまたいで、
        // 「-10時」のような表示になる（例: 実際は0時、ロープレは13時台 → −11時間ではなく +13時間で送る）。
        // 届いた時刻は秒で切り捨てなので、本当の時刻はそこから1秒の間のどこか。真ん中（+0.5秒）で見込む
        var offset = roleplay + TimeSpan.FromSeconds(0.5) - DateTime.Now.TimeOfDay;
        var total = (int)Math.Round(offset.TotalSeconds) % 86400;
        if (total < 0) total += 86400;
        if (_clockOn && _clockSentOffset is { } sent)
        {
            var diff = Math.Abs(total - sent);
            if (Math.Min(diff, 86400 - diff) < 2) return; // 0時をまたいだ差で比べる（23:59:59 と 0:00:00 は1秒差）
        }

        var turnOn = !_clockOn;
        _clockOn = true;
        _clockSentOffset = total;
        try
        {
            if (turnOn)
            {
                await _game.SendCommandAsync("mode_req", ["realtimemode_on"]);
                await _game.SendCommandAsync("mode_request", ["realtimemode_on"]);
            }
            await _game.SendCommandAsync("realtime_offset", [(total / 3600).ToString(), (total / 60 % 60).ToString(), (total % 60).ToString()]);
        }
        catch
        {
            _clockOn = false; // 次の周期で送り直す
            _clockSentOffset = null;
        }
    }

    private static void IgnoreWheel(object? sender, MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs h) h.Handled = true;
    }

    private void UpdatePlan(List<StationPlanView> plan)
    {
        _updatingPlan = true;
        try
        {
            UpdatePlanCore(plan);
        }
        finally
        {
            _updatingPlan = false;
        }
    }

    private void UpdatePlanCore(List<StationPlanView> plan)
    {
        var key = (_diaLock ? "dia|" : "") + string.Join("|", plan.Select(p => $"{p.StationId}:{string.Join(",", p.TrackOptions)}:{string.Join(",", p.EndOptions)}:{string.Join(",", p.TurnbackTracks)}:{p.CanPass}:{p.Locked}:{p.Track == null}"));
        if (key != _planKey)
        {
            _planKey = key;
            _planGrid.Rows.Clear();
            foreach (var p in plan)
            {
                var row = new DataGridViewRow { Tag = p.StationId };
                row.CreateCells(_planGrid);
                var cell = (DataGridViewComboBoxCell)row.Cells[_planGrid.Columns["Track"]!.Index];
                if (p.Track == null || p.CanPass) cell.Items.Add("通過");
                foreach (var t in p.TrackOptions) cell.Items.Add(TrackText(p, t, end: false));
                foreach (var t in p.EndOptions) cell.Items.Add(TrackText(p, t, end: true));
                row.Cells[_planGrid.Columns["Track"]!.Index].ReadOnly = _diaLock || p.Locked || p.TrackOptions.Count == 0
                    || p.Track == null && !p.CanPass; // 通過の駅は選べない（江ノ原信号場のように、通過と入庫を選べる駅だけ選べる）
                _planGrid.Rows.Add(row);
            }

            // 作り直したあとは、どの欄も選択中にしない（選択中の欄が残ると、その欄にサーバーの番線を表示できず、
            // 空のままの欄から意図しない番線が送られることがある）
            _planGrid.CurrentCell = null;
        }

        for (var i = 0; i < plan.Count && i < _planGrid.Rows.Count; i++)
        {
            var p = plan[i];
            var row = _planGrid.Rows[i];
            row.Cells["Station"].Value = p.Name;
            row.Cells["Stop"].Value = p.StopType;
            row.Cells["Source"].Value = p.TrackSource + (p.Locked ? "・確定" : "");
            row.Cells["Status"].Value = p.Status;
            var trackText = p.Track is { } pt ? TrackText(p, pt, p.EndHere) : "通過";
            var trackCell = row.Cells["Track"];
            if (!(_planGrid.CurrentCell == trackCell && _planGrid.IsCurrentCellInEditMode) && !Equals(trackCell.Value, trackText))
                trackCell.Value = trackText;
            row.DefaultCellStyle.BackColor = p.Status.Contains("待") ? Color.FromArgb(255, 245, 225)
                : p.Status.Contains("開通") || p.Status == "進路確定" ? Color.FromArgb(232, 245, 233) : SystemColors.Window;
        }
    }

    private const string EndShuntSuffix = "（打ち切り・入換）";

    /// <summary>番線の欄の文字: 「◯番線」、行き止まりなら「◯番線（打ち切り）」、打ち切って入換するなら「◯番線（打ち切り・入換）」。</summary>
    private static string TrackText(StationPlanView p, int track, bool end) =>
        end ? $"{track}番線{EndShuntSuffix}" : p.TurnbackTracks.Contains(track) ? $"{track}番線（打ち切り）" : $"{track}番線";

    private void ShowMessage(string message)
    {
        _messageLabel.Text = message;
        _messageUntil = DateTime.UtcNow.AddSeconds(5);
    }

    /// <summary>サーバーへ送る自分の状態（毎周期の報告と、防護無線の発報で使う）。</summary>
    private ClientReport CreateReport(GameState? game, bool gameConnected, string trainNumber)
    {
        var shared = _mode == ShareMode.Shared;
        var report = new ClientReport
        {
            TrainNumber = trainNumber,
            TrainCrewConnected = gameConnected,
            InGame = gameConnected && game?.InGame == true,
            TrackCircuits = gameConnected && game != null ? game.TrackCircuits : new List<string>(),
            ContinueDuty = shared && _continueButton.Checked,
            TrainNumberTyped = _confirmedTrain.Length > 0, // 入力欄の列番か（ダイヤ運転中は手入力が要る）
            Passenger = _role == CrewRole.Rider && _config.Passenger,
            AutoDriving = _role == CrewRole.Conductor && _crewPanel?.AutoDriving == true,
            Link = _link.Health
        };
        if (shared && gameConnected && game != null)
        {
            if (game.InGame && _role == CrewRole.Driver)
            {
                report.CarModels = _crewPanel?.CarModels.ToArray() ?? []; // 列車走行位置の編成
                report.AverageOccupancy = _crewPanel?.AverageOccupancyPercent(); // 列車走行位置の乗車率
            }
            report.Stops = game.Stops;
            report.TraveledDistance = game.TraveledDistance;
            report.NextStationDistance = game.NextStationDistance;
            report.Speed = game.Speed;
            report.GameRoutes = (game.Routes ?? new()).Select(r => new RouteKey { Interlocking = r.Interlock, RouteName = r.Route }).ToList();
            report.TrackChoices = new Dictionary<string, int>(_trackChoices);
            report.EndChoices = new Dictionary<string, int>(_endChoices);
        }

        return report;
    }

    // =====================================================================================
    // 防護無線（F5 で発報・解除。受信範囲にいる間は警報音を繰り返し鳴らす）
    // =====================================================================================

    private ProtectionRadioAudio? _radioAudio;
    private readonly System.Windows.Forms.Timer _radioAudioTimer = new() { Interval = 100 };
    private bool _radioToggling;
    private bool _radioHotKeyRegistered;

    private void StartProtectionRadio()
    {
        try { _radioAudio = new ProtectionRadioAudio(); }
        catch { ShowMessage("防護無線の警報音を読み込めませんでした（警報は鳴りません）"); }
        _radioAudioTimer.Tick += (_, _) => UpdateRadioAudio();
        _radioAudioTimer.Start();
        Shown += (_, _) =>
        {
            if (!_radioHotKeyRegistered)
                ShowMessage("防護無線の F5 キーを登録できません。F5 を使う他のアプリ（別の共有クライアントなど）を閉じて起動し直してください");
        };
    }

    // 受信範囲にいて、サーバーとゲームにつながっていて運転画面にいる間だけ鳴らす（配信が5秒止まったら止める）
    private void UpdateRadioAudio() =>
        _radioAudio?.Update(!_closing && _server.IsJoined && _server.HasFreshState && _game.IsConnected
                            && _game.Latest?.InGame == true && _server.State?.ProtectionRadioReceiving == true);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _radioHotKeyRegistered = ProtectionRadioHotKey.Register(Handle);
        _trainRadioHotKeyRegistered = TrainRadioHotKey.Register(Handle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_radioHotKeyRegistered) ProtectionRadioHotKey.Unregister(Handle);
        if (_trainRadioHotKeyRegistered) TrainRadioHotKey.Unregister(Handle);
        _trainRadioHotKeyRegistered = false;
        _radioHotKeyRegistered = false;
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == ProtectionRadioHotKey.Message && m.WParam.ToInt32() == ProtectionRadioHotKey.Id)
        {
            _ = ToggleProtectionRadioAsync();
            return;
        }

        if (m.Msg == TrainRadioHotKey.Message && m.WParam.ToInt32() == TrainRadioHotKey.Id)
        {
            if (_role == CrewRole.Rider) ShowMessage("添乗は列車無線を聞くだけです（司令は呼び出せません）");
            else if (_voiceConsole != null && !_closing) _ = _voiceConsole.CallFromKeyAsync(this);
            return;
        }

        base.WndProc(ref m);
    }

    // =====================================================================================
    // 列車無線（F6 で司令を呼び出す。司令員が応答したら、通話を終えるまでマイクが開く）
    // =====================================================================================

    private TrainRadioConsole? _voiceConsole;
    private ExtensionPhoneForm? _extPhone; // 内線電話（携帯）。駅・司令・ほかの列車とかけ合う（停車中だけ。車掌はいつでも）
    private bool _trainRadioHotKeyRegistered;

    private void StartTrainRadio()
    {
        try
        {
            _voiceConsole = new TrainRadioConsole(_server, _config.Radio, _config.Save,
                () => !_closing && _server.HasFreshState && _game.IsConnected && _game.Latest?.InGame == true && _server.State?.ProtectionRadioReceiving == true,
                dispatcher: false);
        }
        catch (Exception ex)
        {
            ShowMessage("列車無線を使えません: " + ex.Message);
        }

        try
        {
            if (_role != CrewRole.Rider) _extPhone = new ExtensionPhoneForm(_server, _config.Radio, keitai: true); // 添乗には内線電話はない
        }
        catch (Exception ex)
        {
            ShowMessage("内線電話を使えません: " + ex.Message);
        }

        Shown += (_, _) =>
        {
            if (_radioHotKeyRegistered && !_trainRadioHotKeyRegistered)
                ShowMessage("列車無線の F6 キーを登録できません。F6 を使う他のアプリを閉じるか、無線機の［司令呼出］を押してください");
        };
    }

    private TabPage BuildTrainRadioTab()
    {
        var page = new TabPage { Text = "列車無線", Padding = new Padding(12) };
        var open = new Button { Text = "無線機を開く", Dock = DockStyle.Top, Height = 44 };
        open.Click += (_, _) => _voiceConsole?.OpenConsole(this);
        var phone = new Button { Text = "内線電話（携帯）を開く", Dock = DockStyle.Top, Height = 36 };
        phone.Click += (_, _) => _extPhone?.OpenPhone();

        // 列車無線の音量: 話しているとき（無線機のつまみ・［音の設定］と同じ値）と、聞いているだけのとき
        var volumeRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 104, ColumnCount = 3, RowCount = 2, Padding = new Padding(0, 8, 0, 0) };
        volumeRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        volumeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        volumeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        volumeRow.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        volumeRow.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        void VolumeBar(int row, string title, Func<float> get, Action<float> set)
        {
            var label = new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) };
            var bar = new TrackBar
            {
                Minimum = 0, Maximum = 100, TickFrequency = 10, SmallChange = 5, LargeChange = 10, Dock = DockStyle.Fill,
                Value = (int)Math.Round(Math.Clamp(get(), 0, 1) * 100), AccessibleName = "列車無線の" + title
            };
            var shown = new Label { Text = $"{bar.Value}%", AutoSize = true, Anchor = AnchorStyles.Left };
            bar.ValueChanged += (_, _) =>
            {
                shown.Text = $"{bar.Value}%";
                var v = bar.Value / 100f;
                if (Math.Abs(get() - v) < 0.001f) return;
                set(v);
                _config.Save();
            };
            // 無線機のつまみや［音の設定］で変えたら、こちらも合わせる
            _radioAudioTimer.Tick += (_, _) =>
            {
                if (bar.Capture) return;
                var v = (int)Math.Round(Math.Clamp(get(), 0, 1) * 100);
                if (v != bar.Value) bar.Value = v;
            };
            volumeRow.Controls.Add(label, 0, row);
            volumeRow.Controls.Add(bar, 1, row);
            volumeRow.Controls.Add(shown, 2, row);
        }

        VolumeBar(0, "話しているとき", () => _config.Radio.Volume, v => _config.Radio.Volume = v);
        VolumeBar(1, "聞いているだけのとき", () => _config.Radio.ListenVolume, v => _config.Radio.ListenVolume = v);
        // 説明もいちばん下に積む。窓が狭くて入りきらないときは、タブ全体をスクロールする
        var help = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 12, 0, 0),
            Text = "F6：司令を呼び出す（送話キーではありません）。無線機も開きます。\n\n" +
                   "司令員が応答すると、通話を終えるまでマイクが開きます。受話器を戻すか［終了］で終えます。\n\n" +
                   "共通通話は全員に、個別通話は当事者だけに聞こえます。司令からの一斉通告は全員に届きます。\n\n" +
                   "無線機を閉じていても、聞こえる通話・一斉通告・呼出音は流れます。\n\n" +
                   "音量: 「話しているとき」は自分が通話に入っているときの相手の声、「聞いているだけのとき」はほかの人の通話や、応答していない一斉通告の声です。\n\n" +
                   "マイクの感度・エコー除去・無線風の音・人ごとの音量は、無線機の［音の設定］で変えられます。"
        };
        page.AutoScroll = true;
        page.ClientSizeChanged += (_, _) => help.MaximumSize = new Size(Math.Max(100, page.ClientSize.Width - page.Padding.Horizontal - 4), 0);
        page.Controls.Add(help);
        page.Controls.Add(phone);
        page.Controls.Add(volumeRow);
        page.Controls.Add(open);
        return page;
    }

    private async Task ToggleProtectionRadioAsync()
    {
        if (_closing || _radioToggling || !_server.IsJoined || _role == CrewRole.Rider) return; // 添乗は防護無線を扱わない
        _radioToggling = true;
        try
        {
            // 押した時点の位置を一緒に送る。解除は、ゲームから外れていてもできる
            var game = _game.Latest;
            var report = CreateReport(game, _game.IsConnected, EffectiveTrainNumber(game));
            if (!await _server.ToggleProtectionRadioAsync(report))
            {
                System.Media.SystemSounds.Beep.Play();
                ShowMessage("防護無線を発報できませんでした（運転画面で、路線の上にいるときに押してください）");
            }
        }
        finally
        {
            _radioToggling = false;
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closing)
        {
            _backButton.Enabled = _connectButton.Enabled = false;
            _radioAudioTimer.Stop();
            _tabletForm?.Close(); // 乗務タブレットの窓も閉じる（位置と大きさを覚える）
            _closingClock = true;
            _clockForm?.Close(); // ロープレ時計も閉じる（位置と大きさを覚え、開いていたことも覚えたまま）
            _radioAudio?.Update(false);
            _voiceConsole?.Shutdown();
            _extPhone?.Shutdown();
            _onboardTimer.Stop();
            CloseOnboardPopup();
            _onboardAudio?.Dispose();
            foreach (var f in _requestForms.Values.ToList()) f.CloseSilently(); // 届いていた申し込みの窓も閉じる
            _requestForms.Clear();
            _linkForm?.Close();
            _link.Dispose();
            // 作動させた特発・進路・信号・他列車の表示を戻してから閉じる
            e.Cancel = true;
            _closing = true;
            _timer.Stop();
            _config.TrainNumber = _confirmedTrain;
            _config.Save();
            await _server.StopAsync();
            await _otherRoutes.ClearAllAsync(_game);
            if (_crewPanel != null) await _crewPanel.StopAsync();
            _crewPanel?.Shutdown();
            await LeaveGameModesAsync();
            // ワンマンの強制も外す（このソフトを使わずに遊ぶときは、ゲームの設定どおりに）
            if (_onemanApplied && _game.IsConnected)
                try { await SendOnemanAsync(false); } catch { }
            _game.Stop();
            // モードを選ぶ画面に戻ったあとも音や窓が残らないように、ここで捨てる
            _radioAudio?.Dispose();
            _radioAudio = null;
            _voiceConsole?.Dispose();
            _extPhone?.Dispose();
            _timer.Dispose();
            _radioAudioTimer.Dispose();
            Close();
            return;
        }

        base.OnFormClosing(e);
    }
}
