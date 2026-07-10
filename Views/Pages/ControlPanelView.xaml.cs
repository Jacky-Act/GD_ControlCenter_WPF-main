using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Models.Spectrometer;
using GD_ControlCenter_WPF.Services.Spectrometer;
using GD_ControlCenter_WPF.Services.Spectrometer.Logic;
using GD_ControlCenter_WPF.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using ScottPlot;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

/*
 * 文件名: ControlPanelView.xaml.cs
 * 描述: 主控制面板光谱图表渲染引擎。支持实时波形与参考波形的双图层叠加、渲染节流（33ms）、触控框选放大及坐标轴状态持久化。
 * 本类作为 View 层，仅负责响应消息总线的数据推送并驱动 ScottPlot 进行物理绘图，不处理任何业务计算。
 * 维护指南: 
 * 1. 渲染节流阀限制为 30FPS，修改 _lastRenderTime 逻辑需考虑 UI 线程负载。
 * 2. 坐标轴记忆功能通过 SaveLimitsToViewModel 实现，依赖于 ViewModel 层的 SavedAxisLimits 属性。
 * 3. 触控框选逻辑通过拦截 Preview 事件屏蔽了 ScottPlot 原生的右键平移。
 */

namespace GD_ControlCenter_WPF.Views.Pages
{
    /// <summary>
    /// ControlPanelView.xaml 的交互逻辑：处理高性能光谱图表渲染与交互。
    /// </summary>
    public partial class ControlPanelView : UserControl
    {
        #region 1. 渲染状态与缓存变量

        /// <summary>
        /// 实时波形曲线颜色（主题紫色）。
        /// </summary>
        private readonly string _primaryColorHex = "#673ab7";

        /// <summary>
        /// 寻峰服务引用，用于将图表点击坐标映射至后台算法。
        /// </summary>
        private readonly PeakTrackingService _peakTrackingService = null!;

        /// <summary>
        /// 标识是否为首帧数据，用于初始化坐标轴视图规则。
        /// </summary>
        private bool _isFirstFrame = true;

        /// <summary>
        /// 记录上一次物理渲染的时刻，实现 30FPS 节流限制。
        /// </summary>
        private DateTime _lastRenderTime = DateTime.MinValue;

        /// <summary>
        /// 图表允许显示的物理最大边界限制。
        /// </summary>
        private AxisLimits? _maxLimits = null;

        /// <summary>
        /// 记录最后一次右键点击处的 X 轴波长坐标。
        /// </summary>
        private double _lastRightClickX = 0;

        /// <summary>
        /// 指示图表是否处于持续自动量程追踪模式。
        /// </summary>
        private bool _isContinuousAutoScale = true;

        /// <summary>
        /// 缓存当前最新的实时光谱数据快照。
        /// </summary>
        private SpectralData? _currentData;

        /// <summary>
        /// 缓存当前载入的参考文件数据，用于底层图层绘制。
        /// </summary>
        private SpectralData? _cachedReferenceData = null;

        /// <summary>
        /// 框选放大起始点（屏幕像素坐标）。
        /// </summary>
        private Point _boxZoomStart;

        /// <summary>
        /// 标识当前是否正处于框选操作中。
        /// </summary>
        private bool _isBoxZooming = false;

        /// <summary>
        private ControlPanelViewModel? _vm;

        // --- 零分配渲染缓存 ---
        private ScottPlot.Plottables.Scatter? _livePlot;
        private double[]? _cachedWavelengths;
        private double[]? _cachedIntensities;
        
        private ScottPlot.Plottables.Scatter? _refPlot;
        private SpectralData? _lastRenderedRefData;

        private List<ScottPlot.Plottables.VerticalLine> _peakLines = new();
        private List<ScottPlot.Plottables.Text> _peakTexts = new();

        #endregion

        #region 2. 初始化与 UI 事件订阅

        /// <summary>
        /// 构造函数：初始化图表控件、订阅输入事件及配置自定义菜单。
        /// </summary>
        public ControlPanelView()
        {
            InitializeComponent();

            this.DataContextChanged += OnDataContextChanged;
            _peakTrackingService = App.Services.GetRequiredService<PeakTrackingService>();

            // 绑定交互事件
            SpecPlot.PreviewMouseRightButtonDown += OnPlotRightClick;
            SpecPlot.PreviewMouseLeftButtonDown += OnPlotLeftMouseDown;
            SpecPlot.PreviewMouseMove += OnPlotMouseMove;
            SpecPlot.PreviewMouseLeftButtonUp += OnPlotLeftMouseUp;

            // 菜单与边距配置
            SetupCustomMenu();
            SpecPlot.Plot.Axes.Margins(0, 0.1);

            // 坐标轴持久化监听：鼠标松开或滚轮停止时保存当前视野
            SpecPlot.PreviewMouseUp += (s, e) =>
            {
                _isContinuousAutoScale = false;
                SaveLimitsToViewModel();
            };

            SpecPlot.PreviewMouseWheel += (s, e) =>
            {
                _isContinuousAutoScale = false;
                Dispatcher.BeginInvoke(new Action(SaveLimitsToViewModel), System.Windows.Threading.DispatcherPriority.Background);
            };

            // 页面卸载时执行最后一次保存
            this.Unloaded += (s, e) => SaveLimitsToViewModel();
        }

        /// <summary>
        /// 将当前图表的视图边界限制同步保存至 ViewModel。
        /// </summary>
        private void SaveLimitsToViewModel()
        {
            if (_vm != null && !_isFirstFrame)
            {
                var limits = SpecPlot.Plot.Axes.GetLimits();
                _vm.SavedAxisLimits = new double[] { limits.Left, limits.Right, limits.Bottom, limits.Top };
            }
        }

        /// <summary>
        /// 配置图表右键自定义业务菜单（寻峰、移除标记）。
        /// </summary>
        private void SetupCustomMenu()
        {
            SpecPlot.Menu?.Clear();

            SpecPlot.Menu?.Add("寻峰", (plotControl) =>
            {
                if (_currentData != null)
                {
                    // 动态计算 20 像素对应的波长跨度作为寻峰窗口
                    double captureWindow = CalculateToleranceByPixels(20, 0.5);
                    double snappedX = SpectrometerLogic.GetActualPeakWavelength(_currentData, _lastRightClickX, captureWindow);
                    _peakTrackingService.AddPeak(snappedX);
                    RenderPlot(_currentData);
                }
            });

            SpecPlot.Menu?.Add("去除附近一条", (plotControl) =>
            {
                if (_currentData != null)
                {
                    double removeTolerance = CalculateToleranceByPixels(50, 5.0);
                    _peakTrackingService.RemovePeakNear(_lastRightClickX, removeTolerance);
                    RenderPlot(_currentData);
                }
            });

            SpecPlot.Menu?.Add("去除全部", (plotControl) =>
            {
                _peakTrackingService.ClearAll();
                if (_currentData != null) RenderPlot(_currentData);
                else SpecPlot.Refresh();
            });
        }

        /// <summary>
        /// 处理右键点击：将点击像素转换为图表内部的物理波长坐标。
        /// </summary>
        private void OnPlotRightClick(object sender, MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(SpecPlot);
            double dpiScaleX = 1.0, dpiScaleY = 1.0;

            PresentationSource source = PresentationSource.FromVisual(SpecPlot);
            if (source?.CompositionTarget != null)
            {
                dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
                dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
            }

            float physicalX = (float)(pos.X * dpiScaleX);
            float physicalY = (float)(pos.Y * dpiScaleY);

            var coordinates = SpecPlot.Plot.GetCoordinates(physicalX, physicalY);
            _lastRightClickX = coordinates.X;
        }

        #endregion

        #region 3. 消息总线接管逻辑

        /// <summary>
        /// 响应 DataContext 变更：注销旧订阅并重新绑定全局消息监听器。
        /// </summary>
        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            WeakReferenceMessenger.Default.UnregisterAll(this);

            if (e.NewValue is ControlPanelViewModel vm)
            {
                _vm = vm;
            }

            // 数据流订阅：实时光谱
            WeakReferenceMessenger.Default.Register<SpectralDataMessage>(this, (r, m) =>
            {
                OnPlotUpdateRequested(m.Value);
            });

            // 路由订阅：参考图层控制
            WeakReferenceMessenger.Default.Register<LoadReferencePlotMessage>(this, (r, m) =>
            {
                Dispatcher.Invoke(() =>
                {
                    _cachedReferenceData = m.Value;
                    RenderPlot(_currentData);
                    if (_currentData == null)
                    {
                        SpecPlot.Plot.Axes.AutoScale();
                        SpecPlot.Refresh();
                    }
                });
            });

            WeakReferenceMessenger.Default.Register<ClearReferencePlotMessage>(this, (r, m) =>
            {
                Dispatcher.Invoke(() => { _cachedReferenceData = null; RenderPlot(_currentData); });
            });

            WeakReferenceMessenger.Default.Register<ClearWaveformMessage>(this, (r, m) =>
            {
                Dispatcher.Invoke(() => { _currentData = null; RenderPlot(null); });
            });

            // 交互订阅：量程切换请求
            WeakReferenceMessenger.Default.Register<AutoRangeRequestMessage>(this, (r, m) =>
            {
                Dispatcher.Invoke(() =>
                {
                    _isContinuousAutoScale = true;
                    if (_currentData != null) RenderPlot(_currentData);
                    else { SpecPlot.Plot.Axes.Rules.Clear(); SpecPlot.Plot.Axes.AutoScale(); SpecPlot.Refresh(); }
                });
            });

            WeakReferenceMessenger.Default.Register<MaxRangeRequestMessage>(this, (r, m) =>
            {
                Dispatcher.Invoke(() =>
                {
                    _isContinuousAutoScale = false;
                    if (_maxLimits.HasValue) SpecPlot.Plot.Axes.SetLimits(_maxLimits.Value);
                    SpecPlot.Refresh();
                    SaveLimitsToViewModel();
                });
            });

            // 恢复视图状态
            RestoreViewState();
        }

        /// <summary>
        /// 从 ViewModel 恢复历史视图边界与数据图层。
        /// </summary>
        private void RestoreViewState()
        {
            if (_vm == null) return;
            bool needRefresh = false;

            if (_vm.IsReferenceFileLoaded && _vm.LoadedReferenceData != null)
            {
                _cachedReferenceData = _vm.LoadedReferenceData;
                needRefresh = true;
            }

            if (_vm.LatestSpectralData != null)
            {
                _currentData = _vm.LatestSpectralData;
                needRefresh = true;
            }

            if (needRefresh)
            {
                Dispatcher.Invoke(() =>
                {
                    RenderPlot(_currentData);
                    if (_currentData == null)
                    {
                        if (_vm.SavedAxisLimits != null)
                            SpecPlot.Plot.Axes.SetLimits(_vm.SavedAxisLimits[0], _vm.SavedAxisLimits[1], _vm.SavedAxisLimits[2], _vm.SavedAxisLimits[3]);
                        else
                            SpecPlot.Plot.Axes.AutoScale();
                        _isFirstFrame = false;
                        SpecPlot.Refresh();
                    }
                });
            }
        }

        #endregion

        #region 4. 图表核心渲染引擎 (Render Engine)

        /// <summary>
        /// 执行异步渲染节流，确保 UI 线程不被高频采集数据淹没。
        /// </summary>
        private void OnPlotUpdateRequested(SpectralData data)
        {
            if ((DateTime.Now - _lastRenderTime).TotalMilliseconds < 33) return;
            _lastRenderTime = DateTime.Now;
            
            Dispatcher.BeginInvoke(new Action(() => 
            {
                if (!this.IsVisible) return;
                RenderPlot(data);
            }));
        }

        /// <summary>
        /// 物理重绘方法：协调参考层、实时层及寻峰标注层的叠加绘制。
        /// </summary>
        private void RenderPlot(SpectralData? liveData)
        {
            // 底层：参考波形 (红色虚线)
            if (_cachedReferenceData != null)
            {
                if (_refPlot == null || _cachedReferenceData != _lastRenderedRefData)
                {
                    if (_refPlot != null) SpecPlot.Plot.Remove(_refPlot);
                    _refPlot = SpecPlot.Plot.Add.Scatter(_cachedReferenceData.Wavelengths, _cachedReferenceData.Intensities);
                    _refPlot.MarkerSize = 0; _refPlot.LineWidth = 1.0f; _refPlot.Color = ScottPlot.Color.FromHex("#F44336");
                    _lastRenderedRefData = _cachedReferenceData;
                }
            }
            else
            {
                if (_refPlot != null) { SpecPlot.Plot.Remove(_refPlot); _refPlot = null; _lastRenderedRefData = null; }
            }

            // 顶层：实时波形 (紫色实线)
            if (liveData?.Wavelengths != null && liveData.Wavelengths.Length > 0)
            {
                _currentData = liveData;
                int len = liveData.Wavelengths.Length;

                if (_livePlot == null || _cachedWavelengths == null || _cachedWavelengths.Length != len)
                {
                    if (_livePlot != null) SpecPlot.Plot.Remove(_livePlot);

                    _cachedWavelengths = new double[len];
                    _cachedIntensities = new double[len];
                    Array.Copy(liveData.Wavelengths, _cachedWavelengths, len);
                    Array.Copy(liveData.Intensities, _cachedIntensities, len);

                    _livePlot = SpecPlot.Plot.Add.Scatter(_cachedWavelengths, _cachedIntensities);
                    _livePlot.MarkerSize = 0; _livePlot.LineWidth = 1.5f; _livePlot.Color = ScottPlot.Color.FromHex(_primaryColorHex);
                }
                else
                {
                    // 零分配原位拷贝
                    Array.Copy(liveData.Wavelengths, _cachedWavelengths, len);
                    Array.Copy(liveData.Intensities, _cachedIntensities, len);
                }

                HandleScalingLogic(liveData);

                // 标注层：寻峰垂直线
                DrawTrackedPeaks(SpecPlot.Plot.Axes.GetLimits().Top);
            }
            else
            {
                if (_livePlot != null) { SpecPlot.Plot.Remove(_livePlot); _livePlot = null; }
                
                // 清理可能遗留的峰线
                foreach (var line in _peakLines) SpecPlot.Plot.Remove(line);
                foreach (var txt in _peakTexts) SpecPlot.Plot.Remove(txt);
                _peakLines.Clear(); _peakTexts.Clear();
            }

            SpecPlot.Refresh();
        }

        /// <summary>
        /// 处理坐标轴缩放逻辑与视野边界规则。
        /// </summary>
        private void HandleScalingLogic(SpectralData data)
        {
            if (_isFirstFrame)
            {
                _maxLimits = new AxisLimits(data.Wavelengths[0], data.Wavelengths[^1], -1000, 70000);
                SpecPlot.Plot.Axes.Rules.Clear();
                SpecPlot.Plot.Axes.Rules.Add(new ScottPlot.AxisRules.MaximumBoundary(SpecPlot.Plot.Axes.Bottom, SpecPlot.Plot.Axes.Left, _maxLimits.Value));

                if (_vm?.SavedAxisLimits != null)
                {
                    SpecPlot.Plot.Axes.SetLimits(_vm.SavedAxisLimits[0], _vm.SavedAxisLimits[1], _vm.SavedAxisLimits[2], _vm.SavedAxisLimits[3]);
                    _isContinuousAutoScale = false;
                }
                else
                {
                    SpecPlot.Plot.Axes.AutoScale();
                    _isContinuousAutoScale = true;
                }
                _isFirstFrame = false;
            }
            else if (_isContinuousAutoScale)
            {
                SpecPlot.Plot.Axes.AutoScale();
                if (_vm != null) _vm.SavedAxisLimits = null;
            }
        }

        /// <summary>
        /// 在图表上绘制被追踪的特征峰垂直标注线与数据文本。
        /// </summary>
        private void DrawTrackedPeaks(double yAxisMax)
        {
            foreach (var line in _peakLines) SpecPlot.Plot.Remove(line);
            foreach (var txt in _peakTexts) SpecPlot.Plot.Remove(txt);
            _peakLines.Clear();
            _peakTexts.Clear();

            foreach (var peak in _peakTrackingService.TrackedPeaks)
            {
                var vLine = SpecPlot.Plot.Add.VerticalLine(peak.CurrentWavelength);
                vLine.Color = ScottPlot.Color.FromHex("#F44336");
                vLine.LineWidth = 1.0f;
                _peakLines.Add(vLine);

                var txt = SpecPlot.Plot.Add.Text($"X: {peak.CurrentWavelength:F2}\nY: {peak.CurrentIntensity:F0}", peak.CurrentWavelength, yAxisMax);
                txt.LabelFontColor = ScottPlot.Color.FromHex("#F44336");
                txt.LabelFontSize = 14; txt.LabelBold = true;
                txt.LabelAlignment = ScottPlot.Alignment.UpperCenter;
                _peakTexts.Add(txt);
            }
        }

        #endregion

        #region 5. 辅助计算与触屏框选

        /// <summary>
        /// 将屏幕像素宽度转换为当前视图下的物理波长容差。
        /// </summary>
        private double CalculateToleranceByPixels(int pixelCount, double minTolerance)
        {
            double xAxisSpan = SpecPlot.Plot.Axes.Bottom.Range.Span;
            double nmPerPixel = SpecPlot.ActualWidth > 0 ? xAxisSpan / SpecPlot.ActualWidth : 0.1;
            return Math.Max(nmPerPixel * pixelCount, minTolerance);
        }

        private void OnPlotLeftMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (BtnTouchBoxZoom.IsChecked == true)
            {
                _isBoxZooming = true;
                _boxZoomStart = e.GetPosition(SpecPlot);
                e.Handled = true; // 拦截事件，屏蔽 ScottPlot 原生平移

                Canvas.SetLeft(ZoomRectangle, _boxZoomStart.X);
                Canvas.SetTop(ZoomRectangle, _boxZoomStart.Y);
                ZoomRectangle.Width = ZoomRectangle.Height = 0;
                ZoomRectangle.Visibility = Visibility.Visible;
                SpecPlot.CaptureMouse();
            }
        }

        private void OnPlotMouseMove(object sender, MouseEventArgs e)
        {
            if (_isBoxZooming)
            {
                Point current = e.GetPosition(SpecPlot);
                double x = Math.Min(_boxZoomStart.X, current.X);
                double y = Math.Min(_boxZoomStart.Y, current.Y);
                ZoomRectangle.Width = Math.Abs(_boxZoomStart.X - current.X);
                ZoomRectangle.Height = Math.Abs(_boxZoomStart.Y - current.Y);
                Canvas.SetLeft(ZoomRectangle, x); Canvas.SetTop(ZoomRectangle, y);
            }
        }

        private void OnPlotLeftMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isBoxZooming)
            {
                _isBoxZooming = false;
                SpecPlot.ReleaseMouseCapture();
                ZoomRectangle.Visibility = Visibility.Collapsed;
                e.Handled = true;

                Point end = e.GetPosition(SpecPlot);
                if (Math.Abs(end.X - _boxZoomStart.X) > 15) // 过滤微小抖动
                {
                    double dpiX = 1.0, dpiY = 1.0;
                    PresentationSource source = PresentationSource.FromVisual(SpecPlot);
                    if (source?.CompositionTarget != null) { dpiX = source.CompositionTarget.TransformToDevice.M11; dpiY = source.CompositionTarget.TransformToDevice.M22; }

                    var coord1 = SpecPlot.Plot.GetCoordinates((float)(_boxZoomStart.X * dpiX), (float)(_boxZoomStart.Y * dpiY));
                    var coord2 = SpecPlot.Plot.GetCoordinates((float)(end.X * dpiX), (float)(end.Y * dpiY));

                    _isContinuousAutoScale = false;
                    SpecPlot.Plot.Axes.SetLimits(Math.Min(coord1.X, coord2.X), Math.Max(coord1.X, coord2.X), Math.Min(coord1.Y, coord2.Y), Math.Max(coord1.Y, coord2.Y));
                    SpecPlot.Refresh();
                }
            }
        }

        #endregion
        private void ToggleButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            // 获取绑定在当前页面的 ViewModel
            if (this.DataContext is GD_ControlCenter_WPF.ViewModels.ControlPanelViewModel vm)
            {
                // 直接呼叫 ViewModel 里的切换方法！
                vm.ToggleMeasurementModeCommand.Execute(null);
            }
        }
    }
}