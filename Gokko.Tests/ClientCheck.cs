using Gokko.Client;
using TrainCrew;

namespace Gokko.Tests;

/// <summary>
/// クライアントの自動運転の決まり。Gokko.Client の AutoDriver.cs・StopDistance.cs をそのまま取り込み、ゲームの値を作って動かす
/// （TRAIN CREW の部品 TrainCrewInput.dll は、列車の状態を入れる型として使うだけ。ゲームは呼ばない）。ゲームもサーバーも要らない（1秒かからない）。
/// --data と、既定の走らせる試験の初めに見る。
///
/// 決まり:
/// ・入れたときにどの駅で発車を待っているか（StopDistance.StoppedAt）: ゲームの「次の駅」は、ゲームの残りで見る
/// ・引上線から出る始発（1667。新野崎5番線 → 入換 → 4番線の停目に止まる → 新井川へ）を、入れてから4番線の停目に止まるまで通して走らせる。
///   新野崎の停目（359m）に止まること。新井川を目指して4番線の停目を過ぎない（本番 2026-11-28: 5番線を出た時点で「新井川」を目指していて、
///   4番線の停目を 52m 過ぎた）。停車駅の一覧に引上線の停目がある・ない・通過、自動運転を入換の進路が開く前に入れる・開いたあとに入れる、
///   の組み合わせで見る
/// </summary>
public static class ClientCheck
{
    public static int Run()
    {
        var bad = new List<string>();
        void Expect(bool ok, string what) { if (!ok) bad.Add(what); }

        Expect(!StopDistance.StoppedAt("新野崎", 0, 0.07, "新野崎", 359.29), "引上線にいる（ゲームの残り 359m）のに、新野崎に止まっていることにしない");
        Expect(StopDistance.StoppedAt("新野崎", 359.36, 359.15, "新野崎", 0.21), "新野崎の停目に止まっていれば（ゲームの残り 0.2m）、新野崎に止まっている");
        Expect(StopDistance.StoppedAt("新野崎", 359.36, 359.15, "新井川", 1154), "ゲームの次の駅でない駅は、一覧の位置で見る（残り 0.2m なら止まっている）");
        Expect(!StopDistance.StoppedAt("新井川", 1513, 359.15, "新野崎", 0.21), "先の駅（新井川）には止まっていない");

        foreach (var siding in new[] { "停車", "なし", "通過" })
        foreach (var startAt in new[] { 0.0, 22.0 })
        {
            var (pos, note) = RunShunt(siding, startAt);
            Expect(Math.Abs(pos - 359) < 3,
                $"引上線から4番線への入換（一覧の引上線: {siding}、自動運転を入れた: {(startAt == 0 ? "進路が開く前" : "開いたあと")}）: 新野崎の停目 359m に止まる（止まった所 {pos:0.0}m、{note}）");
        }

        Console.WriteLine($"クライアントの自動運転: {(bad.Count == 0 ? "合格" : $"不合格 {bad.Count}件")}");
        foreach (var b in bad) Console.WriteLine($"  ✗ {b}");
        return bad.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// 新野崎5番線（引上線、走った距離 0）から、入換で4番線の停目（359m）へ。20秒で入換の進路が開き（ゲームの「次の駅」が引上線から新野崎に変わる）、
    /// 25秒で車掌のブザー。4番線の出発信号機（370m）は進行（止まる所は停目だけで決まる）。止まった所と、自動運転の最後の状態を返す。
    /// siding: 停車駅の一覧の「新野崎引上線」が 停車・なし・通過。startAt: 自動運転を入れる秒。
    /// </summary>
    private static (double Pos, string Note) RunShunt(string siding, double startAt)
    {
        var list = new List<Station>();
        if (siding != "なし")
            list.Add(new Station { Name = "新野崎引上線", StopPosName = "新野崎駅5番下り", stopType = siding == "通過" ? StopType.Passing : StopType.StopForOperation, TotalLength = 0, DepTime = new TimeSpan(16, 25, 0) });
        list.Add(new Station { Name = "新野崎", StopPosName = "新野崎駅4番下り", stopType = StopType.StopForPassenger, TotalLength = 359, DepTime = new TimeSpan(16, 29, 0) });
        list.Add(new Station { Name = "新井川", StopPosName = "新井川駅下り", stopType = StopType.StopForPassenger, TotalLength = 1513, DepTime = new TimeSpan(16, 30, 50) });

        var auto = new AutoDriver();
        var sd = new StopDistance(null);
        double pos = 0, v = 0; // m・km/h
        int power = 0, brake = 5;
        var moved = false;
        var note = "";
        for (var t = 0.0; t < 180; t += 0.1)
        {
            var open = t >= 20;
            // ゲームの「次の駅」: 入換の進路が開くまでは引上線（残り 0）、開いたら新野崎
            var (nextName, nextDist) = open ? ("新野崎", 359 - pos) : ("新野崎引上線", 0 - pos);
            var cars = Enumerable.Range(0, 6).Select(_ => new CarState { CarModel = "5320", DoorClose = true }).ToList();
            var raw = new TrainState
            {
                Speed = (float)v, AllClose = true, CarStates = cars, NowTime = new TimeSpan(16, 25, 0) + TimeSpan.FromSeconds(t), diaName = "1667",
                TotalLength = (float)pos, stationList = list, nowStaIndex = 0, nextStaName = nextName, nextStaDistance = (float)nextDist, speedLimit = 25,
                nextSpeedLimit = -1, nextSpeedLimitDistance = -1, ATS_Speed = "", ATS_State = "無表示"
            };
            var signals = new List<SignalInfo>
            {
                new() { name = "新野崎入換25L", phase = open ? "YY" : "R", distance = (float)(5 - pos) },
                new() { name = "新野崎下り出発3L", phase = "G", distance = (float)(370 - pos) }
            };
            var s = new TciState(true, true, power, brake, 1, (float)v, true, "1667", (float)nextDist, nextName, false, "", 25, -1, -1, -1, "無表示", false);
            var stop = sd.Update(new StopDistanceInput(pos, v, 6, list.Select(x => new GameStop(x.Name, x.StopPosName, x.stopType != StopType.Passing, x.TotalLength)).ToList(),
                signals.Select(x => new GameSignal(x.name, x.phase, x.distance)).ToList(), []));
            if (t >= startAt)
            {
                var buzzer = t >= 25 && t < 25.1;
                (power, brake) = auto.Step(s, raw, signals, stop, buzzer, false, (long)(t * 1000), sd.RouteLimits(stop, 6));
                note = auto.Note;
            }

            // 走り: 力行 0.5km/h/s × 段、ブレーキは段ごとの減速度（自動運転の目安と同じ）、B8 は 4.5
            double[] decel = [0, 0.5, 1.0, 1.65, 2.27, 2.8, 3.4, 3.8, 4.5];
            var a = brake > 0 ? -decel[Math.Clamp(brake, 0, 8)] : power * 0.5;
            v = Math.Max(0, v + a * 0.1);
            pos += v / 3.6 * 0.1;
            if (v > 1) moved = true;
            if (moved && v == 0 && t > 30) break; // 走り出して、止まった
        }
        return (pos, note);
    }
}
