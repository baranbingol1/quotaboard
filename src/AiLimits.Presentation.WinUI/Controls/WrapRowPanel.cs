// SPDX-License-Identifier: Apache-2.0
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AiLimits.Presentation.WinUI.Controls;

/// <summary>
/// Left-to-right wrapping panel where each row takes the height of its
/// tallest child and every child is stretched to that row height, so card
/// outlines in a row stay flush instead of leaving ragged gaps under short
/// cards. Unlike ItemsWrapGrid, one card expanding does not clip: the grid's
/// uniform cell size comes from the first item only, so a non-first card
/// that grows taller gets cut off.
/// </summary>
public sealed class WrapRowPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double rowWidth = 0,
            rowHeight = 0,
            totalWidth = 0,
            totalHeight = 0;
        foreach (UIElement child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            Size desired = child.DesiredSize;
            if (rowWidth > 0 && rowWidth + desired.Width > availableSize.Width)
            {
                totalWidth = Math.Max(totalWidth, rowWidth);
                totalHeight += rowHeight;
                rowWidth = 0;
                rowHeight = 0;
            }
            rowWidth += desired.Width;
            rowHeight = Math.Max(rowHeight, desired.Height);
        }
        totalWidth = Math.Max(totalWidth, rowWidth);
        totalHeight += rowHeight;
        return new Size(
            double.IsInfinity(availableSize.Width) ? totalWidth : Math.Min(totalWidth, availableSize.Width),
            totalHeight
        );
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // First pass: group children into rows so each row's height is known
        // before any child is arranged.
        List<(int Start, int Count, double Height)> rows = [];
        double rowWidth = 0,
            rowHeight = 0;
        int rowStart = 0;
        for (int index = 0; index < Children.Count; index++)
        {
            Size desired = Children[index].DesiredSize;
            if (rowWidth > 0 && rowWidth + desired.Width > finalSize.Width)
            {
                rows.Add((rowStart, index - rowStart, rowHeight));
                rowStart = index;
                rowWidth = 0;
                rowHeight = 0;
            }
            rowWidth += desired.Width;
            rowHeight = Math.Max(rowHeight, desired.Height);
        }
        if (rowStart < Children.Count)
        {
            rows.Add((rowStart, Children.Count - rowStart, rowHeight));
        }

        double y = 0;
        foreach ((int start, int count, double height) in rows)
        {
            double x = 0;
            for (int index = start; index < start + count; index++)
            {
                UIElement child = Children[index];
                child.Arrange(new Rect(x, y, child.DesiredSize.Width, height));
                x += child.DesiredSize.Width;
            }
            y += height;
        }
        return finalSize;
    }
}
