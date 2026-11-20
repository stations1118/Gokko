using TrainCrew;

namespace Gokko.Client;

/// <summary>運転操作の読み取り結果（TrainCrewInput の GetTrainState から、乗務の中継に要るものだけ）。</summary>
public sealed record TciState(
    bool InGame,
    bool IsDriver,
    int Power,
    int Brake,
    int Reverser,
    float Speed,
    bool AllClose,
    string DiaName,
    float NextStationDistance,
    string NextStation,
    bool Buzzer,
    string DoorSide,
    float SpeedLimit,
    float NextSpeedLimit,
    float NextSpeedLimitDistance,
    float AtsSpeed,
    string AtsState,
    bool AtsBrake,
    string[]? CarModels = null,
    float[]? CarOccupancy = null);

// Buzzer: ゲームの中でブザーが鳴っている（trainSwitch の運転士ブザー buzzerM・車掌ブザー buzzerC のどちらか。ゲームのキーやスイッチで押したときも入る）
// DoorSide: 今の駅（止まっている・向かっている駅）でドアが開く側（"Left" / "Right"。分からなければ ""）
// SpeedLimit: 今の制限速度（km/h）。NextSpeedLimit・NextSpeedLimitDistance: 先の制限速度とそこまでの距離（なければ -1）
// CarModels・CarOccupancy: 車両ごとの形式と乗車率（ゲームの CarState.CarModel・occupancyRate。CarStates の並びのまま。
//   放送装置で、聞く人が乗っている車両のスピーカーの音にする。運転士は前の端、車掌は後ろの端の車両）
// AtsSpeed: ATS の速度（ゲームの ATS_Speed。数でなければ -1）。AtsState: ATS の表示（ふだんは「無表示」）。AtsBrake: ATS のブレーキが動いている

/// <summary>
/// TRAIN CREW の公式の入出力ライブラリ（TrainCrewInput.dll）。ノッチ・レバーサー・ボタンをゲームへ送り、運転操作を読む。
/// 運転画面の操作だけに使う。DLL の呼び出しは UI のスレッドからだけ行う。
/// </summary>
public sealed class TciLink : IDisposable
{
    private bool _ready;
    private int _lastPower = int.MinValue, _lastBrake = int.MinValue, _lastReverser = int.MinValue;

    /// <summary>DLL が使えないときの理由（使えれば空）。</summary>
    public string Error { get; private set; } = "";

    public bool Ready => _ready;

    /// <summary>最後に <see cref="Read"/> で読んだゲームの値そのもの（AI 運転士が、TciState にない停車駅の並び・時刻を使う）。</summary>
    public TrainState? LastRaw { get; private set; }

    private long _lastSignalRequestMs = -10_000;
    private long _lastStaRequestMs = -10_000;

    /// <summary>
    /// 前方の信号機（名前・現示・距離）。信号機の情報は頼まないと届かないので、届いていなければ5秒ごとに頼み直す。
    /// 届かなければ空（AI 運転士は、制限速度と ATS だけを見る）。
    /// </summary>
    public IReadOnlyList<SignalInfo> Signals()
    {
        if (!_ready) return [];
        try
        {
            var list = TrainCrewInput.signals?.ToList() ?? [];
            var now = Environment.TickCount64;
            if (list.Count == 0 && now - _lastSignalRequestMs > 5000)
            {
                _lastSignalRequestMs = now;
                TrainCrewInput.RequestData(DataRequest.Signal);
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    public void Start()
    {
        if (_ready) return;
        try
        {
            TrainCrewInput.Init();
            _ready = true;
            Error = "";
        }
        catch (Exception ex)
        {
            Error = ex.GetType().Name;
        }
    }

    public TciState? Read()
    {
        if (!_ready) return null;
        try
        {
            var gs = TrainCrewInput.gameState;
            var s = TrainCrewInput.GetTrainState();
            LastRaw = s;
            if (s == null) return null;
            // 停車駅の一覧は頼まないと届かない（実機で確認: 頼むまで stationList が空）。空なら3秒ごとに頼む
            if ((s.stationList == null || s.stationList.Count == 0) && Environment.TickCount64 - _lastStaRequestMs > 3000)
            {
                _lastStaRequestMs = Environment.TickCount64;
                TrainCrewInput.RequestStaData();
            }
            var inGame = gs.gameScreen is GameScreen.MainGame or GameScreen.MainGame_Pause;
            var sw = TrainCrewInput.trainSwitch;
            var buzzer = sw != null && (sw.buzzerM || sw.buzzerC);
            var side = "";
            if (s.stationList != null && s.nowStaIndex >= 0 && s.nowStaIndex < s.stationList.Count)
                side = s.stationList[s.nowStaIndex].doorDir == DoorDir.LeftSide ? "Left" : "Right";
            return new TciState(inGame, gs.crewType == CrewType.Driver, s.Pnotch, s.Bnotch, s.Reverser, s.Speed, s.AllClose,
                s.diaName ?? "", s.nextStaDistance, s.nextStaName ?? "", buzzer, side,
                s.speedLimit, s.nextSpeedLimit, s.nextSpeedLimitDistance,
                float.TryParse(s.ATS_Speed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ats) ? ats : -1,
                s.ATS_State ?? "", s.Lamps != null && s.Lamps.TryGetValue(PanelLamp.ATS_BrakeApply, out var atsBrake) && atsBrake,
                s.CarStates?.Select(c => c.CarModel ?? "").ToArray(),
                s.CarStates?.Select(c => c.occupancyRate).ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>ノッチを送る（変わったときだけ。force なら同じ値でも送る）。力行・ブレーキとも正の値（ゲームの Pnotch・Bnotch と同じ）。</summary>
    public void SetNotch(int power, int brake, bool force = false)
    {
        if (!_ready || (!force && power == _lastPower && brake == _lastBrake)) return;
        try
        {
            TrainCrewInput.SetNotch(power, brake);
            _lastPower = power;
            _lastBrake = brake;
        }
        catch
        {
            // 次に変わったときに送り直す
        }
    }

    public void SetReverser(int dir, bool force = false)
    {
        if (!_ready || (!force && dir == _lastReverser)) return;
        try
        {
            TrainCrewInput.SetReverser(dir);
            _lastReverser = dir;
        }
        catch
        {
            // 次に変わったときに送り直す
        }
    }

    /// <summary>ATO のノッチ（手動と重ねる。ブレーキは手動と強い方、力行は手動が N のときだけ）。車掌の非常ブレーキは -8。</summary>
    public void SetAtoNotch(int notch)
    {
        if (!_ready) return;
        try
        {
            TrainCrewInput.SetATO_Notch(notch);
        }
        catch
        {
            // 次の状態の知らせで送り直す
        }
    }

    /// <summary>送ったノッチの記録を捨てる（次は同じ値でも送る）。</summary>
    public void ForgetSent() => _lastPower = _lastBrake = _lastReverser = int.MinValue;

    /// <summary>ボタン（"DoorOpnLeft"、"DoorClsRight"、"Buzzer" など InputAction の名前）を押す・離す。</summary>
    public bool SetButton(string action, bool on)
    {
        if (!_ready || !Enum.TryParse<InputAction>(action, out var a)) return false;
        try
        {
            TrainCrewInput.SetButton(a, on);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>ボタンを押して、少しあとに離す。</summary>
    public async Task PressAsync(string action, int holdMs = 200)
    {
        if (!SetButton(action, true)) return;
        await Task.Delay(holdMs);
        SetButton(action, false);
    }

    public void Dispose()
    {
        if (!_ready) return;
        _ready = false;
        try
        {
            TrainCrewInput.Dispose();
        }
        catch
        {
            // 終了時のエラーは無視する
        }
    }
}
