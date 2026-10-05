using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace IPhoneMirror.App.Windows.MappingWizard;

internal static class WizardPageTransition
{
    internal static BitmapSource? Snapshot(FrameworkElement page)
    {
        if (!SystemParameters.ClientAreaAnimation || page.ActualWidth < 1 || page.ActualHeight < 1) return null;
        // A bounded, short-lived visual snapshot lets the old step fade without
        // retaining its input handlers or creating another preview control.
        var scale = Math.Min(1, 1000 / Math.Max(page.ActualWidth, page.ActualHeight));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(page), null, new Rect(0, 0, page.ActualWidth * scale, page.ActualHeight * scale));
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(page.ActualWidth * scale)),
            Math.Max(1, (int)Math.Ceiling(page.ActualHeight * scale)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    internal static void Play(FrameworkElement incoming, Image outgoing, BitmapSource? snapshot, int direction)
    {
        outgoing.BeginAnimation(UIElement.OpacityProperty, null);
        outgoing.Source = snapshot;
        if (snapshot is null) return;
        var duration = incoming.TryFindResource("FastAnimationDuration") is Duration value ? value : new Duration(TimeSpan.FromMilliseconds(160));
        var oldTransform = new TranslateTransform(); outgoing.RenderTransform = oldTransform;
        oldTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -direction * 12, duration) { FillBehavior = FillBehavior.Stop });
        var fade = new DoubleAnimation(1, 0, duration) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => { if (ReferenceEquals(outgoing.Source, snapshot)) outgoing.Source = null; };
        outgoing.BeginAnimation(UIElement.OpacityProperty, fade);
        var transform = new TranslateTransform(); incoming.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(direction * 12, 0, duration) { FillBehavior = FillBehavior.Stop });
        incoming.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { FillBehavior = FillBehavior.Stop });
    }
}
