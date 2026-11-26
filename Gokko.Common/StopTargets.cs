namespace Gokko.Common;

/// <summary>
/// 停目までの距離をクライアントで出すための表（GET /api/stop-targets）。クライアントの自動運転が、乗務の初めに1回だけ読む。
/// サーバーは表を配るだけで、距離の計算はクライアントがする（毎回の通信はしない）。
/// </summary>
public sealed class StopTargetTable
{
    public List<StopTarget> Targets { get; set; } = new();
}

/// <summary>
/// 駅・方向・番線ごとの停目。
/// ・6両停目の位置: 信号機 <see cref="Signal"/> の <see cref="Before"/> m 手前（<see cref="After"/> があれば After m 先）。
///   信号機がなければ（Signal が null）、番線を合わせる補正（<see cref="Markers"/>）にだけ使う
/// ・その両数で止まる停目: <see cref="Markers"/> のうち、両数以上でいちばん小さい停目。なければいちばん先（Offset が小さい）の停目。
///   Offset は 6両停目（0m の停目）から手前への距離（m）
/// ・Station はダイヤの駅名（江原検 など。ゲームの駅名と違う所がある）。Track は番線（"1"・"11"・"X"。番線のない駅は ""）
/// </summary>
public sealed class StopTarget
{
    public string Station { get; set; } = "";

    /// <summary>"up"（上り）・"down"（下り）。</summary>
    public string Dir { get; set; } = "";

    public string Track { get; set; } = "";

    public string? Signal { get; set; }
    public double? Before { get; set; }
    public double? After { get; set; }

    public List<StopTargetMarker> Markers { get; set; } = new();

    /// <summary>その番線の軌道回路（自列車がここにいれば、この番線に入っている）。分からなければ null。</summary>
    public string? Circuit { get; set; }

    /// <summary>その番線に入る場内信号機（R でなければ、この番線に入る進路が開いている）。</summary>
    public List<string> ArriveSignals { get; set; } = new();

    /// <summary>
    /// その番線に入るときの制限速度の変わり目（ゲームの自動運転でその番線に入った記録から。なければ空）。
    /// ゲームの制限はシナリオの番線の進路のものなので、違う番線に入るときはこちらを使う。
    /// </summary>
    public List<StopTargetLimit> Limits { get; set; } = new();

    /// <summary>その両数で止まる停目の、6両停目から手前への距離（m）。停目の表がなければ 0。</summary>
    public double OffsetFor(int cars)
    {
        if (Markers.Count == 0) return 0;
        var fit = Markers.Where(m => m.Cars >= cars).OrderBy(m => m.Cars).ThenBy(m => m.Offset).FirstOrDefault();
        return (fit ?? Markers.OrderBy(m => m.Offset).First()).Offset;
    }
}

/// <summary>制限速度の変わり目（6両停目から Before m 手前で Kmh になる。先はマイナス）。</summary>
public sealed class StopTargetLimit
{
    public double Before { get; set; }
    public double Kmh { get; set; }
}

/// <summary>停目1つ（Cars 両停目が、6両停目から Offset m 手前にある）。</summary>
public sealed class StopTargetMarker
{
    public int Cars { get; set; }
    public double Offset { get; set; }
}
