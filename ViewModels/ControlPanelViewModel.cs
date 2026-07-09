using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Models.Spectrometer;
using GD_ControlCenter_WPF.Services;
using GD_ControlCenter_WPF.Services.Platform3D;
using GD_ControlCenter_WPF.Services.Spectrometer;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;
using System.Windows;
using static GD_ControlCenter_WPF.Models.AppConfig;

/*
 * 文件名: ControlPanelViewModel.cs
 * 描述: 仪表台面板的主控系统。
 * 架构职责:
 * 1. 统筹管理光谱仪、高压电源、蠕动泵及注射泵等多个子业务模块的生命周期。
 * 2. 内置等离子体运行状态监控逻辑，支持自动点火挽救序列（最多尝试 5 次）。
 * 3. 负责实时光谱数据的读取与展示、参考图层的冲突校验、以及自动化脚本采集的内存缓冲与批量导出。
 * 维护指南: 
 * 1. 布局高度 DashboardHeight 由外部注入，内部按 15% 比例自动计算参数栏高度。
 * 2. 脚本保存采用 List 内存缓冲策略，在软件关闭时通过 EmergencySave 机制执行强制落盘以防数据丢失。
 */

namespace GD_ControlCenter_WPF.ViewModels
{
    /// <summary>
    /// 主控制面板视图模型：集成硬件控制、状态监测与数据持久化业务逻辑。
    /// </summary>
    public partial class ControlPanelViewModel : ObservableObject
    {
        #region 1. 核心依赖与子模块 (Dependencies & Sub-Modules)

        /// <summary>
        /// 通用硬件控制服务。负责下发点火指令、控制转向阀门等底层 IO 操作。
        /// </summary>
        private readonly GeneralDeviceService _generalDeviceService;

        /// <summary>
        /// 本地 JSON 配置服务。用于读取和持久化用户设置。
        /// </summary>
        private readonly JsonConfigService _jsonConfigService;

        /// <summary>
        /// 光谱仪视图模型（子模块）。包装了光谱仪的启停逻辑与参数双向绑定。
        /// </summary>
        [ObservableProperty]
        private SpectrometerViewModel _specVM = null!;

        /// <summary>
        /// 电池视图模型（子模块）。负责电量监控 UI 的更新。
        /// </summary>
        [ObservableProperty]
        private BatteryViewModel _batteryVM;

        /// <summary>
        /// 高压电源视图模型。用于监听电流变化以判断等离子体状态。
        /// </summary>
        private readonly HighVoltageViewModel _hvVM;

        /// <summary>
        /// 蠕动泵视图模型。用于判断进液系统是否正常工作。
        /// </summary>
        private readonly PeristalticPumpViewModel _pumpVM;

        /// <summary>
        /// 注射泵视图模型。
        /// </summary>
        private readonly SyringePumpViewModel _syringeVM;

        /// <summary>
        /// 硬件控制卡片集合。用于 UI 层的 ItemsControl 动态渲染。
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<DeviceBaseViewModel> _devices = new();

        #endregion

        #region 2. UI 布局与视图状态 (UI & View State)

        /// <summary>
        /// 仪表盘区域高度。
        /// </summary>
        [ObservableProperty]
        private double _dashboardHeight;

        /// <summary>
        /// 参数信息栏高度。由 DashboardHeight 的比例动态生成。
        /// </summary>
        [ObservableProperty]
        private double _paramHeaderHeight = 35;

        /// <summary>
        /// 保存窗口计算时的原始参考高度。
        /// </summary>
        private double _originalHeight;

        /// <summary>
        /// 系统全局状态提示文本。
        /// </summary>
        [ObservableProperty]
        private string _statusInfo = "系统就绪";

        /// <summary>
        /// 转向阀门物理状态：True 为通道 1，False 为通道 2。
        /// </summary>
        [ObservableProperty]
        private bool _isSteeringValveActive;

        /// <summary>
        /// 动态文本：基于参考文件加载状态切换显示。
        /// </summary>
        public string ReferenceFileButtonText => IsReferenceFileLoaded ? "关闭文件" : "打开文件";

        /// <summary>
        /// 图表视窗坐标记忆 [XMin, XMax, YMin, YMax]。
        /// </summary>
        public double[]? SavedAxisLimits { get; set; }

        #endregion

        #region 3. 内存流转与持久化缓存 (Data Flow & Cache)

        /// <summary>
        /// 实时缓存主界面最新一帧光谱数据。
        /// </summary>
        private SpectralData? _latestSpectralData;

        /// <summary>
        /// 公开的最新光谱数据只读引用。
        /// </summary>
        public SpectralData? LatestSpectralData => _latestSpectralData;

        /// <summary>
        /// 内存中的历史参考光谱实体。
        /// </summary>
        private SpectralData? _loadedReferenceData;

        /// <summary>
        /// 公开的参考光谱数据只读引用。
        /// </summary>
        public SpectralData? LoadedReferenceData => _loadedReferenceData;

        /// <summary>
        /// 指示是否已加载外部参考文件。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ReferenceFileButtonText))]
        private bool _isReferenceFileLoaded;

        /// <summary>
        /// 标识自动化脚本采集任务是否运行中。
        /// </summary>
        [ObservableProperty]
        private bool _isScriptSaving;

        /// <summary>
        /// 脚本保存任务的取消令牌源。
        /// </summary>
        private CancellationTokenSource? _scriptSaveCts;

        /// <summary>
        /// 脚本保存全局内存缓存池，用于削峰填谷减少磁盘 IO 次数。
        /// </summary>
        private readonly System.Collections.Generic.List<SpectralData> _scriptSaveCache = new();

        /// <summary>
        /// 紧急停机标记：开启时拦截所有针对 UI 主线程的 Dispatcher 调用。
        /// </summary>
        private bool _isEmergencyShutdown = false;

        /// <summary>
        /// 硬件操作防抖锁。
        /// </summary>
        private bool _isToggling = false;

        #endregion

        #region 4. 等离子自动点火状态机 (Plasma State Machine)

        /// <summary>
        /// 标识等离子体是否已通过电流阈值验证 (>5.0mA)。
        /// </summary>
        private bool _isPlasmaStable = false;

        /// <summary>
        /// 指示系统当前是否正处于自动挽救点火序列中。
        /// </summary>
        private bool _isReigniting = false;

        /// <summary>
        /// 当前挽救序列已尝试的次数计数。
        /// </summary>
        private int _reigniteAttemptCount = 0;

        /// <summary>
        /// 最大允许挽救尝试次数。超过 5 次将触发系统强制保护断开。
        /// </summary>
        private const int MaxReigniteAttempts = 5;

        #endregion

        #region 5. 构造函数与初始化管线

        /// <summary>
        /// 构造 ControlPanelViewModel 并执行子模块注入。
        /// </summary>
        public ControlPanelViewModel(GeneralDeviceService generalDeviceService, JsonConfigService jsonConfigService, BatteryViewModel batteryVM,
            HighVoltageViewModel highVoltageVM, PeristalticPumpViewModel peristalticPumpVM, SyringePumpViewModel syringePumpVM)
        {
            _generalDeviceService = generalDeviceService;
            _jsonConfigService = jsonConfigService;
            _batteryVM = batteryVM;
            _hvVM = highVoltageVM;
            _pumpVM = peristalticPumpVM;
            _syringeVM = syringePumpVM;

            // 初始化光谱仪及子卡片集合
            InitSubModules();
            // 挂载硬件运行状态监控
            SubscribeHardwareEvents();
            // 注册全局消息处理
            RegisterGlobalMessages();
        }

        /// <summary>
        /// 初始化光谱仪包装器，并配置参数持久化委托。
        /// </summary>
        private void InitSubModules()
        {
            var appConfig = _jsonConfigService.Load();
            
            // 同步配置文件中的初始测样模式状态
            IsFlowInjectionMode = appConfig.CurrentMeasurementMode == MeasurementMode.FlowInjection;

            var specConfig = new SpectrometerConfig();

            if (appConfig.LastIntegrationTime > 0) specConfig.IntegrationTimeMs = appConfig.LastIntegrationTime;
            if (appConfig.LastAveragingCount > 0) specConfig.AveragingCount = appConfig.LastAveragingCount;

            // 初始化光谱仪包装器
            SpecVM = new SpectrometerViewModel(specConfig, null!)
            {
                SaveConfigAction = () =>
                {
                    var config = _jsonConfigService.Load();
                    config.LastIntegrationTime = SpecVM.IntegrationTimeMs;
                    config.LastAveragingCount = SpecVM.AveragingCount;
                    _jsonConfigService.Save(config);
                }
            };

            // 组装硬件卡片展示列表
            Devices.Add(_hvVM);
            Devices.Add(_pumpVM);
            Devices.Add(_syringeVM);
        }

        /// <summary>
        /// 订阅电源与泵的关键属性变化。
        /// </summary>
        private void SubscribeHardwareEvents()
        {
            _hvVM.PropertyChanged += OnHardwareStateChanged;
            _pumpVM.PropertyChanged += OnHardwareStateChanged;

            // 监听光谱仪硬件池变化
            Services.Spectrometer.SpectrometerManager.Instance.Devices.CollectionChanged += (s, e) =>
            {
                if (Services.Spectrometer.SpectrometerManager.Instance.Devices.Count == 0 && SpecVM.IsCurrentlyMeasuring)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        // 断开后自动恢复停止状态
                        SpecVM.IsCurrentlyMeasuring = false;
                        SpecVM.Model.IsConnected = false;
                        SpecVM.Model.SerialNumber = "已断开连接";
                        SpecVM.UpdateFromModel();
                        StatusInfo = "未检测到光谱仪";
                        _isToggling = false;

                        // 强制清空残余波形
                        WeakReferenceMessenger.Default.Send(new ClearWaveformMessage());
                    });
                }
            };
        }

        /// <summary>
        /// 注册全局弱引用消息，处理光谱硬件连接及光谱数据流。
        /// </summary>
        private void RegisterGlobalMessages()
        {
            // 响应光谱仪硬件的全局禁用/启用
            WeakReferenceMessenger.Default.Register<SpectrometerStatusMessage>(this, (r, m) =>
            {
                var incomingConfig = m.Value;
                if (incomingConfig.SerialNumber == "GLOBAL_SWITCH" && !incomingConfig.IsConnected)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        SpecVM.IsCurrentlyMeasuring = false;
                        SpecVM.Model.IsConnected = false;
                        SpecVM.Model.SerialNumber = "已断开连接";
                        SpecVM.UpdateFromModel();
                        StatusInfo = "光谱仪硬件已被全局禁用";
                        _isToggling = false;
                    });
                }
            });

            // 处理实时光谱数据流入，并执行波长一致性保护校验
            WeakReferenceMessenger.Default.Register<SpectralDataMessage>(this, (r, m) =>
            {
                _latestSpectralData = m.Value;

                if (IsReferenceFileLoaded && _loadedReferenceData != null)
                {
                    double[] liveX = _latestSpectralData.Wavelengths;
                    double[] fileX = _loadedReferenceData.Wavelengths;

                    // 若硬件波长范围与参考文件冲突，强制移除参考层以防显示畸变
                    if (liveX.Length != fileX.Length || Math.Abs(liveX[0] - fileX[0]) > 0.1 || Math.Abs(liveX[^1] - fileX[^1]) > 0.1)
                    {
                        _loadedReferenceData = null;
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            IsReferenceFileLoaded = false;
                            StatusInfo = "波长不匹配，已自动关闭参考光谱";
                        });
                        WeakReferenceMessenger.Default.Send(new ClearReferencePlotMessage());
                    }
                }
            });
        }

        #endregion

        #region 6. 核心业务流水线 (启停调度 & 持久化)

        /// <summary>
        /// 切换光谱采集状态。执行设备扫描、初始化及配置下发序列。
        /// </summary>
        [RelayCommand]
        private async Task ToggleSpectrometer()
        {
            var config = _jsonConfigService.Load();
            if (!config.IsSpectrometerEnabled)
            {
                StatusInfo = "请先在设置中启用光谱仪连接";
                return;
            }

            if (_isToggling) return;
            _isToggling = true;

            try
            {
                // 首次连接需执行全总线设备探测
                if (SpectrometerManager.Instance.Devices.Count == 0)
                {
                    StatusInfo = "正在扫描光谱仪...";
                    int count = await SpectrometerManager.Instance.DiscoverAndInitDevicesAsync();
                    if (count == 0) 
                    { 
                        StatusInfo = "未检测到光谱仪";
                        WeakReferenceMessenger.Default.Send(new ClearWaveformMessage());
                        return; 
                    }
                }

                var devices = SpectrometerManager.Instance.Devices;
                int deviceCount = devices.Count;

                if (!SpecVM.IsCurrentlyMeasuring)
                {
                    StatusInfo = "正在初始化硬件...";
                    bool allSuccess = await Task.Run(async () =>
                    {
                        bool success = true;
                        foreach (var service in devices)
                        {
                            bool isInitialized = service.Config.IsConnected || await service.InitializeAsync();
                            if (isInitialized)
                                await service.UpdateConfigurationAsync(SpecVM.IntegrationTimeMs, SpecVM.AveragingCount);
                            else
                            {
                                success = false;
                                break;
                            }
                        }
                        if (success) SpectrometerManager.Instance.StartAll();
                        return success;
                    });

                    if (allSuccess && devices.Any())
                    {
                        var firstDevice = devices.First();
                        SpecVM.SetService(firstDevice);
                        SpecVM.Model.SerialNumber = devices.Count == 1 ? firstDevice.Config.SerialNumber : "联机模式";
                        SpecVM.Model.IsConnected = true;
                        SpecVM.UpdateFromModel();
                        SpecVM.IsCurrentlyMeasuring = true;
                        StatusInfo = devices.Count == 1 ? $"采集进行中: {firstDevice.Config.SerialNumber}" : $"多机联机采集运行中 (共 {devices.Count} 台)";
                    }
                    else
                    {
                        StatusInfo = devices.Any() ? "部分设备初始化失败" : "硬件连接在启动时断开";
                    }
                }
                else
                {
                    StatusInfo = "正在安全停止硬件...";
                    await Task.Run(() => SpectrometerManager.Instance.StopAll());
                    SpecVM.IsCurrentlyMeasuring = false;
                    StatusInfo = "采集已停止";
                }
            }
            finally
            {
                _isToggling = false;
            }
        }

        /// <summary>
        /// 自动化脚本保存引擎。控制后台线程定时抓取最新光谱帧并暂存至内存 List。
        /// </summary>
        private void ToggleScriptSaveAsync()
        {
            if (IsScriptSaving)
            {
                _scriptSaveCts?.Cancel();
                IsScriptSaving = false;
                StatusInfo = "正在中止采集并保存已缓存数据...";
                return;
            }

            if (!SpecVM.IsCurrentlyMeasuring || _latestSpectralData == null)
            {
                StatusInfo = "请先启动光谱仪采集数据";
                return;
            }

            var config = _jsonConfigService.Load();
            int count = config.ScriptSaveCount;
            int interval = config.ScriptSaveInterval;
            string dirPath = string.IsNullOrWhiteSpace(config.ScriptSaveDirectory) || !System.IO.Directory.Exists(config.ScriptSaveDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop) : config.ScriptSaveDirectory;

            string serial = SpecVM.Model.SerialNumber == "已断开连接" ? "Unknown" : (SpecVM.Model.SerialNumber == "联机模式" ? "MultiDevice" : SpecVM.Model.SerialNumber);
            string fileName = $"Spectrum_Auto_{serial}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
            string fullPath = System.IO.Path.Combine(dirPath, fileName);

            IsScriptSaving = true;
            _isEmergencyShutdown = false;
            _scriptSaveCts = new CancellationTokenSource();
            var token = _scriptSaveCts.Token;

            float currentIntTime = SpecVM.IntegrationTimeMs;
            uint currentAvgCount = SpecVM.AveragingCount;

            _ = Task.Run(async () =>
            {
                _scriptSaveCache.Clear();
                try
                {
                    for (int i = 1; i <= count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        Application.Current.Dispatcher.Invoke(() => StatusInfo = $"脚本运行中: 正在缓存 {i}/{count} ...");

                        var dataToSave = _latestSpectralData;
                        if (dataToSave != null && dataToSave.Wavelengths != null)
                        {
                            // 执行深度拷贝存入内存池，防止后续引用被硬件更新覆盖
                            _scriptSaveCache.Add(new SpectralData
                            {
                                Wavelengths = dataToSave.Wavelengths.ToArray(),
                                Intensities = dataToSave.Intensities.ToArray(),
                                AcquisitionTime = dataToSave.AcquisitionTime
                            });
                        }
                        if (i < count) await Task.Delay(interval * 1000, token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { Application.Current.Dispatcher.Invoke(() => StatusInfo = "缓存数据时发生异常"); }
                finally
                {
                    // 业务收尾：执行批量物理写入
                    if (!_isEmergencyShutdown)
                    {
                        if (_scriptSaveCache.Count > 0)
                        {
                            Application.Current.Dispatcher.Invoke(() => StatusInfo = $"正在将 {_scriptSaveCache.Count} 帧数据写入硬盘，请稍候...");
                            using var exportService = new ExcelExportService();
                            var dataToSave = _scriptSaveCache.ToList();
                            bool success = exportService.ExportScriptSpectraBulkAsync(fullPath, dataToSave, currentIntTime, currentAvgCount).GetAwaiter().GetResult();

                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                StatusInfo = success ? $"脚本保存完成 (共 {dataToSave.Count} 帧，已存至 {fileName})" : "写入硬盘失败，请检查文件是否被占用";
                                IsScriptSaving = false;
                            });
                        }
                        else
                        {
                            Application.Current.Dispatcher.Invoke(() => { StatusInfo = "未采集到有效数据，已取消保存"; IsScriptSaving = false; });
                        }
                        _scriptSaveCache.Clear();
                    }
                }
            });
        }

        /// <summary>
        /// 当软件意外关闭时，立即终止所有任务并强制将内存池数据写入桌面 Exit 文件。
        /// </summary>
        public void EmergencySave()
        {
            if (!IsScriptSaving || _scriptSaveCache.Count == 0) return;

            _isEmergencyShutdown = true;
            IsScriptSaving = false;
            _scriptSaveCts?.Cancel();

            var config = _jsonConfigService.Load();
            string dirPath = string.IsNullOrWhiteSpace(config.ScriptSaveDirectory) ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop) : config.ScriptSaveDirectory;
            string serial = SpecVM.Model.SerialNumber == "联机模式" ? "MultiDevice" : (string.IsNullOrEmpty(SpecVM.Model.SerialNumber) ? "Unknown" : SpecVM.Model.SerialNumber);
            string fileName = $"Spectrum_Auto_{serial}_{DateTime.Now:yyyyMMdd_HHmmss}_Exit.xlsx";

            using var exportService = new ExcelExportService();
            exportService.ExportScriptSpectraBulkAsync(System.IO.Path.Combine(dirPath, fileName), _scriptSaveCache.ToList(), SpecVM.IntegrationTimeMs, SpecVM.AveragingCount).GetAwaiter().GetResult();
        }

        #endregion

        #region 7. UI 控制与通用对话框命令 (Commands)

        /// <summary> 拦截转向阀 UI 开关，同步下发至物理 IO 寄存器。 </summary>
        partial void OnIsSteeringValveActiveChanged(bool value) => _generalDeviceService.ControlSteeringValve(value);

        /// <summary> 执行单次点火脉冲命令。 </summary>
        [RelayCommand] private void Fire() => _generalDeviceService.Fire();

        /// <summary> 发送全局消息请求图表视窗自适应缩放。 </summary>
        [RelayCommand] private void AutoRange() => WeakReferenceMessenger.Default.Send(new AutoRangeRequestMessage());

        /// <summary> 发送全局消息请求恢复图表最大极限视野。 </summary>
        [RelayCommand] private void MaxRange() => WeakReferenceMessenger.Default.Send(new MaxRangeRequestMessage());

        /// <summary> 启动三维平台控制与自动校准对话框。 </summary>
        [RelayCommand]
        private void OpenPlatform3DWindow()
        {
            var existingWindow = System.Windows.Application.Current.Windows.OfType<GD_ControlCenter_WPF.Views.Dialogs.Platform3DWindow>().FirstOrDefault();
            if (existingWindow != null)
            {
                if (existingWindow.WindowState == System.Windows.WindowState.Minimized)
                {
                    existingWindow.WindowState = System.Windows.WindowState.Normal;
                }
                existingWindow.Activate();
                return;
            }

            var platformService = App.Services.GetRequiredService<IPlatform3DService>();
            var calibrationService = App.Services.GetRequiredService<PlatformCalibrationService>();
            var jsonConfigService = App.Services.GetRequiredService<JsonConfigService>();
            var peakTrackingService = App.Services.GetRequiredService<PeakTrackingService>();

            var window = new GD_ControlCenter_WPF.Views.Dialogs.Platform3DWindow();
            var vm = new GD_ControlCenter_WPF.ViewModels.Dialogs.Platform3DViewModel(
                platformService, calibrationService, jsonConfigService, peakTrackingService, () => window.Close());

            window.DataContext = vm;
            window.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;

            // 获取当前主窗口
            var mainWindow = System.Windows.Application.Current.MainWindow;

            // 定义主窗口关闭时的联动动作：关掉当前的三维平台窗口
            EventHandler mainWindowClosedHandler = (sender, e) =>
            {
                window.Close();
            };

            if (mainWindow != null)
            {
                // 绑定主窗口关闭事件
                mainWindow.Closed += mainWindowClosedHandler;
            }

            // 订阅窗口自身的关闭事件
            window.Closed += (sender, e) =>
            {               

                // 如果子窗口自己先被关闭了，必须解绑主窗口的事件，防止内存泄漏
                if (mainWindow != null)
                {
                    mainWindow.Closed -= mainWindowClosedHandler;
                }
            };

            vm.SavePreferences();
            window.Show();
        }

        /// <summary> 打开积分时间调整对话框。 </summary>
        [RelayCommand]
        private void OpenIntegrationTimeConfig()
        {
            var window = new GD_ControlCenter_WPF.Views.Dialogs.SpecParamSettingWindow();
            window.DataContext = new GD_ControlCenter_WPF.ViewModels.Dialogs.SpecParamSettingViewModel(
                "积分时间设置", "参数配置 - 积分时间 (ms)", SpecVM.IntegrationTimeMs, SpecVM.Model.MinIntegrationTime, 600000, 5.0,
                (val) => SpecVM.ChangeIntegrationTimeCommand.Execute(val), () => window.Close());
            window.Owner = Application.Current.MainWindow;
            window.ShowDialog();
        }

        /// <summary> 打开平均次数调整对话框。 </summary>
        [RelayCommand]
        private void OpenAveragingCountConfig()
        {
            var window = new GD_ControlCenter_WPF.Views.Dialogs.SpecParamSettingWindow();
            window.DataContext = new GD_ControlCenter_WPF.ViewModels.Dialogs.SpecParamSettingViewModel(
                "平均次数设置", "参数配置 - 平均次数", SpecVM.AveragingCount, 1, 10000, 1.0,
                (val) => SpecVM.ChangeAveragingCountCommand.Execute(val), () => window.Close());
            window.Owner = Application.Current.MainWindow;
            window.ShowDialog();
        }

        /// <summary> 执行单帧光谱 Excel 导出业务。 </summary>
        [RelayCommand]
        private async Task SingleSaveAsync()
        {
            if (_latestSpectralData?.Wavelengths == null || _latestSpectralData.Wavelengths.Length == 0)
            {
                StatusInfo = "暂无光谱数据可保存";
                return;
            }
            try
            {
                StatusInfo = "正在导出单幅光谱...";
                var config = _jsonConfigService.Load();
                string dirPath = string.IsNullOrWhiteSpace(config.SingleSaveExportPath) || !System.IO.Directory.Exists(config.SingleSaveExportPath)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop) : config.SingleSaveExportPath;
                string serial = SpecVM.Model.SerialNumber == "联机模式" ? "MultiDevice" : (string.IsNullOrEmpty(SpecVM.Model.SerialNumber) ? "Unknown" : SpecVM.Model.SerialNumber);

                using var exportService = new ExcelExportService();
                await exportService.ExportSingleSpectrumAsync(System.IO.Path.Combine(dirPath, $"Spectrum_{serial}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"), _latestSpectralData.AcquisitionTime, SpecVM.IntegrationTimeMs, SpecVM.AveragingCount, _latestSpectralData.Wavelengths, _latestSpectralData.Intensities);
                StatusInfo = "单幅保存成功";
            }
            catch (Exception) { StatusInfo = "单幅保存失败"; }
        }

        /// <summary> 调度本地参考光谱加载业务。包含文件读取、格式校验及波长匹配检查。 </summary>
        [RelayCommand]
        private async Task ToggleReferenceFileAsync()
        {
            if (IsReferenceFileLoaded)
            {
                _loadedReferenceData = null;
                IsReferenceFileLoaded = false;
                WeakReferenceMessenger.Default.Send(new ClearReferencePlotMessage());
                StatusInfo = "已关闭参考光谱";
            }
            else
            {
                var config = _jsonConfigService.Load();
                var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Excel Files (*.xlsx)|*.xlsx", Title = "选择参考光谱文件" };
                if (!string.IsNullOrWhiteSpace(config.SingleSaveExportPath) && System.IO.Directory.Exists(config.SingleSaveExportPath))
                    dialog.InitialDirectory = config.SingleSaveExportPath;

                if (dialog.ShowDialog() == true)
                {
                    StatusInfo = "正在读取文件...";
                    using var service = new ExcelExportService();
                    var parsedData = await service.ImportSingleSpectrumAsync(dialog.FileName);

                    if (parsedData == null) { StatusInfo = "文件读取失败或格式不符合要求"; return; }
                    if (SpecVM.IsCurrentlyMeasuring && _latestSpectralData?.Wavelengths != null)
                    {
                        if (_latestSpectralData.Wavelengths.Length != parsedData.Wavelengths.Length || Math.Abs(_latestSpectralData.Wavelengths[0] - parsedData.Wavelengths[0]) > 0.1)
                        {
                            StatusInfo = "加载失败：该文件的波长范围与当前运行的硬件不匹配";
                            return;
                        }
                    }
                    _loadedReferenceData = parsedData;
                    IsReferenceFileLoaded = true;
                    WeakReferenceMessenger.Default.Send(new LoadReferencePlotMessage(_loadedReferenceData));
                    StatusInfo = $"已加载参考光谱: {parsedData.AcquisitionTime:HH:mm:ss}";
                }
            }
        }

        /// <summary> 弹出自动化脚本保存配置弹窗。 </summary>
        [RelayCommand]
        private void OpenScriptSaveWindow()
        {
            var window = new GD_ControlCenter_WPF.Views.Dialogs.ScriptSaveWindow();
            window.DataContext = new GD_ControlCenter_WPF.ViewModels.Dialogs.ScriptSaveViewModel(_jsonConfigService, _jsonConfigService.Load(), IsScriptSaving, () => window.Close(), () => { ToggleScriptSaveAsync(); });
            window.Owner = Application.Current.MainWindow;
            window.ShowDialog();
        }

        /// <summary> 接收来自外部的可用显示高度，以此计算仪表盘与参数头部的比例布局。 </summary>
        public void SetInitialHeight(double height)
        {
            DashboardHeight = height;
            _originalHeight = height;
            ParamHeaderHeight = Math.Max(35, height * 0.15);
        }

        /// <summary> 广播同步属性：确保子弹窗能够实时感应后台采集状态。 </summary>
        partial void OnIsScriptSavingChanged(bool value) => WeakReferenceMessenger.Default.Send(new ScriptSaveStateChangedMessage(value));

        #endregion

        #region 8. 自动点火核心控制逻辑 (Hardware Event Callbacks)

        /// <summary> 响应硬件关键属性变化回调，驱动等离子体监控状态机。 </summary>
        private void OnHardwareStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(HighVoltageViewModel.MonitorCurrent) ||
                e.PropertyName == nameof(HighVoltageViewModel.IsRunning) ||
                e.PropertyName == nameof(PeristalticPumpViewModel.IsRunning))
            {
                CheckPlasmaState();
            }
        }

        /// <summary> 核心状态监测：通过电流大小 (>5.0mA 为稳，<1.0mA 为灭) 判定燃烧状态，并自动触发异常挽救。 </summary>
        private void CheckPlasmaState()
        {
            if (!_hvVM.IsRunning || !_pumpVM.IsRunning)
            {
                _isPlasmaStable = false;
                _reigniteAttemptCount = 0;
                return;
            }

            if (double.TryParse(_hvVM.MonitorCurrent, out double current))
            {
                if (current > 5.0)
                {
                    if (!_isPlasmaStable)
                    {
                        _isPlasmaStable = true;
                        _reigniteAttemptCount = 0;
                        _isReigniting = false;
                        StatusInfo = "等离子体已稳定运行";
                    }
                }
                else if (current <= 1.0 && _isPlasmaStable && !_isReigniting)
                {
                    _isPlasmaStable = false;
                    _ = HandleUnexpectedExtinctionAsync();
                }
            }
        }

        /// <summary> 意外熄火挽救异步流水线。执行延时等待进液、重复点火 Fire 动作，并具备硬件手动中断自毁机制。 </summary>
        private async Task HandleUnexpectedExtinctionAsync()
        {
            _isReigniting = true;
            if (!_jsonConfigService.Load().IsAutoReigniteEnabled)
            {
                StatusInfo = "意外熄火 (未开启自动点火)";
                _isReigniting = false;
                return;
            }

            while (_reigniteAttemptCount < MaxReigniteAttempts)
            {
                _reigniteAttemptCount++;
                StatusInfo = $"意外熄火，准备尝试第 {_reigniteAttemptCount} 次自动点火...";
                await Task.Delay(2000); // 进液缓冲

                if (!_hvVM.IsRunning || !_pumpVM.IsRunning)
                {
                    StatusInfo = "自动点火已取消 (硬件已手动关闭)";
                    _isReigniting = false;
                    return;
                }

                StatusInfo = $"正在执行第 {_reigniteAttemptCount} 次自动点火...";
                _generalDeviceService.Fire();
                await Task.Delay(3000); // 点火反应时间

                if (double.TryParse(_hvVM.MonitorCurrent, out double current) && current > 5.0)
                {
                    _isReigniting = false;
                    _isPlasmaStable = true;
                    _reigniteAttemptCount = 0;
                    StatusInfo = "等离子体已稳定运行";
                    return;
                }
            }

            StatusInfo = "多次自动点火失败，为保障安全已切断输出";
            _hvVM.IsRunning = _pumpVM.IsRunning = false;
            _isReigniting = false;
        }

        #endregion
        [ObservableProperty]
        private bool _isFlowInjectionMode;

        // 3. 定义命令方法，名称要对齐
        [RelayCommand]
        public void ToggleMeasurementMode()
        {
            var config = _jsonConfigService.Load();
            var mode = IsFlowInjectionMode ? MeasurementMode.FlowInjection : MeasurementMode.Continuous;
            config.CurrentMeasurementMode = mode;
            _jsonConfigService.Save(config);

            WeakReferenceMessenger.Default.Send(new MeasurementModeChangedMessage(mode));
        }
    }
}