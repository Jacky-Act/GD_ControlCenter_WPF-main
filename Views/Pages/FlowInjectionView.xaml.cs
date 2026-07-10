using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Models.Spectrometer;
using GD_ControlCenter_WPF.ViewModels;
using GD_ControlCenter_WPF.Services.Spectrometer.Logic;
using ScottPlot;
using System.Windows.Controls;
using System;

namespace GD_ControlCenter_WPF.Views.Pages
{
    public partial class FlowInjectionView : UserControl
    {
        private ScottPlot.Plottables.DataLogger _dataLogger;
        private double _timeCounter = 0;

        // --- 零分配渲染缓存 ---
        private ScottPlot.Plottables.Scatter? _livePlot;
        private double[]? _cachedWavelengths;
        private double[]? _cachedIntensities;

        public FlowInjectionView()
        {
            InitializeComponent();

            // 初始化下方的时序趋势图 DataLogger
            _dataLogger = TimeSeriesPlot.Plot.Add.DataLogger();
            TimeSeriesPlot.Plot.Axes.AutoScale();

            // 注册消息：接收光谱仪数据
            WeakReferenceMessenger.Default.Register<SpectralDataMessage>(this, (r, m) =>
            {
                OnPlotUpdateRequested(m.Value);
            });

            // 右键菜单配置（寻峰逻辑）
            SpecPlot.Menu?.Clear();
            SpecPlot.Menu?.Add("捕捉此点作为元素峰", (p) =>
            {
                double wl = SpecPlot.Plot.Axes.Bottom.Range.Center;
                if (this.DataContext is FlowInjectionViewModel vm)
                {
                    vm.PickedElements.Add($"峰@{wl:F2}");
                }
            });
        }

        private DateTime _lastRenderTime = DateTime.MinValue;

        private void OnPlotUpdateRequested(SpectralData data)
        {
            if ((DateTime.Now - _lastRenderTime).TotalMilliseconds < 33) return;
            _lastRenderTime = DateTime.Now;

            Dispatcher.BeginInvoke(new Action(() => 
            {
                if (!this.IsVisible) return;
                RenderPlots(data);
            }));
        }

        private void RenderPlots(SpectralData data)
        {
            if (data.Wavelengths == null || data.Wavelengths.Length == 0) return;

            var vm = this.DataContext as FlowInjectionViewModel;
            if (vm == null) return;

            double targetWl = vm.GetTargetWavelength();

            // 1. 渲染上图：实时全谱
            int len = data.Wavelengths.Length;
            if (_livePlot == null || _cachedWavelengths == null || _cachedWavelengths.Length != len)
            {
                if (_livePlot != null) SpecPlot.Plot.Remove(_livePlot);

                _cachedWavelengths = new double[len];
                _cachedIntensities = new double[len];
                Array.Copy(data.Wavelengths, _cachedWavelengths, len);
                Array.Copy(data.Intensities, _cachedIntensities, len);

                _livePlot = SpecPlot.Plot.Add.Scatter(_cachedWavelengths, _cachedIntensities);
                _livePlot.MarkerSize = 0;
                _livePlot.Color = ScottPlot.Colors.MediumPurple;
            }
            else
            {
                // 零分配原位拷贝
                Array.Copy(data.Wavelengths, _cachedWavelengths, len);
                Array.Copy(data.Intensities, _cachedIntensities, len);
            }

            SpecPlot.Plot.Axes.AutoScale();
            SpecPlot.Refresh();

            // 2. 渲染下图：单元素时序（流动注射核心）
            if (targetWl > 0)
            {
                double currentIntensity = SpectrometerLogic.GetIntensityAtWavelength(data, targetWl);

                // 向 DataLogger 添加点（X为计数或时间，Y为强度）
                _dataLogger.Add(_timeCounter++, currentIntensity);
                TimeSeriesPlot.Plot.Axes.AutoScale();
                TimeSeriesPlot.Refresh();

                // 同时将数据反馈给 ViewModel 缓冲区，用于计算峰高
                vm.AddDataToScan(currentIntensity);
            }
        }
    }
}
