using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Services;
using System.Windows;
using static GD_ControlCenter_WPF.Models.AppConfig;

namespace GD_ControlCenter_WPF.ViewModels
{
    public partial class AnalysisWorkstationViewModel : ObservableObject
    {
        public SampleMeasurementViewModel ContinuousVM { get; }
        public FlowInjectionViewModel FlowInjectionVM { get; }

        // ================= 核心：必须使用 ObservableProperty 让 UI 知道值变了 =================
        [ObservableProperty]
        private Visibility _continuousVisibility = Visibility.Visible;

        [ObservableProperty]
        private Visibility _flowInjectionVisibility = Visibility.Collapsed;

        public AnalysisWorkstationViewModel(
            SampleMeasurementViewModel continuousVM,
            FlowInjectionViewModel flowInjectionVM,
            JsonConfigService configService)
        {
            ContinuousVM = continuousVM;
            FlowInjectionVM = flowInjectionVM;

            // 1. 初始化时读取配置并设定初始界面
            var config = configService.Load();
            SwitchMode(config.CurrentMeasurementMode);

            // 2. 监听仪表台传来的切换消息
            WeakReferenceMessenger.Default.Register<MeasurementModeChangedMessage>(this, (r, m) =>
            {
                // 必须在 UI 线程执行切换，否则 Visibility 改变无效
                Application.Current.Dispatcher.Invoke(() =>
                {
                    SwitchMode(m.Value);
                });
            });
        }

        private void SwitchMode(MeasurementMode mode)
        {
            // 直接赋值 Visibility 枚举，这是最稳妥的 WPF 显隐控制方式
            if (mode == MeasurementMode.Continuous)
            {
                ContinuousVisibility = Visibility.Visible;
                FlowInjectionVisibility = Visibility.Collapsed;
            }
            else
            {
                ContinuousVisibility = Visibility.Collapsed;
                FlowInjectionVisibility = Visibility.Visible;
            }
        }
    }
}