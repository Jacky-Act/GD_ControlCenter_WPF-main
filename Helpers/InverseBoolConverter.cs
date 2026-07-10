using System.Globalization;
using System.Windows.Data;

/*
 * 文件名: InverseBoolConverter.cs
 * 描述: 本文件包含一个布尔取反的数据转换器（Converter），
 * 主要用于在 UI 层将 ViewModel 中的布尔状态反转。
 * 典型应用场景：让“开始采集”和“停止采集”按钮的启用状态互斥（如一方绑定的状态为 True 则转换返回 False 变灰禁用）。
 */

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