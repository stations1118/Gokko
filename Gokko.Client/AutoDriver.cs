using System.Globalization;
using TrainCrew;

namespace Gokko.Client;

/// <summary>
/// 自動運転（車掌モードの［自動運転］。運転士がいない列車で、自分のゲームのノッチを決める）。サーバーには何も聞かない。
/// ・止まる位置: 次の停車駅の停目までの残り（<see cref="StopDistance"/> が出す。入る番線・両数に合った停目。通過駅は使わない）。
///   確定（信号機から出した値）か、シナリオどおりの番線なら停目をめがけ、シナリオと違う番線でおおまかなうちは少し手前をめがける
/// ・発車: ドアが閉まっていて、車掌のブザー（発車の合図）が鳴り始めたら。運転停車（ドアを開けない停車）は発車時刻に出る
/// ・走り方: 次の停車駅の着時刻に間に合う平均の速さより少し速い速度まで力行して惰行する。遅れていれば制限速度いっぱいまで
/// ・守るもの: 制限速度・先の制限・ATS の速度と「接近」・前方の停止信号・注意（Y）・警戒（YY）・減速（YG）の信号機を過ぎる速度
/// ・止まり方: 停目の手前で速度を落とさず、走ってきた速さのまま停止パターン（B5 の目安）に当たったところで入れ始める。
///   段を変えたら効ききるまで 2秒保ち、そのあと実際の減速度で止まる位置を見込んで、はっきりずれたときだけ段を変える（B5 → B3 のように下げる）。
///   8km/h からは B2 で止める（B1 は抑速ブレーキの列車があるので使わない）。目安と実際の効き方の比は走りながら覚える
/// 常用最大は B7（B8 は直通非常なので使わない）。
/// </summary>
public sealed class AutoDriver
{
    private const int MaxBrake = 7;            // 常用最大（B8 は直通非常なので使わない）
    private const int HoldBrake = 4;           // 止まっている間のブレーキ
    private const int MaxPower = 5;            // 力行の一番強い段の見込み
    private const double LimitMargin = 2;      // 制限速度・ATS の速度より、これだけ下を上限にする（km/h）
    private const double PlanDecel = 2.0;      // 先の制限・停止信号まで減速しきれるかを見るときの減速度（km/h/s）
    private const double PlanMargin = 20;      // 同じく、減速しきるのに要る距離に足す余裕（m）
    private const double PatternDecel = 2.6;   // 減速しきる点まで、この減速度が要るようになったらブレーキを入れ始める（km/h/s。B5 の目安 2.8 に少し余裕）
    private const double BrakeLag = 0.8;       // ブレーキが効き始めるまでの遅れ（秒）。この間に進む分を、残り距離から引いて見る
    private const int HoldMs = 2000;           // 段を変えたら、これだけは保つ（ブレーキシリンダの圧が上がって効ききるまで。行ったり来たりしない）
    private const double StopTolerance = 1.5;  // 保ったあと、このままの減速度で止まる位置の見込みがこれ以上ずれたら段を変える（m）
    private const double FinalSpeed = 8;       // この速度（km/h）からは B2 で止める（B1 は抑速ブレーキの列車があるので使わない）
    private const int FinalNotch = 2;
    private const double SteadyFactor = 1.1;   // 効ききったあとの減速度 ÷ 目安（目安は効き始めの遅れ込みの平均なので、効ききると少し強い）
    private const double StepBand = 0.1;       // 段を選ぶときの余裕（km/h/s）
    private const double SignalMargin = 15;    // 停止信号の、これだけ手前に止まる（m）
    private const double RoughMargin = 10;     // シナリオと違う番線で、停目までの残りがおおまかなうちは、これだけ手前をめがける（m。確定すれば外す）
    private const double JumpMeters = 2;       // 停目までの残りが1周期でこれ以上変わったら、すぐ段を合わせ直す（m）
    private const double CoastBand = 8;        // 惰行で、目指す速度よりこれだけ下がったら、また力行する（km/h）
    private const double ScheduleFactor = 1.25; // 間に合う平均の速さに掛ける（加速・減速の分、巡航は平均より速くする）
    private const double MinCruise = 35;       // 目指す速度の下限（km/h）
    private const double StopBias = 0.4;       // 停目より、これだけ先をめがける（試験で、どの駅も 0.3〜1.0m 手前に止まったため。m）
    private const double YellowSpeed = 45;     // 注意（Y）の信号機を過ぎるときの速度（km/h）
    private const double DoubleYellowSpeed = 25; // 警戒（YY）の信号機を過ぎるときの速度（km/h）
    private const double YellowGreenSpeed = 80; // 減速（YG）の信号機を過ぎるときの速度（km/h）

    /// <summary>
    /// 段ごとの減速度の目安（km/h/s。平坦・VVVF 車で、この段でブレーキをかけ続けて止まれる値。ブレーキの遅れ込み）。
    /// 「残り何両でこの速度ならこの段で止まれる」の目安（1両 20m）から出した値で、運転の記録の値とも合う。添字が段（0・1 は使わない）
    /// </summary>
    private static readonly double[] BrakeDecel = [0, 0, 1.0, 1.65, 2.27, 2.8, 3.4, 3.8];

    private int _lastStopIndex = -2;           // 最後に止まった（発車を待っている）停車駅の番号。-2 はまだ決めていない
    private bool _departOk;                    // 発車してよい（ブザーが鳴った・運転停車の発車時刻）
    private bool _powering;
    private double _signalCap = double.PositiveInfinity; // 注意・警戒の信号機を過ぎてから、次の信号機が見えるまでの速度の上限
    private bool _stopping;
    private int _brake;
    private long _brakeMs;
    private double _lastTarget = double.NaN; // 前の周期の、止まる位置までの残り（m）
    private long _lastTargetMs;
    private double _gain = 1.0;                // 実際の減速度 ÷ 目安（走りながら覚える）
    private List<Station> _stations = [];
    private readonly Queue<(long Ms, float Speed)> _speeds = new();

    /// <summary>覚えたブレーキの効き方（実際の減速度 ÷ 目安。ランカーブに残す）。</summary>
    public double Gain => _gain;

    /// <summary>今していること（操作盤に出す）。</summary>
    public string Note { get; private set; } = "";

    /// <summary>
    /// 1周期ぶん。返すのは力行・ブレーキの段（どちらかは 0）。
    /// stop: 停目までの残り（<see cref="StopDistance.Update"/> の結果）。departSignal: 車掌のブザーが今鳴り始めた。emergency: 車掌の非常ブレーキが入っている。
    /// </summary>
    public (int Power, int Brake) Step(TciState s, TrainState raw, IReadOnlyList<SignalInfo> signals, StopDistanceResult stop, bool departSignal, bool emergency, long now)
    {
        var accel = Accel(now, s.Speed); // km/h/s（減速はマイナス）
        // 停車駅の一覧は途中で届かなくなることがあるので、前に届いたものを使い続ける
        if (raw.stationList is { Count: > 0 } list) _stations = list.ToList();
        var stations = _stations;
        var pos = raw.TotalLength;
        if (stations.Count == 0) return Hold("停車駅の一覧を待っています", MaxBrake); // 届くまでは走らない（止まる駅が分からない）

        if (_lastStopIndex == -2)
        {
            // 始めたとき: 止まっていて、近くに停車駅があれば、その駅で発車を待っているものとする
            _lastStopIndex = -1;
            if (s.Speed < 0.5f)
                for (var i = 0; i < stations.Count; i++)
                    if (stations[i].stopType != StopType.Passing && Math.Abs(stations[i].TotalLength - pos) < 30) _lastStopIndex = i;
        }

        if (emergency)
        {
            _departOk = false; // 緩めたあとは、また発車の合図（ブザー）を待つ
            _stopping = false;
            return Hold("車掌の非常ブレーキ", MaxBrake);
        }

        // 次の停車駅（通過駅は飛ばす）
        var target = -1;
        for (var i = Math.Max(0, _lastStopIndex + 1); i < stations.Count; i++)
        {
            if (stations[i].stopType == StopType.Passing) continue;
            if (_lastStopIndex < 0 && stations[i].TotalLength < pos - 15) continue;
            target = i;
            break;
        }

        var dStop = double.PositiveInfinity;
        var targetName = "";
        var confirmed = true;
        if (target >= 0)
        {
            var st = stations[target];
            targetName = st.Name ?? "";
            // 停目までの残りが、この駅のものならそれを使う（止まった直後は、まだ止まった駅の残りが来る）。なければゲームの値
            double meters = s.NextStation == targetName ? s.NextStationDistance : st.TotalLength - pos;
            if (stop.Kind != StopDistanceKind.None && StopDistance.ParseStopPos(st.StopPosName ?? "", targetName).Station == stop.Station)
            {
                meters = stop.Remaining;
                confirmed = stop.Kind == StopDistanceKind.Confirmed || stop.Track == stop.ScenarioTrack;
            }
            dStop = meters + (confirmed ? StopBias : -RoughMargin);
        }

        // 止まっている
        if (s.Speed < 0.3f && !_powering)
        {
            // 停目に着いた（おおまかなら、余裕を取って手前に止まったところも着いたとみなす）
            if (target >= 0 && dStop < 15)
            {
                _lastStopIndex = target;
                _departOk = false;
                _stopping = false;
                return Hold(confirmed ? $"{targetName} に停車（残り {dStop - StopBias:0.0}m）" : $"{targetName} に停車（停目の位置はおおまか）", HoldBrake);
            }

            if (target < 0) return Hold("終点", HoldBrake);

            // 発車を待つ
            if (_lastStopIndex >= 0 && !_departOk)
            {
                var here = stations[_lastStopIndex];
                if (departSignal && s.AllClose) _departOk = true;
                else if (here.stopType == StopType.StopForOperation && s.AllClose && Seconds(here.DepTime - raw.NowTime) <= 0) _departOk = true;
                if (!_departOk)
                {
                    var wait = Seconds(here.DepTime - raw.NowTime);
                    var dep = wait > 0 ? $"発車まで {wait:0}秒" : "発車時刻を過ぎています";
                    return Hold(!s.AllClose ? $"ドアが開いています（{dep}）" : $"発車の合図（ブザー）を待っています（{dep}）", HoldBrake);
                }
            }
        }

        if (!s.AllClose) return Hold("ドアが開いています", MaxBrake);

        // 前方の停止信号
        var dSignal = double.PositiveInfinity;
        var signalName = "";
        signals = OnRoute(signals);
        foreach (var sig in signals)
        {
            if (!IsStop(sig.phase) || sig.distance < 0) continue;
            if (sig.distance - SignalMargin < dSignal)
            {
                dSignal = sig.distance - SignalMargin;
                signalName = sig.name ?? "";
            }
        }

        // 注意（Y）・警戒（YY）・減速（YG）の信号機: その先の信号機は停止かもしれない（信号機は手前 200〜350m からしか見えない）。
        // 信号機までに決めた速度まで落とし、過ぎたら次の信号機が見えるまでその速度を超えない
        var first = signals.Where(x => x.distance >= 0).OrderBy(x => x.distance).FirstOrDefault();
        var vSignal = double.PositiveInfinity;
        if (first != null)
        {
            var cap = Caution(first.phase);
            if (double.IsInfinity(cap)) _signalCap = double.PositiveInfinity; // 次の信号機が進行
            else
            {
                vSignal = Math.Sqrt(cap * cap + 7.2 * PlanDecel * Math.Max(0, first.distance - 10));
                if (first.distance < 30) _signalCap = cap;
            }
        }
        vSignal = Math.Min(vSignal, _signalCap);

        // 今出してよい速度（制限速度・ATS・先の制限）
        var limit = s.SpeedLimit > 0 ? s.SpeedLimit : 120f;
        if (s.AtsSpeed > 0) limit = Math.Min(limit, s.AtsSpeed);
        var vAllow = limit - LimitMargin;
        if (s.NextSpeedLimit >= 0 && s.NextSpeedLimitDistance >= 0)
        {
            var vn = Math.Max(0, s.NextSpeedLimit - LimitMargin);
            vAllow = Math.Min(vAllow, Math.Sqrt(vn * vn + 7.2 * PlanDecel * Math.Max(0, s.NextSpeedLimitDistance - PlanMargin)));
        }
        vAllow = Math.Min(vAllow, vSignal);
        var atsWarning = s.AtsBrake || s.AtsState.Contains("接近");
        // ATS のパターン（P・停P・終端P）が出ている間は力行しない
        var atsPattern = !atsWarning && s.AtsState.Length > 0 && s.AtsState != "無表示";

        // 止まる位置（停目か停止信号の近い方）
        var dTarget = Math.Min(dStop, dSignal);
        var what = dSignal < dStop ? $"{signalName} の手前" : targetName;
        // 1周期で進んだ分より大きく変わった: 停目までの残りが付け直された（信号機が見えて確定した、など）か、停止信号が出た・消えた
        var jumped = !double.IsNaN(_lastTarget) && Math.Abs(dTarget - (_lastTarget - s.Speed / 3.6 * (now - _lastTargetMs) / 1000.0)) > JumpMeters;
        _lastTarget = double.IsInfinity(dTarget) ? double.NaN : dTarget;
        _lastTargetMs = now;
        // 止まり方: 停目の手前の「減速しきる点」（そこから B2 で FinalSpeed から止まりきれる距離 df）までに FinalSpeed へ落とし、そこから B2 で止める。
        // need は減速しきる点までに要る減速度（効き始めの遅れの間に進む分を引く）。FinalSpeed 以下では停目までに要る減速度
        var v = (double)s.Speed;
        var df = FinalSpeed * FinalSpeed / (7.2 * Steady(FinalNotch));
        var dMain = dTarget - df - v / 3.6 * BrakeLag;
        var need = v > FinalSpeed
            ? (dMain > 0.3 ? (v * v - FinalSpeed * FinalSpeed) / (7.2 * dMain) : 99)
            : (dTarget > 0.1 ? v * v / (7.2 * dTarget) : (v > 0.5 ? 99 : 0));

        if (dTarget < -1 && s.Speed > 0.5f) return Hold($"{what}を行き過ぎました", MaxBrake);

        if (!_stopping && !double.IsInfinity(dTarget) && (v > FinalSpeed ? need >= PatternDecel : need >= Steady(FinalNotch) * 0.9))
        {
            // 走ってきた速さのまま、停止パターンに当たったところで入れ始める（最初の段は目安から。ふつうは B5）
            _stopping = true;
            _brake = 0;
            SetBrake(v > FinalSpeed ? NotchFor(need) : FinalNotch, now);
        }

        // 止まる先が先へ跳んだ（停止信号が進行に変わった・停目までの残りが付け直された）: ブレーキをやめる
        if (_stopping && jumped && need < 1.0 && v > 10) _stopping = false;

        if (_stopping)
        {
            _powering = false;
            if (s.Speed < 0.3f)
            {
                _stopping = false;
                return Hold($"{what}に停車（残り {dTarget:0.0}m）", HoldBrake);
            }

            var since = now - _brakeMs;
            if (v > FinalSpeed)
            {
                if (jumped)
                {
                    SetBrake(Math.Max(NotchFor(need), FinalNotch), now); // 残りが付け直された: 保つ時間を待たずに選び直す
                }
                else if (since >= HoldMs && !double.IsNaN(accel) && accel < -0.2)
                {
                    // 効ききった: 今の減速度のままなら、減速しきる点のどれだけ手前・先で FinalSpeed になるか。はっきりずれたときだけ段を変える
                    var meas = -accel;
                    Learn(meas, v);
                    var room = dTarget - df;
                    var err = room - (v * v - FinalSpeed * FinalSpeed) / (7.2 * meas); // プラスなら手前で落ちきる（効きすぎ）
                    if (err < -StopTolerance) SetBrake(Math.Min(_brake + 1, MaxBrake), now);
                    else if (err > StopTolerance && room > 0.3)
                    {
                        // 効きすぎ: 足りる一番弱い段まで下げる（B5 → B3 のように飛んでよい）
                        var want = (v * v - FinalSpeed * FinalSpeed) / (7.2 * room);
                        var n = FinalNotch;
                        while (n < MaxBrake && Steady(n) < want + StepBand) n++;
                        if (n < _brake) SetBrake(n, now);
                    }
                }
                else if (since >= 500 && need > Decel(_brake) + 0.8)
                {
                    SetBrake(Math.Min(_brake + 1, MaxBrake), now); // 効く前でも、明らかに足りない（残りが急に縮んだなど）
                }
            }
            else
            {
                // 最後は B2 で止める（B1 は抑速ブレーキの列車があるので使わない）。B2 で止まりきれないときだけ強い段を残す
                // B2 で止まれる距離（ゆるめているときは、効き始めるまでに進む分も）
                var b2 = v * v / (7.2 * Steady(FinalNotch)) + (_brake == 0 ? v / 3.6 * BrakeLag : 0);
                if (_brake > FinalNotch && b2 <= dTarget + 0.3) SetBrake(FinalNotch, now);
                else if (_brake == FinalNotch && b2 > dTarget + 1.0 && since >= 1000) SetBrake(FinalNotch + 1, now);
                else if (_brake == FinalNotch && b2 < dTarget - 1.0 && v > 2 && since >= 1000) SetBrake(0, now); // 手前で止まりそう: 少しゆるめて転がす
                else if (_brake == 0 && b2 >= dTarget - 0.3) SetBrake(FinalNotch, now);
            }

            if (atsWarning) _brake = Math.Max(_brake, 4);
            Note = string.Create(CultureInfo.InvariantCulture, $"{what}に止まります（残り {dTarget:0.0}m{(confirmed ? "" : "・おおまか")}、B{_brake}）");
            return (0, _brake);
        }

        // 間に合う平均の速さから、目指す速度を決める
        var cruise = vAllow;
        var late = "";
        if (target >= 0 && !double.IsInfinity(dStop))
        {
            var left = Seconds(stations[target].ArvTime - raw.NowTime);
            if (left > 5)
            {
                var vNeed = dStop / left * 3.6 * ScheduleFactor;
                cruise = Math.Min(vAllow, Math.Max(MinCruise, vNeed));
                late = $"着まで {left:0}秒";
            }
            else
            {
                late = "遅れ";
            }
        }

        // 止まる位置（停目・停止信号）の手前で減速しきれる速度
        if (!double.IsInfinity(dSignal)) cruise = Math.Min(cruise, Math.Sqrt(7.2 * PlanDecel * Math.Max(0, dSignal - PlanMargin)));
        if (s.Speed < 0.5f && cruise < 5) return Hold(dSignal < dStop ? $"{signalName} の信号待ち" : "停目の手前で止まっています", HoldBrake);

        if (atsWarning || s.Speed > vAllow + 1)
        {
            // 少し超えただけなら B2（下り坂で、強いブレーキと惰行を行ったり来たりしないように）。大きく超えたら強く
            _powering = false;
            var over = s.Speed - vAllow;
            var b = atsWarning ? 5 : over > 15 ? 7 : over > 8 ? 6 : over > 5 ? 5 : over > 2.5 ? 3 : 2;
            Note = atsWarning ? "ATS で減速" : "制限速度で減速";
            return (0, b);
        }

        if (atsPattern && s.Speed > 15) cruise = Math.Min(cruise, s.Speed - 1); // 止まっているときは、パターンが出ていても発車する
        if (_powering && s.Speed >= cruise) _powering = false;
        else if (!_powering && s.Speed <= cruise - CoastBand) _powering = true;
        else if (!_powering && s.Speed < 3 && cruise > 5) _powering = true; // 発車

        if (_powering)
        {
            var p = s.Speed < cruise - 10 ? MaxPower : 3;
            Note = string.Create(CultureInfo.InvariantCulture, $"力行 → {cruise:0}km/h（{targetName}　{late}）");
            return (p, 0);
        }

        Note = string.Create(CultureInfo.InvariantCulture, $"惰行（{targetName}　{late}）");
        return (0, 0);
    }

    /// <summary>止める・止めたままにする。</summary>
    private (int, int) Hold(string note, int brake)
    {
        _powering = false;
        _lastTarget = double.NaN;
        Note = note;
        return (0, brake);
    }

    private void SetBrake(int brake, long now)
    {
        if (brake == _brake) return;
        _brake = brake;
        _brakeMs = now;
    }

    /// <summary>この段で見込める減速度（入れてから止まるまでの平均。効き始めの遅れ込み。目安 × 覚えた比）。</summary>
    private double Decel(int notch) => BrakeDecel[Math.Clamp(notch, 2, MaxBrake)] * _gain;

    /// <summary>この段が効ききったあとの減速度（目安 × 効ききった分 × 覚えた比）。</summary>
    private double Steady(int notch) => BrakeDecel[Math.Clamp(notch, 2, MaxBrake)] * SteadyFactor * _gain;

    /// <summary>要る減速度に足りる一番弱い段（B2〜B7）。</summary>
    private int NotchFor(double need)
    {
        for (var n = 2; n <= MaxBrake; n++)
            if (Decel(n) >= need + StepBand) return n;
        return MaxBrake;
    }

    /// <summary>効ききった減速度（段を変えて HoldMs たったあと）から、目安との比を覚える（10km/h 以上のときだけ。止まる間際は効き方が変わる）。</summary>
    private void Learn(double decel, double speed)
    {
        if (_brake < 2 || speed < 10) return;
        var ratio = Math.Clamp(decel / (BrakeDecel[_brake] * SteadyFactor), 0.6, 1.5);
        _gain = Math.Clamp(_gain * 0.8 + ratio * 0.2, 0.6, 1.5);
    }

    /// <summary>停止の現示か（R・停止）。</summary>
    private static bool IsStop(string? phase) =>
        phase is { Length: > 0 } p && (p.StartsWith("R", StringComparison.OrdinalIgnoreCase) || p.Contains("停止") || p.Equals("Stop", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 並んで建っている信号機（同じ場所の番線ごとの場内など）のうち、自分の進路のものだけを残す。
    /// ゲームは並んでいる信号機をすべて返すので、同じ距離（±10m）の中で一番進める現示を取る。どれも停止なら停止。
    /// </summary>
    private static List<SignalInfo> OnRoute(IReadOnlyList<SignalInfo> signals)
    {
        var result = new List<SignalInfo>();
        foreach (var sig in signals.Where(x => x.distance >= 0).OrderBy(x => x.distance))
        {
            var same = result.FindIndex(x => Math.Abs(x.distance - sig.distance) < 10);
            if (same < 0) result.Add(sig);
            else if (Rank(sig.phase) > Rank(result[same].phase)) result[same] = sig;
        }
        return result;
    }

    /// <summary>現示の進める順（停止 0 ＜ 警戒 ＜ 注意 ＜ 減速 ＜ 進行）。</summary>
    private static int Rank(string? phase) => phase switch
    {
        "YY" => 1,
        "Y" => 2,
        "YG" => 3,
        "G" => 4,
        _ => IsStop(phase) ? 0 : 2
    };

    /// <summary>注意・警戒・減速の現示なら、その信号機を過ぎるときの速度（それ以外は上限なし）。</summary>
    private static double Caution(string? phase) => phase switch
    {
        "Y" => YellowSpeed,
        "YY" => DoubleYellowSpeed,
        "YG" => YellowGreenSpeed,
        _ => double.PositiveInfinity
    };

    /// <summary>時刻の差（秒）。日付をまたいだら、近い方に直す。</summary>
    private static double Seconds(TimeSpan diff)
    {
        var s = diff.TotalSeconds;
        if (s < -12 * 3600) s += 24 * 3600;
        else if (s > 12 * 3600) s -= 24 * 3600;
        return s;
    }

    /// <summary>1秒ほど前からの加速度（km/h/s）。記録が短すぎれば NaN。</summary>
    private double Accel(long now, float speed)
    {
        _speeds.Enqueue((now, speed));
        while (_speeds.Count > 2 && now - _speeds.Peek().Ms > 1000) _speeds.Dequeue();
        var (ms, v) = _speeds.Peek();
        var dt = (now - ms) / 1000.0;
        return dt < 0.5 ? double.NaN : (speed - v) / dt;
    }
}
