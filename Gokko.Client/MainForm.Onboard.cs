using Gokko.Common;

namespace Gokko.Client;

/// <summary>
/// 列車ごとの放送装置のタブ（車内放送・車外放送・インターホン）。在線共有のときだけ出す。
/// ・運転士・車掌: 車内放送か車外放送のどちらかを流せる（列車ごとに同時に1人・1種類）。インターホンは入にしている間、声を出すと自動で送る
/// ・添乗: 聞くだけ。「乗客として乗る」なら、聞こえるのは車内・車外放送だけ
/// </summary>
public sealed partial class MainForm
{
    internal static readonly Color InsideColor = Color.FromArgb(37, 99, 235);
    internal static readonly Color OutsideColor = Color.FromArgb(217, 119, 6);

    /// <summary>押されている（入の）ボタンの色（車内・車外・連絡で共通の水色）と、その上の文字の色。「入」とは書かず、色だけで見せる。</summary>
    internal static readonly Color PressedColor = Color.FromArgb(56, 189, 248);
    internal static readonly Color PressedText = Color.FromArgb(8, 47, 73);

    private OnboardAudio? _onboardAudio;
    private readonly System.Windows.Forms.Timer _onboardTimer = new() { Interval = 100 };
    private readonly Label _onboardStatus = new() { Dock = DockStyle.Top, Height = 46, Font = new Font("Yu Gothic UI", 10.5f, FontStyle.Bold) };
    private readonly Label _onboardMembers = new() { Dock = DockStyle.Top, Height = 40, ForeColor = Color.FromArgb(90, 96, 108) };
    private readonly CheckBox _insideButton = OnboardToggle("車内放送");
    private readonly CheckBox _outsideButton = OnboardToggle("車外放送");
    private readonly CheckBox _intercomButton = OnboardToggle("連絡");
    private readonly CheckBox _passengerBox = new() { Text = OnboardTexts.PassengerCheck, Dock = DockStyle.Top, Height = 32 };
    private readonly ProgressBar _onboardMic = new() { Maximum = 100, Dock = DockStyle.Top, Height = 8, Style = ProgressBarStyle.Continuous };
    private readonly Label _onboardSend = new() { Dock = DockStyle.Top, Height = 26 };
    private readonly CheckBox _onboardMonitorBox = new()
    {
        Text = OnboardTexts.MonitorCheck, Dock = DockStyle.Top, Height = 30
    };

    private readonly Label _onboardModel = new() { Dock = DockStyle.Top, Height = 24, ForeColor = Color.FromArgb(90, 96, 108) };
    private bool _onboardBusy;

    private static CheckBox OnboardToggle(string text) => new()
    {
        Text = text, Appearance = Appearance.Button, AutoCheck = false, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
        TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Yu Gothic UI", 11f, FontStyle.Bold), Margin = new Padding(3)
    };

    private void StartOnboard()
    {
        if (_mode != ShareMode.Shared) return;
        try
        {
            _onboardAudio = new OnboardAudio(_server, _config.Radio);
        }
        catch (Exception ex)
        {
            ShowMessage("放送装置を使えません: " + ex.Message);
        }

        _onboardTimer.Tick += (_, _) => UpdateOnboard();
        _onboardTimer.Start();

        // キー・コントローラーのボタン（運転士・車掌）。ゲームを前面にしたままでも効く
        if (_role is CrewRole.Driver or CrewRole.Conductor)
        {
            _onboardKeyHook = new GlobalKeyboardHook();
            _onboardKeyHook.KeyDown += k => OnOnboardBinding(k.ToString());
            _onboardKeyHook.Start();
            _onboardTimer.Tick += (_, _) =>
            {
                if (_config.OnboardKeys.Values.Any(v => v.Contains("Joy")))
                    foreach (var (binding, down) in _onboardJoystick.Poll())
                        if (down) OnOnboardBinding(binding);
            };
        }
    }

    // ---- キー（車内・車外・連絡。押すたびに入・切） ----

    private GlobalKeyboardHook? _onboardKeyHook;
    private readonly JoystickPoller _onboardJoystick = new();

    private void OnOnboardBinding(string binding)
    {
        if (_closing || Form.ActiveForm is KeyCaptureDialog or OnboardKeyDialog) return; // キーを決めている間は扱わない
        var action = _config.OnboardKeys.FirstOrDefault(kv => CrewController.SplitBindings(kv.Value).Contains(binding)).Key;
        switch (action)
        {
            case "Inside": _ = ToggleBroadcastAsync(OnboardChannel.Inside); break;
            case "Outside": _ = ToggleBroadcastAsync(OnboardChannel.Outside); break;
            case "Intercom": _ = ToggleIntercomAsync(); break;
        }
    }

    private void EditOnboardKeys()
    {
        using var dialog = new OnboardKeyDialog(_config.OnboardKeys);
        if (dialog.ShowDialog((IWin32Window?)_onboardPopup ?? this) != DialogResult.OK) return;
        _config.OnboardKeys = dialog.Result.Where(kv => kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        _config.Save();
        ShowMessage("放送装置のキーを保存しました");
    }

    /// <summary>キーの割り当ての表示（「車内 F9」など。なければ空）。</summary>
    private string OnboardKeyText(string action) =>
        string.Join("／", CrewController.SplitBindings(_config.OnboardKeys.GetValueOrDefault(action)).Select(KeyCaptureDialog.BindingText));

    // ---- 放送装置のポップ（運転士・車掌。車掌用操作盤とは別の、最前面の小さな窓） ----

    private OnboardPopupForm? _onboardPopup;
    private bool _onboardPopupAuto; // 列車に乗ったときに一度だけ自動で出したか

    private void OpenOnboardPopup()
    {
        if (_role is not (CrewRole.Driver or CrewRole.Conductor) || _closing) return;
        if (_onboardPopup == null)
        {
            _onboardPopup = new OnboardPopupForm(_role == CrewRole.Conductor ? "放送装置（車掌）" : "放送装置（運転士）", _config.Radio, _config.Save);
            _onboardPopup.KeysClicked += EditOnboardKeys;
            _onboardPopup.SoundClicked += () => _voiceConsole?.OpenSoundSettings(topMost: true);
            _onboardPopup.ApplySavedBounds(_config.OnboardPopupBounds);
            _onboardPopup.BroadcastClicked += async c => await ToggleBroadcastAsync(c);
            _onboardPopup.IntercomClicked += async () => await ToggleIntercomAsync();
            _onboardPopup.HiddenByUser += () =>
            {
                _config.OnboardPopupBounds = _onboardPopup.SavedBounds;
                _config.OnboardPopupOpen = false; // 次からは自動で出さない（タブの［放送装置を開く］で出す）
                _config.Save();
            };
        }

        if (!_onboardPopup.Visible) _onboardPopup.Show();
        _onboardPopup.Activate();
        if (!_config.OnboardPopupOpen)
        {
            _config.OnboardPopupOpen = true;
            _config.Save();
        }
    }

    private void CloseOnboardPopup()
    {
        _onboardKeyHook?.Dispose();
        _onboardKeyHook = null;
        if (_onboardPopup == null) return;
        _config.OnboardPopupBounds = _onboardPopup.SavedBounds;
        _config.Save();
        _onboardPopup.AllowClose = true;
        _onboardPopup.Close();
        _onboardPopup.Dispose();
        _onboardPopup = null;
    }

    private TabPage BuildOnboardTab()
    {
        var page = new TabPage { Text = "車内放送", Padding = new Padding(12), BackColor = Color.White };
        var talk = _role is CrewRole.Driver or CrewRole.Conductor;

        var buttons = new TableLayoutPanel { Dock = DockStyle.Top, Height = 58, ColumnCount = 3, Visible = talk };
        for (var i = 0; i < 3; i++) buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        buttons.Controls.Add(_insideButton, 0, 0);
        buttons.Controls.Add(_outsideButton, 1, 0);
        buttons.Controls.Add(_intercomButton, 2, 0);
        _insideButton.Click += async (_, _) => await ToggleBroadcastAsync(OnboardChannel.Inside);
        _outsideButton.Click += async (_, _) => await ToggleBroadcastAsync(OnboardChannel.Outside);
        _intercomButton.Click += async (_, _) => await ToggleIntercomAsync();

        // 放送装置のポップ（最前面の小さな窓。ゲームの画面の上で放送・インターホンを扱う）
        var popup = new TableLayoutPanel { Dock = DockStyle.Top, Height = 38, ColumnCount = 2, Visible = talk };
        popup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        popup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        var open = new Button { Text = "放送装置を開く（最前面の小さな窓）", Dock = DockStyle.Fill, Margin = new Padding(3) };
        open.Click += (_, _) => OpenOnboardPopup();
        var keys = new Button { Text = "キー設定", Dock = DockStyle.Fill, Margin = new Padding(3) };
        keys.Click += (_, _) => EditOnboardKeys();
        var sound = new Button { Text = "音の設定", Dock = DockStyle.Fill, Margin = new Padding(3) }; // マイクの感度・ブースト（列車無線と共通）
        sound.Click += (_, _) => _voiceConsole?.OpenSoundSettings();
        popup.ColumnCount = 3;
        popup.ColumnStyles.Clear();
        popup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        popup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        popup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        popup.Controls.Add(open, 0, 0);
        popup.Controls.Add(keys, 1, 0);
        popup.Controls.Add(sound, 2, 0);

        _passengerBox.Visible = _role == CrewRole.Rider;
        _passengerBox.Checked = _config.Passenger;
        _passengerBox.CheckedChanged += (_, _) =>
        {
            _config.Passenger = _passengerBox.Checked;
            _config.Save();
            _server.Passenger = _passengerBox.Checked; // 次の報告（0.5秒以内）でサーバーに伝わる
            ShowMessage(_passengerBox.Checked ? OnboardTexts.PassengerOn : OnboardTexts.PassengerOff);
        };

        // 音量の表は、行の数に合わせて高さを自動で決める（固定の高さだと、つまみの行が下で切れる）
        var volumes = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Padding = new Padding(0, 6, 0, 0)
        };
        volumes.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        volumes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        volumes.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        void Volume(string label, Func<float> get, Action<float> set, int row)
        {
            var bar = new TrackBar { Minimum = 0, Maximum = 200, TickFrequency = 20, SmallChange = 5, LargeChange = 10, Dock = DockStyle.Fill, AccessibleName = label };
            bar.Value = (int)Math.Round(Math.Clamp(get(), 0, 2) * 100);
            _onboardTimer.Tick += (_, _) => // ポップで変えたら、こちらも合わせる
            {
                if (bar.Capture) return;
                var v = (int)Math.Round(Math.Clamp(get(), 0, 2) * 100);
                if (v != bar.Value) bar.Value = v;
            };
            var value = new Label { Text = $"{bar.Value}%", AutoSize = true, Anchor = AnchorStyles.Left };
            bar.ValueChanged += (_, _) =>
            {
                value.Text = $"{bar.Value}%";
                set(bar.Value / 100f);
                _config.Save();
            };
            volumes.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            volumes.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            volumes.Controls.Add(bar, 1, row);
            volumes.Controls.Add(value, 2, row);
        }

        Volume("放送音量", () => _config.Radio.BroadcastVolume, v => _config.Radio.BroadcastVolume = v, 0);
        Volume("連絡音量", () => _config.Radio.IntercomVolume, v => _config.Radio.IntercomVolume = v, 1);
        if (talk) Volume("送る音量", () => _config.Radio.OnboardSendGain, v => _config.Radio.OnboardSendGain = v, 2);

        // 説明もいちばん下に積む。窓が狭くて入りきらないときは、タブ全体をスクロールする（操作・音量・説明のどれも切れない）
        var helpText = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 10, 0, 0), ForeColor = Color.FromArgb(90, 96, 108),
            Text = talk ? OnboardTexts.CrewHelp : OnboardTexts.RiderHelp // 文言は OnboardTexts.cs
        };
        page.AutoScroll = true;
        page.ClientSizeChanged += (_, _) => helpText.MaximumSize = new Size(Math.Max(100, page.ClientSize.Width - page.Padding.Horizontal - 4), 0);

        page.Controls.Add(helpText);
        page.Controls.Add(volumes);
        page.Controls.Add(_onboardModel);
        _onboardMonitorBox.Visible = talk;
        _onboardMonitorBox.Checked = _config.Radio.OnboardMonitor;
        _onboardMonitorBox.CheckedChanged += (_, _) =>
        {
            if (_config.Radio.OnboardMonitor == _onboardMonitorBox.Checked) return;
            _config.Radio.OnboardMonitor = _onboardMonitorBox.Checked;
            _config.Save();
        };
        page.Controls.Add(_onboardMonitorBox);
        page.Controls.Add(_onboardMic);
        page.Controls.Add(_onboardSend);
        page.Controls.Add(_passengerBox);
        page.Controls.Add(popup);
        page.Controls.Add(buttons);
        page.Controls.Add(_onboardMembers);
        page.Controls.Add(_onboardStatus);
        return page;
    }

    private async Task ToggleBroadcastAsync(OnboardChannel channel)
    {
        var st = _server.Onboard;
        if (_onboardBusy || st == null) return;
        var mine = st.BroadcasterId == st.SelfId && st.Broadcast == channel;
        await OnboardCommandAsync(Protocol.SetOnboardBroadcast, mine ? OnboardChannel.None : channel);
    }

    private async Task ToggleIntercomAsync()
    {
        var st = _server.Onboard;
        if (_onboardBusy || st == null) return;
        await OnboardCommandAsync(Protocol.SetIntercom, !st.Intercom);
    }

    private async Task OnboardCommandAsync(string method, object arg)
    {
        _onboardBusy = true;
        try
        {
            var result = await _server.OnboardCommandAsync(method, arg);
            ShowMessage(result.Message);
            if (!result.Ok) System.Media.SystemSounds.Beep.Play();
        }
        finally
        {
            _onboardBusy = false;
        }
    }

    private static string ChannelName(OnboardChannel c) => c switch
    {
        OnboardChannel.Inside => "車内放送", OnboardChannel.Outside => "車外放送", OnboardChannel.Intercom => "連絡", _ => ""
    };

    private void UpdateOnboard()
    {
        var online = !_closing && _server.IsJoined && _server.HasFreshState;
        var st = online ? _server.Onboard : null;
        // 聞く人が乗っている車両のスピーカーの音にする（運転士は前の端、車掌は後ろの端、添乗はまん中の車両）。
        // 形式の違う車両をつないだ編成（4300系2両＋5320系など）では、運転士と車掌で聞こえ方が変わる
        var models = _crewPanel?.CarModels ?? [];
        var occupancy = _crewPanel?.CarOccupancy ?? [];
        var seat = models.Count == 0 ? -1 : _role switch { CrewRole.Driver => 0, CrewRole.Conductor => models.Count - 1, _ => models.Count / 2 };
        if (_onboardAudio != null)
        {
            _onboardAudio.CarModel = seat >= 0 ? models[seat] : "";
            _onboardAudio.Occupancy = seat >= 0 && seat < occupancy.Count ? occupancy[seat] : -1;
        }
        _onboardAudio?.Synchronize(st, online);

        // 状態
        if (!_server.IsJoined) SetOnboardStatus("サーバーに接続すると使えます", Color.Gray);
        else if (!_server.OnboardSupported) SetOnboardStatus("サーバーが古いため、放送装置は使えません", Color.Gray);
        else if (st == null)
            SetOnboardStatus(_role == CrewRole.Conductor ? "列番を確定して、運転士に乗務を許可されると使えます" : "列番を確定すると使えます", Color.Gray);
        else if (st.Broadcast != OnboardChannel.None)
        {
            var who = st.BroadcasterId == st.SelfId ? "自分" : st.Members.FirstOrDefault(m => m.Id == st.BroadcasterId)?.Label ?? "";
            SetOnboardStatus($"列番 {st.TrainNumber}　● {ChannelName(st.Broadcast)}", st.Broadcast == OnboardChannel.Outside ? OutsideColor : InsideColor);
        }
        else SetOnboardStatus($"列番 {st.TrainNumber}　", Color.FromArgb(32, 36, 44));

        // 乗っている人
        var members = "";
        if (st != null)
        {
            var crew = st.Members.Where(m => m.Role != CrewRole.Rider)
                .Select(m => m.Label + (m.Id == st.SelfId ? "（自分）" : "") + (m.Intercom ? "　インターホン入" : ""));
            var riders = st.Members.Count(m => m.Role == CrewRole.Rider);
            var passengers = st.Members.Count(m => m is { Role: CrewRole.Rider, Passenger: true });
            members = string.Join("／", crew) + (riders > 0 ? $"／添乗 {riders}人" + (passengers > 0 ? $"（うち乗客 {passengers}人）" : "") : "");
            var speaking = _onboardAudio?.Speaking ?? [];
            if (speaking.Count > 0)
                members += "\n受話中: " + string.Join("、", speaking.Select(k =>
                    $"{st.Members.FirstOrDefault(m => m.Id == k.Sender)?.Label ?? ""}（{ChannelName(k.Channel)}）"));
        }

        if (_onboardMembers.Text != members) _onboardMembers.Text = members;

        // ボタン
        var canTalk = st?.CanTalk == true;
        var otherBroadcasting = st != null && st.Broadcast != OnboardChannel.None && st.BroadcasterId != st.SelfId;
        // 車内・車外・連絡はどれか1つ（押すと、ほかの2つは切れる。サーバーが切り替える）
        SetToggle(_insideButton, st is { Broadcast: OnboardChannel.Inside } && st.BroadcasterId == st.SelfId, canTalk && !otherBroadcasting, PressedColor);
        SetToggle(_outsideButton, st is { Broadcast: OnboardChannel.Outside } && st.BroadcasterId == st.SelfId, canTalk && !otherBroadcasting, PressedColor);
        SetToggle(_intercomButton, st?.Intercom == true, canTalk, PressedColor);

        // 送話
        var audio = _onboardAudio;
        var send = audio?.SendChannel ?? OnboardChannel.None;
        var (text, color) = audio == null ? ("", Color.Gray)
            : audio.Error.Length > 0 ? (audio.Error, Color.Firebrick)
            : audio.Transmitting ? ($"● 送話中（{ChannelName(send)}）", Color.Firebrick)
            : send != OnboardChannel.None ? ($"マイク待機中（{ChannelName(send)}", Color.FromArgb(90, 96, 108))
            : (canTalk ? "マイクOFF" : st?.HearsIntercom == true ? "聞くだけ（放送・インターホン）" : st != null ? "聞くだけ（放送）" : "", Color.Gray);
        if (_onboardSend.Text != text) _onboardSend.Text = text;
        _onboardSend.ForeColor = color;
        _onboardMic.Value = (int)Math.Round(Math.Clamp(audio?.MicLevel ?? 0, 0, 1) * 100);

        // ポップ: 列車に乗ったら一度だけ自動で出す（前に閉じていたら出さない）。出ていれば同じ表示に合わせる
        if (canTalk && !_onboardPopupAuto)
        {
            _onboardPopupAuto = true;
            if (_config.OnboardPopupOpen) OpenOnboardPopup();
        }

        // 形式の表示: 「形式 5320（新しめの車両）・乗車率 85%」。形式が混ざっていれば「形式 4300 + 5320（…）」（かっこの中は、自分の車両の音）
        var kinds = models.Where(x => x.Length > 0).Distinct().ToList();
        var model = (_onboardAudio?.CarModel is { Length: > 0 }
                        ? $"形式 {string.Join(" + ", kinds)}（{_onboardAudio.Profile.Name}）"
                        : "形式 不明（標準）")
                    + (_onboardAudio?.Occupancy is >= 0 and var occ ? $"・乗車率 {(occ > 2 ? occ : occ * 100):0}%" : "");
        if (_onboardMonitorBox.Checked != _config.Radio.OnboardMonitor) _onboardMonitorBox.Checked = _config.Radio.OnboardMonitor; // ポップで変えたら合わせる
        if (_onboardModel.Text != model) _onboardModel.Text = model;
        if (_onboardPopup is { Visible: true } popup)
        {
            popup.ShowModel(model);
            popup.ShowKeys(OnboardKeyText("Inside"), OnboardKeyText("Outside"), OnboardKeyText("Intercom"));
        }

        if (_onboardPopup is { Visible: true } popup2)
            popup2.ShowState(_onboardStatus.Text.Replace("　● ", "　"), _onboardStatus.ForeColor, st, text, color, audio?.MicLevel ?? 0);
    }

    private void SetOnboardStatus(string text, Color color)
    {
        if (_onboardStatus.Text != text) _onboardStatus.Text = text;
        _onboardStatus.ForeColor = color;
    }

    private static void SetToggle(CheckBox b, bool on, bool enabled, Color color)
    {
        b.Checked = on;
        b.Enabled = enabled || on;
        b.BackColor = on ? color : Color.FromArgb(243, 244, 246);
        b.ForeColor = on ? PressedText : enabled ? Color.FromArgb(32, 36, 44) : Color.Gray;
        b.FlatAppearance.BorderColor = on ? color : Color.FromArgb(209, 213, 219);
        b.FlatAppearance.CheckedBackColor = b.BackColor; // 入のときもこの色（既定の淡い色にしない）
    }
}
