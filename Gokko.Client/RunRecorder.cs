using Gokko.Common;
using TrainCrew;

namespace Gokko.Client;

/// <summary>
/// ランカーブ（1駅間の走り方）を手元にためて、駅に止まったら1つにまとめて渡す（<see cref="Completed"/>）。走りながらは何も送らない。
/// ・走り出したら記録を始め、停目の近く（残り 30m 以内）に 2秒止まったら終える。途中の信号待ちは続けて記録する
/// ・行はブレーキ中は 0.2秒ごと、ほかは 2秒ごとか、ノッチ・制限・番線・信号機・軌道回路が変わったとき
/// ・運転画面を出た・ダイヤが変わった・長すぎる（15分か 3000行）ときは、そこまでで捨てる（止まった位置がないと使えないため）
/// </summary>
public sealed class RunRecorder
{
    private const float MovingSpeed = 1f;      // これより速くなったら走り始めとみなす（km/h）
    private const float StoppedSpeed = 0.3f;   // これより遅ければ止まっている（km/h）
    private const int StopHoldMs = 2000;       // 止まってから、これだけたったら記録を終える
    private const double StationWithin = 30;   // 停目まで、これより近く止まったら駅に止まったとみなす（m）
    private const int BrakeStepMs = 200;       // ブレーキ中の行の間隔
    private const int CoastStepMs = 2000;      // ほかの行の間隔
    private const int MaxPoints = 3000;
    private const int MaxMs = 15 * 60 * 1000;

    private RunCurve? _run;
    private long _startMs, _lastPointMs, _stoppedMs;
    private int _lastP = -1, _lastB = -1;
    private double _lastLimit = double.NaN;
    private string? _lastTrack, _lastScenarioTrack, _lastSignal;
    private List<string>? _lastCircuits;
    private string _from = "";
    private string _dia = "";

    /// <summary>1駅間の記録ができた（止まった駅まで）。</summary>
    public event Action<RunCurve>? Completed;

    /// <summary>記録を捨てる（自動運転を切った・乗務をやめたなど）。次は走り出したところから。</summary>
    public void Reset()
    {
        _run = null;
        _from = "";
    }

    /// <summary>1周期ぶん。mode は "auto" か "manual"。gain は自動運転の覚えた効き方、stopOffset は駅ごとの補正（人の運転はどちらも 0）。</summary>
    public void Step(string mode, TciState s, TrainState raw, StopDistanceResult stop, IReadOnlyList<SignalInfo> signals,
        IReadOnlyCollection<string> circuits, string train, double gain, double stopOffset, long now)
    {
        if (!s.InGame || s.DiaName != _dia)
        {
            _dia = s.InGame ? s.DiaName : "";
            Reset();
            if (!s.InGame) return;
        }

        if (_run == null)
        {
            if (s.Speed < MovingSpeed || !s.AllClose) return;
            var cars = raw.CarStates ?? [];
            _run = new RunCurve
            {
                Mode = mode, Train = train, Dia = s.DiaName, From = _from, Cars = cars.Count,
                Model = cars.Count > 0 ? cars[0].CarModel ?? "" : "", Models = cars.Select(c => c.CarModel ?? "").ToList(),
                Start = raw.NowTime.ToString(@"hh\:mm\:ss")
            };
            _startMs = now;
            _lastPointMs = long.MinValue;
            _stoppedMs = 0;
            _lastP = _lastB = -1;
            _lastLimit = double.NaN;
            _lastTrack = _lastScenarioTrack = _lastSignal = null;
            _lastCircuits = null;
        }

        var run = _run;
        if (run.Mode != mode) run.Mode = "mixed"; // 途中で自動運転を入れた・切った

        // 行を足す
        var track = stop.Kind == StopDistanceKind.None ? "" : stop.Track;
        var scenario = stop.Kind == StopDistanceKind.None ? "" : stop.ScenarioTrack;
        var signal = stop.Signal ?? "";
        var circuitsChanged = _lastCircuits == null || !_lastCircuits.SequenceEqual(circuits);
        var changed = s.Power != _lastP || s.Brake != _lastB || s.SpeedLimit != _lastLimit
                      || track != _lastTrack || scenario != _lastScenarioTrack || signal != _lastSignal || circuitsChanged;
        var step = s.Brake > 0 ? BrakeStepMs : CoastStepMs;
        if (changed || now - _lastPointMs >= step)
        {
            run.Points.Add(new RunPoint
            {
                T = Math.Round((now - _startMs) / 1000.0, 2),
                X = Math.Round(raw.TotalLength, 2),
                V = Math.Round(s.Speed, 2),
                P = s.Power,
                B = s.Brake,
                G = Math.Round(raw.gradient, 2),
                L = s.SpeedLimit,
                N = Math.Round(s.NextStationDistance, 2),
                R = Math.Round(stop.Remaining, 2),
                K = (int)stop.Kind,
                Rough = Math.Round(stop.Rough, 2),
                Track = track != _lastTrack ? track : null,
                ScenarioTrack = scenario != _lastScenarioTrack ? scenario : null,
                Signal = signal != _lastSignal ? signal : null,
                SignalDistance = stop.SignalDistance is { } sd ? Math.Round(sd, 2) : null,
                Circuits = circuitsChanged ? circuits.ToList() : null
            });
            _lastPointMs = now;
            _lastP = s.Power;
            _lastB = s.Brake;
            _lastLimit = s.SpeedLimit;
            _lastTrack = track;
            _lastScenarioTrack = scenario;
            _lastSignal = signal;
            if (circuitsChanged) _lastCircuits = circuits.ToList();
        }

        if (run.Points.Count > MaxPoints || now - _startMs > MaxMs)
        {
            Reset();
            return;
        }

        // 止まった
        if (s.Speed >= StoppedSpeed)
        {
            _stoppedMs = 0;
            return;
        }

        if (_stoppedMs == 0) _stoppedMs = now;
        if (now - _stoppedMs < StopHoldMs || stop.Kind == StopDistanceKind.None || Math.Abs(stop.Remaining) > StationWithin) return; // 信号待ちなどは続ける

        run.To = stop.Station;
        run.StopPosName = raw.stationList is { } list && raw.nowStaIndex >= 0 && raw.nowStaIndex < list.Count ? list[raw.nowStaIndex].StopPosName ?? "" : "";
        run.Track = stop.Track;
        run.ScenarioTrack = stop.ScenarioTrack;
        run.End = raw.NowTime.ToString(@"hh\:mm\:ss");
        run.StopRemaining = Math.Round(stop.Remaining, 2);
        run.StopKind = stop.Kind.ToString();
        run.StopRough = Math.Round(stop.Rough, 2);
        run.StopTraveled = Math.Round(raw.TotalLength, 2);
        run.StopSignals = signals.Select(x => new RunSignal { Name = x.name ?? "", Phase = x.phase ?? "", Distance = Math.Round(x.distance, 2) }).ToList();
        run.StopCircuits = circuits.ToList();
        run.Gain = Math.Round(gain, 3);
        run.StopOffset = Math.Round(stopOffset, 2);
        _run = null;
        _from = stop.Station;
        Completed?.Invoke(run);
    }
}
