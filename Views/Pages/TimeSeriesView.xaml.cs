using GD_ControlCenter_WPF.ViewModels;
using ScottPlot.WPF;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

/*
 * 文件名: TimeSeriesView.xaml.cs
 * 描述: 多通道时序滚动图表的渲染引擎类。
 * 本类通过 DispatcherTimer 实现了“渲染节流”机制，将底层 100Hz 的高频数据推流降频至 20FPS（50ms/帧）进行 UI 绘制。
 * 核心策略：ViewModel 负责高频异步写入内存，View 层负责定时抓取静态数据切片进行离线绘图，杜绝高频重绘引发的 UI 线程假死。
 * 维护指南: 
 * 1. 绘图引擎依赖于 ScottPlot.WPF，升级库版本时需注意 Axis 接口的兼容性。
 * 2. 页面卸载（Unloaded）时必须停止定时器以释放 CPU 资源。
 */

namespace GD_ControlCenter_WPF.Views.Pages
{
    /// <summary>
    /// TimeSeriesView.xaml 的交互逻辑：实现高性能多通道动态波形渲染。
    /// </summary>
    public partial class TimeSeriesView : UserControl
    {
        #region 1. 状态与定时器

        /// <summary>
        /// 存储当前视图中所有活跃的 WpfPlot 控件引用。
        /// 用于在定时器触发时进行批量渲染轮询。
        /// </summary>
        private readonly List<WpfPlot> _activePlots = new();

        /// <summary>
        /// 渲染节流定时器：控制全屏图表的统一刷新频率。
        /// </summary>
        private readonly DispatcherTimer _renderTimer = new();

        #endregion

        #region 2. 初始化与生命周期管理

        /// <summary>
        /// 初始化时序图视图组件，配置渲染步长与生命周期钩子。
        /// </summary>
        public TimeSeriesView()
        {
            InitializeComponent();

            // 设定 50ms 刷新步长（对应 20FPS），兼顾流畅度与 CPU 开销
            _renderTimer.Interval = TimeSpan.FromMilliseconds(50);

            // 注册全局渲染回调
            _renderTimer.Tick += (s, e) =>
            {
                // 遍历每一个动态生成的通道图表
                foreach (var plotControl in _activePlots)
                {
                    // 获取各通道独立的 ViewModel 数据上下文
                    if (plotControl.DataContext is TimeSeriesViewModel.TimeSeriesChartItem item)
                    {
                        // 仅在当前帧周期内有新数据点到达时才重绘画布
                        if (item.HasNewData)
                        {
                            item.HasNewData = false;
                            RenderStaticChart(plotControl, item);
                        }
                    }
                }
            };

            // 生命周期绑定：确保后台页面不消耗渲染性能
            this.Loaded += (s, e) => _renderTimer.Start();
            this.Unloaded += (s, e) => _renderTimer.Stop();
        }

        #endregion

        #region 3. 动态控件挂载事件

        /// <summary>
        /// 当 ItemsControl 实例化新的 WpfPlot 控件并加载至视觉树时触发。
        /// </summary>
        private void OnPlotLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not WpfPlot plotControl) return;
            if (plotControl.DataContext is not TimeSeriesViewModel.TimeSeriesChartItem item) return;

            // 将新生成的控件加入实时监控队列
            if (!_activePlots.Contains(plotControl))
            {
                _activePlots.Add(plotControl);
            }

            // 初始绘制：确保切换回页面时能立即看到历史轨迹
            RenderStaticChart(plotControl, item);
        }

        /// <summary>
        /// 当通道被移除或图表控件从视觉树中卸载时触发。
        /// </summary>
        private void OnPlotUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is WpfPlot plotControl)
            {
                _activePlots.Remove(plotControl);
            }
        }

        #endregion

        #region 4. 核心渲染引擎逻辑

        /// <summary>
        /// 执行单幅图表的物理渲染。
        /// 包含内存切片提取、散点图构建及坐标轴自适应计算。
        /// </summary>
        /// <param name="plotControl">ScottPlot 控件实例。</param>
        /// <param name="item">关联的通道数据模型。</param>
        private void RenderStaticChart(WpfPlot plotControl, TimeSeriesViewModel.TimeSeriesChartItem item)
        {
            // 1. 重置画布
            plotControl.Plot.Clear();

            double[] xs;
            double[] ys;

            // --- 2. 线程同步与数据切片 ---
            // 锁定 ViewModel 的原始数据源，快速拷贝出一份静态副本用于 UI 线程绘图。
            // 这样绘图过程不会阻塞后台硬件继续往集合中 Push 数据。
            lock (item.SyncRoot)
            {
                if (item.TimePoints.Count == 0)
                {
                    // 若无数据，设定默认可见域并刷新空画布
                    plotControl.Plot.Axes.SetLimits(0, 5, 0, 100);
                    plotControl.Refresh();
                    return;
                }

                xs = item.TimePoints.ToArray();
                ys = item.IntensityPoints.ToArray();
            }

            // --- 3. 添加散点图形（Scatter） ---
            var scatter = plotControl.Plot.Add.Scatter(xs, ys);

            // 视觉优化：隐藏离散点，仅显示连续线条
            scatter.MarkerSize = 0;
            scatter.Color = item.LineColor;
            scatter.LineWidth = 1.5f;
            scatter.LegendText = item.Title;

            // --- 4. 全局样式配置 ---
            plotControl.Plot.Axes.Left.TickLabelStyle.ForeColor = ScottPlot.Colors.Black;
            plotControl.Plot.Axes.Left.FrameLineStyle.Color = ScottPlot.Colors.Black;
            plotControl.Plot.Axes.Bottom.TickLabelStyle.ForeColor = ScottPlot.Colors.Black;
            plotControl.Plot.Axes.Bottom.FrameLineStyle.Color = ScottPlot.Colors.Black;
            plotControl.Plot.Grid.MajorLineColor = ScottPlot.Colors.Black.WithAlpha(0.1);
            plotControl.Plot.ShowLegend();
            plotControl.Plot.Legend.Alignment = ScottPlot.Alignment.UpperLeft;
            plotControl.Plot.Legend.FontColor = ScottPlot.Colors.Black;
            plotControl.Plot.Legend.BackgroundColor = ScottPlot.Colors.White;

            // --- 5. 坐标轴动态接管 ---

            // X 轴逻辑：实现“波形左移”的滚动效果
            double minX = xs[0];  // 最小时间戳（窗口左侧）
            double maxX = xs[^1]; // 最大时间戳（窗口右侧）

            // 确保窗口跨度至少为 5 秒
            double xSpan = Math.Max(5.0, maxX - minX);

            // 右侧预留 5% 的“呼吸空间”，防止波形贴死屏幕边缘
            double rightEdge = maxX + xSpan * 0.05;
            plotControl.Plot.Axes.SetLimitsX(minX, rightEdge);

            // Y 轴逻辑：自适应幅值缩放
            double minY = ys.Min();
            double maxY = ys.Max();
            double ySpan = maxY - minY;

            // 死区保护：若强度为直线（Span 为 0），给予 10 Counts 的固定量程
            if (ySpan < 0.0001) ySpan = 10.0;

            // 上下各留 10% 安全边距
            plotControl.Plot.Axes.SetLimitsY(minY - ySpan * 0.1, maxY + ySpan * 0.1);

            // 推送绘图缓冲区至屏幕
            plotControl.Refresh();
        }

        #endregion
    }
}