// SPDX-License-Identifier: Apache-2.0
using AiLimits.Presentation.WinUI;
using AiLimits.Presentation.WinUI.Shell;
using AiLimits.Presentation.WinUI.Theming;
using AiLimits.Presentation.WinUI.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace AiLimits.App;

public sealed partial class MainWindow : Window
{
    public MainWindow(LiveDashboardViewModel viewModel, IStartupRegistrationService startupRegistration)
    {
        InitializeComponent();
        ThemeService.Applied += StyleTitleBar;
        // Keep custom chrome/tray in sync when the OS theme flips in System mode.
        Root.ActualThemeChanged += (_, _) => ThemeService.NotifySystemThemeChanged();
        Closed += (_, _) => ThemeService.Applied -= StyleTitleBar;
        ThemeService.Apply(Root, ThemeService.LoadPreference(), persist: false);
        Root.Children.Add(new ShellPage(viewModel, startupRegistration));
        Title = "QuotaBoard";
        try
        {
            SystemBackdrop = new MicaBackdrop();
            AppWindow.Resize(new SizeInt32(1280, 840));
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        }
        catch { }
        StyleTitleBar(ThemeService.Current);
    }

    // The stock caption bar stays light regardless of the in-app theme; paint
    // it to match the page background so the top strip doesn't glare.
    private void StyleTitleBar(ElementTheme theme)
    {
        try
        {
            // Colors come from the active theme palette so the caption bar matches
            // whichever OpenCode theme + mode is selected.
            ResolvedThemeColors colors = ThemeService.CurrentColors;
            bool dark = colors.IsDarkBackground;
            Color background = colors.TitleBarBackground;
            Color foreground = colors.TitleBarForeground;
            Color inactiveForeground = colors.TitleBarInactiveForeground;
            Color hover = colors.TitleBarHover;
            AppWindowTitleBar titleBar = AppWindow.TitleBar;
            titleBar.BackgroundColor = background;
            titleBar.InactiveBackgroundColor = background;
            titleBar.ForegroundColor = foreground;
            titleBar.InactiveForegroundColor = inactiveForeground;
            titleBar.ButtonBackgroundColor = background;
            titleBar.ButtonInactiveBackgroundColor = background;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = inactiveForeground;
            titleBar.ButtonHoverBackgroundColor = hover;
            titleBar.ButtonHoverForegroundColor = foreground;
            // SetIcon resolves relative to the working directory, so anchor to
            // the install dir; the charcoal monogram vanishes on a dark caption
            // bar, so swap to the porcelain variant there.
            AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", dark ? "QuotaBoard-Light.ico" : "QuotaBoard.ico"));
        }
        catch { }
    }
}
