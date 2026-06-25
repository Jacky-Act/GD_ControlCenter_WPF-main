using CommunityToolkit.Mvvm.ComponentModel;
using GD_ControlCenter_WPF.Services;
using GD_ControlCenter_WPF.ViewModels.Dialogs;
using GD_ControlCenter_WPF.Views.Dialogs;
using System.ComponentModel;
using System.Windows;

/*
 * 文件名: SyringePumpViewModel.cs
 * 描述: 注射泵设备卡片视图模型。
 * 核心逻辑：
 * 1. 自动化流水线：当用户点击卡片开关时，执行一段包含延时的“抽液-推液”完整循环。
 * 2. 状态锁定：在序列执行期间通过 IsBusy 属性禁用所有交互操作。
 */

namespace GD_ControlCenter_WPF.ViewModels
{
    public partial class SyringePumpViewModel : DeviceBaseViewModel
    {
        #region 1. 依赖服务

        private readonly GeneralDeviceService _generalService;
        private readonly JsonConfigService _configService;

        #endregion

        #region 2. 绑定属性

        /// <summary>
        /// 界面展示的当前设定距离快照。
        /// </summary>
        [ObservableProperty] private string _settingDistance = "0";

        /// <summary>
        /// 界面展示的初始方向描述（输入/输出）。
        /// </summary>
        [ObservableProperty] private string _directionText = "输入";

        #endregion

        public SyringePumpViewModel(GeneralDeviceService generalService, JsonConfigService configService)
        {
            _generalService = generalService;
            _configService = configService;
            DisplayName = "注射泵";
            UpdateUIFromConfig();
        }

        /// <summary>
        /// 同步本地配置至 UI 文本快照。
        /// </summary>
        private void UpdateUIFromConfig()
        {
            var config = _configService.Load();
            SettingDistance = $"{config.LastSyringeDistance}";
            DirectionText = config.IsSyringeOutput ? "输出" : "输入";
        }

        /// <summary>
        /// 监听卡片启动信号，触发进样自动化序列。
        /// </summary>
        protected override async void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            if (e.PropertyName == nameof(IsRunning) && IsRunning)
            {
                // 1. 锁定控制权限
                IsBusy = true;

                var config = _configService.Load();
                short targetDistance = (short)config.LastSyringeDistance;
                bool initialPort = config.IsSyringeOutput;

                // 2. 动态计算动作所需时间
                // 公式：(量程比) * 4.5秒全行程时间 + 1秒物理惯性缓冲
                int delayMs = (int)((targetDistance / 3000.0) * 4500) + 1000;

                try
                {
                    // 步骤 A：启动抽液动作
                    ButtonText = "抽";
                    _generalService.ControlSyringePump(initialPort, targetDistance);
                    await Task.Delay(delayMs);

                    // 步骤 B：启动推液动作（返回原点，物理阀门自动换向）
                    ButtonText = "推";
                    _generalService.ControlSyringePump(!initialPort, 0);
                    await Task.Delay(delayMs);
                }
                finally
                {
                    // 步骤 C：恢复初始状态
                    ButtonText = "启动";
                    IsRunning = false;
                    IsBusy = false;
                }
            }
        }

        protected override void ExecuteOpenDetail()
        {
            if (IsBusy) return;
            var window = new SyringePumpSettingWindow();
            window.DataContext = new SyringePumpSettingViewModel(_configService, _configService.Load(), () => { window.Close(); UpdateUIFromConfig(); });
            window.Owner = Application.Current.MainWindow;
            window.ShowDialog();
        }
    }
}