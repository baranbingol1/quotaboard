// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Pricing;
using AiLimits.Presentation.WinUI.ViewModels;
using AiLimits.Presentation.WinUI.Localization;
using Microsoft.Windows.AppLifecycle;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AiLimits.Presentation.WinUI.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly LiveDashboardViewModel? _viewModel;
    private readonly IStartupRegistrationService? _startupRegistration;
    private bool _initializing = true;

    public sealed record PlanCostRow(string Id, string Name, string Account, string Value, string PlanBadge, string Placeholder);

    public sealed record ProviderVisibilityRow(string Id, string Name, string Account, bool Visible, string OnLabel, string OffLabel);

    public sealed record ModelPricingRow(string ServiceId, string Model, string Source, string Key, string Input, string Output, string CacheRead, string CacheWrite);

    private const int ModelPricingPageSize = 6;

    private ModelPricingRow[] _allModelPricingRows = [];
    private int _visibleModelPricingCount = ModelPricingPageSize;

    /// <summary>Bindable view model for x:Bind; null only in design-time construction.</summary>
    public LiveDashboardViewModel? ViewModel => _viewModel;

    public SettingsPage(LiveDashboardViewModel? viewModel = null, IStartupRegistrationService? startupRegistration = null)
    {
        _viewModel = viewModel;
        _startupRegistration = startupRegistration;
        InitializeComponent();
        ThemePreference themePreference = ThemeService.LoadPreference();
        foreach (var palette in Theming.ThemeCatalog.All)
        {
            ThemeNamePicker.Items.Add(new ComboBoxItem { Content = palette.DisplayName, Tag = palette.Id });
        }
        ThemeNamePicker.SelectedIndex = Math.Max(0, Theming.ThemeCatalog.All
            .ToList().FindIndex(palette => string.Equals(palette.Id, themePreference.ThemeId, StringComparison.OrdinalIgnoreCase)));
        ThemePicker.SelectedIndex = themePreference.Mode switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0
        };
        FontSelection fonts = FontPreference.Load();
        SeedFontPicker(ContentFontPicker, Theming.FontCatalog.ContentFonts, fonts.ContentFontId);
        SeedFontPicker(MetricFontPicker, Theming.FontCatalog.MetricFonts, fonts.MetricFontId);
        SystemLanguageItem.Content = LocalizationService.GetLanguageDisplayName("");
        EnglishLanguageItem.Content = LocalizationService.GetLanguageDisplayName(LocalizationService.EnglishLanguageTag);
        TurkishLanguageItem.Content = LocalizationService.GetLanguageDisplayName(LocalizationService.TurkishLanguageTag);
        LanguagePicker.SelectedIndex = LocalizationService.SelectedLanguageTag switch
        {
            LocalizationService.EnglishLanguageTag => 1,
            LocalizationService.TurkishLanguageTag => 2,
            _ => 0
        };
        StartWithWindowsToggle.IsOn = startupRegistration?.IsStartupEnabled ?? false;
        StartWithWindowsToggle.IsEnabled = startupRegistration is not null;
        TrayMonitorToggle.IsOn = TrayMonitorPreference.Enabled;
        DashboardDisplayOptions display = DashboardDisplayPreference.Current;
        ShowRemainingToggle.IsOn = display.ShowUsageAsRemaining;
        AbsoluteResetToggle.IsOn = display.ShowResetTimeAsAbsolute;
        HideZeroToggle.IsOn = display.HideZeroOrNotApplicableMeters;
        QuotaAlertsToggle.IsOn = QuotaAlertPreference.LoadEnabled();
        BlurEmailsToggle.IsOn = EmailPrivacyPreference.Enabled;
        LanguageRestartInfo.Message = LocalizationService.GetString("Language_RestartRequired");
        _initializing = false;
        Loaded += async (_, _) =>
        {
            RebuildPlanRows();
            RebuildProviderVisibilityRows();
            RebuildModelPricingRows();
            // Reads the cached catalog only; opening Settings never fetches.
            if (_viewModel is not null)
            {
                await _viewModel.LoadModelCatalogStatusAsync();
            }
        };
    }

    private void RebuildModelPricingRows()
    {
        var overrides = ModelPricingOverridePreference.LoadAll();
        // Rows the user can price: models observed but unpriced by the catalog,
        // plus any already carrying a manual override (so they stay editable
        // even once the override makes them "priced").
        var seen = new HashSet<(string Service, string Model)>();
        var rows = new List<ModelPricingRow>();
        foreach (ModelUsageRowViewModel model in _viewModel?.AllModelRows ?? [])
        {
            if (string.IsNullOrEmpty(model.ServiceId) || string.IsNullOrEmpty(model.Model))
            {
                continue;
            }
            bool hasOverride = overrides.TryGet(model.ServiceId, model.Model, out ManualModelPrice existing);
            if (model.IsPriced && !hasOverride)
            {
                continue;
            }
            if (!seen.Add((model.ServiceId.ToLowerInvariant(), model.Model.ToLowerInvariant())))
            {
                continue;
            }
            rows.Add(new ModelPricingRow(
                model.ServiceId,
                model.Model,
                model.Source,
                model.ServiceId + "\0" + model.Model,
                hasOverride ? FormatPrice(existing.InputPerMillion) : "",
                hasOverride ? FormatPrice(existing.OutputPerMillion) : "",
                hasOverride && existing.CacheReadPerMillion.HasValue ? FormatPrice(existing.CacheReadPerMillion.Value) : "",
                hasOverride && existing.CacheWritePerMillion.HasValue ? FormatPrice(existing.CacheWritePerMillion.Value) : ""));
        }
        _allModelPricingRows = rows.ToArray();
        _visibleModelPricingCount = ModelPricingPageSize;
        RenderModelPricingPage();
    }

    private void RenderModelPricingPage()
    {
        string query = ModelPricingSearchBox.Text?.Trim() ?? "";
        ModelPricingRow[] matching = query.Length == 0
            ? _allModelPricingRows
            : _allModelPricingRows
                .Where(row => row.Model.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || row.Source.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || row.ServiceId.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        int visible = Math.Min(_visibleModelPricingCount, matching.Length);
        ModelPricingList.ItemsSource = matching.Take(visible).ToArray();
        int remaining = matching.Length - visible;
        ModelPricingShowMoreButton.Visibility = remaining > 0 ? Visibility.Visible : Visibility.Collapsed;
        ModelPricingShowLessButton.Visibility = visible > ModelPricingPageSize ? Visibility.Visible : Visibility.Collapsed;
        ModelPricingShowMoreButton.Content = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            LocalizationService.GetString("Usage_ShowMore"),
            remaining);
        ModelPricingShowLessButton.Content = LocalizationService.GetString("Usage_ShowLess");
        ModelPricingEmptyHint.Visibility = matching.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnModelPricingSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }
        _visibleModelPricingCount = ModelPricingPageSize;
        RenderModelPricingPage();
    }

    private void OnModelPricingShowMore(object sender, RoutedEventArgs e)
    {
        _visibleModelPricingCount += ModelPricingPageSize;
        RenderModelPricingPage();
    }

    private void OnModelPricingShowLess(object sender, RoutedEventArgs e)
    {
        _visibleModelPricingCount = ModelPricingPageSize;
        RenderModelPricingPage();
    }

    private static string FormatPrice(decimal value) =>
        value.ToString("0.######", System.Globalization.CultureInfo.CurrentCulture);

    // The machine locale writes decimal commas while prices are usually pasted
    // with dots; accept both (the old NumberBoxes rejected the "wrong"
    // separator outright, which made the inputs feel broken).
    internal static double? ParsePrice(string? text)
    {
        string trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return null;
        }
        trimmed = trimmed.Replace(',', '.');
        // NumberStyles.Float also parses "Infinity"/huge exponents; both must be
        // rejected here because the callers cast to decimal, which would throw.
        return double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
            && double.IsFinite(value) && value >= 0 && value <= MaxPrice
            ? value
            : null;
    }

    // Generous ceiling for both per-million-token rates and monthly plan costs;
    // anything above this is a typo or paste error, not a price.
    internal const double MaxPrice = 1_000_000;

    private void OnModelPriceBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not ModelPricingRow row)
        {
            return;
        }
        // Gather the row's four boxes from the shared item panel so one changed
        // lane persists the whole model together.
        if (box.Parent is not Panel panel)
        {
            return;
        }
        double?[] lanes = panel.Children.OfType<TextBox>().Select(lane => ParsePrice(lane.Text)).ToArray();
        if (lanes.Length < 4)
        {
            return;
        }
        double? input = lanes[0], output = lanes[1], cacheRead = lanes[2], cacheWrite = lanes[3];
        // An explicit 0 in a cache lane is kept distinct from an empty box:
        // empty cache write falls back to the input rate at quote time, 0 is free.
        ManualModelPrice? price = input is not > 0 || output is not > 0
            ? null
            : new ManualModelPrice(
                (decimal)input.Value,
                (decimal)output.Value,
                cacheRead.HasValue ? (decimal)cacheRead.Value : null,
                cacheWrite.HasValue ? (decimal)cacheWrite.Value : null);
        // Only persist real changes: a focus hop through unchanged boxes must
        // not rewrite the store or recompute the dashboard.
        bool hasCurrent = ModelPricingOverridePreference.LoadAll().TryGet(row.ServiceId, row.Model, out ManualModelPrice current);
        if (price is null ? !hasCurrent : hasCurrent && price == current)
        {
            return;
        }
        ModelPricingOverridePreference.SaveFor(row.ServiceId, row.Model, price);
        _ = _viewModel?.ReapplyFromCacheAsync();
    }

    private void RebuildProviderVisibilityRows()
    {
        var visibility = ProviderVisibilityPreference.Load();
        string onLabel = LocalizationService.GetString("Settings_VisibilityOn");
        string offLabel = LocalizationService.GetString("Settings_VisibilityOff");
        ProviderVisibilityList.ItemsSource = (_viewModel?.Connections ?? [])
            .Where(connection => !string.IsNullOrEmpty(connection.Id))
            .Select(connection => new ProviderVisibilityRow(
                connection.Id,
                connection.Name,
                connection.DisplayAccount,
                visibility.IsVisible(connection.Id),
                onLabel,
                offLabel))
            .ToArray();
    }

    private void OnProviderVisibilityToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.Tag is not string providerId || string.IsNullOrEmpty(providerId))
        {
            return;
        }
        // The initial binding also raises Toggled; only persist real changes.
        if (ProviderVisibilityPreference.Load().IsVisible(providerId) == toggle.IsOn)
        {
            return;
        }
        ProviderVisibilityPreference.SetVisible(providerId, toggle.IsOn);
        _ = _viewModel?.ReapplyFromCacheAsync();
    }

    private void RebuildPlanRows()
    {
        var costs = PlanCostPreference.LoadAll();
        var rows = (_viewModel?.Connections ?? [])
            .Where(connection => connection.IsConnected && !string.IsNullOrEmpty(connection.Id)
                // OpenCode is a local history source, never a subscription of its own.
                && connection.Id != "opencode")
            .Select(connection => new PlanCostRow(
                connection.Id,
                connection.Name,
                connection.DisplayAccount,
                costs.TryGetValue(connection.Id, out var value) ? FormatPrice(value) : "",
                string.IsNullOrEmpty(connection.DetectedPlan)
                    ? ""
                    : connection.DetectedPlan.EndsWith("plan", StringComparison.OrdinalIgnoreCase)
                        ? string.Format(LocalizationService.GetString("Settings_DetectedPlan"), connection.DetectedPlan)
                        : string.Format(LocalizationService.GetString("Settings_DetectedPlanNamed"), connection.DetectedPlan),
                double.IsNaN(connection.SuggestedMonthlyCost)
                    ? LocalizationService.GetString("Settings_UsdPerMonth")
                    // Standard price of the detected plan; used automatically until overridden.
                    : string.Format(LocalizationService.GetString("Settings_DetectedPrice"), connection.SuggestedMonthlyCost)))
            .ToArray();
        PlanCostList.ItemsSource = rows;
        PlanCostEmptyHint.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPlanCostBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.Tag is not string providerId || string.IsNullOrEmpty(providerId))
        {
            return;
        }
        double? value = ParsePrice(box.Text);
        decimal? cost = value is > 0 ? (decimal)value.Value : null;
        // Only persist real changes, and reflect them on the Overview metric
        // immediately instead of waiting for the next full refresh.
        var costs = PlanCostPreference.LoadAll();
        decimal? current = costs.TryGetValue(providerId, out var existing) ? existing : null;
        if (cost == current)
        {
            return;
        }
        PlanCostPreference.SaveFor(providerId, cost);
        _ = _viewModel?.ReapplyFromCacheAsync();
    }

    private static void SeedFontPicker(ComboBox picker, IReadOnlyList<Theming.FontChoice> choices, string? selectedId)
    {
        // Index 0 is "no explicit pick": each theme names the fonts it was
        // designed around, and that is what an untouched install follows.
        picker.Items.Add(new ComboBoxItem
        {
            Content = LocalizationService.GetString("Settings_FontMatchTheme"),
            Tag = null
        });
        int selectedIndex = 0;
        for (int index = 0; index < choices.Count; index++)
        {
            picker.Items.Add(new ComboBoxItem { Content = choices[index].DisplayName, Tag = choices[index].Id });
            if (string.Equals(choices[index].Id, selectedId, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = index + 1;
            }
        }
        picker.SelectedIndex = selectedIndex;
    }

    private void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (XamlRoot?.Content is not FrameworkElement root)
        {
            return;
        }
        string? contentId = (ContentFontPicker.SelectedItem as ComboBoxItem)?.Tag as string;
        string? metricId = (MetricFontPicker.SelectedItem as ComboBoxItem)?.Tag as string;
        FontPreference.Save(new FontSelection(contentId, metricId));
        // Fonts flow through the theme dictionary, so a reapply swaps them live.
        ThemeService.Reapply(root);
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e) => ApplyThemeSelection();

    private void OnThemeNameChanged(object sender, SelectionChangedEventArgs e) => ApplyThemeSelection();

    private void ApplyThemeSelection()
    {
        // Both pickers raise SelectionChanged once while the constructor seeds
        // them; XamlRoot is still null then, so only user-driven changes apply.
        if (XamlRoot?.Content is not FrameworkElement root)
        {
            return;
        }
        ElementTheme mode = ThemePicker.SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        string themeId = (ThemeNamePicker.SelectedItem as ComboBoxItem)?.Tag as string
            ?? Theming.ThemeCatalog.Default.Id;
        ThemeService.Apply(root, new ThemePreference(themeId, mode), persist: true);
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguagePicker.SelectedItem is not ComboBoxItem item)
        {
            return;
        }
        string tag = item.Tag as string ?? string.Empty;
        if (!LocalizationService.SetLanguageOverride(tag))
        {
            return;
        }
        LanguageRestartInfo.IsOpen = true;
        AppInstance.Restart(string.Empty);
        // Restart returns only when Windows could not relaunch the app.
        LanguageRestartInfo.Message = LocalizationService.GetString("Language_RestartFailed");
    }

    private void OnStartWithWindowsToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _startupRegistration is null)
        {
            return;
        }
        bool requested = StartWithWindowsToggle.IsOn;
        if (!_startupRegistration.SetStartupEnabled(requested))
        {
            _initializing = true;
            StartWithWindowsToggle.IsOn = _startupRegistration.IsStartupEnabled;
            _initializing = false;
        }
    }

    private void OnTrayMonitorToggled(object sender, RoutedEventArgs e)
    {
        if (!_initializing) TrayMonitorPreference.Save(TrayMonitorToggle.IsOn);
    }

    private void OnShowRemainingToggled(object sender, RoutedEventArgs e)
    {
        if (!_initializing) DashboardDisplayPreference.Update(showUsageAsRemaining: ShowRemainingToggle.IsOn);
    }

    private void OnAbsoluteResetToggled(object sender, RoutedEventArgs e)
    {
        if (!_initializing) DashboardDisplayPreference.Update(showResetTimeAsAbsolute: AbsoluteResetToggle.IsOn);
    }

    private void OnHideZeroToggled(object sender, RoutedEventArgs e)
    {
        if (!_initializing) DashboardDisplayPreference.Update(hideZeroOrNotApplicableMeters: HideZeroToggle.IsOn);
    }

    private void OnQuotaAlertsToggled(object sender, RoutedEventArgs e)
    {
        if (!_initializing) QuotaAlertPreference.SaveEnabled(QuotaAlertsToggle.IsOn);
    }

    private void OnBlurEmailsToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }
        EmailPrivacyPreference.Save(BlurEmailsToggle.IsOn);
        // The dashboard re-renders itself via the Changed event; this page's
        // own provider lists snapshot the label, so rebuild them here.
        RebuildPlanRows();
        RebuildProviderVisibilityRows();
    }
}
