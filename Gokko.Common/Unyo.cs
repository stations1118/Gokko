using System.Text.RegularExpressions;

namespace Gokko.Common;

/// <summary>
/// 運行番号（列車番号の付け方から逆算する）。
/// 列番 = [回・臨・試] ＋ 始発駅の時刻（時） ＋ 下2桁 ＋ [種別の記号 A・K・B・C など]。
/// 上り（赤山町方面）は運行番号の下2桁そのまま、下り（館浜方面）は下2桁＋1。
/// 運行番号が100番台なら数字に＋3000、200番台なら＋6000、300番台なら＋9000する。
/// 300番台は時の桁が1つしか入らないので、時の1の位だけを書く（試9091 → 10時台・下2桁91 → 390 運行）。
/// 例: 3643K → 142 運行（3643−3000＝643 → 6時台・下2桁43 → 下りなので42 → 100番台）。折り返しの 3842B も 142 運行。
/// 1182C → 1283C → 1282C → 1383C は、どれも 82 運行。
/// 運行番号にはアルファベットが付くものがある（24S など）。数字が同じでも別の運行。
/// 列番の数字のすぐ後が S・T なら、その文字を付ける（725SD・924SC → 24S 運行、4717T → 116T 運行）。
/// 運行番号は文字列で持つ（"24"・"24S"）。並べる順は数字の順、同じ数字ならアルファベットなし→ありの順（<see cref="Comparer"/>）。
/// </summary>
public static partial class Unyo
{
    [GeneratedRegex(@"^(?:回|臨|試)?(\d+)([A-Z]*)")]
    private static partial Regex NumberPart();

    [GeneratedRegex(@"^(\d+)([A-Z]*)$")]
    private static partial Regex UnyoPattern();

    /// <summary>運行番号に付くアルファベット（列番の数字のすぐ後の文字がこれなら、運行番号に付ける）。</summary>
    private const string Letters = "ST";

    /// <summary>列番から運行番号を求める（"24"・"24S"）。数字が読めなければ null。</summary>
    public static string? FromTrain(string? train)
    {
        if (string.IsNullOrWhiteSpace(train)) return null;
        var m = NumberPart().Match(train.Normalize(System.Text.NormalizationForm.FormKC).Trim().ToUpperInvariant());
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var n)) return null;
        var offset = 0;
        if (n >= 9000) { offset = 300; n -= 9000; }
        else if (n >= 6000) { offset = 200; n -= 6000; }
        else if (n >= 3000) { offset = 100; n -= 3000; }
        var low = n % 100;
        var suffix = m.Groups[2].Value;
        var letter = suffix.Length > 0 && Letters.Contains(suffix[0]) ? suffix[..1] : "";
        return $"{offset + low - low % 2}{letter}";
    }

    /// <summary>
    /// 人が書いた運行番号をそろえる（全角→半角・小文字→大文字・前後の空白と「運行」を取る・頭の0を取る）。
    /// "24"・"２４ｓ"・"24S運行" → "24"・"24S"・"24S"。運行番号の形でなければ null。
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Normalize(System.Text.NormalizationForm.FormKC).Trim().ToUpperInvariant().Replace(" ", "");
        if (s.EndsWith("運行")) s = s[..^2];
        if (s.StartsWith("運行")) s = s[2..];
        var m = UnyoPattern().Match(s);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var n)) return null;
        return $"{n}{m.Groups[2].Value}";
    }

    /// <summary>運行番号の数字の部分（"24S" → 24）。読めなければ null。</summary>
    public static int? NumberOf(string? unyo) =>
        unyo != null && UnyoPattern().Match(unyo) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? n : null;

    /// <summary>運行番号の並べ方: 数字の順、同じ数字ならアルファベットなし→ありの順（24 → 24S → 26）。null は最後。</summary>
    public static IComparer<string?> Comparer { get; } = Comparer<string?>.Create(Compare);

    public static int Compare(string? a, string? b)
    {
        if (a == b) return 0;
        if (a == null) return 1;
        if (b == null) return -1;
        var na = NumberOf(a) ?? int.MaxValue;
        var nb = NumberOf(b) ?? int.MaxValue;
        if (na != nb) return na.CompareTo(nb);
        return string.CompareOrdinal(a, b); // 同じ数字なら短い方（アルファベットなし）が先
    }
}
