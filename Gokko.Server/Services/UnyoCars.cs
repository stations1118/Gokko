using System.Text;

namespace Gokko.Server.Services;

/// <summary>
/// 編成の両数の表。自動運転の列車を出すとき・連結・解放の両数に使う。
///
/// ・Data/UnyoCars.csv: 運行番号ごとの両数。1行目の見出しから「運行番号」「両数」の列を探す。表にない運行は6両。
///   運行番号は "24"・"24S" のように書く（24 と 24S は別の運行）。全角・小文字・「24S運行」も読む
/// ・Data/TrainCars.csv（なくてよい）: 列番ごとの両数。運行の両数より優先する。運転区間の外で連結・解放して
///   両数が変わる列車（東井で 2+2 の4両になる 840・841・940・1747 など）に使う。見出しは「列番」「両数」
/// どちらもスプレッドシートから書き出した CSV をそのまま置けるように、ほかの列は読まない。
/// 両数が空・0・11以上・数字でないときは、運行の表は6両、列番の表はその行を使わず運行の両数にして、Problems に残す。
/// </summary>
public sealed class UnyoCars
{
    public const string FileName = "UnyoCars.csv";
    public const string TrainFileName = "TrainCars.csv";
    public const int Default = 6;

    private readonly Dictionary<string, int> _cars;
    private readonly Dictionary<string, int> _byTrain;

    /// <summary>読んだときに見つかったおかしな所（管理画面と記録に出す）。</summary>
    public IReadOnlyList<string> Problems { get; }

    private UnyoCars(Dictionary<string, int> cars, Dictionary<string, int> byTrain, List<string> problems)
    {
        _cars = cars;
        _byTrain = byTrain;
        Problems = problems;
    }

    /// <summary>その列車の両数。列番の表にあればそれ、なければ運行の両数（表にない運行・運行番号がない列車は6両）。</summary>
    public int Of(string number, string? unyo) =>
        _byTrain.TryGetValue(number, out var t) ? t
        : unyo != null && _cars.TryGetValue(unyo, out var c) ? c : Default;

    /// <param name="known">時刻表にある運行番号（渡せば、時刻表にない運行番号の行を Problems に残す）。</param>
    public static UnyoCars Load(string dataDir, IReadOnlyCollection<string>? known = null)
    {
        var problems = new List<string>();
        var cars = new Dictionary<string, int>();
        var unyoRows = ReadTable(Path.Combine(dataDir, FileName), FileName, ["運行番号", "運行"], true, problems);
        if (unyoRows == null) problems[^1] += "（どの運行も6両で出します）";
        foreach (var (row, key, n, error) in unyoRows ?? [])
        {
            if (Gokko.Common.Unyo.Normalize(key) is not { } unyo) { problems.Add($"{FileName} {row}行目: 運行番号「{key}」が運行番号の形（24・24S など）ではありません（読みません）"); continue; }
            if (known != null && !known.Contains(unyo)) problems.Add($"運行{unyo}: 時刻表にない運行番号です（24 と 24S は別の運行です）");
            var v = n ?? Default;
            if (n == null) problems.Add($"運行{unyo}: {error}（6両にします）");
            if (cars.ContainsKey(unyo)) problems.Add($"運行{unyo}: 2回書いてあります（あとの行の {v}両を使います）");
            cars[unyo] = v;
        }

        var byTrain = new Dictionary<string, int>();
        foreach (var (_, key, n, error) in ReadTable(Path.Combine(dataDir, TrainFileName), TrainFileName, ["列番", "列車番号"], false, problems) ?? [])
        {
            if (n == null) { problems.Add($"{TrainFileName} {key}: {error}（運行の両数にします）"); continue; }
            if (byTrain.ContainsKey(key)) problems.Add($"{TrainFileName} {key}: 2回書いてあります（あとの行の {n}両を使います）");
            byTrain[key] = n.Value;
        }

        return new UnyoCars(cars, byTrain, problems);
    }

    /// <summary>
    /// 表を読む。見出しの名前で鍵の列と「両数」の列を探し、(行番号, 鍵, 両数, 両数がおかしい理由) を返す。両数がおかしい行は両数を null にする。
    /// 読めないときは null（ファイルがないのは required のときだけ知らせる）。
    /// </summary>
    private static List<(int Row, string Key, int? Cars, string? Error)>? ReadTable(string path, string name, string[] keyHeaders, bool required, List<string> problems)
    {
        if (!File.Exists(path))
        {
            if (required) problems.Add($"{name} がありません");
            return null;
        }

        List<List<string>> rows;
        try { rows = ParseCsv(File.ReadAllText(path, Encoding.UTF8)); }
        catch (Exception ex)
        {
            problems.Add($"{name} を読めませんでした（{ex.Message}）");
            return null;
        }

        var head = rows.Count == 0 ? [] : rows[0].Select(h => h.Trim().TrimStart('﻿')).ToList();
        var keyCol = head.FindIndex(keyHeaders.Contains);
        var carsCol = head.FindIndex(h => h == "両数");
        if (keyCol < 0 || carsCol < 0)
        {
            problems.Add($"{name} の1行目に「{keyHeaders[0]}」と「両数」の見出しがありません");
            return null;
        }

        var list = new List<(int, string, int?, string?)>();
        for (var i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            string Cell(int c) => c < r.Count ? r[c].Trim() : "";
            var key = Cell(keyCol);
            if (key.Length == 0)
            {
                if (r.Any(x => x.Trim().Length > 0)) problems.Add($"{name} {i + 1}行目: {keyHeaders[0]}が空です（読みません）");
                continue;
            }

            var carsText = Cell(carsCol);
            int? n = null;
            string? error = null;
            if (carsText.Length == 0) error = "両数が空です";
            else if (!int.TryParse(carsText, out var v)) error = $"両数「{carsText}」が数字ではありません";
            else if (v < 1 || v > 10) error = $"両数 {v} は1〜10両の外です";
            else n = v;
            list.Add((i + 1, key, n, error));
        }

        return list;
    }

    /// <summary>CSV を行と列に分ける（"" で囲んだ列の中のカンマ・改行・"" に対応）。</summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\r') { }
            else if (ch == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>(); }
            else cell.Append(ch);
        }

        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}
