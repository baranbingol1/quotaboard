// SPDX-License-Identifier: Apache-2.0
using AiLimits.Domain;
using AiLimits.Presentation.WinUI.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace AiLimits.Presentation.WinUI.Converters;

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key =
            value is MeterStatus.Critical or MeterStatus.Exhausted ? "CriticalBrush"
            : value is MeterStatus.Approaching ? "WarningBrush"
            : "HealthyBrush";
        return ThemeService.ResolveThemeResource(key) ?? new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Colors the provider status dot on cards and connection rows.</summary>
public sealed class CardStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string key = value switch
        {
            CardStatusKind.Live => "HealthyBrush",
            CardStatusKind.Stale or CardStatusKind.RateLimited or CardStatusKind.SignInRequired => "WarningBrush",
            CardStatusKind.Error => "CriticalBrush",
            _ => "TextQuietBrush",
        };
        return ThemeService.ResolveThemeResource(key) ?? new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
