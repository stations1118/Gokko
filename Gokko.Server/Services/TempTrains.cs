using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gokko.Server.Services;

/// <summary>
/// 催物用の臨時列車（運転課の画面 untenka.html で作り、司令用管理画面で適用する）。Data/TempTrains.json に保存する。
///
/// ・グループ（例「野球臨」）に、運行番号ごとの列車（列番・種別・行先・駅ごとの停車/通過・着・発・番線）をまとめる
/// ・状態: 下書き → 申請中 → 承認（差戻しは理由を付けて下書きと同じく直せる）。申請は誰でも、承認は運転課課長代理だけ
/// ・承認したグループを司令用管理画面で適用すると、所定のダイヤに重ねる（<see cref="RailData.SetExtraTrains"/>）。
///   所定のダイヤ（Timetable.json）は変えない。取消で外す（走っている自動運転の列車も消す）
/// ・同じ運行番号の中で、ある列車の終点から次の列車が出るなら、折返でつなぐ（自動運転の列車がそのまま続けて走る）
/// ・適用中かどうかも保存し、サーバーを起動したときに重ね直す
/// </summary>
public sealed class TempTrains
{
    public const string FileName = "TempTrains.json";

    public const string Draft = "下書き";
    public const string Pending = "申請中";
    public const string Approved = "承認";
    public const string Rejected = "差戻し";

    /// <summary>種別 → 列番の末尾の文字（RouteEngine の列番の読み方と同じ）。普通は文字なし、回送は頭に「回」。</summary>
    public static readonly (string Type, string Suffix)[] Types =
        [("特急", "A"), ("快急", "K"), ("急行", "B"), ("区急", "D"), ("準急", "C"), ("普通", ""), ("回送", "")];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _lock = new();
    private readonly string _path;
    private readonly RailData _rail;
    private readonly ILogger<TempTrains>? _log;
    private List<Group> _groups = new();
    private int _seq;

    public sealed class FileShape
    {
        public int Seq { get; set; }
        public List<Group> Groups { get; set; } = new();
    }

    public sealed class Group
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = Draft;
        public string Owner { get; set; } = "";
        public string OwnerName { get; set; } = "";
        /// <summary>差戻しの理由・承認のひとこと。</summary>
        public string Comment { get; set; } = "";
        public string ApprovedBy { get; set; } = "";
        public DateTime UpdatedUtc { get; set; }
        /// <summary>司令用管理画面で適用しているか。</summary>
        public bool Applied { get; set; }
        /// <summary>司令用管理画面で「走らせない」にした運行番号（適用しても、この運行番号の列車は出さない）。</summary>
        public List<string> OffUnyo { get; set; } = new();
        /// <summary>
        /// 終夜運転（終電から始発までの間に走らせる）。4時〜6時の時刻を翌朝（28時〜30時）として扱い、翌6時までのスジを引ける。
        /// 後運用に翌朝の所定の列車（始発）を選べる。
        /// </summary>
        public bool Overnight { get; set; }
        public List<TrainSpec> Trains { get; set; } = new();

        /// <summary>走らせる列車（走らせないにした運行番号の列車を除く）。</summary>
        public IEnumerable<TrainSpec> Running() => Trains.Where(t => !OffUnyo.Contains(t.Unyo));
    }

    public sealed class TrainSpec
    {
        /// <summary>運行番号（"24"・"24S"）。前の形の数字も読む。</summary>
        [System.Text.Json.Serialization.JsonConverter(typeof(UnyoJsonConverter))]
        public string Unyo { get; set; } = "";
        public string Number { get; set; } = "";
        public string Type { get; set; } = "";
        /// <summary>種別表記（出発信号機の列選に出す。C特1・急行 など）。空なら種別。</summary>
        public string Label { get; set; } = "";
        public string Dest { get; set; } = "";
        public List<StopSpec> Stops { get; set; } = new();

        /// <summary>
        /// 終点のあと。空は自動（同じ運行番号で、終点から次に出る列車へそのまま折り返す）、「なし」は折り返さない、
        /// 「発」はそのまま（着いた番線から）TurnNext として出る、「入換」は TurnVia の線へ入換してから TurnNext として出る、
        /// 「種別変更」は向きを変えずに、着いた番線から列番・種別を変えて TurnNext として続けて走る（時刻表の途中駅で列番が変わる列車と同じ）。
        /// </summary>
        public string TurnHow { get; set; } = "";
        /// <summary>後運用の列番（同じグループの列車。増結車の連結する相手は所定の列車でもよい）。</summary>
        public string TurnNext { get; set; } = "";
        /// <summary>入換の時刻（入換のときだけ。空なら後運用の発車の2分前）。</summary>
        public string? TurnTime { get; set; }
        /// <summary>入換で入る線（TrackRoutes.json の shuntOut の名前。空なら着いた番線からの最初のもの）。</summary>
        public string TurnVia { get; set; } = "";
        /// <summary>両数（0 なら運行番号ごとの既定。自動運転の列車の長さ・連結の位置に使う）。</summary>
        public int Cars { get; set; }
        /// <summary>
        /// 前運用: 留置になる所定の列車（滞泊など、終点のあとがない列車）の列番。空ならなし。
        /// その列車が発駅の同じ番線に着いたあと、そのままこの列車として出る（所定の列車の「終点のあと」を、この列車への折返にする）。
        /// </summary>
        public string From { get; set; } = "";
    }

    /// <summary>1駅。Stop は「停」か「通」。時刻は "HH:mm:ss"（"HH:mm" でもよい）。Track は番線。</summary>
    public sealed class StopSpec
    {
        public string St { get; set; } = "";
        public string Stop { get; set; } = "通";
        public string? Arr { get; set; }
        public string? Dep { get; set; }
        public int? Track { get; set; }
    }

    public TempTrains(string dataDir, RailData rail, ILogger<TempTrains>? log = null)
    {
        _path = Path.Combine(dataDir, FileName);
        _rail = rail;
        _log = log;
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<FileShape>(File.ReadAllText(_path), Json) is { } f)
            {
                _groups = f.Groups;
                _seq = Math.Max(f.Seq, _groups.Count);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "{File} を読めませんでした", FileName);
        }

        lock (_lock)
        {
            // 起動したとき: 適用していたグループを重ね直す（承認でなくなっていたものは外す）
            foreach (var g in _groups.Where(g => g.Applied && g.Status != Approved)) g.Applied = false;
            if (_groups.Any(g => g.Applied))
            {
                var msg = ApplyLayer();
                if (msg != null) _log?.LogWarning("臨時列車: {Message}", msg);
            }
        }
    }

    /// <summary>適用・取消で、ダイヤから外した列番（走っている自動運転の列車を消すため）。</summary>
    public event Action<IReadOnlyCollection<string>>? Removed;

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new FileShape { Seq = _seq, Groups = _groups }, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "{File} に保存できませんでした", FileName);
        }
    }

    // =========================================================================================
    // 一覧・編集（運転課の画面）
    // =========================================================================================

    public List<Group> Groups()
    {
        lock (_lock) return _groups.Select(Clone).ToList();
    }

    public Group? Get(string? id)
    {
        lock (_lock) return _groups.FirstOrDefault(g => g.Id == id) is { } g ? Clone(g) : null;
    }

    private static Group Clone(Group g) => JsonSerializer.Deserialize<Group>(JsonSerializer.Serialize(g, Json), Json)!;

    private static bool Editable(Group g) => g.Status is Draft or Rejected;

    /// <summary>グループを作る・直す（下書き・差戻しのものだけ、作った人が）。うまくいけば (id, 注意) を返す。</summary>
    public (string? Id, string? Error, List<string> Warnings) Save(string? id, string name, List<TrainSpec> trains, string user, string userName, bool overnight = false)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return (null, "グループの名前を入れてください", new());
        if (name.Length > 30) return (null, "グループの名前は30文字までにしてください", new());
        if ((trains?.Count ?? 0) > 100) return (null, "1つのグループの列車は100本までにしてください", new());
        trains = Normalize(trains ?? new(), overnight);
        var (errors, warnings) = Check(trains, id, overnight);
        if (errors.Count > 0) return (null, string.Join("\n", errors), warnings);
        lock (_lock)
        {
            Group g;
            if (string.IsNullOrEmpty(id))
            {
                g = new Group { Id = $"g{++_seq}", Owner = user, OwnerName = userName };
                _groups.Add(g);
            }
            else
            {
                g = _groups.FirstOrDefault(x => x.Id == id)!;
                if (g == null) return (null, "そのグループはありません", warnings);
                if (g.Owner != user) return (null, "作った人だけが直せます", warnings);
                if (!Editable(g)) return (null, $"{g.Status}のグループは直せません（取り下げて下書きに戻してください）", warnings);
            }
            g.Name = name;
            g.Overnight = overnight;
            g.Trains = trains;
            g.UpdatedUtc = DateTime.UtcNow;
            Save();
            return (g.Id, null, warnings);
        }
    }

    /// <summary>
    /// 状態を変える。action: submit（申請。作った人）・withdraw（取り下げて下書きへ。作った人か課長代理。適用中は不可）・
    /// approve / reject（課長代理。申請中のものだけ）・delete（作った人。適用中・承認は不可）。
    /// </summary>
    public string? Act(string? id, string action, string user, bool approver, string? comment)
    {
        lock (_lock)
        {
            var g = _groups.FirstOrDefault(x => x.Id == id);
            if (g == null) return "そのグループはありません";
            var mine = g.Owner == user;
            switch (action)
            {
                case "submit":
                    if (!mine) return "作った人だけが申請できます";
                    if (!Editable(g)) return $"{g.Status}のグループは申請できません";
                    if (g.Trains.Count == 0) return "列車がありません";
                    var (errors, _) = Check(g.Trains, g.Id, g.Overnight);
                    if (errors.Count > 0) return string.Join("\n", errors);
                    g.Status = Pending;
                    g.Comment = "";
                    break;
                case "withdraw":
                    if (!mine && !approver) return "作った人か運転課課長代理だけが取り下げられます";
                    if (g.Applied) return "適用中です。先に司令用管理画面で取り消してください";
                    if (g.Status is not (Pending or Approved)) return "申請中・承認のグループだけ取り下げられます";
                    g.Status = Draft;
                    g.ApprovedBy = "";
                    break;
                case "approve":
                    if (!approver) return "運転課課長代理だけが承認できます";
                    if (g.Status != Pending) return "申請中のグループだけ承認できます";
                    var (errs, _) = Check(g.Trains, g.Id, g.Overnight);
                    if (errs.Count > 0) return string.Join("\n", errs);
                    g.Status = Approved;
                    g.ApprovedBy = user;
                    g.Comment = (comment ?? "").Trim();
                    break;
                case "reject":
                    if (!approver) return "運転課課長代理だけが差し戻せます";
                    if (g.Status != Pending) return "申請中のグループだけ差し戻せます";
                    g.Status = Rejected;
                    g.Comment = (comment ?? "").Trim();
                    break;
                case "delete":
                    if (!mine && !approver) return "作った人か運転課課長代理だけが消せます";
                    if (g.Applied || g.Status == Approved) return "承認したグループは消せません（取り下げてから消してください）";
                    _groups.Remove(g);
                    Save();
                    return null;
                default:
                    return "その操作はありません";
            }
            g.UpdatedUtc = DateTime.UtcNow;
            Save();
            return null;
        }
    }

    // =========================================================================================
    // 適用・取消（司令用管理画面）
    // =========================================================================================

    /// <summary>グループを適用する（on）・取り消す。うまくいかなければ理由を返す。</summary>
    public string? Apply(string? id, bool on)
    {
        List<string> removed;
        lock (_lock)
        {
            var g = _groups.FirstOrDefault(x => x.Id == id);
            if (g == null) return "そのグループはありません";
            if (on && g.Status != Approved) return "承認されたグループだけ適用できます";
            if (g.Applied == on) return null;
            if (on)
            {
                // ほかの適用中のグループ・所定のダイヤと列番がかぶらないか
                var used = _groups.Where(x => x.Applied).SelectMany(x => x.Running()).Select(t => t.Number).ToHashSet();
                var dup = g.Running().Select(t => t.Number).Where(n => used.Contains(n) || _rail.BaseDia.ContainsKey(n)).ToList();
                if (dup.Count > 0) return $"列番がほかの列車とかぶっています: {string.Join("、", dup)}";
            }
            g.Applied = on;
            removed = on ? new() : g.Trains.Select(t => t.Number).ToList();
            var msg = ApplyLayer();
            if (msg != null) { g.Applied = !on; ApplyLayer(); return msg; }
            Save();
        }
        if (removed.Count > 0) Removed?.Invoke(removed);
        return null;
    }

    /// <summary>
    /// グループの運行番号ごとに、走らせるか（on）・走らせないかを決める（適用の前でも後でもよい）。
    /// 適用中なら、すぐダイヤに重ね直す（走らせないにした運行番号の、走っている自動運転の列車は消す）。うまくいかなければ理由を返す。
    /// </summary>
    public string? SetUnyo(string? id, string? unyo, bool on)
    {
        List<string> removed;
        lock (_lock)
        {
            var g = _groups.FirstOrDefault(x => x.Id == id);
            if (g == null) return "そのグループはありません";
            var u = (unyo ?? "").Trim();
            if (!g.Trains.Any(t => t.Unyo == u)) return $"このグループに運行番号 {u} の列車はありません";
            if (g.OffUnyo.Contains(u) != on) return null;
            if (on && g.Applied)
            {
                var used = _groups.Where(x => x.Applied).SelectMany(x => x.Running()).Select(t => t.Number).ToHashSet();
                var dup = g.Trains.Where(t => t.Unyo == u).Select(t => t.Number).Where(n => used.Contains(n) || _rail.BaseDia.ContainsKey(n)).ToList();
                if (dup.Count > 0) return $"列番がほかの列車とかぶっています: {string.Join("、", dup)}";
            }
            if (on) g.OffUnyo.Remove(u); else g.OffUnyo.Add(u);
            removed = !on && g.Applied ? g.Trains.Where(t => t.Unyo == u).Select(t => t.Number).ToList() : new();
            if (g.Applied && ApplyLayer() is { } msg)
            {
                if (on) g.OffUnyo.Add(u); else g.OffUnyo.Remove(u);
                ApplyLayer();
                return msg;
            }
            Save();
        }
        if (removed.Count > 0) Removed?.Invoke(removed);
        return null;
    }

    /// <summary>適用中のグループの列車を、所定のダイヤに重ねる（_lock の中）。うまくいかなければ理由を返す。走らせないにした運行番号の列車は除く。</summary>
    private string? ApplyLayer()
    {
        var extra = new List<DiaTrain>();
        var replace = new List<DiaTrain>();
        foreach (var g in _groups.Where(g => g.Applied))
        {
            var (e, r) = Build(g.Running().ToList(), g.Overnight);
            extra.AddRange(e);
            replace.AddRange(r);
        }
        // 終夜運転: 同じ所定の列車を、2つの臨時列車の前運用・後運用にしない
        var twice = replace.GroupBy(d => d.Number).Where(x => x.Count() > 1).Select(x => x.Key)
            .Concat(extra.Where(d => d.Turn is { Kind: "折返", Number: { } tn } && _rail.BaseDia.ContainsKey(tn)).GroupBy(d => d.Turn!.Number!).Where(x => x.Count() > 1).Select(x => x.Key)).ToList();
        if (twice.Count > 0)
            return $"所定の列車 {string.Join("、", twice)} を、2本の臨時列車でつないでいます（ほかの適用中のグループと重なっています）";
        var skipped = _rail.SetExtraTrains("催物", extra, replace);
        return skipped.Count > 0 ? $"所定のダイヤにある列番は足せませんでした: {string.Join("、", skipped)}" : null;
    }

    // =========================================================================================
    // 駅の並び: 路線の駅（ゾーン）に、時刻表に載っている路線の外の駅（大路〜三郷の手前など）を足したもの。
    // 路線の外の駅には、時刻表の列車の駅の並びから、路線の端の外側へ続くゾーン（2ずつ）を振る。進路は扱わない
    // =========================================================================================

    public sealed record ExtStation(string Id, string Name, int Zone, bool Control);

    private List<ExtStation>? _ext;
    private Dictionary<string, ExtStation>? _extById;

    /// <summary>駅の並び（ゾーンの順）。所定の時刻表から1回だけ作る。</summary>
    public IReadOnlyList<ExtStation> Stations
    {
        get
        {
            if (_ext != null) return _ext;
            var line = _rail.Line;
            var list = line.StationIds.Where(id => line.ZoneOfStation(id) >= 0)
                .Select(id => new ExtStation(id, line.StationName(id), line.ZoneOfStation(id), _rail.Stations.TryGetValue(id, out var d) && d.RouteControl)).ToList();
            if (list.Count > 0)
            {
                int zmin = list.Min(s => s.Zone), zmax = list.Max(s => s.Zone);
                string minId = list.First(s => s.Zone == zmin).Id, maxId = list.First(s => s.Zone == zmax).Id;
                var names = new Dictionary<string, string>();
                List<string> before = new(), after = new();
                foreach (var dia in _rail.BaseDia.Values)
                {
                    var ids = dia.Stops.Where(s => s.StationId != null).Select(s => s.StationId!).ToList();
                    foreach (var s in dia.Stops) if (s.StationId is { } sid && line.ZoneOfStation(sid) < 0) names.TryAdd(sid, s.Name);
                    // 路線の端の駅から外へ、路線の外の駅が続く所（いちばん長く続く列車の並び）
                    foreach (var (edge, isBefore) in new[] { (minId, true), (maxId, false) })
                    {
                        var i = ids.IndexOf(edge);
                        if (i < 0) continue;
                        foreach (var step in new[] { -1, 1 })
                        {
                            var seq = new List<string>();
                            for (var j = i + step; j >= 0 && j < ids.Count && line.ZoneOfStation(ids[j]) < 0; j += step) seq.Add(ids[j]);
                            ref var best = ref isBefore ? ref before : ref after;
                            if (seq.Count > best.Count) best = seq;
                        }
                    }
                }
                for (var k = 0; k < before.Count; k++)
                    if (list.All(s => s.Id != before[k])) list.Add(new ExtStation(before[k], names.GetValueOrDefault(before[k], before[k]), zmin - 2 * (k + 1), false));
                for (var k = 0; k < after.Count; k++)
                    if (list.All(s => s.Id != after[k])) list.Add(new ExtStation(after[k], names.GetValueOrDefault(after[k], after[k]), zmax + 2 * (k + 1), false));
            }
            _extById = list.ToDictionary(s => s.Id);
            return _ext = list.OrderBy(s => s.Zone).ToList();
        }
    }

    /// <summary>駅の並びのゾーン（路線の外の駅も）。並びにない駅は null。</summary>
    public int? ZoneOf(string? id) { _ = Stations; return id != null && _extById!.TryGetValue(id, out var s) ? s.Zone : null; }

    private Dictionary<(string From, string To), double>? _minRuns;

    /// <summary>
    /// 駅間の最速タイム（秒）: 所定の時刻表で、隣り合う駅（駅の並びの順）の間を走るのにかかったいちばん短い時間
    /// （前の駅を出て〔通過して〕から、次の駅に着く〔通過する〕まで）と、標準運転時分のいちばん短いもの。向きごと。所定の時刻表は変わらないので、1回だけ作る
    /// </summary>
    public IReadOnlyDictionary<(string From, string To), double> MinRuns
    {
        get
        {
            if (_minRuns != null) return _minRuns;
            var m = new Dictionary<(string, string), double>();
            foreach (var d in _rail.BaseDia.Values)
            {
                var pts = d.Stops.Where(s => s.StationId != null && ZoneOf(s.StationId) != null).ToList();
                for (var i = 1; i < pts.Count; i++)
                {
                    if (DaySec(pts[i - 1].Dep ?? pts[i - 1].Arr) is not { } ta || DaySec(pts[i].Arr ?? pts[i].Dep) is not { } tb || tb <= ta) continue;
                    var key = (pts[i - 1].StationId!, pts[i].StationId!);
                    if (!m.TryGetValue(key, out var cur) || tb - ta < cur) m[key] = tb - ta;
                }
            }
            // 標準運転時分（RunTimes.json）のほうが短い区間は、そちら（所定の時刻表には余裕があるため。どの種別・停車／通過でもいちばん短いもの）
            foreach (var r in _rail.RunTimes)
            {
                if (StationIdByName(r.From) is not { } f || StationIdByName(r.To) is not { } to || r.Seconds <= 0) continue;
                if (!m.TryGetValue((f, to), out var cur) || r.Seconds < cur) m[(f, to)] = r.Seconds;
            }
            return _minRuns = m;
        }
    }

    public string StationName(string id) { _ = Stations; return _extById!.TryGetValue(id, out var s) ? s.Name : _rail.Line.StationName(id); }

    /// <summary>駅名 → 駅 ID（路線の外の駅も）。</summary>
    public string? StationIdByName(string name) => _rail.Line.StationIdByName(name) ?? Stations.FirstOrDefault(s => s.Name == name)?.Id;

    // =========================================================================================
    // 入力の確かめ・ダイヤの列車に直す
    // =========================================================================================

    private static readonly Regex TimeRe = new(@"^(\d{1,2}):(\d{2})(?::(\d{2}))?$");

    /// <summary>"H:mm" "HH:mm:ss" → "HH:mm:ss"。読めなければ null。</summary>
    public static string? NormTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = TimeRe.Match(s.Normalize(System.Text.NormalizationForm.FormKC).Trim());
        if (!m.Success) return null;
        int h = int.Parse(m.Groups[1].Value), mi = int.Parse(m.Groups[2].Value), se = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        if (h > 29 || mi > 59 || se > 59) return null; // 終夜運転は翌6時（30時）の手前まで
        return $"{h % 24:00}:{mi:00}:{se:00}";
    }

    /// <summary>運転日の秒（4時前は翌日）。</summary>
    private static double? DaySec(string? s) => DaySecAt(s, false);

    /// <summary>運転日の秒。ふだんは4時前、終夜運転（overnight）は6時前を翌日（24時以降）とする。</summary>
    private static double? DaySecAt(string? s, bool overnight)
    {
        if (NormTime(s) is not { } t) return null;
        var p = t.Split(':');
        var v = int.Parse(p[0]) * 3600 + int.Parse(p[1]) * 60 + int.Parse(p[2]);
        return v < (overnight ? 6 : 4) * 3600 ? v + 86400 : v;
    }

    private List<TrainSpec> Normalize(List<TrainSpec> trains, bool overnight = false)
    {
        double? DaySec(string? s) => DaySecAt(s, overnight);
        foreach (var t in trains)
        {
            t.Number = RailData.NormalizeTrain(t.Number);
            t.Unyo = Gokko.Common.Unyo.Normalize(t.Unyo) ?? "";
            t.Type = (t.Type ?? "").Trim();
            t.Label = (t.Label ?? "").Trim();
            t.Dest = (t.Dest ?? "").Trim();
            t.TurnHow = t.TurnHow is "なし" or "発" or "入換" or "種別変更" or "連結" ? t.TurnHow : "";
            t.Cars = Math.Clamp(t.Cars, 0, 10);
            t.TurnNext = t.TurnHow is "発" or "入換" or "種別変更" or "連結" ? RailData.NormalizeTrain(t.TurnNext) : "";
            t.TurnTime = t.TurnHow == "入換" ? NormTime(t.TurnTime) ?? (string.IsNullOrWhiteSpace(t.TurnTime) ? null : t.TurnTime) : null;
            t.TurnVia = t.TurnHow == "入換" ? (t.TurnVia ?? "").Trim() : "";
            t.From = RailData.NormalizeTrain(t.From);
            t.Stops ??= new();
            foreach (var s in t.Stops)
            {
                s.St = (s.St ?? "").Trim();
                s.Stop = s.Stop == "停" ? "停" : "通";
                s.Arr = s.Stop == "停" ? NormTime(s.Arr) ?? (string.IsNullOrWhiteSpace(s.Arr) ? null : s.Arr) : null;
                s.Dep = NormTime(s.Dep) ?? (string.IsNullOrWhiteSpace(s.Dep) ? null : s.Dep);
            }
        }
        return trains.OrderBy(t => t.Unyo, Gokko.Common.Unyo.Comparer).ThenBy(t => t.Stops.Select(s => DaySec(s.Dep ?? s.Arr)).FirstOrDefault(x => x != null) ?? 0).ToList();
    }

    /// <summary>列番の列車: このグループの列車、なければ所定の時刻表の列車（駅の時刻・番線だけ。連結・切り離しの相手を確かめるのに使う）。</summary>
    private TrainSpec? FindTrain(string number, List<TrainSpec> trains)
    {
        if (number.Length == 0) return null;
        if (trains.FirstOrDefault(x => x.Number == number) is { } mine) return mine;
        if (!_rail.BaseDia.TryGetValue(number, out var d)) return null;
        return new TrainSpec
        {
            Number = d.Number, Type = d.Type, Unyo = d.Unyo ?? "", Cars = d.Cars ?? 0,
            Stops = d.Stops.Where(s => s.StationId != null).Select(s => new StopSpec { St = s.StationId!, Stop = s.Stop == "停" ? "停" : "通", Arr = s.Arr, Dep = s.Dep, Track = s.Track }).ToList()
        };
    }

    /// <summary>所定の列車の前の列車（所定の時刻表で、折り返し・途中で列番が変わってその列車になる列車）。なければ null。</summary>
    private string? BasePred(string number) =>
        _rail.BaseDia.Values.FirstOrDefault(d => d.Turn is { Kind: "折返", Number: { } tn } && tn == number || d.Next?.Number == number)?.Number;

    /// <summary>所定の列車を写して、「終点のあと」だけ差し替えたもの（終夜運転で、留置になる列車を臨時列車につなぐ）。</summary>
    private static DiaTrain WithTurn(DiaTrain d, DiaTurn turn) => d.WithTurn(turn);

    /// <summary>
    /// 構内入換: 1駅だけの列車で、このグループの列車の終点のあと「入換」の後運用になっているもの（その列車の終点と同じ駅）。
    /// 江ノ原信号場の 10番線 → 13番線 → 11番線 のように、駅の中で番線を移るのに使う（入換1回が1列車）。
    /// </summary>
    private static bool IsYardShunt(TrainSpec t, List<TrainSpec> trains) =>
        t.Stops.Count == 1 && t.Number.Length > 0
        && trains.Any(p => p != t && p.TurnHow == "入換" && p.TurnNext == t.Number && p.Stops.Count > 0 && p.Stops[^1].St == t.Stops[0].St);

    /// <summary>種別に合う列番の形か（末尾の文字・回送の「回」）。</summary>
    private static string? NumberTypeError(TrainSpec t)
    {
        if (RouteEngine.TrainInfo(t.Number) is not { } info) return $"{t.Number}: 列番に数字がありません";
        if (info.Type == t.Type) return null;
        var suffix = Types.FirstOrDefault(x => x.Type == t.Type).Suffix;
        return t.Type == "回送" ? $"{t.Number}: 回送の列番は「回」で始めてください"
            : t.Type == "普通" ? $"{t.Number}: 普通の列番は、数字のあとに種別の文字（A・B・C・D・K）を付けないでください"
            : $"{t.Number}: {t.Type}の列番は、末尾を {suffix} にしてください";
    }

    /// <summary>
    /// 入力を確かめる。errors があれば保存・申請・承認できない。warnings は知らせるだけ。
    /// 確かめること: 列番の形（種別・向き）・列番のかぶり・駅の並び・時刻の順・停車駅の番線
    /// </summary>
    public (List<string> Errors, List<string> Warnings) Check(List<TrainSpec> trains, string? groupId, bool overnight = false)
    {
        double? DaySec(string? s) => DaySecAt(s, overnight);
        var errors = new List<string>();
        var warnings = new List<string>();
        var downUp = DownIncreasing();
        var seen = new HashSet<string>();
        var types = Types.Select(x => x.Type).ToHashSet();
        foreach (var t in trains)
        {
            var n = t.Number;
            if (n.Length == 0) { errors.Add("列番が空の列車があります"); continue; }
            if (!seen.Add(n)) errors.Add($"{n}: 同じ列番が2本あります");
            if (_rail.BaseDia.ContainsKey(n)) errors.Add($"{n}: 所定の時刻表にある列番です");
            if (t.Unyo.Length == 0) errors.Add($"{n}: 運行番号を入れてください（24・24S など）");
            if (!types.Contains(t.Type)) { errors.Add($"{n}: 種別を選んでください"); continue; }
            if (NumberTypeError(t) is { } te) errors.Add(te);
            // 増結車（連結）と構内入換（前の列車の終点のあと「入換」の後運用で、その駅の1駅だけ）は、1駅だけでよい。
            // 切り離した車両（解＋元の列番）も、切り離した駅で後運用の列車になるなら1駅だけでよい（前運用の「切り離し」で作る）
            var yard = IsYardShunt(t, trains) || n.StartsWith("解") && t.TurnHow is "発" or "入換" or "種別変更" && t.TurnNext.Length > 0;
            if (t.Stops.Count < (t.TurnHow == "連結" || yard ? 1 : 2))
            {
                errors.Add(t.Stops.Count == 1
                    ? $"{n}: 駅を2つ以上入れてください（駅の中だけで入換する列車なら、前の列車の終点のあとを「入換」にして、この列車を後運用に選んでください）"
                    : $"{n}: 駅を{(t.TurnHow == "連結" ? 1 : 2)}つ以上入れてください");
                continue;
            }

            // 駅は路線の駅で、向きに沿って並んでいること
            var zs0 = t.Stops.Select(s => ZoneOf(s.St)).ToList();
            if (zs0.Any(z => z == null)) { errors.Add($"{n}: 路線にない駅があります"); continue; }
            var zones = zs0.Select(z => z!.Value).ToList();
            var inc = zones[^1] > zones[0];
            // 1駅だけの列車（構内入換・増結車）は、列番の数字の最後（奇数＝下り、偶数＝上り）で向きを決める
            Dir? trainDir = zones.Count < 2 ? RouteEngine.TrainInfo(n)?.Dir : downUp is { } du0 ? (inc == du0 ? Dir.Down : Dir.Up) : null;
            for (var i = 1; i < zones.Count; i++)
                if ((zones[i] > zones[i - 1]) != inc || zones[i] == zones[i - 1]) { errors.Add($"{n}: 駅の並びが逆になっている所があります"); break; }
            if (zones.Count >= 2 && RouteEngine.TrainInfo(n) is { } info && downUp is { } du && (info.Dir == Dir.Down) != (inc == du))
                errors.Add($"{n}: 列番の数字の最後が{(info.Dir == Dir.Down ? "奇数（下り）" : "偶数（上り）")}ですが、駅の並びは{(info.Dir == Dir.Down ? "上り" : "下り")}です");
            if (t.Stops[0].Stop != "停" || t.Stops[^1].Stop != "停") errors.Add($"{n}: 発駅と終点は停車にしてください");

            // 時刻: 停車駅は着・発（始発は発だけ、終点は着だけでよい）、通過駅は通過の時刻。前の駅より前にならないこと
            double? prev = null;
            for (var i = 0; i < t.Stops.Count; i++)
            {
                var s = t.Stops[i];
                var name = StationName(s.St);
                var first = i == 0;
                var last = i == t.Stops.Count - 1;
                if (s.Arr != null && DaySec(s.Arr) == null || s.Dep != null && DaySec(s.Dep) == null) { errors.Add($"{n} {name}: 時刻の形が違います（例 10:05 か 10:05:30）"); continue; }
                if (last ? s.Arr == null && s.Dep == null : s.Dep == null) { errors.Add($"{n} {name}: {(last ? "着" : s.Stop == "停" ? "発" : "通過")}の時刻を入れてください"); continue; }
                foreach (var v in new[] { DaySec(s.Arr), DaySec(s.Dep) })
                {
                    if (v == null) continue;
                    if (prev != null && v < prev) { errors.Add($"{n} {name}: 前の駅より早い時刻になっています"); break; }
                    prev = v;
                }
                if (_rail.Stations.TryGetValue(s.St, out var st) && st.RouteControl && s.Stop == "停" && s.Track == null && !first)
                    errors.Add($"{n} {name}: 停車する駅の番線を入れてください");
                if (first && _rail.Stations.TryGetValue(s.St, out var st0) && st0.RouteControl && s.Track == null)
                    errors.Add($"{n} {name}: 発駅の番線を入れてください");
                // 入れない番線（その向きの場内・出発の進路がない番線）
                if (s.Track is { } tk && trainDir is { } td && _rail.Stations.TryGetValue(s.St, out var sr) && sr.RouteControl
                    && !TrackAllowed(sr.Routes[td], tk, first, i == t.Stops.Count - 1))
                    errors.Add($"{n} {name}: {(td == Dir.Down ? "下り" : "上り")}は{tk}番線に入れません");
            }

            // 駅間の最速タイム（所定の時刻表でいちばん短い運転時間）より短い駅間は知らせる
            for (var i = 1; i < t.Stops.Count; i++)
            {
                var a = t.Stops[i - 1]; var b = t.Stops[i];
                if (DaySec(a.Dep ?? a.Arr) is not { } ta || DaySec(b.Arr ?? b.Dep) is not { } tb || !MinRuns.TryGetValue((a.St, b.St), out var min)) continue;
                if (tb - ta < min) warnings.Add($"{n} {StationName(a.St)}→{StationName(b.St)}: {tb - ta:0}秒は、所定の最速（{min:0}秒）より短いです");
            }

            // 解放（列番が「解」＋元の列車の列番。時刻表の 解2076C と同じ）: 発駅で元の列車から切り離して出る。
            // 元の列車がこのグループにあれば、その駅に停車し、同じ番線で、元の列車が着いたあとに出るか
            // 元の列車は所定の列車でもよい（終点に限らず、途中の停車駅でも切り離せる）
            if (n.StartsWith("解") && FindTrain(n[1..], trains) is { } src)
            {
                var first = t.Stops[0];
                var fname = StationName(first.St);
                var ss = src.Stops.FirstOrDefault(s => s.St == first.St);
                if (ss != null && ss == src.Stops[0]) errors.Add($"{n}: {src.Number} は{fname}から出る列車です（切り離すのは、着いた駅でです）");
                else if (ss == null || ss.Stop != "停") errors.Add($"{n}: 元の列車 {src.Number} は{fname}に停車しません（切り離す駅に停車させてください）");
                else
                {
                    if (first.Track != null && ss.Track != null && first.Track != ss.Track)
                        errors.Add($"{n}: {src.Number} は{fname} {ss.Track}番線に着きます。切り離した車両も {ss.Track}番線から出してください");
                    if (DaySec(first.Dep) is { } cd && DaySec(ss.Arr ?? ss.Dep) is { } sa && cd < sa)
                        errors.Add($"{n}: {src.Number} が{fname}に着くより前に出ることになっています");
                    if (t.Cars > 0 && src.Cars > 0 && t.Cars >= src.Cars)
                        errors.Add($"{n}: 切り離す両数（{t.Cars}両）が、{src.Number} の両数（{src.Cars}両）以上です");
                }
            }

            // 前運用（終夜運転など）: 留置になる所定の列車が、発駅の同じ番線に着いたあと、そのままこの列車として出る
            if (t.From.Length > 0)
            {
                var first = t.Stops[0];
                var fname = StationName(first.St);
                if (!_rail.BaseDia.TryGetValue(t.From, out var fd)) errors.Add($"{n}: 前運用の {t.From} は所定の時刻表にありません");
                else if (fd.Turn is { Kind: not "滞泊" } || fd.Next != null) errors.Add($"{n}: 所定の {t.From} は終点のあとも走る列車です（前運用にできるのは、留置になる列車だけです）");
                else if (fd.Stops.LastOrDefault() is not { } fl || fl.StationId != first.St) errors.Add($"{n}: 所定の {t.From} は{fname}で終わる列車ではありません");
                else
                {
                    if (fl.Track != null && first.Track != null && fl.Track != first.Track)
                        errors.Add($"{n}: 所定の {t.From} は{fname} {fl.Track}番線に着きます。この列車も {fl.Track}番線から出してください");
                    if (DaySec(fl.Arr ?? fl.Dep) is { } fa && DaySec(first.Dep) is { } fdp && fdp < fa)
                        errors.Add($"{n}: 所定の {t.From} が{fname}に着くより前に出ることになっています");
                    if (trains.Any(x => x != t && x.From == t.From)) errors.Add($"{n}: 所定の {t.From} は、ほかの列車の前運用にもなっています");
                    if (trains.Any(x => x != t && x.TurnHow is "発" or "入換" or "種別変更" && x.TurnNext == n))
                        errors.Add($"{n}: このグループの列車の後運用になっている列車には、前運用を決められません");
                }
            }

            // 増結車（連結）: 待つ駅（終点）に止まる後運用の列車に、あとから入ってきてもらって連結する（増結車が先に番線で待つ）
            if (t.TurnHow == "連結")
            {
                var end = t.Stops[^1];
                var endName = StationName(end.St);
                // 連結する相手は、このグループの列車か所定の列車
                var parent = FindTrain(t.TurnNext, trains);
                var ps = parent?.Stops.FirstOrDefault(s => s.St == end.St);
                if (t.TurnNext.Length == 0 || parent == null || parent == t) errors.Add($"{n}: 連結する列車{(t.TurnNext.Length > 0 ? $" {t.TurnNext}" : "")}が、このグループにも所定の時刻表にもありません");
                else if (ps == null || ps.Stop != "停") errors.Add($"{n}: 連結する列車 {parent.Number} は{endName}に停車しません");
                else
                {
                    if (n != "連" + parent.Number) errors.Add($"{n}: 増結車の列番は「連{parent.Number}」にしてください（連結する列車の列番に「連」を付ける）");
                    if (end.Track != null && ps.Track != null && end.Track != ps.Track) errors.Add($"{n}: {parent.Number} は{endName} {ps.Track}番線に着きます。増結車も {ps.Track}番線で待ってください");
                    // 連結できる番線か（場内が2段あり、番線の場内と同じ所へ入る入換信号機がある番線だけ。止まってから入換信号機で入る）
                    if (ps.Track is { } ptk && RouteEngine.TrainInfo(parent.Number) is { } pinfo && !RouteEngine.CanCoupleAt(_rail, end.St, pinfo.Dir, ptk))
                    {
                        var okTracks = _rail.Stations.TryGetValue(end.St, out var csd) ? csd.Routes[pinfo.Dir].Arrive.Keys.Where(k => RouteEngine.CanCoupleAt(_rail, end.St, pinfo.Dir, k)).Order().ToList() : new List<int>();
                        errors.Add($"{n}: {endName} {ptk}番線は、{(pinfo.Dir == Dir.Down ? "下り" : "上り")}の列車が番線へ入る入換信号機がないので、連結できません"
                            + (okTracks.Count > 0 ? $"（{endName}で連結できる番線: {string.Join("・", okTracks)}番線）" : $"（{endName}では{(pinfo.Dir == Dir.Down ? "下り" : "上り")}の列車に連結できません）"));
                    }
                    double? ready = DaySec(end.Arr ?? end.Dep), parr = DaySec(ps.Arr ?? ps.Dep);
                    if (ready != null && parr != null && parr < ready) errors.Add($"{n}: {parent.Number} が{endName}に着くのは、増結車が番線に入るより前です（増結車が先に入って待ちます）");
                    if (trains.Count(x => x != t && x.TurnHow == "連結" && x.TurnNext == parent.Number) > 0) errors.Add($"{n}: {parent.Number} には、ほかの増結車も連結します");
                    if (t.Cars > 0 && parent.Cars > 0 && t.Cars + parent.Cars > 10) errors.Add($"{n}: 連結すると {t.Cars + parent.Cars}両になります（10両まで）");
                }
            }

            // 終点のあと（そのまま折返・入換して折返）
            if (t.TurnHow is "発" or "入換" or "種別変更")
            {
                var end = t.Stops[^1];
                var endName = StationName(end.St);
                // 終夜運転なら、翌朝の所定の列車（前の列車がない始発）も後運用にできる（そのまま折返・入換だけ）
                var next = trains.FirstOrDefault(x => x.Number == t.TurnNext)
                    ?? (overnight && t.TurnHow is "発" or "入換" && !trains.Any(x => x.Number == t.TurnNext) ? FindTrain(t.TurnNext, trains) : null);
                if (next != null && !trains.Contains(next) && BasePred(next.Number) is { } bp)
                    errors.Add($"{n}: 所定の {next.Number} は、所定の {bp} から続けて走る列車です（後運用にできるのは、前の列車がない所定の列車だけです）");
                if (t.TurnNext.Length == 0 || next == null) errors.Add($"{n}: 後運用の列番{(t.TurnNext.Length > 0 ? $" {t.TurnNext}" : "")}がこのグループに{(overnight ? "も所定の時刻表にも" : "")}ありません");
                else if (next == t) errors.Add($"{n}: 後運用に自分は選べません");
                else if (next.Stops.Count == 0 || next.Stops[0].St != end.St) errors.Add($"{n}: 後運用 {next.Number} は{endName}から出る列車ではありません");
                else
                {
                    if (trains.Count(x => x != t && x.TurnHow is "発" or "入換" or "種別変更" && x.TurnNext == next.Number) > 0)
                        errors.Add($"{n}: 後運用 {next.Number} は、ほかの列車の後運用にもなっています");
                    // 構内入換（1駅だけの列車）は、着の時刻（入換で番線に入る時刻）を発車の代わりにする
                    double? arr = DaySec(end.Arr ?? end.Dep), dep = DaySec(next.Stops[0].Dep ?? (next.Stops.Count == 1 ? next.Stops[0].Arr : null));
                    if (arr != null && dep != null && dep < arr) errors.Add($"{n}: 後運用 {next.Number} の発車が、{endName}に着くより前です");
                    if (t.TurnHow == "種別変更")
                    {
                        // 種別変更: 同じ向きに、着いた番線からそのまま続けて走る（折り返さない）
                        var nz = next.Stops.Select(s => ZoneOf(s.St)).Where(z => z != null).Select(z => z!.Value).ToList();
                        if (nz.Count >= 2 && (nz[^1] > nz[0]) != inc) errors.Add($"{n}: 種別変更の後運用 {next.Number} は、同じ向きに走る列車にしてください（向きが変わるなら折返）");
                        if (end.Track != null && next.Stops[0].Track != null && end.Track != next.Stops[0].Track)
                            errors.Add($"{n}: 種別変更は着いた番線（{end.Track}番線）のまま出ます。{next.Number} の発車の番線を {end.Track}番線にしてください");
                    }
                    if (t.TurnHow == "入換")
                    {
                        var tt = DaySec(t.TurnTime);
                        if (t.TurnTime != null && tt == null) errors.Add($"{n}: 入換の時刻の形が違います（例 10:05）");
                        else if (tt != null && arr != null && dep != null && (tt < arr || tt > dep)) errors.Add($"{n}: 入換の時刻は、着いてから後運用の発車までの間にしてください");
                        if (trainDir is { } td2 && _rail.Stations.TryGetValue(end.St, out var es) && es.RouteControl)
                        {
                            var outs = es.Routes[td2].ShuntOuts.Where(o => o.From == end.Track).Select(o => o.Label).Distinct().ToList();
                            // 引上線がなければ、着いた番線から後運用の発車の番線へ、そのまま入換で移る（駒野 1番線 → 5番線など。後運用の始発の入換）
                            // 後運用の向き（入換は折り返しに限らない。駒野の下りで1番線に着き、5番線へ移って下りで出ることもある）
                            var nzs = next.Stops.Select(s => ZoneOf(s.St)).Where(z => z != null).Select(z => z!.Value).ToList();
                            // 後運用が1駅だけ（構内入換）なら、後運用の列番の向き
                            var nd = nzs.Count >= 2 && downUp is { } du3 ? ((nzs[^1] > nzs[0]) == du3 ? Dir.Down : Dir.Up)
                                : nzs.Count < 2 && RouteEngine.TrainInfo(next.Number) is { } ninfo ? ninfo.Dir
                                : td2 == Dir.Down ? Dir.Up : Dir.Down;
                            var moves = es.Routes[nd].Shunts.Where(s => s.From == end.Track).Select(s => s.To).Distinct().Order().ToList();
                            var odd = nd == Dir.Down ? Dir.Up : Dir.Down;
                            if (outs.Count == 0)
                            {
                                if (next.Stops.Count == 1 && next.Stops[0].Track is { } yt && !moves.Contains(yt)
                                    && es.Routes[odd].Shunts.Any(s => s.From == end.Track && s.To == yt))
                                    errors.Add($"{next.Number}: {endName} {end.Track}番線から{yt}番線への入換は{(odd == Dir.Down ? "下り" : "上り")}の入換です。列番の数字の最後を{(odd == Dir.Down ? "奇数" : "偶数")}にしてください");
                                else if (moves.Count == 0 && !es.Routes[odd].Shunts.Any(s => s.From == end.Track)) errors.Add($"{n}: {endName} {end.Track}番線から入換できる線・番線がありません");
                                else if (t.TurnVia.Length > 0) errors.Add($"{n}: {endName} {end.Track}番線からは「{t.TurnVia}」へ入換できません");
                                else if (next.Stops[0].Track is not { } mt || !moves.Contains(mt))
                                {
                                    var all = es.Routes[Dir.Down].Shunts.Concat(es.Routes[Dir.Up].Shunts).Where(s => s.From == end.Track).Select(s => s.To).Distinct().Order().ToList();
                                    errors.Add(moves.Count > 0
                                        ? $"{n}: {endName} {end.Track}番線からは {string.Join("・", moves.Select(x => x + "番線"))}へ入換できます。{next.Number} の発車の番線をそのどれかにしてください"
                                        : $"{n}: {endName} {end.Track}番線から{(nd == Dir.Down ? "下り" : "上り")}で入換できる番線がありません（{string.Join("・", all.Select(x => x + "番線"))}へは構内入換で移れます）");
                                }
                            }
                            else if (t.TurnVia.Length > 0 && !outs.Contains(t.TurnVia)) errors.Add($"{n}: {endName} {end.Track}番線からは「{t.TurnVia}」へ入換できません");
                            // 入換した線から、後運用の発車の番線へ入れるか（知らせるだけ）
                            var via = t.TurnVia.Length > 0 ? t.TurnVia : outs.FirstOrDefault();
                            var nextDir = nd;
                            var tos = es.Routes[nextDir].Shunts.Where(s => s.FromLabel == via).Select(s => s.To).Distinct().ToList();
                            if (via != null && tos.Count > 0 && next.Stops[0].Track is { } nt && !tos.Contains(nt))
                                warnings.Add($"{n}: 「{via}」から {next.Number} の発車の {nt}番線へは入換の進路がありません（{string.Join("・", tos.Select(x => x + "番線"))}へは入れます）");
                        }
                    }
                }
            }
        }

        // ほかのグループの列番とかぶっていないか（知らせるだけ。適用するときに、適用中のものとかぶれば断る）
        lock (_lock)
            foreach (var g in _groups.Where(g => g.Id != groupId))
            foreach (var n in g.Trains.Select(x => x.Number).Where(seen.Contains))
                warnings.Add($"{n}: グループ「{g.Name}」にも同じ列番があります（同時には適用できません）");

        // 所定の運行番号とかぶっていないか（知らせるだけ）
        var baseUnyo = _rail.BaseDia.Values.Select(d => d.Unyo).Where(u => u != null).ToHashSet();
        foreach (var u in trains.Select(t => t.Unyo).Distinct().Where(u => baseUnyo.Contains(u)))
            warnings.Add($"運行番号 {u} は所定の時刻表でも使っています（乗務員の行路と重なるおそれがあります）");
        return (errors, warnings);
    }

    /// <summary>
    /// その番線に入れるか（RouteEngine の番線の選び方と同じ）: 場内で入れる（発駅は不要）、かつ出発できる（終点は不要。行き止まりは打ち切りとして可）。
    /// </summary>
    public static bool TrackAllowed(StationDirRoutes r, int track, bool first, bool last) =>
        (first || r.Arrive.ContainsKey(track)) && (last || r.Depart.ContainsKey(track) || (!first && r.Turnback.Contains(track)));

    /// <summary>下り（列番が奇数）は、路線の駅の並び（ゾーン）が増える向きか。所定のダイヤから読む。</summary>
    public bool? DownIncreasing()
    {
        int votes = 0;
        foreach (var d in _rail.BaseDia.Values.Take(400))
        {
            var zs = d.Stops.Select(s => s.StationId is { } id ? _rail.Line.ZoneOfStation(id) : -1).Where(z => z >= 0).ToList();
            if (zs.Count < 2 || zs[0] == zs[^1] || RouteEngine.TrainInfo(d.Number) is not { } info) continue;
            votes += (zs[^1] > zs[0]) == (info.Dir == Dir.Down) ? 1 : -1;
        }
        return votes == 0 ? null : votes > 0;
    }

    /// <summary>入力した列車を、ダイヤの列車に直す。同じ運行番号で、終点から次の列車が出るなら折返でつなぐ。</summary>
    /// <summary>手で決めた終点のあと（発・入換）。自動・なし・後運用がないときは null。</summary>
    private DiaTurn? TurnOf(TrainSpec t, List<TrainSpec> trains, bool overnight)
    {
        double? DaySec(string? s) => DaySecAt(s, overnight);
        // 終夜運転なら、後運用は翌朝の所定の列車でもよい
        if (t.TurnHow is not ("発" or "入換") || (trains.FirstOrDefault(x => x.Number == t.TurnNext) ?? (overnight ? FindTrain(t.TurnNext, trains) : null)) is not { } next
            || next.Stops.Count == 0) return null;
        var dep = NormTime(next.Stops[0].Dep);
        if (t.TurnHow == "発") return new DiaTurn("折返", next.Number, dep, "発");
        // 構内入換（後運用が1駅だけで発の時刻がない）: 入換の時刻が空なら、後運用の着の時刻（入換で番線に入る時刻）
        var yardArr = dep == null && next.Stops.Count == 1 ? NormTime(next.Stops[0].Arr) : null;
        var time = NormTime(t.TurnTime) ?? yardArr ?? (DaySec(dep) is { } ds ? NormTime(TimeOfDay(ds - 120)) : null);
        return new DiaTurn("折返", next.Number, time, "入換", t.TurnVia.Length > 0 ? t.TurnVia : null);
    }

    private static string TimeOfDay(double sec)
    {
        var v = ((int)Math.Round(sec) % 86400 + 86400) % 86400;
        return $"{v / 3600:00}:{v / 60 % 60:00}:{v % 60:00}";
    }

    /// <summary>
    /// 入力した列車を、ダイヤの列車に直す。Replace は差し替える所定の列車（前運用にした、留置になる所定の列車の「終点のあと」を、その臨時列車への折返にしたもの）。
    /// </summary>
    private (List<DiaTrain> Extra, List<DiaTrain> Replace) Build(List<TrainSpec> trains, bool overnight)
    {
        double? DaySec(string? s) => DaySecAt(s, overnight);
        var list = new List<DiaTrain>();
        var replace = new List<DiaTrain>();
        var byUnyo = trains.Where(t => t.Unyo.Length > 0).GroupBy(t => t.Unyo).ToList();
        foreach (var g in byUnyo)
        {
            var ordered = g.OrderBy(t => DaySec(t.Stops.FirstOrDefault()?.Dep ?? t.Stops.FirstOrDefault()?.Arr) ?? 0).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var t = ordered[i];
                var d = new DiaTrain
                {
                    Number = t.Number, Type = t.Type, TypeLabel = t.Label.Length > 0 ? t.Label : t.Type,
                    Dest = t.Dest.Length > 0 ? t.Dest : StationName(t.Stops[^1].St), Unyo = t.Unyo, Cars = t.Cars > 0 ? t.Cars : null
                };
                foreach (var s in t.Stops)
                {
                    // 注記（Note）は付けない: スタフの駅の欄に「併B」などの停止位置目標が出ないように（新野崎には併A・併B の停止位置目標がない）
                    d.Stops.Add(new DiaStop(StationName(s.St), s.St, s.Stop, NormTime(s.Arr), NormTime(s.Dep), s.Track));
                    d.Stations[s.St] = (s.Stop == "停" ? "停車" : "通過", s.Track);
                }
                var last = t.Stops[^1].St;
                if (_rail.Stations.TryGetValue(last, out var ts) && ts.RouteControl) d.TerminalId = last;
                // 種別変更: 時刻表の「途中駅で列番が変わる列車」と同じく、次の列番と変わる駅（Next）にする
                if (t.TurnHow == "種別変更" && trains.FirstOrDefault(x => x.Number == t.TurnNext) is { } chg && chg != t)
                    d.Next = (chg.Number, last);
                // 増結車: 時刻表の 連777 と同じく、終点（待つ駅）で後運用の列車に「併結」する
                else if (t.TurnHow == "連結" && t.TurnNext.Length > 0)
                    d.Turn = new DiaTurn("併結", t.TurnNext, NormTime(FindTrain(t.TurnNext, trains)?.Stops.FirstOrDefault(s => s.St == last)?.Dep), null);
                else d.Turn = TurnOf(t, trains, overnight) ?? (t.TurnHow.Length == 0 && i + 1 < ordered.Count && ordered[i + 1].Stops.FirstOrDefault() is { } nf && nf.St == last
                    ? new DiaTurn("折返", ordered[i + 1].Number, NormTime(nf.Dep), "発") : null);
                list.Add(d);
                if (t.From.Length > 0 && t.Stops.Count > 0 && _rail.BaseDia.TryGetValue(t.From, out var fd))
                    replace.Add(WithTurn(fd, new DiaTurn("折返", t.Number, NormTime(t.Stops[0].Dep), "発")));
            }
        }
        return (list, replace);
    }
}
