using System.Windows;

namespace LocalCaption.App.Interview.Answers;

/// <summary>The Answers views' own styles (see the XAML); one shared instance.</summary>
public partial class AnswersTheme : ResourceDictionary
{
    private static AnswersTheme? _shared;

    public AnswersTheme() => InitializeComponent();

    /// <summary>
    /// The instance every Answers view merges into its <c>Resources</c> (a dictionary can be
    /// merged into many). Created on first use, on the UI thread.
    /// </summary>
    public static AnswersTheme Shared => _shared ??= new AnswersTheme();

    /// <summary>Merge <see cref="Shared"/> into <paramref name="element"/>'s resources (once).</summary>
    public static void Apply(FrameworkElement element)
    {
        if (!element.Resources.MergedDictionaries.Contains(Shared))
            element.Resources.MergedDictionaries.Add(Shared);
    }
}
