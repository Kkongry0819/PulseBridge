using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PulseBridge;

// Shared, inherited switch keeps decorative motion consistent across the window.
public static class UiMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(UiMotion), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.RegisterAttached(
        "Interactive", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, InteractiveChanged));
    public static bool GetInteractive(DependencyObject target) => (bool)target.GetValue(InteractiveProperty);
    public static void SetInteractive(DependencyObject target, bool value) => target.SetValue(InteractiveProperty, value);
    static void InteractiveChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element) return;
        if ((bool)e.NewValue) { element.MouseEnter += Enter; element.MouseLeave += Leave; }
        else { element.MouseEnter -= Enter; element.MouseLeave -= Leave; }
    }
    static TranslateTransform Translation(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform translate) return translate;
        if (element.RenderTransform is TransformGroup group && group.Children.OfType<TranslateTransform>().FirstOrDefault() is { } existing) return existing;
        var result = new TranslateTransform();
        var transforms = new TransformGroup();
        if (element.RenderTransform != Transform.Identity) transforms.Children.Add(element.RenderTransform.Clone());
        transforms.Children.Add(result); element.RenderTransform = transforms;
        return result;
    }
    static void Enter(object sender, MouseEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (GetEnabled(element) && SystemParameters.ClientAreaAnimation)
            Translation(element).BeginAnimation(TranslateTransform.YProperty, Ease(-2, 160));
    }
    static void Leave(object sender, MouseEventArgs e)
    {
        var element = (FrameworkElement)sender;
        var transform = Translation(element);
        if (GetEnabled(element) && SystemParameters.ClientAreaAnimation) transform.BeginAnimation(TranslateTransform.YProperty, Ease(0, 180));
        else { transform.BeginAnimation(TranslateTransform.YProperty, null); transform.Y = 0; }
    }
    public static DoubleAnimation Ease(double to, double milliseconds) => new(to, TimeSpan.FromMilliseconds(milliseconds))
    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
    public static void Reveal(FrameworkElement element, int delay = 0)
    {
        if (!GetEnabled(element) || !SystemParameters.ClientAreaAnimation) return;
        var slide = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        slide.KeyFrames.Add(new LinearDoubleKeyFrame(14, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        slide.KeyFrames.Add(new LinearDoubleKeyFrame(14, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
        slide.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + 430)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        Translation(element).BeginAnimation(TranslateTransform.YProperty, slide);
        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + 380))));
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }
    public static void Reset(DependencyObject root)
    {
        if (root is FrameworkElement element)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            if (element.RenderTransform is TranslateTransform t) t.BeginAnimation(TranslateTransform.YProperty, null);
            if (element.RenderTransform is TransformGroup g)
                foreach (var transform in g.Children.OfType<TranslateTransform>()) transform.BeginAnimation(TranslateTransform.YProperty, null);
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) Reset(VisualTreeHelper.GetChild(root, i));
    }
}
