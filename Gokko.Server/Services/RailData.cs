using System.Text;
using System.Text.Json;

namespace Gokko.Server.Services;

/// <summary>方向。下り＝館浜方向（ゾーンの番号が増える向き）、上り＝新大路方向。</summary>
public enum Dir
{
    Down,
    Up
}

public static class DirExtensions
{
    public static string Label(this Dir d) => d == Dir.Down ? "下り" : "上り";

    /// <summary>進む向きのゾーンの増分（下り +1、上り -1）。</summary>
    public static int Step(this Dir d) => d == Dir.Down ? 1 : -1;
}

/// <summary>進路1本。</summary>
public sealed record RouteDef(
    string StationId,
    string Interlock,
    string Name,
    string Category,
    string Signal,
    IReadOnlyList<string> Related,
    string From,
    string To,
    IReadOnlyList<string> Circuits,
    string Kind,
    string Indicator,
    IReadOnlyList<string> Approach)
{
    // Approach: 接近鎖錠の区間の軌道回路。列車がここにいる間は、進路を取り消すと
    // 運転士の目の前で信号が変わるので、抑止では取り消さない。書かれていない進路は空

    /// <summary>出発信号機の進路か。出発の進路の LED は、進路表示ではなく列車の種別（列選）を出す。</summary>
    public bool IsDepartureSignal => Kind.Contains("出発");
}

/// <summary>駅・方向ごとの、番線と進路の対応（TrackRoutes.json）。</summary>
public sealed class StationDirRoutes
{
    public Dictionary<int, List<RouteDef>> Arrive { get; } = new();
    public Dictionary<int, List<RouteDef>> Depart { get; } = new();
    public List<RouteDef> Through { get; } = new();

    /// <summary>始発の入換（引上線・検車区などからホームへ）。</summary>
    public List<ShuntDef> Shunts { get; } = new();

    /// <summary>
    /// 終着の列車の入換（ホームから引上線・検車区などへ。TrackRoutes.json の shuntOut）。
    /// 同じ番線に複数あれば、書いた順（最初が、列車の「入換要求」ボタンで使う既定）。
    /// </summary>
    public List<ShuntOutDef> ShuntOuts { get; } = new();

    /// <summary>出発停止の番線（その番線に入る場内進路を開通させている間は、出発を開通できない。TrackRoutes.json の arrive の departStop）。</summary>
    public HashSet<int> DepartStop { get; } = new();

    /// <summary>出発停止の番線のうち、停車する列車だけ出発停止にする番線（departStop が "停車"。通過列車は場内・出発を一緒に開ける）。</summary>
    public HashSet<int> DepartStopStoppingOnly { get; } = new();

    /// <summary>行き止まり（折り返し）として選べる番線。入れるが、同じ方向へは出られない（TrackRoutes.json の arrive の turnback）。
    /// この番線を選ぶと、列車はこの駅で運転を打ち切る扱いになり、先の駅の進路は扱わない。</summary>
    public HashSet<int> Turnback { get; } = new();

    /// <summary>そのまま同じ方向に通れる番線（着と発の両方がある）。</summary>
    public IEnumerable<int> ThroughTracks => Arrive.Keys.Where(Depart.ContainsKey).OrderBy(t => t);
}

/// <summary>
/// 始発の入換1つ。元は番線（From）か、番線のない線路の軌道回路（FromCircuit）で書く。
/// 同じ元・同じ番線への入換が複数あれば、書いた順に優先する。RequireFree の回路に他の列車がいれば、その入換は使わない
/// （例: 大道寺 Y線→E線→番線 を既定にし、E線がふさがっていれば Y線から直接）。
/// Then は、途中の線（大道寺の X線・Y線）で向きを変えてから開ける2つ目の進路（大道寺 5番線 → X線 → 2番線 の 31LB）。なければ空。
/// </summary>
public sealed record ShuntDef(string FromLabel, int? From, string? FromCircuit, int To, List<RouteDef> Routes, List<string> RequireFree, string? Via, List<RouteDef>? Then = null)
{
    public List<RouteDef> ThenRoutes => Then ?? [];
}

/// <summary>
/// 終着の列車の入換1つ（番線 From から、Label の線路へ）。同じ番線・同じ行き先（Label）が複数あれば、まとめて1つの選択肢とし、
/// 書いた順に優先する。RequireFree の回路に他の列車がいれば次の候補（大道寺 番線→E線→Y線 を既定にし、E線がふさがっていれば直接）。
/// Then は、引き上げた線（大道寺の X線）で向きを変えてから開ける2つ目の進路（大道寺 3番線 → X線 → 5番線 の 31L5。留置線へ入れるとき）。なければ空。
/// </summary>
public sealed record ShuntOutDef(int From, string Label, List<RouteDef> Routes, List<string> RequireFree, string? Via, List<RouteDef>? Then = null)
{
    public List<RouteDef> ThenRoutes => Then ?? [];
}

public sealed class StationDef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Interlock { get; init; }
    public required bool SingleTrackStation { get; init; }
    public required bool RouteControl { get; init; }
    public Dictionary<Dir, StationDirRoutes> Routes { get; } = new() { [Dir.Down] = new(), [Dir.Up] = new() };

    /// <summary>
    /// 番線に入りきってから、出発を開通させるまで待つ秒数（RouteRules.json の departDelaySeconds。方向・番線ごと）。
    /// 例: 大道寺の上り1番線は、入ってから120秒たつまで出発信号を出さない。
    /// </summary>
    public Dictionary<(Dir Dir, int Track), int> DepartDelay { get; } = new();

    /// <summary>番線 → その番線の軌道回路。</summary>
    public Dictionary<int, string> TrackCircuit { get; } = new();

    /// <summary>方向 → 所定の番線の決まり（キー → 番線）。</summary>
    public Dictionary<Dir, Dictionary<string, int>> DefaultTracks { get; } = new();

    /// <summary>ダイヤグラムを番線ごとに分けたときの段（上から順。Label は番線か入換線の名前、Circuit は入換線の軌道回路）。空なら進路のある番線すべて。</summary>
    public List<(string Label, string? Circuit)> DiagramRows { get; } = new();

    /// <summary>ダイヤグラムの運行のつなぎ線で、折り返しの入換にかかる秒数（"2>Y" → 70。Stations.json の shuntSeconds）。</summary>
    public Dictionary<string, int> ShuntSeconds { get; } = new();
}

public sealed class SignalDef
{
    public required string Name { get; init; }
    public string? Type { get; init; }
    public Dir? Direction { get; init; }
    public bool IsRepeater { get; init; }
    public IReadOnlyList<string> Next { get; init; } = [];
    public IReadOnlyList<string> Protects { get; init; } = [];

    /// <summary>
    /// ほかの列車がいたら停止にする回路（Protects から、同じ連動装置のほかの信号機の進路だけに入っている回路を除いたもの）。
    /// Protects は進路に関係なく分岐の先の両方の番線を含むことがある（高見沢下り場内14L に2番線の 15LT・15RT が入っている）。
    /// そのまま使うと、もう一方の番線にいる列車で、自分の進路の信号まで停止にしてしまう。
    /// </summary>
    public IReadOnlyList<string> StopProtects { get; set; } = [];

    /// <summary>
    /// 行き先が何通りもある信号機（大道寺上り場内1RTB・入換など）の、進路ごとの「その進路では見ない回路」
    /// （StopProtects のうち、自分のほかの進路だけに入っている回路）。進路が1本だけの信号機は空。
    /// </summary>
    public Dictionary<(string Interlock, string Route), HashSet<string>> SkipByRoute { get; set; } = new();

    /// <summary>
    /// 停止にする回路。この信号機の進路のうち、開いている進路（isSet）が分かれば、その進路で通らない回路を除く。
    /// 開いている進路がなければ（ゲームの信号はどのみち停止）、StopProtects のまま。
    /// </summary>
    public IEnumerable<string> StopProtectsFor(Func<(string Interlock, string Route), bool> isSet)
    {
        if (SkipByRoute.Count == 0) return StopProtects;
        HashSet<string>? skip = null;
        foreach (var (key, s) in SkipByRoute)
        {
            if (!isSet(key)) continue;
            if (skip == null) skip = new HashSet<string>(s);
            else skip.IntersectWith(s); // 2本開いていれば、どちらかで通る回路は見る
        }

        return skip == null ? StopProtects : StopProtects.Where(c => !skip.Contains(c));
    }

    /// <summary>
    /// 単線区間の閉塞信号機が従う方向てこ（区間の両端の駅の方向てこ。例: TH65_14F・TH64_11F ＝ 大道寺〜藤江）。
    /// 方向てこがこの信号機の向き（Direction）でない間は、この信号機は停止のまま。
    /// </summary>
    public IReadOnlyList<string> DirectionRoutes { get; init; } = [];
}

/// <summary>待避する駅と、方向ごとの待避の番線。</summary>
public sealed record PassingStation(string StationId, int DownTrack, int UpTrack)
{
    public int TrackFor(Dir d) => d == Dir.Down ? DownTrack : UpTrack;
}

/// <summary>ダイヤ（Timetable.json）の1列車。進路を作るときの基本（番線・停車・終着・列選）。</summary>
public sealed class DiaTrain
{
    public required string Number { get; init; }

    /// <summary>種別（特急・急行・普通・回送 など。試運転は回送として扱う）。</summary>
    public required string Type { get; init; }

    /// <summary>種別表記（C特1・A特・急行 など）。出発信号機の列選に出す。</summary>
    public required string TypeLabel { get; init; }

    /// <summary>時刻表に書かれた行き先（大道寺・江原検 など）。列車走行位置に出す。書かれていなければ空。</summary>
    public string Dest { get; set; } = "";

    /// <summary>進路を扱う駅で終わるなら、その駅（江原検 → 江ノ原信号場）。路線の外で終わるなら null。</summary>
    public string? TerminalId { get; set; }

    /// <summary>
    /// 途中駅で列番が変わる列車の、次の列番と、変わる駅（例: 1651 は大道寺から 回1651）。なければ null。
    /// </summary>
    public (string Number, string StationId)? Next { get; set; }

    /// <summary>運行番号（"24"・"24S"。列番の付け方から求めたもの。<see cref="Gokko.Common.Unyo"/>）。読めなければ null。</summary>
    public string? Unyo { get; set; }

    /// <summary>両数（臨時列車で決めたときだけ。null なら自動運転は運行番号ごとの両数）。</summary>
    public int? Cars { get; set; }

    /// <summary>終点のあと（スタフの「折返」「引継」「滞泊」）。書かれていなければ null。</summary>
    public DiaTurn? Turn { get; set; }

    /// <summary>時刻表（路線の外の駅も含む、ダイヤに書かれた全駅）。時刻は "HH:mm:ss"、書かれていなければ null。</summary>
    public List<DiaStop> Stops { get; } = new();

    /// <summary>駅ID → 停車するか・番線（書かれていなければ null）。</summary>
    public Dictionary<string, (string StopType, int? Track)> Stations { get; } = new();

    /// <summary>この列車を写して、「終点のあと」だけ差し替えたもの（終夜運転で、留置になる所定の列車を臨時列車につなぐ）。</summary>
    public DiaTrain WithTurn(DiaTurn? turn)
    {
        var c = new DiaTrain
        {
            Number = Number, Type = Type, TypeLabel = TypeLabel, Dest = Dest, TerminalId = TerminalId, Next = Next, Unyo = Unyo, Cars = Cars, Turn = turn
        };
        c.Stops.AddRange(Stops);
        foreach (var (k, v) in Stations) c.Stations[k] = v;
        return c;
    }
}

/// <summary>
/// 終点のあと。Kind は 折返（Number の列車として折り返す。Time はその発車・入換の時刻、How は「発」か「入換」）、
/// 引継（同じ列番のまま線区の外へ）、滞泊（その駅で夜を越す）。
/// Via は、入換で折り返すときに引き上げる線（TrackRoutes.json の shuntOut の label。"X線" など）。書かれていなければ既定の線。
/// 留置線に置いてから出すとき（大道寺 3番線 → X線 → 5番線）は、Via が留置線（"5番線"）で、Out はそこから番線へ出るときに通る線
/// （"X線"・"Y線"。TrackRoutes.json の shunt の via）、After は留置線から出る前に通り過ぎるのを待つ列車（2121A など）。
/// Time は留置線から出る入換の時刻。
/// </summary>
public sealed record DiaTurn(string Kind, string? Number, string? Time, string? How, string? Via = null, string? Out = null, string? After = null);

/// <summary>時刻表の1駅（StationId は路線の外の駅なら null。Stop は 停・通・運停）。</summary>
public sealed record DiaStop(string Name, string? StationId, string Stop, string? Arr, string? Dep, int? Track = null, string? Note = null);

/// <summary>標準運転時分の1区間。</summary>
public sealed record RunTimeRow(string Type, Dir Dir, string From, string To, bool FromStops, bool ToStops, int Seconds);

/// <summary>
/// 在線共有（集中モード）で使う、路線・進路・信号のデータ一式（Data フォルダ）。
/// Stations.json / TrackRoutes.json / RouteRules.json / Interlockings.json / Signals.json / SignalTypes.json / RunTimes.json / Timetable.json
/// </summary>
public sealed class RailData
{
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public required LineMap Line { get; init; }
    public Dictionary<string, StationDef> Stations { get; } = new();
    public Dictionary<(string Interlock, string Route), RouteDef> Routes { get; } = new();
    public Dictionary<string, SignalDef> Signals { get; } = new();

    /// <summary>灯式 → 次の現示 → 自分の現示。</summary>
    public Dictionary<string, Dictionary<string, string>> SignalTypes { get; } = new();

    public HashSet<(string, string)> SingleTrackSections { get; } = new();
    public Dictionary<string, PassingStation> PassingStations { get; } = new();

    /// <summary>種別 → 強さ（大きいほど強い）。</summary>
    public Dictionary<string, int> TypeRank { get; } = new();

    public int MaxPassingWaits { get; set; } = 3;

    public int RankOf(string type) => TypeRank.TryGetValue(type, out var r) ? r : 0;
    public List<RunTimeRow> RunTimes { get; } = new();
    public int PassingMarginSeconds { get; set; } = 120;

    /// <summary>現示時素（踏切の警報時分のため）。「連動装置/進路名」→ 秒。ゲームで進路が開通してから、この秒数は信号を停止のままにする。</summary>
    public Dictionary<string, int> ClearTimes { get; } = new();
    public int PassingToleranceSeconds { get; set; } = 60;
    public int DefaultDwellSeconds { get; private set; } = 20;

    /// <summary>待避線に入ったときの最低の停車時分（秒）。</summary>
    public int MinLoopDwellSeconds { get; private set; } = 90;
    public int AfterStopSeconds { get; private set; } = 0;

    /// <summary>進路を信号機の位置から取り始めるか（ApproachTrigger）と、限界点からの余裕（閉塞の数）。切ってあれば「2つ手前の駅」で取る。</summary>
    public bool ApproachTriggerEnabled { get; set; }
    public int ApproachTriggerMargin { get; set; } = 1;

    /// <summary>
    /// 第1場内を先に取る駅・向き → 第1場内を取り始める信号機（RouteRules.json の approachTrigger.firstHome）。
    /// 第2場内（とその先）は、第1場内が開いている前提で計算した位置で取る（withSecond の駅は第2場内も一緒に取る）。
    /// </summary>
    public Dictionary<(string Station, Dir Dir), string> ApproachFirstHome { get; } = new();

    /// <summary>
    /// 第1場内を先に取る駅のうち、第2場内も第1場内と同じ位置でまとめて取る駅・向き
    /// （firstHome に { "signal": "信号機名", "withSecond": true } と書いたもの）。
    /// 通過列車は出発も一緒に取る。
    /// </summary>
    public HashSet<(string Station, Dir Dir)> ApproachFirstHomeWithSecond { get; } = new();

    /// <summary>ダイヤ運転中、出発も場内と一緒に（出発・場内の順に）要求する駅（浜園など）。RouteRules.json の departBeforeArrive。</summary>
    public HashSet<string> DepartBeforeArrive { get; } = new();

    /// <summary>進路を扱う駅（路線の並び順）。</summary>
    public IEnumerable<StationDef> ControlStations =>
        Line.StationIds.Where(id => Stations.TryGetValue(id, out var s) && s.RouteControl).Select(id => Stations[id]);

    public bool IsSingleTrack(string a, string b) => SingleTrackSections.Contains((a, b)) || SingleTrackSections.Contains((b, a));

    /// <summary>単線区間に接する駅か（行き違いのために、空いている番線へ入れる駅）。</summary>
    public bool TouchesSingleTrack(string station) => SingleTrackSections.Any(p => p.Item1 == station || p.Item2 == station);

    // 所定のダイヤ（Timetable.json から読んだもの。起動したあとは変えない）
    private readonly Dictionary<string, DiaTrain> _baseDia = new();
    private volatile Dictionary<string, DiaTrain>? _dia;

    /// <summary>
    /// ダイヤ。列車番号（正規化したもの）→ 列車。読み込みが済んだあとは書き換えない（読むだけ）。
    /// 催物用の臨時列車を適用している間は、所定のダイヤに臨時列車を足した別の表になる（<see cref="SetExtraTrains"/>）。
    /// 臨時停車で時刻を変えた列車があれば、その列車も差し替えた表になる（<see cref="SetRetimed"/>）。
    /// </summary>
    public Dictionary<string, DiaTrain> Dia => _dia ?? _baseDia;

    /// <summary>所定のダイヤ（臨時列車を足していないもの）。</summary>
    public IReadOnlyDictionary<string, DiaTrain> BaseDia => _baseDia;

    // 所定のダイヤに重ねる列車（出どころ → 列車。催物用の臨時列車・特発臨など）
    private readonly Dictionary<string, List<DiaTrain>> _extra = new();
    // 所定の列車の差し替え（出どころ → 列車。終夜運転で、留置になる所定の列車の「終点のあと」を臨時列車につなぐなど）
    private readonly Dictionary<string, List<DiaTrain>> _replace = new();
    private readonly object _extraLock = new();

    /// <summary>
    /// 所定のダイヤに重ねる列車を、出どころ（source。"催物"・"特発臨" など）ごとに差し替える。extra が空なら、その出どころの列車を外す。
    /// どの出どころにも列車がなければ、所定のダイヤに戻す。
    /// 所定のダイヤは変えず、足した表を新しく作って参照を切り替えるだけなので、読んでいる途中の処理はそのまま古い表を読み終える。
    /// 表は、ほかの出どころを先に足し、この出どころを最後に足す。所定のダイヤ・ほかの出どころにある列番は足さない（この出どころの足せなかった列番を返す）。
    /// replace は、所定の列車を差し替えるもの（同じ列番の所定の列車があるものだけ。中身は呼ぶ側が所定の列車を写して作る）。
    /// </summary>
    public List<string> SetExtraTrains(string source, IReadOnlyCollection<DiaTrain> extra, IReadOnlyCollection<DiaTrain>? replace = null)
    {
        List<string> skipped;
        lock (_extraLock)
        {
            if (extra.Count == 0) _extra.Remove(source);
            else _extra[source] = extra.ToList();
            if (replace is not { Count: > 0 }) _replace.Remove(source);
            else _replace[source] = replace.ToList();
            skipped = Rebuild(source);
        }
        Turns.DiaChanged();
        return skipped;
    }

    // 所定のダイヤの列車を、時刻を変えたものに差し替える（列番 → 列車。浜園の特急の臨時停車の時刻など）
    private Dictionary<string, DiaTrain> _retimed = new();

    /// <summary>時刻を差し替えている列車（列番 → 差し替えた列車）。</summary>
    public IReadOnlyDictionary<string, DiaTrain> Retimed => _retimed;

    /// <summary>
    /// 所定のダイヤの列車を、時刻を変えた列車に差し替える（<see cref="TempStopRetime"/>）。空なら所定に戻す。
    /// 臨時列車と同じく、表を新しく作って参照を切り替える。所定のダイヤにない列番は差し替えない。
    /// </summary>
    public void SetRetimed(IReadOnlyDictionary<string, DiaTrain> retimed)
    {
        lock (_extraLock)
        {
            _retimed = retimed.Where(kv => _baseDia.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            Rebuild(null);
        }
        Turns.DiaChanged();
    }

    /// <summary>記事（待避・連絡・交換）を作り直せなかったときの理由（なければ null）。</summary>
    public string? NotesError { get; private set; }

    /// <summary>
    /// 所定のダイヤに、時刻の差し替えと臨時列車を重ねた表を作り直す（_extraLock の中で）。source の出どころの足せなかった列番を返す。
    /// 重ねた列車と、その相手の列車の待避・連絡・交換の記事も作り直す（<see cref="DiaNotes"/>）。
    /// </summary>
    private List<string> Rebuild(string? source)
    {
        var skipped = new List<string>();
        if (_extra.Count == 0 && _retimed.Count == 0 && _replace.Count == 0)
        {
            _dia = null;
            return skipped;
        }
        var d = new Dictionary<string, DiaTrain>(_baseDia);
        // 所定の列車の差し替え（終夜運転の前運用）。臨時停車で時刻も変えた列車は、変えた時刻に差し替えた「終点のあと」を付ける（両方生かす）
        foreach (var list in _replace.Values)
            foreach (var t in list)
                if (_baseDia.ContainsKey(t.Number)) d[t.Number] = t;
        foreach (var (n, t) in _retimed) d[n] = d.TryGetValue(n, out var r) && !ReferenceEquals(r, _baseDia[n]) ? t.WithTurn(r.Turn) : t;
        foreach (var (src, list) in _extra)
            if (src != source)
                foreach (var t in list) d.TryAdd(t.Number, t);
        if (source != null && _extra.TryGetValue(source, out var mine))
            foreach (var t in mine)
                if (!d.TryAdd(t.Number, t)) skipped.Add(t.Number);

        // 待避・連絡・交換の記事を、時刻を変えた列車・臨時列車と、その相手の列車だけ作り直す（DiaNotes）
        var overlay = new Dictionary<string, DiaTrain>();
        foreach (var (n, t) in d)
            if (!ReferenceEquals(t, _baseDia.GetValueOrDefault(n))) overlay[n] = t;
        try
        {
            foreach (var (n, t) in DiaNotes.Rewrite(_baseDia, overlay)) d[n] = t;
        }
        catch (Exception ex)
        {
            NotesError = ex.Message; // 記事が作れなくても、表は差し替える（記事は所定のまま）
        }
        _dia = d;
        return skipped;
    }

    public static string NormalizeTrain(string? s) => (s ?? "").Normalize(NormalizationForm.FormKC).Trim().ToUpperInvariant();

    public DiaTrain? DiaOf(string train) => Dia.GetValueOrDefault(NormalizeTrain(train));

    // ---- 試運転（所定の臨時列車）: 普段は走らせず、司令が選んだ運行番号だけ走らせる。選択は再起動で「なし」に戻る ----
    private volatile HashSet<string> _trialsOn = new();

    /// <summary>試運転か（列番が「試」で始まる。時刻表の種別は試運転）。</summary>
    public static bool IsTrial(DiaTrain d) => d.Number.StartsWith("試");

    /// <summary>試運転の運行番号（時刻表の並び）。</summary>
    public List<string> TrialUnyos() => Dia.Values.Where(d => IsTrial(d) && d.Unyo != null).Select(d => d.Unyo!).Distinct().Order(Gokko.Common.Unyo.Comparer).ToList()!;

    /// <summary>走らせる試運転の運行番号。</summary>
    public IReadOnlyCollection<string> TrialsOn => _trialsOn;

    /// <summary>試運転の選択を変えると増える（ダイヤグラムの計画の線を読み直す合図）。</summary>
    public int TrialVersion { get; private set; }

    public void SetTrialsOn(IEnumerable<string> unyos)
    {
        var known = TrialUnyos().ToHashSet();
        _trialsOn = unyos.Select(Gokko.Common.Unyo.Normalize).OfType<string>().Where(known.Contains).ToHashSet();
        TrialVersion++;
    }

    /// <summary>走らない試運転か（選ばれていない運行番号の試運転）。自動運転の列車として出さず、ダイヤグラムの計画の線も引かない。</summary>
    public bool IsIdleTrial(DiaTrain d) => IsTrial(d) && !(d.Unyo is { } u && _trialsOn.Contains(u));

    private TurnChanges? _turns;

    /// <summary>後運用の差し替え（ダイヤ運転中に司令が変えたもの）。「終点のあと」と運行番号は、これを通して読む。</summary>
    public TurnChanges Turns => LazyInitializer.EnsureInitialized(ref _turns, () => new TurnChanges(this));

    public static RailData Load(string dir, LineMap line)
    {
        var data = new RailData { Line = line };
        JsonDocument Doc(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, name), Encoding.UTF8), JsonOptions);
        string Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        List<string> List(JsonElement e, string p) =>
            e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new();

        // ---- 進路（Interlockings.json） ----
        using (var doc = Doc("Interlockings.json"))
        {
            foreach (var il in doc.RootElement.GetProperty("interlockings").EnumerateArray())
            {
                var sid = Str(il, "stationId");
                var name = Str(il, "interlock");
                foreach (var r in il.GetProperty("routes").EnumerateArray())
                {
                    var def = new RouteDef(sid, name, Str(r, "name"), Str(r, "category"), Str(r, "signal"),
                        List(r, "related"), Str(r, "from"), Str(r, "to"), List(r, "circuits"), Str(r, "kind"), Str(r, "indicator"), List(r, "approach"));
                    data.Routes[(name, def.Name)] = def;
                }
            }
        }

        // ---- 駅（Stations.json） ----
        using (var doc = Doc("Stations.json"))
        {
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                var st = new StationDef
                {
                    Id = Str(s, "id"), Name = Str(s, "name"), Interlock = Str(s, "interlock"),
                    SingleTrackStation = Str(s, "track") == "単線",
                    RouteControl = s.TryGetProperty("routeControl", out var rc) && rc.GetBoolean()
                };
                if (s.TryGetProperty("diagramRows", out var dr))
                    foreach (var row in dr.EnumerateArray().Select(x => x.GetString() ?? ""))
                    {
                        var p = row.Split(':', 2);
                        st.DiagramRows.Add((p[0], p.Length > 1 ? p[1] : null));
                    }
                if (s.TryGetProperty("shuntSeconds", out var ss) && ss.ValueKind == JsonValueKind.Object)
                    foreach (var p in ss.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.Number) st.ShuntSeconds[p.Name] = p.Value.GetInt32();
                data.Stations[st.Id] = st;
                // 番線の軌道回路: 「N番線へ」の進路の最後の回路のうち、いちばん多く出てくるもの
                // （新野崎の 10LC は 34T まで、4L が 4LT まで。どちらも「3番線へ」なので、多い方の 4LT を番線の回路とする）
                var lastByTrack = new Dictionary<int, List<string>>();
                foreach (var r in data.Routes.Values.Where(r => r.Interlock == st.Interlock && r.Circuits.Count > 0))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(r.To, @"^(\d+)番線$");
                    if (!m.Success) continue;
                    var t = int.Parse(m.Groups[1].Value);
                    if (!lastByTrack.TryGetValue(t, out var list)) lastByTrack[t] = list = new List<string>();
                    list.Add(r.Circuits[^1]);
                }

                // 同じ数なら、その番線から出るすべての進路の接近に入っている回路（ホームの回路）。
                // 水越1番線は 12R→21ロT・14L→12RT が1本ずつ。21ロT は列車が通らない回路で、出発の接近にあるのは 12RT（赤山町2番線も 23ロT と 2RBT で 2RBT）
                foreach (var (t, list) in lastByTrack)
                {
                    var counts = list.GroupBy(x => x).Select(g => (Name: g.Key, Count: g.Count())).ToList();
                    var top = counts.Max(c => c.Count);
                    var tied = counts.Where(c => c.Count == top).Select(c => c.Name).ToList();
                    var departApproach = data.Routes.Values
                        .Where(r => r.Interlock == st.Interlock && r.From == $"{t}番線" && r.Approach.Count > 0).Select(r => r.Approach).ToList();
                    st.TrackCircuit[t] = tied.FirstOrDefault(c => departApproach.Count > 0 && departApproach.All(a => a.Contains(c))) ?? tied[0];
                }
            }
        }

        // ---- 番線の表（TrackRoutes.json） ----
        using (var doc = Doc("TrackRoutes.json"))
        {
            foreach (var s in doc.RootElement.GetProperty("stations").EnumerateArray())
            {
                if (!data.Stations.TryGetValue(Str(s, "id"), out var st))
                    throw new InvalidDataException($"TrackRoutes.json: Stations.json にない駅 {Str(s, "id")}");
                foreach (var (key, d) in new[] { ("down", Dir.Down), ("up", Dir.Up) })
                {
                    var e = s.GetProperty(key);
                    var target = st.Routes[d];
                    // 進路の並び。関連進路（第二場内・入換の続きなど）が書かれていなければ、主の進路のすぐあとに足す
                    // （例: 新野崎 25LC → 21L、25LD → 22L。SetRoute では関連進路は自動で設定されないため）
                    List<RouteDef> Resolve(IEnumerable<string> names)
                    {
                        var list = new List<RouteDef>();
                        foreach (var n in names)
                        {
                            if (!data.Routes.TryGetValue((st.Interlock, n), out var r))
                                throw new InvalidDataException($"TrackRoutes.json: {st.Name} の進路 {n} が Interlockings.json にない");
                            if (!list.Contains(r)) list.Add(r);
                        }

                        for (var i = 0; i < list.Count; i++)
                        foreach (var rel in list[i].Related)
                            if (data.Routes.TryGetValue((st.Interlock, rel), out var rr) && !list.Contains(rr))
                                list.Insert(i + 1, rr);
                        return list;
                    }
                    target.Through.AddRange(Resolve(List(e, "through")));
                    if (e.TryGetProperty("shunt", out var shunts))
                        foreach (var sh in shunts.EnumerateArray())
                        {
                            int? from = sh.TryGetProperty("from", out var f) ? f.GetInt32() : null;
                            var fromCircuit = Str(sh, "fromCircuit") is { Length: > 0 } fc ? fc : null;
                            var label = Str(sh, "fromLabel") is { Length: > 0 } fl ? fl : from != null ? $"{from}番線" : fromCircuit ?? "?";
                            var via = Str(sh, "via") is { Length: > 0 } v ? v : null;
                            target.Shunts.Add(new ShuntDef(label, from, fromCircuit, sh.GetProperty("to").GetInt32(), Resolve(List(sh, "routes")),
                                List(sh, "requireFree"), via, Resolve(List(sh, "then"))));
                        }
                    if (e.TryGetProperty("shuntOut", out var outs))
                        foreach (var so in outs.EnumerateArray())
                            target.ShuntOuts.Add(new ShuntOutDef(so.GetProperty("from").GetInt32(), Str(so, "label"), Resolve(List(so, "routes")),
                                List(so, "requireFree"), Str(so, "via") is { Length: > 0 } sov ? sov : null, Resolve(List(so, "then"))));
                    foreach (var (prop, map) in new[] { ("arrive", target.Arrive), ("depart", target.Depart) })
                    {
                        if (!e.TryGetProperty(prop, out var arr)) continue;
                        foreach (var t in arr.EnumerateArray())
                        {
                            var track = t.GetProperty("track").GetInt32();
                            map[track] = Resolve(List(t, "routes"));
                            if (prop == "arrive" && t.TryGetProperty("departStop", out var ds)
                                && (ds.ValueKind == JsonValueKind.True || ds.ValueKind == JsonValueKind.String && ds.GetString() == "停車"))
                            {
                                target.DepartStop.Add(track);
                                if (ds.ValueKind == JsonValueKind.String) target.DepartStopStoppingOnly.Add(track); // "停車": 通過列車は出発停止にしない
                            }
                            if (prop == "arrive" && t.TryGetProperty("turnback", out var tb) && tb.ValueKind == JsonValueKind.True)
                                target.Turnback.Add(track);
                        }
                    }
                }
            }
        }

        // ---- 決まり（RouteRules.json） ----
        using (var doc = Doc("RouteRules.json"))
        {
            var root = doc.RootElement;
            foreach (var pair in root.GetProperty("singleTrackSections").EnumerateArray())
            {
                var p = pair.EnumerateArray().Select(x => x.GetString()!).ToArray();
                data.SingleTrackSections.Add((p[0], p[1]));
            }

            foreach (var st in root.GetProperty("defaultTracks").EnumerateObject())
            {
                if (!data.Stations.TryGetValue(st.Name, out var station)) continue;
                foreach (var (label, d) in new[] { ("下り", Dir.Down), ("上り", Dir.Up) })
                {
                    if (!st.Value.TryGetProperty(label, out var m)) continue;
                    station.DefaultTracks[d] = m.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt32());
                }
            }

            var rank = 0;
            foreach (var t in root.GetProperty("typeRank").EnumerateArray()) data.TypeRank[t.GetString()!] = rank++;
            foreach (var p in root.GetProperty("passingStations").EnumerateArray())
            {
                var ps = new PassingStation(Str(p, "station"), p.GetProperty("下り").GetInt32(), p.GetProperty("上り").GetInt32());
                data.PassingStations[ps.StationId] = ps;
            }

            data.MaxPassingWaits = root.GetProperty("maxPassingWaits").GetInt32();
            data.PassingMarginSeconds = root.GetProperty("passingMarginSeconds").GetInt32();
            data.PassingToleranceSeconds = root.GetProperty("passingToleranceSeconds").GetInt32();
            data.DefaultDwellSeconds = root.GetProperty("defaultDwellSeconds").GetInt32();
            if (root.TryGetProperty("minLoopDwellSeconds", out var ml)) data.MinLoopDwellSeconds = ml.GetInt32();
            if (root.TryGetProperty("clearTimes", out var ct) && ct.ValueKind == JsonValueKind.Object)
                foreach (var x in ct.EnumerateObject()) data.ClearTimes[x.Name] = x.Value.GetInt32();
            if (root.TryGetProperty("afterStopSeconds", out var ms)) data.AfterStopSeconds = ms.GetInt32();
            if (root.TryGetProperty("departBeforeArrive", out var dba) && dba.ValueKind == JsonValueKind.Array)
                foreach (var x in dba.EnumerateArray()) if (x.GetString() is { Length: > 0 } sid) data.DepartBeforeArrive.Add(sid);
            if (root.TryGetProperty("approachTrigger", out var at) && at.ValueKind == JsonValueKind.Object)
            {
                if (at.TryGetProperty("enabled", out var ae)) data.ApproachTriggerEnabled = ae.GetBoolean();
                if (at.TryGetProperty("margin", out var am)) data.ApproachTriggerMargin = Math.Max(0, am.GetInt32());
                if (at.TryGetProperty("firstHome", out var fh) && fh.ValueKind == JsonValueKind.Object)
                    foreach (var st in fh.EnumerateObject())
                    foreach (var (label, d) in new[] { ("下り", Dir.Down), ("上り", Dir.Up) })
                    {
                        if (!st.Value.TryGetProperty(label, out var v)) continue;
                        // "信号機名"（第1場内だけ先に取る）または { "signal": "信号機名", "withSecond": true }（第2場内もまとめて取る）
                        var name = v.ValueKind == JsonValueKind.String ? v.GetString()
                            : v.ValueKind == JsonValueKind.Object && v.TryGetProperty("signal", out var sg) ? sg.GetString() : null;
                        if (name is not { Length: > 0 }) continue;
                        data.ApproachFirstHome[(st.Name, d)] = name;
                        if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("withSecond", out var ws) && ws.ValueKind == JsonValueKind.True)
                            data.ApproachFirstHomeWithSecond.Add((st.Name, d));
                    }
            }

            if (root.TryGetProperty("departDelaySeconds", out var dd))
                foreach (var st in dd.EnumerateObject())
                {
                    if (!data.Stations.TryGetValue(st.Name, out var station)) continue;
                    foreach (var (label, d) in new[] { ("下り", Dir.Down), ("上り", Dir.Up) })
                    {
                        if (!st.Value.TryGetProperty(label, out var m)) continue;
                        foreach (var t in m.EnumerateObject())
                            if (int.TryParse(t.Name, out var track)) station.DepartDelay[(d, track)] = t.Value.GetInt32();
                    }
                }
        }

        // ---- 信号（Signals.json / SignalTypes.json） ----
        using (var doc = Doc("Signals.json"))
        {
            foreach (var s in doc.RootElement.GetProperty("signals").EnumerateArray())
            {
                var dirText = Str(s, "direction");
                data.Signals[Str(s, "name")] = new SignalDef
                {
                    Name = Str(s, "name"),
                    Type = Str(s, "type") is { Length: > 0 } t ? t : null,
                    Direction = dirText == "下り" ? Dir.Down : dirText == "上り" ? Dir.Up : null,
                    IsRepeater = s.TryGetProperty("isRepeater", out var rep) && rep.GetBoolean(),
                    Next = List(s, "next"),
                    Protects = List(s, "protects"),
                    DirectionRoutes = List(s, "directionRoutes")
                };
            }
        }

        using (var doc = Doc("SignalTypes.json"))
        {
            foreach (var t in doc.RootElement.GetProperty("types").EnumerateObject())
                data.SignalTypes[t.Name] = t.Value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!);
        }

        // ---- 標準運転時分（RunTimes.json）。種別・方向・区間ごとの秒。fromStop / toStop は発駅・着駅の扱い（停・通・運停） ----
        if (File.Exists(Path.Combine(dir, "RunTimes.json")))
        {
            using var doc = Doc("RunTimes.json");
            foreach (var r in doc.RootElement.GetProperty("runTimes").EnumerateArray())
            {
                data.RunTimes.Add(new RunTimeRow(Str(r, "type"), Str(r, "dir") == "下り" ? Dir.Down : Dir.Up, Str(r, "from"), Str(r, "to"),
                    Str(r, "fromStop") != "通", Str(r, "toStop") != "通", r.GetProperty("seconds").GetInt32()));
            }
        }

        // ---- ダイヤ（Timetable.json）。列車ごとに、駅の着発時刻（"HH:mm:ss"）・停車（停）／通過（通）／運転停車（運停）・番線 ----
        // st は LineData.json の駅ID（路線にない場所は null）。書かれていない欄は null
        if (File.Exists(Path.Combine(dir, "Timetable.json")))
        {
            using var doc = Doc("Timetable.json");
            foreach (var t in doc.RootElement.GetProperty("trains").EnumerateArray())
            {
                var number = NormalizeTrain(Str(t, "number"));
                if (number.Length == 0) continue;
                if (!data.Dia.TryGetValue(number, out var cur))
                {
                    var type = Str(t, "type") == "試運転" ? "回送" : Str(t, "type");
                    data.Dia[number] = cur = new DiaTrain { Number = number, Type = type, TypeLabel = Str(t, "label") };
                }

                if (Str(t, "dest") is { Length: > 0 } dest) cur.Dest = dest; // 同じ列番が分けて書かれていたら、あとのもの（先へ行く方）

                if (t.TryGetProperty("next", out var nx) && nx.ValueKind == JsonValueKind.Object
                    && NormalizeTrain(Str(nx, "number")) is { Length: > 0 } nextNumber && Str(nx, "st") is { Length: > 0 } nextStation)
                    cur.Next = (nextNumber, nextStation);

                // 運行番号は "24S" のような文字列（数字だけの 6 も読む）。書いていなければ列番から求める
                cur.Unyo ??= t.TryGetProperty("unyo", out var un) && un.ValueKind is JsonValueKind.Number or JsonValueKind.String
                    && Gokko.Common.Unyo.Normalize(un.ValueKind == JsonValueKind.Number ? un.GetRawText() : un.GetString()) is { } unText
                    ? unText : Gokko.Common.Unyo.FromTrain(number);
                if (t.TryGetProperty("turn", out var tn) && tn.ValueKind == JsonValueKind.Object && Str(tn, "kind") is { Length: > 0 } turnKind)
                    cur.Turn = new DiaTurn(turnKind, NormalizeTrain(Str(tn, "number")) is { Length: > 0 } tno ? tno : null,
                        Str(tn, "time") is { Length: > 0 } ttime ? ttime : null, Str(tn, "how") is { Length: > 0 } thow ? thow : null,
                        Str(tn, "via") is { Length: > 0 } tvia ? tvia : null, Str(tn, "out") is { Length: > 0 } tout ? tout : null,
                        NormalizeTrain(Str(tn, "after")) is { Length: > 0 } tafter ? tafter : null);

                if (cur.Stops.Count == 0)
                    foreach (var s in t.GetProperty("stops").EnumerateArray())
                        cur.Stops.Add(new DiaStop(Str(s, "name"), Str(s, "st") is { Length: > 0 } sid0 ? sid0 : null, Str(s, "stop"),
                            Str(s, "arr") is { Length: > 0 } a0 ? a0 : null, Str(s, "dep") is { Length: > 0 } d0 ? d0 : null,
                            s.TryGetProperty("track", out var tk0) && tk0.ValueKind == JsonValueKind.Number ? tk0.GetInt32() : null,
                            Str(s, "note") is { Length: > 0 } n0 ? n0 : null));

                string? lastStation = null;
                foreach (var s in t.GetProperty("stops").EnumerateArray())
                {
                    var sid = Str(s, "st");
                    lastStation = sid.Length > 0 ? sid : null;
                    if (lastStation == null) continue;
                    var stop = Str(s, "stop") switch { "停" => "停車", "運停" => "運転停車", _ => "通過" };
                    cur.Stations[sid] = (stop, s.TryGetProperty("track", out var tr) && tr.ValueKind == JsonValueKind.Number ? tr.GetInt32() : null);
                }

                // 最後の駅が進路を扱う駅なら、そこが終着
                if (lastStation != null && data.Stations.TryGetValue(lastStation, out var ts) && ts.RouteControl) cur.TerminalId = lastStation;
            }
        }

        ComputeStopProtects(data);
        // 待避・連絡・交換の記事を作るための所定の分を、裏で前もって作っておく（臨時停車・臨時列車を入れたときに軽く済むように）
        _ = Task.Run(() => DiaNotes.Prepare(data._baseDia));
        return data;
    }

    /// <summary>信号機ごとの StopProtects を作る。進路のない信号機（閉塞信号機など）は Protects のまま。防護区間のない信号機は、進路の回路から。</summary>
    private static void ComputeStopProtects(RailData data)
    {
        var bySignal = data.Routes.Values.GroupBy(r => r.Signal).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var s in data.Signals.Values)
        {
            if (!bySignal.TryGetValue(s.Name, out var own)) { s.StopProtects = s.Protects; continue; }
            var ownCircuits = own.SelectMany(r => r.Circuits).ToHashSet();
            var interlocks = own.Select(r => r.Interlock).ToHashSet();
            var others = data.Routes.Values.Where(r => interlocks.Contains(r.Interlock) && r.Signal != s.Name)
                .SelectMany(r => r.Circuits).Where(c => !ownCircuits.Contains(c)).ToHashSet();
            // 防護区間が書かれていない信号機（行き止まりの番線への場内・入換。江ノ原信号場上り場内1RB・1RA、館浜下り場内5L など）は、
            // 自分の進路の回路で見る。書かれていないと、その番線にほかの列車がいても停止にならない
            // （写した進路がゲームに残ったとき、ほかの列車がいる12番線へ進行を出してしまう）
            var protects = s.Protects.Count > 0 ? s.Protects : own.SelectMany(r => r.Circuits).Distinct().ToList();
            s.StopProtects = protects.Where(c => !others.Contains(c)).ToList();
            if (own.Count > 1)
                s.SkipByRoute = own.ToDictionary(r => (r.Interlock, r.Name),
                    r => s.StopProtects.Where(c => ownCircuits.Contains(c) && !r.Circuits.Contains(c)).ToHashSet());
        }
    }
}
