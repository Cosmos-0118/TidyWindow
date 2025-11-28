using System;
using System.Globalization;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;

namespace TidyWindow.App.Converters;

public sealed class QuestionnaireOptionSelectionConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2)
        {
            return false;
        }

        var selected = values[0] as string;
        var optionId = values[1] as string;
        if (string.IsNullOrWhiteSpace(optionId))
        {
            return false;
        }

        return string.Equals(selected, optionId, StringComparison.OrdinalIgnoreCase);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked && targetTypes.Length > 0)
        {
            var optionIdIndex = Math.Min(1, targetTypes.Length - 1);
            var optionIdType = targetTypes[optionIdIndex];
            if (optionIdType == typeof(string))
            {
                // When checked, request the bound option id to be applied to SelectedOptionId.
                return new object[] { Binding.DoNothing, Binding.DoNothing };
            }
        }

        return targetTypes.Length switch
        {
            0 => Array.Empty<object>(),
            1 => new object[] { Binding.DoNothing },
            _ => new object[] { Binding.DoNothing, Binding.DoNothing }
        };
    }
}
