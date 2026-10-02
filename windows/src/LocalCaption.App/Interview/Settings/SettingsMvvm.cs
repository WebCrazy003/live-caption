using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;

namespace LocalCaption.App.Interview.Settings;

/// <summary>INotifyPropertyChanged with a setter helper. Local to these pages.</summary>
public abstract class SettingsObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raise for <paramref name="name"/>; null or empty refreshes every binding.</summary>
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void RaiseMany(params string[] names)
    {
        foreach (var name in names) Raise(name);
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>One entry in a picker: what to show, and the config value it stands for.</summary>
public sealed record SettingsChoice(string Label, string Value);

/// <summary>
/// A radio button bound to a string setting: checked when the value equals the
/// <c>ConverterParameter</c>; checking it writes the parameter back. Unchecking writes nothing
/// (the newly checked sibling does).
/// </summary>
public sealed class SettingEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString());

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter?.ToString() : Binding.DoNothing;
}

/// <summary>True → Visible, anything else → Collapsed; with <c>ConverterParameter=Invert</c> the reverse.</summary>
public sealed class SettingVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var on = value switch
        {
            bool b => b,
            string s => s.Length > 0,
            null => false,
            _ => true,
        };
        if (parameter as string == "Invert") on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
