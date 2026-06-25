using GD_ControlCenter_WPF.Services;
using GD_ControlCenter_WPF.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;

/*
 * 文件名: MainWindow.xaml.cs
 * 描述: 软件主窗口的交互逻辑类。
 * 负责主界面的初始化、全局 DataContext 的绑定、以及基于屏幕比例的窗口尺寸自适应与状态持久化。
 * 维护指南: 
 * 1. 窗口尺寸恢复逻辑需优先考虑不同分辨率下的显示安全（使用 SystemParameters.WorkArea）。
 * 2. 窗口关闭时使用 RestoreBounds 记录尺寸，以确保在最大化状态下关闭软件也能正确记忆还原尺寸。
 * 3. 页面初始化的 DashboardHeight 比例（35%）若需调整，应同步修改 ApplyScreenProportionalSize 里的硬编码值。
 */

namespace GD_ControlCenter_WPF.Views
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑：控制软件顶层窗口的行为与生命周期。
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// 初始化主窗口实例。
        /// 执行 IoC 容器绑定、尺寸恢复及事件挂载。
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();

            // 从全局 IoC 容器中提取单例 MainViewModel 并注入 DataContext
            this.DataContext = App.Services.GetRequiredService<MainViewModel>();

            // 执行屏幕比例计算与历史状态恢复
            ApplyScreenProportionalSize();

            // 注册窗口关闭事件，用于执行参数持久化
            this.Closing += MainWindow_Closing;
        }

        /// <summary>
        /// 核心显示逻辑：按照屏幕比例初始化窗口，并从配置文件中恢复上次关闭时的位置与尺寸。
        /// </summary>
        private void ApplyScreenProportionalSize()
        {
            // 获取配置管理服务并加载持久化实体
            var configService = App.Services.GetRequiredService<JsonConfigService>();
            var config = configService.Load();

            // 物理环境探测：获取主屏幕除去任务栏后的可用工作区尺寸
            double screenWidth = SystemParameters.WorkArea.Width;
            double screenHeight = SystemParameters.WorkArea.Height;

            // 比例规则定义：设定初始建议尺寸比例
            double widthRatio = 0.80;  // 宽度占屏幕 80%
            double heightRatio = 0.72; // 高度占屏幕 72%

            // 计算目标尺寸基准值
            double targetWidth = screenWidth * widthRatio;
            double targetHeight = screenHeight * heightRatio;

            // 防呆保护：设定窗口的物理最小尺寸，防止用户缩小至内容不可见
            this.MinWidth = targetWidth;
            this.MinHeight = targetHeight;

            // 应用恢复逻辑：若配置文件中的尺寸合法，则优先使用；否则使用计算出的比例基准值
            this.Width = Math.Max(targetWidth, config.MainWindowWidth);
            this.Height = Math.Max(targetHeight, config.MainWindowHeight);

            // 状态恢复：若上次是以最大化状态关闭，则自动进入最大化模式
            if (config.IsMainWindowMaximized)
            {
                this.WindowState = WindowState.Maximized;
            }

            // 业务逻辑下发：将关键的 Dashboard 初始高度（占 35%）同步至主视图模型
            if (this.DataContext is MainViewModel vm)
            {
                double initialDashboardHeight = targetHeight * 0.35;
                vm.DashboardHeight = initialDashboardHeight;
                vm.ControlPanelVM.SetInitialHeight(initialDashboardHeight);
            }
        }

        /// <summary>
        /// 窗口关闭回调：在进程结束前，将当前的窗口布局状态（尺寸、最大化标识）同步至本地 JSON。
        /// </summary>
        /// <param name="sender">触发源。</param>
        /// <param name="e">取消事件参数。</param>
        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            var configService = App.Services.GetRequiredService<JsonConfigService>();

            // 重新载入最新配置，防止覆盖其他并发修改项
            var config = configService.Load();

            // 记录当前是否处于最大化状态
            config.IsMainWindowMaximized = (this.WindowState == WindowState.Maximized);

            // 特殊逻辑：即便在最大化状态下，也应记录其“正常模式”下的真实尺寸
            // RestoreBounds 存储了窗口在进入 Maximize 之前的物理矩形信息
            if (this.RestoreBounds.Width > 0 && this.RestoreBounds.Height > 0)
            {
                config.MainWindowWidth = this.RestoreBounds.Width;
                config.MainWindowHeight = this.RestoreBounds.Height;
            }

            // 执行物理落盘
            configService.Save(config);
        }
    }
}