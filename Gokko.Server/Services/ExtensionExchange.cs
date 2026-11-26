using Gokko.Common;

namespace Gokko.Server.Services;

/// <summary>
/// 内線電話の交換機（列車無線とは別の回線。同時に通話してよい）。
/// ・回線: 司令（司令員みんなで1つ）・駅（盤のある駅ごと。信号制御盤の電話機を開いた人みんなで1つ）・列車（列番ごと。運転士と車掌）
/// ・番号: 司令と駅は管理画面で決める（<see cref="ExtensionBook"/>）。列車は列番
/// ・かけると、相手の回線の電話機がみな鳴り、誰か1人が出たら通話になる（45秒で取り消し）
/// ・保留: 保留にした人の声は送らず、相手の声も届かない（相手には保留音）。保留のあいだ、ほかの電話をかけたり受けたりできる
///   別の通話に出る・かける・保留から戻ると、いまの通話は自動で保留になる（同時に話せるのは1件だけ）
/// ・転送: 相手を別の番号へ回し、自分は抜ける（相手には呼出音）
/// ・一斉呼出: 司令員から全駅を呼ぶ。出た駅から順に入り、出ている人みんなで話せる。司令員が切ると終わる
/// ・運転士は停車中（0km/h）だけ使える。走り出したら、話している通話は自動で保留になる。車掌はいつでも
/// ・音声は列車無線と同じ形式（Opus 40ms）。サーバーは中継するだけで、記録しない
/// ・列車無線とはまったく別のもの（互いに邪魔しない。無線で話しながら電話も使える）
/// ・スマホを受話器にした人（handset）は、PC の接続がしたものとして扱う（音声はスマホへ届ける）
/// </summary>
public sealed class ExtensionExchange(
    PlayerRegistry players, CrewRelay relay, TrainRadio radio, RelayPanels panels, ExtensionBook book, RailData rail, GameData data, TimeProvider clock)
{
    private static readonly TimeSpan RingLife = TimeSpan.FromSeconds(45);
    private const double StoppedKmh = 0.5;
    private const double BudgetBurst = 4;

    public const string KindDispatcher = "dispatcher", KindStation = "station", KindDriver = "driver", KindConductor = "conductor", KindTrain = "train";

    /// <summary>電話機1台（PC の接続、または信号制御盤のブラウザ）。</summary>
    private sealed record Terminal(string Id, string Line, string Label, string Kind, bool CanUse, string Reason, string CrewId, DateTime JoinedUtc);

    private sealed class Budget(DateTimeOffset now)
    {
        public DateTimeOffset CreditAt = now;
        public double Credit = BudgetBurst;
    }

    private sealed class Leg
    {
        public required string Line;
        public required string Label;
        public string? Terminal; // null なら鳴っている（まだ誰も出ていない）
        public bool Held;
        public bool Owner; // 一斉呼出をかけた司令員
        public DateTimeOffset Since;
        public string TransferredBy = "";
    }

    private sealed class Call
    {
        public long Id;
        public bool Page;
        public readonly List<Leg> Legs = new();
        public readonly Dictionary<string, Budget> Budgets = new();
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, (string Station, string CrewId, DateTime At)> _stations = new(); // 盤の電話機の接続ID → 駅
    private readonly Dictionary<string, string> _handsets = new(); // スマホの接続ID → 社員番号
    private readonly Dictionary<string, string> _currentHandset = new(); // 社員番号 → いま使うスマホ
    private readonly Dictionary<long, Call> _calls = new();
    private long _serial;
    private long _revision = 1;

    private static ExtensionResult Result(bool ok, string message) => new() { Ok = ok, Message = message };

    // ---- 電話機 ----

    /// <summary>信号制御盤の電話機として参加する。役職は呼ぶ側で確かめる。</summary>
    public string? AddStation(string connectionId, string stationId, string crewId)
    {
        var station = panels.StationList().FirstOrDefault(s => string.Equals(s.Id, stationId, StringComparison.OrdinalIgnoreCase));
        if (station.Id == null) return $"駅 {stationId} の盤はありません";
        lock (_gate)
        {
            _stations[connectionId] = (station.Id, crewId, DateTime.UtcNow);
            _revision++;
        }

        return null;
    }

    /// <summary>スマホの受話器として参加する。同じ社員番号の前のスマホがあれば、その接続ID を返す。</summary>
    public string? AddHandset(string connectionId, string crewId)
    {
        lock (_gate)
        {
            _handsets[connectionId] = crewId;
            var old = _currentHandset.GetValueOrDefault(crewId);
            _currentHandset[crewId] = connectionId;
            _revision++;
            return old != null && old != connectionId ? old : null;
        }
    }

    public bool IsBrowser(string connectionId)
    {
        lock (_gate) return _stations.ContainsKey(connectionId) || _handsets.ContainsKey(connectionId);
    }

    public bool IsHandset(string connectionId)
    {
        lock (_gate) return _handsets.ContainsKey(connectionId);
    }

    public bool IsReplacedHandset(string connectionId)
    {
        lock (_gate) return _handsets.TryGetValue(connectionId, out var crewId) && _currentHandset.GetValueOrDefault(crewId) != connectionId;
    }

    /// <summary>ブラウザの接続が切れた。</summary>
    public void RemoveBrowser(string connectionId)
    {
        lock (_gate)
        {
            if (_stations.Remove(connectionId)) _revision++;
            if (_handsets.Remove(connectionId, out var crewId))
            {
                if (_currentHandset.GetValueOrDefault(crewId) == connectionId) _currentHandset.Remove(crewId);
                _revision++;
            }

            Clean(Terminals());
        }
    }

    /// <summary>いまの電話機（接続ID → 電話機）。</summary>
    private Dictionary<string, Terminal> Terminals()
    {
        var list = new Dictionary<string, Terminal>();
        foreach (var (id, crewId) in radio.Dispatchers())
            list[id] = new Terminal(id, "D", "司令", KindDispatcher, true, "", crewId, DateTime.MinValue);

        foreach (var p in players.Snapshot())
        {
            if (PlayerRegistry.IsVirtual(p.ConnectionId)) continue;
            var train = p.TrainNumber.Trim();
            var stopped = Math.Abs(p.ReportedSpeed) < StoppedKmh;
            list[p.ConnectionId] = train.Length == 0
                ? new Terminal(p.ConnectionId, "", "運転士（列番未設定）", KindDriver, false, "列番を設定すると使えます", p.CrewId, p.JoinedUtc)
                : new Terminal(p.ConnectionId, "T:" + train, $"{train}列車 運転士", KindDriver, stopped, stopped ? "" : "走行中は使えません（停車中だけ）", p.CrewId, p.JoinedUtc);
        }

        foreach (var c in relay.Conductors())
        {
            var train = c.TrainNumber.Trim();
            list[c.ConnectionId] = train.Length == 0
                ? new Terminal(c.ConnectionId, "", "車掌（列番未設定）", KindConductor, false, "列番を設定すると使えます", c.CrewId, c.JoinedUtc)
                : new Terminal(c.ConnectionId, "T:" + train, $"{train}列車 車掌", KindConductor, true, "", c.CrewId, c.JoinedUtc);
        }

        foreach (var (id, s) in _stations)
            list[id] = new Terminal(id, "S:" + s.Station, StationLabel(s.Station), KindStation, true, "", s.CrewId, s.At);
        return list;
    }

    /// <summary>線区の中の駅か（赤山町 TH58 〜 館浜 TH76。三郷など線区の外の盤は電話帳に載せない）。</summary>
    private static bool InLine(string stationId) =>
        int.TryParse(new string(stationId.Where(char.IsAsciiDigit).ToArray()), out var n) && n is >= 58 and <= 76;

    /// <summary>
    /// 番号を持つ駅（連動装置のある駅をみな。盤のデータがまだない駅も入れる）。路線の順（赤山町 → 館浜）。
    /// 信号制御盤の駅を選ぶ画面（/api/relay/stations）と同じ並び。
    /// </summary>
    public List<(string Id, string Name)> NumberedStations()
    {
        var built = panels.StationList();
        var ids = rail.Routes.Values.Select(r => r.StationId).Where(s => s != "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        ids.UnionWith(built.Select(s => s.Id));
        var order = data.Line.StationIds.Where(ids.Contains).ToList();
        order.AddRange(ids.Except(order, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal));
        return order.Select(id => (id, built.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)).Name ?? data.Line.StationName(id))).ToList();
    }

    private string StationLabel(string stationId)
    {
        var name = panels.StationList().FirstOrDefault(s => s.Id == stationId).Name ?? data.Line.StationName(stationId);
        return name.EndsWith("信号場") ? name : name + "駅";
    }

    private string LineLabel(string line) => line switch
    {
        "D" => "司令",
        _ when line.StartsWith("S:") => StationLabel(line[2..]),
        _ when line.StartsWith("T:") => line[2..] + "列車",
        _ => line
    };

    private string LineNumber(string line) => line switch
    {
        "D" => book.Dispatcher,
        _ when line.StartsWith("S:") => book.Station(line[2..]),
        _ when line.StartsWith("T:") => line[2..],
        _ => ""
    };

    /// <summary>番号から回線を探す（司令・駅の番号が先。なければ列番）。</summary>
    private string? Resolve(string? number, Dictionary<string, Terminal> terms)
    {
        number = (number ?? "").Trim();
        if (number.Length == 0) return null;
        if (number == book.Dispatcher) return "D";
        foreach (var (id, _) in NumberedStations())
            if (book.Station(id) == number) return "S:" + id;
        var train = "T:" + number;
        return terms.Values.Any(t => t.Line == train) ? train : null;
    }

    /// <summary>スマホの受話器（PC の接続ID → スマホの接続ID）。同じ社員番号の、いちばん新しい PC の参加に結ぶ。</summary>
    private Dictionary<string, string> HandsetLinks(Dictionary<string, Terminal> terms)
    {
        var links = new Dictionary<string, string>();
        if (_currentHandset.Count == 0) return links;
        var pcs = terms.Values.Where(t => t.Kind != KindStation && t.CrewId.Length > 0)
            .GroupBy(t => t.CrewId).ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.JoinedUtc).First().Id);
        foreach (var (crewId, phone) in _currentHandset)
            if (pcs.TryGetValue(crewId, out var pc)) links[pc] = phone;
        return links;
    }

    /// <summary>スマホの受話器が結んでいる PC の接続ID（なければ null）。</summary>
    public string? PcOfHandset(string handsetId)
    {
        lock (_gate)
        {
            var links = HandsetLinks(Terminals());
            return links.FirstOrDefault(kv => kv.Value == handsetId).Key;
        }
    }

    /// <summary>この PC にスマホを結んでいるか（そのあいだ、PC のマイクの音は捨てる）。</summary>
    public bool HasHandset(string pcId)
    {
        lock (_gate) return _currentHandset.Count > 0 && HandsetLinks(Terminals()).ContainsKey(pcId);
    }

    /// <summary>音声の送り先を、スマホを結んでいる人はスマホへ置き換えるための組（PC → スマホ）。</summary>
    public Dictionary<string, string> Links()
    {
        lock (_gate) return HandsetLinks(Terminals());
    }

    // ---- 片づけ ----

    private void Clean(Dictionary<string, Terminal> terms)
    {
        var now = clock.GetUtcNow();
        foreach (var call in _calls.Values.ToList())
        {
            foreach (var leg in call.Legs.ToList())
            {
                if (leg.Terminal != null)
                {
                    if (!terms.TryGetValue(leg.Terminal, out var t))
                    {
                        RemoveLeg(call, leg);
                        continue;
                    }

                    // 運転士が走り出したら、話している通話は保留にする
                    if (!t.CanUse && !leg.Held)
                    {
                        leg.Held = true;
                        _revision++;
                    }
                }
                else if (now - leg.Since > RingLife || !terms.Values.Any(t => t.Line == leg.Line && !call.Legs.Any(l => l.Terminal == t.Id)))
                    RemoveLeg(call, leg);
            }

            EndIfDone(call);
        }
    }

    private void RemoveLeg(Call call, Leg leg)
    {
        call.Legs.Remove(leg);
        if (leg.Terminal != null) call.Budgets.Remove(leg.Terminal);
        _revision++;
    }

    private void EndIfDone(Call call)
    {
        var done = call.Page
            ? !call.Legs.Any(l => l.Owner) || call.Legs.Count < 2
            : call.Legs.Count < 2 || call.Legs.All(l => l.Terminal == null);
        if (!done) return;
        _calls.Remove(call.Id);
        _revision++;
    }

    private Dictionary<string, Terminal> Snapshot()
    {
        var terms = Terminals();
        Clean(terms);
        return terms;
    }

    /// <summary>この電話機が話している通話を、ほかはみな保留にする（同時に話せるのは1件だけ）。</summary>
    private void HoldOthers(string terminal, Call? except)
    {
        foreach (var call in _calls.Values)
        {
            if (call == except) continue;
            foreach (var leg in call.Legs)
                if (leg.Terminal == terminal && !leg.Held)
                {
                    leg.Held = true;
                    _revision++;
                }
        }
    }

    private bool Present(Dictionary<string, Terminal> terms, string line, params string?[] except) =>
        terms.Values.Any(t => t.Line == line && !except.Contains(t.Id));

    // ---- 操作 ----

    public ExtensionResult Dial(string id, string? number)
    {
        lock (_gate)
        {
            var terms = Snapshot();
            if (!terms.TryGetValue(id, out var me)) return Result(false, "内線を使えません（参加していません）");
            if (!me.CanUse) return Result(false, me.Reason);
            var line = Resolve(number, terms);
            if (line == null) return Result(false, $"番号 {number?.Trim()} は、いま使われていません");
            if (line == me.Line) return Result(false, "自分の番号です");
            if (!Present(terms, line, id)) return Result(false, $"{LineLabel(line)} には、いま出られる人がいません");
            if (_calls.Values.Any(c => c.Legs.Any(l => l.Terminal == id) && c.Legs.Any(l => l.Line == line && l.Terminal == null)))
                return Result(false, $"{LineLabel(line)} を呼び出し中です");

            HoldOthers(id, null);
            var now = clock.GetUtcNow();
            var call = new Call { Id = ++_serial };
            call.Legs.Add(new Leg { Line = me.Line, Label = me.Label, Terminal = id, Since = now });
            call.Legs.Add(new Leg { Line = line, Label = LineLabel(line), Since = now });
            call.Budgets[id] = new Budget(now);
            _calls[call.Id] = call;
            _revision++;
            return Result(true, $"{LineLabel(line)} を呼び出しています");
        }
    }

    public ExtensionResult Page(string id)
    {
        lock (_gate)
        {
            var terms = Snapshot();
            if (!terms.TryGetValue(id, out var me) || me.Kind != KindDispatcher) return Result(false, "一斉呼出は司令員だけができます");
            if (_calls.Values.Any(c => c.Page && c.Legs.Any(l => l.Owner && l.Terminal == id))) return Result(false, "一斉呼出中です");
            var stations = panels.StationList().Select(s => "S:" + s.Id).Where(l => Present(terms, l, id)).ToList();
            if (stations.Count == 0) return Result(false, "電話機を開いている駅がありません");

            HoldOthers(id, null);
            var now = clock.GetUtcNow();
            var call = new Call { Id = ++_serial, Page = true };
            call.Legs.Add(new Leg { Line = me.Line, Label = me.Label, Terminal = id, Owner = true, Since = now });
            foreach (var line in stations) call.Legs.Add(new Leg { Line = line, Label = LineLabel(line), Since = now });
            call.Budgets[id] = new Budget(now);
            _calls[call.Id] = call;
            _revision++;
            return Result(true, $"全駅（{stations.Count}駅）を一斉に呼び出しています");
        }
    }

    public ExtensionResult Answer(string id, long callId)
    {
        lock (_gate)
        {
            var terms = Snapshot();
            if (!terms.TryGetValue(id, out var me)) return Result(false, "内線を使えません（参加していません）");
            if (!_calls.TryGetValue(callId, out var call)) return Result(false, "この着信は切れました");
            if (call.Legs.Any(l => l.Terminal == id)) return Result(false, "この通話には出ています");
            var leg = call.Legs.FirstOrDefault(l => l.Terminal == null && l.Line == me.Line);
            if (leg == null) return Result(false, "この着信には、ほかの人が出ました");
            if (!me.CanUse) return Result(false, me.Reason);

            HoldOthers(id, call);
            var now = clock.GetUtcNow();
            leg.Terminal = id;
            leg.Label = me.Label;
            leg.Since = now;
            call.Budgets[id] = new Budget(now);
            _revision++;
            return Result(true, "出ました");
        }
    }

    public ExtensionResult Hangup(string id, long callId)
    {
        lock (_gate)
        {
            Snapshot();
            if (!_calls.TryGetValue(callId, out var call)) return Result(true, "切れています");
            var leg = call.Legs.FirstOrDefault(l => l.Terminal == id);
            if (leg == null) return Result(false, "この通話には出ていません");
            RemoveLeg(call, leg);
            EndIfDone(call);
            return Result(true, "切りました");
        }
    }

    public ExtensionResult Hold(string id, long callId, bool on)
    {
        lock (_gate)
        {
            var terms = Snapshot();
            if (!_calls.TryGetValue(callId, out var call) || call.Legs.FirstOrDefault(l => l.Terminal == id) is not { } leg)
                return Result(false, "この通話には出ていません");
            if (leg.Held == on) return Result(true, on ? "保留中です" : "通話中です");
            if (!on)
            {
                if (terms.TryGetValue(id, out var me) && !me.CanUse) return Result(false, me.Reason);
                HoldOthers(id, call);
            }

            leg.Held = on;
            _revision++;
            return Result(true, on ? "保留にしました" : "保留から戻りました");
        }
    }

    public ExtensionResult Transfer(string id, long callId, string? number)
    {
        lock (_gate)
        {
            var terms = Snapshot();
            if (!_calls.TryGetValue(callId, out var call) || call.Legs.FirstOrDefault(l => l.Terminal == id) is not { } mine)
                return Result(false, "この通話には出ていません");
            if (call.Page) return Result(false, "一斉呼出は転送できません");
            var other = call.Legs.FirstOrDefault(l => l != mine);
            if (other?.Terminal == null) return Result(false, "相手が出てから転送してください");
            var line = Resolve(number, terms);
            if (line == null) return Result(false, $"番号 {number?.Trim()} は、いま使われていません");
            if (line == other.Line) return Result(false, "相手と同じ番号には転送できません");
            if (!Present(terms, line, other.Terminal)) return Result(false, $"{LineLabel(line)} には、いま出られる人がいません");

            var now = clock.GetUtcNow();
            RemoveLeg(call, mine);
            call.Legs.Add(new Leg { Line = line, Label = LineLabel(line), Since = now, TransferredBy = mine.Label });
            other.Held = false; // 転送のあいだ保留にしていても、相手には呼出音を聞かせる
            _revision++;
            return Result(true, $"{other.Label} を {LineLabel(line)} へ転送しました");
        }
    }

    // ---- 音声 ----

    /// <summary>音声の宛先（接続ID）。送ってよくない音声なら空。</summary>
    public IReadOnlyList<string> Route(string id, TrainRadioPacket packet)
    {
        if (packet.Opus is not { Length: > 0 and <= TrainRadioFormat.MaxPacketBytes }) return [];
        lock (_gate)
        {
            if (!_calls.TryGetValue(packet.SessionId, out var call)) return [];
            var mine = call.Legs.FirstOrDefault(l => l.Terminal == id);
            if (mine == null || mine.Held || !call.Budgets.TryGetValue(id, out var budget)) return [];
            var now = clock.GetUtcNow();
            budget.Credit = Math.Min(BudgetBurst, budget.Credit + Math.Max(0, (now - budget.CreditAt).TotalSeconds) * TrainRadioFormat.PacketsPerSecond);
            budget.CreditAt = now;
            if (budget.Credit < 1) return [];
            budget.Credit -= 1;
            packet.SenderId = id; // なりすましを許さない
            return call.Legs.Where(l => l.Terminal != null && l.Terminal != id && !l.Held).Select(l => l.Terminal!).Distinct().ToList();
        }
    }

    // ---- 配信 ----

    /// <summary>電話機ごとの状態（接続ID → 状態）。PC の接続と、盤のブラウザ。</summary>
    public Dictionary<string, ExtensionState> Views()
    {
        lock (_gate)
        {
            var terms = Snapshot();
            var links = HandsetLinks(terms);
            var now = clock.GetUtcNow();

            // 電話帳（ワンタッチのボタン）: 司令所と、線区の中の盤のある駅（館浜→赤山町の順）。誰もいなくても初めから並べる。
            // 列車は載せない（列番を押せばかけられる）
            var directory = new List<ExtensionEntry>
            {
                new() { Number = book.Dispatcher, Label = "司令所", Kind = KindDispatcher, Present = Present(terms, "D") }
            };
            foreach (var (sid, _) in Enumerable.Reverse(NumberedStations()).Where(s => InLine(s.Id)))
                directory.Add(new ExtensionEntry { Number = book.Station(sid), Label = StationLabel(sid), Kind = KindStation, Present = Present(terms, "S:" + sid) });

            var views = new Dictionary<string, ExtensionState>();
            foreach (var me in terms.Values)
            {
                var calls = new List<ExtensionCall>();
                foreach (var call in _calls.Values.OrderBy(c => c.Id))
                {
                    var mine = call.Legs.FirstOrDefault(l => l.Terminal == me.Id);
                    var ringing = mine == null && me.Line.Length > 0 ? call.Legs.FirstOrDefault(l => l.Terminal == null && l.Line == me.Line) : null;
                    if (mine == null && ringing == null) continue;
                    var others = call.Legs.Where(l => l != mine && l != ringing).ToList();
                    var answered = others.Where(l => l.Terminal != null).ToList();

                    string status = ringing != null ? "incoming"
                        : mine!.Held ? "held"
                        : answered.Count == 0 ? "outgoing"
                        : "talking";

                    string peer, peerNumber;
                    if (call.Page && mine is { Owner: true })
                    {
                        var joined = call.Legs.Count(l => !l.Owner && l.Terminal != null);
                        var total = call.Legs.Count(l => !l.Owner);
                        peer = $"全駅 一斉（{joined}/{total}駅）";
                        peerNumber = "";
                    }
                    else if (call.Page)
                    {
                        var owner = call.Legs.FirstOrDefault(l => l.Owner);
                        peer = (owner?.Label ?? "司令") + "（一斉）";
                        peerNumber = owner != null ? LineNumber(owner.Line) : "";
                    }
                    else
                    {
                        var other = answered.FirstOrDefault() ?? others.FirstOrDefault();
                        peer = other?.Label ?? "";
                        peerNumber = other != null ? LineNumber(other.Line) : "";
                    }

                    var peerHeld = call.Page
                        ? (mine is { Owner: true } ? answered.Count > 0 && answered.All(l => l.Held) : others.Any(l => l.Owner && l.Held))
                        : answered.Any(l => l.Held);

                    calls.Add(new ExtensionCall
                    {
                        Id = call.Id,
                        Status = status,
                        IsPage = call.Page,
                        Peer = peer,
                        PeerNumber = peerNumber,
                        PeerHeld = peerHeld,
                        TransferredBy = (ringing ?? mine)!.TransferredBy,
                        Parties = call.Legs.Where(l => l.Terminal != null).Select(l => new ExtensionParty { Id = l.Terminal!, Label = l.Label, Held = l.Held }).ToList(),
                        Seconds = (int)Math.Max(0, (now - (ringing ?? mine)!.Since).TotalSeconds)
                    });
                }

                views[me.Id] = new ExtensionState
                {
                    Revision = _revision,
                    SelfId = me.Id,
                    Label = me.Label,
                    Number = me.Line.Length > 0 ? LineNumber(me.Line) : "",
                    Kind = me.Kind,
                    CanUse = me.CanUse,
                    Reason = me.Reason,
                    HandsetLinked = links.ContainsKey(me.Id),
                    Calls = calls,
                    Directory = directory.Where(d => !(me.Line.Length > 0 && LineNumber(me.Line) == d.Number)).ToList()
                };
            }

            return views;
        }
    }

    /// <summary>スマホの受話器（スマホの接続ID → 結んでいる PC の接続ID か null）。</summary>
    public Dictionary<string, string?> Handsets()
    {
        lock (_gate)
        {
            if (_handsets.Count == 0) return new();
            var links = HandsetLinks(Terminals());
            var byPhone = links.ToDictionary(kv => kv.Value, kv => kv.Key);
            return _handsets.Keys.ToDictionary(h => h, h => byPhone.GetValueOrDefault(h));
        }
    }
}
