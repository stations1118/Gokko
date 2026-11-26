using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Gokko.Common;

namespace Gokko.Server.Services;

/// <summary>
/// ランカーブ（1駅間の走り方の記録）。クライアントが駅に止まったときに1回だけ送ってくるものを、
/// 社員番号と受け取った時刻を付けて logs/runcurve-yyyyMMdd.log（1駅間1行の JSON）に足すだけ。計算はしない。
/// ・1回の大きさ・1人あたりの間隔と1日の回数・1日のファイルの大きさに上限をつける（超えたら捨てる）
/// ・ふるい分け（形式・駅・利用者・自動/手動）は管理用の API（<see cref="Find"/>）で、その日のファイルを読んで行う
/// </summary>
public sealed class RunCurves(ILogger<RunCurves>? logger = null)
{
    private const long MaxBytesPerDay = 100L * 1024 * 1024;
    private const int MaxCompressed = 60 * 1024;     // 送られてくる gzip の上限（SignalR の受け取りの上限 64KB より小さく）
    private const int MaxJson = 2 * 1024 * 1024;     // ほどいた JSON の上限
    private const int MaxPoints = 5000;
    private const int MinIntervalMs = 3000;          // 1人あたり、これより短い間隔では受け取らない
    private const int MaxPerDay = 2000;              // 1人あたりの1日の回数

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly DailyLog _file = new("runcurve", MaxBytesPerDay);
    private readonly ConcurrentDictionary<string, (long LastMs, string Day, int Count)> _rate = new();

    /// <summary>1つ受け取る（gzip した RunCurve の JSON）。足したら true。</summary>
    public bool Add(string crewId, byte[] data)
    {
        if (string.IsNullOrEmpty(crewId) || data is not { Length: > 0 and <= MaxCompressed }) return false;
        var now = Environment.TickCount64;
        var day = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var ok = true;
        _rate.AddOrUpdate(crewId, _ => (now, day, 1), (_, r) =>
        {
            var count = r.Day == day ? r.Count : 0;
            if (now - r.LastMs < MinIntervalMs || count >= MaxPerDay)
            {
                ok = false;
                return r;
            }
            return (now, day, count + 1);
        });
        if (!ok) return false;

        RunCurve? curve;
        try
        {
            using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            using var json = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int n;
            while ((n = gz.Read(buffer, 0, buffer.Length)) > 0)
            {
                json.Write(buffer, 0, n);
                if (json.Length > MaxJson) return false;
            }
            curve = JsonSerializer.Deserialize<RunCurve>(json.ToArray(), Json);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "ランカーブを読めませんでした ID:{CrewId}", crewId);
            return false;
        }

        if (curve == null || curve.Points.Count > MaxPoints || curve.Mode is not ("auto" or "manual" or "mixed")) return false;
        var at = DateTime.UtcNow;
        var line = JsonSerializer.Serialize(new RunCurveRecord { Crew = crewId, Received = at, Curve = curve }, Json);
        _file.Append(at, line + "\n", () => $"{{\"full\":\"1日の上限（{_file.MaxMb}MB）を超えたので、この日はもう書きません\"}}\n");
        return true;
    }

    /// <summary>
    /// その日（yyyyMMdd。なければ今日）のランカーブを、条件で選んで返す。station は発駅・着駅のどちらか、model は先頭の形式か全車のどれか。
    /// points が false なら、行（Points）は除いて行数だけ（一覧を軽くする）。
    /// </summary>
    public List<object> Find(string? date, string? crew, string? model, string? station, string? mode, bool points, int limit)
    {
        var day = string.IsNullOrEmpty(date) ? DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : date;
        var result = new List<object>();
        if (day.Length != 8 || !day.All(char.IsAsciiDigit)) return result;
        var path = Path.Combine(AppContext.BaseDirectory, "logs", $"runcurve-{day}.log");
        if (!File.Exists(path)) return result;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line && result.Count < limit)
        {
            RunCurveRecord? r;
            try { r = JsonSerializer.Deserialize<RunCurveRecord>(line, Json); }
            catch (JsonException) { continue; }
            if (r?.Curve is not { } c) continue;
            if (!string.IsNullOrEmpty(crew) && r.Crew != crew) continue;
            if (!string.IsNullOrEmpty(mode) && c.Mode != mode) continue;
            if (!string.IsNullOrEmpty(model) && c.Model != model && !c.Models.Contains(model)) continue;
            if (!string.IsNullOrEmpty(station) && c.From != station && c.To != station) continue;
            if (points) result.Add(r);
            else
                result.Add(new
                {
                    r.Crew, r.Received, c.Mode, c.Train, c.Dia, c.From, c.To, c.StopPosName, c.Track, c.ScenarioTrack, c.Cars, c.Model,
                    c.Start, c.End, c.StopRemaining, c.StopKind, c.StopRough, c.Gain, PointCount = c.Points.Count
                });
        }
        return result;
    }

    /// <summary>この何日かのランカーブを、古い日から順に読む（集計用。管理用の API から呼ばれたときだけ）。</summary>
    public IEnumerable<(string Crew, RunCurve Curve)> ReadDays(int days)
    {
        var today = DateTime.Now.Date;
        for (var d = Math.Clamp(days, 1, DailyLog.KeepDays) - 1; d >= 0; d--)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "logs", $"runcurve-{today.AddDays(-d):yyyyMMdd}.log");
            if (!File.Exists(path)) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                RunCurveRecord? r;
                try { r = JsonSerializer.Deserialize<RunCurveRecord>(line, Json); }
                catch (JsonException) { continue; }
                if (r?.Curve is { } c) yield return (r.Crew, c);
            }
        }
    }

    private sealed class RunCurveRecord
    {
        public string Crew { get; set; } = "";
        public DateTime Received { get; set; }
        public RunCurve? Curve { get; set; }
    }
}
