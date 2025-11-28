using System;
using System.Globalization;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;

namespace TidyWindow.App.Converters;

public sealed class QuestionnaireOptionSelectionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string selected && parameter is string optionId)
        {
            return string.Equals(selected, optionId, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked && parameter is string optionId)
        {
            return optionId;
        }

        return Binding.DoNothing;
    }
}
