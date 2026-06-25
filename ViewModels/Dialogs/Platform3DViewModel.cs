using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Models.Platform3D;
using GD_ControlCenter_WPF.Services;
using GD_ControlCenter_WPF.Services.Platform3D;
using GD_ControlCenter_WPF.Services.Spectrometer;
using System.Collections.ObjectModel;

/*
 * 文件名: Platform3DViewModel.cs
 * 描述: 三维运动平台调试与校准视图模型。
 * 负责处理平台的手动位移控制、三轴自动归零以及基于光谱反馈的全自动空间寻优校准流程。
 * 维护指南: 
 * 1. 界面坐标同步高度依赖于 Platform3DService 的物理反馈，移动动作需严格执行轴向与限位状态的安全预检。
 * 2. Z 轴具备特殊的负向行程（-2000），在 MoveAsync 逻辑中需优先处理此物理特性。
 * 3. 校准流程包含 4 个阶段，每阶段完成后均设有 UI 停顿缓冲以提升用户交互感知。
 */

namespace GD_ControlCenter_WPF.ViewModels.Dialogs
{
    /// <summary>
    /// 三维平台位移控制与自动校准视图模型。
    /// </summary>
    public partial class Platform3DViewModel : ObservableObject
    {
        #region 1. 依赖服务与字段

        /// <summary>
        /// 三维平台基础控制服务。
        /// </summary>
        private readonly IPlatform3DService _platformService;

        /// <summary>
        /// 平台校准与寻优业务服务。
        /// </summary>
        private readonly PlatformCalibrationService _calibrationService;

        /// <summary>
        /// JSON 配置持久化服务。
        /// </summary>
        private readonly JsonConfigService _jsonConfigService = null!;

        /// <summary>
        /// 特征峰追踪服务，用于获取可用的寻优波长列表。
        /// </summary>
        private readonly PeakTrackingService _peakTrackingService;

        /// <summary>
        /// 关闭对话框的委托动作。
        /// </summary>
        private readonly Action _closeAction;

        #endregion

        #region 2. UI 绑定属性 (坐标与限位)

        /// <summary>
        /// 界面显示的实时 X 轴脉冲坐标。
        /// </summary>
        [ObservableProperty] private double _currentX;

        /// <summary>
        /// 界面显示的实时 Y 轴脉冲坐标。
        /// </summary>
        [ObservableProperty] private double _currentY;

        /// <summary>
        /// 界面显示的实时 Z 轴脉冲坐标。
        /// </summary>
        [ObservableProperty] private double _currentZ;

        /// <summary>
        /// X 轴物理步进最大限制。
        /// </summary>
        [ObservableProperty] private double _maxX = PlatformLimits.MaxStepX;

        /// <summary>
        /// Y 轴物理步进最大限制。
        /// </summary>
        [ObservableProperty] private double _maxY = PlatformLimits.MaxStepY;

        /// <summary>
        /// Z 轴物理步进最大限制。
        /// </summary>
        [ObservableProperty] private double _maxZ = PlatformLimits.MaxStepZ;

        #endregion

        #region 3. 交互控制参数

        /// <summary>
        /// 当前选中的轴索引（0:X, 1:Y, 2:Z）。
        /// </summary>
        [ObservableProperty] private int _selectedAxisIndex = 0;

        /// <summary>
        /// 当前选中的移动方向索引（0:正向, 1:反向）。
        /// </summary>
        [ObservableProperty] private int _selectedDirectionIndex = 0;

        /// <summary>
        /// 用户设定的单次移动脉冲步长。
        /// </summary>
        [ObservableProperty] private double _stepDistance = 100.0;

        // --- 轴选择桥接属性 (用于 RadioButton 绑定) ---
        public bool IsXSelected { get => SelectedAxisIndex == 0; set { if (value) { SelectedAxisIndex = 0; OnPropertyChanged(nameof(IsXSelected)); } } }
        public bool IsYSelected { get => SelectedAxisIndex == 1; set { if (value) { SelectedAxisIndex = 1; OnPropertyChanged(nameof(IsYSelected)); } } }
        public bool IsZSelected { get => SelectedAxisIndex == 2; set { if (value) { SelectedAxisIndex = 2; OnPropertyChanged(nameof(IsZSelected)); } } }

        // --- 方向选择桥接属性 ---
        public bool IsDirPositive { get => SelectedDirectionIndex == 0; set { if (value) { SelectedDirectionIndex = 0; OnPropertyChanged(nameof(IsDirPositive)); } } }
        public bool IsDirNegative { get => SelectedDirectionIndex == 1; set { if (value) { SelectedDirectionIndex = 1; OnPropertyChanged(nameof(IsDirNegative)); } } }

        #endregion

        #region 4. 校准状态与光谱反馈

        /// <summary>
        /// 校准流程的实时文字提示信息。
        /// </summary>
        [ObservableProperty] private string _calibrationStatus = "待命：系统已就绪";

        /// <summary>
        /// 指示当前是否正处于自动化校准运行状态中。
        /// </summary>
        [ObservableProperty] private bool _isCalibrating;

        /// <summary>
        /// 可供选作校准目标的特征峰波长格式化列表。
        /// </summary>
        [ObservableProperty] private ObservableCollection<string> _peakWavelengths = new();

        /// <summary>
        /// 当前选中的校准目标波长字符串。
        /// </summary>
        [ObservableProperty] private string _selectedPeakWavelength = string.Empty;

        #endregion

        #region 5. 构造函数与初始化

        /// <summary>
        /// 构造 Platform3DViewModel。
        /// </summary>
        public Platform3DViewModel(IPlatform3DService platformService,PlatformCalibrationService calibrationService,JsonConfigService jsonConfigService,
                PeakTrackingService peakTrackingService,Action closeAction)
        {
            _platformService = platformService;
            _calibrationService = calibrationService;
            _jsonConfigService = jsonConfigService;
            _peakTrackingService = peakTrackingService;
            _closeAction = closeAction;

            // 加载主界面标记的所有可用特征峰
            LoadAvailablePeaks();

            // 恢复上次保存的步长设置
            var config = _jsonConfigService?.Load();
            StepDistance = config?.Platform3D?.DefaultStepDistance ?? 100.0;

            // 初始同步坐标
            SyncPositionFromService();

            // 注册限位触发消息监听
            WeakReferenceMessenger.Default.Register<PlatformBoundaryMessage>(this, (r, m) =>
            {
                string boundaryName = m.IsMin ? "零点" : "最大值";

                // 物理触发限位后，强制在 UI 线程同步一次坐标
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    SyncPositionFromService();
                });

                // 更新状态栏提示
                if (IsCalibrating)
                {
                    if (m.IsMin)
                    {
                        CalibrationStatus = m.Axis switch
                        {
                            AxisType.X => $"⚠️ 已到达边界：X 轴触碰 {boundaryName} 限位，开始 Y 轴的移动。",
                            AxisType.Y => $"⚠️ 已到达边界：Y 轴触碰 {boundaryName} 限位，开始 Z 轴的移动。",
                            AxisType.Z => $"⚠️ 已到达边界：Z 轴触碰 {boundaryName} 限位，准备开始全轴寻优。",
                            _ => CalibrationStatus
                        };
                    }
                    else
                    {
                        CalibrationStatus = $"⚠️ 已到达边界：{m.Axis} 轴触碰 {boundaryName} 限位，开始折返。";
                    }
                }
                else
                {
                    CalibrationStatus = $"⚠️ 已到达边界：{m.Axis} 轴触碰 {boundaryName} 限位，运动中止。";
                }
            });
        }

        #endregion

        #region 6. 核心逻辑方法

        /// <summary>
        /// 从寻峰服务中加载所有已标记的特征峰波长。
        /// </summary>
        private void LoadAvailablePeaks()
        {
            PeakWavelengths.Clear();
            foreach (var peak in _peakTrackingService.TrackedPeaks)
            {
                PeakWavelengths.Add($"{peak.BaseWavelength:F2} nm");
            }

            SelectedPeakWavelength = PeakWavelengths.FirstOrDefault() ?? string.Empty;
        }

        /// <summary>
        /// 将 ViewModel 属性与底层 Service 的物理坐标进行同步。
        /// </summary>
        private void SyncPositionFromService()
        {
            CurrentX = _platformService.CurrentPosition.X;
            CurrentY = _platformService.CurrentPosition.Y;
            CurrentZ = _platformService.CurrentPosition.Z;
        }

        /// <summary>
        /// 执行异步手动移动指令。
        /// </summary>
        [RelayCommand]
        private async Task MoveAsync()
        {
            // 若平台正在运动或正处于校准状态，则拒绝手动介入
            if (_platformService.Status.IsMoving || IsCalibrating) return;

            AxisType targetAxis = (AxisType)(SelectedAxisIndex + 1);
            bool isPositive = SelectedDirectionIndex == 0;
            int step = (int)Math.Round(StepDistance);

            // 软件安全预检 (防止撞击极限位)
            if (isPositive && _platformService.Status.IsAtMax[targetAxis])
            {
                CalibrationStatus = $"操作拒绝：{targetAxis} 轴已处于物理极限位。";
                return;
            }

            if (!isPositive)
            {
                if (targetAxis == AxisType.Z)
                {
                    // Z 轴特殊放行逻辑：确立物理零点后，才执行 -2000 软限位判断。
                    if (_platformService.Status.HasReceivedZZero)
                    {
                        if (_platformService.CurrentPosition.Z - step < PlatformLimits.MinStepZ)
                        {
                            CalibrationStatus = $"操作拒绝：Z 轴超过最低软限位 ({PlatformLimits.MinStepZ})。";
                            return;
                        }
                    }
                }
                else if (_platformService.Status.IsAtMin[targetAxis])
                {
                    CalibrationStatus = $"操作拒绝：{targetAxis} 轴已处于零点。";
                    return;
                }
            }

            CalibrationStatus = $"正在手动移动 {targetAxis} 轴...";

            // 提交异步移动请求
            bool success = await _platformService.MoveAxisAsync(targetAxis, step, isPositive);

            // 后验同步：无论结果如何，必须刷新 UI 坐标
            SyncPositionFromService();

            if (success)
            {
                CalibrationStatus = "移动完成。";
            }
        }

        /// <summary>
        /// 执行全自动四阶段校准序列（归零 -> X 寻优 -> Y 寻优 -> Z 寻优）。
        /// </summary>
        [RelayCommand]
        private async Task StartCalibrationAsync()
        {
            if (_platformService.Status.IsMoving) return;

            if (string.IsNullOrEmpty(SelectedPeakWavelength))
            {
                CalibrationStatus = "操作拒绝：未检测到目标波长。";
                return;
            }

            IsCalibrating = true;

            try
            {
                using var cts = new CancellationTokenSource();
                double targetWl = double.TryParse(SelectedPeakWavelength.Split(' ')[0], out var val) ? val : 253.65;

                // --- 阶段 1: 全轴物理归零 ---
                CalibrationStatus = "步骤 1/4: 正在执行全轴机械归零...";
                await _calibrationService.AutoHomeAllAxesAsync(cts.Token);
                SyncPositionFromService();
                await Task.Delay(800, cts.Token);

                // --- 阶段 2: X 轴自动寻优 ---
                CalibrationStatus = $"步骤 2/4: 执行 X 轴中心点寻优 ({targetWl:F2} nm)...";
                await _calibrationService.StartAxisCalibrationAsync(AxisType.X, targetWl, 2.0, cts.Token);
                SyncPositionFromService();
                await Task.Delay(800, cts.Token);

                // --- 阶段 3: Y 轴自动寻优 ---
                CalibrationStatus = $"步骤 3/4: 执行 Y 轴中心点寻优 ({targetWl:F2} nm)...";
                await _calibrationService.StartAxisCalibrationAsync(AxisType.Y, targetWl, 2.0, cts.Token);
                SyncPositionFromService();
                await Task.Delay(800, cts.Token);

                // --- 阶段 4: Z 轴自动寻优 ---
                CalibrationStatus = $"步骤 4/4: 执行 Z 轴中心点寻优 ({targetWl:F2} nm)...";
                await _calibrationService.StartAxisCalibrationAsync(AxisType.Z, targetWl, 2.0, cts.Token);
                SyncPositionFromService();

                // 成果展示停留
                await Task.Delay(1500, cts.Token);

                CalibrationStatus = "全自动空间校准已完成！";
            }
            catch (OperationCanceledException)
            {
                CalibrationStatus = "校准已手动终止";
            }
            catch (Exception ex)
            {
                CalibrationStatus = $"校准失败: {ex.Message}";
            }
            finally
            {
                SyncPositionFromService();
                IsCalibrating = false;
            }
        }

        /// <summary>
        /// 保存用户设置的步长偏好至本地 JSON 配置文件。
        /// </summary>
        public void SavePreferences()
        {
            var config = _jsonConfigService.Load();

            // 补全可能缺失的配置节
            if (config.Platform3D == null)
                config.Platform3D = new Platform3DConfig();

            config.Platform3D.DefaultStepDistance = StepDistance;

            // 持久化保存
            _jsonConfigService.Save(config);
        }

        #endregion
    }
}