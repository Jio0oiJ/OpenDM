using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using OpenDM.Core.Models;

namespace OpenDM.App;

/// <summary>任务状态 → 颜色（下载中蓝、完成绿、暂停琥珀、出错红）。</summary>
public sealed class StateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var state = value is TaskState s ? s : TaskState.Queued;
        string hex = state switch
        {
            TaskState.Downloading => "#4C8DFF",
            TaskState.Completed => "#3FB950",
            TaskState.Paused => "#D29922",
            TaskState.Error => "#F85149",
            TaskState.Probing => "#8A97A6",
            _ => "#8A97A6"
        };
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>状态图标字形。</summary>
public sealed class StateGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var state = value is TaskState s ? s : TaskState.Queued;
        return state switch
        {
            TaskState.Downloading => "\uE896",
            TaskState.Completed => "\uE73E",
            TaskState.Paused => "\uE769",
            TaskState.Error => "\uEA39",
            TaskState.Probing => "\uE895",
            _ => "\uE896"
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>值为 0 时可见（用于空列表提示）。</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int i) return i == 0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → Visibility，可选参数 invert。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility vis && vis == Visibility.Visible;
}

/// <summary>double 0..1 → 百分比文本。</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d ? $"{d * 100:F0}%" : "0%";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>连接数滑块的值显示为整数。</summary>
public sealed class DoubleToIntConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d ? ((int)Math.Round(d)).ToString() : "8";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && int.TryParse(s, out var i) ? (double)i : 8d;
}
