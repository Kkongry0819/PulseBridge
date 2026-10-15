using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PulseBridge;
public sealed class BridgeServer : IDisposable
{
    readonly HeartState state;
    readonly Func<bool> canIngest;
    readonly CancellationTokenSource stop = new();
    readonly TcpListener listener;
    readonly SemaphoreSlim slots = new(12);
    public string Key { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public BridgeServer(HeartState state, bool lan, Func<bool> canIngest, int port = 8976, bool start = true)
    {
        this.state = state; this.canIngest = canIngest;
        listener = new TcpListener(lan ? IPAddress.Any : IPAddress.Loopback, port);
        if (start) { listener.Start(); _ = Run(); }
    }
    async Task Run()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                if (!await slots.WaitAsync(0)) { client.Dispose(); continue; }
                _ = Handle(client);
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
    }
    async Task Handle(TcpClient client)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(4));
            var ct = deadline.Token;
            try
            {
                using var stream = client.GetStream();
                await Process(stream, IPAddress.IsLoopback(((IPEndPoint)client.Client.RemoteEndPoint!).Address), ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
            finally { slots.Release(); }
        }
    }
    internal async Task Process(Stream stream, bool local, CancellationToken ct)
    {
                using var header = new MemoryStream(); var one = new byte[1];
                while (header.Length < 8192)
                {
                    if (await stream.ReadAsync(one, ct) == 0) return;
                    header.WriteByte(one[0]);
                    var bytes = header.GetBuffer(); int n = (int)header.Length;
                    if (n >= 4 && bytes[n-4] == 13 && bytes[n-3] == 10 && bytes[n-2] == 13 && bytes[n-1] == 10) break;
                }
                if (header.Length >= 8192) { await Reply(stream, 431, "请求头过大", ct); return; }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var first = lines[0].Split(' ');
                if (first.Length != 3) { await Reply(stream, 400, "请求格式错误", ct); return; }
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1).Where(x => x.Contains(':')))
                {
                    var at = line.IndexOf(':'); var key = line[..at].Trim();
                    if (headers.ContainsKey(key)) { await Reply(stream, 400, "重复请求头", ct); return; }
                    headers[key] = line[(at+1)..].Trim();
                }
                var path = first[1].Split('?')[0];
                bool authorized = headers.GetValueOrDefault("Authorization") == "Bearer " + Key;
                if ((!local || first[0] == "POST") && !authorized) { await Reply(stream, 401, "需要 Bearer Token", ct); return; }
                if (headers.ContainsKey("Transfer-Encoding")) { await Reply(stream, 400, "不支持分块请求", ct); return; }
                if (first[0] == "GET" && path == "/text") { await Reply(stream, 200, state.Get().text, ct); return; }
                if (first[0] == "GET" && path == "/heart-rate") { await Reply(stream, 200, JsonSerializer.Serialize(state.Get()), ct, true); return; }
                if (first[0] == "GET" && path == "/health") { await Reply(stream, 200, "PulseBridge ready", ct); return; }
                if (first[0] != "POST" || path != "/heart-rate") { await Reply(stream, 404, "Not found", ct); return; }
                if (!canIngest()) { await Reply(stream, 409, "请先连接本地 HTTP 数据源", ct); return; }
                if (!int.TryParse(headers.GetValueOrDefault("Content-Length"), out int count) || count is < 1 or > 4096) { await Reply(stream, 400, "Content-Length 必须为 1–4096", ct); return; }
                var body = new byte[count]; await stream.ReadExactlyAsync(body, ct);
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    int bpm = doc.RootElement.GetProperty("bpm").GetInt32();
                    var time = doc.RootElement.TryGetProperty("measured_at", out var measured) ? DateTimeOffset.FromUnixTimeMilliseconds(measured.GetInt64()) : DateTimeOffset.UtcNow;
                    if (!state.Add(bpm, time, "本地 HTTP")) { await Reply(stream, 422, "心率无效、时间过期或样本乱序", ct); return; }
                    await Reply(stream, 200, "ok", ct);
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentOutOfRangeException or OverflowException) { await Reply(stream, 400, "需要 JSON: {\"bpm\":78}", ct); }
    }
    static async Task Reply(Stream stream, int code, string text, CancellationToken ct, bool json = false)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} Response\r\nContent-Type: {(json ? "application/json" : "text/plain")}; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct); await stream.WriteAsync(body, ct);
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); }
}
