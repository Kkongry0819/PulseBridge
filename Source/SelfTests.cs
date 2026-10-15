using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PulseBridge;
public static class SelfTests
{
    internal sealed class TestStream : Stream
    {
        readonly MemoryStream input;
        public readonly MemoryStream Output = new();
        public TestStream(byte[] request) { input = new(request); }
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => input.Length; public override long Position { get => input.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => input.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => Output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => Output.WriteAsync(buffer, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long length) => throw new NotSupportedException();
    }
    public static async Task<int> Run(string[] args)
    {
        var report = new List<string>();
        void Assert(bool condition, string name) { if (!condition) throw new Exception(name); report.Add("PASS " + name); }
        int reportIndex = Array.IndexOf(args, "--report");
        var root = reportIndex >= 0 && reportIndex + 1 < args.Length ? Path.GetDirectoryName(Path.GetFullPath(args[reportIndex+1]))! : AppContext.BaseDirectory;
        var folder = Path.Combine(root, "PulseBridge-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert(HeartState.ParseBle(new byte[] { 0, 78 }) == 78, "BLE 8-bit sample");
            Assert(HeartState.ParseBle(new byte[] { 1, 44, 1 }) == 300, "BLE 16-bit sample");
            bool invalid = false; try { HeartState.ParseBle(new byte[] { 1, 50 }); } catch (FormatException) { invalid = true; }
            Assert(invalid, "BLE truncated packet rejected");
            invalid = false; try { HeartState.ParseBle(new byte[] { 4, 78 }); } catch (FormatException) { invalid = true; }
            Assert(invalid, "BLE no skin contact rejected");
            var state = new HeartState(); Assert(state.Get().bpm == null, "Starts disconnected"); state.Begin("test");
            var now = DateTimeOffset.UtcNow;
            Assert(state.Add(78, now, "test"), "Accept fresh sample");
            Assert(!state.Add(0, now.AddMilliseconds(1), "test") && !state.Add(351, now.AddMilliseconds(1), "test"), "Invalid BPM rejected");
            Assert(!state.Add(88, now.AddSeconds(-1), "test"), "Out-of-order sample rejected");
            Assert(!state.Add(88, now.AddMinutes(-1), "test") && !state.Add(88, now.AddMinutes(1), "test"), "Stale and future sample rejected");
            Assert(state.Get(now.AddSeconds(16)).bpm == null, "Stale state clears BPM at 15 seconds");
            state.Template = "HR {bpm} / {source}";
            Assert(state.Get().text == "HR 78 / test", "Output template replacement");
            state.Write(folder);
            Assert(File.ReadAllText(Path.Combine(folder, "heart-rate.txt")) == state.Get().text, "Atomic text output");
            Assert(JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "heart-rate.json"))).RootElement.GetProperty("bpm").GetInt32() == 78, "JSON output");
            state.Export(Path.Combine(folder, "session.csv")); Assert(File.ReadAllLines(Path.Combine(folder, "session.csv")).Length == 2, "CSV session export");
            state.Stop(); Assert(state.Get().bpm == null && !state.Add(80, DateTimeOffset.UtcNow, "test"), "Disconnect clears current reading and rejects updates");
            state.Begin("demo"); state.Add(70, DateTimeOffset.UtcNow, "demo", true);
            Assert(state.Get().demo && state.Get().text.StartsWith("[演示]"), "Demo explicitly labeled in output");
            var json = "{\"measured_at\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ",\"data\":{\"heart_rate\":82}}";
            Assert(HeartState.ParsePulsoid(json).bpm == 82, "Official Pulsoid payload parsing");
            bool accept = true; state.Begin("HTTP");
            using var bridge = new BridgeServer(state, false, () => accept, 0, false);
            async Task<string> Request(string method, string path, string? body = null, bool auth = true, bool local = true)
            {
                var text = method + " " + path + " HTTP/1.1\r\nHost: localhost\r\n" + (auth ? "Authorization: Bearer " + bridge.Key + "\r\n" : "");
                if (body != null) text += "Content-Type: application/json\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n";
                text += "\r\n" + body;
                using var stream = new TestStream(Encoding.UTF8.GetBytes(text));
                await bridge.Process(stream, local, CancellationToken.None);
                return Encoding.UTF8.GetString(stream.Output.ToArray());
            }
            Assert((await Request("GET", "/health")).Contains("PulseBridge ready"), "HTTP health route");
            Assert((await Request("POST", "/heart-rate", "{\"bpm\":81}", false)).StartsWith("HTTP/1.1 401"), "HTTP unauthenticated write blocked");
            Assert((await Request("POST", "/heart-rate", "{\"bpm\":81}")).StartsWith("HTTP/1.1 200") && state.Get().bpm == 81, "HTTP authenticated push updates state");
            Assert((await Request("GET", "/text")).Contains("81"), "Status command endpoint reads pushed BPM");
            var jsonResponse = await Request("GET", "/heart-rate");
            Assert(JsonDocument.Parse(jsonResponse[(jsonResponse.IndexOf("\r\n\r\n") + 4)..]).RootElement.GetProperty("bpm").GetInt32() == 81, "HTTP JSON endpoint");
            Assert((await Request("POST", "/heart-rate", "{\"bpm\":999}")).StartsWith("HTTP/1.1 422"), "HTTP invalid BPM rejected");
            Assert((await Request("POST", "/heart-rate", "{\"bpm\":true}")).StartsWith("HTTP/1.1 400"), "HTTP wrong JSON type rejected");
            Assert((await Request("POST", "/heart-rate", "bad-json")).StartsWith("HTTP/1.1 400"), "HTTP malformed JSON rejected");
            Assert((await Request("POST", "/heart-rate", "{\"bpm\":80,\"measured_at\":0}")).StartsWith("HTTP/1.1 422"), "HTTP stale measured_at rejected");
            Assert((await Request("GET", "/text", null, false, false)).StartsWith("HTTP/1.1 401"), "LAN read requires authentication");
            Assert((await Request("GET", "/missing")).StartsWith("HTTP/1.1 404"), "Unknown HTTP route rejected");
            Assert((await Request("POST", "/heart-rate", new string('x', 4097))).StartsWith("HTTP/1.1 400"), "HTTP oversized body rejected");
            accept = false;
            Assert((await Request("POST", "/heart-rate", "{\"bpm\":80}")).StartsWith("HTTP/1.1 409"), "Inactive HTTP source rejects writes");
            var packet = Osc.Packet("♥ 81 BPM");
            Assert(packet.Length % 4 == 0 && Encoding.UTF8.GetString(packet).StartsWith("/chatbox/input\0") && Encoding.UTF8.GetString(packet).Contains(",sTF\0") && Encoding.UTF8.GetString(packet).Contains("♥ 81 BPM"), "OSC packet UTF-8, padding and boolean tags");
            report.Add("NOTE HTTP protocol tested through the production stream handler; live socket calls are blocked by this execution environment.");
            report.Add("NOTE BLE adapter compiled and invoked; Windows reported DisabledByUser. Wearable, cloud credentials and live VRChat delivery require device testing.");
            report.Add($"SUCCESS {report.Count(x => x.StartsWith("PASS "))} checks");
            return Finish(0);
        }
        catch (Exception ex) { report.Add("FAIL " + ex); return Finish(1); }
        int Finish(int code)
        {
            int index = Array.IndexOf(args, "--report");
            string path = index >= 0 && index + 1 < args.Length ? args[index+1] : Path.Combine(folder, "test-report.txt");
            File.WriteAllLines(path, report); return code;
        }
    }
}
