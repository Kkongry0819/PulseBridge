using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PulseBridge;
public record Reading(int Bpm, DateTimeOffset Time, string Source, bool Demo);
public record Snapshot(int? bpm, string text, string status, string source, bool demo, DateTimeOffset? measured_at, DateTimeOffset updated_at);
public class Settings
{
    public bool ReducedMotion { get; set; }
    public string Template { get; set; } = "♥ {bpm} BPM";
    public string Provider { get; set; } = "Pulsoid 云端";
    public bool Osc { get; set; }
    public int OscPort { get; set; } = 9000;
    public bool AllowLan { get; set; }
}
public sealed class HeartState
{
    readonly object gate = new();
    readonly List<Reading> history = new();
    Reading? latest;
    public string Template { get; set; } = "♥ {bpm} BPM";
    public bool Active { get; private set; }
    public string Source { get; private set; } = "未连接";
    public const int StaleSeconds = 15;
    public void Begin(string source) { lock (gate) { latest = null; history.Clear(); Active = true; Source = source; } }
    public void Stop() { lock (gate) { Active = false; latest = null; } }
    public bool Add(int bpm, DateTimeOffset time, string source, bool demo = false)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (!Active || bpm is < 1 or > 350 || time > now.AddSeconds(5) || time < now.AddSeconds(-StaleSeconds) || latest != null && time <= latest.Time) return false;
            latest = new(bpm, time, source, demo); Source = source; history.Add(latest);
            if (history.Count > 100000) history.RemoveAt(0);
            return true;
        }
    }
    public Snapshot Get(DateTimeOffset? clock = null)
    {
        lock (gate)
        {
            var now = clock ?? DateTimeOffset.UtcNow;
            var live = Active && latest != null && (now - latest.Time).TotalSeconds <= StaleSeconds;
            var status = !Active ? "未连接" : latest == null ? "等待心率" : live ? (latest.Demo ? "演示数据" : "实时数据") : "数据已过期";
            var text = live ? Template.Replace("{bpm}", latest!.Bpm.ToString()).Replace("{source}", Source) : "♥ -- BPM · " + status;
            if (live && latest!.Demo) text = "[演示] " + text;
            return new(live ? latest!.Bpm : null, text, status, Source, latest?.Demo ?? false, latest?.Time, now);
        }
    }
    public Reading[] History() { lock (gate) return history.ToArray(); }
    public void Write(string dir)
    {
        var snapshot = Get();
        Atomic(Path.Combine(dir, "heart-rate.json"), JsonSerializer.Serialize(snapshot));
        Atomic(Path.Combine(dir, "heart-rate.txt"), snapshot.text);
    }
    static void Atomic(string path, string data)
    {
        File.WriteAllText(path + ".tmp", data, new UTF8Encoding(false)); File.Move(path + ".tmp", path, true);
    }
    public static int ParseBle(byte[] data)
    {
        if (data.Length < 2) throw new FormatException("心率数据包太短");
        if ((data[0] & 4) != 0 && (data[0] & 2) == 0) throw new FormatException("传感器未接触皮肤");
        if ((data[0] & 1) == 0) return data[1];
        if (data.Length < 3) throw new FormatException("16 位心率数据包不完整");
        return data[1] | data[2] << 8;
    }
    public static (int bpm, DateTimeOffset time) ParsePulsoid(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return (doc.RootElement.GetProperty("data").GetProperty("heart_rate").GetInt32(), DateTimeOffset.FromUnixTimeMilliseconds(doc.RootElement.GetProperty("measured_at").GetInt64()));
    }
    public void Export(string path)
    {
        var csv = new StringBuilder("timestamp_utc,bpm,source,demo\r\n");
        foreach (var v in History()) csv.AppendLine($"{v.Time:O},{v.Bpm},\"{v.Source.Replace("\"", "\"\"")}\",{v.Demo.ToString().ToLowerInvariant()}");
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
    }
}
public static class Osc
{
    static byte[] Text(string text)
    {
        var data = Encoding.UTF8.GetBytes(text + "\0"); Array.Resize(ref data, (data.Length + 3) / 4 * 4); return data;
    }
    public static byte[] Packet(string text) => Text("/chatbox/input").Concat(Text(",sTF")).Concat(Text(text)).ToArray();
    public static async Task Send(string text, int port)
    {
        using var udp = new UdpClient(); await udp.SendAsync(Packet(text), new IPEndPoint(IPAddress.Loopback, port));
    }
}
