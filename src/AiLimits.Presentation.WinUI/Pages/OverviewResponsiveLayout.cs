// SPDX-License-Identifier: Apache-2.0
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AiLimits.Presentation.WinUI.Pages;

/// <summary>
/// Equal-width cells for the summary strip at the top of Overview, reflowing
/// into fewer columns as the window narrows.
/// <para>
/// The strip used to be a fixed seven-column <c>Grid</c> whose star weights
/// were hand-tuned (<c>* Auto * Auto 1.35* Auto Auto</c>). That left a wide
/// dead gap next to the plan-value block on a maximised window and crushed
/// every cell at once on a narrow one. Here each cell gets exactly the same
/// width, and once a cell would fall below <see cref="MinCellWidth"/> the
/// strip wraps to another row rather than shrinking further.
/// </para>
/// </summary>
public sealed class StatStrip : Panel
{
    public static readonly DependencyProperty MinCellWidthProperty = DependencyProperty.Register(
        nameof(MinCellWidth),
        typeof(double),
        typeof(StatStrip),
        new PropertyMetadata(230.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing),
        typeof(double),
        typeof(StatStrip),
        new PropertyMetadata(28.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing),
        typeof(double),
        typeof(StatStrip),
        new PropertyMetadata(22.0, OnLayoutPropertyChanged));

    /// <summary>Narrowest a cell may get before the strip drops to fewer columns.</summary>
    public double MinCellWidth
    {
        get => (double)GetValue(MinCellWidthProperty);
        set => SetValue(MinCellWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((StatStrip)sender).InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0)
        {
            return new Size(0, 0);
        }

        int columns = ColumnCount(availableSize.Width);
        double cellWidth = CellWidth(availableSize.Width, columns);
        foreach (UIElement child in Children)
        {
            child.Measure(new Size(cellWidth, double.PositiveInfinity));
        }

        double height = 0;
        for (int row = 0; row * columns < Children.Count; row++)
        {
            height += row == 0 ? 0 : Math.Max(0, RowSpacing);
            height += RowHeight(row, columns);
        }
        double width = double.IsInfinity(availableSize.Width)
            ? cellWidth * columns + Math.Max(0, ColumnSpacing) * (columns - 1)
            : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0)
        {
            return finalSize;
        }

        int columns = ColumnCount(finalSize.Width);
        double cellWidth = CellWidth(finalSize.Width, columns);
        double gap = Math.Max(0, ColumnSpacing);
        double y = 0;
        for (int row = 0; row * columns < Children.Count; row++)
        {
            double rowHeight = RowHeight(row, columns);
            for (int column = 0; column < columns; column++)
            {
                int index = row * columns + column;
                if (index >= Children.Count)
                {
                    break;
                }
                Children[index].Arrange(new Rect(
                    column * (cellWidth + gap),
                    y,
                    cellWidth,
                    rowHeight));
            }
            y += rowHeight + Math.Max(0, RowSpacing);
        }
        return finalSize;
    }

    private int ColumnCount(double availableWidth)
    {
        if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            return Children.Count;
        }
        double gap = Math.Max(0, ColumnSpacing);
        double minimum = Math.Max(1, MinCellWidth);
        int fits = Math.Clamp((int)Math.Floor((availableWidth + gap) / (minimum + gap)), 1, Children.Count);

        // Balance the rows. Four cells in a three-wide strip should read as
        // 2x2, not 3+1 — a lone trailing cell leaves a conspicuous dead gap
        // across the rest of its row.
        int rows = (int)Math.Ceiling((double)Children.Count / fits);
        return (int)Math.Ceiling((double)Children.Count / rows);
    }

    private double CellWidth(double availableWidth, int columns)
    {
        if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            return Math.Max(1, MinCellWidth);
        }
        double gap = Math.Max(0, ColumnSpacing);
        return Math.Max(1, (availableWidth - gap * (columns - 1)) / columns);
    }

    private double RowHeight(int row, int columns)
    {
        double height = 0;
        for (int column = 0; column < columns; column++)
        {
            int index = row * columns + column;
            if (index >= Children.Count)
            {
                break;
            }
            height = Math.Max(height, Children[index].DesiredSize.Height);
        }
        return height;
    }
}
