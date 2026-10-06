using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using CxSshClient.Models;

namespace CxSshClient.Helpers;

public class ProtocolGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is SessionProtocol p ? ProtocolInfo.Glyph(p) : "\uE71D";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

public class ProtocolColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not SessionProtocol p) return new SolidColorBrush(Colors.Gray);
        var hex = ProtocolInfo.Color(p).TrimStart('#');
        byte r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
        byte g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
        byte b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

public class ProtocolColorSoftConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not SessionProtocol p) return new SolidColorBrush(Colors.Gray);
        var hex = ProtocolInfo.Color(p).TrimStart('#');
        byte r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
        byte g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
        byte b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(38, r, g, b));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is bool b ? !b : true;
}

public class TimeAgoConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not long ms) return "";
        var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
        var diff = DateTimeOffset.Now - t;
        if (diff.TotalMinutes < 1) return "刚刚";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes} 分钟前";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours} 小时前";
        if (diff.TotalDays < 30) return $"{(int)diff.TotalDays} 天前";
        return t.ToString("yyyy-MM-dd");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

public class StringNotEmptyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        !string.IsNullOrWhiteSpace(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}
