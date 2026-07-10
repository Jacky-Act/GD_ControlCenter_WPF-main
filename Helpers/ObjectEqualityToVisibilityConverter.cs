using System.Globalization;
using System.Windows;
using System.Windows.Data;

/*
 * 文件名: ObjectEqualityToVisibilityConverter.cs
 * 描述: 本文件包含一个对象等值判断与可见性转换器（Converter），
 * 核心作用是配合 ItemsControl 实现 WPF 客户端页面的内存常驻缓存机制。
 * 只有当绑定的列表项 ViewModel 与全局激活的 CurrentPage 完全相同时，才将页面状态设为可见（Visible），其余所有非激活页面均设为折叠（Collapsed）以提升渲染性能。
 */

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
