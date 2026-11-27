using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Gokko.Common;

namespace Gokko.Client;

/// <summary>
/// 内線電話（列車無線とは別の回線。別ウィンドウ）。駅・司令・列車とかけ合う。
/// ・司令機（desk）: 事務所のビジネスホン（縦長・左に受話器）の形。液晶・ワンタッチ（上下 2 段 × 16。ランプ: 緑 = 出られる人がいる、
///   赤の点滅 = 着信中）・機能キー（一斉呼出・応答・終話・マイク）・ダイヤル・スピーカ／再呼／転送／保留。
///   受話器（スピーカ）を上げて番号を押すと、押し終えて 2.5 秒か # でかかる。転送は通話中に［転送］→ 転送先の番号
/// ・運転士・車掌（keitai）: 折りたたみの携帯電話。通話・終話・左右のソフトキー（電話帳・転送／保留・マイク）・上下キー・ダイヤル
/// ・携帯: 番号は数字キー（キーボードの数字でも）で押して［通話］。転送は、通話中に番号を押して［転送］
/// ・運転士は停車中（0km/h）だけ使える（走り出すと、話している通話は保留になる）
/// ・着信すると、閉じていても前に出てくる（ほかの画面の操作は奪わない）
/// ・ウィンドウの端を引っぱると、形を保ったまま大きさが変わる（60〜200%。設定に覚えておく）
/// </summary>
public sealed class ExtensionPhoneForm : Form
{
    private readonly ServerLink _server;
    private readonly ExtensionAudio _audio;
    private readonly bool _keitai;
    private readonly RadioSettings _settings;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private readonly Font _small = new("Yu Gothic UI", 9f), _mid = new("Yu Gothic UI", 11f), _big = new("Yu Gothic UI", 15f, FontStyle.Bold), _num = new("Consolas", 20f, FontStyle.Bold);

    private readonly List<PhoneKey> _pad = new();
    private readonly PhoneKey _callKey, _endKey, _holdKey, _transferKey, _pageKey, _muteKey, _leftSoft, _rightSoft, _up, _down, _center;
    private readonly TrackBar _volume = new() { Minimum = 0, Maximum = 200, TickStyle = TickStyle.None, Value = 100 };

    // 卓上の電話機だけ
    private readonly PhoneKey _hookKey, _redialKey, _pageSwitch, _volUp, _volDown, _crewKey;
    private readonly PhoneKey[] _ones = new PhoneKey[16];
    private readonly System.Windows.Forms.Timer _dialTimer = new() { Interval = 2500 }; // 番号を押し終えてからかかるまで
    private bool _offHook, _transferArmed;
    private int _onePage; // 0 = 上、1 = 下
    private static string _lastNumber = "";
    private static readonly Color DeskInk = Color.FromArgb(40, 40, 40), DeskEdge = Color.FromArgb(138, 134, 126), DeskFace = Color.FromArgb(246, 245, 241);

    private ExtensionState? _state;
    private string _buffer = "", _message = "";
    private DateTime _messageUntil;
    private bool _showDirectory, _busy, _shuttingDown;
    private int _dirIndex = -1;
    private long _lastIncoming = -1;
    private long _lastActive = -1;

    public ExtensionPhoneForm(ServerLink server, RadioSettings settings, bool keitai)
    {
        _server = server;
        _keitai = keitai;
        _settings = settings;
        _audio = new ExtensionAudio(server, settings);
        Text = keitai ? "内線電話（携帯）" : "内線電話 — 司令";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        KeyPreview = true;
        ShowInTaskbar = true;
        BackColor = keitai ? Color.FromArgb(24, 30, 40) : Color.FromArgb(48, 50, 54);
        ClientSize = keitai ? new Size(300, 640) : new Size(380, 566);

        PhoneKey Key(string text, Color face, Color ink) => keitai
            ? new() { Text = text, Face = face, Ink = ink }
            : new() { Text = text, Face = DeskFace, Ink = DeskInk, Shadow = DeskEdge, Font = new Font("Yu Gothic UI", 8.5f) };
        var light = keitai ? Color.FromArgb(222, 230, 240) : Color.FromArgb(232, 234, 236);
        var ink = Color.FromArgb(20, 30, 45);
        _callKey = Key(keitai ? "📞 通話" : "発信", keitai ? light : Color.FromArgb(70, 190, 105), keitai ? Color.FromArgb(29, 138, 62) : Color.White);
        _endKey = Key("終話", keitai ? light : Color.FromArgb(220, 80, 66), keitai ? Color.FromArgb(192, 57, 43) : Color.White);
        _holdKey = Key("保留", Color.FromArgb(240, 190, 80), ink);
        _transferKey = Key("転送", light, ink);
        _pageKey = Key("一斉呼出（全駅）", light, ink);
        _muteKey = Key("マイク 入", light, ink);
        _leftSoft = Key("電話帳", light, ink);
        _rightSoft = Key("マイク切", light, ink);
        _up = Key("▲", light, ink);
        _down = Key("▼", light, ink);
        _center = Key("決定", Color.White, ink);
        _hookKey = Key("◁ スピーカ", light, ink);
        _redialKey = Key("再呼", light, ink);
        _pageSwitch = Key("上下", light, ink);
        _volDown = Key("▼", light, ink);
        _volUp = Key("▲", light, ink);
        _crewKey = Key("乗務員", light, ink);
        if (!keitai) _crewKey.Font = new Font("Yu Gothic UI", 7f);
        for (var i = 0; i < _ones.Length; i++)
        {
            var slot = i;
            _ones[i] = Key("", light, ink);
            _ones[i].Click += async (_, _) => await OneTouchAsync(slot);
        }
        foreach (var d in "123456789*0#")
        {
            var k = Key(d.ToString(), light, ink);
            k.Click += (_, _) => Press(d);
            _pad.Add(k);
        }

        if (keitai) LayoutKeitai(); else LayoutDesk();
        _volume.Value = 100;
        _volume.BackColor = keitai ? Color.FromArgb(31, 53, 88) : Color.FromArgb(40, 42, 46);
        _volume.ValueChanged += (_, _) => _audio.Volume = _volume.Value / 100f;

        _callKey.Click += async (_, _) => await CallAsync();
        _endKey.Click += async (_, _) => await EndAsync();
        _holdKey.Click += async (_, _) => await HoldAsync();
        _transferKey.Click += async (_, _) => await TransferAsync();
        _pageKey.Click += async (_, _) => await PageAsync();
        _muteKey.Click += (_, _) => ToggleMute();
        _hookKey.Click += async (_, _) => await HookAsync();
        _redialKey.Click += async (_, _) => await RedialAsync();
        _pageSwitch.Click += (_, _) => { _onePage = 1 - _onePage; RefreshKeys(); Invalidate(); };
        // ［乗務員］の一覧を開いているあいだ、▲▼は一覧を選ぶ
        _volDown.Click += (_, _) => { if (_showDirectory) MoveDirectory(1); else StepVolume(-25); };
        _volUp.Click += (_, _) => { if (_showDirectory) MoveDirectory(-1); else StepVolume(25); };
        _crewKey.Click += (_, _) =>
        {
            if (_showDirectory) { _showDirectory = false; Invalidate(); return; }
            _buffer = "";
            _dirIndex = -1;
            MoveDirectory(0);
        };
        _dialTimer.Tick += async (_, _) => await FireDialAsync();
        _up.Click += (_, _) => MoveDirectory(-1);
        _down.Click += (_, _) => MoveDirectory(1);
        _center.Click += async (_, _) =>
        {
            if (_showDirectory || (Incoming != null && Active == null)) await CallAsync();
            else MoveDirectory(0);
        };
        _leftSoft.Click += async (_, _) =>
        {
            if (_showDirectory) { _showDirectory = false; Invalidate(); }
            else if (Active is { Status: "talking" }) await TransferAsync();
            else MoveDirectory(0);
        };
        _rightSoft.Click += async (_, _) =>
        {
            if (Active is { Status: "talking" } || Held.Count > 0) await HoldAsync();
            else ToggleMute();
        };

        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        // 大きさ: いまの配置を「等倍」として覚え、ウィンドウを伸び縮みできるようにする
        foreach (Control c in Controls) _design[c] = (c.Bounds, c.Font.Size, c is PhoneKey k ? k.Radius : -1);
        _designClient = ClientSize;
        FormBorderStyle = FormBorderStyle.Sizable;
        var saved = keitai ? settings.ExtPhoneScaleKeitai : settings.ExtPhoneScaleDesk;
        if (saved is > 0 and not 1f) SetScale(saved);
    }

    private void LayoutKeitai()
    {
        // 上半分は画面（OnPaint で描く）。下半分にキー
        _leftSoft.SetBounds(22, 352, 80, 30);
        _rightSoft.SetBounds(198, 352, 80, 30);
        _callKey.SetBounds(22, 390, 80, 34);
        _endKey.SetBounds(198, 390, 80, 34);
        _up.SetBounds(126, 348, 48, 26);
        _center.SetBounds(120, 376, 60, 30);
        _down.SetBounds(126, 408, 48, 26);
        for (var i = 0; i < _pad.Count; i++) _pad[i].SetBounds(30 + i % 3 * 82, 446 + i / 3 * 40, 74, 34);
        _volume.SetBounds(70, 610, 200, 24);
        Controls.AddRange([_leftSoft, _rightSoft, _callKey, _endKey, _up, _center, _down, _volume]);
        Controls.AddRange(_pad.ToArray());
    }

    // 卓上の電話機の場所（PaintDesk と合わせる）
    private const int PanelX = 100, PanelW = 266;
    private static readonly Rectangle DeskLcd = new(PanelX, 30, PanelW, 96);
    private static readonly Rectangle DeskPaper = new(PanelX + 12, 160, PanelW - 12, 148);

    private void LayoutDesk()
    {
        // 左は受話器（OnPaint）。右の上から: 液晶・上下と音量・ワンタッチ（記名紙）・機能キー・ダイヤル・下の 4 つ
        var y = DeskLcd.Bottom + 9;
        _pageSwitch.SetBounds(PanelX + 54, y, 36, 17);
        _crewKey.SetBounds(PanelX + 130, y, 34, 17);
        _volDown.SetBounds(PanelX + PanelW - 76, y, 36, 17);
        _volUp.SetBounds(PanelX + PanelW - 36, y, 36, 17);
        var cw = (DeskPaper.Width - 8 - 15) / 4;
        for (var i = 0; i < _ones.Length; i++)
            _ones[i].SetBounds(DeskPaper.X + 4 + i % 4 * (cw + 5), DeskPaper.Y + 18 + i / 4 * 36, cw, 16);
        var fw = (PanelW - 18) / 4;
        PhoneKey[] fn = [_pageKey, _callKey, _endKey, _muteKey];
        for (var i = 0; i < 4; i++) fn[i].SetBounds(PanelX + i * (fw + 6), 330, fw, 18);
        var padX = PanelX + (PanelW - (3 * 54 + 2 * 16)) / 2;
        for (var i = 0; i < _pad.Count; i++)
        {
            _pad[i].SetBounds(padX + i % 3 * 70, 360 + i / 3 * 40, 54, 33);
            _pad[i].Radius = 15;
            _pad[i].Font = new Font("Yu Gothic UI", 13f, FontStyle.Bold);
        }

        PhoneKey[] bottom = [_hookKey, _redialKey, _transferKey, _holdKey];
        for (var i = 0; i < 4; i++)
        {
            bottom[i].SetBounds(PanelX + i * (fw + 6), 524, fw, 24);
            bottom[i].Radius = 11;
        }

        foreach (var k in fn.Concat(_ones).Append(_pageSwitch).Append(_crewKey).Append(_volUp).Append(_volDown)) k.Radius = 4;
        Controls.AddRange([_pageSwitch, _crewKey, _volDown, _volUp, .. _ones, .. fn, .. bottom]);
        Controls.AddRange(_pad.ToArray());
    }

    // ---- 状態の読み方 ----

    private List<ExtensionCall> Calls => _state?.Calls ?? new();
    private ExtensionCall? Active => Calls.FirstOrDefault(c => c.Status is "talking" or "outgoing");
    private ExtensionCall? Incoming => Calls.FirstOrDefault(c => c.Status == "incoming");
    private List<ExtensionCall> Held => Calls.Where(c => c.Status == "held").ToList();
    private List<ExtensionEntry> Directory => _state?.Directory ?? new();
    private List<ExtensionEntry> Crew => _state?.Crew ?? new();

    /// <summary>一覧で選んでかける相手。卓上は乗務員だけ（司令・駅はワンタッチで固定）、携帯は司令・駅・乗務員をみな。</summary>
    private List<ExtensionEntry> Book => _keitai ? Directory.Concat(Crew).ToList() : Crew;

    private static bool IsCrew(ExtensionEntry d) => d.Kind is ExtensionExchangeKinds.Driver or ExtensionExchangeKinds.Conductor;

    // ---- 開く・閉じる ----

    public void OpenPhone()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override bool ShowWithoutActivation => true; // 着信で出てきても、ゲームの操作を奪わない

    private void Tick()
    {
        var online = _server.IsJoined;
        _state = online ? _server.Extension : null;
        _audio.Synchronize(_state, online, _keitai);

        var inc = Incoming;
        if (inc != null && inc.Id != _lastIncoming && !_shuttingDown)
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        }

        var act = Active;
        if (_lastActive >= 0 && act == null && Held.All(h => h.Id != _lastActive)) { Say("通話が終わりました", 2000); _offHook = false; }
        _lastIncoming = inc?.Id ?? -1;
        _lastActive = act?.Id ?? -1;
        if (act != null) _offHook = true;
        if (act is not { Status: "talking" }) _transferArmed = false;
        _audio.DialTone = Dialing && _buffer.Length == 0 && !_transferArmed;

        RefreshKeys();
        if (_keitai) Invalidate(new Rectangle(0, 0, (int)Math.Ceiling(300 * _scale), (int)Math.Ceiling(340 * _scale)));
        else Invalidate();
    }

    private void RefreshKeys()
    {
        var act = Active;
        var inc = Incoming;
        var talking = act is { Status: "talking" };
        var usable = _state is { CanUse: true } && !_busy;
        _callKey.Text = _keitai ? "📞 通話" : "";
        _endKey.Text = _keitai ? (_buffer.Length > 0 ? "クリア" : "終話") : "";
        _holdKey.Text = _keitai ? "保留" : "♫ 保留";
        _muteKey.Text = "";
        _pageKey.Text = "";
        _pageKey.Enabled = !_keitai && _state?.Kind == ExtensionExchangeKinds.Dispatcher && !_busy;
        _leftSoft.Text = _showDirectory ? "戻る" : talking ? "転送" : "電話帳";
        _rightSoft.Text = talking ? "保留" : Held.Count > 0 ? "保留解除" : _audio.Muted ? "マイク入" : "マイク切";
        _callKey.Enabled = usable;
        _transferKey.Enabled = usable && (talking || !_keitai);
        _holdKey.Enabled = !_busy;
        _callKey.Blink = inc != null && act == null;

        if (!_keitai)
        {
            var amber = Color.FromArgb(240, 196, 92);
            _hookKey.Face = _offHook && act == null ? amber : DeskFace;
            _transferKey.Face = _transferArmed ? amber : DeskFace;
            _crewKey.Face = _showDirectory ? amber : DeskFace;
            _crewKey.Invalidate();
            _holdKey.Face = Held.Count > 0 && !talking ? amber : DeskFace;
            _hookKey.Invalidate();
            _transferKey.Invalidate();
            _holdKey.Invalidate();
        }

        if (!_keitai)
        {
            // ワンタッチ（上下 2 段 × 16）。名前とランプは記名紙に描く（PaintDesk）
            for (var i = 0; i < _ones.Length; i++) _ones[i].Enabled = DirectoryAt(i) is { Present: true } && !_busy;
        }
    }

    private ExtensionEntry? DirectoryAt(int slot)
    {
        var i = _onePage * 16 + slot;
        return i < Directory.Count ? Directory[i] : null;
    }

    // ---- 操作 ----

    private void Say(string text, int ms = 3500)
    {
        _message = text;
        _messageUntil = DateTime.Now.AddMilliseconds(ms);
        Invalidate();
    }

    private async Task<ExtensionResult?> CommandAsync(string method, params object?[] args)
    {
        if (_busy) return null;
        _busy = true;
        try
        {
            var r = await _server.ExtensionCommandAsync(method, args);
            if (!r.Ok) Say(r.Message);
            return r;
        }
        finally
        {
            _busy = false;
        }
    }

    // 卓上: 受話器（スピーカ）を上げて、まだ通話も着信もないとき（番号を押すとかかる）
    private bool Dialing => !_keitai && _offHook && Active == null && Incoming == null;

    private async Task FireDialAsync()
    {
        _dialTimer.Stop();
        if (_buffer.Length == 0) return;
        if (_transferArmed) await TransferAsync();
        else if (Dialing) await CallAsync();
    }

    private async void Press(char d)
    {
        _audio.Key(d);
        var waiting = Dialing || _transferArmed;
        if (waiting && d == '#' && _buffer.Length > 0) { await FireDialAsync(); return; }
        if (_buffer.Length < 12) _buffer += d;
        _showDirectory = false;
        if (waiting) { _dialTimer.Stop(); _dialTimer.Start(); }
        Invalidate();
    }

    private async Task CallAsync()
    {
        if (_buffer.Length > 0)
        {
            var n = _buffer;
            _buffer = "";
            _dialTimer.Stop();
            if (await CommandAsync(ExtensionProtocol.Dial, n) is { Ok: true } r)
            {
                _lastNumber = n;
                Say(r.Message, 2000);
            }

            return;
        }

        var book = Book;
        if (_showDirectory && _dirIndex >= 0 && _dirIndex < book.Count)
        {
            _buffer = book[_dirIndex].Number;
            _showDirectory = false;
            if (_transferArmed) { await TransferAsync(); return; }
            if (!_keitai) _offHook = true;
            await CallAsync();
            return;
        }

        if (Incoming is { } inc) { await CommandAsync(ExtensionProtocol.Answer, inc.Id); return; }
        if (Active == null && Held.Count > 0) { await CommandAsync(ExtensionProtocol.Hold, Held[0].Id, false); return; }
        Say(_keitai ? "番号を押すか、電話帳から選んでください" : "受話器を上げて番号を押すか、ワンタッチ・［乗務員］の一覧から選んでください");
    }

    private async Task EndAsync()
    {
        if (_buffer.Length > 0) { _buffer = ""; _dialTimer.Stop(); Invalidate(); return; }
        if (_transferArmed) { _transferArmed = false; Invalidate(); return; }
        if (_showDirectory) { _showDirectory = false; Invalidate(); return; }
        if (Active is { } act) { await CommandAsync(ExtensionProtocol.Hangup, act.Id); return; }
        if (Held.Count > 0) await CommandAsync(ExtensionProtocol.Hangup, Held[0].Id);
    }

    private async Task HoldAsync()
    {
        if (Active is { Status: "talking" } act) await CommandAsync(ExtensionProtocol.Hold, act.Id, true);
        else if (Held.Count > 0) await CommandAsync(ExtensionProtocol.Hold, Held[0].Id, false);
        else Say("保留にできる通話がありません");
    }

    private async Task TransferAsync()
    {
        if (Active is not { Status: "talking" } act)
        {
            _transferArmed = false;
            Say(_keitai ? "通話中に、番号を押してから［転送］を押します" : "通話中に［転送］を押して、転送先の番号を押します");
            return;
        }

        if (act.IsPage) { Say("一斉呼出は転送できません"); return; }
        if (_buffer.Length == 0)
        {
            // 卓上: ［転送］→ 転送先の番号（もう一度［転送］で元の通話に戻る）
            if (!_keitai) { _transferArmed = !_transferArmed; Say(_transferArmed ? "転送先の番号を押してください" : "", _transferArmed ? 15000 : 0); return; }
            Say("転送先の番号を押してから［転送］を押します");
            return;
        }

        var n = _buffer;
        _buffer = "";
        _transferArmed = false;
        _dialTimer.Stop();
        if (await CommandAsync(ExtensionProtocol.Transfer, act.Id, n) is { Ok: true } r) Say(r.Message);
    }

    private async Task OneTouchAsync(int slot)
    {
        if (DirectoryAt(slot) is not { } d) return;
        _buffer = d.Number;
        if (_transferArmed) { await TransferAsync(); return; }
        _offHook = true;
        await CallAsync();
    }

    private async Task RedialAsync()
    {
        if (_lastNumber.Length == 0) { Say("再呼できる番号がありません"); return; }
        if (Active != null) { Say("通話中は再呼できません"); return; }
        _buffer = _lastNumber;
        _offHook = true;
        await CallAsync();
    }

    /// <summary>受話器（スピーカ）: 上げる = 着信に出る・押してあった番号にかける・保留中の通話に戻る（何もなければ発信音）。置く = 切る。</summary>
    private async Task HookAsync()
    {
        _transferArmed = false;
        if (Active != null) { _offHook = false; await EndAsync(); return; }
        if (Incoming != null) { _offHook = true; await CallAsync(); return; }
        if (_offHook) { _offHook = false; _buffer = ""; _dialTimer.Stop(); Invalidate(); return; }
        _offHook = true;
        if (_buffer.Length > 0 || Held.Count > 0) await CallAsync();
        Invalidate();
    }

    private void StepVolume(int step)
    {
        _volume.Value = Math.Clamp(_volume.Value + step, _volume.Minimum, _volume.Maximum);
        Say($"受話音量 {_volume.Value}%", 1500);
    }

    private async Task PageAsync()
    {
        if (Active is { IsPage: true } act) { await CommandAsync(ExtensionProtocol.Hangup, act.Id); return; }
        if (await CommandAsync(ExtensionProtocol.Page) is { Ok: true } r) Say(r.Message);
    }

    private void ToggleMute()
    {
        _audio.Muted = !_audio.Muted;
        Invalidate();
    }

    private void MoveDirectory(int step)
    {
        var book = Book;
        if (book.Count == 0) { Say(_keitai ? "電話帳は空です" : "いま参加している乗務員はいません"); return; }
        _showDirectory = true;
        _dirIndex = _dirIndex < 0 || _dirIndex >= book.Count ? 0 : (_dirIndex + step + book.Count) % book.Count;
        Invalidate();
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        char? d = e.KeyCode switch
        {
            >= Keys.D0 and <= Keys.D9 when !e.Shift => (char)('0' + (e.KeyCode - Keys.D0)),
            >= Keys.NumPad0 and <= Keys.NumPad9 => (char)('0' + (e.KeyCode - Keys.NumPad0)),
            Keys.Multiply => '*',
            _ => null
        };
        if (d != null) { Press(d.Value); e.Handled = true; return; }
        if (!_keitai && e.Shift && e.KeyCode == Keys.D3) { Press('#'); e.Handled = true; return; }
        if (e.KeyCode == Keys.Back && _buffer.Length > 0) { _buffer = _buffer[..^1]; Invalidate(); e.Handled = true; }
        else if (e.KeyCode == Keys.Enter) { e.Handled = true; await CallAsync(); }
        else if (e.KeyCode == Keys.Escape) { e.Handled = true; await EndAsync(); }
        else if ((_keitai || _showDirectory) && e.KeyCode == Keys.Up) { MoveDirectory(-1); e.Handled = true; }
        else if ((_keitai || _showDirectory) && e.KeyCode == Keys.Down) { MoveDirectory(1); e.Handled = true; }
    }

    // ---- 描き方 ----

    private (string Big, string Sub, string Note, bool Blink) Lines()
    {
        var note = DateTime.Now < _messageUntil ? _message : "";
        if (!_server.IsJoined) return ("未接続", "サーバーに接続すると使えます", note, false);
        if (_state == null) return ("内線", "サーバーが内線に対応していません", note, false);
        var act = Active;
        var inc = Incoming;
        string big = "待受", sub = _state.CanUse ? "番号を押してください" : _state.Reason;
        var blink = false;
        if (_state.HandsetLinked) sub = "スマホの携帯電話につないでいます";
        if (act is { Status: "outgoing" }) { big = act.IsPage ? "一斉呼出中" : "呼出中"; sub = act.Peer + (act.PeerNumber.Length > 0 ? $"（{act.PeerNumber}）" : ""); }
        else if (act != null) { big = (act.IsPage ? "一斉 " : "通話中 ") + $"{act.Seconds / 60:00}:{act.Seconds % 60:00}"; sub = act.Peer + (act.PeerHeld ? "〔相手が保留中〕" : ""); }
        else if (inc != null)
        {
            big = inc.IsPage ? "一斉呼出 着信" : "着信";
            sub = inc.Peer + (inc.PeerNumber.Length > 0 ? $"（{inc.PeerNumber}）" : "") + (inc.TransferredBy.Length > 0 ? $" ← {inc.TransferredBy}から転送" : "");
            blink = true;
        }
        else if (Held.Count > 0) { big = "保留中"; sub = Held[0].Peer; }

        var extra = new List<string>();
        if (inc != null && act != null) extra.Add("着信あり: " + inc.Peer);
        if (Held.Count > 0 && (act != null || inc != null)) extra.Add($"保留 {Held.Count}件");
        if (_audio.Muted) extra.Add("マイク切");
        if (_audio.Error.Length > 0) extra.Add(_audio.Error);
        return (big, sub, note.Length > 0 ? note : string.Join("　", extra), blink);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Math.Abs(_scale - 1f) < 0.005f)
        {
            PaintPhone(e.Graphics);
            return;
        }

        // 等倍の座標のまま、広げて描く（文字は Text3 が拡大に合う描き方に切り替える）
        e.Graphics.ScaleTransform(_scale, _scale);
        PaintPhone(e.Graphics);
    }

    private void PaintPhone(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        if (_keitai) PaintKeitai(g); else PaintDesk(g);
    }

    // ---- 大きさ ----

    private readonly Dictionary<Control, (Rectangle Bounds, float Font, int Radius)> _design = new();
    private Size _designClient;
    private float _scale = 1f;
    private bool _resizing;
    private const float MinScale = 0.6f, MaxScale = 2f;

    /// <summary>等倍のときの場所（描くときはこちらを使う）。</summary>
    private Rectangle D(Control c) => _design.TryGetValue(c, out var d) ? d.Bounds : c.Bounds;

    private void SetScale(float scale)
    {
        scale = Math.Clamp(scale, MinScale, MaxScale);
        ClientSize = new Size((int)Math.Round(_designClient.Width * scale), (int)Math.Round(_designClient.Height * scale));
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        if (_design.Count == 0 || _resizing || WindowState == FormWindowState.Minimized || ClientSize.Width <= 0) return;
        var scale = Math.Clamp(ClientSize.Width / (float)_designClient.Width, MinScale, MaxScale);
        if (Math.Abs(scale - _scale) < 0.001f) return;
        _resizing = true;
        _scale = scale;
        SuspendLayout();
        foreach (var (c, d) in _design)
        {
            c.Bounds = new Rectangle((int)Math.Round(d.Bounds.X * scale), (int)Math.Round(d.Bounds.Y * scale),
                (int)Math.Round(d.Bounds.Width * scale), (int)Math.Round(d.Bounds.Height * scale));
            if (c is not PhoneKey k) continue;
            var old = k.Font;
            k.Font = new Font(old.FontFamily, d.Font * scale, old.Style);
            old.Dispose();
            if (d.Radius >= 0) k.Radius = (int)Math.Round(d.Radius * scale);
        }

        ResumeLayout();
        _resizing = false;
        Invalidate(true);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (_keitai) _settings.ExtPhoneScaleKeitai = _scale;
        else _settings.ExtPhoneScaleDesk = _scale;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    // 端を引っぱっているあいだ、縦横の比をそろえる
    protected override void WndProc(ref Message m)
    {
        const int WmSizing = 0x0214;
        if (m.Msg == WmSizing && _designClient.Width > 0)
        {
            var r = Marshal.PtrToStructure<Rect>(m.LParam);
            var extraW = Width - ClientSize.Width;
            var extraH = Height - ClientSize.Height;
            var edge = m.WParam.ToInt32(); // 1 左 2 右 3 上 4 左上 5 右上 6 下 7 左下 8 右下
            var aspect = _designClient.Height / (float)_designClient.Width;
            var w = r.Right - r.Left - extraW;
            if (edge is 3 or 6) w = (int)Math.Round((r.Bottom - r.Top - extraH) / aspect);
            w = Math.Clamp(w, (int)(_designClient.Width * MinScale), (int)(_designClient.Width * MaxScale));
            var h = (int)Math.Round(w * aspect);
            if (edge is 1 or 4 or 7) r.Left = r.Right - w - extraW; else r.Right = r.Left + w + extraW;
            if (edge is 3 or 4 or 5) r.Top = r.Bottom - h - extraH; else r.Bottom = r.Top + h + extraH;
            Marshal.StructureToPtr(r, m.LParam, false);
            m.Result = 1;
        }

        base.WndProc(ref m);
    }

    private static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void PaintKeitai(Graphics g)
    {
        var blue1 = Color.FromArgb(58, 90, 140);
        var blue2 = Color.FromArgb(31, 53, 88);
        using (var top = Round(new Rectangle(8, 8, 284, 316), 26))
        using (var br = new LinearGradientBrush(new Rectangle(8, 8, 284, 316), blue1, blue2, 60f)) g.FillPath(br, top);
        using (var hinge = new LinearGradientBrush(new Rectangle(24, 322, 252, 14), Color.FromArgb(13, 22, 38), blue1, 90f)) g.FillRectangle(hinge, 24, 322, 252, 14);
        using (var bot = Round(new Rectangle(8, 334, 284, 298), 26))
        using (var br = new LinearGradientBrush(new Rectangle(8, 334, 284, 298), blue1, blue2, 60f)) g.FillPath(br, bot);
        using (var spk = new SolidBrush(Color.FromArgb(13, 22, 38))) g.FillRectangle(spk, 126, 20, 48, 5);

        var scr = new Rectangle(24, 34, 252, 272);
        using (var b = new LinearGradientBrush(scr, Color.FromArgb(247, 251, 255), Color.FromArgb(223, 234, 246), 90f)) g.FillRectangle(b, scr);
        using (var pen = new Pen(Color.FromArgb(13, 22, 38), 4)) g.DrawRectangle(pen, scr);
        var ink = Color.FromArgb(16, 33, 58);
        var dim = Color.FromArgb(51, 80, 122);
        var on = _server.IsJoined && _state != null;
        Text3(g, on ? "▂▄▆█" : "圏外", _small, dim, new Rectangle(30, 38, 60, 18), ContentAlignment.MiddleLeft);
        Text3(g, _state?.Label ?? "内線", _small, dim, new Rectangle(80, 38, 140, 18), ContentAlignment.MiddleCenter);
        Text3(g, DateTime.Now.ToString("HH:mm"), _small, dim, new Rectangle(220, 38, 52, 18), ContentAlignment.MiddleRight);
        using (var pen = new Pen(Color.FromArgb(182, 200, 220))) g.DrawLine(pen, 28, 58, 272, 58);

        if (_showDirectory)
        {
            var list = Book;
            var start = Math.Max(0, Math.Min(_dirIndex - 5, list.Count - 11));
            for (var i = 0; i < 11 && start + i < list.Count; i++)
            {
                var d = list[start + i];
                var r = new Rectangle(30, 62 + i * 19, 240, 19);
                var sel = start + i == _dirIndex;
                if (sel) using (var b = new SolidBrush(Color.FromArgb(47, 111, 208))) g.FillRectangle(b, r);
                Text3(g, IsCrew(d) ? $"{d.Label}　{d.Number}" : $"{d.Number}　{d.Label}", _small, sel ? Color.White : d.Present ? ink : Color.FromArgb(138, 155, 178), r, ContentAlignment.MiddleLeft);
            }
        }
        else
        {
            var (big, sub, note, blink) = Lines();
            var show = !blink || DateTime.Now.Millisecond < 600;
            if (show) Text3(g, big, _big, blink ? Color.FromArgb(192, 57, 43) : ink, new Rectangle(28, 96, 244, 34), ContentAlignment.MiddleCenter);
            Text3(g, sub, _mid, ink, new Rectangle(28, 134, 244, 48), ContentAlignment.TopCenter);
            Text3(g, _buffer, _num, ink, new Rectangle(28, 186, 244, 40), ContentAlignment.MiddleCenter);
            Text3(g, note, _small, dim, new Rectangle(28, 230, 244, 48), ContentAlignment.TopCenter);
        }

        using (var b = new SolidBrush(dim)) g.FillRectangle(b, 26, 284, 248, 20);
        var act = Active;
        Text3(g, _leftSoft.Text, _small, Color.White, new Rectangle(30, 284, 80, 20), ContentAlignment.MiddleLeft);
        Text3(g, _showDirectory ? "発信" : Incoming != null && act == null ? "応答" : "決定", _small, Color.White, new Rectangle(110, 284, 80, 20), ContentAlignment.MiddleCenter);
        Text3(g, _rightSoft.Text, _small, Color.White, new Rectangle(190, 284, 80, 20), ContentAlignment.MiddleRight);
        Text3(g, "音量", _small, Color.FromArgb(223, 232, 243), new Rectangle(26, 610, 44, 24), ContentAlignment.MiddleLeft);
    }

    private void PaintDesk(Graphics g)
    {
        var edge = Color.FromArgb(138, 134, 126);
        var label = Color.FromArgb(77, 73, 67);
        var blinkOn = Environment.TickCount64 / 250 % 2 == 0;

        // 本体（明るい灰色）
        var bodyRect = new Rectangle(4, 4, _designClient.Width - 8, _designClient.Height - 8);
        using (var body = Round(bodyRect, 26))
        using (var br = new LinearGradientBrush(bodyRect, Color.FromArgb(246, 245, 241), Color.FromArgb(207, 204, 196), 70f)) g.FillPath(br, body);

        // 受話器受け（左）と受話器
        var cradle = new Rectangle(12, 14, 76, 536);
        using (var p = Round(cradle, 30))
        using (var br = new LinearGradientBrush(cradle, Color.FromArgb(189, 185, 177), Color.FromArgb(174, 170, 162), 0f)) g.FillPath(br, p);
        using (var b = new SolidBrush(Color.FromArgb(143, 139, 132)))
        {
            g.FillRectangle(b, cradle.X + 27, cradle.Y + 44, 22, 10);
            g.FillRectangle(b, cradle.X + 27, cradle.Bottom - 54, 22, 10);
        }

        var act = Active;
        var lifted = _offHook || act != null;
        var state = g.Save();
        if (lifted)
        {
            g.TranslateTransform(cradle.X + cradle.Width / 2f, cradle.Y + cradle.Height / 2f);
            g.RotateTransform(-6f);
            g.TranslateTransform(-(cradle.X + cradle.Width / 2f) - 8, -(cradle.Y + cradle.Height / 2f) - 12);
        }

        var hs = Rectangle.Inflate(cradle, -3, -3);
        if (lifted)
            using (var sh = Round(new Rectangle(hs.X + 6, hs.Y + 14, hs.Width, hs.Height), 34))
            using (var b = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillPath(b, sh);
        using (var p = Round(hs, 34))
        {
            using (var br = new LinearGradientBrush(hs, Color.FromArgb(212, 209, 202), Color.FromArgb(203, 200, 192), 0f))
            {
                var blend = new ColorBlend { Colors = [Color.FromArgb(212, 209, 202), Color.FromArgb(247, 246, 243), Color.FromArgb(203, 200, 192)], Positions = [0f, 0.42f, 1f] };
                br.InterpolationColors = blend;
                g.FillPath(br, p);
            }

            using (var pen = new Pen(Color.FromArgb(169, 165, 157))) g.DrawPath(pen, p);
        }

        foreach (var cy in new[] { hs.Y + 40, hs.Bottom - 40 })
        {
            var c = new Rectangle(hs.X + hs.Width / 2 - 23, cy - 23, 46, 46);
            using (var b = new SolidBrush(Color.FromArgb(222, 220, 214))) g.FillEllipse(b, c);
            using (var b = new SolidBrush(Color.FromArgb(109, 106, 100)))
                for (var dx = -18; dx <= 18; dx += 6)
                for (var dy = -18; dy <= 18; dy += 6)
                    if (dx * dx + dy * dy <= 18 * 18) g.FillEllipse(b, c.X + 23 + dx - 1.5f, c.Y + 23 + dy - 1.5f, 3, 3);
        }

        g.Restore(state);

        // 上: 名前と着信ランプ
        Text3(g, "G O K K O", _small, Color.FromArgb(120, 116, 108), new Rectangle(PanelX, 10, 120, 18), ContentAlignment.MiddleLeft);
        Text3(g, "着信", _small, Color.FromArgb(120, 116, 108), new Rectangle(PanelX + PanelW - 50, 10, 34, 18), ContentAlignment.MiddleRight);
        Lamp(g, PanelX + PanelW - 12, 19, Incoming != null ? (blinkOn ? Color.FromArgb(255, 74, 61) : Color.FromArgb(141, 138, 131)) : Color.FromArgb(141, 138, 131), 9);

        // 液晶（灰色の液晶。上の段に印と時刻、次に大きく番号、その下に文字）
        using (var b = new LinearGradientBrush(DeskLcd, Color.FromArgb(201, 206, 196), Color.FromArgb(179, 185, 173), 90f)) g.FillRectangle(b, DeskLcd);
        using (var pen = new Pen(Color.FromArgb(74, 73, 68), 3)) g.DrawRectangle(pen, DeskLcd);
        var ink = Color.FromArgb(29, 34, 26);
        var talking = act is { Status: "talking" };
        var held = Held;
        var icons = new List<string>();
        if (_offHook && act == null) icons.Add("◁");
        if (held.Count > 0) icons.Add("♫保留");
        if (_transferArmed) icons.Add("H 転送");
        if (_volume.Value > 100) icons.Add("大");
        if (_audio.Muted) icons.Add("消音");
        Text3(g, string.Join("  ", icons), _small, ink, new Rectangle(DeskLcd.X + 6, DeskLcd.Y + 3, 180, 16), ContentAlignment.MiddleLeft);
        var time = talking ? $"{act!.Seconds / 60:00}:{act.Seconds % 60:00}" : DateTime.Now.ToString("HH:mm");
        Text3(g, time, _small, ink, new Rectangle(DeskLcd.Right - 70, DeskLcd.Y + 3, 64, 16), ContentAlignment.MiddleRight);
        var cur = act ?? Incoming ?? held.FirstOrDefault();
        var num = _buffer.Length > 0 ? _buffer : cur?.PeerNumber ?? "";
        var (big, sub, note, blink) = Lines();
        if (_showDirectory)
        {
            // ［乗務員］の一覧: 選んでいる人を出す（▲▼で選び、［応答］でかける・［終話］で閉じる）
            var book = Book;
            if (_dirIndex >= book.Count) _dirIndex = book.Count - 1;
            var pick = _dirIndex >= 0 ? book[_dirIndex] : null;
            num = pick?.Number ?? "";
            big = pick != null ? $"乗務員 {_dirIndex + 1}/{book.Count}" : "乗務員 0人";
            sub = pick?.Label ?? "いま参加している乗務員はいません";
            note = _transferArmed ? "▲▼で選び［応答］で転送" : "▲▼で選び［応答］でかける";
            blink = false;
        }

        Text3(g, num, _num, ink, new Rectangle(DeskLcd.X + 4, DeskLcd.Y + 18, DeskLcd.Width - 8, 32), ContentAlignment.MiddleLeft);
        var me = _state is { Label.Length: > 0 } ? $"{_state.Label}　内線 {_state.Number}" : "内線電話";
        if (!blink || blinkOn) Text3(g, big + (sub.Length > 0 ? "　" + sub : ""), _small, ink, new Rectangle(DeskLcd.X + 6, DeskLcd.Y + 50, DeskLcd.Width - 12, 15), ContentAlignment.MiddleLeft);
        Text3(g, note.Length > 0 ? note : me, _small, ink, new Rectangle(DeskLcd.X + 6, DeskLcd.Y + 64, DeskLcd.Width - 12, 15), ContentAlignment.MiddleLeft);
        if (note.Length > 0) Text3(g, me, _small, ink, new Rectangle(DeskLcd.X + 6, DeskLcd.Y + 78, DeskLcd.Width - 12, 15), ContentAlignment.MiddleLeft);

        // 上下・通話のランプ
        var off = Color.FromArgb(141, 138, 131);
        var amber = Color.FromArgb(255, 176, 46);
        var green = Color.FromArgb(63, 210, 107);
        var cy2 = DeskLcd.Bottom + 17;
        Text3(g, "上", _small, label, new Rectangle(PanelX, cy2 - 8, 14, 16), ContentAlignment.MiddleLeft);
        Lamp(g, PanelX + 18, cy2, _onePage == 0 ? amber : off, 8);
        Text3(g, "下", _small, label, new Rectangle(PanelX + 26, cy2 - 8, 14, 16), ContentAlignment.MiddleLeft);
        Lamp(g, PanelX + 44, cy2, _onePage == 1 ? amber : off, 8);
        Text3(g, "通話", _small, label, new Rectangle(PanelX + 92, cy2 - 8, 30, 16), ContentAlignment.MiddleLeft);
        Lamp(g, PanelX + 122, cy2, act != null ? green : held.Count > 0 ? (blinkOn ? green : off) : off, 8);
        Text3(g, "音量", _small, label, new Rectangle(PanelX + PanelW - 104, cy2 - 8, 26, 16), ContentAlignment.MiddleRight);

        // スピーカの穴と記名紙（ワンタッチの名前とランプ）
        using (var b = new SolidBrush(Color.FromArgb(150, 111, 107, 100)))
            for (var y = DeskPaper.Y + 4; y < DeskPaper.Bottom - 4; y += 6) g.FillRectangle(b, PanelX, y, 8, 2);
        using (var b = new SolidBrush(Color.FromArgb(253, 253, 249))) g.FillRectangle(b, DeskPaper);
        using (var pen = new Pen(Color.FromArgb(183, 179, 171))) g.DrawRectangle(pen, DeskPaper);
        var ringing = Calls.Where(c => c.Status == "incoming").Select(c => c.PeerNumber).ToHashSet();
        var busyNumbers = Calls.Where(c => c.Status != "incoming").Select(c => c.PeerNumber).ToHashSet();
        var red = Color.FromArgb(255, 74, 61);
        for (var i = 0; i < _ones.Length; i++)
        {
            var k = D(_ones[i]);
            var d = DirectoryAt(i);
            var lamp = d == null ? off : ringing.Contains(d.Number) ? (blinkOn ? red : off) : busyNumbers.Contains(d.Number) ? red : d.Present ? green : off;
            Lamp(g, k.X + 4, k.Y - 8, lamp, 6);
            if (d != null) Text3(g, d.Label, _small, Color.FromArgb(35, 50, 74), new Rectangle(k.X + 9, k.Y - 16, k.Width - 9, 16), ContentAlignment.MiddleLeft);
        }

        // 機能キーの名前（キーの上に刷ってある）
        var fnLabels = new[]
        {
            _state?.Kind == ExtensionExchangeKinds.Dispatcher ? (act is { IsPage: true } ? "一斉終了" : "一斉呼出") : "",
            "応答", _buffer.Length > 0 ? "消去" : "終話", _audio.Muted ? "マイク切" : "マイク"
        };
        PhoneKey[] fn = [_pageKey, _callKey, _endKey, _muteKey];
        for (var i = 0; i < 4; i++) Text3(g, fnLabels[i], _small, label, new Rectangle(D(fn[i]).Left, D(fn[i]).Top - 16, D(fn[i]).Width, 16), ContentAlignment.MiddleCenter);
        if (_audio.Error.Length > 0) Text3(g, _audio.Error, _small, Color.FromArgb(176, 58, 46), new Rectangle(PanelX, 548, PanelW, 14), ContentAlignment.MiddleLeft);
    }

    private static void Lamp(Graphics g, int cx, int cy, Color color, int size)
    {
        using (var b = new SolidBrush(color)) g.FillEllipse(b, cx - size / 2f, cy - size / 2f, size, size);
        using (var pen = new Pen(Color.FromArgb(95, 92, 86))) g.DrawEllipse(pen, cx - size / 2f, cy - size / 2f, size, size);
    }

    private static void Text3(Graphics g, string text, Font font, Color color, Rectangle rect, ContentAlignment align)
    {
        using (var m = g.Transform)
        {
            if (!m.IsIdentity)
            {
                // 広げて描いているとき: TextRenderer は拡大に付いてこないので GDI+ で描く
                using var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter };
                sf.Alignment = align is ContentAlignment.MiddleCenter or ContentAlignment.TopCenter ? StringAlignment.Center
                    : align == ContentAlignment.MiddleRight ? StringAlignment.Far : StringAlignment.Near;
                sf.LineAlignment = align is ContentAlignment.MiddleLeft or ContentAlignment.MiddleCenter or ContentAlignment.MiddleRight ? StringAlignment.Center : StringAlignment.Near;
                var hint = g.TextRenderingHint;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var b = new SolidBrush(color)) g.DrawString(text, font, b, rect, sf);
                g.TextRenderingHint = hint;
                return;
            }
        }

        var flags = TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        flags |= align switch
        {
            ContentAlignment.MiddleLeft => TextFormatFlags.VerticalCenter | TextFormatFlags.Left,
            ContentAlignment.MiddleCenter => TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter,
            ContentAlignment.MiddleRight => TextFormatFlags.VerticalCenter | TextFormatFlags.Right,
            ContentAlignment.TopCenter => TextFormatFlags.HorizontalCenter,
            _ => TextFormatFlags.Left
        };
        TextRenderer.DrawText(g, text, font, rect, color, flags);
    }

    // ---- 終わり ----

    /// <summary>サーバーから抜けたとき: マイクと音を止める。</summary>
    public void StopForDisconnect() => _audio.Suspend();

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_shuttingDown && e.CloseReason == CloseReason.UserClosing)
        {
            // ×は隠すだけ（着信があれば、また出てくる）。通話はそのまま
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    public void Shutdown()
    {
        _shuttingDown = true;
        _timer.Stop();
        _audio.Dispose();
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shuttingDown = true;
            _timer.Dispose();
            _dialTimer.Dispose();
            _audio.Dispose();
            _small.Dispose();
            _mid.Dispose();
            _big.Dispose();
            _num.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>サーバーの <c>ExtensionExchange</c> の電話機の種類（ExtensionState.Kind）。</summary>
internal static class ExtensionExchangeKinds
{
    public const string Dispatcher = "dispatcher";
    public const string Driver = "driver";
    public const string Conductor = "conductor";
}

internal enum PhoneLamp { Off, Green, Red, RedBlink }

/// <summary>電話機のキー（角の丸いボタン。ランプ付きにもできる）。</summary>
internal sealed class PhoneKey : Control
{
    private bool _pressed;
    public Color Face { get; set; } = Color.Gainsboro;
    public Color Ink { get; set; } = Color.Black;
    public PhoneLamp Lamp { get; set; }
    public bool Blink { get; set; }
    public bool AlignLeft { get; set; }
    public int Radius { get; set; } = -1; // -1: 高さに合わせる
    public Color Shadow { get; set; } = Color.FromArgb(17, 17, 17);

    private static readonly System.Windows.Forms.Timer BlinkTimer = new() { Interval = 250 };
    private static event Action? BlinkTick;

    static PhoneKey()
    {
        BlinkTimer.Tick += (_, _) => BlinkTick?.Invoke();
        BlinkTimer.Start();
    }

    public PhoneKey()
    {
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold);
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent; // 角の丸みの外は、電話機の本体が見える
        BlinkTick += OnBlink;
    }

    private void OnBlink()
    {
        if (Blink || Lamp == PhoneLamp.RedBlink) Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var phase = Environment.TickCount64 / 250 % 2 == 0;
        var r = new Rectangle(0, _pressed ? 2 : 0, Width - 1, Height - 3);
        var radius = Radius >= 0 ? Radius : Math.Min(10, Height / 3);
        using (var shadow = new SolidBrush(Shadow))
        using (var sp = RoundRect(new Rectangle(0, 2, Width - 1, Height - 3), radius)) g.FillPath(shadow, sp);
        var face = Enabled ? Face : Face.GetBrightness() > 0.8f ? Color.FromArgb(Face.R * 15 / 16, Face.G * 15 / 16, Face.B * 15 / 16) : ControlPaint.Dark(Face, 0.25f); // 明るいキーは少し暗くするだけ
        if (Blink && phase) face = Color.FromArgb(255, 220, 120);
        using (var path = RoundRect(r, radius))
        using (var br = new LinearGradientBrush(r, ControlPaint.Light(face, 0.3f), face, 90f)) g.FillPath(br, path);
        var text = r;
        if (Lamp != PhoneLamp.Off || AlignLeft)
        {
            var lampColor = Lamp switch
            {
                PhoneLamp.Green => Color.FromArgb(63, 210, 107),
                PhoneLamp.Red => Color.FromArgb(255, 74, 61),
                PhoneLamp.RedBlink => phase ? Color.FromArgb(255, 74, 61) : Color.FromArgb(60, 30, 28),
                _ => Color.FromArgb(42, 44, 46)
            };
            using (var b = new SolidBrush(lampColor)) g.FillEllipse(b, 8, r.Y + r.Height / 2 - 5, 10, 10);
            text = new Rectangle(r.X + 24, r.Y, r.Width - 28, r.Height);
        }

        TextRenderer.DrawText(g, Text, Font, text, Enabled ? Ink : Color.FromArgb(150, Ink),
            TextFormatFlags.VerticalCenter | (AlignLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter) | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        var d = Math.Max(2, radius * 2);
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) BlinkTick -= OnBlink;
        base.Dispose(disposing);
    }
}
