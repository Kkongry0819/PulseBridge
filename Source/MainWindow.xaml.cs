using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PulseBridge;
public partial class MainWindow : Window
{
    readonly HeartState state = new();
    readonly string directory;
    readonly FileStream instanceLock;
    Settings settings = new();
    BridgeServer? server;
    CancellationTokenSource? sourceCancel, scanCancel;
    Task? sourceTask, scanTask;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    DateTimeOffset? sessionStart;
    DateTimeOffset lastWrite = DateTimeOffset.MinValue, lastOsc = DateTimeOffset.MinValue;
    bool ready, ticking, closed;
    volatile bool httpMode;
    readonly Dictionary<string, DeviceItem> devices = new();
    public record DeviceItem(string Address, string Label);

    public MainWindow(string directory)
    {
        this.directory = directory; Directory.CreateDirectory(directory);
        try { instanceLock = new FileStream(System.IO.Path.Combine(directory, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("已有一个 PulseBridge 正在使用此数据目录。请先关闭它。"); }
        InitializeComponent();
        try { settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(System.IO.Path.Combine(directory, "settings.json"))) ?? new(); } catch { }
        settings.Template = string.IsNullOrWhiteSpace(settings.Template) ? "♥ {bpm} BPM" : settings.Template[..Math.Min(120, settings.Template.Length)];
        settings.OscPort = settings.OscPort is >= 1 and <= 65535 ? settings.OscPort : 9000;
        // Output to VRChat and LAN access are explicit per-launch choices.
        settings.Osc = false; settings.AllowLan = false;
        state.Template = settings.Template; TemplateBox.Text = settings.Template;
        ProviderBox.SelectedIndex = Array.IndexOf(new[] { "Pulsoid 云端", "蓝牙 BLE 直连", "本地 HTTP 桥接", "演示模式" }, settings.Provider);
        if (ProviderBox.SelectedIndex < 0) ProviderBox.SelectedIndex = 0;
        OscPortBox.Text = settings.OscPort.ToString();
        InitializeVisuals();
        ready = true; UpdateProvider(); StartServer();
        timer.Tick += Tick; timer.Start(); Closed += OnClosed;
        Tick(null, EventArgs.Empty);
    }
    void StartServer()
    {
        server?.Dispose(); server = null;
        try
        {
            server = new BridgeServer(state, settings.AllowLan, () => httpMode);
            Footer.Text = $"●  本地接口 127.0.0.1:{server.Port}    ·    心率趋势仅用于展示，不是心电图。";
        }
        catch (Exception ex) { Footer.Text = "本地接口不可用：" + ex.Message; Log("端口 8976 无法使用，状态命令和 HTTP 接入不可用。请关闭占用程序后重启。"); }
    }
    string Provider => (ProviderBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Pulsoid 云端";
    void Save()
    {
        try { File.WriteAllText(System.IO.Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Log("配置保存失败：" + ex.Message); }
    }
    void Log(string message)
    {
        if (!closed) Dispatcher.BeginInvoke(() => { ConnectionMessage.Text = message; });
    }
    void Provider_Changed(object sender, SelectionChangedEventArgs e) { if (ready) { UpdateProvider(); settings.Provider = Provider; Save(); } }
    void UpdateProvider()
    {
        PulsoidPanel.Visibility = Provider == "Pulsoid 云端" ? Visibility.Visible : Visibility.Collapsed;
        BlePanel.Visibility = Provider == "蓝牙 BLE 直连" ? Visibility.Visible : Visibility.Collapsed;
        HttpPanel.Visibility = Provider == "本地 HTTP 桥接" ? Visibility.Visible : Visibility.Collapsed;
        ProviderHelp.Text = Provider switch
        {
            "Pulsoid 云端" => "Apple Watch、Wear OS，以及由手机转发的小米 / 华为设备。先在配套 App 中确认心率已上传至 Pulsoid。",
            "蓝牙 BLE 直连" => "连接开放标准心率广播的设备（180D / 2A37）。请先开启手表的「心率广播 / 心率共享」。Apple Watch 通常需走云端。",
            "本地 HTTP 桥接" => "让你自己的手机程序或脚本把心率推送到本机。接口本身不会从手表采集数据。",
            _ => "使用模拟心率体验趋势与状态输出。所有显示和导出都会标记为演示数据。"
        };
        if (IsLoaded)
        {
            UiMotion.Reveal(ProviderHelp);
            foreach (var panel in new[] { PulsoidPanel, BlePanel, HttpPanel })
                if (panel.Visibility == Visibility.Visible) UiMotion.Reveal(panel, 45);
        }
    }
    async void Connect_Click(object sender, RoutedEventArgs e) { await Connect(); }
    async Task Connect()
    {
        if (sourceCancel != null) return;
        string provider = Provider, token = TokenBox.Password.Trim();
        if (provider == "Pulsoid 云端" && token.Length == 0) { Log("请先填写 Pulsoid Token；也可选择演示模式预览。"); TokenBox.Focus(); return; }
        if (provider == "蓝牙 BLE 直连" && DeviceList.SelectedItem is not DeviceItem) { Log("请先扫描并选择一个设备。"); return; }
        if (provider == "本地 HTTP 桥接" && server == null) { Log("本地接口尚未启动，请检查端口 8976。"); return; }
        if (scanCancel != null)
        {
            scanCancel.Cancel();
            if (scanTask != null) { try { await scanTask; } catch { } }
        }
        state.Begin(provider); sessionStart = DateTimeOffset.UtcNow;
        sourceCancel = new(); var ct = sourceCancel.Token;
        ProviderBox.IsEnabled = false; ConnectButton.IsEnabled = false; ScanButton.IsEnabled = false; StopButton.IsEnabled = true; LanBox.IsEnabled = false;
        httpMode = provider == "本地 HTTP 桥接"; Log(httpMode ? "接口已就绪，等待手机或脚本推送心率。" : "正在连接…");
        string? address = (DeviceList.SelectedItem as DeviceItem)?.Address;
        sourceTask = Task.Run(async () =>
        {
            try
            {
                switch (provider)
                {
                    case "Pulsoid 云端": await Providers.Pulsoid(state, token, Log, ct); break;
                    case "蓝牙 BLE 直连":
                        int delay = 2;
                        while (!ct.IsCancellationRequested)
                        {
                            try { await Providers.Ble("connect", address, BleLine, ct); }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                            catch (Exception ex) { Log(ex.Message + $" {delay} 秒后重试。"); }
                            await Task.Delay(TimeSpan.FromSeconds(delay), ct); delay = Math.Min(delay * 2, 30);
                        }
                        break;
                    case "演示模式": Log("演示已启动，当前没有连接真实设备。"); await Providers.Demo(state, ct); break;
                    default: await Task.Delay(Timeout.Infinite, ct); break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { Log("数据源停止：" + ex.Message); }
        });
    }
    void BleLine(JsonElement item)
    {
        var type = item.GetProperty("type").GetString();
        if (type == "sample")
        {
            try { var bpm = HeartState.ParseBle(Convert.FromBase64String(item.GetProperty("packet").GetString()!)); state.Add(bpm, DateTimeOffset.UtcNow, "蓝牙 BLE"); }
            catch (FormatException ex) { Log(ex.Message); }
        }
        else if (type == "status" || type == "error") Log(item.GetProperty("text").GetString() ?? "蓝牙状态更新");
        else if (type == "device") Dispatcher.BeginInvoke(() =>
        {
            string address = item.GetProperty("address").GetString()!;
            if (devices.ContainsKey(address)) return;
            var device = new DeviceItem(address, item.GetProperty("name").GetString() + (item.GetProperty("hr").GetBoolean() ? " · 心率广播" : " · 待确认") + $"  {item.GetProperty("rssi").GetInt32()} dBm");
            devices.Add(address, device); DeviceList.Items.Add(device);
        });
    }
    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        scanCancel = new(); var ct = scanCancel.Token;
        ScanButton.IsEnabled = false; ProviderBox.IsEnabled = false; ConnectButton.IsEnabled = false;
        DeviceList.Items.Clear(); devices.Clear(); Log("正在扫描附近设备，请等待约 9 秒…");
        scanTask = Task.Run(() => Providers.Ble("scan", null, BleLine, ct));
        try { await scanTask; if (devices.Count == 0) Log("没有找到具名蓝牙设备。请确认蓝牙已开启、设备在附近并启用心率广播。"); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log(ex.Message); }
        finally
        {
            scanCancel.Dispose(); scanCancel = null;
            if (!closed) { ScanButton.IsEnabled = true; ProviderBox.IsEnabled = true; ConnectButton.IsEnabled = true; }
        }
    }
    async void Stop_Click(object sender, RoutedEventArgs e) { await Stop(); }
    async Task Stop()
    {
        StopButton.IsEnabled = false; httpMode = false;
        sourceCancel?.Cancel(); state.Stop();
        if (sourceTask != null) { try { await sourceTask; } catch { } }
        sourceCancel?.Dispose(); sourceCancel = null; sourceTask = null;
        ProviderBox.IsEnabled = true; ConnectButton.IsEnabled = true; ScanButton.IsEnabled = true; LanBox.IsEnabled = true;
        Log("已断开；会话记录可导出。下次连接将开始新会话。");
        state.Write(directory); Tick(null, EventArgs.Empty);
    }
    async void Tick(object? sender, EventArgs e)
    {
        if (ticking || closed) return; ticking = true;
        try
        {
            var now = DateTimeOffset.UtcNow; var value = state.Get(); var rows = state.History();
            BpmText.Text = value.bpm?.ToString() ?? "--"; PreviewText.Text = value.text;
            UpdateReadingMotion(BpmText.Text); UpdateHeartbeat(value.bpm);
            TopStatus.Text = value.bpm != null ? (value.demo ? "●  演示数据" : "●  实时接收中") : "○  " + value.status;
            TopStatus.Foreground = Brush(value.demo ? "#EDC587" : value.bpm != null ? "#94D4BC" : "#B5A9C4");
            LiveBadge.Text = value.demo ? "DEMO / 模拟数据" : value.bpm != null ? "LIVE / 实时" : "WAITING / 等待";
            SourceLabel.Text = "⌁  " + value.source;
            DataStatus.Text = value.measured_at != null ? $"最后采样 {(now - value.measured_at.Value).TotalSeconds:0} 秒前 · {value.status}" : "等待设备连接，或使用演示模式预览界面";
            AvgText.Text = rows.Length > 0 ? rows.Average(x => x.Bpm).ToString("0") : "--";
            MinText.Text = rows.Length > 0 ? rows.Min(x => x.Bpm).ToString() : "--";
            MaxText.Text = rows.Length > 0 ? rows.Max(x => x.Bpm).ToString() : "--";
            SessionTime.Text = sessionStart == null ? "00:00:00" : (now - sessionStart.Value).ToString(@"hh\:mm\:ss");
            if (now - lastWrite > TimeSpan.FromSeconds(1)) { state.Write(directory); lastWrite = now; }
            DrawChart();
            if (settings.Osc && now - lastOsc >= TimeSpan.FromSeconds(5))
            {
                lastOsc = now; await Osc.Send(string.Concat(value.text.EnumerateRunes().Take(144).Select(x => x.ToString())), settings.OscPort);
            }
        }
        catch (Exception ex) { Log("输出更新失败：" + ex.Message); }
        finally { ticking = false; }
    }
    static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    void Chart_SizeChanged(object sender, SizeChangedEventArgs e) { if (ready) DrawChart(); }
    void DrawChart() => RenderTrend();
    void Template_Changed(object sender, TextChangedEventArgs e)
    {
        if (!ready) return; settings.Template = string.IsNullOrWhiteSpace(TemplateBox.Text) ? "♥ {bpm} BPM" : TemplateBox.Text;
        state.Template = settings.Template; Save(); Tick(null, EventArgs.Empty);
    }
    void Osc_Changed(object sender, RoutedEventArgs e) { if (ready) { settings.Osc = OscBox.IsChecked == true; lastOsc = DateTimeOffset.MinValue; Save(); } }
    void Port_Changed(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(OscPortBox.Text, out int port) && port is >= 1 and <= 65535) { settings.OscPort = port; Save(); }
        else { OscPortBox.Text = settings.OscPort.ToString(); Log("OSC 端口必须在 1–65535 之间。"); }
    }
    void Lan_Changed(object sender, RoutedEventArgs e) { if (ready) { settings.AllowLan = LanBox.IsChecked == true; StartServer(); Save(); } }
    void Copy(string text) { try { Clipboard.SetText(text); Log("已复制到剪贴板。"); } catch (Exception ex) { Log("剪贴板暂不可用：" + ex.Message); } }
    void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        if (server == null) { Log("本地接口未启动，无法生成命令。"); return; }
        Copy("${curl.exe -fsS --max-time 2 http://127.0.0.1:" + server.Port + "/text}");
    }
    void CopyPush_Click(object sender, RoutedEventArgs e)
    {
        if (server == null) return;
        Copy("Invoke-RestMethod -Method Post -Uri http://127.0.0.1:" + server.Port + "/heart-rate -Headers @{ Authorization = 'Bearer " + server.Key + "' } -ContentType 'application/json' -Body '{\"bpm\":78}'");
    }
    void Export_Click(object sender, RoutedEventArgs e)
    {
        if (state.History().Length == 0) { Log("当前会话没有样本，请先接收心率。"); return; }
        var dialog = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = $"heart-rate-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog(this) == true) { try { state.Export(dialog.FileName); Log("会话记录已导出。"); } catch (Exception ex) { Log("导出失败：" + ex.Message); } }
    }
    static void Open(string target) { try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(ex.Message, "PulseBridge"); } }
    void Folder_Click(object sender, RoutedEventArgs e) => Open(directory);
    void Token_Click(object sender, RoutedEventArgs e) => Open("https://pulsoid.net/ui/keys");
    void Overview_Click(object sender, RoutedEventArgs e) { MainScroll.ScrollToTop(); UiMotion.Reveal(Header); }
    void Devices_Click(object sender, RoutedEventArgs e) { DeviceCard.BringIntoView(); ProviderBox.Focus(); UiMotion.Reveal(DeviceCard); }
    void Sync_Click(object sender, RoutedEventArgs e) { SyncCard.BringIntoView(); TemplateBox.Focus(); UiMotion.Reveal(SyncCard); }
    void Guide_Click(object sender, RoutedEventArgs e)
    {
        string guide = "请参阅安装包中的 README.md。";
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "README.md");
        if (File.Exists(path)) guide = File.ReadAllText(path);
        if (server != null)
        {
            var ips = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(x => x.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(x => x.GetIPProperties().UnicastAddresses)
                .Where(x => x.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(x.Address))
                .Select(x => "http://" + x.Address + ":" + server.Port + "/heart-rate");
            guide = "本次运行的 HTTP 推送密钥（重启或切换局域网设置后改变）：\n" + server.Key + "\n\n局域网候选地址（需先勾选允许局域网设备推送）：\n" + string.Join("\n", ips) + "\n\n" + guide;
        }
        var box = new TextBox { Text = guide, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Padding = new Thickness(25), FontSize = 13 };
        new Window { Title = "PulseBridge · 设备接入指南", Icon = Icon, Owner = this, Width = 780, Height = 710, Background = Brush("#17131E"), Content = box, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }
    public async Task Capture(string path)
    {
        ProviderBox.SelectedIndex = 3; await Connect();
        // Seed only explicit demo samples within the freshness window.
        for (int i = 14; i >= 1; i--) state.Add(76 + (int)(Math.Sin(i * .5) * 7), DateTimeOffset.UtcNow.AddSeconds(-i), "演示模式", true);
        await Task.Delay(2500); Tick(null, EventArgs.Empty);
        var root = (FrameworkElement)Content;
        root.Width = 1240; root.Height = 940; root.Measure(new Size(1240, 940)); root.Arrange(new Rect(0, 0, 1240, 940)); root.UpdateLayout();
        DrawChart(); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1240, 940, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
        await Stop();
    }
    public async Task VerifyUi(string report)
    {
        var results = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); results.Add("PASS " + name); }
        Show(); await Task.Delay(750);
        Check(Icon != null && FindResource("BrandMark") is DrawingImage, "Window icon and vector brand resource load");
        ProviderBox.SelectedIndex = 1;
        Check(BlePanel.Visibility == Visibility.Visible && PulsoidPanel.Visibility == Visibility.Collapsed && HttpPanel.Visibility == Visibility.Collapsed, "BLE provider panel visibility");
        ProviderBox.SelectedIndex = 0; TokenBox.Password = ""; await Connect();
        Check(sourceCancel == null && ProviderBox.IsEnabled, "GUI missing-token validation");
        ProviderBox.SelectedIndex = 2; await Connect();
        Check(httpMode && !ProviderBox.IsEnabled && StopButton.IsEnabled, "GUI HTTP connection state");
        var body = "{\"bpm\":88}";
        using (var stream = new SelfTests.TestStream(Encoding.UTF8.GetBytes("POST /heart-rate HTTP/1.1\r\nAuthorization: Bearer " + server!.Key + "\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body)))
            await server.Process(stream, true, CancellationToken.None);
        Tick(null, EventArgs.Empty);
        Check(BpmText.Text == "88" && PreviewText.Text.Contains("88"), "HTTP production handler updates GUI and preview");
        Check(AvgText.Text == "88" && MinText.Text == "88" && MaxText.Text == "88", "GUI statistics update");
        await Stop(); Check(BpmText.Text == "--" && ProviderBox.IsEnabled, "GUI disconnect clears BPM");
        TemplateBox.Text = "HR {bpm}"; ProviderBox.SelectedIndex = 3; await Connect(); await Task.Delay(1100); Tick(null, EventArgs.Empty);
        Check(PreviewText.Text.StartsWith("[演示] HR ") && TopStatus.Text.Contains("演示"), "GUI template and demo labeling");
        Check(trendDot != null && trendDot.Visibility == Visibility.Visible && trendLines.Any(x => x.Points.Count > 0), "Trend draws samples and latest point");
        MotionBox.IsChecked = false;
        Check(!UiMotion.GetEnabled(this) && !((ScaleTransform)HeartIcon.RenderTransform).HasAnimatedProperties && HeartHalo.Opacity == 0, "Reduced motion stops heartbeat and halo immediately");
        Check(JsonSerializer.Deserialize<Settings>(File.ReadAllText(System.IO.Path.Combine(directory, "settings.json")))!.ReducedMotion, "Reduced motion preference persists");
        MotionBox.IsChecked = true; Tick(null, EventArgs.Empty);
        Check(!SystemParameters.ClientAreaAnimation || ((ScaleTransform)HeartIcon.RenderTransform).HasAnimatedProperties, "Heartbeat restarts when animation is enabled");
        WindowState = WindowState.Minimized;
        Check(!((ScaleTransform)HeartIcon.RenderTransform).HasAnimatedProperties, "Minimized window suspends heartbeat");
        WindowState = WindowState.Normal;
        Width = MinWidth; Height = MinHeight; UpdateLayout();
        Check(MainScroll.ScrollableWidth == 0 && ProviderBox.ActualWidth >= 180 && OscPortBox.ActualHeight >= 29, "Minimum window preserves controls without horizontal overflow");
        state.Export(System.IO.Path.Combine(directory, "ui-session.csv"));
        Check(File.ReadAllText(System.IO.Path.Combine(directory, "ui-session.csv")).Contains("true"), "GUI session exports demo provenance");
        await Stop(); TemplateBox.Text = "♥ {bpm} BPM";
        Check(!((ScaleTransform)HeartIcon.RenderTransform).HasAnimatedProperties && trendDot!.Visibility == Visibility.Collapsed, "Disconnect stops heartbeat and live trend marker");
        File.WriteAllLines(report, results.Append($"SUCCESS {results.Count} GUI integration checks"));
    }
    void OnClosed(object? sender, EventArgs e)
    {
        DisposeVisuals();
        closed = true; timer.Stop(); httpMode = false;
        scanCancel?.Cancel(); sourceCancel?.Cancel(); state.Stop();
        try { state.Write(directory); } catch { }
        server?.Dispose(); instanceLock.Dispose();
    }
}

