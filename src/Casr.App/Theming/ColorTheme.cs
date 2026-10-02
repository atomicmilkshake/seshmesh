namespace Casr.App.Theming;

/// <summary>
/// Defines a complete color scheme palette for the CASR application.
/// </summary>
public record ColorTheme(
    string Id,
    string DisplayName,
    string Description,
    string ThemeBg,
    string ThemeSurface,
    string ThemeCard,
    string ThemeCardAlt,
    string ThemeBorder,
    string ThemeAccent,
    string ThemeAccentHover,
    string ThemeAccentText,
    string ThemeTextPrimary,
    string ThemeTextSecondary,
    string ThemeHighlight,
    string ThemeSelection,
    string ThemeSelectionHover,
    string ThemeStatusBarBg,
    string ThemeStatusBarFg
)
{
    /// <summary>True when the theme is a dark color scheme (dark background, light text), derived from the background luminance.</summary>
    public bool IsDark
    {
        get
        {
            var c = ThemeBg.TrimStart('#');
            if (c.Length < 6) return true;
            var r = Convert.ToInt32(c.Substring(0, 2), 16);
            var g = Convert.ToInt32(c.Substring(2, 2), 16);
            var b = Convert.ToInt32(c.Substring(4, 2), 16);
            // Relative luminance threshold
            return (0.299 * r + 0.587 * g + 0.114 * b) < 140;
        }
    }
}

