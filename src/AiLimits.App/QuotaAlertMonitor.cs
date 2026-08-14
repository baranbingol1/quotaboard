// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using AiLimits.Application.Alerts;
using AiLimits.Domain;
using AiLimits.Infrastructure.Persistence;
using AiLimits.Presentation.WinUI;
using AiLimits.Presentation.WinUI.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace AiLimits.App;

internal sealed class QuotaAlertMonitor : IDisposable
{
    private readonly WindowsAppNotificationSink _notificationSink;
    private readonly AlertProcessor _processor;

    public QuotaAlertMonitor(SqliteDatabase database)
    {
        _notificationSink = new WindowsAppNotificationSink();
        _processor = new AlertProcessor(
            new AlertEvaluator(new LocalizedAlertTextProvider()),
            new SqliteAlertStateRepository(database),
            _notificationSink,
            NullLogger<AlertProcessor>.Instance
        );
    }

    public Task ProcessAsync(
        IEnumerable<ProviderSnapshot> snapshots,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        AlertPolicy policy = AlertPolicy.Suggested with { Enabled = QuotaAlertPreference.LoadEnabled() };
        return _processor.ProcessAsync(snapshots, policy, now, cancellationToken);
    }

    public void Dispose() => _notificationSink.Dispose();
}

internal sealed class LocalizedAlertTextProvider : IAlertTextProvider
{
    public string UsageTitle(string providerId, string meterKey, string meterName, double threshold) =>
        F("Alert_UsageTitle", ProviderName(providerId), RuntimeText.MeterDisplayName(meterKey, meterName), threshold);

    public string UsageMessage(double usedPercent, DateTimeOffset? resetsAt) =>
        resetsAt is DateTimeOffset reset
            ? F("Alert_UsageWithReset", usedPercent, reset.ToLocalTime())
            : F("Alert_Usage", usedPercent);

    public string ResetTitle(string providerId, string meterKey, string meterName) =>
        F("Alert_ResetTitle", ProviderName(providerId), RuntimeText.MeterDisplayName(meterKey, meterName));

    public string ResetMessage(TimeSpan remaining) => F("Alert_ResetMessage", FormatRemaining(remaining));

    private static string FormatRemaining(TimeSpan remaining) =>
        remaining.TotalHours >= 1
            ? F("Alert_HoursMinutes", (int)remaining.TotalHours, remaining.Minutes)
            : F("Alert_Minutes", Math.Max(1, remaining.Minutes));

    private static string ProviderName(string providerId) =>
        providerId.ToLowerInvariant() switch
        {
            "codex" => "Codex",
            "claude" => "Claude Code",
            "opencode" => "OpenCode",
            "droid" => "Droid",
            "copilot" => "GitHub Copilot",
            "amp" => "Amp",
            _ => providerId,
        };

    private static string F(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString(key), args);
}

internal sealed class WindowsAppNotificationSink : IAlertNotificationSink, IDisposable
{
    private AppNotificationManager? _manager;
    private bool _registered;

    public WindowsAppNotificationSink()
    {
        try
        {
            _manager = AppNotificationManager.Default;
            _manager.Register();
            _registered = true;
        }
        catch
        {
            // App startup and refresh must remain safe on Windows editions or
            // policies where app notifications are unavailable.
        }
    }

    public Task<bool> TryShowAsync(AlertCandidate candidate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_registered || _manager is null)
        {
            return Task.FromResult(false);
        }

        try
        {
            AppNotification notification = new AppNotificationBuilder()
                .AddText(candidate.Title)
                .AddText(candidate.Message)
                .BuildNotification();
            _manager.Show(notification);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public void Dispose()
    {
        if (!_registered || _manager is null)
        {
            return;
        }

        try
        {
            _manager.Unregister();
        }
        catch { }
        finally
        {
            _registered = false;
        }
    }
}
