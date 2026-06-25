using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using GD_ControlCenter_WPF.Services;
using GD_ControlCenter_WPF.ViewModels.Dialogs;
using GD_ControlCenter_WPF.Views.Dialogs;

/*
 * 文件名: PeristalticPumpViewModel.cs
 * 描述: 蠕动泵设备卡片的视图模型。负责主界面卡片的实时数据同步、流速计算展示以及参数设置弹窗的调度。
 * 维护指南: 
 * 1. 本类通过 JsonConfigService 实时同步本地配置中的转速与系数。
 * 2. 核心流速计算公式: FlowRate = K * Speed + B。
 * 3. 当 IsRunning 属性变更时，会触发物理硬件的启停控制指令。
 */

namespace GD_ControlCenter_WPF.ViewModels
{
    /// <summary>
    /// 蠕动泵设备视图模型。
    /// </summary>
    public partial class PeristalticPumpViewModel : DeviceBaseViewModel
    {
        #region 1. 依赖服务与字段

        /// <summary> 
        /// 通用设备控制服务，用于下发蠕动泵物理指令。 
        /// </summary>
        private readonly GeneralDeviceService _generalService;

        /// <summary> 
        /// JSON 配置持久化服务。 
        /// </summary>
        private readonly JsonConfigService _configService;

        #endregion

        #region 2. UI 绑定属性 (状态快照)

        /// <summary> 
        /// 界面显示的当前设定转速 (0-100)。 
        /// </summary>
        [ObservableProperty] private string _settingSpeed = "0";

        /// <summary>
        /// 界面显示的当前转向描述（前向/后向）。
        /// </summary>
        [ObservableProperty] private string _directionText = "前向";

        /// <summary>
        /// 根据转速与 K/B 系数实时计算得出的流速数值 (单位: mL/min 或自定义)。
        /// </summary>
        [ObservableProperty] private double _currentFlowRate;

        #endregion

        #region 3. 初始化

        /// <summary>
        /// 构造函数：注入服务并执行首次界面数据刷新。
        /// </summary>
        public PeristalticPumpViewModel(GeneralDeviceService generalService, JsonConfigService configService)
        {
            _generalService = generalService;
            _configService = configService;

            DisplayName = "蠕动泵";

            // 初始化加载
            UpdateUIFromConfig();
        }

        #endregion

        #region 4. 核心逻辑：数据同步与弹窗调度

        /// <summary>
        /// 状态同步逻辑：从本地物理文件载入最新参数，并重新计算流速。
        /// 用于在程序启动或设置弹窗关闭后刷新主卡片显示。
        /// </summary>
        private void UpdateUIFromConfig()
        {
            var config = _configService.Load();

            // 同步基础物理参数
            SettingSpeed = $"{config.LastPumpSpeed}";
            DirectionText = config.IsPumpClockwise ? "后向" : "前向";

            // 执行核心数学计算：流速 = 斜率 K * 转速 + 截距 B (保留两位小数)
            CurrentFlowRate = Math.Round(config.PumpFlowRateK * config.LastPumpSpeed + config.PumpFlowRateB, 2);
        }

        /// <summary>
        /// 重写基类方法：点击卡片配置图标时弹出参数设定对话框。
        /// </summary>
        protected override void ExecuteOpenDetail()
        {
            var currentConfig = _configService.Load();
            var window = new PeristalticPumpSettingWindow();

            // 构建弹窗 VM 并注入闭包回调，确保在点击“应用”后主界面能及时重算流速
            var vm = new PeristalticPumpSettingViewModel(
                _generalService,
                _configService,
                currentConfig,
                this.IsRunning,
                () =>
                {
                    window.Close();
                    UpdateUIFromConfig(); // 弹窗关闭后立即执行数据重载
                }
            );

            window.DataContext = vm;
            window.Owner = Application.Current.MainWindow;
            window.ShowDialog();
        }

        /// <summary>
        /// 监听卡片开关（IsRunning）属性变更。
        /// 职责：同步操作物理硬件。
        /// </summary>
        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            if (e.PropertyName == nameof(IsRunning))
            {
                var config = _configService.Load();
                // 下发 13 字节硬件控制帧：(转速, 方向, 启停标志)
                _generalService.ControlPeristalticPump((short)config.LastPumpSpeed, config.IsPumpClockwise, IsRunning);
            }
        }

        #endregion
    }
}