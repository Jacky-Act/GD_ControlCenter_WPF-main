using System;
using System.Globalization;
using System.Windows.Data;

namespace GD_ControlCenter_WPF.Helpers
{
    /// <summary>
    /// 布尔取反转换器：用于让“开始采集”和“停止采集”按钮状态互斥。
    /// </summary>
    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b) return !b; // 如果为 True，返回 False (变灰禁用)
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b) return !b;
            return false;
        }
    }
}