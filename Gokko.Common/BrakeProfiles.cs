namespace Gokko.Common;

/// <summary>
/// ランカーブ（人の運転・自動運転の記録）から集計した、自動運転の見込みの値。サーバーは Data/BrakeProfiles.json を配るだけで、
/// クライアントは乗務を始めたときに1回だけ読む（GET /api/brake-profiles）。表がなければ、クライアントは今までの目安で走る。
/// 集計は管理用の API（POST /api/admin/brake-profiles/build）を押したときだけ行う（ふだんの負荷は増えない）。
/// </summary>
public sealed class BrakeProfileTable
{
    /// <summary>集計した時刻（UTC）と、もとにした記録の数。</summary>
    public DateTime BuiltUtc { get; set; }
    public int Runs { get; set; }

    /// <summary>形式ごとの、段の効き方と効き始めの遅れ。</summary>
    public List<BrakeProfile> Models { get; set; } = new();

    /// <summary>駅・番線ごとの、止まる位置の補正。</summary>
    public List<StopCorrection> Stops { get; set; } = new();
}

/// <summary>
/// 1形式（先頭の車両の形式）の効き方。Decel は段（添字。0・1 は使わない）ごとの効ききった減速度（km/h/s。勾配のゆるい所の記録だけ）。
/// 測れなかった段は 0（クライアントは目安を使う）。LagSec は段を入れてから効ききるまでの秒（測れなければ 0）。
/// </summary>
public sealed class BrakeProfile
{
    public string Model { get; set; } = "";
    public double[] Decel { get; set; } = new double[8];
    public int[] Samples { get; set; } = new int[8];
    public double LagSec { get; set; }
    public int LagSamples { get; set; }
}

/// <summary>
/// 駅・番線（方向も）ごとに、自動運転が止まった位置のずれの中央値（m。プラスは手前に止まりがち）。
/// クライアントは停目までの残りにこの分を足して、ずれを打ち消す（記録が少ない・ずれが大きすぎるものは入れない）。
/// </summary>
public sealed class StopCorrection
{
    public string Station { get; set; } = "";
    public string Track { get; set; } = "";
    /// <summary>止まった停止位置名（「高見沢駅1番下り」など。方向の見分けに使う）。</summary>
    public string StopPosName { get; set; } = "";
    public double Offset { get; set; }
    public int Samples { get; set; }
}
