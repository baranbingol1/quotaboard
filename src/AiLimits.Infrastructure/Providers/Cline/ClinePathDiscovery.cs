// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Infrastructure.Providers.Cline;

/// <summary>
/// Locates the local Cline storage roots. The Cline CLI (v3.x) keeps its data
/// under ~/.cline/data; the VS Code extension (saoudrizwan.claude-dev) keeps
/// the same layout under each editor's globalStorage. Any subset may exist.
/// </summary>
internal static class ClinePathDiscovery
{
    private static readonly string[] EditorNames = ["Code", "Code - Insiders", "VSCodium", "Cursor", "Windsurf"];

    internal static IEnumerable<string> FindRoots()
    {
        string cliRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cline",
            "data"
        );
        if (Directory.Exists(cliRoot))
        {
            yield return cliRoot;
        }

        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (string editor in EditorNames)
        {
            string extensionRoot = Path.Combine(roaming, editor, "User", "globalStorage", "saoudrizwan.claude-dev");
            if (Directory.Exists(extensionRoot))
            {
                yield return extensionRoot;
            }
        }
    }
}
