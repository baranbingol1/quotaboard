// SPDX-License-Identifier: Apache-2.0
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using AiLimits.Application.Tray;
using AiLimits.Domain;
using AiLimits.Presentation.WinUI;
using AiLimits.Presentation.WinUI.Localization;
using AiLimits.Presentation.WinUI.ViewModels;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AiLimits.App;

internal sealed class TrayIconService : IDisposable
{
    private readonly Window _window;
    private readonly LiveDashboardViewModel _dashboard;
    private readonly Action _noteUserInteraction;
    private readonly Func<Task> _requestQuit;
    private TaskbarIcon? _icon;
    private nint _iconHandle;
    private MeterStatus? _displayedStatus;
    private bool _quitting;
    private bool _hidden;

    public TrayIconService(
        Window window,
        LiveDashboardViewModel dashboard,
        Action noteUserInteraction,
        Func<Task> requestQuit
    )
    {
        _window = window;
        _dashboard = dashboard;
        _noteUserInteraction = noteUserInteraction;
        _requestQuit = requestQuit;
        _window.AppWindow.Closing += OnWindowClosing;
        _window.Activated += OnWindowActivated;
        _dashboard.Providers.CollectionChanged += OnDashboardChanged;
        _dashboard.PropertyChanged += OnDashboardPropertyChanged;
        TrayMonitorPreference.Changed += OnPreferenceChanged;
        if (TrayMonitorPreference.Enabled)
        {
            EnsureIconCreated();
        }
    }

    public bool StartMinimized()
    {
        if (!TrayMonitorPreference.Enabled)
        {
            return false;
        }
        if (!EnsureIconCreated())
        {
            return false;
        }
        try
        {
            H.NotifyIcon.WindowExtensions.Hide(_window);
            _hidden = !_window.AppWindow.IsVisible;
            return _hidden;
        }
        catch
        {
            _hidden = false;
            return false;
        }
    }

    private void CreateIcon()
    {
        if (_icon is not null)
        {
            TryUpdateIcon();
            return;
        }

        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem(L("Tray_Open"), OpenWindow));
        menu.Items.Add(MenuItem(L("Tray_Refresh"), Refresh));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem(L("Tray_Quit"), Quit));

        _icon = new TaskbarIcon
        {
            CustomName = "QuotaBoard",
            NoLeftClickDelay = true,
            PopupActivation = PopupActivationMode.LeftClick,
            MenuActivation = PopupActivationMode.RightClick,
            ContextFlyout = menu,
        };
        _icon.TrayPopup = BuildPopup();
        TryUpdateIcon();
    }

    private bool EnsureIconCreated()
    {
        try
        {
            CreateIcon();
            return TryForceCreate();
        }
        catch
        {
            _icon?.Dispose();
            _icon = null;
            ReleaseIconHandle();
            _displayedStatus = null;
            return false;
        }
    }

    private bool TryForceCreate()
    {
        if (_icon is null)
        {
            return false;
        }
        if (_icon.IsCreated)
        {
            return true;
        }
        try
        {
            _icon.ForceCreate(enablesEfficiencyMode: false);
            return _icon.IsCreated;
        }
        catch (InvalidOperationException)
        {
            // Explorer can reject NIM_ADD while the taskbar is restarting.
            // The next window activation or preference change retries it.
            return false;
        }
    }

    private static MenuFlyoutItem MenuItem(string text, Action action) =>
        new() { Text = text, Command = new DelegateCommand(action) };

    private UIElement BuildPopup()
    {
        var content = new StackPanel { Spacing = 8, MinWidth = 300 };
        content.Children.Add(
            new TextBlock
            {
                Text = "QUOTABOARD",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                CharacterSpacing = 100,
            }
        );

        var meters = TopMeters().Take(4).ToArray();
        if (meters.Length == 0)
        {
            content.Children.Add(
                new TextBlock { Text = L("Tray_NoMeters"), Foreground = new SolidColorBrush(Colors.Gray) }
            );
        }
        else
        {
            foreach ((ProviderCardViewModel provider, MeterViewModel meter) in meters)
            {
                var row = new Grid { ColumnSpacing = 14 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = new TextBlock
                {
                    Text = $"{provider.Name} · {meter.DisplayName}",
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var detail = new TextBlock
                {
                    Text = $"{meter.DisplayUsageLabel} · {meter.DisplayResetLabel}",
                    Foreground = StatusBrush(meter.Status),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                };
                Grid.SetColumn(detail, 1);
                row.Children.Add(title);
                row.Children.Add(detail);
                content.Children.Add(row);
            }
        }

        content.Children.Add(
            new TextBlock
            {
                Text = L("Tray_Hint"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Colors.Gray),
            }
        );

        return new Border
        {
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 88, 96, 100)),
            Background = new SolidColorBrush(Color.FromArgb(255, 28, 32, 34)),
            Child = content,
        };
    }

    private IEnumerable<(ProviderCardViewModel Provider, MeterViewModel Meter)> TopMeters() =>
        _dashboard
            .Providers.SelectMany(provider => provider.AllMeters.Select(meter => (Provider: provider, Meter: meter)))
            .Where(item => !item.Meter.IsStale && TrayMeterStatusPolicy.IsCurrent(item.Meter.Status))
            .OrderByDescending(item => TrayMeterStatusPolicy.Severity(item.Meter.Status))
            .ThenByDescending(item => item.Meter.UsedPercent)
            .ThenBy(item => item.Meter.ResetsAt ?? DateTimeOffset.MaxValue);

    private void UpdateIcon()
    {
        if (_icon is null)
        {
            return;
        }
        (ProviderCardViewModel Provider, MeterViewModel Meter)[] urgent = TopMeters().Take(1).ToArray();
        MeterStatus status = urgent.Length == 0 ? MeterStatus.Healthy : urgent[0].Meter.Status;
        if (_displayedStatus != status)
        {
            nint previous = _iconHandle;
            _iconHandle = CreateStatusIconHandle(status);
            _icon.TrayIcon.UpdateIcon(_iconHandle);
            if (previous != 0)
            {
                _ = DestroyIcon(previous);
            }
            _displayedStatus = status;
        }
        string tooltip =
            urgent.Length == 0
                ? L("Tray_TooltipNoMeters")
                : $"QuotaBoard · {urgent[0].Provider.Name} {urgent[0].Meter.DisplayName}: {urgent[0].Meter.DisplayUsageLabel}";
        _icon.ToolTipText = tooltip.Length <= 127 ? tooltip : tooltip[..127];
        _icon.TrayPopup = BuildPopup();
    }

    // The real QuotaBoard monogram (never a stand-in glyph), ringed in the current
    // quota-status color so tray health stays glanceable. Composed synchronously
    // with GDI into a real HICON: the old GeneratedIconSource + BitmapImage path
    // generated the icon before the async image decode finished, shipping a
    // fully transparent bitmap to the tray — and both shipped .ico variants are
    // a bare glyph on transparency, invisible on a dark taskbar without a tile.
    private static nint CreateStatusIconHandle(MeterStatus status)
    {
        const int size = 32;
        using var bitmap = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            using var tile = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(23, 26, 28));
            graphics.FillEllipse(tile, 0.5f, 0.5f, size - 1f, size - 1f);
            // Load from the install dir (matches MainWindow.SetIcon) so it
            // resolves in the unpackaged app without ms-appx PRI lookup.
            string glyphPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "QuotaBoard-Tray-Glyph.png");
            if (System.IO.File.Exists(glyphPath))
            {
                using var glyph = new System.Drawing.Bitmap(glyphPath);
                graphics.DrawImage(glyph, new System.Drawing.RectangleF(7f, 7f, size - 14f, size - 14f));
            }
            using var ring = new System.Drawing.Pen(StatusColor(status), 3f);
            graphics.DrawEllipse(ring, 1.5f, 1.5f, size - 3f, size - 3f);
        }
        return bitmap.GetHicon();
    }

    private static System.Drawing.Color StatusColor(MeterStatus status) =>
        status switch
        {
            MeterStatus.Exhausted => System.Drawing.Color.FromArgb(255, 92, 92),
            MeterStatus.Critical => System.Drawing.Color.FromArgb(255, 139, 77),
            MeterStatus.Approaching => System.Drawing.Color.FromArgb(255, 201, 71),
            _ => System.Drawing.Color.FromArgb(69, 199, 113),
        };

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint handle);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint windowHandle);

    private void ReleaseIconHandle()
    {
        if (_iconHandle != 0)
        {
            _ = DestroyIcon(_iconHandle);
            _iconHandle = 0;
        }
    }

    private static SolidColorBrush StatusBrush(MeterStatus status) =>
        new(
            status switch
            {
                MeterStatus.Exhausted => Color.FromArgb(255, 255, 92, 92),
                MeterStatus.Critical => Color.FromArgb(255, 255, 139, 77),
                MeterStatus.Approaching => Color.FromArgb(255, 255, 201, 71),
                _ => Color.FromArgb(255, 69, 199, 113),
            }
        );

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_quitting || !TrayMonitorPreference.Enabled)
        {
            return;
        }
        if (!EnsureIconCreated())
        {
            // Never strand the user with a hidden window and no usable tray
            // entry. If Explorer registration fails, allow the close to exit.
            return;
        }
        args.Cancel = true;
        H.NotifyIcon.WindowExtensions.Hide(_window);
        _hidden = true;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated && TrayMonitorPreference.Enabled)
        {
            EnsureIconCreated();
        }
    }

    internal void OpenWindow()
    {
        H.NotifyIcon.WindowExtensions.Show(_window);
        _window.Activate();
        _ = SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window));
        _hidden = false;
        _noteUserInteraction();
    }

    private void Refresh()
    {
        if (_dashboard.RefreshCommand.CanExecute(null))
        {
            _dashboard.RefreshCommand.Execute(null);
        }
    }

    private void Quit()
    {
        _ = _requestQuit();
    }

    public void MarkQuitting() => _quitting = true;

    private void OnPreferenceChanged(bool enabled)
    {
        if (enabled)
        {
            EnsureIconCreated();
        }
        else
        {
            _icon?.Dispose();
            _icon = null;
            ReleaseIconHandle();
            _displayedStatus = null;
            if (_hidden)
            {
                OpenWindow();
            }
        }
    }

    private void OnDashboardChanged(object? sender, NotifyCollectionChangedEventArgs args) => TryUpdateIcon();

    private void OnDashboardPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (
            args.PropertyName
            is nameof(LiveDashboardViewModel.IsRefreshing)
                or nameof(LiveDashboardViewModel.LastUpdated)
        )
        {
            TryUpdateIcon();
        }
    }

    private void TryUpdateIcon()
    {
        try
        {
            UpdateIcon();
        }
        catch
        {
            // The monitor is optional UI. A transient Explorer or icon-source
            // failure must not break a successful quota refresh.
        }
    }

    public void Dispose()
    {
        _window.AppWindow.Closing -= OnWindowClosing;
        _window.Activated -= OnWindowActivated;
        _dashboard.Providers.CollectionChanged -= OnDashboardChanged;
        _dashboard.PropertyChanged -= OnDashboardPropertyChanged;
        TrayMonitorPreference.Changed -= OnPreferenceChanged;
        _icon?.Dispose();
        _icon = null;
        ReleaseIconHandle();
        _displayedStatus = null;
    }

    private static string L(string key) => LocalizationService.GetString(key);

    private sealed class DelegateCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
