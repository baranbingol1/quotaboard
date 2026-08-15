// SPDX-License-Identifier: Apache-2.0
using AiLimits.Presentation.WinUI.Localization;
using AiLimits.Presentation.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AiLimits.Presentation.WinUI.Controls;

public sealed partial class ProviderCard : UserControl
{
    private bool _expanded;

    public ProviderCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Collapse();
    }

    private void Collapse()
    {
        _expanded = false;
        if (DataContext is ProviderCardViewModel card)
        {
            Meters.ItemsSource = card.VisibleMeters;
            MoreLabelText.Text = card.MoreLabel;
            MoreButton.Visibility = card.AdditionalCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProviderCardViewModel card || card.AdditionalCount == 0)
        {
            return;
        }
        _expanded = !_expanded;
        Meters.ItemsSource = _expanded ? card.DisplayMeters : card.VisibleMeters;
        MoreLabelText.Text = _expanded
            ? LocalizationService.GetString("ProviderCard_ShowFewerLimits")
            : card.MoreLabel;
    }
}
