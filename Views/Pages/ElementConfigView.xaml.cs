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
