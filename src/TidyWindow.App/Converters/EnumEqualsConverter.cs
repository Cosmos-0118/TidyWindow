using System;
using System.Globalization;
using System.Windows.Data;

namespace TidyWindow.App.Converters;

/// <summary>
/// Compares an enum value with a converter parameter (either an enum instance or enum name) and returns true when they match.
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
        {
            return false;
        }

        if (parameter is string paramString)
        {
            var enumType = value.GetType();
            if (!enumType.IsEnum)
            {
                return false;
            }

            if (!Enum.IsDefined(enumType, paramString))
            {
                return false;
            }

            var parsed = Enum.Parse(enumType, paramString);
            return Equals(value, parsed);
        }

        return Equals(value, parameter);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }
}
