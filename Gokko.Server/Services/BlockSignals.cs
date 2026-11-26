using System.Globalization;
using System.Text;
using Gokko.Common;

namespace Gokko.Server.Services;

/// <summary>
/// 駅間の閉塞信号機の、停止の後始末と強制進行。
///
/// 停止の出し方: サーバーは毎周期（0.5秒）、ほかの列車の在線から「停止にする信号機」を全部求めて送る（SignalCalculator）。
/// クライアント（SignalApplier）は送ったものとの差分だけゲームへ送り、指示から消えた信号機に1回だけ None（ゲームの現示に戻す）を送る。
/// ところが、ゲームとつなぎ直したとき・シナリオを閉じたときは、送った記録を捨てるだけで None を送らない。
/// その瞬間に停止にしていた閉塞信号機は、ゲームの中で停止のまま残り、もうだれも None を送らないので赤に固定される。
///
/// 定期解除: SweepInterval（10秒）ごとに SweepWindow（1.5秒）のあいだ、停止を出していない閉塞信号機すべてに None を入れて送る。
/// 停止にする判断（在線・方向てこ・強制進行）を全部終えたあとの、同じ周期の結果から「停止にしていないもの」だけを選ぶので、
/// 停止を出す処理とぶつからない（列車が入れば、その周期から R になり、None は入らない）。
/// クライアントは配信を0.5秒ごとに読むので、1回分の配信を読み飛ばしても届くよう、窓を数周期ぶん開けておく。
///
/// 定期解除は、管理画面・司令用管理画面からサーバーを止めずに切れる（ゲームでおかしな動きが出たとき用。再起動すると入に戻る）。
///
/// 強制進行: 管理画面・司令用管理画面から閉塞信号機を選ぶと、サーバーからの停止（在線・方向てこ）を出さず、None を送り続ける。
/// ゲームが自分の列車・進路から出す停止までは上げられない（ゲームは「今より低い現示」しか受け付けない）。解除するまで続く。
/// </summary>
public sealed class BlockSignals(RailData rail, IncidentManager log)
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan SweepWindow = TimeSpan.FromSeconds(1.5);

    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _forced = new(StringComparer.Ordinal);
    private DateTime _cycleStartUtc = DateTime.MinValue;
    private bool _sweepEnabled = true;

    /// <summary>定期解除が入っているか。</summary>
    public bool SweepEnabled
    {
        get { lock (_lock) return _sweepEnabled; }
    }

    /// <summary>定期解除を入れる・切る。変わったら記録に残す。</summary>
    public void SetSweepEnabled(bool on)
    {
        bool changed;
        lock (_lock)
        {
            changed = _sweepEnabled != on;
            _sweepEnabled = on;
        }

        if (changed) log.Note(on ? "閉塞信号機の定期解除を入れました" : "閉塞信号機の定期解除を切りました");
    }

    /// <summary>閉塞信号機（中継信号機を除く。名前に「閉塞」を含むもの）。</summary>
    public IReadOnlyList<SignalDef> All { get; } = rail.Signals.Values
        .Where(s => !s.IsRepeater && s.Name.Contains("閉塞"))
        .OrderBy(s => s.Name[..2]).ThenBy(s => int.TryParse(new string(s.Name.Where(char.IsDigit).ToArray()), out var n) ? n : 0)
        .ToList();

    public bool IsBlock(string name) => All.Any(s => s.Name == name);

    /// <summary>今、定期解除の窓の中か（10秒ごとに1.5秒）。切っていれば false。</summary>
    public bool SweepActive(DateTime now)
    {
        lock (_lock)
        {
            if (!_sweepEnabled) return false;
            if (now - _cycleStartUtc >= SweepInterval) _cycleStartUtc = now;
            return now - _cycleStartUtc < SweepWindow;
        }
    }

    public Dictionary<string, DateTime> Forced()
    {
        lock (_lock) return new Dictionary<string, DateTime>(_forced);
    }

    /// <summary>強制進行にする・やめる。閉塞信号機でなければ null、変わらなければ false。</summary>
    public bool? SetForced(string name, bool on)
    {
        if (!IsBlock(name)) return null;
        bool changed;
        lock (_lock) changed = on ? _forced.TryAdd(name, DateTime.UtcNow) : _forced.Remove(name);
        if (changed) log.Note(on ? $"閉塞信号機 {name} を強制進行にしました" : $"閉塞信号機 {name} の強制進行を解除しました");
        return changed;
    }

    /// <summary>
    /// 1人分の信号の指示に、強制進行と定期解除を入れる。停止にする判断（在線・方向てこ・取り消した進路）を全部入れたあとに呼ぶ。
    /// </summary>
    public void Apply(List<SignalCommand> signals, IReadOnlyDictionary<string, DateTime> forced, bool sweep)
    {
        if (forced.Count > 0)
        {
            signals.RemoveAll(s => forced.ContainsKey(s.Name));
            foreach (var name in forced.Keys) signals.Add(new SignalCommand { Name = name, Phase = "None" });
        }

        if (!sweep) return;
        var commanded = signals.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var s in All)
            if (!commanded.Contains(s.Name)) signals.Add(new SignalCommand { Name = s.Name, Phase = "None" });
    }
}

/// <summary>
/// 在線の記録。在線共有の列車（継続乗務で残している在線を含む）が踏んでいる軌道回路を、変わったときだけ
/// 日ごとのファイル logs/occupancy-yyyyMMdd.log に1行ずつ書く（その列車の今の回路をすべて書く。その行だけで位置が分かる）。
/// ファイルの行の末尾には、回路が変わった報告での走った距離と速度を「@1234.5 v54.0」の形で付ける（回路の長さを出すため。継続乗務の在線には付けない）。
/// 画面用に直近 500 行も持つ。1日のファイルが MaxBytesPerDay を超えたら、その日はもう書かない。
/// 1行 60〜100 バイトほどで、列車1本が回路を移るのは数秒〜数十秒に1回なので、10本で3時間でも 1MB 前後。
/// </summary>
public sealed class OccupancyLog(GameClock clock)
{
    private const long MaxBytesPerDay = 50L * 1024 * 1024;

    private readonly object _lock = new();
    private readonly Dictionary<string, string> _last = new(StringComparer.Ordinal); // 接続ID → 「列番 回路…」
    private readonly List<string> _recent = new();
    private readonly DailyLog _file = new("occupancy", MaxBytesPerDay);

    public List<string> View()
    {
        lock (_lock) return Enumerable.Reverse(_recent).Take(300).ToList();
    }

    public void Record(IReadOnlyList<PlayerInfo> players)
    {
        var now = DateTime.UtcNow;
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        var where = new Dictionary<string, string>(StringComparer.Ordinal); // 接続ID → 「 @距離 v速度」（変化の判定には使わない）
        foreach (var p in players)
        {
            if (p.Mode != ShareMode.Shared || !p.InGame || p.TrackCircuits.Count == 0) continue;
            var ghost = p.ConnectionId.StartsWith(PlayerRegistry.GhostPrefix);
            var ai = p.ConnectionId.StartsWith(PlayerRegistry.AiPrefix);
            var train = (p.TrainNumber.Length > 0 ? p.TrainNumber : "（列番なし）") + (ghost ? "（継続乗務）" : ai ? "（にぎやかし）" : "");
            current[p.ConnectionId] = $"{train} {string.Join(" ", p.TrackCircuits.Order(StringComparer.Ordinal))}";
            if (!ghost && p.CircuitsChangedDistance is { } d)
                where[p.ConnectionId] = string.Create(CultureInfo.InvariantCulture, $" @{d:0.0} v{p.CircuitsChangedSpeed:0.0}");
        }

        List<string>? lines = null;
        List<string>? fileLines = null;
        lock (_lock)
        {
            foreach (var (id, text) in current)
                if (!_last.TryGetValue(id, out var old) || old != text)
                {
                    (lines ??= new()).Add(text);
                    (fileLines ??= new()).Add(text + where.GetValueOrDefault(id, ""));
                }
            foreach (var (id, old) in _last)
                if (!current.ContainsKey(id))
                {
                    var gone = $"{old.Split(' ')[0]} 在線なし（{old[(old.IndexOf(' ') + 1)..]} から消えた）";
                    (lines ??= new()).Add(gone);
                    (fileLines ??= new()).Add(gone);
                }
            _last.Clear();
            foreach (var (id, text) in current) _last[id] = text;
            if (lines == null) return;

            var game = clock.TextAt(now);
            foreach (var l in lines)
            {
                _recent.Add($"{game} {l}");
                if (_recent.Count > 500) _recent.RemoveAt(0);
            }

            Write(now, game, fileLines!);
        }
    }

    private void Write(DateTime now, string game, List<string> lines)
    {
        // 書けなくても配信は続ける（DailyLog は例外を出さない）
        var local = now.ToLocalTime();
        var sb = new StringBuilder();
        foreach (var l in lines) sb.Append($"{local:yyyy-MM-dd HH:mm:ss} {game} {l}{Environment.NewLine}");
        _file.Append(now, sb.ToString(),
            () => $"{local:yyyy-MM-dd HH:mm:ss} {game} この日の在線の記録が {_file.MaxMb}MB を超えたので、ここで書くのをやめます{Environment.NewLine}");
    }
}
