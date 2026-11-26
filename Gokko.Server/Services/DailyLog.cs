using System.Globalization;
using System.Text;

namespace Gokko.Server.Services;

/// <summary>
/// 日ごとの記録ファイル（logs/{名前}-yyyyMMdd.log）へ書き足す。進路・在線・自動運転の状態・負荷の記録で同じことを別々に書いていたのをまとめたもの。
/// ・日付（サーバーの時刻）が変わったら、新しい日のファイルに書く。そのとき <see cref="KeepDays"/> 日より古い同じ名前のファイルを消す
/// ・1日のファイルが上限を超えたら、そのことを1行書いて、その日はもう書かない
/// ・書けなくても例外は出さない（記録が書けないことで、進路の判断や配信を止めない）
/// </summary>
public sealed class DailyLog(string name, long maxBytesPerDay, string? header = null)
{
    /// <summary>記録ファイルを残す日数。これより古いものは、日付が変わったときに消す。</summary>
    public const int KeepDays = 30;

    private readonly object _gate = new();
    private string _day = "";
    private string _path = "";
    private long _bytes;
    private bool _full;

    /// <summary>上限（MB）。上限を超えたときの1行に書く。</summary>
    public long MaxMb => maxBytesPerDay / 1024 / 1024;

    /// <summary>
    /// text を書き足す（行の終わりの改行まで入れて渡す）。at はその記録の時刻（日付をまたいだかを見る）。
    /// 上限を超えていたら、text の代わりに fullNotice() を1回だけ書く。
    /// </summary>
    public void Append(DateTime at, string text, Func<string> fullNotice)
    {
        lock (_gate)
        {
            try
            {
                var day = at.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (day != _day)
                {
                    var dir = Path.Combine(AppContext.BaseDirectory, "logs");
                    Directory.CreateDirectory(dir);
                    _path = Path.Combine(dir, $"{name}-{day}.log");
                    _day = day;
                    _bytes = File.Exists(_path) ? new FileInfo(_path).Length : 0;
                    _full = false;
                    DeleteOld(dir, at.ToLocalTime().Date);
                    if (_bytes == 0 && header != null) Write(header);
                }

                if (_full) return;
                if (_bytes > maxBytesPerDay)
                {
                    _full = true;
                    text = fullNotice();
                }

                Write(text);
            }
            catch
            {
                // 書けなくても、呼んだ側の処理は続ける
            }
        }
    }

    private void Write(string text)
    {
        File.AppendAllText(_path, text);
        _bytes += Encoding.UTF8.GetByteCount(text);
    }

    /// <summary>KeepDays 日より古い、同じ名前の記録ファイルを消す（消せなくても続ける）。</summary>
    private void DeleteOld(string dir, DateTime today)
    {
        try
        {
            var prefix = name + "-";
            foreach (var file in Directory.GetFiles(dir, $"{name}-????????.log"))
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                if (!stem.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (!DateTime.TryParseExact(stem[prefix.Length..], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
                if ((today - d).TotalDays > KeepDays) File.Delete(file);
            }
        }
        catch
        {
            // 消せなくても記録は続ける
        }
    }
}
