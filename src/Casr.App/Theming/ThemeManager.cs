using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Casr.Core.Logging;

namespace Casr.App.Theming;

/// <summary>
/// Manages application-wide color palettes and runtime theme switching.
/// </summary>
public static class ThemeManager
{
    public static ColorTheme DefaultTheme { get; } = new(
        Id: "vscode-dark",
        DisplayName: "VS Code Dark Modern",
        Description: "Classic Visual Studio Code studio palette with clean slate, fluent blue, and electric cyan",
        ThemeBg: "#1E1E1E",
        ThemeSurface: "#252526",
        ThemeCard: "#2D2D30",
        ThemeCardAlt: "#1F1F20",
        ThemeBorder: "#3E3E42",
        ThemeAccent: "#0078D4",
        ThemeAccentHover: "#1A8CEB",
        ThemeAccentText: "#FFFFFF",
        ThemeTextPrimary: "#F0F0F0",
        ThemeTextSecondary: "#9E9E9E",
        ThemeHighlight: "#4EC9B0",
        ThemeSelection: "#094771",
        ThemeSelectionHover: "#2A2D2E",
        ThemeStatusBarBg: "#007ACC",
        ThemeStatusBarFg: "#FFFFFF"
    );

    public static IReadOnlyList<ColorTheme> AllThemes { get; } = new List<ColorTheme>
    {
        DefaultTheme,

        new(
            Id: "tokyo-night",
            DisplayName: "Tokyo Night Storm",
            Description: "Authentic Folke Tokyo Night Storm with rich twilight indigo, radiant cyan, and electric blue",
            ThemeBg: "#1F2335",
            ThemeSurface: "#1A1B26",
            ThemeCard: "#24283B",
            ThemeCardAlt: "#16161E",
            ThemeBorder: "#2E3554",
            ThemeAccent: "#7AA2F7",
            ThemeAccentHover: "#89B4FA",
            ThemeAccentText: "#15161E",
            ThemeTextPrimary: "#C0CAF5",
            ThemeTextSecondary: "#7982A9",
            ThemeHighlight: "#7DCFFF",
            ThemeSelection: "#2D3F76",
            ThemeSelectionHover: "#283457",
            ThemeStatusBarBg: "#1A1B26",
            ThemeStatusBarFg: "#7AA2F7"
        ),

        new(
            Id: "catppuccin-mocha",
            DisplayName: "Catppuccin Mocha",
            Description: "The beloved pastel dark theme with soothing lavender, rich mauve, and cool sapphire",
            ThemeBg: "#1E1E2E",
            ThemeSurface: "#181825",
            ThemeCard: "#313244",
            ThemeCardAlt: "#11111B",
            ThemeBorder: "#45475A",
            ThemeAccent: "#CBA6F7",
            ThemeAccentHover: "#DDB6F2",
            ThemeAccentText: "#11111B",
            ThemeTextPrimary: "#CDD6F4",
            ThemeTextSecondary: "#A6ADC8",
            ThemeHighlight: "#89DCEB",
            ThemeSelection: "#45475A",
            ThemeSelectionHover: "#363A4F",
            ThemeStatusBarBg: "#181825",
            ThemeStatusBarFg: "#CBA6F7"
        ),

        new(
            Id: "dracula",
            DisplayName: "Dracula Pro",
            Description: "Legendary gothic vampiric palette featuring vivid neon purple, toxic emerald, and hot pink",
            ThemeBg: "#282A36",
            ThemeSurface: "#21222C",
            ThemeCard: "#343746",
            ThemeCardAlt: "#1E1F29",
            ThemeBorder: "#4D526A",
            ThemeAccent: "#BD93F9",
            ThemeAccentHover: "#FF79C6",
            ThemeAccentText: "#282A36",
            ThemeTextPrimary: "#F8F8F2",
            ThemeTextSecondary: "#A5ADC6",
            ThemeHighlight: "#50FA7B",
            ThemeSelection: "#44475A",
            ThemeSelectionHover: "#3B3F54",
            ThemeStatusBarBg: "#21222C",
            ThemeStatusBarFg: "#FF79C6"
        ),

        new(
            Id: "synthwave-84",
            DisplayName: "Synthwave '84 / Cyberpunk",
            Description: "Electrifying retro-futurism with neon magenta, laser cyan, and deep ultraviolet shadow",
            ThemeBg: "#1A1528",
            ThemeSurface: "#130F1E",
            ThemeCard: "#261D3B",
            ThemeCardAlt: "#171224",
            ThemeBorder: "#49316B",
            ThemeAccent: "#FF2A85",
            ThemeAccentHover: "#FF4D9C",
            ThemeAccentText: "#FFFFFF",
            ThemeTextPrimary: "#FDE5FF",
            ThemeTextSecondary: "#A894BE",
            ThemeHighlight: "#05D9E8",
            ThemeSelection: "#492261",
            ThemeSelectionHover: "#381B4B",
            ThemeStatusBarBg: "#130F1E",
            ThemeStatusBarFg: "#05D9E8"
        ),

        new(
            Id: "github-dark",
            DisplayName: "GitHub Dark High Contrast",
            Description: "Pristine developer aesthetic with deep charcoal, crisp white text, and brilliant sapphire",
            ThemeBg: "#0D1117",
            ThemeSurface: "#161B22",
            ThemeCard: "#21262D",
            ThemeCardAlt: "#161B22",
            ThemeBorder: "#38414D",
            ThemeAccent: "#2F81F7",
            ThemeAccentHover: "#58A6FF",
            ThemeAccentText: "#FFFFFF",
            ThemeTextPrimary: "#F0F6FC",
            ThemeTextSecondary: "#8B949E",
            ThemeHighlight: "#3FB950",
            ThemeSelection: "#1C3D6E",
            ThemeSelectionHover: "#1B222E",
            ThemeStatusBarBg: "#161B22",
            ThemeStatusBarFg: "#58A6FF"
        ),

        new(
            Id: "one-dark-pro",
            DisplayName: "One Dark Pro",
            Description: "The iconic Atom editor theme with warm slate, chalk blue, and radiant moss green",
            ThemeBg: "#282C34",
            ThemeSurface: "#21252B",
            ThemeCard: "#2C313C",
            ThemeCardAlt: "#1E2227",
            ThemeBorder: "#3E4452",
            ThemeAccent: "#61AFEF",
            ThemeAccentHover: "#7EC7FF",
            ThemeAccentText: "#1E2227",
            ThemeTextPrimary: "#ABB2BF",
            ThemeTextSecondary: "#7F848E",
            ThemeHighlight: "#98C379",
            ThemeSelection: "#3E4452",
            ThemeSelectionHover: "#333842",
            ThemeStatusBarBg: "#21252B",
            ThemeStatusBarFg: "#61AFEF"
        ),

        new(
            Id: "nord",
            DisplayName: "Nord Frost",
            Description: "Arctic glacial slate with frost blues, polar white, and calming aurora emerald",
            ThemeBg: "#2E3440",
            ThemeSurface: "#242933",
            ThemeCard: "#3B4252",
            ThemeCardAlt: "#1E222A",
            ThemeBorder: "#4C566A",
            ThemeAccent: "#88C0D0",
            ThemeAccentHover: "#8FBCBB",
            ThemeAccentText: "#242933",
            ThemeTextPrimary: "#ECEFF4",
            ThemeTextSecondary: "#929EAF",
            ThemeHighlight: "#A3BE8C",
            ThemeSelection: "#4C566A",
            ThemeSelectionHover: "#3F4758",
            ThemeStatusBarBg: "#242933",
            ThemeStatusBarFg: "#88C0D0"
        ),

        new(
            Id: "solarized-dark",
            DisplayName: "Solarized Dark Studio",
            Description: "Precision lab cyan and deep abyss teal with glowing golden amber highlights",
            ThemeBg: "#002B36",
            ThemeSurface: "#073642",
            ThemeCard: "#0A404E",
            ThemeCardAlt: "#04232C",
            ThemeBorder: "#135363",
            ThemeAccent: "#2AA198",
            ThemeAccentHover: "#3BC7BC",
            ThemeAccentText: "#002B36",
            ThemeTextPrimary: "#93A1A1",
            ThemeTextSecondary: "#657B83",
            ThemeHighlight: "#B58900",
            ThemeSelection: "#073642",
            ThemeSelectionHover: "#0E4857",
            ThemeStatusBarBg: "#073642",
            ThemeStatusBarFg: "#2AA198"
        ),

        new(
            Id: "monokai-pro",
            DisplayName: "Monokai Pro",
            Description: "Warm charcoal with radiant sunburst gold, vivid coral, and electric cyan",
            ThemeBg: "#2D2A2E",
            ThemeSurface: "#221F22",
            ThemeCard: "#363337",
            ThemeCardAlt: "#19171A",
            ThemeBorder: "#49454A",
            ThemeAccent: "#FFD866",
            ThemeAccentHover: "#FFE485",
            ThemeAccentText: "#221F22",
            ThemeTextPrimary: "#FCFCFA",
            ThemeTextSecondary: "#939293",
            ThemeHighlight: "#78DCE8",
            ThemeSelection: "#49454A",
            ThemeSelectionHover: "#3B383C",
            ThemeStatusBarBg: "#221F22",
            ThemeStatusBarFg: "#FFD866"
        ),

        new(
            Id: "rose-pine",
            DisplayName: "Rosé Pine",
            Description: "Dark, warm twilight aesthetic with delicate rose petal, soft foam, and golden accents",
            ThemeBg: "#191724",
            ThemeSurface: "#1F1D2E",
            ThemeCard: "#26233A",
            ThemeCardAlt: "#14131C",
            ThemeBorder: "#393552",
            ThemeAccent: "#EBBCBA",
            ThemeAccentHover: "#F6C177",
            ThemeAccentText: "#191724",
            ThemeTextPrimary: "#E0DEF4",
            ThemeTextSecondary: "#908CAA",
            ThemeHighlight: "#9CCFD8",
            ThemeSelection: "#393552",
            ThemeSelectionHover: "#2E2B42",
            ThemeStatusBarBg: "#1F1D2E",
            ThemeStatusBarFg: "#EBBCBA"
        ),

        new(
            Id: "oled-obsidian",
            DisplayName: "OLED Pure Obsidian",
            Description: "True pitch black #000000 designed for OLED displays with vivid laser emerald accents",
            ThemeBg: "#000000",
            ThemeSurface: "#09090B",
            ThemeCard: "#121215",
            ThemeCardAlt: "#050507",
            ThemeBorder: "#27272A",
            ThemeAccent: "#22C55E",
            ThemeAccentHover: "#4ADE80",
            ThemeAccentText: "#000000",
            ThemeTextPrimary: "#F4F4F5",
            ThemeTextSecondary: "#71717A",
            ThemeHighlight: "#06B6D4",
            ThemeSelection: "#1E293B",
            ThemeSelectionHover: "#18181B",
            ThemeStatusBarBg: "#09090B",
            ThemeStatusBarFg: "#22C55E"
        ),

        new(
            Id: "vscode-light",
            DisplayName: "VS Code Light Modern",
            Description: "Clean daylight studio palette with crisp white surfaces, slate text, and fluent blue",
            ThemeBg: "#F5F5F5",
            ThemeSurface: "#FFFFFF",
            ThemeCard: "#F0F0F0",
            ThemeCardAlt: "#FAFAFA",
            ThemeBorder: "#D4D4D4",
            ThemeAccent: "#0078D4",
            ThemeAccentHover: "#106EBE",
            ThemeAccentText: "#FFFFFF",
            ThemeTextPrimary: "#1F1F1F",
            ThemeTextSecondary: "#6E6E6E",
            ThemeHighlight: "#0E7C86",
            ThemeSelection: "#ADD6FF",
            ThemeSelectionHover: "#E5F1FB",
            ThemeStatusBarBg: "#007ACC",
            ThemeStatusBarFg: "#FFFFFF"
        ),

        new(
            Id: "github-light",
            DisplayName: "GitHub Light",
            Description: "GitHub's daylight palette with paper-white canvas, ink text, and sapphire links",
            ThemeBg: "#F6F8FA",
            ThemeSurface: "#FFFFFF",
            ThemeCard: "#F6F8FA",
            ThemeCardAlt: "#FBFBFC",
            ThemeBorder: "#D0D7DE",
            ThemeAccent: "#0969DA",
            ThemeAccentHover: "#1F6FEB",
            ThemeAccentText: "#FFFFFF",
            ThemeTextPrimary: "#1F2328",
            ThemeTextSecondary: "#59636E",
            ThemeHighlight: "#1A7F37",
            ThemeSelection: "#B6D7FF",
            ThemeSelectionHover: "#DDF4FF",
            ThemeStatusBarBg: "#0969DA",
            ThemeStatusBarFg: "#FFFFFF"
        ),

        new(
            Id: "solarized-light",
            DisplayName: "Solarized Light",
            Description: "Ethan Schoonover's precision low-contrast daylight palette with warm cream and teal accents",
            ThemeBg: "#FDF6E3",
            ThemeSurface: "#EEE8D5",
            ThemeCard: "#E4DEC7",
            ThemeCardAlt: "#F7F1DC",
            ThemeBorder: "#D3CBB1",
            ThemeAccent: "#268BD2",
            ThemeAccentHover: "#2AA198",
            ThemeAccentText: "#FDF6E3",
            ThemeTextPrimary: "#073642",
            ThemeTextSecondary: "#657B83",
            ThemeHighlight: "#B58900",
            ThemeSelection: "#C8D9E8",
            ThemeSelectionHover: "#E8E2CC",
            ThemeStatusBarBg: "#EEE8D5",
            ThemeStatusBarFg: "#073642"
        ),

        new(
            Id: "catppuccin-latte",
            DisplayName: "Catppuccin Latte",
            Description: "The soothing pastel light theme with warm latte cream, mauve accents, and sky blue highlights",
            ThemeBg: "#EFF1F5",
            ThemeSurface: "#FFFFFF",
            ThemeCard: "#E6E9EF",
            ThemeCardAlt: "#F5F6FA",
            ThemeBorder: "#CCD0DA",
            ThemeAccent: "#8839EF",
            ThemeAccentHover: "#7287FD",
            ThemeAccentText: "#FFFFFF",
            ThemeTextPrimary: "#4C4F69",
            ThemeTextSecondary: "#6C6F85",
            ThemeHighlight: "#179299",
            ThemeSelection: "#C4CEF6",
            ThemeSelectionHover: "#E0E6F5",
            ThemeStatusBarBg: "#E6E9EF",
            ThemeStatusBarFg: "#4C4F69"
        ),

        new(
            Id: "nord-snow",
            DisplayName: "Nord Snow Storm",
            Description: "Arctic daylight counterpart to Nord Frost with polar snow whites and frost blue accents",
            ThemeBg: "#ECEFF4",
            ThemeSurface: "#FFFFFF",
            ThemeCard: "#E5E9F0",
            ThemeCardAlt: "#F3F5F9",
            ThemeBorder: "#D8DEE9",
            ThemeAccent: "#5E81AC",
            ThemeAccentHover: "#81A1C1",
            ThemeAccentText: "#FFFFFF",
            ThemeTextPrimary: "#2E3440",
            ThemeTextSecondary: "#4C566A",
            ThemeHighlight: "#8FBCBB",
            ThemeSelection: "#C9D5E8",
            ThemeSelectionHover: "#E5E9F0",
            ThemeStatusBarBg: "#E5E9F0",
            ThemeStatusBarFg: "#2E3440"
        ),

        new(
            Id: "rose-pine-dawn",
            DisplayName: "Rosé Pine Dawn",
            Description: "Soft morning light aesthetic with warm linen, delicate rose, and golden pine accents",
            ThemeBg: "#FAF4ED",
            ThemeSurface: "#FFFAF3",
            ThemeCard: "#F2E9E1",
            ThemeCardAlt: "#FBF6EF",
            ThemeBorder: "#DFDAD2",
            ThemeAccent: "#B4637A",
            ThemeAccentHover: "#D7827E",
            ThemeAccentText: "#FFFAF3",
            ThemeTextPrimary: "#575279",
            ThemeTextSecondary: "#797593",
            ThemeHighlight: "#286983",
            ThemeSelection: "#EAD9DE",
            ThemeSelectionHover: "#F4EDE8",
            ThemeStatusBarBg: "#F2E9E1",
            ThemeStatusBarFg: "#575279"
        )
    };

    public static ColorTheme CurrentTheme { get; private set; } = DefaultTheme;

    public static event EventHandler<ColorTheme>? ThemeChanged;

    public static ColorTheme GetTheme(string? themeNameOrId)
    {
        if (string.IsNullOrWhiteSpace(themeNameOrId)) return DefaultTheme;

        var match = AllThemes.FirstOrDefault(t => t.DisplayName.Equals(themeNameOrId, StringComparison.OrdinalIgnoreCase))
            ?? AllThemes.FirstOrDefault(t => t.Id.Equals(themeNameOrId, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            CasrLogger.Warn("THEME", $"Unknown theme '{themeNameOrId}'; falling back to '{DefaultTheme.DisplayName}' ({DefaultTheme.Id}).");
            return DefaultTheme;
        }
        return match;
    }

    public static void ApplyTheme(string? themeNameOrId)
    {
        var theme = GetTheme(themeNameOrId);
        CurrentTheme = theme;

        try
        {
            SetBrush("ThemeBgBrush", theme.ThemeBg);
            SetBrush("ThemeSurfaceBrush", theme.ThemeSurface);
            SetBrush("ThemeCardBrush", theme.ThemeCard);
            SetBrush("ThemeCardAltBrush", theme.ThemeCardAlt);
            SetBrush("ThemeBorderBrush", theme.ThemeBorder);
            SetBrush("ThemeAccentBrush", theme.ThemeAccent);
            SetBrush("ThemeAccentHoverBrush", theme.ThemeAccentHover);
            SetBrush("ThemeAccentTextBrush", theme.ThemeAccentText);
            SetBrush("ThemeTextPrimaryBrush", theme.ThemeTextPrimary);
            SetBrush("ThemeTextSecondaryBrush", theme.ThemeTextSecondary);
            SetBrush("ThemeHighlightBrush", theme.ThemeHighlight);
            SetBrush("ThemeSelectionBrush", theme.ThemeSelection);
            SetBrush("ThemeSelectionHoverBrush", theme.ThemeSelectionHover);
            SetBrush("ThemeStatusBarBgBrush", theme.ThemeStatusBarBg);
            SetBrush("ThemeStatusBarFgBrush", theme.ThemeStatusBarFg);

            CasrLogger.Info("THEME", $"Applied theme '{theme.DisplayName}' ({theme.Id})");
            ThemeChanged?.Invoke(null, theme);
        }
        catch (Exception ex)
        {
            CasrLogger.Error("THEME", $"Failed to apply theme '{themeNameOrId}'", ex);
        }
    }

    private static void SetBrush(string key, string hexColor)
    {
        if (Application.Current == null) return;

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hexColor);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Application.Current.Resources[key] = brush;
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("THEME", $"Error setting brush for '{key}' with hex '{hexColor}': {ex.Message}");
        }
    }
}
