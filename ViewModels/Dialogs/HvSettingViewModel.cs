using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GD_ControlCenter_WPF.Models;
using GD_ControlCenter_WPF.Services;
using System.Windows;

/*
 * 文件名: HvSettingViewModel.cs
 * 描述: 高压电源参数设定对话框的视图模型。处理目标电压/电流的范围校验、持久化保存及动态调压指令下发。
 * 维护指南: 
 * 1. 电压物理量程限制为 0 - 1500 V，电流限制为 0 - 100 mA。
 * 2. 应用命令（ApplyCommand）会同时触发磁盘写入（Save）与硬件即时同步（若当前处于 Running 状态）。
 */

namespace GD_ControlCenter_WPF.ViewModels.Dialogs
{
    /// <summary>
    /// 高压设置弹窗视图模型。
    /// </summary>
    public partial class HvSettingViewModel : ObservableObject
    {
        #region 1. 私有字段与配置状态

        /// <summary> 
        /// 高压硬件服务引用。 
        /// </summary>
        private readonly HighVoltageService _hvService;

        /// <summary> 
        /// 配置管理服务引用。 
        /// </summary>
        private readonly JsonConfigService _configService;

        /// <summary> 
        /// 关闭窗口的回调动作。 
        /// </summary>
        private readonly Action _closeAction;

        /// <summary> 
        /// 当前设备是否正处于高压输出激活状态。 
        /// </summary>
        private readonly bool _isRunning;

        #endregion

        #region 2. UI 绑定属性 (设定值)

        /// <summary> 
        /// 用户在滑动条或文本框中设定的目标电压 (0-1500V)。 
        /// </summary>
        [ObservableProperty] private int _targetVoltage;

        /// <summary> 
        /// 用户在滑动条或文本框中设定的目标电流 (0-100mA)。 
        /// </summary>
        [ObservableProperty] private int _targetCurrent;

        #endregion

        #region 3. 初始化

        /// <summary>
        /// 构造函数：初始化设定值快照。
        /// </summary>
        public HvSettingViewModel(HighVoltageService hvService, JsonConfigService configService, AppConfig currentConfig,
            bool isRunning, Action closeAction)
        {
            _hvService = hvService;
            _configService = configService;
            _isRunning = isRunning;
            _closeAction = closeAction;

            // 载入当前已保存的物理参数
            TargetVoltage = currentConfig.LastHvVoltage;
            TargetCurrent = currentConfig.LastHvCurrent;
        }

        #endregion

        #region 4. 核心命令逻辑

        /// <summary>
        /// 执行“应用”逻辑：包含合法性校验、持久化及硬件同步。
        /// </summary>
        [RelayCommand]
        private void Apply()
        {
            // 物理安全边界校验
            if (TargetVoltage < 0 || TargetVoltage > 1500)
            {
                MessageBox.Show("电压设定值超出安全硬件范围 (0-1500 V)", "输入拦截", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (TargetCurrent < 0 || TargetCurrent > 100)
            {
                MessageBox.Show("电流设定值超出安全硬件范围 (0-100 mA)", "输入拦截", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 执行数据持久化，确保下次启动程序或点击“启动”按钮时能读取到新值
            var config = _configService.Load();
            config.LastHvVoltage = TargetVoltage;
            config.LastHvCurrent = TargetCurrent;
            _configService.Save(config);

            // 动态同步：如果此时硬件正处于开启输出状态，需立即下发指令使新参数生效
            if (_isRunning)
            {
                _hvService.SetHighVoltage(TargetVoltage, TargetCurrent);
            }

            // 通过回调通知 View 关闭对话框
            _closeAction?.Invoke();
        }

        #endregion
    }
}