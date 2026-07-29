using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PhotoLog.Avalonia;

/// Google-Photos-style justified rows. Every cell is exactly the photo's aspect ratio
/// (full image visible, never cropped, never letterboxed); each row shares one height,
/// solved so the row exactly fills the panel width. Last row stays at natural height.
public sealed class JustifiedPanel : Panel
{
    public static readonly StyledProperty<double> RowHeightProperty =
        AvaloniaProperty.Register<JustifiedPanel, double>(nameof(RowHeight), 160.0);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<JustifiedPanel, double>(nameof(Spacing), 4.0);

    /// Photo width ÷ height — bind on the item template root.
    public static readonly AttachedProperty<double> AspectProperty =
        AvaloniaProperty.RegisterAttached<JustifiedPanel, Control, double>("Aspect", 1.0);

    static JustifiedPanel()
    {
        AffectsMeasure<JustifiedPanel>(RowHeightProperty, SpacingProperty);
        // Aspect lands on the template root inside the ContentPresenter — reflow when a thumb decodes.
        AspectProperty.Changed.AddClassHandler<Control>((c, _) =>
            c.FindAncestorOfType<JustifiedPanel>()?.InvalidateMeasure());
    }

    public double RowHeight { get => GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    public static double GetAspect(Control c) => c.GetValue(AspectProperty);
    public static void SetAspect(Control c, double v) => c.SetValue(AspectProperty, v);

    double AspectOf(Control child)
    {
        if (child is ContentPresenter cp)
        {
            if (cp.Child is null) cp.UpdateChild();
            if (cp.Child is { } inner) return Math.Clamp(GetAspect(inner), 0.2, 5.0);
        }
        return Math.Clamp(GetAspect(child), 0.2, 5.0);
    }

    /// Pure row partition: greedy fill with best-break — close the row with or without the
    /// overflowing photo, whichever height lands closer to target. Height of a closed row is
    /// solved so aspect-true widths + gaps exactly equal <paramref name="width"/>.
    public static List<(int Start, int Count, double H)> Rows(
        IReadOnlyList<double> aspects, double width, double targetH, double spacing)
    {
        var rows = new List<(int, int, double)>();
        int start = 0;
        double sumA = 0;
        for (var i = 0; i < aspects.Count; i++)
        {
            var a = aspects[i];
            var n = i - start + 1;
            if (width > 0 && (sumA + a) * targetH + spacing * (n - 1) >= width)
            {
                var hWith = (width - spacing * (n - 1)) / (sumA + a);
                var hWithout = n > 1 ? (width - spacing * (n - 2)) / sumA : double.MaxValue;
                if (n > 1 && Math.Abs(hWithout - targetH) < Math.Abs(hWith - targetH))
                {
                    rows.Add((start, n - 1, hWithout));
                    start = i;
                    sumA = a;
                }
                else
                {
                    rows.Add((start, n, hWith));
                    start = i + 1;
                    sumA = 0;
                }
            }
            else
            {
                sumA += a;
            }
        }
        if (start < aspects.Count)
            rows.Add((start, aspects.Count - start, targetH)); // last row: natural, not stretched
        return rows;
    }

    List<(int Start, int Count, double H)> BuildRows(double width)
    {
        var aspects = new double[Children.Count];
        for (var i = 0; i < Children.Count; i++)
            aspects[i] = AspectOf(Children[i]);
        return Rows(aspects, width, RowHeight, Spacing);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 800;
        double y = 0;
        foreach (var (start, count, h) in BuildRows(width))
        {
            for (var i = start; i < start + count; i++)
                Children[i].Measure(new Size(AspectOf(Children[i]) * h, h));
            y += h + Spacing;
        }
        return new Size(width, Math.Max(0, y - Spacing));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rows = BuildRows(finalSize.Width);
        List<Control>? moved = null;
        double y = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            var (start, count, h) = rows[r];
            double x = 0;
            for (var i = start; i < start + count; i++)
            {
                var w = AspectOf(Children[i]) * h;
                if (i == start + count - 1 && r < rows.Count - 1)
                    w = Math.Max(1, finalSize.Width - x); // absorb fp dust so the right edge is flush
                var rect = new Rect(x, y, w, h);
                Flip(Children[i], rect, ref moved);
                Children[i].Arrange(rect);
                x += w + Spacing;
            }
            y += h + Spacing;
        }
        if (moved is not null)
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var c in moved)
                {
                    var tr = c.GetValue(FlipTransitionsProperty);
                    if (tr is null) c.SetValue(FlipTransitionsProperty, tr = NewFlipTransitions());
                    c.Transitions = tr;
                    c.RenderTransform = TransformOperations.Identity; // glide from inverted offset to rest
                }
            }, DispatcherPriority.Loaded);
        return new Size(finalSize.Width, Math.Max(0, y - Spacing));
    }

    // ---- FLIP reflow: snap the child to where it *was* (invert), then transition to rest ----

    static readonly AttachedProperty<Rect> LastRectProperty =
        AvaloniaProperty.RegisterAttached<JustifiedPanel, Control, Rect>("LastRect");

    static readonly AttachedProperty<Transitions?> FlipTransitionsProperty =
        AvaloniaProperty.RegisterAttached<JustifiedPanel, Control, Transitions?>("FlipTransitions");

    static Transitions NewFlipTransitions() =>
    [
        new TransformOperationsTransition
        {
            Property = RenderTransformProperty,
            Duration = TimeSpan.FromMilliseconds(240),
            Easing = new CubicEaseOut(),
        },
    ];

    static void Flip(Control child, Rect rect, ref List<Control>? moved)
    {
        var prev = child.GetValue(LastRectProperty);
        child.SetValue(LastRectProperty, rect);
        if (prev.Width <= 0) return; // first layout — entrance fade covers it
        // Re-anchor from the current visual spot so interrupted glides stay continuous.
        var cur = child.RenderTransform is { } t ? new Point(t.Value.M31, t.Value.M32) : default;
        var dx = prev.X + cur.X - rect.X;
        var dy = prev.Y + cur.Y - rect.Y;
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return;
        child.Transitions = null; // set the inverted offset without animating the set itself
        var b = new TransformOperations.Builder(1);
        b.AppendTranslate(dx, dy);
        child.RenderTransform = b.Build();
        (moved ??= []).Add(child);
    }
}
