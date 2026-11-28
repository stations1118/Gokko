using Gokko.Client;

namespace Gokko.Tests;

/// <summary>
/// クライアントの自動運転の決まり（Gokko.Client/StopDistance.cs をそのまま取り込んで動かす）。ゲームもサーバーも要らない（数ミリ秒）。
/// --data と、既定の走らせる試験の初めに見る。自動運転そのもの（AutoDriver.cs）は TRAIN CREW の部品を使うので取り込まない。
///
/// 決まり（自動運転を入れたとき、どの停車駅で発車を待っているか。StopDistance.StoppedAt）:
/// ・引上線から出る始発（1667。新野崎5番線 → 4番線）: ゲームの「次の駅」が新野崎で残り 359m なら、新野崎に止まっていない。
///   停車駅の一覧の新野崎の位置が、引上線（走った距離 0）と 30m 以内でも。止まっていることにすると、次の新井川を目指して、
///   入換で新野崎を通過しようとする（本番 2026-11-28 のランカーブ: 新野崎の停目まで残り 7m でもブレーキをかけず、52m 行き過ぎた）
/// ・ふつうに駅に止まっている（ゲームの残りが数 m）なら、その駅に止まっている
/// ・ゲームの「次の駅」でない駅は、一覧の位置で見る
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

        Console.WriteLine($"クライアントの自動運転: {(bad.Count == 0 ? "合格" : $"不合格 {bad.Count}件")}");
        foreach (var b in bad) Console.WriteLine($"  ✗ {b}");
        return bad.Count == 0 ? 0 : 1;
    }
}
