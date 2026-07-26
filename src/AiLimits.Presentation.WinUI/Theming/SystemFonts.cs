// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace AiLimits.Presentation.WinUI.Theming;

/// <summary>
/// Answers "is this font family actually installed on this machine".
///
/// The font selector offers system families that only ship with newer Windows
/// builds or alongside Windows Terminal (Segoe UI Variable, Cascadia Mono), so
/// every choice carries a fallback chain and something has to decide when to
/// walk down it. XAML cannot: an unknown <c>FontFamily</c> silently renders in
/// whatever DirectWrite substitutes, which is how a "monospace" metric font can
/// end up proportional with no visible error.
/// </summary>
internal static class SystemFonts
{
    private const int DefaultCharSet = 1;
    // LOGFONTW.lfFaceName is a fixed 32-wchar buffer including the terminator.
    private const int MaxFaceNameLength = 31;

    private static readonly ConcurrentDictionary<string, bool> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool IsInstalled(string family)
    {
        if (string.IsNullOrWhiteSpace(family) || family.Trim().Length > MaxFaceNameLength)
        {
            return false;
        }
        return Cache.GetOrAdd(family.Trim(), Probe);
    }

    private static bool Probe(string family)
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            // No screen DC to enumerate against. Assume the family is present:
            // a transient failure must not silently rewrite the user's fonts.
            return true;
        }

        try
        {
            var request = new LogFont { lfCharSet = DefaultCharSet, lfFaceName = family };
            bool found = false;
            // EnumFontFamiliesEx matches on the exact family name and performs no
            // substitution of its own, which is precisely what a probe needs.
            EnumFontFamiliesExProc callback = (_, _, _, _) =>
            {
                found = true;
                return 0; // Zero stops enumeration at the first match.
            };
            _ = EnumFontFamiliesEx(dc, ref request, callback, IntPtr.Zero, 0);
            GC.KeepAlive(callback);
            return found;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return true;
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, dc);
        }
    }

    private delegate int EnumFontFamiliesExProc(IntPtr logFont, IntPtr textMetric, uint fontType, IntPtr param);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LogFont
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumFontFamiliesExW")]
    private static extern int EnumFontFamiliesEx(
        IntPtr dc,
        ref LogFont logFont,
        EnumFontFamiliesExProc callback,
        IntPtr param,
        uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}
