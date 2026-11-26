namespace Gokko.Common;

/// <summary>
/// ランカーブ（1駅間の走り方の記録）。クライアントが駅に止まったときに1回だけ送り、サーバーは日ごとのファイルに足すだけ。
/// ブレーキの効き方・停止位置のずれを確かめ、自動運転の見込みを直すのに使う（形式・駅・利用者でふるい分ける）。
/// 名前は入れない（だれのものかは、サーバーが接続から社員番号を付ける）。
/// </summary>
public sealed class RunCurve
{
    /// <summary>"auto"（自動運転）か "manual"（人の運転）。</summary>
    public string Mode { get; set; } = "";
    public string Train { get; set; } = "";
    public string Dia { get; set; } = "";
    /// <summary>発駅（前に止まった駅。分からなければ ""）と着駅（止まった駅。ダイヤの駅名）。</summary>
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    /// <summary>止まった駅の停止位置名（「高見沢駅1番下り」など）。</summary>
    public string StopPosName { get; set; } = "";
    /// <summary>入った番線とシナリオの番線（番線のない駅は ""）。</summary>
    public string Track { get; set; } = "";
    public string ScenarioTrack { get; set; } = "";
    public int Cars { get; set; }
    /// <summary>先頭の車両の形式と、全車の形式。</summary>
    public string Model { get; set; } = "";
    public List<string> Models { get; set; } = new();
    /// <summary>走り始めた時刻・止まった時刻（ゲームの時刻 HH:mm:ss）。</summary>
    public string Start { get; set; } = "";
    public string End { get; set; } = "";
    /// <summary>止まったときの停目までの残り（m。プラスは手前に止まった）と、その確かさ（"Confirmed"・"Rough"・"None"）。</summary>
    public double StopRemaining { get; set; }
    public string StopKind { get; set; } = "";
    /// <summary>止まったときの、ゲームの距離から出した残り（確定のときも入る）。</summary>
    public double StopRough { get; set; }
    /// <summary>止まったときの走った距離（m）。</summary>
    public double StopTraveled { get; set; }
    /// <summary>止まったときに見えていた信号機ぜんぶ（遠くても）。</summary>
    public List<RunSignal> StopSignals { get; set; } = new();
    /// <summary>止まったときの自列車の軌道回路。</summary>
    public List<string> StopCircuits { get; set; } = new();
    /// <summary>自動運転だけ: 覚えたブレーキの効き方（実際の減速度 ÷ 目安）。人の運転は 0。</summary>
    public double Gain { get; set; }
    public List<RunPoint> Points { get; set; } = new();
}

/// <summary>信号機（名前・現示・距離 m）。</summary>
public sealed class RunSignal
{
    public string Name { get; set; } = "";
    public string Phase { get; set; } = "";
    public double Distance { get; set; }
}

/// <summary>
/// ランカーブの1行。ブレーキ中は 0.2秒ごと、ほかは 2秒ごとか、ノッチ・制限・番線・信号機・軌道回路が変わったとき。
/// 名前を短くして大きさを抑える。
/// </summary>
public sealed class RunPoint
{
    /// <summary>走り始めてからの秒。</summary>
    public double T { get; set; }
    /// <summary>走った距離（m）。</summary>
    public double X { get; set; }
    /// <summary>速度（km/h）。</summary>
    public double V { get; set; }
    public int P { get; set; }
    public int B { get; set; }
    /// <summary>勾配（ゲームの値のまま）。</summary>
    public double G { get; set; }
    /// <summary>制限速度（km/h）。</summary>
    public double L { get; set; }
    /// <summary>ゲームの次の駅までの残り（nextStaDistance）。</summary>
    public double N { get; set; }
    /// <summary>停目までの残り（m）と、その確かさ（0 なし・1 おおまか・2 確定）と、ゲームの距離から出した残り。</summary>
    public double R { get; set; }
    public int K { get; set; }
    public double Rough { get; set; }
    /// <summary>変わったときだけ入る: 入る番線・シナリオの番線・使った信号機の名前・自列車の軌道回路。ほかの行は null。</summary>
    public string? Track { get; set; }
    public string? ScenarioTrack { get; set; }
    public string? Signal { get; set; }
    public List<string>? Circuits { get; set; }
    /// <summary>使った信号機までの距離（m。なければ null）。</summary>
    public double? SignalDistance { get; set; }
}
