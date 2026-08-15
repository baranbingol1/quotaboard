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

    public static readonly ThemePalette Catppuccinmacchiato = new(
        "catppuccin-macchiato",
        "Catppuccin Macchiato",
        new("#8aadf4", "#8aadf4"),
        new("#c6a0f6", "#c6a0f6"),
        new("#f5bde6", "#f5bde6"),
        new("#24273a", "#24273a"),
        new("#1e2030", "#1e2030"),
        new("#181926", "#181926"),
        new("#cad3f5", "#cad3f5"),
        new("#939ab7", "#939ab7"),
        new("#363a4f", "#363a4f"),
        new("#494d64", "#494d64"),
        new("#5b6078", "#5b6078"),
        new("#a6da95", "#a6da95"),
        new("#eed49f", "#eed49f"),
        new("#ed8796", "#ed8796"),
        new("#8bd5ca", "#8bd5ca"),
        new ThemeColor[]
        {
            new("#8aadf4", "#8aadf4"),
            new("#a6da95", "#a6da95"),
            new("#f5bde6", "#f5bde6"),
            new("#c6a0f6", "#c6a0f6"),
            new("#eed49f", "#eed49f"),
            new("#ed8796", "#ed8796"),
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

    public static readonly ThemePalette Nord = new(
        "nord",
        "Nord",
        new("#88C0D0", "#5E81AC"),
        new("#81A1C1", "#81A1C1"),
        new("#8FBCBB", "#8FBCBB"),
        new("#2E3440", "#ECEFF4"),
        new("#3B4252", "#E5E9F0"),
        new("#434C5E", "#D8DEE9"),
        new("#ECEFF4", "#2E3440"),
        new("#8B95A7", "#3B4252"),
        new("#434C5E", "#4C566A"),
        new("#4C566A", "#434C5E"),
        new("#434C5E", "#4C566A"),
        new("#A3BE8C", "#A3BE8C"),
        new("#D08770", "#D08770"),
        new("#BF616A", "#BF616A"),
        new("#88C0D0", "#5E81AC"),
        new ThemeColor[]
        {
            new("#88C0D0", "#5E81AC"),
            new("#A3BE8C", "#A3BE8C"),
            new("#EBCB8B", "#EBCB8B"),
            new("#81A1C1", "#81A1C1"),
            new("#D08770", "#D08770"),
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

    public static readonly ThemePalette Kanagawa = new(
        "kanagawa",
        "Kanagawa",
        new("#7E9CD8", "#2D4F67"),
        new("#957FB8", "#957FB8"),
        new("#D27E99", "#D27E99"),
        new("#1F1F28", "#F2E9DE"),
        new("#2A2A37", "#EAE4D7"),
        new("#363646", "#E3DCD2"),
        new("#DCD7BA", "#54433A"),
        new("#727169", "#9E9389"),
        new("#54546D", "#D4CBBF"),
        new("#C38D9D", "#C38D9D"),
        new("#363646", "#DCD4C9"),
        new("#98BB6C", "#98BB6C"),
        new("#D7A657", "#D7A657"),
        new("#E82424", "#E82424"),
        new("#76946A", "#76946A"),
        new ThemeColor[]
        {
            new("#7E9CD8", "#2D4F67"),
            new("#98BB6C", "#98BB6C"),
            new("#D27E99", "#D27E99"),
            new("#957FB8", "#957FB8"),
            new("#D7A657", "#D7A657"),
            new("#E82424", "#E82424"),
        }
    );

    public static readonly ThemePalette Ayu = new(
        "ayu",
        "Ayu",
        new("#59C2FF", "#59C2FF"),
        new("#D2A6FF", "#D2A6FF"),
        new("#E6B450", "#E6B450"),
        new("#0B0E14", "#0B0E14"),
        new("#0F131A", "#0F131A"),
        new("#0D1017", "#0D1017"),
        new("#BFBDB6", "#BFBDB6"),
        new("#565B66", "#565B66"),
        new("#6C7380", "#6C7380"),
        new("#6C7380", "#6C7380"),
        new("#11151C", "#11151C"),
        new("#7FD962", "#7FD962"),
        new("#E6B673", "#E6B673"),
        new("#D95757", "#D95757"),
        new("#39BAE6", "#39BAE6"),
        new ThemeColor[]
        {
            new("#59C2FF", "#59C2FF"),
            new("#7FD962", "#7FD962"),
            new("#E6B450", "#E6B450"),
            new("#D2A6FF", "#D2A6FF"),
            new("#95E6CB", "#95E6CB"),
            new("#D95757", "#D95757"),
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

    public static readonly ThemePalette Matrix = new(
        "matrix",
        "Matrix",
        new("#2eff6a", "#1cc24b"),
        new("#00efff", "#24f6d9"),
        new("#c770ff", "#c770ff"),
        new("#0a0e0a", "#eef3ea"),
        new("#0e130d", "#e4ebe1"),
        new("#141c12", "#dae1d7"),
        new("#62ff94", "#203022"),
        new("#8ca391", "#748476"),
        new("#1e2a1b", "#748476"),
        new("#2eff6a", "#1cc24b"),
        new("#141c12", "#dae1d7"),
        new("#62ff94", "#1cc24b"),
        new("#e6ff57", "#e6ff57"),
        new("#ff4b4b", "#ff4b4b"),
        new("#30b3ff", "#30b3ff"),
        new ThemeColor[]
        {
            new("#2eff6a", "#1cc24b"),
            new("#62ff94", "#1cc24b"),
            new("#c770ff", "#c770ff"),
            new("#00efff", "#24f6d9"),
            new("#e6ff57", "#e6ff57"),
            new("#ff4b4b", "#ff4b4b"),
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
