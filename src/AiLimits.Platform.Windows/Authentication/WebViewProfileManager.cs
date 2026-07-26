// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Platform.Windows.Authentication;

public sealed class WebViewProfileManager(string rootDirectory)
{
    public string GetProfileDirectory(string providerId, string accountId)
    {
        string combined = Path.Combine(rootDirectory, Safe(providerId), Safe(accountId));
        string directory = Path.GetFullPath(combined);
        string rootPrefix = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("WebView profile path escaped the configured root.");
        }
        Directory.CreateDirectory(directory);
        return directory;
    }

    public async Task DeleteProfileAsync(string providerId, string accountId, CancellationToken cancellationToken)
    {
        string directory = Path.GetFullPath(Path.Combine(rootDirectory, Safe(providerId), Safe(accountId)));
        string value = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("WebView profile path escaped the configured root.");
        }
        if (Directory.Exists(directory))
        {
            await Task.Run(delegate
            {
                Directory.Delete(directory, recursive: true);
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Safe(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, "value");
        char[] invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select((char character) => (!invalid.Contains(character)) ? character : '_'));
    }
}
