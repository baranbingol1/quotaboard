// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;

namespace AiLimits.Presentation.WinUI.Theming;

/// <summary>
/// The built-in theme palettes, transcribed from OpenCode's theme definitions
/// (packages/tui/src/theme/assets). Each ships both a dark and light variant.
/// </summary>
public static class ThemeCatalog
{
    public static readonly ThemePalette Tokyonight = new(
        "tokyonight",
        "Tokyo Night",
        new("#82aaff", "#2e7de9"),
        new("#c099ff", "#9854f1"),
        new("#ff966c", "#b15c00"),
        new("#1a1b26", "#e1e2e7"),
        new("#1e2030", "#d5d6db"),
        new("#222436", "#c8c9ce"),
        new("#c8d3f5", "#3258AF"),
        new("#828bb8", "#555B6C"),
        new("#737aa2", "#737a8c"),
        new("#9099b2", "#5a607d"),
        new("#545c7e", "#9699a8"),
        new("#c3e88d", "#587539"),
        new("#ff966c", "#b15c00"),
        new("#ff757f", "#f52a65"),
        new("#82aaff", "#2e7de9"),
        new ThemeColor[]
        {
            new("#82aaff", "#2e7de9"),
            new("#c3e88d", "#587539"),
            new("#ff966c", "#b15c00"),
            new("#c099ff", "#9854f1"),
            new("#73daca", "#118c74"),
            new("#ff757f", "#f52a65"),
        }
    );

    public static readonly ThemePalette Catppuccin = new(
        "catppuccin",
        "Catppuccin",
        new("#89b4fa", "#1e66f5"),
        new("#cba6f7", "#8839ef"),
        new("#f5c2e7", "#ea76cb"),
        new("#1e1e2e", "#eff1f5"),
        new("#181825", "#e6e9ef"),
        new("#11111b", "#dce0e8"),
        new("#cdd6f4", "#4c4f69"),
        new("#9399b2", "#636679"),
        new("#313244", "#ccd0da"),
        new("#45475a", "#bcc0cc"),
        new("#585b70", "#acb0be"),
        new("#a6e3a1", "#40a02b"),
        new("#f9e2af", "#df8e1d"),
        new("#f38ba8", "#d20f39"),
        new("#94e2d5", "#179299"),
        new ThemeColor[]
        {
            new("#89b4fa", "#1e66f5"),
            new("#a6e3a1", "#40a02b"),
            new("#f5c2e7", "#ea76cb"),
            new("#cba6f7", "#8839ef"),
            new("#f9e2af", "#df8e1d"),
            new("#f38ba8", "#d20f39"),
        }
    );

    // Macchiato is one of Catppuccin's dark flavours; upstream pairs every dark
    // flavour with Latte, the palette's only light one. The light column below is
    // Latte, matching the Catppuccin entry above.
    public static readonly ThemePalette Catppuccinmacchiato = new(
        "catppuccin-macchiato",
        "Catppuccin Macchiato",
        new("#8aadf4", "#1e66f5"),
        new("#c6a0f6", "#8839ef"),
        new("#f5bde6", "#ea76cb"),
        new("#24273a", "#eff1f5"),
        new("#1e2030", "#e6e9ef"),
        new("#181926", "#dce0e8"),
        new("#cad3f5", "#4c4f69"),
        new("#939ab7", "#636679"),
        new("#363a4f", "#ccd0da"),
        new("#494d64", "#bcc0cc"),
        new("#5b6078", "#acb0be"),
        new("#a6da95", "#40a02b"),
        new("#eed49f", "#df8e1d"),
        new("#ed8796", "#d20f39"),
        new("#8bd5ca", "#179299"),
        new ThemeColor[]
        {
            new("#8aadf4", "#1e66f5"),
            new("#a6da95", "#40a02b"),
            new("#f5bde6", "#ea76cb"),
            new("#c6a0f6", "#8839ef"),
            new("#eed49f", "#df8e1d"),
            new("#ed8796", "#d20f39"),
        }
    );

    public static readonly ThemePalette Gruvbox = new(
        "gruvbox",
        "Gruvbox",
        new("#83a598", "#076678"),
        new("#d3869b", "#8f3f71"),
        new("#8ec07c", "#427b58"),
        new("#282828", "#fbf1c7"),
        new("#3c3836", "#ebdbb2"),
        new("#504945", "#d5c4a1"),
        new("#ebdbb2", "#3c3836"),
        new("#928374", "#685D54"),
        new("#665c54", "#bdae93"),
        new("#ebdbb2", "#3c3836"),
        new("#504945", "#d5c4a1"),
        new("#b8bb26", "#79740e"),
        new("#fe8019", "#af3a03"),
        new("#fb4934", "#9d0006"),
        new("#fabd2f", "#b57614"),
        new ThemeColor[]
        {
            new("#83a598", "#076678"),
            new("#b8bb26", "#79740e"),
            new("#8ec07c", "#427b58"),
            new("#d3869b", "#8f3f71"),
            new("#fe8019", "#af3a03"),
            new("#fb4934", "#9d0006"),
        }
    );

    // Nord ships no official light variant: the light column keeps the Snow Storm
    // greys upstream uses for light surfaces and darkens Frost/Aurora until each
    // role clears ~3.5:1 on that background.
    public static readonly ThemePalette Nord = new(
        "nord",
        "Nord",
        new("#88C0D0", "#5E81AC"),
        new("#81A1C1", "#5681AC"),
        new("#8FBCBB", "#518785"),
        new("#2E3440", "#ECEFF4"),
        new("#3B4252", "#E5E9F0"),
        new("#434C5E", "#D8DEE9"),
        new("#ECEFF4", "#2E3440"),
        new("#8B95A7", "#3B4252"),
        new("#434C5E", "#C2CAD6"),
        new("#4C566A", "#8A94A6"),
        new("#434C5E", "#D5DCE6"),
        new("#A3BE8C", "#67874C"),
        new("#D08770", "#C26345"),
        new("#BF616A", "#BF616A"),
        new("#88C0D0", "#5E81AC"),
        new ThemeColor[]
        {
            new("#88C0D0", "#5E81AC"),
            new("#A3BE8C", "#67874C"),
            new("#EBCB8B", "#C38D22"),
            new("#81A1C1", "#8A5F82"),
            new("#D08770", "#C26345"),
            new("#BF616A", "#BF616A"),
        }
    );

    public static readonly ThemePalette Everforest = new(
        "everforest",
        "Everforest",
        new("#a7c080", "#8da101"),
        new("#7fbbb3", "#3a94c5"),
        new("#d699b6", "#df69ba"),
        new("#2d353b", "#fdf6e3"),
        new("#333c43", "#efebd4"),
        new("#343f44", "#f4f0d9"),
        new("#d3c6aa", "#5c6a72"),
        new("#7a8478", "#616C5A"),
        new("#859289", "#939f91"),
        new("#9da9a0", "#829181"),
        new("#7a8478", "#a6b0a0"),
        new("#a7c080", "#8da101"),
        new("#e69875", "#f57d26"),
        new("#e67e80", "#f85552"),
        new("#83c092", "#35a77c"),
        new ThemeColor[]
        {
            new("#a7c080", "#8da101"),
            new("#dbbc7f", "#dfa000"),
            new("#d699b6", "#df69ba"),
            new("#7fbbb3", "#3a94c5"),
            new("#e69875", "#f57d26"),
            new("#e67e80", "#f85552"),
        }
    );

    // Light column is Kanagawa Lotus, the palette's official light variant.
    public static readonly ThemePalette Kanagawa = new(
        "kanagawa",
        "Kanagawa",
        new("#7E9CD8", "#2D4F67"),
        new("#957FB8", "#624C83"),
        new("#D27E99", "#B35B79"),
        new("#1F1F28", "#F2E9DE"),
        new("#2A2A37", "#EAE4D7"),
        new("#363646", "#E3DCD2"),
        new("#DCD7BA", "#54433A"),
        new("#727169", "#6C6A5D"),
        new("#54546D", "#D4CBBF"),
        new("#C38D9D", "#B35B79"),
        new("#363646", "#DCD4C9"),
        new("#98BB6C", "#69824A"),
        new("#D7A657", "#BC6400"),
        new("#E82424", "#C84053"),
        new("#76946A", "#498297"),
        new ThemeColor[]
        {
            new("#7E9CD8", "#2D4F67"),
            new("#98BB6C", "#69824A"),
            new("#D27E99", "#B35B79"),
            new("#957FB8", "#624C83"),
            new("#D7A657", "#BC6400"),
            new("#E82424", "#C84053"),
        }
    );

    // Light column is Ayu Light (ayu-colors "light" flavour), with the syntax and
    // accent hues darkened where the stock values fall below ~3.5:1 on its near-white
    // background.
    public static readonly ThemePalette Ayu = new(
        "ayu",
        "Ayu",
        new("#59C2FF", "#1C8BDB"),
        new("#D2A6FF", "#9E73C9"),
        new("#E6B450", "#C47200"),
        new("#0B0E14", "#FCFCFC"),
        new("#0F131A", "#F3F4F5"),
        new("#0D1017", "#E9EBEC"),
        new("#BFBDB6", "#5C6166"),
        new("#565B66", "#6E757E"),
        new("#6C7380", "#D3D6DA"),
        new("#6C7380", "#A8AEB6"),
        new("#11151C", "#E6E8EA"),
        new("#7FD962", "#6E9300"),
        new("#E6B673", "#DE6106"),
        new("#D95757", "#E65050"),
        new("#39BAE6", "#2D90B2"),
        new ThemeColor[]
        {
            new("#59C2FF", "#1C8BDB"),
            new("#7FD962", "#64B33D"),
            new("#E6B450", "#C47200"),
            new("#D2A6FF", "#9E73C9"),
            new("#95E6CB", "#40B38D"),
            new("#D95757", "#E65050"),
        }
    );

    public static readonly ThemePalette Onedark = new(
        "one-dark",
        "One Dark",
        new("#61afef", "#4078f2"),
        new("#c678dd", "#a626a4"),
        new("#56b6c2", "#0184bc"),
        new("#282c34", "#fafafa"),
        new("#21252b", "#f0f0f1"),
        new("#353b45", "#eaeaeb"),
        new("#abb2bf", "#383a42"),
        new("#5c6370", "#6A6B72"),
        new("#393f4a", "#d1d1d2"),
        new("#61afef", "#4078f2"),
        new("#2c313a", "#e0e0e1"),
        new("#98c379", "#50a14f"),
        new("#e5c07b", "#c18401"),
        new("#e06c75", "#e45649"),
        new("#d19a66", "#986801"),
        new ThemeColor[]
        {
            new("#61afef", "#4078f2"),
            new("#98c379", "#50a14f"),
            new("#56b6c2", "#0184bc"),
            new("#c678dd", "#a626a4"),
            new("#e5c07b", "#c18401"),
            new("#e06c75", "#e45649"),
        }
    );

    // Light column keeps the phosphor-green identity but drops it onto paper: the
    // neon greens, lime and cyan are far too pale to read on a light background.
    public static readonly ThemePalette Matrix = new(
        "matrix",
        "Matrix",
        new("#2eff6a", "#159339"),
        new("#00efff", "#06907d"),
        new("#c770ff", "#8f2fcc"),
        new("#0a0e0a", "#eef3ea"),
        new("#0e130d", "#e4ebe1"),
        new("#141c12", "#dae1d7"),
        new("#62ff94", "#203022"),
        new("#8ca391", "#5f6f62"),
        new("#1e2a1b", "#c3cfbf"),
        new("#2eff6a", "#159339"),
        new("#141c12", "#dae1d7"),
        new("#62ff94", "#159339"),
        new("#e6ff57", "#748900"),
        new("#ff4b4b", "#c62828"),
        new("#30b3ff", "#0084d1"),
        new ThemeColor[]
        {
            new("#2eff6a", "#159339"),
            new("#62ff94", "#3fa85c"),
            new("#c770ff", "#8f2fcc"),
            new("#00efff", "#06907d"),
            new("#e6ff57", "#748900"),
            new("#ff4b4b", "#c62828"),
        },
        ContentFontId: "cascadia-mono"
    );

    public static IReadOnlyList<ThemePalette> All { get; } =
        new[]
        {
            Tokyonight,
            Catppuccin,
            Catppuccinmacchiato,
            Gruvbox,
            Nord,
            Everforest,
            Kanagawa,
            Ayu,
            Onedark,
            Matrix,
        };

    public static ThemePalette Default => Tokyonight;

    public static ThemePalette Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Default;
        }
        return All.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Default;
    }
}
