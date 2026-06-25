using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Models.Spectrometer;
using GD_ControlCenter_WPF.Services;
using GD_ControlCenter_WPF.Services.Spectrometer;
using System.Collections.ObjectModel;
using System.Windows;

/*
 * 文件名: SettingsViewModel.cs
 * 描述: 系统全局设置视图模型。负责管理串口通讯配置、光谱仪全局使能开关、自动点火延时参数以及各类数据导出路径的持久化。
 * 维护指南: 
 * 1. 串口通讯采用固定波特率 9600。
 * 2. 点火延时参数（IgnitionDelaySeconds）范围限制在 1.0s - 5.0s 之间，步进为 0.1s。
 * 3. 所有的路径设置在首次为空时均会自动回退至系统桌面路径。
 * 4. 光谱仪使能切换时涉及硬件连接/断开的重型操作，已通过后台任务异步处理。
 */

namespace GD_ControlCenter_WPF.ViewModels
{
    /// <summary>
    /// 全局设置页面视图模型。
    /// </summary>
    public partial class SettingsViewModel : ObservableObject
    {
        #region 1. 依赖服务与物理常量

        /// <summary>
        /// 串口通讯核心服务。
        /// </summary>
        private readonly ISerialPortService _serialService;

        /// <summary>
        /// JSON 配置管理服务。
        /// </summary>
        private readonly JsonConfigService _configService;

        /// <summary>
        /// 通讯波特率常量：9600。
        /// </summary>
        private const int BAUD_RATE = 9600;

        #endregion

        #region 2. UI 绑定属性 (串口与连接状态)

        /// <summary>
        /// 系统当前识别到的所有可用串口名称集合。
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<string> _availablePorts = new();

        /// <summary>
        /// 用户在下拉列表中选中的目标串口号。
        /// </summary>
        [ObservableProperty]
        private string? _selectedPort;

        /// <summary>
        /// 界面显示的串口连接状态描述文本（如：“已连接 (COM2)”）。
        /// </summary>
        [ObservableProperty]
        private string _statusText = "未连接";

        #endregion

        #region 3. UI 绑定属性 (功能配置与路径)

        /// <summary>
        /// 全局光谱仪使能开关。
        /// 开启时触发硬件扫描与初始化，关闭时执行全线断开。
        /// </summary>
        [ObservableProperty]
        private bool _isSpectrometerEnabled;

        /// <summary>
        /// 是否开启自动点火重连逻辑。
        /// </summary>
        [ObservableProperty]
        private bool _isAutoReigniteEnabled;

        /// <summary>
        /// 点火触发后的安全等待延时（单位：秒）。
        /// </summary>
        [ObservableProperty]
        private double _ignitionDelaySeconds;

        /// <summary>
        /// 实时光谱单帧保存的默认导出文件夹路径。
        /// </summary>
        [ObservableProperty]
        private string _singleSaveExportPath = string.Empty;

        /// <summary>
        /// 时序图数据自动保存的目录路径。
        /// </summary>
        [ObservableProperty]
        private string _timeSeriesSaveDirectory = string.Empty;

        #endregion

        #region 4. 初始化

        /// <summary>
        /// 构造设置页面视图模型。
        /// </summary>
        /// <param name="serialService">串口服务。</param>
        /// <param name="configService">配置服务。</param>
        public SettingsViewModel(ISerialPortService serialService, JsonConfigService configService)
        {
            _serialService = serialService;
            _configService = configService;

            // 注册串口底层状态变更的回调
            _serialService.ConnectionStatusChanged += OnConnectionStatusChanged;

            // 恢复历史配置状态
            var config = _configService.Load();
            IsSpectrometerEnabled = config.IsSpectrometerEnabled;
            IsAutoReigniteEnabled = config.IsAutoReigniteEnabled;
            IgnitionDelaySeconds = config.IgnitionDelaySeconds;

            // 路径初始化策略：若配置为空，默认指向桌面
            SingleSaveExportPath = string.IsNullOrWhiteSpace(config.SingleSaveExportPath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                : config.SingleSaveExportPath;

            TimeSeriesSaveDirectory = string.IsNullOrWhiteSpace(config.TimeSeriesSaveDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                : config.TimeSeriesSaveDirectory;

            // 初始刷新物理串口列表
            RefreshPorts();
        }

        #endregion

        #region 5. 属性变更拦截器 (业务逻辑联动)

        /// <summary>
        /// 拦截光谱仪开关状态变更。
        /// 职责：同步持久化配置，并驱动硬件管理器执行异步连断逻辑。
        /// </summary>
        partial void OnIsSpectrometerEnabledChanged(bool value)
        {
            var config = _configService.Load();
            config.IsSpectrometerEnabled = value;
            _configService.Save(config);

            if (value)
            {
                // 开启：启动异步硬件发现与初始化流程
                _ = SpectrometerManager.Instance.DiscoverAndInitDevicesAsync();
            }
            else
            {
                // 关闭：在后台线程执行断开清理，防止由于 USB 句柄释放导致的 UI 响应迟钝
                _ = Task.Run(() =>
                {
                    SpectrometerManager.Instance.DisconnectAll();
                });
            }

            // 构建广播消息，通知系统其余组件光谱总开关状态变化
            var globalStatusConfig = new SpectrometerConfig
            {
                SerialNumber = "GLOBAL_SWITCH",
                IsConnected = value
            };

            WeakReferenceMessenger.Default.Send(new SpectrometerStatusMessage(globalStatusConfig));
        }

        /// <summary>
        /// 拦截点火开关状态变更。
        /// </summary>
        partial void OnIsAutoReigniteEnabledChanged(bool value)
        {
            var config = _configService.Load();
            config.IsAutoReigniteEnabled = value;
            _configService.Save(config);
        }

        /// <summary>
        /// 拦截点火延时参数变更。
        /// 职责：强制执行数值物理边界限制并执行持久化。
        /// </summary>
        partial void OnIgnitionDelaySecondsChanged(double value)
        {
            // 物理安全边界：1.0s - 5.0s
            if (value < 1.0) IgnitionDelaySeconds = 1.0;
            else if (value > 5.0) IgnitionDelaySeconds = 5.0;
            else
            {
                var config = _configService.Load();
                config.IgnitionDelaySeconds = Math.Round(value, 1); // 保证一秒一位精度
                _configService.Save(config);
            }
        }

        #endregion

        #region 6. 串口通讯逻辑

        /// <summary>
        /// 响应底层连接状态变更。
        /// </summary>
        private void OnConnectionStatusChanged(bool isOpen)
        {
            Application.Current.Dispatcher.Invoke(UpdateStatusUI);
        }

        /// <summary>
        /// 刷新可用串口列表。
        /// 包含自动尝试连接逻辑。
        /// </summary>
        [RelayCommand]
        private void RefreshPorts()
        {
            var ports = _serialService.GetAvailablePorts();
            AvailablePorts.Clear();
            foreach (var port in ports)
            {
                AvailablePorts.Add(port);
            }

            // 若当前未连接且检测到串口，执行自动重连逻辑
            if (!_serialService.IsOpen)
            {
                _serialService.AutoConnect();
            }

            // 更新选中项显示
            if (_serialService.IsOpen && _serialService.CurrentPortName != null && AvailablePorts.Contains(_serialService.CurrentPortName))
            {
                SelectedPort = _serialService.CurrentPortName;
            }
            else if (AvailablePorts.Count > 0)
            {
                SelectedPort = AvailablePorts[0];
            }
            else
            {
                SelectedPort = null;
            }

            UpdateStatusUI();
        }

        /// <summary>
        /// 执行手动串口连接请求。
        /// </summary>
        [RelayCommand]
        private void Connect()
        {
            if (string.IsNullOrEmpty(SelectedPort)) return;

            // 状态防重：若已连接当前选中的串口则忽略
            if (_serialService.IsOpen && _serialService.CurrentPortName == SelectedPort) return;

            // 互斥：先关闭旧连接再尝试新连接
            if (_serialService.IsOpen) _serialService.Close();

            if (!_serialService.Open(SelectedPort, BAUD_RATE))
            {
                MessageBox.Show($"无法打开串口 {SelectedPort}，该设备可能正在被其他程序占用。", "连接冲突", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            UpdateStatusUI();
        }

        /// <summary>
        /// 同步连接状态文本至 UI。
        /// </summary>
        private void UpdateStatusUI()
        {
            StatusText = _serialService.IsOpen
                ? $"已连接 ({_serialService.CurrentPortName ?? "未知"})"
                : "未连接";
        }

        #endregion

        #region 7. 导出路径管理

        /// <summary>
        /// 浏览并保存单次光谱保存路径。
        /// </summary>
        [RelayCommand]
        private void BrowseExportPath()
        {
            var dialog = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog
            {
                Description = "选择单次采样数据的导出目录",
                UseDescriptionForTitle = true,
                SelectedPath = SingleSaveExportPath
            };

            if (dialog.ShowDialog() == true)
            {
                SingleSaveExportPath = dialog.SelectedPath;
                var config = _configService.Load();
                config.SingleSaveExportPath = dialog.SelectedPath;
                _configService.Save(config);
            }
        }

        /// <summary>
        /// 浏览并保存时序图自动备份路径。
        /// </summary>
        [RelayCommand]
        private void BrowseTimeSeriesDirectory()
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择时序监测数据的备份目录",
                UseDescriptionForTitle = true,
                SelectedPath = TimeSeriesSaveDirectory
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                TimeSeriesSaveDirectory = dialog.SelectedPath;
                var config = _configService.Load();
                config.TimeSeriesSaveDirectory = TimeSeriesSaveDirectory;
                _configService.Save(config);
            }
        }

        #endregion

        #region 8. 参数精细化调整指令

        /// <summary>
        /// 增加点火延时时间（+0.1s）。
        /// </summary>
        [RelayCommand]
        private void IncreaseIgnitionDelay()
        {
            IgnitionDelaySeconds = Math.Round(Math.Min(5.0, IgnitionDelaySeconds + 0.1), 1);
        }

        /// <summary>
        /// 减少点火延时时间（-0.1s）。
        /// </summary>
        [RelayCommand]
        private void DecreaseIgnitionDelay()
        {
            IgnitionDelaySeconds = Math.Round(Math.Max(1.0, IgnitionDelaySeconds - 0.1), 1);
        }

        #endregion
    }
}