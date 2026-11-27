using System.Text;
using System.Text.Json;

namespace Gokko.Server.Services;

/// <summary>
/// 内線番号の帳面（Data/Extensions.json）。司令と、盤のある駅ごとの番号を、管理画面で決める。
/// 運転士・車掌は社員番号（8けた）がそのまま番号になる（帳面には入れない）。司令・駅の番号は数字だけ（1〜7けた。8けたは社員番号と重なるので使わない）。
/// 初めは 司令 = 100、駅 = 駅ID の数字（TH65 → 65）。
/// </summary>
public sealed class ExtensionBook
{
    public const string FileName = "Extensions.json";
    public const string DefaultDispatcher = "100";

    private sealed class Saved
    {
        public string Dispatcher { get; set; } = DefaultDispatcher;
        public Dictionary<string, string> Stations { get; set; } = new();
    }

    private readonly object _lock = new();
    private readonly string _path;
    private readonly ILogger<ExtensionBook>? _logger;
    private Saved _saved = new();

    public ExtensionBook(string dataDir, ILogger<ExtensionBook>? logger = null)
    {
        _path = Path.Combine(dataDir, FileName);
        _logger = logger;
        try
        {
            if (File.Exists(_path)) _saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path), Json) ?? new Saved();
        }
        catch (Exception e)
        {
            _logger?.LogWarning("内線番号の帳面を読めません（初めの番号を使います）: {Message}", e.Message);
        }
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public string Dispatcher
    {
        get { lock (_lock) return _saved.Dispatcher; }
    }

    /// <summary>駅の番号（帳面になければ駅ID の数字）。</summary>
    public string Station(string stationId)
    {
        lock (_lock)
            return _saved.Stations.TryGetValue(stationId, out var n) && n.Length > 0 ? n : DefaultStation(stationId);
    }

    public static string DefaultStation(string stationId)
    {
        var digits = new string(stationId.Where(char.IsAsciiDigit).ToArray());
        return digits.Length > 0 ? digits : stationId;
    }

    public static bool IsValidNumber(string? n) => n is { Length: >= 1 and <= 7 } && n.All(char.IsAsciiDigit);

    /// <summary>番号を決め直す。誤りがあればそのわけを返し、何も変えない。</summary>
    public string? Set(string? dispatcher, Dictionary<string, string>? stations, IReadOnlyCollection<string> stationIds)
    {
        dispatcher = (dispatcher ?? "").Trim();
        var map = new Dictionary<string, string>();
        foreach (var id in stationIds)
            map[id] = (stations != null && stations.TryGetValue(id, out var n) ? n : DefaultStation(id))?.Trim() ?? "";

        if (!IsValidNumber(dispatcher)) return "司令の番号は、数字 1〜7けたで決めてください";
        foreach (var (id, n) in map)
            if (!IsValidNumber(n)) return $"{id} の番号は、数字 1〜7けたで決めてください";
        var all = map.Values.Append(dispatcher).ToList();
        var dup = all.GroupBy(x => x).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) return $"番号 {dup.Key} が重なっています";

        lock (_lock)
        {
            _saved = new Saved { Dispatcher = dispatcher, Stations = map };
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(_saved, Json), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                _logger?.LogWarning("内線番号の帳面を保存できません: {Message}", e.Message);
                return "保存できませんでした（この起動のあいだは新しい番号を使います）: " + e.Message;
            }
        }

        return null;
    }
}
