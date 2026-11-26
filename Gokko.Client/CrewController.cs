using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Gokko.Common;

namespace Gokko.Client;

/// <summary>
/// 乗務（人の運転士と人の車掌が、同じ列車に乗って遊ぶ）の中継。クライアントの本体には何も出さない。
/// ・運転士: 自分のノッチ・レバーサー・戸閉を0.02秒ごとに読み、車掌が乗っていればサーバーの中継（/crew）へ送る。
///   車掌の操作（ドア・ブザー・非常ブレーキ・再開閉・乗降促進）が届いたら、自分のゲームで同じ操作をする
/// ・車掌: 車掌用操作盤（<see cref="ConductorPanelForm"/>。常に最前面の別窓）を出す。届いた運転士のノッチ・レバーサーを
///   自分のゲームへそのまま送る（TrainCrewInput）。操作盤の操作は、自分のゲームと運転士のゲームの両方で行う。
///   運転士の操作が3秒届かなくなったら常用最大ブレーキ（B8 の直通非常は使わない）
/// 1列車に運転士1人・車掌1人まで（サーバーが断る）。
/// 運転の記録は、この exe のフォルダの crew-logs\ にだけ CSV で残す（サーバーには残さない）。
/// </summary>
public sealed class CrewController
{
    private const int StaleMs = 3000;

    /// <summary>操作が変わらなくても送る・記録する間隔（走っている間は短く。位置のずれを細かく見るため）。</summary>
    private const int MovingHeartbeatMs = 100;
    private const int StoppedHeartbeatMs = 500;

    /// <summary>
    /// 運転士の操作が届かないとき・車掌をやめるときにかけるブレーキ（常用最大）。
    /// B8 は直通非常で、3000形はブレーキ方式の都合でしばらく動けなくなるので使わない。
    /// </summary>
    private const int ServiceMaxBrake = 7;

    /// <summary>車掌が状態（戸閉・非常・再開閉・乗降促進）を知らせる間隔。</summary>
    private const int StatusMs = 1000;

    /// <summary>運転士のゲームのドアが、車掌のゲームとこれだけ食い違ったままなら、合わせ直す（ドアが動く時間より長く）。</summary>
    private const int DoorMismatchMs = 5000;

    /// <summary>添乗: 運転士のゲームのドアと食い違ったら、これだけ待ってから自分のゲームのドアを合わせる（開くとき・閉まりきったとき）。</summary>
    private const int RiderDoorMs = 300;

    private readonly CrewRole _role;
    private readonly GameLink _game;
    private readonly ClientConfig _config;
    private readonly TciLink _tci = new();
    private readonly CrewLink _crew = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 20 }; // ゲームの操作を読む・送る周期（表示は0.1秒ごと）
    private long _lastViewMs;
    private ConductorPanelForm? _panel;

    // 運転士
    private long _seq = Environment.TickCount64;
    private DriverInput? _lastSent;
    private long _lastSentMs;
    private readonly ConcurrentQueue<ConductorInput> _fromConductor = new();
    private bool? _conductorAllClose;
    private long _conductorMs;
    private float? _conductorDistance; // 車掌のゲームの走った距離（試験ページからの操作では入らない）
    private long _doorMismatchSinceMs;
    private long _lastDoorFixMs;
    private string _lastOpenAction = "";
    private bool _appliedEmergency, _appliedReOpen, _appliedJoukou, _appliedBuzzer;
    private long _appliedBuzzerOffMs; // 車掌のブザーを止めた時刻（直後のゲームのブザーの状態は、自分が押したものと見なさない）
    private GlobalKeyboardHook? _keyHook; // 運転士: ゲームでブザーに使っているキーを見張る（ゲームのブザーの状態は読めないため）
    private bool _keyBuzzer;
    private readonly JoystickPoller _joystick = new(); // 運転士: コントローラー（マスコン・ゲームパッド）のボタン
    private readonly ConcurrentQueue<string> _driverKeyEvents = new(); // 運転士がキーで扱ったボタン（"ボタン名:1" / ":0"）

    // 車掌
    private volatile DriverInput? _latestInput;
    private long _latestInputMs;
    private long _lastSeq;
    private long _prevArrivalMs;
    private long _arrivalGapMs;
    private bool _haveDriver;
    private bool _linkBrake; // 運転士の操作が届かないのでブレーキをかけている
    private bool _emergency, _reOpen, _joukou, _buzzer;
    private bool _gameBuzzer;      // 車掌がゲームのキー・スイッチでブザーを押している
    private bool _localBuzzerOn;   // 自分のゲームのブザーを入れている（操作盤か、運転士のブザー）
    private long _localBuzzerOffMs;
    private long _lastPanelDoorMs; // 操作盤でドアを扱った時刻（直後のドアの開閉は、操作盤の操作として送ってある）
    private bool? _ownAllClose;
    private long _lastStatusMs;
    private DriverInput? _appliedInput;
    private int _catchUp; // 位置のずれを縮めるために、運転士のノッチへ足している段数（プラスで加速側）
    private long _catchUpMs;
    private int _maxPowerSeen = 1; // 運転士が使った一番強い力行（これより強い力行は足さない）
    private string _catchUpNote = "";
    private bool _brakeMode; // ブレーキ中の寄せ（止まる位置の予測で寄せる）をしている
    private bool _distMode; // 距離優先の寄せをしている
    private int _distNotch; // 距離優先で送っているノッチ（力行はプラス、ブレーキはマイナス）
    private int _gameMaxPower = GuessMaxPower; // 距離優先で使う力行の一番強い段（ゲームが受け付けなかったら下げる）
    private long _distNotchMs;
    // 距離優先で使う、段ごとの加速度（km/h/s。減速はマイナス）を 20km/h ごとに覚えたもの。初めは前の記録から測った値
    private readonly Dictionary<(int Notch, int Band), double> _accelModel = new();
    private int _modelNotch = int.MinValue;
    private long _modelNotchSinceMs;
    private long _notchMismatchMs; // ゲームのノッチが、送ったノッチと違い始めた時刻（0 なら同じ）
    private int _brakeOff; // ブレーキ中に、運転士のブレーキへ足している段数
    private long _brakeOffMs;
    private readonly Queue<(long Ms, float Speed)> _ownSpeeds = new(), _driverSpeeds = new();
    private double _ownDecel = double.NaN, _driverDecel = double.NaN; // 減速度（km/h/s。減速でプラス）
    private StreamWriter? _log;

    public CrewController(CrewRole role, GameLink game, ClientConfig config)
    {
        _role = role;
        _game = game;
        _config = config;
        _crew.DriverInputReceived += OnDriverInput;
        _crew.ConductorInputReceived += i => _fromConductor.Enqueue(i);
        _timer.Tick += (_, _) => Tick();
    }

    public CrewRole Role => _role;

    // ---- 通信状況（LinkMonitor が読む） ----

    /// <summary>相手の操作（運転士へは車掌の、車掌・添乗へは運転士の）が最後に届いてからの時間（ミリ秒）。まだ届いていなければ -1。</summary>
    public long PeerInputAgeMs
    {
        get
        {
            var at = _role == CrewRole.Driver ? _conductorMs : Interlocked.Read(ref _latestInputMs);
            return at > 0 ? Environment.TickCount64 - at : -1;
        }
    }

    /// <summary>車掌・添乗: 運転士の操作が届いた間隔（直前の2つの間。ミリ秒）。</summary>
    public long PeerArrivalGapMs => Interlocked.Read(ref _arrivalGapMs);

    /// <summary>中継との往復の時間（ミリ秒。分からなければ -1）。</summary>
    public int CrewRttMs => _crew.RttMs;

    /// <summary>中継につながっているか。</summary>
    public bool CrewConnected => _crew.IsJoined;

    /// <summary>中継で列番の乗務に入っているか（車掌は運転士に許可されているか）。</summary>
    public bool CrewJoined => _crew.IsJoined && _crew.Status is { TrainNumber.Length: > 0 }
                              && (_role != CrewRole.Conductor || _crew.Approval is null or { State: ApprovalState.Approved });

    /// <summary>同じ列番の顔ぶれ（中継から）。</summary>
    public CrewStatus? CrewStatus => _crew.Status;

    /// <summary>中継のお知らせ（断られたなど）。</summary>
    public string CrewMessage => _crew.Message;

    /// <summary>
    /// 位置のずれ（m。車掌の列車が運転士の列車より前にいればプラス）。分からなければ NaN。
    /// 車掌・添乗は自分のゲームと運転士の操作から、運転士は車掌が1秒ごとに知らせる走った距離から（粗い）。
    /// </summary>
    public double LastGap { get; private set; } = double.NaN;

    /// <summary>画面の下のお知らせに出す。</summary>
    public event Action<string>? Message;

    /// <summary>運転士へ: 自分の列車に車掌が乗っているか（本体の情報の行に出す）。</summary>
    public string Summary { get; private set; } = "";

    public void Start(string url, string password, string crewToken)
    {
        _tci.Start();
        if (_role == CrewRole.Driver && _keyHook == null)
        {
            _keyHook = new GlobalKeyboardHook();
            _keyHook.KeyDown += k => OnDriverInputDevice(k.ToString(), true);
            _keyHook.KeyUp += k => OnDriverInputDevice(k.ToString(), false);
            _keyHook.Start();
        }

        _crew.Start(url, password, _role, crewToken);
        _timer.Start();
        if (_role == CrewRole.Conductor) ShowPanel(); // 添乗には操作盤を出さない（扱えるものがない）
    }

    public async Task StopAsync()
    {
        _timer.Stop();
        _keyHook?.Dispose();
        _keyHook = null;
        _keyBuzzer = false;
        _buzzerDown.Clear();
        await _crew.StopAsync();
        ReleaseControl();
        ClosePanel();
        CloseLog();
    }

    /// <summary>割り当ての文字列（"N"、"Joy0:5"、両方なら "N|Joy0:5"）を分ける。</summary>
    public static string[] SplitBindings(string? text) => (text ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private readonly HashSet<string> _buzzerDown = new(); // ブザーに割り当てたキー・ボタンのうち、押しているもの

    /// <summary>
    /// 運転士: キーかコントローラーのボタン（割り当ての文字列）を押した・離した。
    /// ブザーは押している間、ほかは同じボタンを車掌のゲームでも押す・離す。
    /// </summary>
    private void OnDriverInputDevice(string binding, bool down)
    {
        if (SplitBindings(_config.DriverBuzzerKey).Contains(binding))
        {
            if (down) _buzzerDown.Add(binding);
            else _buzzerDown.Remove(binding);
            _keyBuzzer = _buzzerDown.Count > 0;
        }

        foreach (var (action, text) in _config.DriverKeys)
            if (DriverInput.Actions.ContainsKey(action) && SplitBindings(text).Contains(binding))
                _driverKeyEvents.Enqueue($"{action}:{(down ? 1 : 0)}");
    }

    /// <summary>運転士: ブザーの割り当て（"N|Joy0:5" のような文字列）。</summary>
    public string DriverBuzzerBindings => _config.DriverBuzzerKey;

    /// <summary>運転士: ボタンの割り当て（ボタン名 → 割り当ての文字列）。</summary>
    public IReadOnlyDictionary<string, string> DriverKeys => _config.DriverKeys;

    /// <summary>運転士: 割り当てを保存する（"Buzzer" はブザー。空は使わない）。</summary>
    public void SetDriverKeys(Dictionary<string, string> bindings)
    {
        _config.DriverBuzzerKey = bindings.GetValueOrDefault("Buzzer") ?? "";
        _config.DriverKeys = bindings.Where(kv => kv.Key != "Buzzer" && kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        _config.Save();
        _buzzerDown.Clear();
        _keyBuzzer = false;
    }

    /// <summary>乗る列番（毎周期）。</summary>
    public void SetTrain(string train) => _crew.SetTrain(train);

    // ---- 車掌の乗務の許可（運転士が「車掌乗務を許可」を入れ、申し込みに「はい」と答えると乗れる） ----

    /// <summary>運転士へ: 車掌の乗務の申し込みが届いた（SignalR のスレッドから呼ばれる）。</summary>
    public event Action<ConductorRequest>? ConductorRequested
    {
        add => _crew.ConductorRequested += value;
        remove => _crew.ConductorRequested -= value;
    }

    /// <summary>運転士へ: 申し込みが取り消された（SignalR のスレッドから呼ばれる）。</summary>
    public event Action<string>? RequestCancelled
    {
        add => _crew.RequestCancelled += value;
        remove => _crew.RequestCancelled -= value;
    }

    /// <summary>車掌へ: 許可の状態が変わった（SignalR のスレッドから呼ばれる）。</summary>
    public event Action<CrewApproval>? ApprovalReceived
    {
        add => _crew.ApprovalReceived += value;
        remove => _crew.ApprovalReceived -= value;
    }

    public void SetAllowConductor(bool allow) => _crew.SetAllow(allow);

    public void AnswerConductor(string requestId, bool accept) => _crew.Answer(requestId, accept);

    /// <summary>車掌: ［確定］を押し直した（断られていたら、また申し込む）。</summary>
    public void Reconfirm() => _crew.Rejoin();

    public void Shutdown()
    {
        _timer.Stop();
        _keyHook?.Dispose();
        _keyHook = null;
        ReleaseControl();
        ClosePanel();
        CloseLog();
        _tci.Dispose();
    }

    // =====================================================================================
    // 車掌用操作盤
    // =====================================================================================

    private void ShowPanel()
    {
        if (_panel != null) return;
        // 前の既定値（D で左の開など。WASD 移動とぶつかる）のままのキーは、1 度だけ今の既定値（数字 2〜0）に移す（自分で変えたキーは残す）
        if (!_config.ConductorKeysMoved)
        {
            foreach (var (name, old) in ConductorPanelForm.OldDefaultKeys)
                if (_config.ConductorKeys.TryGetValue(name, out var saved) && saved == old.ToString())
                    _config.ConductorKeys.Remove(name);
            _config.ConductorKeysMoved = true;
            _config.Save();
        }

        var keys = new Dictionary<string, Keys>();
        foreach (var (name, text) in _config.ConductorKeys)
            if (Enum.TryParse<Keys>(text, out var k)) keys[name] = k;
        _panel = new ConductorPanelForm(keys);
        _panel.DoorAction += OnPanelDoor;
        _panel.SetCatchUpMode(CatchUpModeText(CatchUpMode));
        _panel.CatchUpModeClicked += CycleCatchUpMode;
        _panel.AutoClicked += ToggleAuto;
        _panel.SetAuto(_auto != null);
        _panel.NfbToggled += (action, on) =>
        {
            // 3/4閉・羽衣橋（ドアカット）: 自分のゲームで押し、運転士のゲームでも押す
            _ = _tci.PressAsync(action, 100);
            SendConductor(action);
        };
        _panel.EmergencyChanged += on =>
        {
            _emergency = on;
            _tci.SetAtoNotch(on ? -8 : 0); // 自分のゲームにも（ATO のブレーキは手動と強い方が効く）
            SendConductor("");
        };
        _panel.ReOpenChanged += on =>
        {
            _reOpen = on;
            _tci.SetButton("ReOpenSW", on);
            SendConductor("");
        };
        _panel.JoukouChanged += on =>
        {
            _joukou = on;
            _tci.SetButton("JoukouSokusin", on);
            SendConductor("");
        };
        _panel.BuzzerChanged += on =>
        {
            _buzzer = on;
            UpdateLocalBuzzer(); // 押している間だけ鳴る（自分のゲームにも）
            SendConductor("");
        };
        _panel.KeysChanged += keys =>
        {
            _config.ConductorKeys = keys.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
            _config.Save();
        };
        var b = _config.ConductorPanelBounds.Split(',');
        if (b.Length == 4 && b.All(x => int.TryParse(x, out _)))
            _panel.ApplySavedBounds(new Rectangle(int.Parse(b[0]), int.Parse(b[1]), int.Parse(b[2]), int.Parse(b[3])));
        _panel.Show();
    }

    private void ClosePanel()
    {
        if (_panel == null) return;
        var r = _panel.SavedBounds; // 次に出すときも同じ位置・大きさにする
        _config.ConductorPanelBounds = $"{r.X},{r.Y},{r.Width},{r.Height}";
        _config.Save();
        StopAuto("");
        if (_emergency) _tci.SetAtoNotch(0);
        if (_localBuzzerOn) _tci.SetButton("Buzzer", false);
        _emergency = _reOpen = _joukou = _buzzer = _gameBuzzer = _localBuzzerOn = false;
        _panel.AllowClose = true;
        _panel.Close();
        _panel.Dispose();
        _panel = null;
    }

    private void OnPanelDoor(string action)
    {
        _lastPanelDoorMs = Environment.TickCount64;
        _ = _tci.PressAsync(action, 50);
        SendConductor(action);
    }

    // =====================================================================================

    private void OnDriverInput(DriverInput input)
    {
        // 送り元が替わった（運転士がつなぎ直した）ら、番号を数え直す
        var last = Interlocked.Read(ref _lastSeq);
        if (input.Seq <= last && last - input.Seq < 100_000) return;
        Interlocked.Exchange(ref _lastSeq, input.Seq);
        var now = Environment.TickCount64;
        var prev = Interlocked.Exchange(ref _prevArrivalMs, now);
        if (prev > 0) Interlocked.Exchange(ref _arrivalGapMs, now - prev);
        Interlocked.Exchange(ref _latestInputMs, now);
        _latestInput = input;
        if (input.Action.Length > 0) _driverActions.Enqueue(input.Action); // 運転士がキーで扱ったボタン（毎周期の最新だけでは取りこぼすので、別に並べる）
    }

    /// <summary>乗っている列車の車両ごとの形式（ゲームの CarModel。CarStates の並びのまま。分からなければ空）。</summary>
    public IReadOnlyList<string> CarModels { get; private set; } = [];

    /// <summary>車両ごとの乗車率（ゲームの値のまま）。</summary>
    public IReadOnlyList<float> CarOccupancy { get; private set; } = [];

    /// <summary>全車の乗車率の単純平均（%）。どれか1両でも取得できなければ未取得。</summary>
    public decimal? AverageOccupancyPercent()
    {
        if (CarOccupancy.Count == 0 || CarOccupancy.Count != CarModels.Count) return null;
        decimal sum = 0;
        foreach (var rate in CarOccupancy)
        {
            if (!float.IsFinite(rate) || rate < 0 || rate > int.MaxValue) return null;
            sum += (decimal)rate;
        }
        var scale = _config.OccupancyRateIsRatio ? 100 : 1;
        return sum * scale / CarOccupancy.Count;
    }

    private void Tick()
    {
        var s = _tci.Read();
        if (s is { InGame: true })
        {
            CarModels = s.CarModels ?? [];
            CarOccupancy = s.CarOccupancy ?? [];
        }
        else
        {
            CarModels = [];
            CarOccupancy = [];
        }
        if (_role == CrewRole.Driver) DriverTick(s);
        else ConductorTick(s);
        var now = Environment.TickCount64;
        if (now - _lastViewMs < 100) return;
        _lastViewMs = now;
        UpdateView(s);
    }

    // ---- 運転士 ----

    private void DriverTick(TciState? s)
    {
        var now = Environment.TickCount64;
        foreach (var (binding, down) in _joystick.Poll()) OnDriverInputDevice(binding, down); // コントローラーのボタン
        while (_fromConductor.TryDequeue(out var c))
        {
            _conductorAllClose = c.AllClose;
            _conductorMs = now;
            if (c.AllClose != null) _conductorDistance = c.TraveledDistance;
            ApplyConductorState(c.Emergency, c.ReOpen, c.Joukou, c.Buzzer);
            switch (c.Action)
            {
                case { Length: > 0 }:
                    _ = _tci.PressAsync(c.Action, 50);
                    if (c.Action.StartsWith("DoorOpn")) _lastOpenAction = c.Action;
                    _lastDoorFixMs = now; // 押したばかりなので、しばらく合わせ直さない
                    _doorMismatchSinceMs = 0;
                    break;
            }
        }

        // 車掌の知らせが途絶えたら、押しっぱなしの操作（非常・再開閉・乗降促進・ブザー）を戻す
        if (now - _conductorMs > StaleMs && (_appliedEmergency || _appliedReOpen || _appliedJoukou || _appliedBuzzer))
        {
            ApplyConductorState(false, false, false, false);
            Message?.Invoke("車掌の操作が届かなくなったので、車掌の非常ブレーキ・再開閉・乗降促進・ブザーを戻しました");
        }

        if (s == null || !s.InGame)
        {
            CloseLog(); // 運転画面を出たら、次のシナリオは別のファイルにする
            return;
        }

        // 押した操作が効かなかったとき（1回目の戸閉が効かないことがある）に備えて、
        // 止まっている間に車掌のゲームとドアが食い違ったままなら合わせ直す（閉は左右とも、開は車掌が最後に開けた側。
        // DoorOpn・DoorCls は体のある側だけを扱うので使わない）
        if (_conductorAllClose is { } want && now - _conductorMs < StaleMs && s.Speed < 0.5f && want != s.AllClose
            && (want || _lastOpenAction.Length > 0))
        {
            if (_doorMismatchSinceMs == 0) _doorMismatchSinceMs = now;
            if (now - _doorMismatchSinceMs > DoorMismatchMs && now - _lastDoorFixMs > DoorMismatchMs)
            {
                if (want)
                {
                    _ = _tci.PressAsync("DoorClsLeft", 50);
                    _ = _tci.PressAsync("DoorClsRight", 50);
                }
                else
                {
                    _ = _tci.PressAsync(_lastOpenAction, 50);
                }
                _lastDoorFixMs = now;
                _doorMismatchSinceMs = 0;
                Message?.Invoke("ドアを車掌に合わせました");
            }
        }
        else
        {
            _doorMismatchSinceMs = 0;
        }

        // 運転士が押したブザー: ブザーのキーを押している間（キーはゲームにもそのまま届く）。
        // ゲームのブザーの状態（trainSwitch）は今のゲームでは値が入らないが、入るようになったときのために見ておく
        // （車掌のブザーを自分のゲームで鳴らしている間と、止めた直後は除く）
        var ownBuzzer = _keyBuzzer || (s.Buzzer && !_appliedBuzzer && now - _appliedBuzzerOffMs > 400);

        var game = _game.Latest;
        var input = new DriverInput
        {
            Power = s.Power, Brake = s.Brake, Reverser = s.Reverser, Speed = s.Speed, AllClose = s.AllClose, Buzzer = ownBuzzer,
            TraveledDistance = game?.TraveledDistance ?? 0, NextStationDistance = s.NextStationDistance, NextStation = s.NextStation,
            ClientMs = Environment.TickCount64
        };
        // 運転士がキーで扱ったボタン（勾配起動など）は、1つずつすぐ送る（押した・離した）
        while (_driverKeyEvents.TryDequeue(out var ev))
        {
            if (!HasFollowers) continue;
            _crew.SendDriverInput(new DriverInput
            {
                Seq = ++_seq, Power = input.Power, Brake = input.Brake, Reverser = input.Reverser, Speed = input.Speed, AllClose = input.AllClose,
                Buzzer = input.Buzzer, TraveledDistance = input.TraveledDistance, NextStationDistance = input.NextStationDistance,
                NextStation = input.NextStation, ClientMs = input.ClientMs, Action = ev
            });
        }

        var changed = _lastSent == null || _lastSent.Power != input.Power || _lastSent.Brake != input.Brake
                      || _lastSent.Reverser != input.Reverser || _lastSent.AllClose != input.AllClose || _lastSent.Buzzer != input.Buzzer;
        if (!changed && input.ClientMs - _lastSentMs < (s.Speed > 0.1f ? MovingHeartbeatMs : StoppedHeartbeatMs)) return;
        input.Seq = ++_seq;
        if (HasFollowers) _crew.SendDriverInput(input); // 車掌か添乗が乗っているときだけ送る
        WriteDriverLog(input, s);
        _lastSent = input;
        _lastSentMs = input.ClientMs;
    }

    /// <summary>運転士のゲームに、車掌の非常ブレーキ・再開閉・乗降促進・ブザーの状態を合わせる（変わったときだけ送る）。</summary>
    private void ApplyConductorState(bool emergency, bool reOpen, bool joukou, bool buzzer)
    {
        if (buzzer != _appliedBuzzer)
        {
            _tci.SetButton("Buzzer", buzzer);
            _appliedBuzzer = buzzer;
            if (!buzzer) _appliedBuzzerOffMs = Environment.TickCount64;
        }

        if (emergency != _appliedEmergency)
        {
            _tci.SetAtoNotch(emergency ? -8 : 0);
            _appliedEmergency = emergency;
            Message?.Invoke(emergency ? "車掌が非常ブレーキを入れました" : "車掌の非常ブレーキが切れました");
        }

        if (reOpen != _appliedReOpen)
        {
            _tci.SetButton("ReOpenSW", reOpen);
            _appliedReOpen = reOpen;
        }

        if (joukou != _appliedJoukou)
        {
            _tci.SetButton("JoukouSokusin", joukou);
            _appliedJoukou = joukou;
        }
    }

    // ---- 車掌 ----

    private void ConductorTick(TciState? s)
    {
        if (s == null || !s.InGame)
        {
            _ownAllClose = null;
            CloseLog();
            _fromConductor.Clear(); // 運転画面の外で届いた車掌のドアの操作は、あとで押さない
            return;
        }

        var now = Environment.TickCount64;
        var rider = _role == CrewRole.Rider; // 添乗: 運転士の操作を映すだけ（ドア・ブザーは送らない）

        // 車掌がゲームの中で（キーや車掌スイッチを直接触って）ドアを扱ったら、すぐ運転士のゲームでも同じにする。
        // 読めるのは「全部閉まった」「開いた」だけなので、閉は閉まりきったとき（左右とも）、開は開いたときに駅の開く側で送る。
        // 操作盤で扱った直後は、操作盤の操作として送ってあるので送らない
        if (!rider && _ownAllClose is { } wasClosed && wasClosed != s.AllClose && now - _lastPanelDoorMs > 6000)
        {
            if (s.AllClose)
            {
                SendConductor("DoorClsLeft", s.AllClose);
                SendConductor("DoorClsRight", s.AllClose);
            }
            else if (s.DoorSide.Length > 0)
            {
                SendConductor("DoorOpn" + s.DoorSide, s.AllClose);
            }
        }

        _ownAllClose = s.AllClose;
        _panel?.SyncDoors(s.AllClose, s.DoorSide); // ドアの絵をゲームのドアに合わせる（シナリオの初めから開いているときも）

        // 車掌がゲームのキー・スイッチで押したブザー（自分のゲームのブザーを入れている間と、止めた直後は除く）
        var gameBuzzer = s.Buzzer && !_localBuzzerOn && now - _localBuzzerOffMs > 400;
        if (!rider && gameBuzzer != _gameBuzzer)
        {
            _gameBuzzer = gameBuzzer;
            SendConductor("");
        }

        if (!rider && now - _lastStatusMs > StatusMs) SendConductor(""); // 状態だけ（運転士のゲームが食い違ったら合わせ直す）

        var input = _latestInput;
        var age = now - Interlocked.Read(ref _latestInputMs);
        if (rider)
        {
            // 車掌の操作盤のドアの開・閉は、押した瞬間に自分のゲームでも押す（運転士のゲームと同時に動く）
            while (_fromConductor.TryDequeue(out var c))
                if (c.Action is { Length: > 0 } a && (a.StartsWith("DoorOpn") || a.StartsWith("DoorCls")) && s.Speed < 0.5f)
                    RiderDoor(a, now);
            FollowDriverDoors(input, age, s, now);
        }

        // 運転士がキーで扱ったボタン（勾配起動など）を、自分のゲームでも押す・離す
        while (_driverActions.TryDequeue(out var ev))
        {
            var colon = ev.LastIndexOf(':');
            if (colon <= 0) continue;
            var name = ev[..colon];
            if (!DriverInput.Actions.ContainsKey(name)) continue;
            var down = ev[(colon + 1)..] == "1";
            if (name is DriverInput.DoorClose or DriverInput.DoorOpen)
            {
                // 運転士がゲームのドアのキーを押した: 添乗は、運転士のドアが閉まりきるのを待たずに、同時に動かす（車掌は自分で扱うので使わない）
                if (rider && down && s.Speed < 0.5f)
                {
                    if (name == DriverInput.DoorClose)
                    {
                        RiderDoor("DoorClsLeft", now);
                        RiderDoor("DoorClsRight", now);
                    }
                    else if (s.DoorSide.Length > 0) RiderDoor("DoorOpn" + s.DoorSide, now);
                }
                continue;
            }

            _tci.SetButton(name, down);
            if (down) _heldDriverActions.Add(name);
            else _heldDriverActions.Remove(name);
        }

        if (age >= StaleMs && _heldDriverActions.Count > 0)
        {
            foreach (var name in _heldDriverActions) _tci.SetButton(name, false);
            _heldDriverActions.Clear();
        }

        // 運転士のブザーは、押している間だけ自分のゲームでも鳴らす
        var driverBuzzer = input != null && age < StaleMs && input.Buzzer;
        if (driverBuzzer != _driverBuzzer)
        {
            _driverBuzzer = driverBuzzer;
            UpdateLocalBuzzer();
        }

        // 自動運転: 運転士がいない間だけ。運転士が乗ってきたら切って、運転士の操作に従う
        if (_auto != null && !rider)
        {
            if ((input != null && age < StaleMs) || _crew.Status is { Drivers: > 0 }) StopAuto("運転士が乗ったので、自動運転を切りました");
            else
            {
                AutoTick(s, gameBuzzer, now);
                return;
            }
        }

        if (input != null && age < StaleMs)
        {
            if (_linkBrake) _tci.ForgetSent();
            _linkBrake = false;
            _haveDriver = true;
            _tci.SetReverser(input.Reverser);
            _ownDecel = Decel(_ownSpeeds, now, s.Speed);
            LearnAccel(s, now);
            if (!ReferenceEquals(input, _appliedInput)) _driverDecel = Decel(_driverSpeeds, input.ClientMs, input.Speed);
            var (power, brake) = CatchUp(input, s, age, now);
            _tci.SetNotch(power, brake);

            // ゲームの中でキーやマスコンに触れると、ゲームのノッチが送った値から変わり、運転士がノッチを変えるまでそのままになる。
            // 0.3秒以上違っていたら送り直す（車掌の非常ブレーキ中は除く）
            if (!_emergency && (s.Power != power || s.Brake != brake))
            {
                if (_notchMismatchMs == 0) _notchMismatchMs = now;
                else if (now - _notchMismatchMs > NotchResendMs)
                {
                    // 距離優先で、運転士が使ったことのない強い力行を送ってゲームが受け付けなかったら、ゲームの一番強い段に合わせる
                    if (power > _maxPowerSeen && s.Power > 0 && s.Power < power && s.Brake == 0)
                        _gameMaxPower = Math.Max(_maxPowerSeen, s.Power);

                    _tci.SetNotch(power, brake, force: true);
                    _notchMismatchMs = 0;
                }
            }
            else
            {
                _notchMismatchMs = 0;
            }

            if (!ReferenceEquals(input, _appliedInput))
            {
                _appliedInput = input;
                WriteLog(input, s);
            }
        }
        else if (_haveDriver && !_linkBrake)
        {
            _catchUp = 0;
            // 運転士の操作が届かない: 走り続けないように常用最大ブレーキ（直通非常の B8 は使わない）
            _tci.SetNotch(0, ServiceMaxBrake, force: true);
            _linkBrake = true;
            Message?.Invoke($"運転士の操作が届かないので、ブレーキ（B{ServiceMaxBrake}）をかけました");
        }
    }

    /// <summary>
    /// 添乗: 自分のゲームのドアを、運転士のゲームのドアに合わせる（運転士のゲームのドアは、車掌がいれば車掌の操作で動いている）。
    /// 読めるのは「全部閉まった」「開いた」だけなので、開いたら駅の開く側を開け、閉まりきったら左右とも閉める。
    /// </summary>
    /// <summary>
    /// 運転士の操作（ノッチ・ドアの開閉など）を受け取る人がいるか（車掌か添乗）。
    /// 添乗は、車掌がいない列車（運転士だけ）でも、運転士のゲームのドアに合わせて自分のゲームのドアを開け閉めする。
    /// </summary>
    private void RiderDoor(string action, long now)
    {
        _ = _tci.PressAsync(action, 50);
        _lastDoorFixMs = now; // 押したばかりなので、しばらく（ドアが動き終わるまで）運転士のドアに合わせ直さない
        _doorMismatchSinceMs = 0;
    }

    // ---- 自動運転（車掌モード。運転士がいない列車で、自分のゲームのノッチを自動運転が決める） ----

    private AutoDriver? _auto;
    private bool _autoBuzzer; // 発車の合図（ブザー）が鳴っていたか（鳴り始めを見る）
    private int _autoPower = int.MinValue, _autoBrake = int.MinValue; // 前の周期に送ったノッチ
    private long _autoMismatchMs; // ゲームのノッチが、送ったノッチと違い始めた時刻（0 なら同じ）

    /// <summary>［自動運転］: 入・切。入れられるのは運転士がいないときだけ。</summary>
    private void ToggleAuto()
    {
        if (_auto != null)
        {
            StopAuto("自動運転を切りました（ノッチを中立・常用最大ブレーキに戻しました）");
            return;
        }

        var age = Environment.TickCount64 - Interlocked.Read(ref _latestInputMs);
        if (_role != CrewRole.Conductor) return;
        if ((_latestInput != null && age < StaleMs) || _crew.Status is { Drivers: > 0 })
        {
            Message?.Invoke("運転士が乗っているので、自動運転は使えません");
            return;
        }

        _auto = new AutoDriver();
        _autoBuzzer = true; // 入れた瞬間に鳴っていたブザーでは発車しない
        _autoPower = _autoBrake = int.MinValue;
        _autoMismatchMs = 0;
        _tci.WantStations = true;
        _panel?.SetAuto(true);
        Message?.Invoke("自動運転: ノッチは自動で扱います。ドアは操作盤で扱い、閉めたらブザーで発車の合図をしてください");
    }

    /// <summary>自動運転を切る（入っていなければ何もしない）。ノッチは中立・常用最大ブレーキに戻す。</summary>
    private void StopAuto(string message)
    {
        if (_auto == null) return;
        _auto = null;
        _tci.WantStations = false;
        _tci.SetNotch(0, ServiceMaxBrake, force: true);
        _panel?.SetAuto(false);
        if (message.Length > 0) Message?.Invoke(message);
    }

    private void AutoTick(TciState s, bool gameBuzzer, long now)
    {
        var buzzer = _buzzer || gameBuzzer;
        var departSignal = buzzer && !_autoBuzzer;
        _autoBuzzer = buzzer;
        if (_auto == null || !s.IsDriver || _tci.LastRaw is not { } raw) return;

        // ゲームのキーやマスコンに触れてゲームのノッチが変わっても、自動運転を優先する（0.3秒違っていたら送り直す）
        var mismatch = _autoPower != int.MinValue && (s.Power != _autoPower || s.Brake != _autoBrake) && !_emergency;
        if (!mismatch) _autoMismatchMs = 0;
        else if (_autoMismatchMs == 0) _autoMismatchMs = now;
        var resend = mismatch && now - _autoMismatchMs > NotchResendMs;
        if (resend) _autoMismatchMs = 0;

        var (power, brake) = _auto.Step(s, raw, _tci.Signals(), departSignal, _emergency, now);
        _tci.SetReverser(1, resend);
        _tci.SetNotch(power, brake, resend);
        _autoPower = power;
        _autoBrake = brake;
    }

    private bool HasFollowers => _crew.Status is { } st && (st.Conductors > 0 || st.Riders > 0);

    private void FollowDriverDoors(DriverInput? input, long age, TciState s, long now)
    {
        if (input == null || age >= StaleMs || s.Speed >= 0.5f || input.AllClose == s.AllClose)
        {
            _doorMismatchSinceMs = 0;
            return;
        }

        if (_doorMismatchSinceMs == 0) _doorMismatchSinceMs = now;
        if (now - _doorMismatchSinceMs < RiderDoorMs || now - _lastDoorFixMs < DoorMismatchMs) return;
        if (input.AllClose)
        {
            _ = _tci.PressAsync("DoorClsLeft", 50);
            _ = _tci.PressAsync("DoorClsRight", 50);
        }
        else if (s.DoorSide.Length > 0)
        {
            _ = _tci.PressAsync("DoorOpn" + s.DoorSide, 50);
        }

        _lastDoorFixMs = now;
        _doorMismatchSinceMs = 0;
    }

    private void SendConductor(string action, bool? allClose = null)
    {
        _crew.SendConductorInput(new ConductorInput
        {
            Action = action, AllClose = allClose ?? _ownAllClose ?? true, Emergency = _emergency, ReOpen = _reOpen, Joukou = _joukou,
            Buzzer = _buzzer || _gameBuzzer, TraveledDistance = _game.Latest?.TraveledDistance ?? 0
        });
        _lastStatusMs = Environment.TickCount64;
    }

    private bool _driverBuzzer; // 運転士のブザーが届いている
    private readonly ConcurrentQueue<string> _driverActions = new();
    private readonly HashSet<string> _heldDriverActions = new(); // 運転士のキーで押したままのボタン（運転士の操作が途絶えたら離す）

    /// <summary>自分（車掌）のゲームのブザー: 操作盤で押しているか、運転士のブザーが届いている間だけ入れる。</summary>
    private void UpdateLocalBuzzer()
    {
        var on = _buzzer || _driverBuzzer;
        if (on == _localBuzzerOn) return;
        _tci.SetButton("Buzzer", on);
        _localBuzzerOn = on;
        if (!on) _localBuzzerOffMs = Environment.TickCount64;
    }

    /// <summary>車掌をやめるとき: 送ったノッチを中立・ブレーキに戻しておく（自分のマスコンで動かせるように）。</summary>
    private void ReleaseControl()
    {
        if (_role == CrewRole.Driver)
        {
            ApplyConductorState(false, false, false, false);
            return;
        }

        if (!_haveDriver) return;
        _tci.SetNotch(0, ServiceMaxBrake, force: true);
        _haveDriver = false;
        _latestInput = null;
    }

    // =====================================================================================
    // 表示
    // =====================================================================================

    private static string Notch(int power, int brake, int reverser)
    {
        var r = reverser switch { 1 => "前", -1 => "後", _ => "切" };
        var n = brake >= 8 ? "非常" : brake > 0 ? $"B{brake}" : power > 0 ? $"P{power}" : "N";
        return $"{n}（{r}）";
    }

    private void UpdateView(TciState? s)
    {
        var st = _crew.Status;
        var joined = _crew.IsJoined && st != null && st.TrainNumber.Length > 0;
        LastGap = double.NaN;
        if (_role == CrewRole.Driver)
        {
            if (_conductorDistance is { } cd && _game.Latest is { InGame: true } g && Environment.TickCount64 - _conductorMs < StaleMs && st is { Conductors: > 0 })
                LastGap = cd - g.TraveledDistance;
            Summary = _crew.Message.Length > 0 ? $"　乗務: {_crew.Message}"
                : !joined ? ""
                : (st!.Conductors > 0 ? "　車掌 乗車中" : "") + (st.Riders > 0 ? $"　添乗 {st.Riders}人" : "");
            return;
        }

        if (_panel == null && _role != CrewRole.Rider) return;
        string link;
        var ok = false;
        if (!_tci.Ready) link = $"TrainCrewInput が使えません（{_tci.Error}）";
        else if (_crew.Message.Length > 0) link = _crew.Message;
        else if (!_crew.IsJoined) link = "中継につないでいます…";
        else if (!joined) link = "列車番号を確定すると乗務に入ります";
        else if (_crew.Approval is { State: not ApprovalState.Approved } approval) link = approval.Message; // 運転士の許可を待っている・断られた
        else if (st!.Drivers == 0) link = "運転士がまだいません";
        else
        {
            var input = _latestInput;
            var age = Environment.TickCount64 - Interlocked.Read(ref _latestInputMs);
            if (_linkBrake) link = $"運転士の操作が届きません（B{ServiceMaxBrake}）";
            else if (input == null || age >= StaleMs) link = "運転士の操作を待っています";
            else
            {
                ok = true;
                LastGap = Gap(input, s, age);
                link = string.Create(CultureInfo.InvariantCulture,
                    $"運転士 {Notch(input.Power, input.Brake, input.Reverser)}　ずれ {LastGap:+0.0;-0.0}m{(_catchUpNote.Length > 0 ? "　" + _catchUpNote : "")}");
            }
        }

        if (_auto != null) { link = $"自動運転: {_auto.Note}"; ok = true; }
        if (s is { IsDriver: false }) { link = "ゲームを運転士モードにしてください"; ok = false; }
        if (_role == CrewRole.Rider)
        {
            Summary = $"　添乗: {link}";
            return;
        }

        _panel?.SetLink(joined ? $"列番 {st!.TrainNumber}" : "列番 ―", link, ok);
    }

    // ---- 位置のずれを縮める（車掌） ----

    private const double CatchUpGain = 0.15;    // ずれ 1m あたりに目指す速度の差（km/h。20m 後ろなら 3km/h 速く）
    private const double CatchUpMaxDv = 6;      // 目指す速度の差の上限（km/h）
    private const double CatchUpGainLow = 0.5;  // 低速（20km/h 以下）での、ずれ 1m あたりに目指す速度の差（km/h）
    private const double CatchUpDeadband = 1.5; // これより小さいずれは縮めない（m）
    private const double CatchUpMinSpeed = 3;   // 運転士・車掌のどちらかがこれより遅いときは足し引きしない（km/h。発車の間際）
    private const double BrakeMinSpeed = 1;     // ブレーキ中の寄せは、これより遅くなるまで続ける（km/h）
    private const double BrakeMinDecel = 0.5;   // 運転士の減速度がこれより弱い間（ブレーキの効き始め）は、速度の差で寄せる（km/h/s）
    private const int BrakeStepMs = 350;        // ブレーキ中に段を変えたら、これだけ待つ（ブレーキが効くまでの遅れ）
    private const double BrakeDecelTolerance = 0.15; // 要る減速度との差がこれより小さければ、段を変えない（km/h/s）
    private const int BrakeMaxOff = 2;          // ブレーキ中に足し引きする段数の上限
    private const double BrakeGapGain = 0.4;      // ブレーキ中、ずれ 1m あたりに目指す速度の差（km/h）
    private const double BrakeGapFadeStart = 20;  // ブレーキ中、運転士がこの速度を下回ったら、ずれを縮める分を小さくしていく（km/h）
    private const double BrakeGapFadeEnd = 5;     // 同じく、この速度で 0 にする（ここからは速度の下がり方を運転士にそろえるだけ）
    private const double BrakeSpeedTau = 1.5;     // 目指す速度との差を、何秒で戻すつもりで減速度を決めるか
    private const int NotchResendMs = 300;      // ゲームのノッチが送った値とこれだけ違っていたら、送り直す
    private const int CatchUpHoldMs = 800;      // 足し引きを変えたら、これだけは続ける（加速の側だけ。減速の側はすぐ）
    private const double LimitMargin = 1.5;     // 加速の側に足すのは、制限速度・ATS の速度よりこれだけ下まで（km/h）
    private const double PlanDecel = 2.0;       // 先の制限まで減速しきれるかを見るときの減速度（km/h/s。ゆるめに見る）
    private const double PlanMargin = 20;       // 同じく、減速しきるのに要る距離に足す余裕（m）
    private const double PatternMaxDv = 3;      // ATS のパターン（P・停P・終端P）が出ている間、運転士より速くしてよい分（km/h）
    private const double DistGain = 2.0;        // 距離優先: ずれ 1m あたりに目指す速度の差（km/h）
    private const double DistMaxDv = 30;        // 距離優先: 目指す速度の差の上限（km/h。出してよい速度はこれとは別に守る）
    private const double DistStopDecel = 2.5;   // 距離優先: 止まる位置をめがけるときに使うつもりの減速度（km/h/s。B7 はおよそ 3.6）
    private const int GuessMaxPower = 5;        // 距離優先: 力行の一番強い段の見込み（ゲームがこれより低い段までしかなければ、ゲームの値に合わせる）
    private const double DistStopTolerance = 0.5; // 距離優先: これより小さいずれは縮めない（m）
    private const double DistCreepSpeed = 25;   // 距離優先: 運転士が止まっているときに、後ろから詰める速度の上限（km/h）
    private const double DistTau = 1.5;         // 距離優先: 目指す速度との差を、何秒で戻すつもりで加速度を決めるか
    private const double DistCreepTau = 1.0;    // 距離優先: 止まっている運転士へ詰めるときの同じ値（短いほどきびきび）
    private const double DistKeepTolerance = 0.2; // 距離優先: 今の段の加速度が、欲しい加速度とこれより近ければ段を変えない（km/h/s）
    private const int DistStepMs = 200;         // 距離優先: 段を変えたら、これだけは続ける
    private const double AtsFreeSpeed = 1000;   // ATS の速度がこれ以上（3000 など）なら、ATS は今の制限速度を見張っていない（超えても止められない）
    private const double AtsBelow = 3;          // ATS が見張っている区間は、ATS の速度（制限速度 +5）よりこれだけ下を制限とみなす（km/h）

    /// <summary>
    /// 車掌のゲームへ送るノッチ。寄せ方は操作盤で切り替える（<see cref="CatchUpMode"/>）。
    /// 【タイミング優先】ふだんは運転士のノッチのまま。位置がずれていたら、ずれに応じた速度の差を目指して1〜2段足し引きする
    /// （後ろにいれば力行を足すかブレーキをゆるめ、前にいれば力行を減らすかブレーキを足す）。
    /// ・ブレーキ中は、速度の下がり方を運転士にそろえて同じ時刻に止まる（ずれは 5km/h までに縮め、縮めきれなければ位置のずれを残す）
    /// ・低速ほど、同じずれでも大きな速度の差を目指す
    /// ・運転士がブレーキを使っている間は、ゆるめても N まで（力行にはしない）。常用最大・非常のときは触らない
    /// 【距離優先】運転士のノッチにしばられず、力行の一番強い段から B7 までを使って、ずれが 0 になる速度を目指す（<see cref="DistanceNotch"/>）。
    /// 【両方に共通】
    /// ・加速の側は、制限速度・ATS の速度より少し下まで、先の制限までに減速しきれる間だけ。ATS の「接近」・ATS のブレーキのときは加速しない
    ///   （ATS のブレーキが動くと、ゲームが勝手に止めてずれがかえって広がるため）。ATS のパターン（P・停P・終端P）が出ている間は、
    ///   運転士より 3km/h 速いところまで
    /// ・制限速度を超えそうなら、運転士のノッチにかかわらず N、超えたら B1、ATS の「接近」なら B2 以上にする
    /// </summary>
    private (int Power, int Brake) CatchUp(DriverInput input, TciState s, long ageMs, long now)
    {
        if (input.Power > _maxPowerSeen) _maxPowerSeen = input.Power;
        var mode = CatchUpMode;
        var scale = input.Power > 0 ? input.Power : -input.Brake; // 運転士のノッチ（力行はプラス、ブレーキはマイナスの1本の目盛り）
        var doorsShut = input.AllClose && s.AllClose && !_emergency;

        // 寄せない: 切・レバーサーが中立・（タイミング優先で）運転士が常用最大か非常・（距離優先で）運転士が非常で走っている・ドアが開いている
        // （距離優先では、運転士が常用最大で止まるときも、止まった運転士の位置をめがけて止まる）
        var driverHardBrake = mode == "distance" ? input.Brake > ServiceMaxBrake && input.Speed > 0.5f : input.Brake >= ServiceMaxBrake;
        if (mode == "off" || input.Reverser == 0 || driverHardBrake || (mode == "distance" && !doorsShut))
        {
            ResetCatchUp();
            return (input.Power, input.Brake);
        }

        // 守る制限速度: ATS が見張っている区間は ATS の速度（制限速度 +5）より少し下、見張っていない区間（ATS の速度が 3000 など）は無し
        // （フリー運転では 110km/h を超えても止められない）。ATS の速度が読めなければ、制限速度
        var atsFree = s.AtsSpeed >= AtsFreeSpeed;
        var limit = atsFree ? 999f : s.AtsSpeed > 0 ? (float)(s.AtsSpeed - AtsBelow) : s.SpeedLimit > 0 ? s.SpeedLimit : 999f;
        var atsWarning = s.AtsBrake || s.AtsState.Contains("接近");
        var atsPattern = !atsWarning && s.AtsState.Length > 0 && s.AtsState != "無表示"; // P・停P・終端P（パターンがあるだけ）

        // 制限速度と先の制限から、今出してよい速度（先の制限まで、ゆるめの減速度で減速しきれる速度）。
        // 先の制限は ATS が見張るか分からないので見張るとみなすが、後ろにいる自分が運転士と同じ速度なら大丈夫なので、運転士の速度までは出してよい
        var vAllow = (double)limit - LimitMargin;
        if (s.NextSpeedLimit >= 0 && s.NextSpeedLimitDistance >= 0)
        {
            var vn = Math.Max(0, s.NextSpeedLimit + 5 - AtsBelow - LimitMargin);
            var d = Math.Max(0, s.NextSpeedLimitDistance - PlanMargin);
            var vPlan = Math.Sqrt(vn * vn + 7.2 * PlanDecel * d);
            if (vPlan < vAllow) vAllow = Math.Max(vPlan, Math.Min(input.Speed, vAllow));
        }

        var canSpeedUp = !atsWarning && s.Speed < vAllow;
        var gap = Gap(input, s, ageMs);
        int adj;

        if (mode == "distance")
        {
            _brakeMode = false;
            _brakeOff = 0;
            adj = DistanceNotch(input, s, ageMs, now, gap, vAllow, atsWarning, atsPattern);
            _catchUp = adj - scale;
        }
        else if (doorsShut && input.Brake > 0 && input.Power == 0 && input.Speed > BrakeMinSpeed && s.Speed > BrakeMinSpeed && _driverDecel > BrakeMinDecel)
        {
            _distMode = false;
            // ブレーキ中（止まる時刻をそろえる）: 「運転士の今の速度＋ずれを縮める分」を目指す速度にし、
            // 運転士の減速度に、目指す速度との差を戻す分を足した減速度を目指す。自分の減速度がそれより弱ければブレーキを1段足し、
            // 強ければ1段ゆるめる。ずれを縮める分は運転士が遅くなるほど小さくし、止まる間際は 0 にする（速度の下がり方を運転士にそろえて、
            // 同じ時刻に止まる。縮めきれなかったずれは、そのまま残す）。
            // ブレーキは効くまで遅れがあるので、段を変えたら少し待つ。運転士がブレーキを変えれば、足した段ごと一緒に動く
            if (!_brakeMode)
            {
                _brakeMode = true;
                _brakeOff = Math.Clamp(-_catchUp, -1, 1);
                _brakeOffMs = now;
            }

            var vd = Math.Max(0, input.Speed - _driverDecel * Math.Min(ageMs, 1000) / 1000.0);
            var fade = Math.Clamp((vd - BrakeGapFadeEnd) / (BrakeGapFadeStart - BrakeGapFadeEnd), 0, 1);
            var dvGap = Math.Abs(gap) < CatchUpDeadband ? 0 : Math.Clamp(-gap * BrakeGapGain, -CatchUpMaxDv, CatchUpMaxDv) * fade;
            if (atsPattern) dvGap = Math.Min(dvGap, PatternMaxDv);
            var need = _driverDecel + (s.Speed - (vd + dvGap)) / BrakeSpeedTau;
            var err = (double.IsNaN(_ownDecel) ? need : _ownDecel) - need; // プラスなら効きすぎ
            if (now - _brakeOffMs >= BrakeStepMs)
            {
                var before = _brakeOff;
                if (err < -BrakeDecelTolerance) _brakeOff++;
                else if (err > BrakeDecelTolerance) _brakeOff--;
                if (_brakeOff != before) _brakeOffMs = now;
            }

            _brakeOff = Math.Clamp(_brakeOff, -Math.Min(input.Brake, BrakeMaxOff), BrakeMaxOff);
            if (!canSpeedUp) _brakeOff = Math.Max(_brakeOff, 0); // ATS・制限速度の近くでは、運転士よりゆるめない
            _catchUp = -_brakeOff;
            adj = -Math.Clamp(input.Brake + _brakeOff, 0, ServiceMaxBrake);
        }
        else
        {
            _distMode = false;
            _brakeMode = false;
            _brakeOff = 0;

            // 力行・惰行中（とブレーキのかけ始め）: ずれに応じた速度の差を目指して、足し引きする段数を決める。
            // 遅いほど、同じずれでも大きな速度の差を目指す（低速のまま止まると、ずれが縮まらずに残るため）
            var want = 0;
            if (doorsShut && input.Speed > CatchUpMinSpeed && s.Speed > CatchUpMinSpeed)
            {
                var low = Math.Clamp((60 - Math.Min(input.Speed, s.Speed)) / 40, 0, 1); // 60km/h 以上で 0、20km/h 以下で 1
                var gain = CatchUpGain + (CatchUpGainLow - CatchUpGain) * low;
                var tolerance = 1.5 - 0.8 * low;
                var dvTarget = Math.Abs(gap) < CatchUpDeadband ? 0 : Math.Clamp(-gap * gain, -CatchUpMaxDv, CatchUpMaxDv);
                if (atsPattern) dvTarget = Math.Min(dvTarget, PatternMaxDv);
                var err = s.Speed - input.Speed - dvTarget; // マイナスなら遅すぎる
                want = err < -4 ? 2 : err < -tolerance ? 1 : err > 4 ? -2 : err > tolerance ? -1 : 0;
            }

            if (!canSpeedUp) want = Math.Min(want, 0);
            if (want < _catchUp || !canSpeedUp || now - _catchUpMs >= CatchUpHoldMs)
            {
                if (want != _catchUp) _catchUpMs = now;
                _catchUp = want;
            }

            adj = Math.Clamp(scale + _catchUp, -ServiceMaxBrake, Math.Max(scale, _maxPowerSeen));
            if (input.Brake > 0) adj = Math.Min(adj, 0);
        }

        // 制限速度・ATS の守り（運転士のノッチにかかわらず）
        var guard = "";
        if (atsWarning) { adj = Math.Min(adj, -2); guard = "ATS"; }
        else if (s.Speed > limit + 1) { adj = Math.Min(adj, -1); guard = "制限速度"; }
        else if (s.Speed > limit - 1) { adj = Math.Min(adj, 0); guard = "制限速度"; }

        var diff = adj - scale;
        _catchUpNote = guard.Length > 0 && diff < 0 ? $"{guard}で減速" : diff > 0 ? "寄せ 加速" : diff < 0 ? "寄せ 減速" : "";
        return adj > 0 ? (adj, 0) : (0, -adj);
    }

    /// <summary>寄せ方（"time" タイミング優先・"distance" 距離優先・"off" 切）。</summary>
    private string CatchUpMode => !_config.CrewCatchUp ? "off" : _config.CrewCatchUpMode == "distance" ? "distance" : "time";

    private static string CatchUpModeText(string mode) => mode switch { "distance" => "距離優先", "off" => "切", _ => "タイミング優先" };

    /// <summary>操作盤の寄せの切り替え: タイミング優先 → 距離優先 → 切 → タイミング優先。</summary>
    private void CycleCatchUpMode()
    {
        var next = CatchUpMode switch { "time" => "distance", "distance" => "off", _ => "time" };
        _config.CrewCatchUp = next != "off";
        if (next != "off") _config.CrewCatchUpMode = next;
        _config.Save();
        ResetCatchUp();
        _panel?.SetCatchUpMode(CatchUpModeText(next));
    }

    private void ResetCatchUp()
    {
        _catchUp = 0;
        _brakeMode = false;
        _brakeOff = 0;
        _distMode = false;
        _catchUpNote = "";
    }

    /// <summary>
    /// 距離優先のノッチ（力行はプラス、ブレーキはマイナス）。とにかく運転士のいる位置（±5m）を目指して走る。
    /// ・運転士のノッチにしばられない。運転士が N や定速で走っていても、後ろにいれば力行して追いつく。
    ///   力行は一番強い段（見込み P5。ゲームが受け付けなければその段まで）、ブレーキは B7 まで使う
    /// ・目指す速度は「運転士の速度＋ずれ 1m あたり 2km/h（上限 30km/h）」。今出してよい速度（制限速度・先の制限・ATS）は超えない。
    ///   ATS のパターンが出ていれば、運転士より 3km/h 速いところまで
    /// ・運転士がブレーキで止まろうとしている間と、止まったあとは、運転士が止まる位置（今の減速度から予測）をめがけて止まる。
    ///   その位置で止まるのに要る減速度を、そのまま出す（2.5km/h/s で止まれる速度までは、目指す速度で寄っていく）
    /// ・運転士が止まっている所の手前で止まったら、25km/h までで詰めてまた止まる
    /// ・運転士が止まった状態から力行で出るときは、運転士のノッチで一緒に出る
    /// 段は、1段ずつ動かして様子を見るのではなく、段ごとの加速度（走りながら覚える）から、欲しい加速度にいちばん近い段を選ぶ
    /// （少しずつ動かすと、加速しすぎ・減速しすぎを繰り返して離されるため）。
    /// </summary>
    private int DistanceNotch(DriverInput input, TciState s, long ageMs, long now, double gap, double vAllow, bool atsWarning, bool atsPattern)
    {
        var scale = input.Power > 0 ? input.Power : -input.Brake;
        var maxPower = Math.Max(_maxPowerSeen, _gameMaxPower);
        if (!_distMode)
        {
            _distMode = true;
            _distNotch = scale;
            _distNotchMs = now;
        }

        // 一緒に発車する
        if (input.Power > 0 && input.Speed < CatchUpMinSpeed && s.Speed < CatchUpMinSpeed && Math.Abs(gap) < 2)
        {
            _distNotch = scale;
            _distNotchMs = now;
            return scale;
        }

        var driverDecel = double.IsNaN(_driverDecel) ? 0 : _driverDecel;
        var driverStopped = input.Speed < 0.5f && input.Power == 0;
        var driverStopping = !driverStopped && input.Power == 0 && input.Brake > 0 && driverDecel > BrakeMinDecel;
        var vd = Math.Max(0, input.Speed - Math.Max(0, driverDecel) * Math.Min(ageMs, 1000) / 1000.0);
        double v = s.Speed;

        // 運転士の位置に追いつく速度
        var vTarget = Math.Max(0, vd + (Math.Abs(gap) < DistStopTolerance ? 0 : Math.Clamp(-gap * DistGain, -DistMaxDv, DistMaxDv)));
        if (atsPattern) vTarget = Math.Min(vTarget, vd + PatternMaxDv);
        vTarget = Math.Min(vTarget, vAllow);

        var driverAccel = driverStopped ? 0 : -driverDecel;
        if (vTarget < vd) driverAccel = Math.Min(driverAccel, 0); // 運転士より遅くするときは、運転士の加速に合わせない
        var wantAccel = driverAccel + (vTarget - v) / DistTau; // km/h/s

        // 運転士が止まろうとしている・止まっている: 運転士が止まる位置をめがけて止まる
        if (driverStopped || driverStopping)
        {
            var driverStop = driverStopping ? vd * vd / (7.2 * driverDecel) : 0; // 運転士があと何 m で止まるか
            var toStop = driverStop - gap; // 自分が止まる位置まで（m）

            // 止まっていてよいときは、止まったままにする（運転士と同じか、B3 以上のブレーキ）
            if (v < 0.5 && toStop < DistStopTolerance)
            {
                _distNotch = -Math.Max(Math.Min(input.Brake, ServiceMaxBrake), 3);
                _distNotchMs = now;
                return _distNotch;
            }

            var vStop = Math.Sqrt(7.2 * DistStopDecel * Math.Max(0, toStop)); // 止まる位置まで、使うつもりの減速度で止まれる速度
            if (toStop <= 0.1) wantAccel = -9; // 行き過ぎ: いちばん強く
            else if (v >= vStop - 1) wantAccel = -v * v / (7.2 * toStop); // 止まるのに要る減速度をそのまま出す
            else
            {
                var vT = Math.Min(vStop, driverStopped ? Math.Min(vAllow, DistCreepSpeed) : vTarget);
                wantAccel = Math.Min(wantAccel, (vT - v) / (driverStopped ? DistCreepTau : DistTau));
            }
        }

        if (atsWarning) wantAccel = Math.Min(wantAccel, -1);

        // 欲しい加速度にいちばん近い段（今の段で足りていれば変えない。変えたら少しは続ける）
        var band = SpeedBand(v);
        var current = Math.Clamp(_distNotch, -ServiceMaxBrake, maxPower);
        if (now - _distNotchMs >= DistStepMs && Math.Abs(ModelAccel(current, band) - wantAccel) > DistKeepTolerance)
        {
            var best = current;
            var bestErr = Math.Abs(ModelAccel(current, band) - wantAccel);
            for (var n = -ServiceMaxBrake; n <= maxPower; n++)
            {
                var err = Math.Abs(ModelAccel(n, band) - wantAccel);
                if (err < bestErr - 0.05) { best = n; bestErr = err; }
            }

            if (best != _distNotch)
            {
                _distNotch = best;
                _distNotchMs = now;
            }
        }

        if (v >= vAllow || atsWarning) _distNotch = Math.Min(_distNotch, 0); // 出してよい速度に届いたら、力行はしない
        return _distNotch;
    }

    private static int SpeedBand(double speed) => Math.Clamp((int)(speed / 20), 0, 6);

    /// <summary>段 n（力行はプラス、ブレーキはマイナス）での加速度（km/h/s）。覚えた値がなければ、前の記録から測った見込み。</summary>
    private double ModelAccel(int n, int band) => _accelModel.TryGetValue((n, band), out var a) ? a : DefaultAccel(n, band);

    private static double DefaultAccel(int n, int band)
    {
        if (n < 0) return -(0.55 + 0.43 * -n); // B1 1.0 … B7 3.6
        if (n == 0) return -0.15;
        double[] p5 = { 2.4, 2.6, 2.5, 2.0, 1.6, 1.15, 0.8 }; // P5 の加速度（0・20・40…120km/h〜）
        double[] share = { 0, 0.02, 0.23, 0.6, 0.8, 1.0 };    // P1〜P5 の、P5 に対する割合
        return p5[band] * (n < share.Length ? share[n] : 1.0);
    }

    /// <summary>ゲームのノッチが 1 秒以上同じ間、そのときの加速度を段ごと・速度ごとに覚える（坂や車種の違いも、走りながら合っていく）。</summary>
    private void LearnAccel(TciState s, long now)
    {
        var n = s.Power > 0 ? s.Power : -s.Brake;
        if (n != _modelNotch)
        {
            _modelNotch = n;
            _modelNotchSinceMs = now;
            return;
        }

        if (now - _modelNotchSinceMs < 1000 || s.Brake > ServiceMaxBrake || s.Speed < 3 || s.AtsBrake || _emergency || double.IsNaN(_ownDecel)) return;
        var key = (n, SpeedBand(s.Speed));
        var a = ModelAccel(n, key.Item2);
        _accelModel[key] = a + 0.03 * (-_ownDecel - a);
    }

    /// <summary>
    /// 運転士の列車に対する、自分（車掌）の列車の位置のずれ（m。前にいればプラス）。
    /// 同じ駅に向かっている間は、毎フレーム更新される「次の駅までの残り距離」どうしで比べる
    /// （運転士の値は、届いてからの経過時間ぶん速度で進めておく）。比べられなければ、0.5秒ごとの「走った距離」で比べる。
    /// </summary>
    private double Gap(DriverInput input, TciState? s, long ageMs)
    {
        if (s != null && input.NextStation.Length > 0 && input.NextStation == s.NextStation)
        {
            var driverRemaining = input.NextStationDistance - MovedSince(input, ageMs);
            return driverRemaining - s.NextStationDistance;
        }

        return (_game.Latest?.TraveledDistance ?? 0) - input.TraveledDistance;
    }

    /// <summary>運転士の操作が届いてから、運転士の列車が進んだはずの距離（m）。ブレーキ中は減速しながら進んだとして見る。</summary>
    private double MovedSince(DriverInput input, long ageMs)
    {
        var t = Math.Min(ageMs, 1000) / 1000.0;
        var decel = double.IsNaN(_driverDecel) || _driverDecel < 0 || input.Brake == 0 ? 0 : _driverDecel;
        var stopT = decel > 0 ? input.Speed / decel : double.MaxValue;
        t = Math.Min(t, stopT);
        return (input.Speed * t - decel * t * t / 2) / 3.6;
    }

    /// <summary>速度の記録に今の速度を足し、0.6秒ほど前からの減速度（km/h/s。減速でプラス）を返す。記録が短すぎれば NaN。</summary>
    private static double Decel(Queue<(long Ms, float Speed)> q, long nowMs, float speed)
    {
        q.Enqueue((nowMs, speed));
        while (q.Count > 2 && nowMs - q.Peek().Ms > 600) q.Dequeue();
        var (ms, v) = q.Peek();
        var dt = (nowMs - ms) / 1000.0;
        return dt < 0.25 ? double.NaN : (v - speed) / dt;
    }

    // ---- 運転の記録（CSV。この exe のフォルダの crew-logs にだけ残す） ----

    /// <summary>記録の置き場（exe のフォルダの crew-logs）。</summary>
    private static string LogDir => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "crew-logs");

    /// <summary>まだ開いていなければ、新しいファイルを開いて見出しを書く。</summary>
    private StreamWriter OpenLog(string kind, string dia, string header)
    {
        if (_log != null) return _log;
        Directory.CreateDirectory(LogDir);
        _log = new StreamWriter(Path.Combine(LogDir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{kind}_{SafeName(dia)}.csv"), false, new UTF8Encoding(true)) { AutoFlush = true };
        _log.WriteLine(header);
        return _log;
    }

    /// <summary>運転士: 自分の操作と位置（走行中0.1秒ごと・止まっている間0.5秒ごと・操作が変わったとき）。</summary>
    private void WriteDriverLog(DriverInput input, TciState s)
    {
        try
        {
            OpenLog("運転士", s.DiaName, "時刻,列番,P,B,R,速度,走った距離,次の駅,次の駅までの残り,戸閉").WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.Now:HH:mm:ss.fff},{s.DiaName},{input.Power},{input.Brake},{input.Reverser},{input.Speed:0.00},{input.TraveledDistance:0.00},{input.NextStation},{input.NextStationDistance:0.00},{(input.AllClose ? 1 : 0)}"));
        }
        catch
        {
            CloseLog();
        }
    }

    /// <summary>車掌: 届いた運転士の操作と、自分の列車の状態・ずれ。</summary>
    private void WriteLog(DriverInput input, TciState s)
    {
        try
        {
            var log = OpenLog("車掌", s.DiaName, "時刻,届いた番号,運転士P,運転士B,運転士R,運転士速度,運転士距離,運転士残り,運転士戸閉,車掌P,車掌B,車掌速度,車掌距離,車掌残り,車掌戸閉,ずれ,寄せ,制限,先の制限,先の制限まで,ATS速度,ATS表示,ATSブレーキ,運転士減速度,車掌減速度,寄せ方");
            var game = _game.Latest;
            log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.Now:HH:mm:ss.fff},{input.Seq},{input.Power},{input.Brake},{input.Reverser},{input.Speed:0.00},{input.TraveledDistance:0.00},{input.NextStationDistance:0.00},{(input.AllClose ? 1 : 0)}," +
                $"{s.Power},{s.Brake},{s.Speed:0.00},{game?.TraveledDistance ?? 0:0.00},{s.NextStationDistance:0.00},{(s.AllClose ? 1 : 0)},{Gap(input, s, 0):0.00},{_catchUp},{s.SpeedLimit:0},{s.NextSpeedLimit:0},{s.NextSpeedLimitDistance:0},{s.AtsSpeed:0},{s.AtsState},{(s.AtsBrake ? 1 : 0)},{_driverDecel:0.00},{_ownDecel:0.00},{CatchUpModeText(CatchUpMode)}"));
        }
        catch
        {
            CloseLog();
        }
    }

    private void CloseLog()
    {
        try { _log?.Dispose(); } catch { }
        _log = null;
    }

    private static string SafeName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(s.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray());
    }
}
