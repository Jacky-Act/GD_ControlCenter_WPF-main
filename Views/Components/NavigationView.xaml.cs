using System.Windows.Controls;
using GD_ControlCenter_WPF.ViewModels;
using GD_ControlCenter_WPF.Models.Messages;
using CommunityToolkit.Mvvm.Messaging;

/*
 * 文件名: NavigationView.xaml.cs
 * 描述: 侧边导航栏组件的交互逻辑类。
 * 负责管理主功能区域（MainTabControl）与底部工具区域（BottomTabControl）之间的页面切换与互斥选中逻辑。
 * 维护指南: 
 * 1. 页面切换通过直接修改 MainViewModel 的 CurrentPage 属性实现，依赖于 DataTemplate 进行视图映射。
 * 2. 两个 TabControl 之间存在互斥逻辑，确保同一时刻只有一个导航项处于激活状态。
 * 3. 增加新页面时，需同步在 XAML 的 TabItem 顺序与 SelectionChanged 的 switch 分支中进行维护。
 */

namespace GD_ControlCenter_WPF.Views.Components
{
    /// <summary>
    /// NavigationView.xaml 的交互逻辑：处理导航菜单的点击行为与视图状态流转。
    /// </summary>
    public partial class NavigationView : UserControl
    {
        public NavigationView()
        {
            InitializeComponent();
            
            // 监听全局导航消息，使得外部跳转（如从样品序列跳到测样分析）时，侧边栏能够同步高亮
            WeakReferenceMessenger.Default.Register<NavigateMessage>(this, (r, m) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (m.Value == "AnalysisWorkstation")
                    {
                        MainTabControl.SelectedIndex = 4;
                    }
                });
            });
        }

        /// <summary>
        /// 处理上方主功能导航项的选择变更事件。
        /// </summary>
        /// <param name="sender">触发事件的 MainTabControl 对象。</param>
        /// <param name="e">选择变更参数。</param>
        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 防死循环保护：若当前选中项被清空（SelectedIndex 为 -1），则终止后续逻辑，避免递归触发。
            if (MainTabControl.SelectedIndex == -1) return;

            // 核心互斥逻辑：当用户点击上方主功能区时，强制清空下方工具区的选中状态。
            if (BottomTabControl != null)
            {
                BottomTabControl.SelectedIndex = -1;
            }

            // 视图流转逻辑：将选中的索引映射至 MainViewModel 中对应的具体 ViewModel。
            if (this.DataContext is MainViewModel vm)
            {
                switch (MainTabControl.SelectedIndex)
                {
                    case 0: vm.CurrentPage = vm.ControlPanelVM; break;
                    case 1: vm.CurrentPage = vm.TimeSeriesVM; break;
                    case 2: vm.CurrentPage = vm.ElementConfigVM; break;
                    case 3: vm.CurrentPage = vm.SampleSequenceVM; break; 
                    case 4: vm.CurrentPage = vm.AnalysisWorkstationVM; break;
                    case 5: vm.CurrentPage = vm.DataProcessingVM; break; 
                    case 6: vm.CurrentPage = vm.ReportGenerationVM; break; 
                }
            }
        }

        /// <summary>
        /// 处理下方工具（设置/帮助）导航项的选择变更事件。
        /// </summary>
        /// <param name="sender">触发事件的 BottomTabControl 对象。</param>
        /// <param name="e">选择变更参数。</param>
        private void BottomTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 防死循环保护。
            if (BottomTabControl.SelectedIndex == -1) return;

            // 核心互斥逻辑：当用户点击下方工具区时，强制清空上方主功能区的选中状态。
            if (MainTabControl != null)
            {
                MainTabControl.SelectedIndex = -1;
            }

            // 执行页面切换逻辑。
            if (this.DataContext is MainViewModel vm)
            {
                // 通过 SelectedItem 进行精确匹配，避免因索引调整导致的逻辑错误。
                if (BottomTabControl.SelectedItem == SettingsTab)
                {
                    vm.CurrentPage = vm.SettingsVM; // 切换至全局设置页面。
                }
                else if (BottomTabControl.SelectedItem == HelpTab)
                {
                    // 预留帮助页面入口。
                    // vm.CurrentPage = vm.HelpVM; 
                }
            }
        }
    }
}