using System;
using System.Globalization;
using System.Windows.Data;

namespace Casr.App.Converters;

/// <summary>
/// Session recency as a relative string ("just now", "3h ago", "12d ago").
/// Null/MinValue → "Unknown". Pure function of the value; no clock injection.
/// </summary>
public class RelativeTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime dt || dt == DateTime.MinValue) return "Unknown";
        var now = DateTime.Now;
        var delta = now - dt;
        if (delta.TotalSeconds < 0) return "in the future";
        if (delta.TotalMinutes < 1) return "just now";
        if (delta.TotalHours < 1) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalDays < 1) return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 30) return $"{(int)delta.TotalDays}d ago";
        if (delta.TotalDays < 365) return $"{(int)(delta.TotalDays / 30)}mo ago";
        return $"{(int)(delta.TotalDays / 365)}y ago";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();

    /// <summary>Headless-safe helper for tests: same math without WPF types.</summary>
    public static string ToRelativeString(DateTime dt, DateTime now)
    {
        var delta = now - dt;
        if (dt == DateTime.MinValue) return "Unknown";
        if (delta.TotalSeconds < 0) return "in the future";
        if (delta.TotalMinutes < 1) return "just now";
        if (delta.TotalHours < 1) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalDays < 1) return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 30) return $"{(int)delta.TotalDays}d ago";
        if (delta.TotalDays < 365) return $"{(int)(delta.TotalDays / 30)}mo ago";
        return $"{(int)(delta.TotalDays / 365)}y ago";
    }
}
