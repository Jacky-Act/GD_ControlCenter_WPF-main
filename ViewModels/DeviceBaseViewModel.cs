using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/*
 * 文件名: DeviceBaseViewModel.cs
 * 描述: 硬件控制卡片的抽象基类。
 * 架构职责: 
 * 1. 统一封装所有硬件设备卡片（高压电源、蠕动泵、注射泵）的共有 UI 行为。
 * 2. 提供标准化状态管理：包含运行使能、实时数值映射、忙碌状态锁定及交互文本管理。
 * 维护指南: 
 * 1. IsBusy 属性是核心安全开关：当其为 True 时，会自动通过 NotifyCanExecuteChangedFor 禁用右侧 Toggle 按钮。
 * 2. OnIsRunningChanged 拦截器仅在非忙碌状态下重置按钮文字，若子类正在执行自定义自动化序列（如注射泵），需通过修改 ButtonText 覆盖默认值。
 */

namespace GD_ControlCenter_WPF.ViewModels
{
    /// <summary>
    /// 硬件控制卡片基类：定义设备状态机与基础交互指令。
    /// </summary>
    public abstract partial class DeviceBaseViewModel : ObservableObject
    {
        #region 1. UI 绑定属性 (状态与显示)

        /// <summary>
        /// 设备的显示名称（例如：“高压电源”、“注射泵”）。
        /// 用于在卡片顶部标题区域展示。
        /// </summary>
        [ObservableProperty]
        private string? _displayName;

        /// <summary>
        /// 设备的物理运行状态标识。
        /// 绑定至卡片右侧的切换开关，驱动底层的 Start/Stop 业务逻辑。
        /// </summary>
        [ObservableProperty]
        private bool _isRunning;

        /// <summary>
        /// 界面呈现的实时测量数值（例如：“1500 V” 或 “50.0 RPM”）。
        /// 该属性通常由 Service 层的轮询回调触发更新。
        /// </summary>
        [ObservableProperty]
        private string? _displayValue;

        /// <summary>
        /// 启停按钮上显示的动态交互文本。
        /// 默认在“启动”与“停止”间切换，支持子类执行自动化任务时自定义文本。
        /// </summary>
        [ObservableProperty]
        private string _buttonText = "启动";

        /// <summary>
        /// 业务忙碌状态锁。
        /// 当设备处于不可中断的自动化流程（如注射泵进样）时设为 True，用于锁定 UI 交互。
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
        private bool _isBusy;

        #endregion

        #region 2. 交互命令

        /// <summary>
        /// 详情配置指令。
        /// 绑定至卡片左侧点击区域，用于触发各设备特有的参数设定弹窗。
        /// </summary>
        public IRelayCommand OpenDetailCommand { get; }

        /// <summary>
        /// 物理启停控制指令。
        /// 具备可用性检测逻辑：当 <see cref="IsBusy"/> 为 True 时，关联的 UI 按钮将自动进入灰化不可用状态。
        /// </summary>
        public IRelayCommand ToggleCommand { get; }

        #endregion

        #region 3. 构造函数

        /// <summary>
        /// 初始化基类指令集。
        /// </summary>
        protected DeviceBaseViewModel()
        {
            // 路由至子类实现的具体弹窗逻辑
            OpenDetailCommand = new RelayCommand(ExecuteOpenDetail);

            // 路由至启停取反逻辑，并挂载忙碌状态检查
            ToggleCommand = new RelayCommand(() => IsRunning = !IsRunning, () => !IsBusy);
        }

        #endregion

        #region 4. 属性变更拦截器 (Hooks)

        /// <summary>
        /// 拦截运行状态变更，自动驱动 UI 文本反馈。
        /// </summary>
        /// <param name="value">更新后的物理运行状态。</param>
        partial void OnIsRunningChanged(bool value)
        {
            // 安全策略：仅在没有特殊自动化任务占用（非忙碌）时，才执行通用的文字切换
            // 确保高压电源、蠕动泵等常规设备在点击后能正常显示“停止”
            if (!IsBusy)
            {
                ButtonText = value ? "停止" : "启动";
            }
        }

        #endregion

        #region 5. 抽象业务契约

        /// <summary>
        /// 由具体的设备子类实现，负责处理对应参数设置窗口的实例化与 DataContext 绑定。
        /// </summary>
        protected abstract void ExecuteOpenDetail();

        #endregion
    }
}