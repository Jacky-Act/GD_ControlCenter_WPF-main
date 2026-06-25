using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GD_ControlCenter_WPF.Models;
using GD_ControlCenter_WPF.Services;
using System.Windows;

/*
 * 文件名: SyringePumpSettingViewModel.cs
 * 描述: 注射泵参数设定弹窗视图模型。
 */

namespace GD_ControlCenter_WPF.ViewModels.Dialogs
{
    public partial class SyringePumpSettingViewModel : ObservableObject
    {
        private readonly JsonConfigService _configService;
        private readonly Action _closeAction;

        /// <summary>
        /// 用户设定的移动距离（单位：步，范围 0-3000）。
        /// </summary>
        [ObservableProperty] private int _targetDistance;

        /// <summary>
        /// 初始方向标识。
        /// </summary>
        [ObservableProperty] private bool _isOutput;

        /// <summary>
        /// 界面显示的方向文字动态提示。
        /// </summary>
        [ObservableProperty] private string _directionHint = "输出";

        public SyringePumpSettingViewModel(JsonConfigService configService, AppConfig currentConfig, Action closeAction)
        {
            _configService = configService;
            _closeAction = closeAction;

            TargetDistance = currentConfig.LastSyringeDistance;
            IsOutput = currentConfig.IsSyringeOutput;
            UpdateDirectionHint(IsOutput);
        }

        /// <summary>
        /// 监听方向布尔值变化，同步更新 UI 文字提示。
        /// </summary>
        partial void OnIsOutputChanged(bool value) => UpdateDirectionHint(value);

        /// <summary>
        /// 更新方向文字。
        /// </summary>
        private void UpdateDirectionHint(bool value) => DirectionHint = value ? "输出" : "输入";

        /// <summary>
        /// 执行参数保存并关闭弹窗。
        /// </summary>
        [RelayCommand]
        private void Apply()
        {
            // 物理限位逻辑校验
            if (TargetDistance < 0 || TargetDistance > 3000)
            {
                MessageBox.Show("设定值超出物理量程 (0-3000)", "输入拦截", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var config = _configService.Load();
            config.LastSyringeDistance = TargetDistance;
            config.IsSyringeOutput = IsOutput;
            _configService.Save(config);

            _closeAction?.Invoke();
        }
    }
}