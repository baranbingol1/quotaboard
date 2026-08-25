// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Presentation.WinUI.Shell;

/// <summary>
/// Process-wide handle of the main window. WinRT file pickers need an owner
/// HWND through <c>InitializeWithWindow</c>, and pages live in this assembly
/// without a reference to the host that owns the Window.
/// </summary>
public static class MainWindowHandle
{
    private static IntPtr _handle;

    /// <summary>Called once by the host after the main window is created.</summary>
    public static void Register(IntPtr handle) => _handle = handle;

    /// <summary>Zero when no window has registered yet.</summary>
    public static IntPtr Current => _handle;
}
