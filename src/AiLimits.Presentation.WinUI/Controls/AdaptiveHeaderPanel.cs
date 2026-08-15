// SPDX-License-Identifier: Apache-2.0
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AiLimits.Presentation.WinUI.Controls;

/// <summary>
/// Two-slot header: prose on the left, controls on the right. Once the window
/// is narrow enough that the prose would be squeezed under
/// <see cref="MinContentWidth"/>, the controls drop to their own line instead
/// of the two colliding.
/// <para>
/// A plain <c>Grid</c> with <c>*</c> and <c>Auto</c> columns cannot do this:
/// the star column keeps shrinking past the point where its text is readable,
/// so a non-wrapping description ends up sliced off mid-word right where the
/// buttons begin. Children are expected in source order: content, then
/// actions. Extra children are laid out with the content.
/// </para>
/// </summary>
public sealed class AdaptiveHeaderPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing),
        typeof(double),
        typeof(AdaptiveHeaderPanel),
        new PropertyMetadata(16.0, OnLayoutPropertyChanged)
    );

    public static readonly DependencyProperty MinContentWidthProperty = DependencyProperty.Register(
        nameof(MinContentWidth),
        typeof(double),
        typeof(AdaptiveHeaderPanel),
        new PropertyMetadata(280.0, OnLayoutPropertyChanged)
    );

    /// <summary>Gap between the two slots, horizontally when side by side and vertically when stacked.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Width below which the content slot stops sharing a line with the actions.</summary>
    public double MinContentWidth
    {
        get => (double)GetValue(MinContentWidthProperty);
        set => SetValue(MinContentWidthProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((AdaptiveHeaderPanel)sender).InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0)
        {
            return new Size(0, 0);
        }
        if (Children.Count == 1)
        {
            Children[0].Measure(availableSize);
            return Children[0].DesiredSize;
        }

        UIElement content = Children[0];
        UIElement actions = Children[1];
        double gap = Math.Max(0, Spacing);

        actions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double actionsWidth = actions.DesiredSize.Width;
        double contentWidth = ContentWidth(availableSize.Width, actionsWidth, gap);

        if (contentWidth > 0)
        {
            content.Measure(new Size(contentWidth, double.PositiveInfinity));
            return new Size(
                double.IsInfinity(availableSize.Width)
                    ? content.DesiredSize.Width + gap + actionsWidth
                    : availableSize.Width,
                Math.Max(content.DesiredSize.Height, actions.DesiredSize.Height)
            );
        }

        // Stacked: both slots get the full width.
        content.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        actions.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return new Size(
            Math.Max(content.DesiredSize.Width, actions.DesiredSize.Width),
            content.DesiredSize.Height + gap + actions.DesiredSize.Height
        );
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0)
        {
            return finalSize;
        }
        if (Children.Count == 1)
        {
            Children[0].Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
            return finalSize;
        }

        UIElement content = Children[0];
        UIElement actions = Children[1];
        double gap = Math.Max(0, Spacing);
        double actionsWidth = actions.DesiredSize.Width;
        double contentWidth = ContentWidth(finalSize.Width, actionsWidth, gap);

        if (contentWidth > 0)
        {
            content.Arrange(new Rect(0, 0, contentWidth, finalSize.Height));
            // Right-aligned, and never narrower than what it asked for.
            actions.Arrange(
                new Rect(
                    Math.Max(contentWidth + gap, finalSize.Width - actionsWidth),
                    0,
                    actionsWidth,
                    finalSize.Height
                )
            );
            return finalSize;
        }

        double contentHeight = content.DesiredSize.Height;
        content.Arrange(new Rect(0, 0, finalSize.Width, contentHeight));
        actions.Arrange(
            new Rect(0, contentHeight + gap, finalSize.Width, Math.Max(0, finalSize.Height - contentHeight - gap))
        );
        return finalSize;
    }

    /// <summary>
    /// Width for the content slot when the two fit on one line, or zero when
    /// they must stack.
    /// </summary>
    private double ContentWidth(double availableWidth, double actionsWidth, double gap)
    {
        if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth))
        {
            return double.PositiveInfinity;
        }
        double remaining = availableWidth - actionsWidth - gap;
        return remaining >= Math.Max(0, MinContentWidth) ? remaining : 0;
    }
}
