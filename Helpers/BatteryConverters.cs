using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

/*
 * 文件名: BatteryConverters.cs
 * 描述: 本文件包含一组用于 UI 层的数据转换器（Converters），
 * 主要用于处理电池状态、电量显示、以及设备连接状态在 WPF XAML 中的视觉呈现逻辑。
 * 通过实现 IValueConverter 和 IMultiValueConverter，将 ViewModel 中的业务数据转换为 XAML 可识别的颜色、宽度或可见性。
 */

namespace GD_ControlCenter_WPF.Helpers
{
    /// <summary>
    /// 电池电量槽宽度多值转换器。
    /// 根据当前电量百分比与进度条容器的实际宽度，动态计算填充部分的像素宽度。
    /// </summary>
    public class BatteryWidthConverter : IMultiValueConverter
    {
        /// <param name="values">[0]: int 百分比, [1]: double 容器可用总宽度。</param>
        /// <returns>计算后的进度槽像素宽度值。</returns>
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length == 2 && values[0] is int percentage && values[1] is double actualWidth)
            {
                double maxWidth = actualWidth - 4; // 预留边距
                return Math.Max(0, maxWidth * (percentage / 100.0));
            }
            return 0.0;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// 电池状态颜色转换器。
    /// 根据电量百分比返回不同的颜色方案：低电量返回红色警告色，正常电量返回标准绿色。
    /// </summary>
    public class BatteryStatusColorConverter : IValueConverter
    {
        /// <param name="value">电量百分比整型值。</param>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int percentage)
            {
                // 逻辑：低于20%红值，其余绿值
                if (percentage < 20) return Brushes.IndianRed;
                return new SolidColorBrush(Color.FromRgb(76, 175, 80)); // 绿色
            }
            return Brushes.Gray; // 默认/异常色
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// 标准布尔值转可见性转换器。
    /// 将逻辑层（ViewModel）中的 bool 状态映射为 WPF UI 层的 Visibility 枚举。
    /// 常用于根据状态显隐充电图标、提示标签等元素。
    /// </summary>
    public class BooleanToVisibilityConverter : IValueConverter
    {
        /// <param name="value">布尔值：True 对应可见，False 对应折叠（不占位）。</param>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isVisible = value is bool b && b;
            return isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// 反向布尔值转可见性转换器。
    /// 逻辑与标准转换器相反：True 对应折叠，False 对应可见。
    /// 典型应用场景：当设备“离线”（IsOnline = false）时，显示“断开连接”的警告图标。
    /// </summary>
    public class InverseBoolToVisConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 当 IsOnline 为 False 时显示图标
            bool isVisible = value is bool b && !b;
            return isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}