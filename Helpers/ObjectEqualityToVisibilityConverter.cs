using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GD_ControlCenter_WPF.Helpers
{
    /// <summary>
    /// 用于配合 ItemsControl 实现页面缓存的转换器。
    /// 只有当列表中的 ViewModel 与当前激活的 CurrentPage 完全相同时，才显示该页面，其余隐藏。
    /// </summary>
    public class ObjectEqualityToVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values != null && values.Length == 2)
            {
                // values[0] 是当前 Item 的 ViewModel
                // values[1] 是 MainViewModel.CurrentPage
                if (values[0] == values[1])
                    return Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
