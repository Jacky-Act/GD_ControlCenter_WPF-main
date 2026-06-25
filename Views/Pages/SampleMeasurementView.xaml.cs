using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Models.Spectrometer;
using GD_ControlCenter_WPF.ViewModels;
using GD_ControlCenter_WPF.Services.Spectrometer.Logic;
using System.Windows.Controls;
using System.Windows;
using System;
using System.Linq;

namespace GD_ControlCenter_WPF.Views.Pages
{
    public partial class SampleMeasurementView : UserControl
    {
        private double _lastMouseX = 0; 

        public SampleMeasurementView()
        {
            InitializeComponent();
            SetupPlots();

            WeakReferenceMessenger.Default.Register<SpectralDataMessage>(this, (r, m) => RenderPlots(m.Value));

            // 监听鼠标移动，实现精准波长捕捉
            SpecPlot.MouseMove += (s, e) =>
            {
                var pos = e.GetPosition(SpecPlot);
                _lastMouseX = SpecPlot.Plot.GetCoordinates((float)pos.X, (float)pos.Y).X;
            };
        }

        private void SetupPlots()
        {
            SpecPlot.Menu?.Clear();
            SpecPlot.Menu?.Add("捕捉为特征峰", (p) => {
                if (this.DataContext is SampleMeasurementViewModel vm) {
                    vm.PeakTracker.AddPeak(_lastMouseX);
                }
            });

            SpecPlot.Menu?.Add("去除附近标记", (p) => {
                if (this.DataContext is SampleMeasurementViewModel vm) {
                    vm.PeakTracker.RemovePeakNear(_lastMouseX, 5.0);
                }
            });

            SpecPlot.Menu?.Add("清除所有标记", (p) => {
                if (this.DataContext is SampleMeasurementViewModel vm) {
                    vm.PeakTracker.ClearAll();
                    Dispatcher.Invoke(() => vm.PickedElements.Clear());
                }
            });
        }

        private void RenderPlots(SpectralData data)
        {
            if (data.Wavelengths == null || data.Wavelengths.Length == 0) return;

            Dispatcher.Invoke(() =>
            {
                if (this.DataContext is not SampleMeasurementViewModel vm) return;

                // 1. 渲染全谱与追踪红线
                SpecPlot.Plot.Clear();
                SpecPlot.Plot.Add.Scatter(data.Wavelengths, data.Intensities).MarkerSize = 0;

                foreach (var peak in vm.PeakTracker.TrackedPeaks)
                {
                    SpecPlot.Plot.Add.VerticalLine(peak.CurrentWavelength, 1.2f, ScottPlot.Color.FromHex("#F44336"));
                    
                    // 将每个红线点的数据泵入 VM 缓冲区
                    string name = vm.MatchElement(peak.CurrentWavelength);
                    double intensity = SpectrometerLogic.GetIntensityAtWavelength(data, peak.CurrentWavelength);
                    vm.PushData(name, intensity);
                }
                SpecPlot.Plot.Axes.AutoScale();
                SpecPlot.Refresh();

                // 2. 渲染局部细节图
                TrendPlot.Plot.Clear();
                var elementLine = TrendPlot.Plot.Add.Scatter(data.Wavelengths, data.Intensities);
                elementLine.MarkerSize = 0;
                elementLine.Color = ScottPlot.Colors.DeepSkyBlue;

                double targetWl = vm.GetTargetWavelength();
                if (targetWl > 0)
                {
                    TrendPlot.Plot.Axes.SetLimits(targetWl - 2.5, targetWl + 2.5, -100, data.Intensities.Max() + 500);
                }
                TrendPlot.Refresh();
            });
        }
    }
}