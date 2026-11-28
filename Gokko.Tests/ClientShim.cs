namespace Gokko.Client;

/// <summary>
/// 試験に取り込んだクライアントのコード（StopDistance.cs）が参照する接続先の代わり。試験では表をサーバーから読まない（LoadTableAsync は使わない）。
/// </summary>
internal static class ServerAddress
{
    public static string Current => "http://127.0.0.1";
}
