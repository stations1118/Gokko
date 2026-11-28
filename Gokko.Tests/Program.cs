using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gokko.Tests;

// 走らせて確かめる試験。
// サーバーを手元で起動し、ダイヤ運転・自動運転列車アリにして、ロープレ時刻を早回しで進める。走っている間、画面と同じ API から
// 列車の様子を読み続け、「決まり」を破ったら記録して、最後に失敗（終了コード 1）にする。
//
// 使い方（リポジトリのいちばん上のフォルダで）:
//   dotnet run --project Gokko.Tests                         … 既定の時間帯を全部
//   dotnet run --project Gokko.Tests -- --slices 17:30 --minutes 30 --speed 8
//
// 決まり（見張るもの）は Rules.cs。破ってもよいと分かっているもの（まだ直していない既知のもの）は known.txt に書く。
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var opt = Options.Parse(args);
        if (opt == null) return 2;

        var root = FindRoot();
        var serverDir = opt.ServerDir ?? BuildServer(root);
        if (serverDir == null) return 2;
        var known = Known.Load(Path.Combine(root, "Gokko.Tests", "known.txt"));
        var outDir = Path.GetFullPath(opt.OutDir ?? Path.Combine(root, "Gokko.Tests", "out"));
        Directory.CreateDirectory(outDir);

        Console.WriteLine($"走らせて確かめる試験: 時間帯 {string.Join("・", opt.Slices)} から {opt.Minutes}分ずつ、{opt.Speed}倍速、同時に {opt.Parallel}本");
        var gate = new SemaphoreSlim(opt.Parallel);
        var port = opt.Port;
        var runs = opt.Slices.Select(async (slice, i) =>
        {
            await gate.WaitAsync();
            try { return await new Run(slice, opt, serverDir, outDir, port + i).GoAsync(); }
            finally { gate.Release(); }
        }).ToList();
        var results = await Task.WhenAll(runs);

        // まとめ
        var failed = 0;
        Console.WriteLine();
        foreach (var r in results)
        {
            var fresh = r.Violations.Where(v => !known.Covers(v)).ToList();
            var old = r.Violations.Count - fresh.Count;
            Console.WriteLine($"== {r.Slice} から {opt.Minutes}分: 列車 {r.TrainsSeen}本、破った決まり {fresh.Count}件" + (old > 0 ? $"（ほかに、分かっているもの {old}件）" : "")
                              + (r.Error != null ? $"　※ {r.Error}" : ""));
            foreach (var v in fresh) Console.WriteLine($"  ✗ [{v.Rule}] {v.GameTime} {v.Train} {v.Text}");
            foreach (var v in r.Violations.Where(known.Covers)) Console.WriteLine($"  （分かっている）[{v.Rule}] {v.GameTime} {v.Train} {v.Text}");
            if (fresh.Count > 0 || r.Error != null) failed++;
        }
        Console.WriteLine();
        if (opt.SelfTest)
        {
            var caught = results.Any(r => r.Violations.Any(v => v.Rule == "詰まり"));
            Console.WriteLine(caught ? "確かめ 合格: わざと入れた抑止を「詰まり」として捕まえました" : "確かめ 不合格: わざと入れた抑止を捕まえられませんでした（試験が効いていません）");
            return caught ? 0 : 1;
        }
        Console.WriteLine(failed == 0 ? "合格: 決まりを破った所はありません" : $"不合格: {failed}つの時間帯で決まりを破りました。記録は {outDir} にあります");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>リポジトリのいちばん上（Gokko.Server のあるフォルダ）。</summary>
    private static string FindRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Gokko.Server"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    /// <summary>サーバーをビルドして、出力先のフォルダを返す（失敗したら null）。</summary>
    private static string? BuildServer(string root)
    {
        var outDir = Path.Combine(Path.GetTempPath(), "gokko-tests", "server-" + Guid.NewGuid().ToString("N")[..8]);
        Console.WriteLine("サーバーをビルドしています…");
        var p = Process.Start(new ProcessStartInfo("dotnet", $"build \"{Path.Combine(root, "Gokko.Server")}\" -c Release -nologo -v q -o \"{outDir}\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        })!;
        var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode == 0) return outDir;
        Console.WriteLine("サーバーのビルドに失敗しました");
        Console.WriteLine(text);
        return null;
    }
}

public sealed record Options(List<string> Slices, int Minutes, double Speed, int Parallel, int Port, string? ServerDir, string? OutDir, bool SelfTest)
{
    /// <summary>既定の時間帯: 朝の出庫・朝ラッシュ・昼・夕ラッシュ・夜の入庫。</summary>
    private static readonly string[] DefaultSlices = ["05:00", "07:30", "12:00", "17:30", "22:30"];

    public static Options? Parse(string[] args)
    {
        var slices = DefaultSlices.ToList();
        int minutes = 60, parallel = 3, port = 5210;
        double speed = 8;
        string? server = null, outDir = null;
        var selfTest = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i])
            {
                case "--slices": slices = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); break;
                case "--minutes": minutes = int.Parse(Next()); break;
                case "--speed": speed = double.Parse(Next()); break;
                case "--parallel": parallel = Math.Max(1, int.Parse(Next())); break;
                case "--port": port = int.Parse(Next()); break;
                case "--server": server = Next(); break;
                case "--out": outDir = Next(); break;
                // 試験そのものの確かめ: 走っている列車を1本わざと抑止して、「詰まり」を捕まえられるかを見る（捕まえたら合格）
                case "--selftest": selfTest = true; slices = [slices.Contains("17:30") ? "17:30" : slices[0]]; minutes = Math.Max(minutes, 30); break;
                default:
                    Console.WriteLine("使い方: dotnet run --project Gokko.Tests -- [--slices 07:30,17:30] [--minutes 60] [--speed 8] [--parallel 3] [--server ビルド済みのフォルダ] [--out 記録の置き場]");
                    return null;
            }
        }
        return new Options(slices, minutes, speed, parallel, port, server, outDir, selfTest);
    }
}

/// <summary>破った決まり1件。</summary>
public sealed record Violation(string Rule, string GameTime, string Train, string Text);

public sealed record RunResult(string Slice, int TrainsSeen, List<Violation> Violations, string? Error);

/// <summary>1つの時間帯を走らせる（サーバー1つ）。</summary>
public sealed class Run(string slice, Options opt, string serverDir, string outDir, int port)
{
    private const string Key = "sim-test-key"; // 手元で起動する試験用のサーバーだけで使う管理キー
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly StringBuilder _serverLog = new();

    public async Task<RunResult> GoAsync()
    {
        var name = slice.Replace(":", "");
        var work = Path.Combine(Path.GetTempPath(), "gokko-tests", $"run-{name}-{Guid.NewGuid().ToString("N")[..6]}");
        CopyDir(serverDir, work);
        // 前の記録・名簿は持ち込まない
        foreach (var f in Directory.GetFiles(Path.Combine(work, "Data"), "Crew.*")) File.Delete(f);
        if (Directory.Exists(Path.Combine(work, "logs"))) Directory.Delete(Path.Combine(work, "logs"), true);

        var url = $"http://127.0.0.1:{port}";
        var psi = new ProcessStartInfo("dotnet", $"Gokko.Server.dll --urls {url} --Gokko:AdminKey={Key} --Gokko:Random:Enabled=false")
        {
            WorkingDirectory = work, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using var server = Process.Start(psi)!;
        server.OutputDataReceived += (_, e) => { if (e.Data != null) lock (_serverLog) _serverLog.AppendLine(e.Data); };
        server.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_serverLog) _serverLog.AppendLine(e.Data); };
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        _http.DefaultRequestHeaders.Add("X-Admin-Key", Key);

        var rules = new Rules();
        string? error = null;
        try
        {
            if (!await WaitUpAsync(url)) throw new InvalidOperationException("サーバーが起動しませんでした");
            await PostAsync(url, "mode", new { dia = true });
            await PostAsync(url, "clock", new { time = slice + ":00", running = true });
            await PostAsync(url, "clock", new { speed = opt.Speed });
            await PostAsync(url, "ai-trains/mode", new { on = true });

            var realSeconds = opt.Minutes * 60 / opt.Speed;
            var started = Stopwatch.StartNew();
            var injected = !opt.SelfTest;
            while (started.Elapsed.TotalSeconds < realSeconds)
            {
                await Task.Delay(1000);
                if (server.HasExited) throw new InvalidOperationException("サーバーが止まりました");
                if (!injected && started.Elapsed.TotalSeconds > 20) injected = await InjectHoldAsync(url);
                var tid = await _http.GetFromJsonAsync<JsonElement>($"{url}/api/tid/state");
                var ai = await _http.GetFromJsonAsync<JsonElement>($"{url}/api/admin/ai-trains");
                var dia = await _http.GetFromJsonAsync<JsonElement>($"{url}/api/admin/diagram");
                rules.Observe(tid, ai, dia);
            }
            rules.Finish();
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            try { if (!server.HasExited) server.Kill(true); } catch { }
            server.WaitForExit(5000);
        }

        string log;
        lock (_serverLog) log = _serverLog.ToString();
        rules.CheckServerLog(log);

        // 記録を残す（破った決まりがあったときに、あとから調べられるように）
        var keep = Path.Combine(outDir, name);
        if (Directory.Exists(keep)) Directory.Delete(keep, true);
        Directory.CreateDirectory(keep);
        File.WriteAllText(Path.Combine(keep, "server.log"), log);
        File.WriteAllLines(Path.Combine(keep, "violations.txt"), rules.Violations.Select(v => $"[{v.Rule}] {v.GameTime} {v.Train} {v.Text}"));
        var logs = Path.Combine(work, "logs");
        if (Directory.Exists(logs))
            foreach (var f in Directory.GetFiles(logs)) File.Copy(f, Path.Combine(keep, Path.GetFileName(f)), true);
        try { Directory.Delete(work, true); } catch { }

        Console.WriteLine($"  {slice} から {opt.Minutes}分 … 終わり（列車 {rules.TrainsSeen}本・破った決まり {rules.Violations.Count}件）");
        return new RunResult(slice, rules.TrainsSeen, rules.Violations, error);
    }

    /// <summary>試験そのものの確かめ用: 走っている列車を1本、2つ先の駅で抑止する（外さない）。入れられたら true。</summary>
    private async Task<bool> InjectHoldAsync(string url)
    {
        var routes = await _http.GetFromJsonAsync<JsonElement>($"{url}/api/admin/routes");
        foreach (var t in routes.GetProperty("trains").EnumerateArray())
        {
            var stations = t.GetProperty("stations").EnumerateArray().ToList();
            if (stations.Count < 4) continue;
            var train = t.GetProperty("train").GetString();
            var station = stations[1].GetProperty("stationId").GetString();
            await PostAsync(url, "route/hold", new { train, station, hold = true });
            Console.WriteLine($"  （確かめ）{train} を {stations[1].GetProperty("name").GetString()} で抑止しました。「詰まり」が出れば、試験は効いています");
            return true;
        }
        return false;
    }

    private async Task<bool> WaitUpAsync(string url)
    {
        for (var i = 0; i < 60; i++)
        {
            try { if ((await _http.GetAsync($"{url}/api/clock")).IsSuccessStatusCode) return true; }
            catch { }
            await Task.Delay(500);
        }
        return false;
    }

    private async Task PostAsync(string url, string path, object body)
    {
        var res = await _http.PostAsJsonAsync($"{url}/api/admin/{path}", body);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"{path} が {(int)res.StatusCode} を返しました");
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to), true);
    }
}

/// <summary>破ってもよいと分かっているもの（known.txt）。1行に「決まりの名前 TAB 列番か * TAB 本文に含まれる言葉」。# から後ろは説明。</summary>
public sealed class Known
{
    private readonly List<(string Rule, string Train, string Part)> _items = new();

    public static Known Load(string path)
    {
        var k = new Known();
        if (!File.Exists(path)) return k;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = Regex.Replace(raw, "#.*$", "").Trim();
            if (line.Length == 0) continue;
            var p = line.Split('\t', StringSplitOptions.TrimEntries);
            k._items.Add((p[0], p.Length > 1 ? p[1] : "*", p.Length > 2 ? p[2] : ""));
        }
        return k;
    }

    public bool Covers(Violation v) =>
        _items.Any(x => x.Rule == v.Rule && (x.Train == "*" || x.Train == v.Train) && (x.Part.Length == 0 || v.Text.Contains(x.Part)));
}
