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
