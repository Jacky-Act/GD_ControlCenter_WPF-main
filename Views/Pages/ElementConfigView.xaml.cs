using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace GD_ControlCenter_WPF.Views.Pages
{
    /// <summary>
    /// ElementConfigView.xaml 的交互逻辑
    /// </summary>
    public partial class ElementConfigView : UserControl
    {
        public ElementConfigView()
        {
            InitializeComponent();
        }

        private void UserControl_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!(e.OriginalSource is DependencyObject originalSource)) return;

            var parent = originalSource;
            while (parent != null)
            {
                // 添加对 ComboBoxItem 和 PopupRoot 的判断，防止点击下拉框时 Focus 被抢走导致选中失败
                if (parent is TextBox || parent is Button || parent is ComboBox || parent is ComboBoxItem || parent.GetType().Name == "PopupRoot")
                {
                    return;
                }
                if (parent is System.Windows.Media.Visual || parent is System.Windows.Media.Media3D.Visual3D)
                {
                    parent = VisualTreeHelper.GetParent(parent);
                }
                else
                {
                    parent = LogicalTreeHelper.GetParent(parent);
                }
            }

            // 尝试通过逻辑树再找一次，防止跨越 VisualTree
            var logicalParent = originalSource;
            while (logicalParent != null)
            {
                if (logicalParent is ComboBox || logicalParent is ComboBoxItem)
                {
                    return;
                }
                logicalParent = LogicalTreeHelper.GetParent(logicalParent);
            }

            // 点击了空白区域，将焦点转移到 UserControl 本身
            this.Focus();
        }

        private void FittingCurveComboBox_DropDownOpened(object sender, EventArgs e)
        {
            var comboBox = sender as ComboBox;
            if (comboBox != null)
            {
                var popup = comboBox.Template.FindName("PART_Popup", comboBox) as System.Windows.Controls.Primitives.Popup;
                if (popup != null)
                {
                    // 彻底覆盖 MaterialDesign 甚至 WPF 底层逻辑：通过自定义回调强制定位在上方
                    popup.Placement = System.Windows.Controls.Primitives.PlacementMode.Custom;
                    popup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
                    {
                        // 计算坐标：Y 轴偏移为负的弹窗高度，刚好贴在 ComboBox 的正上方
                        return new[] { new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point(0, -popupSize.Height), System.Windows.Controls.Primitives.PopupPrimaryAxis.None) };
                    };
                    
                    // 微微晃动一下 Offset，强迫 Popup 引擎立刻重新计算位置
                    popup.VerticalOffset += 0.001;
                    popup.VerticalOffset -= 0.001;
                }
            }
        }
    }
}
