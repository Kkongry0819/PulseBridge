using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PulseBridge;
public static class Providers
{
    public static async Task Demo(HeartState state, CancellationToken ct)
    {
        double phase = 0;
        while (!ct.IsCancellationRequested)
        {
            state.Add((int)(76 + Math.Sin(phase) * 8 + Random.Shared.Next(-2, 3)), DateTimeOffset.UtcNow, "演示模式", true);
            phase += .18; await Task.Delay(1000, ct);
        }
    }
    public static async Task Pulsoid(HeartState state, string token, Action<string> log, CancellationToken ct)
    {
        int delay = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connect.CancelAfter(TimeSpan.FromSeconds(15)); log("正在连接 Pulsoid…");
                await ws.ConnectAsync(new Uri("wss://dev.pulsoid.net/api/v1/data/real_time"), connect.Token);
                log("Pulsoid 已连接，等待手表推送心率");
                var buffer = new byte[8192];
                while (ws.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream(); WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close) throw new IOException("远端关闭连接");
                        message.Write(buffer, 0, result.Count);
                        if (message.Length > 65536) throw new IOException("云端消息过大");
                    } while (!result.EndOfMessage);
                    try
                    {
                        var (bpm, time) = HeartState.ParsePulsoid(Encoding.UTF8.GetString(message.ToArray()));
                        if (state.Add(bpm, time, "Pulsoid")) delay = 1;
                    }
                    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException) { log("已忽略无效的云端消息"); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception)
            {
                log($"Pulsoid 连接中断；{delay} 秒后重连。请检查网络、Token 和读取权限。");
                await Task.Delay(TimeSpan.FromSeconds(delay), ct); delay = Math.Min(delay * 2, 30);
            }
        }
    }
    public static async Task Ble(string mode, string? address, Action<JsonElement> line, CancellationToken ct)
    {
        var ps = Path.Combine(AppContext.BaseDirectory, "BleHelper.exe");
        var info = new ProcessStartInfo(ps) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        info.ArgumentList.Add(mode);
        if (address != null) info.ArgumentList.Add(address);
        using var process = Process.Start(info) ?? throw new IOException("无法启动蓝牙适配器");
        using var cancel = ct.Register(() => { try { process.Kill(true); } catch { } });
        var errors = process.StandardError.ReadToEndAsync(ct);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct) is string json)
            {
                try { using var doc = JsonDocument.Parse(json); line(doc.RootElement.Clone()); } catch (JsonException) { }
            }
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0 && !ct.IsCancellationRequested) throw new IOException("蓝牙适配器失败，请开启 Windows 蓝牙并确认设备支持心率广播。");
        }
        finally { try { if (!process.HasExited) process.Kill(true); } catch { } }
    }
}
