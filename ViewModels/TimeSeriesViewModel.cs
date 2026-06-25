using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Services;
using GD_ControlCenter_WPF.Services.Spectrometer;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

/*
 * 文件名: TimeSeriesViewModel.cs
 * 描述: 多通道时序监测视图模型。负责光谱特征峰强度随时间变化曲线的数据管理、UI 节流渲染调度及自动化记录导出。
 * 维护指南: 
 * 1. 采用“双轨数据流”架构：UI 轨道通过 FIFO 机制维持轻量化点数（10000点），后台轨道则无限制缓冲直至 Excel 落盘。
 * 2. 底层硬件数据在非托管线程中高频注入，通过锁定每个通道的 SyncRoot 确保 View 层读取渲染时不发生集合冲突。
 */

namespace GD_ControlCenter_WPF.ViewModels
{
    /// <summary>
    /// 时序图监测视图模型：负责多通道数据的泵入、缓存与业务流转。
    /// </summary>
    public partial class TimeSeriesViewModel : ObservableObject
    {
        #region 1. 依赖服务与状态属性

        /// <summary>
        /// JSON 配置管理服务。
        /// 用于读取时序图监控节点的波长配置。
        /// </summary>
        private readonly JsonConfigService _configService;

        /// <summary>
        /// 特征峰追踪服务。
        /// 作为数据源，提供经过寻峰算法修正后的实时物理坐标。
        /// </summary>
        private readonly PeakTrackingService _peakTrackingService;

        /// <summary>
        /// 核心状态：当前是否正处于后台连续采集并缓存数据的录制状态。
        /// 变更时会级联触发 UI 按钮文本、图标及颜色的状态更新。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RecordText))]
        [NotifyPropertyChangedFor(nameof(RecordIcon))]
        [NotifyPropertyChangedFor(nameof(RecordButtonColor))]
        private bool _isRecording;

        /// <summary>
        /// 视图布局模式标识。
        /// True 为 2xN 的矩阵展示模式，False 为单列纵向排列模式。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ViewModeText))]
        [NotifyPropertyChangedFor(nameof(ViewModeIcon))]
        private bool _isMatrixView;

        /// <summary>
        /// 前端渲染容器（UniformGrid）的动态列数。
        /// 内部根据视图模式与通道总数自动计算。
        /// </summary>
        [ObservableProperty]
        private int _gridColumns = 1;

        /// <summary>
        /// 通道图表数据项集合。
        /// 每一项对应 UI 上展示的一个子图表（一个特定的波长追踪点）。
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<TimeSeriesChartItem> _charts = new();

        #endregion

        #region 2. 内部计时与高性能限制

        /// <summary>
        /// 相对时间高精度计时器。
        /// 用于为录制期间的每一帧打上以秒为单位的时间戳（X 轴物理坐标）。
        /// </summary>
        private readonly Stopwatch _stopwatch = new();

        /// <summary>
        /// 物理限制：前端 UI 渲染允许存在的最大历史点数。
        /// 设置为 10000 以平衡长时间监测的视觉连续性与系统内存负载。
        /// </summary>
        private const int MAX_UI_POINTS = 10000;

        // --- UI 按钮动态派生属性 ---
        public string RecordText => IsRecording ? "停止记录" : "开始记录";
        public string RecordIcon => IsRecording ? "StopCircle" : "PlayCircle";
        public string RecordButtonColor => IsRecording ? "#f44336" : "#673ab7";
        public string ViewModeText => IsMatrixView ? "列表视图" : "矩阵视图";
        public string ViewModeIcon => IsMatrixView ? "ViewSequential" : "ViewGrid";

        #endregion

        #region 3. 多线程异步缓存基建

        /// <summary>
        /// 核心并发同步锁。
        /// 用于在后台写入缓存池与前端清理导出之间的资源竞争保护。
        /// </summary>
        private readonly object _cacheLock = new object();

        /// <summary>
        /// 后台无限数据缓存池。
        /// 临时存储录制期间的所有光谱快照，待停止后一次性批量写入 Excel。
        /// </summary>
        private readonly List<Models.Spectrometer.TimeSeriesSnapshot> _timeSeriesCache = new();

        /// <summary>
        /// 录制期表头锁定快照。
        /// 记录启动瞬间的通道名称列表，防止导出过程中由于配置更改导致的 Excel 格式错乱。
        /// </summary>
        private readonly List<string> _lockedTrackPointNames = new();

        /// <summary>
        /// 硬件参数上下文：积分时间（ms）。
        /// </summary>
        private float _currentIntegrationTime;

        /// <summary>
        /// 硬件参数上下文：平均次数。
        /// </summary>
        private uint _currentAveragingCount;

        #endregion

        #region 4. 构造初始化与消息订阅

        /// <summary>
        /// 构造时序图视图模型并注册高频数据总线监听。
        /// </summary>
        public TimeSeriesViewModel(JsonConfigService configService, PeakTrackingService peakTrackingService)
        {
            _configService = configService;
            _peakTrackingService = peakTrackingService;

            // 注册对底层光谱消息的快速拦截
            WeakReferenceMessenger.Default.Register<SpectralDataMessage>(this, (r, m) =>
            {
                // 更新当前硬件环境参数，用于 Excel 导出信息头
                _currentIntegrationTime = m.IntegrationTime;
                _currentAveragingCount = m.AveragingCount;

                // 若处于录制中，则启动双轨泵水引擎
                if (IsRecording) InjectDataToCharts();
            });

            // 初始从配置文件加载已保存的监控节点
            ReloadChartsFromConfig();
        }

        #endregion

        #region 5. 数据泵引擎

        /// <summary>
        /// 核心数据泵：将特征峰服务中的最新物理坐标拆解、映射并分发至各图表通道。
        /// 职责：同步执行前端 FIFO 更新与后台无限缓存追加。
        /// </summary>
        private void InjectDataToCharts()
        {
            double currentTime = _stopwatch.Elapsed.TotalSeconds;
            double[] currentIntensities = new double[Charts.Count];
            int activePeakCount = 0;

            // 步骤 A：遍历通道集合，提取对应波长的实时强度
            for (int i = 0; i < Charts.Count; i++)
            {
                var chart = Charts[i];
                var matchedPeak = _peakTrackingService.TrackedPeaks
                    .FirstOrDefault(p => Math.Abs(p.BaseWavelength - chart.TargetWavelength) < 0.01);

                if (matchedPeak != null)
                {
                    currentIntensities[i] = matchedPeak.CurrentIntensity;
                    activePeakCount++;

                    // 步骤 B：UI 轨道更新 (带线程隔离保护)
                    lock (chart.SyncRoot)
                    {
                        chart.TimePoints.Add(currentTime);
                        chart.IntensityPoints.Add(matchedPeak.CurrentIntensity);

                        // FIFO 淘汰逻辑：若点数溢出，从队首移除最老的数据
                        if (chart.TimePoints.Count > MAX_UI_POINTS)
                        {
                            chart.TimePoints.RemoveAt(0);
                            chart.IntensityPoints.RemoveAt(0);
                        }
                        chart.HasNewData = true; // 触发 UI 渲染脏检查
                    }
                }
            }

            // 步骤 C：异常安全拦截
            // 若监测到主界面所有追踪点均被删除，立即强行保存已有数据并停止，防止产生零数据垃圾帧。
            if (activePeakCount == 0)
            {
                Application.Current.Dispatcher.Invoke(() => { if (IsRecording) _ = ToggleRecordingAsync(); });
                return;
            }

            // 步骤 D：后台轨道更新 (压入 Excel 导出队列)
            lock (_cacheLock)
            {
                _timeSeriesCache.Add(new Models.Spectrometer.TimeSeriesSnapshot
                {
                    Timestamp = DateTime.Now,
                    Intensities = currentIntensities
                });
            }
        }

        #endregion

        #region 6. 录制生命周期管理

        /// <summary>
        /// 执行时序记录的状态切换。
        /// 职责：控制任务启动、内存切割、异步落盘以及 Excel 服务调度。
        /// </summary>
        [RelayCommand]
        private async Task ToggleRecordingAsync()
        {
            if (IsRecording)
            {
                // --- 流程 A：停止录制并安全落盘 ---
                IsRecording = false;
                _stopwatch.Stop();

                List<Models.Spectrometer.TimeSeriesSnapshot> dataToSave;
                List<string> columnsToSave;

                // 原子级剪切操作：瞬间清空缓存并转移指针，随后立即释放锁以恢复系统响应
                lock (_cacheLock)
                {
                    dataToSave = _timeSeriesCache.ToList();
                    columnsToSave = _lockedTrackPointNames.ToList();
                    _timeSeriesCache.Clear();
                    _lockedTrackPointNames.Clear();
                }

                if (dataToSave.Count > 0)
                {
                    var config = _configService.Load();
                    string dirPath = string.IsNullOrWhiteSpace(config.TimeSeriesSaveDirectory)
                        ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                        : config.TimeSeriesSaveDirectory;

                    string fullPath = Path.Combine(dirPath, $"TimeSeries_Auto_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");

                    using var exportService = new ExcelExportService();
                    bool success = await exportService.ExportTimeSeriesBulkAsync(
                        fullPath, columnsToSave, dataToSave, _currentIntegrationTime, _currentAveragingCount);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (success) MessageBox.Show($"记录已完成并导出至：\n{fullPath}", "自动导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
                        else MessageBox.Show("导出失败：目标文件可能已被手动打开占用。", "IO 错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                }
            }
            else
            {
                // --- 流程 B：初始化录制上下文 ---
                // 启动校验：确保当前配置的波长点在寻峰服务中处于活跃追踪状态
                int activeCount = Charts.Count(c => _peakTrackingService.TrackedPeaks.Any(p => Math.Abs(p.BaseWavelength - c.TargetWavelength) < 0.01));
                if (activeCount == 0)
                {
                    MessageBox.Show("当前监控列表中的波长点未被主界面标记追踪，请先标记寻峰点。", "操作拒绝", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 锁定当前业务快照
                lock (_cacheLock)
                {
                    _timeSeriesCache.Clear();
                    _lockedTrackPointNames.Clear();
                    _lockedTrackPointNames.AddRange(Charts.Select(c => c.Title));
                }

                // 清空 UI 历史波形
                foreach (var chart in Charts)
                {
                    lock (chart.SyncRoot) { chart.TimePoints.Clear(); chart.IntensityPoints.Clear(); chart.HasNewData = true; }
                }

                _stopwatch.Restart();
                IsRecording = true;
            }
        }

        /// <summary>
        /// 紧急静默落盘逻辑。
        /// 专供 App 意外退出时调用，通过强制同步阻塞确保内存数据在进程结束前写入桌面。
        /// </summary>
        public void EmergencySave()
        {
            if (!IsRecording || _timeSeriesCache.Count == 0) return;
            _stopwatch.Stop();

            List<Models.Spectrometer.TimeSeriesSnapshot> dataToSave;
            List<string> columnsToSave;

            lock (_cacheLock) { dataToSave = _timeSeriesCache.ToList(); columnsToSave = _lockedTrackPointNames.ToList(); }

            var config = _configService.Load();
            string dirPath = string.IsNullOrWhiteSpace(config.TimeSeriesSaveDirectory) ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop) : config.TimeSeriesSaveDirectory;
            string fullPath = Path.Combine(dirPath, $"TimeSeries_Auto_{DateTime.Now:yyyyMMdd_HHmmss}_Exit.xlsx");

            using var exportService = new ExcelExportService();
            // 使用同步阻塞，确保在 Windows 强制杀进程前完成磁盘操作
            exportService.ExportTimeSeriesBulkAsync(fullPath, columnsToSave, dataToSave, _currentIntegrationTime, _currentAveragingCount).GetAwaiter().GetResult();
        }

        #endregion

        #region 7. UI 交互控制

        /// <summary> 拦截矩阵视图标识变更，动态重算排版列数。 </summary>
        partial void OnIsMatrixViewChanged(bool value) => UpdateGridColumns();

        /// <summary> 切换视图模式指令。 </summary>
        [RelayCommand] private void ToggleViewMode() => IsMatrixView = !IsMatrixView;

        /// <summary> 重算列数：矩阵模式下且通道数大于 1 时，采用双列排版。 </summary>
        private void UpdateGridColumns() => GridColumns = (IsMatrixView && Charts.Count > 1) ? 2 : 1;

        /// <summary> 弹出时序监控节点（波长/颜色）的配置对话框。 </summary>
        [RelayCommand]
        private void OpenSamplingConfig()
        {
            var window = new GD_ControlCenter_WPF.Views.Dialogs.TimeSeriesSamplingConfigWindow();
            window.DataContext = new GD_ControlCenter_WPF.ViewModels.Dialogs.TimeSeriesSamplingConfigViewModel(_configService, _peakTrackingService, () => window.Close());
            window.Owner = Application.Current.MainWindow;
            window.ShowDialog();

            ReloadChartsFromConfig(); // 对话框关闭后立即刷新物理通道列表
        }

        /// <summary>
        /// 通道重载逻辑：根据本地 JSON 配置动态构建时序监控实体。
        /// 具备双向过滤：必须在配置中勾选，且当前主界面必须已开启该特征峰的寻峰追踪。
        /// </summary>
        private void ReloadChartsFromConfig()
        {
            var config = _configService.Load();
            Charts.Clear();

            var validNodes = config.TimeSeriesSampleNodes?
                .Where(n => !string.IsNullOrWhiteSpace(n.Name) && n.SelectedPeakX.HasValue)
                .Where(n => _peakTrackingService.TrackedPeaks.Any(p => Math.Abs(p.BaseWavelength - n.SelectedPeakX!.Value) < 0.01))
                .ToList();

            if (validNodes == null || validNodes.Count == 0)
            {
                Charts.Add(new TimeSeriesChartItem("等待特征峰标记...", 0, 0));
            }
            else
            {
                for (int i = 0; i < validNodes.Count; i++)
                {
                    var node = validNodes[i];
                    Charts.Add(new TimeSeriesChartItem($"{node.Name} ({node.SelectedPeakX!.Value:F2} nm)", node.SelectedPeakX!.Value, i));
                }
            }
            UpdateGridColumns();
        }

        #endregion

        #region 8. 嵌套类：子通道图表数据源

        /// <summary>
        /// 单个监测通道的数据承载体。
        /// 封装了折线图的点集、细粒度同步锁以及 ScottPlot 线条配色方案。
        /// </summary>
        public partial class TimeSeriesChartItem : ObservableObject
        {
            /// <summary> 通道显示名称（含波长说明）。 </summary>
            [ObservableProperty] private string _title;

            /// <summary> 关联的物理目标波长（nm）。 </summary>
            [ObservableProperty] private double _targetWavelength;

            /// <summary> 时间坐标点集 (X)。 </summary>
            public List<double> TimePoints { get; } = new();

            /// <summary> 物理强度点集 (Y)。 </summary>
            public List<double> IntensityPoints { get; } = new();

            /// <summary> 通道级专用锁：防止后台 Inject 写入与 UI 渲染读取发生碰撞。 </summary>
            public object SyncRoot { get; } = new object();

            /// <summary> 系统自动分配的高对比度绘图颜色。 </summary>
            public ScottPlot.Color LineColor { get; }

            /// <summary> 脏数据标识：由 View 层定时器轮询，判定当前通道是否需要重绘。 </summary>
            public bool HasNewData { get; set; } = false;

            public TimeSeriesChartItem(string title, double targetWavelength, int colorIndex)
            {
                Title = title;
                TargetWavelength = targetWavelength;

                // 科学绘图标准色板（高对比度）
                string[] hexColors = { "#1976D2", "#D32F2F", "#388E3C", "#F57C00", "#7B1FA2", "#0097A7", "#E64A19", "#689F38", "#C2185B", "#5D4037" };
                LineColor = ScottPlot.Color.FromHex(hexColors[colorIndex % hexColors.Length]);
            }
        }

        #endregion
    }
}