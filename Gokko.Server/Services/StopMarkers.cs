using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Gokko.Server.Services;

/// <summary>
/// 停止位置目標（何両停目がどこにあるか）の表。将来の車掌単独乗務（止まる停目の案内・ずれの判定）と、
/// 自動運転列車を両数に合った停目に止めるのに使う（使う側はまだ。いまは読み込みと確かめる API だけ）。
///
/// ・Data/StopMarkers.up.csv（上り）・Data/StopMarkers.down.csv（下り）。ユーザーが作った CSV をそのまま置く
/// ・1行目: 1列目は見出しだけ（「6両位置から見て」）。2列目からは「0m」「20m(1両分)」… のように、先頭の数字がその列の距離（m）
/// ・2行目から: 1列目が「駅名＋番線」（駒野1・江原検11・大道寺X・浜園引上。番線のない駅は駅名だけ）、
///   2列目からは、その距離にある停目の両数（6・5・4…）。空はその位置に停目がない
/// ・距離は、0m の列の停目（ほとんどは6両停目。大道寺E・赤山町2 のように短い停目しかない番線もある）から手前（列車が先に通る側）へ測る。
///   20m ずつ 5・4・3・2 と並ぶ番線も、20m の位置に 4両停目がある番線もあるので、列の距離をそのまま使い、両数から位置を計算しない
/// ・止まる停目: その両数以上でいちばん小さい停目（4両で 4両停目がなければ 5→6 の順）。どれもなければ、いちばん先（0m に近い）の停目
/// ・番線のない行（館浜の上り・日野森の下りなど）は、その駅のどの番線にも使う。番線の行があればそちらを先に使う
/// </summary>
public sealed class StopMarkers
{
    public const string UpFileName = "StopMarkers.up.csv";
    public const string DownFileName = "StopMarkers.down.csv";

    /// <summary>停目1つ。Offset は 0m の列の停目から手前への距離（m）。</summary>
    public sealed record Marker(int Cars, double Offset);

    /// <summary>
    /// 表の1行（向き・駅・番線ごと）。Track は番線（"1"・"11"・"X"・"引上"。番線のない駅は ""）。Label は CSV の1列目のまま。
    /// Markers は手前へ遠くなる順。
    /// </summary>
    public sealed record Entry(Dir Dir, string Station, string Track, string Label, int Row, IReadOnlyList<Marker> Markers)
    {
        /// <summary>その両数の列車が止まる停目。</summary>
        public Marker Choose(int cars) =>
            Markers.Where(m => m.Cars >= cars).MinBy(m => (m.Cars, m.Offset)) ?? Markers.MinBy(m => m.Offset)!;
    }

    private readonly Dictionary<(Dir Dir, string Station, string Track), Entry> _entries;

    public IReadOnlyList<Entry> Entries { get; }

    /// <summary>読んだときに見つかったおかしな所（起動時の記録と確かめる API に出す）。</summary>
    public IReadOnlyList<string> Problems { get; }

    private StopMarkers(List<Entry> entries, List<string> problems)
    {
        Entries = entries;
        Problems = problems;
        _entries = new();
        foreach (var e in entries) _entries[(e.Dir, e.Station, e.Track)] = e;
    }

    /// <summary>その駅・番線の行。番線の行がなければ、番線のない行。どちらもなければ null。</summary>
    public Entry? Find(Dir dir, string station, string? track)
    {
        var t = NormalizeTrack(track);
        if (t.Length > 0 && _entries.TryGetValue((dir, station, t), out var e)) return e;
        return _entries.TryGetValue((dir, station, ""), out var any) ? any : null;
    }

    /// <summary>その駅・番線に、その両数の列車が止まる停目。表になければ null。</summary>
    public Marker? Choose(Dir dir, string station, string? track, int cars) => Find(dir, station, track)?.Choose(cars);

    /// <param name="stationNames">駅名（時刻表の駅名）。1列目の「駅名＋番線」を分けるのに使う（いちばん長く合う駅名を取る）。</param>
    public static StopMarkers Load(string dataDir, IEnumerable<string> stationNames)
    {
        var names = stationNames.Where(n => n.Length > 0).Distinct().OrderByDescending(n => n.Length).ToList();
        var problems = new List<string>();
        var entries = new List<Entry>();
        foreach (var (dir, file) in new[] { (Dir.Up, UpFileName), (Dir.Down, DownFileName) })
            ReadFile(Path.Combine(dataDir, file), file, dir, names, entries, problems);
        return new StopMarkers(entries, problems);
    }

    private static readonly Regex DistanceHead = new(@"^\s*(\d+(?:\.\d+)?)\s*m", RegexOptions.IgnoreCase);
    private static readonly Regex TrackForm = new(@"^(\d+|[A-Z]|引上)$");

    private static void ReadFile(string path, string name, Dir dir, List<string> names, List<Entry> entries, List<string> problems)
    {
        if (!File.Exists(path)) { problems.Add($"{name} がありません"); return; }

        List<List<string>> rows;
        try { rows = UnyoCars.ParseCsv(File.ReadAllText(path, Encoding.UTF8)); }
        catch (Exception ex) { problems.Add($"{name} を読めませんでした（{ex.Message}）"); return; }
        if (rows.Count == 0) { problems.Add($"{name} が空です"); return; }

        // 見出し: 2列目から距離
        var head = rows[0].Select(h => h.Trim().TrimStart('﻿').Normalize(NormalizationForm.FormKC)).ToList();
        var distances = new List<double?>();
        for (var c = 1; c < head.Count; c++)
        {
            var m = DistanceHead.Match(head[c]);
            if (m.Success) distances.Add(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            else
            {
                distances.Add(null);
                if (head[c].Length > 0) problems.Add($"{name} 1行目 {c + 1}列目: 見出し「{head[c]}」から距離（m）を読めません（この列は読みません）");
            }
        }
        var known = distances.OfType<double>().ToList();
        for (var i = 1; i < known.Count; i++)
            if (known[i] <= known[i - 1]) problems.Add($"{name} 1行目: 距離が手前へ遠くなる順に並んでいません（{known[i - 1]}m のあとに {known[i]}m）");

        var seen = new HashSet<(string, string)>();
        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var rowNo = r + 1;
            var label = row.Count > 0 ? row[0].Trim() : "";
            if (label.Length == 0)
            {
                if (row.Any(x => x.Trim().Length > 0)) problems.Add($"{name} {rowNo}行目: 1列目（駅名）が空です（読みません）");
                continue;
            }

            var norm = label.Normalize(NormalizationForm.FormKC);
            var station = names.FirstOrDefault(n => norm.StartsWith(n, StringComparison.Ordinal));
            if (station == null) { problems.Add($"{name} {rowNo}行目: 「{label}」の駅名が時刻表にありません（読みません）"); continue; }
            var track = NormalizeTrack(norm[station.Length..]);
            if (track.Length > 0 && !TrackForm.IsMatch(track))
                problems.Add($"{name} {rowNo}行目: 「{label}」の番線「{track}」が番線の形（1・11・X・引上）ではありません（そのまま番線「{track}」として読みます）");

            var markers = new List<Marker>();
            for (var c = 1; c < row.Count; c++)
            {
                var cell = row[c].Trim().Normalize(NormalizationForm.FormKC);
                if (cell.Length == 0) continue;
                var d = c - 1 < distances.Count ? distances[c - 1] : null;
                if (d == null) { problems.Add($"{name} {rowNo}行目 {c + 1}列目: 見出しの距離がない列に「{cell}」があります（読みません）"); continue; }
                if (!int.TryParse(cell.TrimEnd('両'), out var cars) || cars < 1 || cars > 10)
                {
                    problems.Add($"{name} {rowNo}行目（{label}）{d}m: 「{cell}」が両数（1〜10）ではありません（読みません）");
                    continue;
                }
                if (markers.Any(x => x.Cars == cars))
                {
                    problems.Add($"{name} {rowNo}行目（{label}）: {cars}両停目が2つあります（手前の {d}m は読みません）");
                    continue;
                }
                markers.Add(new Marker(cars, d.Value));
            }
            if (markers.Count == 0) { problems.Add($"{name} {rowNo}行目（{label}）: 停目が1つもありません（読みません）"); continue; }
            markers.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            // 手前ほど短い停目のはず。長い停目が短い停目より手前にあれば、書き間違いかもしれないので知らせる（読みはする）
            for (var i = 1; i < markers.Count; i++)
                if (markers[i].Cars > markers[i - 1].Cars)
                    problems.Add($"{name} {rowNo}行目（{label}）: {markers[i].Cars}両停目（{markers[i].Offset}m）が {markers[i - 1].Cars}両停目（{markers[i - 1].Offset}m）より手前にあります（そのまま読みます）");

            if (!seen.Add((station, track)))
                problems.Add($"{name} {rowNo}行目: 「{label}」が2回書いてあります（あとの行を使います）");
            entries.RemoveAll(e => e.Dir == dir && e.Station == station && e.Track == track);
            entries.Add(new Entry(dir, station, track, label, rowNo, markers));
        }
    }

    /// <summary>番線を表の形にする（全角→半角、英字は大文字。「1番線」「1番」も 1）。null は ""。</summary>
    public static string NormalizeTrack(string? track)
    {
        if (string.IsNullOrWhiteSpace(track)) return "";
        var t = track.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        if (t.EndsWith("番線")) t = t[..^2];
        else if (t.EndsWith("番")) t = t[..^1];
        if (t.EndsWith("線") && (t == "引上線" || (t.Length == 2 && char.IsAsciiLetter(t[0])))) t = t[..^1]; // X線 → X、引上線 → 引上
        return t;
    }
}
