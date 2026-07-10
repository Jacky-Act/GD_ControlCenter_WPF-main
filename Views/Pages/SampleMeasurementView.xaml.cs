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
        private DateTime _lastRenderTime = DateTime.MinValue;

        public SampleMeasurementView()
        {
            InitializeComponent();
            SetupPlots();

            WeakReferenceMessenger.Default.Register<SpectralDataMessage>(this, (r, m) => OnPlotUpdateRequested(m.Value));
            WeakReferenceMessenger.Default.Register<TrendPlotRefreshMessage>(this, (r, m) => RenderTrendPlot());

            this.DataContextChanged += (s, e) =>
            {
                if (e.OldValue is SampleMeasurementViewModel oldVm)
                {
                    oldVm.PropertyChanged -= Vm_PropertyChanged;
                }
                if (e.NewValue is SampleMeasurementViewModel newVm)
                {
                    newVm.PropertyChanged += Vm_PropertyChanged;
                }
            };

            // 监听鼠标移动，实现精准波长捕捉
            SpecPlot.MouseMove += (s, e) =>
            {
                var pos = e.GetPosition(SpecPlot);
                _lastMouseX = SpecPlot.Plot.GetCoordinates((float)pos.X, (float)pos.Y).X;
            };
        }

        private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SampleMeasurementViewModel.SelectedPreviewElement))
            {
                // Trigger a render when the combobox selection changes
                RenderTrendPlot();
            }
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

        private void OnPlotUpdateRequested(SpectralData data)
        {
            if ((DateTime.Now - _lastRenderTime).TotalMilliseconds < 33) return;
            _lastRenderTime = DateTime.Now;
            
            Dispatcher.BeginInvoke(new Action(() => RenderPlots(data)));
        }

        private void RenderPlots(SpectralData data)
        {
            if (data.Wavelengths == null || data.Wavelengths.Length == 0) return;

            if (this.DataContext is not SampleMeasurementViewModel vm) return;

            // 1. 渲染全谱与追踪红线
            SpecPlot.Plot.Clear();
                SpecPlot.Plot.Add.Scatter(data.Wavelengths, data.Intensities).MarkerSize = 0;

                /*
                // 【已断开寻峰匹配与推送逻辑】
                foreach (var peak in vm.PeakTracker.TrackedPeaks)
                {
                    SpecPlot.Plot.Add.VerticalLine(peak.CurrentWavelength, 1.2f, ScottPlot.Color.FromHex("#F44336"));
                    
                    // 将每个红线点的数据泵入 VM 缓冲区
                    string name = vm.MatchElement(peak.CurrentWavelength);
                    double intensity = SpectrometerLogic.GetIntensityAtWavelength(data, peak.CurrentWavelength);
                    vm.PushData(name, intensity);
                }
                */

                // 2. 将采集到的全谱数据直接交给 VM 处理提取强度（空方法占位）
                vm.ExtractIntensityFromFullSpectrum(data.Wavelengths, data.Intensities);

                SpecPlot.Plot.Axes.AutoScale();
                SpecPlot.Refresh();

                // 3. 渲染趋势图 (不再在全谱回调中渲染，已分离到 RenderTrendPlot)
        }

        private void RenderTrendPlot()
        {
            Dispatcher.Invoke(() =>
            {
                if (this.DataContext is not SampleMeasurementViewModel vm) return;

                // 渲染趋势图 (绑定到 SelectedPreviewElement 的测量记录)
                TrendPlot.Plot.Clear();
                
                if (vm.SelectedPreviewElement != null && vm.SelectedPreviewElement.Reps != null)
                {
                    var validReps = vm.SelectedPreviewElement.Reps.Where(r => r.Intensity.HasValue).ToList();
                    if (validReps.Count > 0)
                    {
                        double[] xs = validReps.Select(r => (double)r.RepIndex).ToArray();
                        double[] ys = validReps.Select(r => r.Intensity!.Value).ToArray();

                        var scatter = TrendPlot.Plot.Add.Scatter(xs, ys);
                        scatter.MarkerSize = 7;
                        
                        // 强制 X 轴只显示整数刻度
                        int repeats = vm.CurrentSample != null ? vm.CurrentSample.Repeats : validReps.Count;
                        double[] tickPositions = Enumerable.Range(1, repeats).Select(i => (double)i).ToArray();
                        string[] tickLabels = Enumerable.Range(1, repeats).Select(i => i.ToString()).ToArray();
                        TrendPlot.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(tickPositions, tickLabels);
                        scatter.LineWidth = 2;
                        scatter.Color = ScottPlot.Colors.C0; // 统一使用全谱图的默认蓝色
                        scatter.MarkerShape = ScottPlot.MarkerShape.FilledCircle;

                        TrendPlot.Plot.Axes.SetLimitsX(0.5, repeats + 0.5);
                        
                        // Y轴自适应，留出一点裕量
                        double yMin = ys.Min();
                        double yMax = ys.Max();
                        double padding = (yMax - yMin) * 0.2;
                        if (padding == 0) padding = ys[0] * 0.1; // 如果所有点一样高
                        if (padding == 0) padding = 10;
                        
                        TrendPlot.Plot.Axes.SetLimitsY(yMin - padding, yMax + padding);
                    }
                    else
                    {
                        if (vm.CurrentSample != null)
                        {
                            TrendPlot.Plot.Axes.SetLimitsX(0.5, vm.CurrentSample.Repeats + 0.5);
                        }
                        TrendPlot.Plot.Axes.SetLimitsY(0, 100);
                    }
                }
                
                TrendPlot.Refresh();
            });
        }
    }
}