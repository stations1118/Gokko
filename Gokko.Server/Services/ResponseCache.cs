using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Gokko.Server.Services;

/// <summary>
/// 何人もの画面が同じものを短い間隔で読みに来る API の答えを、少しのあいだ使い回す。
/// ダイヤグラム（/diagram）は開いている画面ごとに1秒に1回読まれ、毎回すべての列車の見込みを計算し直していた。
/// 答えは JSON にしたバイト列で持つ（同じものを何度も JSON にしない。JSON にしている最中に中身が変わることもない）。
/// </summary>
public sealed class ResponseCache(IOptions<JsonOptions> json)
{
    private sealed class Entry
    {
        public readonly object Gate = new();
        public byte[]? Body;
        public long At;
        public long Version;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly JsonSerializerOptions _options = json.Value.SerializerOptions;

    /// <summary>使い回している答えを捨てる（GET 以外の要求のあと。◆をずらしたなどの操作が、次に読んだときにすぐ見えるように）。</summary>
    public void Clear()
    {
        foreach (var e in _entries.Values)
            lock (e.Gate) e.Body = null;
    }

    /// <summary>
    /// key の答え。version が前と同じ間はそれを使い回し、変わったら build で作り直す（大きくて、めったに変わらない答え用。
    /// key ごとに1つしか持たない。Clear のあとも作り直す）。
    /// </summary>
    public IResult GetVersioned(string key, long version, Func<object> build)
    {
        var e = _entries.GetOrAdd(key, _ => new Entry());
        lock (e.Gate)
        {
            if (e.Body == null || e.Version != version)
            {
                e.Body = JsonSerializer.SerializeToUtf8Bytes(build(), _options);
                e.Version = version;
            }

            return Results.Bytes(e.Body, "application/json; charset=utf-8");
        }
    }

    /// <summary>key の答え。ttl より新しいものがあればそれを、なければ build で作り直す（同時に来た人は、1人が作るのを待つ）。</summary>
    public IResult Get(string key, TimeSpan ttl, Func<object> build)
    {
        var e = _entries.GetOrAdd(key, _ => new Entry());
        lock (e.Gate)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (e.Body == null || System.Diagnostics.Stopwatch.GetElapsedTime(e.At, now) >= ttl)
            {
                e.Body = JsonSerializer.SerializeToUtf8Bytes(build(), _options);
                e.At = now;
            }

            return Results.Bytes(e.Body, "application/json; charset=utf-8");
        }
    }
}
