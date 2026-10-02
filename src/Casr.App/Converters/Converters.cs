using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Casr.App.Theming;
using Casr.Core.Models;

namespace Casr.App.Converters;

/// <summary>
/// Foreground brush for text placed on a colored badge (provider pill, role pill, selected row).
/// White text on dark themes, dark text on light themes for readable contrast.
/// </summary>
public class OnColorTextBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush DarkThemeBrush = CreateFrozenBrush(255, 255, 255);
    private static readonly SolidColorBrush LightThemeBrush = CreateFrozenBrush(26, 26, 26);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        ThemeManager.CurrentTheme.IsDark ? DarkThemeBrush : LightThemeBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public class RoleToBrushConverter : IValueConverter
{
    // Shared brushes are frozen once: they are never mutated, so freezing makes them
    // free-threaded and avoids per-call allocations/cross-thread affinity faults.
    private static readonly SolidColorBrush UserBrush = FrozenBrush(46, 125, 246);       // Blue
    private static readonly SolidColorBrush AssistantBrush = FrozenBrush(35, 134, 54);   // Green
    private static readonly SolidColorBrush ToolBrush = FrozenBrush(176, 114, 25);       // Orange
    private static readonly SolidColorBrush SystemBrush = FrozenBrush(110, 118, 129);    // Gray
    private static readonly SolidColorBrush DefaultBrush = FrozenBrush(139, 148, 158);

    private static SolidColorBrush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is MessageRole role)
        {
            return role switch
            {
                MessageRole.User => UserBrush,
                MessageRole.Assistant => AssistantBrush,
                MessageRole.Tool => ToolBrush,
                MessageRole.System => SystemBrush,
                _ => DefaultBrush
            };
        }
        return DefaultBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public class ProviderToBadgeBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush AntigravityBrush = FrozenBrush(138, 77, 247); // Purple
    private static readonly SolidColorBrush ClaudeBrush = FrozenBrush(217, 119, 36);      // Claude terracotta
    private static readonly SolidColorBrush CursorBrush = FrozenBrush(0, 122, 204);       // Cursor blue
    private static readonly SolidColorBrush GrokBrush = FrozenBrush(243, 112, 33);        // Grok orange
    private static readonly SolidColorBrush PiBrush = FrozenBrush(245, 158, 11);          // Pi amber gold
    private static readonly SolidColorBrush HermesBrush = FrozenBrush(99, 202, 183);      // Hermes teal
    private static readonly SolidColorBrush OpenCodeBrush = FrozenBrush(168, 85, 247);    // OpenCode violet
    private static readonly SolidColorBrush CodexBrush = FrozenBrush(16, 163, 127);       // Codex emerald
    private static readonly SolidColorBrush DefaultBrush = FrozenBrush(110, 118, 129);

    private static SolidColorBrush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var slug = value?.ToString()?.ToLowerInvariant();
        return slug switch
        {
            "antigravity" => AntigravityBrush,
            "grok" or "grok-build" => GrokBrush,
            "openclaude" => ClaudeBrush,
            "cursor" => CursorBrush,
            "pi" => PiBrush,
            "hermes" => HermesBrush,
            "opencode" => OpenCodeBrush,
            "codex" => CodexBrush,
            _ => DefaultBrush
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isNull = value == null || (value is string s && string.IsNullOrWhiteSpace(s));
        if (Invert) isNull = !isNull;
        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public class BooleanToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is true;
        var shouldInvert = Invert || (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase));
        if (shouldInvert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && v == Visibility.Visible;
}

/// <summary>
/// Grid Match column: (sessionId, view-model) → badge label or plain excerpt.
/// Parameter "badge" returns the source badge ("keyword · exact · …"); anything else
/// returns the plain-text excerpt. Empty string when the row has no current hit.
/// </summary>
public class MatchSnippetConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2) return Fallback(parameter);
        var sessionId = values[0]?.ToString();
        if (values[1] is not ViewModels.MainViewModel vm) return Fallback(parameter);
        if (!vm.TryGetMatchDisplay(sessionId, out var badge, out var excerpt)) return Fallback(parameter);
        var part = parameter as string;
        if (part != null && part.Equals("badgeVisibility", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(badge) ? Visibility.Collapsed : Visibility.Visible;
        return part != null && part.Equals("badge", StringComparison.OrdinalIgnoreCase) ? badge : excerpt;
    }

    private static object Fallback(object parameter) =>
        parameter is string p && p.Equals("badgeVisibility", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Collapsed
            : (object)string.Empty;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
