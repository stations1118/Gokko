using System.Diagnostics;
using System.Text.Json;

namespace Gokko.Server.Services;

/// <summary>
/// 誰が呼んでも同じ中身を返す API の応答を、短いあいだ使い回す（JSON にしたバイト列のまま持つ）。
/// 在線位置（/api/tid/state）は、参加者の在線位置の画面・管理画面・信号制御盤の小窓などが1秒ごとに読みに来る。
/// 呼ばれるたびに作ると、人数ぶん RouteEngine のロックを取り合って配信まで遅らせる（2026-11-22 の運転会）ので、
/// 作るのは maxAge ごとに1回だけにする。作っている間に来た呼び出しは、その結果を待って同じものを返す。
/// </summary>
public sealed class SharedJsonCache
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web); // Results.Ok と同じ書き方

    private sealed record Entry(byte[] Bytes, long Timestamp);

    private readonly object _gate = new();
    private Entry? _entry;

    public byte[] Get(TimeSpan maxAge, Func<object> build)
    {
        if (Fresh(Volatile.Read(ref _entry), maxAge) is { } hit) return hit;
        lock (_gate)
        {
            if (Fresh(_entry, maxAge) is { } again) return again; // 待っている間に、ほかの呼び出しが作った
            var bytes = JsonSerializer.SerializeToUtf8Bytes(build(), Options);
            Volatile.Write(ref _entry, new Entry(bytes, Stopwatch.GetTimestamp()));
            return bytes;
        }
    }

    private static byte[]? Fresh(Entry? e, TimeSpan maxAge) =>
        e != null && Stopwatch.GetElapsedTime(e.Timestamp) < maxAge ? e.Bytes : null;
}
