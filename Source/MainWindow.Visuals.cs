using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace PulseBridge;

public partial class MainWindow
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(this).Handle;
        int dark = 1, caption = 0x001F1318, text = 0x00F8F0F6, corners = 2;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        DwmSetWindowAttribute(handle, 33, ref corners, sizeof(int));
    }
    int animatedBpm;
    string previousBpm = "--";
    readonly List<Polyline> trendLines = new();
    readonly List<Polygon> trendAreas = new();
    readonly List<TextBlock> trendLabels = new();
    readonly List<Line> trendGrid = new();
    Ellipse? trendDot;
    TextBlock? trendEmpty;
    bool MotionAllowed => UiMotion.GetEnabled(this) && IsVisible && WindowState != WindowState.Minimized;

    void InitializeVisuals()
    {
        MotionBox.IsChecked = !settings.ReducedMotion;
        ApplyMotion();
        Loaded += VisualsLoaded;
        StateChanged += VisualVisibilityChanged;
        IsVisibleChanged += (_, _) => UpdateHeartbeat(state.Get().bpm);
        SystemParameters.StaticPropertyChanged += SystemAnimationChanged;
    }
    void VisualsLoaded(object sender, RoutedEventArgs e)
    {
        // Fit the initial window to the current desktop work area, including high DPI displays.
        Width = Math.Max(MinWidth, Math.Min(Width, SystemParameters.WorkArea.Width - 32));
        Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height - 32));
        Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - ActualWidth) / 2;
        Top = SystemParameters.WorkArea.Top + Math.Max(0, (SystemParameters.WorkArea.Height - ActualHeight) / 2);
        var cards = new FrameworkElement[] { Header, HeartCard, StatsRow, TrendCard, DeviceCard, SyncCard };
        for (int i = 0; i < cards.Length; i++) UiMotion.Reveal(cards[i], i * 55);
        UpdateHeartbeat(state.Get().bpm);
    }
    void VisualVisibilityChanged(object? sender, EventArgs e) => UpdateHeartbeat(state.Get().bpm);
    void SystemAnimationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Dispatcher.BeginInvoke(ApplyMotion);
    }
    void Motion_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        settings.ReducedMotion = MotionBox.IsChecked != true;
        ApplyMotion(); Save();
    }
    void ApplyMotion()
    {
        UiMotion.SetEnabled(this, !settings.ReducedMotion && SystemParameters.ClientAreaAnimation);
        if (!UiMotion.GetEnabled(this)) UiMotion.Reset(AppRoot);
        UpdateHeartbeat(state.Get().bpm);
    }
    void UpdateHeartbeat(int? bpm)
    {
        int desired = MotionAllowed ? bpm ?? 0 : 0;
        if (desired == animatedBpm) return;
        animatedBpm = desired;
        var heart = (ScaleTransform)HeartIcon.RenderTransform;
        var halo = (ScaleTransform)HeartHalo.RenderTransform;
        heart.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        heart.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        halo.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        halo.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        HeartHalo.BeginAnimation(OpacityProperty, null);
        if (desired == 0) return;
        var period = TimeSpan.FromSeconds(60.0 / desired);
        DoubleAnimationUsingKeyFrames Beat()
        {
            var animation = new DoubleAnimationUsingKeyFrames { Duration = period, RepeatBehavior = RepeatBehavior.Forever };
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0)));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.13, KeyTime.FromPercent(.13), new CubicEase { EasingMode = EasingMode.EaseOut }));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(.28)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(1.05, KeyTime.FromPercent(.38)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(.55)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(1)));
            return animation;
        }
        heart.BeginAnimation(ScaleTransform.ScaleXProperty, Beat());
        heart.BeginAnimation(ScaleTransform.ScaleYProperty, Beat());
        var ring = new DoubleAnimation(.8, 1.6, period) { RepeatBehavior = RepeatBehavior.Forever };
        halo.BeginAnimation(ScaleTransform.ScaleXProperty, ring);
        halo.BeginAnimation(ScaleTransform.ScaleYProperty, ring.Clone());
        HeartHalo.BeginAnimation(OpacityProperty, new DoubleAnimation(.34, 0, period) { RepeatBehavior = RepeatBehavior.Forever });
    }
    void UpdateReadingMotion(string text)
    {
        if (previousBpm == text) return;
        previousBpm = text;
        if (MotionAllowed) ((TranslateTransform)BpmText.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(230)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }
    void EnsureTrend()
    {
        if (trendDot != null) return;
        for (int i = 0; i < 4; i++)
        {
            var grid = new Line { Stroke = Brush("#3D2C47"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 5 } };
            var label = new TextBlock { FontSize = 9, Foreground = Brush("#A58BB3") };
            trendGrid.Add(grid); trendLabels.Add(label); Chart.Children.Add(grid); Chart.Children.Add(label);
        }
        trendDot = new Ellipse { Width = 7, Height = 7, Fill = Brush("#FFE1EC"), Stroke = Brush("#F58DB5"), StrokeThickness = 1.5 };
        trendEmpty = new TextBlock { Text = "连接设备后，心率趋势将在这里展开", Foreground = Brush("#AB92B6"), FontSize = 10 };
        Panel.SetZIndex(trendDot, 3); Panel.SetZIndex(trendEmpty, 4);
        Chart.Children.Add(trendDot); Chart.Children.Add(trendEmpty);
    }
    void RenderTrend()
    {
        double width = Chart.ActualWidth, height = Chart.ActualHeight;
        if (width < 40 || height < 30) return;
        EnsureTrend();
        var now = DateTimeOffset.UtcNow;
        var data = state.History().Where(x => (now - x.Time).TotalSeconds is >= 0 and <= 60).ToArray();
        double min = data.Length == 0 ? 50 : Math.Max(0, Math.Floor((data.Min(x => x.Bpm) - 10) / 10.0) * 10);
        double max = data.Length == 0 ? 110 : Math.Ceiling((data.Max(x => x.Bpm) + 10) / 10.0) * 10;
        double baseline = height - 8;
        for (int i = 0; i < 4; i++)
        {
            double y = 7 + (height - 20) * i / 3;
            var grid = trendGrid[i]; grid.X1 = 30; grid.X2 = width - 4; grid.Y1 = grid.Y2 = y;
            trendLabels[i].Text = (max - (max - min) * i / 3).ToString("0");
            Canvas.SetTop(trendLabels[i], y - 6);
        }
        var segments = new List<PointCollection>();
        DateTimeOffset? previous = null;
        foreach (var reading in data)
        {
            if (previous == null || reading.Time - previous.Value > TimeSpan.FromSeconds(HeartState.StaleSeconds)) segments.Add(new());
            segments[^1].Add(new Point(30 + (width - 37) * (1 - (now - reading.Time).TotalSeconds / 60),
                7 + (height - 20) * (1 - (reading.Bpm - min) / (max - min))));
            previous = reading.Time;
        }
        while (trendLines.Count < segments.Count)
        {
            var line = new Polyline { Stroke = Brush("#F79EC2"), StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            var area = new Polygon { Fill = new LinearGradientBrush(Color.FromArgb(62, 244, 126, 171), Color.FromArgb(0, 244, 126, 171), 90) };
            Panel.SetZIndex(line, 2); Panel.SetZIndex(area, 1);
            trendLines.Add(line); trendAreas.Add(area); Chart.Children.Add(area); Chart.Children.Add(line);
        }
        for (int i = 0; i < trendLines.Count; i++)
        {
            bool visible = i < segments.Count;
            trendLines[i].Visibility = trendAreas[i].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) continue;
            var points = segments[i]; trendLines[i].Points = points;
            var fill = points.Clone(); fill.Insert(0, new Point(points[0].X, baseline)); fill.Add(new Point(points[^1].X, baseline)); trendAreas[i].Points = fill;
        }
        bool fresh = state.Get().bpm != null;
        trendDot!.Visibility = fresh && segments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (fresh && segments.Count > 0)
        {
            var end = segments[^1][^1]; MoveDot(Canvas.LeftProperty, end.X - 3.5); MoveDot(Canvas.TopProperty, end.Y - 3.5);
        }
        trendEmpty!.Visibility = data.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetTop(trendEmpty, height / 2 - 8); Canvas.SetLeft(trendEmpty, Math.Max(36, (width - 210) / 2));
    }
    void MoveDot(DependencyProperty property, double target)
    {
        double from = (double)trendDot!.GetValue(property);
        trendDot.BeginAnimation(property, null); trendDot.SetValue(property, target);
        if (MotionAllowed && !double.IsNaN(from)) trendDot.BeginAnimation(property,
            new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(360)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }
    void DisposeVisuals()
    {
        SystemParameters.StaticPropertyChanged -= SystemAnimationChanged;
        UiMotion.SetEnabled(this, false); UpdateHeartbeat(null); UiMotion.Reset(AppRoot);
    }
}
