using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Casr.Core.Search;

namespace Casr.App.Converters;

/// <summary>
/// Renders a search snippet (with literal &lt;b&gt;...&lt;/b&gt; match markers over
/// HTML-escaped content) as TextBlock inlines: bold for matches, plain otherwise.
/// Any other &lt;...&gt; sequences are literal text, never markup.
/// </summary>
public static class SnippetHighlightBehavior
{
    public static readonly DependencyProperty HtmlTextProperty =
        DependencyProperty.RegisterAttached(
            "HtmlText",
            typeof(string),
            typeof(SnippetHighlightBehavior),
            new PropertyMetadata(string.Empty, OnHtmlTextChanged));

    public static string GetHtmlText(DependencyObject obj) =>
        (string)obj.GetValue(HtmlTextProperty);

    public static void SetHtmlText(DependencyObject obj, string value) =>
        obj.SetValue(HtmlTextProperty, value);

    private static void OnHtmlTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        block.Inlines.Clear();
        foreach (var (text, bold) in SnippetHighlighter.Parse(e.NewValue as string))
        {
            if (string.IsNullOrEmpty(text)) continue;
            block.Inlines.Add(bold ? new Bold(new Run(text)) : new Run(text));
        }
    }
}
