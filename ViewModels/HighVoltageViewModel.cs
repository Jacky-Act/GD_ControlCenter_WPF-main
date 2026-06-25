using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using GD_ControlCenter_WPF.Services;
using GD_ControlCenter_WPF.ViewModels.Dialogs;
using GD_ControlCenter_WPF.Views.Dialogs;

/*
 * 文件名: HighVoltageViewModel.cs
 * 描述: 高压电源控制卡片的视图模型类。负责主界面卡片的实时监控数据同步、参数展示以及设置弹窗的调度。
 * 维护指南: 
 * 1. 实时数据同步基于 HighVoltageService 的属性变更通知，通过 Dispatcher 确保跨线程 UI 刷新的安全性。
 * 2. 设备开关逻辑（IsRunning）在关闭时仅下发 0V/0mA 指令，特意不停止轮询，以便用户观察电压跌落泄放过程。
 */

namespace GD_ControlCenter_WPF.ViewModels
{
    /// <summary>
    /// 高压电源设备卡片视图模型。
    /// </summary>
    public partial class HighVoltageViewModel : DeviceBaseViewModel
    {
        #region 1. 依赖服务与私有字段

        /// <summary>
        /// 高压电源硬件业务服务。
        /// </summary>
        private readonly HighVoltageService _hvService;

        /// <summary>
        /// JSON 配置持久化服务。
        /// </summary>
        private readonly JsonConfigService _configService;

        #endregion

        #region 2. UI 绑定属性 (监控与设定快照)

        /// <summary> 
        /// 界面显示的设定电压快照 (单位: V)。
        /// </summary>
        [ObservableProperty] private string _setVoltage = "0";

        /// <summary> 
        /// 界面显示的设定电流快照 (单位: mA)。
        /// </summary>
        [ObservableProperty] private string _setCurrent = "0";

        /// <summary> 
        /// 硬件实时反馈的电压测量值 (单位: V)。 
        /// </summary>
        [ObservableProperty] private string _monitorVoltage = "0";

        /// <summary>
        /// 硬件实时反馈的电流测量值 (单位: mA)。
        /// </summary>
        [ObservableProperty] private string _monitorCurrent = "0";

        /// <summary> 
        /// 当前硬件的通讯连接状态描述（在线/离线）。
        /// </summary>
        [ObservableProperty] private string _statusText = "离线";

        #endregion

        #region 3. 初始化

        /// <summary>
        /// 构造高压电源视图模型并注入依赖。
        /// </summary>
        public HighVoltageViewModel(HighVoltageService hvService, JsonConfigService configService)
        {
            _hvService = hvService;
            _configService = configService;

            DisplayName = "高压电源";

            // 注册底层服务的属性变更监听，实现数据自动同步
            _hvService.PropertyChanged += OnServicePropertyChanged;

            // 执行初始化加载
            LoadConfigSettings();
            UpdateMonitorData();
        }

        #endregion

        #region 4. 核心逻辑：数据同步与配置加载

        /// <summary>
        /// 响应底层 Service 属性变化。
        /// 职责：将后台线程的采样更新调度至 UI 线程。
        /// </summary>
        private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // 调度至主线程刷新 UI 绑定属性
            Application.Current.Dispatcher.Invoke(() =>
            {
                UpdateMonitorData();
            });
        }

        /// <summary>
        /// 高频刷新：同步来自硬件的实时监控数据（电压、电流）。
        /// </summary>
        private void UpdateMonitorData()
        {
            MonitorVoltage = $"{_hvService.Voltage}";
            MonitorCurrent = $"{_hvService.Current}";
            StatusText = _hvService.IsOnline ? "在线" : "离线";

            // 将电压值作为卡片主显数值（继承自基类 DisplayValue）
            DisplayValue = MonitorVoltage;
        }

        /// <summary>
        /// 低频刷新：从本地配置文件加载上次保存的设定参数。
        /// </summary>
        private void LoadConfigSettings()
        {
            var config = _configService.Load();
            SetVoltage = $"{config.LastHvVoltage}";
            SetCurrent = $"{config.LastHvCurrent}";
        }

        #endregion

        #region 5. 交互逻辑：设置弹窗与开关控制

        /// <summary>
        /// 重写基类方法：点击卡片配置图标时弹出参数设定对话框。
        /// </summary>
        protected override void ExecuteOpenDetail()
        {
            // 加载当前磁盘上的最新配置
            var currentConfig = _configService.Load();

            // 实例化模态窗口
            var window = new HvSettingWindow();

            // 构建弹窗视图模型并注入闭包回调
            var vm = new HvSettingViewModel(
                _hvService,
                _configService,
                currentConfig,
                this.IsRunning,
                () =>
                {
                    // 关闭回调：当用户在弹窗点击“应用”并校验成功后执行
                    window.Close();
                    // 重新读取磁盘配置并刷新卡片显示
                    LoadConfigSettings();
                }
            );

            window.DataContext = vm;
            window.Owner = Application.Current.MainWindow; // 居中于主窗口
            window.ShowDialog();
        }

        /// <summary>
        /// 监听卡片开关（IsRunning）属性变更。
        /// </summary>
        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            if (e.PropertyName == nameof(IsRunning))
            {
                if (IsRunning)
                {
                    // --- 开启输出逻辑 ---
                    var config = _configService.Load();                  
                    _hvService.SetHighVoltage(config.LastHvVoltage, config.LastHvCurrent);  // 按照设定值同步硬件输出状态                    
                    _hvService.Start(); // 激活 1Hz 频率的硬件查询轮询
                }
                else
                {
                    // --- 关闭输出逻辑 ---                 
                    _hvService.SetHighVoltage(0, 0);    //  下发 0 伏 0 毫安指令停止功率输出

                    // 2. 特别注意：此处【不调用】 _hvService.Stop()，
                    // 目的是保持数据回传，让用户在 UI 上能看到电压受电容影响缓慢降至 0 的安全泄放过程。
                }
            }
        }

        #endregion
    }
}