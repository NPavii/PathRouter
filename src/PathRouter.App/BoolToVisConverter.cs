using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace PathRouter.App;

public sealed class BoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility.Visible;
}

/// <summary>Visible для непустой строки, Collapsed для null/пусто — заголовки пустых групп прячем.</summary>
public sealed class NonEmptyStringToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Текст кнопки сворачивания по флагу IsCollapsed.</summary>
public sealed class CollapseTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? "Развернуть" : "Свернуть";

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
