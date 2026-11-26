using System.Drawing.Drawing2D;

namespace Gokko.Client;

/// <summary>
/// 車掌用操作盤（車掌として乗ったときに出る、常に最前面の小さな窓）。操作と絵は ConductorApp にならう。
/// ・非常ブレーキ（押すたびに入・切）
/// ・左右の車掌スイッチ: 押した場所で扱う（上のレバーの所で閉、真ん中で鍵の差し込み・抜き取り、下のレバーの所で開）。鍵は1本だけで、鍵が差さっている側だけ開閉できる
/// ・再開閉スイッチ（左右。押している間）、乗降促進（押している間）、ブザー（押している間だけ鳴る）
/// ・キーでも扱える（窓が前面でなくても。既定: 鍵 3/6、開 D/H、閉 E/Y、再開閉 F/G、非常 2、乗降促進 R、ブザー N）
/// 窓を押しても前面に出ない（ゲームの操作を邪魔しない）。操作はイベントで知らせ、ゲームと運転士への送り出しは <see cref="CrewPanel"/> が行う。
/// </summary>
public sealed class ConductorPanelForm : Form
{
    // ---- 色（運転士・車掌の画面は今風に） ----
    private static readonly Color Back = Color.FromArgb(24, 27, 33);
    private static readonly Color Card = Color.FromArgb(38, 43, 52);
    private static readonly Color CardHover = Color.FromArgb(52, 58, 70);
    private static readonly Color Text1 = Color.FromArgb(236, 239, 244);
    private static readonly Color Text2 = Color.FromArgb(150, 158, 172);
    private static readonly Color Red = Color.FromArgb(214, 48, 49);
    private static readonly Color Amber = Color.FromArgb(240, 170, 40);
    private static readonly Color Green = Color.FromArgb(46, 184, 114);

    private static readonly TimeSpan Cooldown = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// キーの割り当ての既定値。ゲームの既定のキー（WASD 移動・テンキーなど）とぶつからないよう、上の段の数字 2〜0 だけを使う
    /// （2 非常、3〜5 左の鍵・開・閉、6〜8 右の鍵・開・閉、9 ブザー、0 乗降促進）。再開閉は既定ではキーなし（操作盤のボタンで扱う）。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Keys> DefaultKeys = new Dictionary<string, Keys>
    {
        ["LeftKey"] = Keys.D3, ["LeftOpen"] = Keys.D4, ["LeftClose"] = Keys.D5, ["LeftReOpen"] = Keys.None,
        ["RightKey"] = Keys.D6, ["RightOpen"] = Keys.D7, ["RightClose"] = Keys.D8, ["RightReOpen"] = Keys.None,
        ["ToggleEB"] = Keys.D2, ["Joukou"] = Keys.D0, ["Buzzer"] = Keys.D9
    };

    /// <summary>前の既定値（D・E・F など）。保存してある割り当てがこれと同じなら、新しい既定値に置き換える。</summary>
    public static readonly IReadOnlyDictionary<string, Keys> OldDefaultKeys = new Dictionary<string, Keys>
    {
        ["LeftKey"] = Keys.D3, ["LeftOpen"] = Keys.D, ["LeftClose"] = Keys.E, ["LeftReOpen"] = Keys.F,
        ["RightKey"] = Keys.D6, ["RightOpen"] = Keys.H, ["RightClose"] = Keys.Y, ["RightReOpen"] = Keys.G,
        ["ToggleEB"] = Keys.D2, ["Joukou"] = Keys.R, ["Buzzer"] = Keys.N
    };

    /// <summary>キーの割り当ての表示名。</summary>
    public static readonly IReadOnlyDictionary<string, string> KeyLabels = new Dictionary<string, string>
    {
        ["LeftKey"] = "左 鍵の差し込み・抜き取り", ["LeftOpen"] = "左 開", ["LeftClose"] = "左 閉", ["LeftReOpen"] = "左 再開閉",
        ["RightKey"] = "右 鍵の差し込み・抜き取り", ["RightOpen"] = "右 開", ["RightClose"] = "右 閉", ["RightReOpen"] = "右 再開閉",
        ["ToggleEB"] = "非常ブレーキ", ["Joukou"] = "乗降促進", ["Buzzer"] = "ブザー"
    };

    // ---- 操作の知らせ ----

    /// <summary>ドアの操作（DoorOpnLeft・DoorClsLeft・DoorOpnRight・DoorClsRight）。</summary>
    public event Action<string>? DoorAction;

    public event Action<bool>? EmergencyChanged;
    public event Action<bool>? ReOpenChanged;
    public event Action<bool>? JoukouChanged;

    /// <summary>ブザー（押している間だけ true）。</summary>
    public event Action<bool>? BuzzerChanged;

    /// <summary>NFB（3/4閉 door34sw・羽衣橋 doorCutSw）を押した（引数は TrainCrewInput のボタン名と、押したあとの入・切）。</summary>
    public event Action<string, bool>? NfbToggled;

    /// <summary>寄せ方の切り替えが押された。</summary>
    public event Action? CatchUpModeClicked;

    /// <summary>［自動運転］が押された（運転士がいない列車で、自分のゲームを自動で運転する）。</summary>
    public event Action? AutoClicked;

    private readonly NfbSwitch _door34 = new("3/4閉", "閉");
    private readonly NfbSwitch _doorCut = new("羽衣橋", "締切");

    // ---- 状態 ----
    private bool _emergency;
    private bool _leftKey, _rightKey;
    private bool _leftOpen, _rightOpen;
    private bool _leftReOpen, _rightReOpen;
    private bool _joukou;
    private bool _buzzer;
    private DateTime _lastInput = DateTime.MinValue;

    private readonly Dictionary<string, Keys> _keys;
    private readonly GlobalKeyboardHook _hook = new();

    private readonly Label _titleLabel = new();
    private readonly Label _linkLabel = new();
    private readonly FlatButton _ebButton = new("非常ブレーキ");
    private readonly FlatButton _catchUpButton = new("寄せ　タイミング優先");
    private readonly FlatButton _autoButton = new("自動運転　切");
    private readonly CswView _leftCsw = new();
    private readonly CswView _rightCsw = new();
    private readonly Label _statusLabel = new();
    private readonly Label _keysLabel = new();

    /// <summary>true にすると、閉じるボタンで閉じられる（ふだんは乗務が終わるまで閉じない）。</summary>
    public bool AllowClose { get; set; }

    /// <summary>前回の窓の位置と大きさ（画面からはみ出すなら使わない）。</summary>
    public void ApplySavedBounds(Rectangle bounds)
    {
        if (bounds.Width < MinimumSize.Width || bounds.Height < MinimumSize.Height) return;
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(bounds.X, bounds.Y, bounds.Width, 40)))) return;
        Bounds = bounds;
    }

    /// <summary>今の窓の位置と大きさ（最小化中は元の大きさ）。</summary>
    public Rectangle SavedBounds => WindowState == FormWindowState.Normal ? Bounds : base.RestoreBounds;

    /// <summary>キーの割り当てを変えたとき（保存する）。</summary>
    public event Action<Dictionary<string, Keys>>? KeysChanged;

    public ConductorPanelForm(IReadOnlyDictionary<string, Keys>? keys = null)
    {
        _keys = new Dictionary<string, Keys>(DefaultKeys);
        if (keys != null)
            foreach (var kv in keys)
                if (_keys.ContainsKey(kv.Key)) _keys[kv.Key] = kv.Value;

        Text = "列番 ―";
        FormBorderStyle = FormBorderStyle.Sizable; // 縁をつかんで大きさを変えられる（車掌スイッチの絵が大きさに合わせて伸び縮みする）
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Back;
        ForeColor = Text1;
        Font = new Font("Yu Gothic UI", 10f);
        ClientSize = new Size(380, 798);
        MinimumSize = new Size(300, 698);
        DoubleBuffered = true;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(wa.Right - Width - 16, wa.Top + 40);

        Controls.Add(BuildLayout());
        UpdateView();

        _hook.KeyDown += OnKeyDown;
        _hook.KeyUp += OnKeyUp;
    }

    // 窓を押しても前面（アクティブ）にしない。最前面には固定する
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x00000008;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _hook.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
        {
            // 乗務の間は閉じない（最小化はできる）
            e.Cancel = true;
            WindowState = FormWindowState.Minimized;
            return;
        }

        ReleaseHolds();
        _hook.Dispose();
        base.OnFormClosing(e);
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14, 10, 14, 10), BackColor = Back };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        void Row(float h, Control c, int col = 0, int span = 2)
        {
            if (col == 0) root.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            c.Dock = DockStyle.Fill;
            root.Controls.Add(c, col, root.RowStyles.Count - 1);
            if (span > 1) root.SetColumnSpan(c, span);
        }

        _titleLabel.Text = "列番 ―";
        _titleLabel.Font = new Font(Font.FontFamily, 13f, FontStyle.Bold);
        _titleLabel.TextAlign = ContentAlignment.MiddleLeft;
        Row(28, _titleLabel);

        _linkLabel.ForeColor = Text2;
        _linkLabel.TextAlign = ContentAlignment.MiddleLeft;
        _linkLabel.AutoEllipsis = true;
        Row(22, _linkLabel);

        // 寄せ方の切り替え（押すたびに タイミング優先 → 距離優先 → 切）
        _catchUpButton.Font = new Font(Font.FontFamily, 9.5f);
        _catchUpButton.Margin = new Padding(3, 4, 3, 2);
        _catchUpButton.Click += (_, _) => CatchUpModeClicked?.Invoke();
        Row(34, _catchUpButton, 0, 1);

        // 自動運転（運転士がいない列車だけ。押すたびに入・切）
        _autoButton.Font = new Font(Font.FontFamily, 9.5f);
        _autoButton.Margin = new Padding(3, 4, 3, 2);
        _autoButton.ForeColor = Text2;
        _autoButton.Click += (_, _) => AutoClicked?.Invoke();
        Row(34, _autoButton, 1, 1);

        _ebButton.Font = new Font(Font.FontFamily, 14f, FontStyle.Bold);
        _ebButton.Margin = new Padding(3, 8, 3, 8);
        _ebButton.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) ToggleEmergency(); };
        Row(66, _ebButton);

        var left = SideLabel("左");
        var right = SideLabel("右");
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        root.Controls.Add(left, 0, root.RowStyles.Count - 1);
        root.Controls.Add(right, 1, root.RowStyles.Count - 1);

        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 窓を大きくした分は、車掌スイッチの絵に回す
        _leftCsw.Dock = _rightCsw.Dock = DockStyle.Fill;
        root.Controls.Add(_leftCsw, 0, root.RowStyles.Count - 1);
        root.Controls.Add(_rightCsw, 1, root.RowStyles.Count - 1);
        _leftCsw.CloseClicked += () => { if (!CoolingDown()) Door(left: true, open: false); };
        _leftCsw.OpenClicked += () => { if (!CoolingDown()) Door(left: true, open: true); };
        _leftCsw.KeyClicked += () => ToggleKey(left: true);
        _rightCsw.CloseClicked += () => { if (!CoolingDown()) Door(left: false, open: false); };
        _rightCsw.OpenClicked += () => { if (!CoolingDown()) Door(left: false, open: true); };
        _rightCsw.KeyClicked += () => ToggleKey(left: false);

        // 3/4閉（door34sw）と羽衣橋（ドアカット doorCutSw）の NFB。押すたびに入・切
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        _door34.Dock = _doorCut.Dock = DockStyle.Fill;
        root.Controls.Add(_door34, 0, root.RowStyles.Count - 1);
        root.Controls.Add(_doorCut, 1, root.RowStyles.Count - 1);
        _door34.Toggled += () => ToggleNfb(_door34, "door34sw");
        _doorCut.Toggled += () => ToggleNfb(_doorCut, "doorCutSw");

        var reL = new FlatButton("再開閉") { Margin = new Padding(3, 6, 3, 3) };
        var reR = new FlatButton("再開閉") { Margin = new Padding(3, 6, 3, 3) };
        reL.MouseDown += (_, _) => SetReOpen(left: true, on: true);
        reL.MouseUp += (_, _) => SetReOpen(left: true, on: false);
        reR.MouseDown += (_, _) => SetReOpen(left: false, on: true);
        reR.MouseUp += (_, _) => SetReOpen(left: false, on: false);
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        reL.Dock = reR.Dock = DockStyle.Fill;
        root.Controls.Add(reL, 0, root.RowStyles.Count - 1);
        root.Controls.Add(reR, 1, root.RowStyles.Count - 1);

        var joukou = new FlatButton("乗降促進") { Font = new Font(Font.FontFamily, 12f, FontStyle.Bold) };
        joukou.MouseDown += (_, _) => SetJoukou(true);
        joukou.MouseUp += (_, _) => SetJoukou(false);
        Row(52, joukou);

        var buzzer = new FlatButton("ブザー") { Font = new Font(Font.FontFamily, 12f, FontStyle.Bold) };
        buzzer.MouseDown += (_, _) => SetBuzzer(true);
        buzzer.MouseUp += (_, _) => SetBuzzer(false);
        Row(52, buzzer);

        _statusLabel.ForeColor = Text2;
        _statusLabel.TextAlign = ContentAlignment.MiddleCenter;
        Row(28, _statusLabel);

        var footer = new TableLayoutPanel { ColumnCount = 2, Margin = Padding.Empty, BackColor = Back };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        _keysLabel.ForeColor = Text2;
        _keysLabel.Font = new Font(Font.FontFamily, 8.5f);
        _keysLabel.Dock = DockStyle.Fill;
        _keysLabel.TextAlign = ContentAlignment.MiddleLeft;
        var keyButton = new FlatButton("キー設定") { Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 9f), Margin = new Padding(4, 10, 0, 10) };
        keyButton.Click += (_, _) => EditKeys();
        footer.Controls.Add(_keysLabel, 0, 0);
        footer.Controls.Add(keyButton, 1, 0);
        Row(48, footer);

        return root;
    }

    private Label SideLabel(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Text2,
        Font = new Font(Font.FontFamily, 11f, FontStyle.Bold)
    };

    // =====================================================================================
    // 外から（CrewPanel）
    // =====================================================================================

    /// <summary>列番と中継の状態（上の2行）。</summary>
    /// <summary>寄せ方（「タイミング優先」「距離優先」「切」）を出す。</summary>
    public void SetCatchUpMode(string text)
    {
        _catchUpButton.Text = $"寄せ　{text}";
        _catchUpButton.ForeColor = text == "切" ? Text2 : Text1;
        _catchUpButton.Invalidate();
    }

    /// <summary>自動運転の入・切を出す。</summary>
    public void SetAuto(bool on)
    {
        _autoButton.Text = on ? "自動運転　入" : "自動運転　切";
        _autoButton.ForeColor = on ? Color.White : Text2;
        _autoButton.Fill = on ? Color.FromArgb(40, 96, 180) : Card;
        _autoButton.Invalidate();
    }

    public void SetLink(string title, string link, bool ok)
    {
        _titleLabel.Text = title;
        Text = title; // タイトルバー・タスクバーにも列番
        _linkLabel.Text = link;
        _linkLabel.ForeColor = ok ? Green : Amber;
    }

    /// <summary>
    /// ドアの絵をゲームのドアに合わせる（毎周期呼ぶ）。ゲームから読めるのは「全部閉まっている」かどうかだけなので、
    /// 全部閉なら左右とも閉、開いているのにどちらの絵も閉なら、最後に開けた側（なければ駅の開く側 side）を開にする。
    /// 操作盤で扱った直後は、ゲームのドアが動き終わるまで待つ（開は 3 秒、閉は閉まりきるまで 10 秒）。
    /// 待っても変わらなければ（指令が効かなかったら）、ゲームのほうに戻す。
    /// </summary>
    public void SyncDoors(bool allClose, string side)
    {
        if (Environment.TickCount64 - _doorActionMs < (_doorActionOpen ? DoorOpenSettleMs : DoorCloseSettleMs)) return;
        bool left = _leftOpen, right = _rightOpen;
        if (allClose)
        {
            left = right = false;
        }
        else if (!left && !right)
        {
            var openLeft = side switch { "Left" => true, "Right" => false, _ => _lastOpenedLeft };
            if (openLeft) left = true;
            else right = true;
        }

        if (left == _leftOpen && right == _rightOpen) return;
        _leftOpen = left;
        _rightOpen = right;
        UpdateView();
    }

    private const int DoorOpenSettleMs = 3000, DoorCloseSettleMs = 10000;
    private long _doorActionMs = long.MinValue / 2;
    private bool _doorActionOpen, _lastOpenedLeft = true;

    /// <summary>非常ブレーキを（外から）切る（乗務をやめるときなど）。</summary>
    public void ReleaseHolds()
    {
        if (_leftReOpen || _rightReOpen) { _leftReOpen = _rightReOpen = false; ReOpenChanged?.Invoke(false); }
        if (_joukou) { _joukou = false; JoukouChanged?.Invoke(false); }
        if (_buzzer) { _buzzer = false; BuzzerChanged?.Invoke(false); }
    }

    public bool Emergency => _emergency;

    // =====================================================================================
    // 操作
    // =====================================================================================

    private void ToggleNfb(NfbSwitch sw, string action)
    {
        if (CoolingDown()) return;
        sw.On = !sw.On;
        NfbToggled?.Invoke(action, sw.On);
        Status($"{sw.Label} {(sw.On ? "入" : "切")}");
    }

    private bool CoolingDown()
    {
        var now = DateTime.Now;
        if (now - _lastInput < Cooldown) return true;
        _lastInput = now;
        return false;
    }

    private void Status(string text) => _statusLabel.Text = text;

    private void ToggleEmergency()
    {
        if (CoolingDown()) return;
        _emergency = !_emergency;
        EmergencyChanged?.Invoke(_emergency);
        Status(_emergency ? "非常ブレーキ 入" : "非常ブレーキ 切");
        UpdateView();
    }

    private void ToggleKey(bool left)
    {
        if (left)
        {
            _leftKey = !_leftKey;
            if (_leftKey) _rightKey = false;
        }
        else
        {
            _rightKey = !_rightKey;
            if (_rightKey) _leftKey = false;
        }

        var now = left ? _leftKey : _rightKey;
        Status($"{(left ? "左" : "右")} 鍵を{(now ? "差し込みました（扱える）" : "抜きました")}");
        UpdateView();
    }

    private void Door(bool left, bool open)
    {
        if (!(left ? _leftKey : _rightKey))
        {
            Status($"{(left ? "左" : "右")}の車掌スイッチに鍵を差し込んでください（真ん中を押す）");
            return;
        }

        if (left) _leftOpen = open;
        else _rightOpen = open;
        _doorActionMs = Environment.TickCount64;
        _doorActionOpen = open;
        if (open) _lastOpenedLeft = left;
        DoorAction?.Invoke((open ? "DoorOpn" : "DoorCls") + (left ? "Left" : "Right"));
        Status($"{(left ? "左" : "右")} {(open ? "開" : "閉")}");
        UpdateView();
    }

    private void SetReOpen(bool left, bool on)
    {
        var before = _leftReOpen || _rightReOpen;
        if (left) _leftReOpen = on;
        else _rightReOpen = on;
        var after = _leftReOpen || _rightReOpen;
        if (before == after) return;
        ReOpenChanged?.Invoke(after);
        Status(after ? "再開閉スイッチ 入" : "再開閉スイッチ 切");
    }

    private void SetJoukou(bool on)
    {
        if (_joukou == on) return;
        _joukou = on;
        JoukouChanged?.Invoke(on);
        Status(on ? "乗降促進 入" : "乗降促進 切");
    }

    private void SetBuzzer(bool on)
    {
        if (_buzzer == on) return;
        _buzzer = on;
        BuzzerChanged?.Invoke(on);
        Status(on ? "ブザー 入" : "ブザー 切");
    }

    // ---- キー ----

    private bool _editingKeys;

    private void OnKeyDown(Keys key)
    {
        if (_editingKeys) return;
        if (key == _keys["LeftKey"]) ToggleKey(left: true);
        else if (key == _keys["RightKey"]) ToggleKey(left: false);
        else if (key == _keys["LeftOpen"]) { if (!CoolingDown()) Door(left: true, open: true); }
        else if (key == _keys["LeftClose"]) { if (!CoolingDown()) Door(left: true, open: false); }
        else if (key == _keys["RightOpen"]) { if (!CoolingDown()) Door(left: false, open: true); }
        else if (key == _keys["RightClose"]) { if (!CoolingDown()) Door(left: false, open: false); }
        else if (key == _keys["LeftReOpen"]) SetReOpen(left: true, on: true);
        else if (key == _keys["RightReOpen"]) SetReOpen(left: false, on: true);
        else if (key == _keys["ToggleEB"]) ToggleEmergency();
        else if (key == _keys["Joukou"]) SetJoukou(true);
        else if (key == _keys["Buzzer"]) SetBuzzer(true);
    }

    private void OnKeyUp(Keys key)
    {
        if (_editingKeys) return;
        if (key == _keys["LeftReOpen"]) SetReOpen(left: true, on: false);
        else if (key == _keys["RightReOpen"]) SetReOpen(left: false, on: false);
        else if (key == _keys["Joukou"]) SetJoukou(false);
        else if (key == _keys["Buzzer"]) SetBuzzer(false);
    }

    private void EditKeys()
    {
        _editingKeys = true;
        try
        {
            using var dialog = new KeyBindingDialog(_keys);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            foreach (var kv in dialog.Result) _keys[kv.Key] = kv.Value;
            KeysChanged?.Invoke(new Dictionary<string, Keys>(_keys));
            UpdateView();
        }
        finally
        {
            _editingKeys = false;
        }
    }

    private static string KeyText(Keys k) => k switch
    {
        >= Keys.D0 and <= Keys.D9 => ((int)(k - Keys.D0)).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => "テンキー" + (int)(k - Keys.NumPad0),
        Keys.Decimal => "テンキー.",
        Keys.None => "なし",
        _ => k.ToString()
    };

    private void UpdateView()
    {
        _ebButton.Text = _emergency ? "非常ブレーキ　入" : "非常ブレーキ";
        _ebButton.Fill = _emergency ? Red : Card;
        _ebButton.Border = Red;
        _ebButton.ForeColor = _emergency ? Color.White : Color.FromArgb(255, 120, 120);
        _ebButton.Invalidate();

        _leftCsw.Set(_leftOpen, _leftKey);
        _rightCsw.Set(_rightOpen, _rightKey);

        string K(string name) => KeyText(_keys[name]);
        _keysLabel.Text = $"鍵 {K("LeftKey")}/{K("RightKey")}　開 {K("LeftOpen")}/{K("RightOpen")}　" +
                          $"閉 {K("LeftClose")}/{K("RightClose")}　再開閉 {K("LeftReOpen")}/{K("RightReOpen")}\n" +
                          $"非常 {K("ToggleEB")}　乗降促進 {K("Joukou")}　ブザー {K("Buzzer")}　（左/右）";
    }

    /// <summary>見本の画像を作る（開発用）。</summary>
    public void SetPreviewState(bool emergency, bool leftKey, bool leftOpen, string status)
    {
        _emergency = emergency;
        _leftKey = leftKey;
        _leftOpen = leftOpen;
        _doorCut.On = emergency; // 見本: 2枚目は羽衣橋を入にしておく
        Status(status);
        UpdateView();
    }

    // =====================================================================================
    // 部品
    // =====================================================================================

    /// <summary>角の丸い平らなボタン（押している間は明るく）。</summary>
    private sealed class FlatButton : Control
    {
        private bool _hover, _down;
        public Color Fill { get; set; } = Card;
        public Color Border { get; set; } = Color.FromArgb(70, 78, 92);

        public FlatButton(string text)
        {
            Text = text;
            ForeColor = Text1;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Margin = new Padding(3);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(1, 1, Width - 3, Height - 3);
            using var path = RoundRect(r, 10);
            var fill = _down ? ControlPaint.Light(Fill, 0.25f) : _hover && Fill == Card ? CardHover : Fill;
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            using (var p = new Pen(Border, 1.5f)) g.DrawPath(p, path);
            TextRenderer.DrawText(g, Text, Font, r, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// 車掌スイッチの絵（奥から: 開閉のレバー、スイッチ、鍵）。押した場所で操作を知らせる（押した瞬間に）:
    /// 上のレバーの所（絵の上 15%）は閉、真ん中のスイッチの所は鍵、下のレバーの所（絵の下 15%）は開。
    /// </summary>
    private sealed class CswView : Control
    {
        private static readonly Image? ImgClose = Load("close.png");
        private static readonly Image? ImgOpen = Load("open.png");
        private static readonly Image? ImgCsw = Load("csw.png");
        private static readonly Image? ImgKey = Load("key.png");

        /// <summary>絵の上から何割までを「閉」、何割からを「開」とするか（スイッチの板は 95〜525 / 616）。</summary>
        private const float CloseZone = 95f / 616f, OpenZone = 525f / 616f;

        private bool _open, _key;
        private RectangleF _imageRect;

        public event Action? CloseClicked;
        public event Action? KeyClicked;
        public event Action? OpenClicked;

        public CswView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Back;
            Cursor = Cursors.Hand;
            Margin = new Padding(3);
        }

        private static Image? Load(string name)
        {
            try
            {
                using var s = typeof(CswView).Assembly.GetManifestResourceStream("Gokko.Client.Images." + name);
                return s == null ? null : new Bitmap(s);
            }
            catch
            {
                return null;
            }
        }

        public void Set(bool open, bool key)
        {
            if (_open == open && _key == key) return;
            _open = open;
            _key = key;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || _imageRect.Height <= 0) return;
            var y = (e.Y - _imageRect.Top) / _imageRect.Height; // 絵の上端を 0、下端を 1 として
            if (y < CloseZone) CloseClicked?.Invoke();
            else if (y > OpenZone) OpenClicked?.Invoke();
            else KeyClicked?.Invoke();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            // 黒いレバーが見えるように、絵の後ろは明るい板にする（下の1行は案内の文字）
            var board = new Rectangle(2, 2, Width - 5, Height - 5);
            using (var path = RoundRect(board, 12))
            using (var b = new SolidBrush(Color.FromArgb(226, 231, 238)))
                g.FillPath(b, path);

            // 絵を、縦横比を保って板の真ん中に（下に案内の文字の分をあける）。4枚とも同じ大きさの前提
            var size = ImgCsw?.Size ?? new Size(327, 616);
            const int hint = 20;
            var area = new Rectangle(board.X + 6, board.Y + 6, board.Width - 12, board.Height - 12 - hint);
            var scale = Math.Min(area.Width / (float)size.Width, area.Height / (float)size.Height);
            var w = size.Width * scale;
            var h = size.Height * scale;
            var rect = new RectangleF(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
            _imageRect = rect;
            foreach (var img in new[] { _open ? ImgOpen : ImgClose, ImgCsw, _key ? ImgKey : null })
                if (img != null) g.DrawImage(img, rect);

            using var f = new Font("Yu Gothic UI", 8.5f);
            var text = _key ? (_open ? "開　（上を押すと閉）" : "閉　（下を押すと開）") : "真ん中を押すと鍵";
            TextRenderer.DrawText(g, text, f, new Rectangle(board.X, board.Bottom - hint - 2, board.Width, hint),
                _key ? Color.FromArgb(40, 46, 56) : Color.FromArgb(110, 118, 130), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>
    /// NFB（ノーヒューズブレーカー）風のスイッチ。黒い本体の真ん中にレバーがあり、上が入・下が切。
    /// 入のときは、横の表示（「閉」「締切」）を赤く光らせる。押した瞬間に知らせる（入・切は呼んだ側が決める）。
    /// ゲームのスイッチの状態は読めないので、絵は操作盤で押した回数から決めた入・切。
    /// </summary>
    private sealed class NfbSwitch : Control
    {
        private bool _on;

        public string Label { get; }
        public string Lamp { get; }
        public event Action? Toggled;

        public bool On
        {
            get => _on;
            set { _on = value; Invalidate(); }
        }

        public NfbSwitch(string label, string lamp)
        {
            Label = label;
            Lamp = lamp;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Back;
            Cursor = Cursors.Hand;
            Margin = new Padding(3, 6, 3, 0);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) Toggled?.Invoke();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 板（ゲームの配電盤のような薄緑）
            var board = new Rectangle(2, 2, Width - 5, Height - 5);
            using (var path = RoundRect(board, 10))
            using (var b = new SolidBrush(Color.FromArgb(198, 211, 194)))
                g.FillPath(b, path);

            // NFB の本体（黒）。上下にねじ
            var bodyH = board.Height - 14;
            var bodyW = Math.Min(54, (int)(bodyH * 0.62));
            var body = new Rectangle(board.X + 14, board.Y + 7, bodyW, bodyH);
            using (var path = RoundRect(body, 5))
            using (var b = new SolidBrush(Color.FromArgb(28, 30, 33)))
                g.FillPath(b, path);
            using (var screw = new SolidBrush(Color.FromArgb(150, 154, 160)))
            {
                var r = Math.Max(3, bodyW / 8);
                g.FillEllipse(screw, body.X + bodyW / 2 - r, body.Y + 4, r * 2, r * 2);
                g.FillEllipse(screw, body.X + bodyW / 2 - r, body.Bottom - 4 - r * 2, r * 2, r * 2);
            }

            // レバーの窓と、レバー（入なら上、切なら下）
            var win = new Rectangle(body.X + bodyW / 4, body.Y + bodyH / 4, bodyW / 2, bodyH / 2);
            using (var b = new SolidBrush(Color.FromArgb(12, 13, 15))) g.FillRectangle(b, win);
            var lever = new Rectangle(win.X + 2, _on ? win.Y + 2 : win.Y + win.Height / 2, win.Width - 4, win.Height / 2 - 2);
            using (var b = new SolidBrush(_on ? Color.FromArgb(235, 235, 235) : Color.FromArgb(70, 74, 80))) g.FillRectangle(b, lever);

            // 表示（入なら赤く光る）と名札
            var right = new Rectangle(body.Right + 10, board.Y + 8, board.Right - body.Right - 18, board.Height - 16);
            var lamp = new Rectangle(right.X, right.Y + 4, right.Width, right.Height / 2 - 6);
            using (var b = new SolidBrush(_on ? Color.FromArgb(230, 40, 40) : Color.FromArgb(120, 60, 60))) g.FillRectangle(b, lamp);
            using (var f = new Font("Yu Gothic UI", 13f, FontStyle.Bold))
                TextRenderer.DrawText(g, Lamp, f, lamp, _on ? Color.White : Color.FromArgb(160, 110, 110),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            var plate = new Rectangle(right.X, lamp.Bottom + 8, right.Width, 24);
            using (var b = new SolidBrush(Color.FromArgb(34, 38, 46))) g.FillRectangle(b, plate);
            using (var f = new Font("Yu Gothic UI", 10f, FontStyle.Bold))
                TextRenderer.DrawText(g, Label, f, plate, Color.FromArgb(220, 224, 230), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>キーの割り当ての画面（押したいキーを押して決める）。</summary>
    private sealed class KeyBindingDialog : Form
    {
        public Dictionary<string, Keys> Result { get; }
        private string? _target;
        private readonly Dictionary<string, Button> _buttons = new();
        private readonly Label _hint = new();

        public KeyBindingDialog(IReadOnlyDictionary<string, Keys> keys)
        {
            Result = new Dictionary<string, Keys>(keys);
            Text = "キー設定";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            TopMost = true;
            KeyPreview = true;
            BackColor = Back;
            ForeColor = Text1;
            Font = new Font("Yu Gothic UI", 10f);
            ClientSize = new Size(380, 30 * KeyLabels.Count + 110);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var (name, label) in KeyLabels)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
                layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
                var b = new Button { Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, BackColor = Card, ForeColor = Text1 };
                b.Click += (_, _) => { _target = name; _hint.Text = $"「{label}」に割り当てるキーを押してください（Esc でなし）"; Refresh(); };
                _buttons[name] = b;
                layout.Controls.Add(b);
            }

            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            _hint.Dock = DockStyle.Fill;
            _hint.ForeColor = Amber;
            layout.Controls.Add(_hint);
            layout.SetColumnSpan(_hint, 2);

            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            var ok = new Button { Text = "保存", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, BackColor = Green, ForeColor = Color.White, DialogResult = DialogResult.OK };
            var reset = new Button { Text = "既定に戻す", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, BackColor = Card, ForeColor = Text1 };
            reset.Click += (_, _) => { foreach (var kv in DefaultKeys) Result[kv.Key] = kv.Value; ShowKeys(); };
            layout.Controls.Add(reset);
            layout.Controls.Add(ok);
            Controls.Add(layout);
            ShowKeys();
        }

        private void ShowKeys()
        {
            foreach (var (name, b) in _buttons) b.Text = KeyText(Result[name]);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_target == null) return base.ProcessCmdKey(ref msg, keyData);
            var code = keyData & Keys.KeyCode;
            Result[_target] = code == Keys.Escape ? Keys.None : code; // Esc はキーなし
            _target = null;
            _hint.Text = "";
            ShowKeys();
            return true;
        }
    }
}
