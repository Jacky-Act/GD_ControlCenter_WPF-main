using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GD_ControlCenter_WPF.Models;
using GD_ControlCenter_WPF.Services;
using System.Windows;

/*
 * 文件名: PeristalticPumpSettingViewModel.cs
 * 描述: 蠕动泵参数设定对话框的视图模型。提供转速、方向及流速校准系数（K/B）的设定功能。
 * 核心逻辑:
 * 1. 实时预览: 利用 partial 拦截器监控属性变化，用户在修改 K/B 或转速时 UI 会立刻更新“计算流速”。
 * 2. 状态互斥: 当泵正处于运行中时，禁止修改旋转方向，防止损坏电机。
 * 3. 数据校验: 限制转速物理边界为 0 - 100。
 */

namespace GD_ControlCenter_WPF.ViewModels.Dialogs
{
    /// <summary>
    /// 蠕动泵参数设定弹窗视图模型。
    /// </summary>
    public partial class PeristalticPumpSettingViewModel : ObservableObject
    {
        #region 1. 依赖服务与状态字段

        private readonly GeneralDeviceService _generalService;
        private readonly JsonConfigService _configService;
        private readonly Action _closeAction;

        /// <summary>
        /// 记录当前设备是否处于开启运行状态。
        /// </summary>
        private readonly bool _isRunning;

        #endregion

        #region 2. UI 绑定属性 (设定参数)

        /// <summary>
        /// 用户设定的目标转速 (0-100)。
        /// </summary>
        [ObservableProperty] private int _targetSpeed;

        /// <summary>
        /// 旋转方向标识：True 为顺时针（前向），False 为逆时针（后向）。
        /// </summary>
        [ObservableProperty] private bool _isClockwise;

        /// <summary>
        /// 界面显示的当前方向文字提示。
        /// </summary>
        [ObservableProperty] private string _directionHint = "前向";

        /// <summary>
        /// 流量校准系数 K (斜率)，以字符串形式存储以支持文本框输入交互。
        /// </summary>
        [ObservableProperty] private string _pumpFlowRateK = "1.0";

        /// <summary>
        /// 流量校准系数 B (截距)。
        /// </summary>
        [ObservableProperty] private string _pumpFlowRateB = "0.0";

        /// <summary>
        /// 标识方向切换按钮是否可用（运行中禁用）。
        /// </summary>
        [ObservableProperty] private bool _isDirectionEnabled;

        /// <summary>
        /// 实时计算得出的预期流速，用于在弹窗中即时反馈参数修改结果。
        /// </summary>
        [ObservableProperty] private double _calculatedFlowRate;

        #endregion

        #region 3. 初始化

        /// <summary>
        /// 构造函数：初始化 UI 参数快照并执行首次流速计算。
        /// </summary>
        public PeristalticPumpSettingViewModel(GeneralDeviceService generalService, JsonConfigService configService, AppConfig currentConfig,
            bool isRunning, Action closeAction)
        {
            _generalService = generalService;
            _configService = configService;
            _isRunning = isRunning;
            _closeAction = closeAction;

            // 运行状态安全检查：运行中严禁切换电机方向
            IsDirectionEnabled = !_isRunning;

            // 从本地配置实体装载数据
            TargetSpeed = currentConfig.LastPumpSpeed;
            IsClockwise = currentConfig.IsPumpClockwise;
            PumpFlowRateK = currentConfig.PumpFlowRateK.ToString();
            PumpFlowRateB = currentConfig.PumpFlowRateB.ToString();

            // 执行初始渲染
            UpdateCalculatedFlowRate();
            UpdateDirectionHint(IsClockwise);
        }

        #endregion

        #region 4. 响应式逻辑：属性监听与实时计算

        /// <summary> 监听方向布尔值变化，同步更新 UI 文字提示。 </summary>
        partial void OnIsClockwiseChanged(bool value) => UpdateDirectionHint(value);
        private void UpdateDirectionHint(bool value) => DirectionHint = value ? "前向" : "后向";

        /// <summary> 当转速发生物理变化时，实时重算预期流速。 </summary>
        partial void OnTargetSpeedChanged(int value) => UpdateCalculatedFlowRate();

        /// <summary> 当用户在文本框修改 K 系数时，实时重算预期流速。 </summary>
        partial void OnPumpFlowRateKChanged(string value) => UpdateCalculatedFlowRate();

        /// <summary> 当用户在文本框修改 B 系数时，实时重算预期流速。 </summary>
        partial void OnPumpFlowRateBChanged(string value) => UpdateCalculatedFlowRate();

        /// <summary>
        /// 核心计算引擎：解析文本数据并执行线性公式映射。
        /// 包含容错处理：若用户输入非法字符，默认按 0 处理以防程序崩溃。
        /// </summary>
        private void UpdateCalculatedFlowRate()
        {
            double k = double.TryParse(PumpFlowRateK, out double parsedK) ? parsedK : 0;
            double b = double.TryParse(PumpFlowRateB, out double parsedB) ? parsedB : 0;

            CalculatedFlowRate = Math.Round(k * TargetSpeed + b, 2);
        }

        #endregion

        #region 5. 命令逻辑

        /// <summary>
        /// 执行“应用”逻辑：包含合法性边界校验、持久化保存及运行中指令同步。
        /// </summary>
        [RelayCommand]
        private void Apply()
        {
            // 1. 物理安全量程校验 (0-100%)
            if (TargetSpeed < 0 || TargetSpeed > 100)
            {
                MessageBox.Show("转速设定超出有效范围 (0-100)", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 2. 将数据持久化至本地磁盘，确保主界面卡片及下次启动能读取到新值
            var config = _configService.Load();
            config.LastPumpSpeed = TargetSpeed;
            config.IsPumpClockwise = IsClockwise;

            // 存储时将字符串还原为 double 类型
            config.PumpFlowRateK = double.TryParse(PumpFlowRateK, out double finalK) ? finalK : 1.0;
            config.PumpFlowRateB = double.TryParse(PumpFlowRateB, out double finalB) ? finalB : 0.0;

            _configService.Save(config);

            // 3. 动态同步：如果此时硬件正处于运行状态，需立即下发指令以更新转速/方向
            if (_isRunning)
            {
                _generalService.ControlPeristalticPump((short)TargetSpeed, IsClockwise, true);
            }

            // 4. 通知 View 层关闭弹窗
            _closeAction?.Invoke();
        }

        #endregion
    }
}