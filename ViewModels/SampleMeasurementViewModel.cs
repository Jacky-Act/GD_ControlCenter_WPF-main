using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Models.Spectrometer;
using GD_ControlCenter_WPF.Services;
using GD_ControlCenter_WPF.Services.Spectrometer;
using GD_ControlCenter_WPF.Services.Spectrometer.Logic;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized; 
using System.Linq;
using System.Windows; 
using Microsoft.Win32; 

namespace GD_ControlCenter_WPF.ViewModels
{
    /// <summary>
    /// 连续进样分析视图模型
    /// 职责：管理测量序列、处理跨页面寻峰同步、多通道数据采集、RSD 计算并导出结果。
    /// </summary>
    public partial class SampleMeasurementViewModel : ObservableObject
    {
        private readonly JsonConfigService _configService;
        private readonly ElementConfigViewModel _elementConfigVM;
        private readonly PeakTrackingService _peakTracker; // 全局寻峰大管家

        #region 1. UI 绑定属性

        [ObservableProperty] private ObservableCollection<SampleItemModel> _measurementSequence = new(); // 左侧样品序列
        [ObservableProperty] private SampleItemModel? _currentSample; // 当前正在测量或选中的样品
        [ObservableProperty] private bool _isCollecting; // 是否正在采集数据 (绑定到开始/停止按钮)
        [ObservableProperty] private ObservableCollection<string> _pickedElements = new(); // 下拉框中显示的已识别元素名
        [ObservableProperty] private string _selectedElement = string.Empty; // 当前下拉框选中的元素

        #endregion

        #region 2. 内部数据缓冲区

        // 多通道数据字典：Key为识别后的元素名，Value是采集到的该元素强度列表
        private Dictionary<string, List<double>> _multiChannelBuffer = new Dictionary<string, List<double>>();

        #endregion

        #region 3. 构造函数与初始化

        /// <summary>
        /// 构造函数：注入所有依赖服务，并注册全局消息监听器。
        /// </summary>
        public SampleMeasurementViewModel(JsonConfigService configService, ElementConfigViewModel elementConfigVM, PeakTrackingService peakTracker)
        {
            _configService = configService;
            _elementConfigVM = elementConfigVM;
            _peakTracker = peakTracker;

            // --- 核心修复：监听全局寻峰大管家变化 (处理主界面红线增删同步) ---
            _peakTracker.TrackedPeaks.CollectionChanged += OnGlobalTrackedPeaksChanged;

            // 监听样品序列页面下发的测量任务名单
            WeakReferenceMessenger.Default.Register<SampleSequenceChangedMessage>(this, (r, m) =>
            {
                MeasurementSequence = new ObservableCollection<SampleItemModel>(m.Value);
                if (MeasurementSequence.Count > 0)
                {
                    CurrentSample = MeasurementSequence[0]; // 默认选中第一个样品
                }
                // 每次下发新序列，也强制根据当前已有的峰线重刷一次表格槽位
                RefreshElementsFromTracker();
            });

            // 软件启动或页面初始化时，主动同步一次主界面的峰线
            RefreshElementsFromTracker();
        }

        #endregion

        #region 4. 跨页面同步与元素匹配逻辑

        /// <summary>
        /// 公开的寻峰大管家实例，供 View 层获取追踪的峰线数据进行绘图。
        /// </summary>
        public PeakTrackingService PeakTracker => _peakTracker;

        /// <summary>
        /// 当全局追踪的特征峰集合发生变化时触发（例如在主界面新增/删除红线）。
        /// </summary>
        private void OnGlobalTrackedPeaksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RefreshElementsFromTracker();
        }

        /// <summary>
        /// 【镜像同步引擎核心】：将主界面追踪的物理峰线转换为本页面的业务元素，并维护 UI。
        /// 确保下拉框、右侧表格的元素行与主界面红线标注完全一致（支持同步删除）。
        /// </summary>
        public void RefreshElementsFromTracker()
        {
            // 必须在 UI 线程执行，因为涉及 ObservableCollection 的修改
            Application.Current.Dispatcher.Invoke(() =>
            {
                // 1. 获取主界面当前所有红线对应的“标准名称”列表 (Pb, Cd, 或 峰@xxx)
                var currentTrackedNamesFromMain = _peakTracker.TrackedPeaks
                    .Select(p => MatchElement(p.CurrentWavelength))
                    .Distinct() // 去重
                    .ToList();

                // 2. 同步下拉框 (PickedElements)：移除已删除的峰，添加新发现的峰
                var elementsToRemoveFromPicked = PickedElements.Where(p => !currentTrackedNamesFromMain.Contains(p)).ToList();
                foreach (var name in elementsToRemoveFromPicked)
                {
                    PickedElements.Remove(name);
                }
                foreach (var name in currentTrackedNamesFromMain)
                {
                    if (!PickedElements.Contains(name)) PickedElements.Add(name);
                }

                // 3. 同步右侧 DataGrid 中的元素行（槽位补齐/删除）
                if (MeasurementSequence != null)
                {
                    foreach (var sample in MeasurementSequence)
                    {
                        // A. 物理删除：如果红线没了，表格对应的行也要删掉
                        var rowsToRemove = sample.ElementConcentrations
                            .Where(ec => !currentTrackedNamesFromMain.Contains(ec.ElementName))
                            .ToList();
                        foreach (var row in rowsToRemove) sample.ElementConcentrations.Remove(row);

                        // B. 补全：确保新寻的峰在每个样品的表格里都有对应的行
                        foreach (var name in currentTrackedNamesFromMain)
                        {
                            if (sample.ElementConcentrations.All(ec => ec.ElementName != name))
                            {
                                sample.ElementConcentrations.Add(new ElementConcentrationModel { ElementName = name });
                            }
                        }
                    }
                }

                // 4. 自动选中第一个有效的元素 (防止之前选的被删后下拉框变空)
                if (!PickedElements.Contains(SelectedElement))
                {
                    SelectedElement = PickedElements.FirstOrDefault() ?? string.Empty;
                }
            });
        }

        /// <summary>
        /// 智能匹配：将寻峰捕捉到的波长与用户配置的元素进行对齐 (±3.0nm容差)
        /// </summary>
        /// <param name="peakedWavelength">寻峰算法捕捉到的物理波长</param>
        /// <returns>匹配到的元素名 (如 "Pb") 或原始波长字符串 (如 "峰@283.31")</returns>
        public string MatchElement(double peakedWavelength)
        {
            double tolerance = 3.0; // 设定 3.0nm 的自动识别误差范围

            // 寻找距离当前峰位最近且在容差范围内的已配置元素
            var matched = _elementConfigVM.SelectedConfigs
                .Select(c => new { Config = c, Diff = Math.Abs(c.Wavelength - peakedWavelength) })
                .Where(x => x.Diff <= tolerance)
                .OrderBy(x => x.Diff) // 确保匹配到物理距离最接近的那个元素
                .FirstOrDefault();

            if (matched != null)
            {
                return matched.Config.ElementName; // 匹配成功，返回如 "Pb"
            }
            return $"峰@{peakedWavelength:F2}"; // 匹配失败，返回原始波长格式
        }

        /// <summary>
        /// 获取当前下拉框选中项对应的物理波长值 (用于图表缩放和数据采集)
        /// </summary>
        public double GetTargetWavelength()
        {
            if (string.IsNullOrEmpty(SelectedElement)) return 0;

            // 场景 1：如果选中项是已配置的元素名 (如 "Pb")
            var config = _elementConfigVM.SelectedConfigs.FirstOrDefault(x => x.ElementName == SelectedElement);
            if (config != null) return config.Wavelength;

            // 场景 2：如果选中项是未识别的波长字符串 (如 "峰@283.31")
            if (SelectedElement.Contains("@"))
            {
                string wlStr = SelectedElement.Split('@')[1].Replace("nm", "").Trim();
                return double.TryParse(wlStr, out double wl) ? wl : 0;
            }
            return 0;
        }

        /// <summary>
        /// 供 View 层在每帧渲染时调用，将当前红线位置的强度数据推入多通道缓冲区
        /// </summary>
        /// <param name="name">元素名或峰位字符串</param>
        /// <param name="intensity">当前帧的强度值</param>
        public void PushData(string name, double intensity)
        {
            if (!IsCollecting) return; // 只有在采集状态才积累数据

            // 确保字典中有该通道的列表
            if (!_multiChannelBuffer.ContainsKey(name))
                _multiChannelBuffer[name] = new List<double>();

            _multiChannelBuffer[name].Add(intensity); // 将数据点加入对应通道的缓冲区
        }

        #endregion

        #region 交互命令

        /// <summary>
        /// 开始采集数据命令：清空旧缓冲区，启动采集状态。
        /// </summary>
        [RelayCommand]
        private void StartCollecting()
        {
            if (CurrentSample == null) { MessageBox.Show("请先选择左侧的样品！", "警告"); return; }
            if (PickedElements.Count == 0) { MessageBox.Show("请先在主界面寻峰选择要监控的元素！", "警告"); return; }

            _multiChannelBuffer.Clear(); // 启动前必须清空所有通道的旧数据
            IsCollecting = true;
            CurrentSample.Status = "采集数据中..."; // 更新左侧样品状态
        }

        /// <summary>
        /// 停止采集并计算保存命令：核心结果计算与持久化逻辑。
        /// </summary>
        [RelayCommand]
        private void StopAndSave()
        {
            if (!IsCollecting) return; // 仅在采集状态下点击才有效
            IsCollecting = false; // 停止采集状态

            // 【关键修复】：检查缓冲区是否有足够的数据点 (至少需要2个点才能算 RSD)
            // 检查任意一个通道的数据点数，如果所有通道点数都小于2，则不进行计算
            if (_multiChannelBuffer.Any() && _multiChannelBuffer.Values.Any(list => list.Count > 1))
            {
                foreach (var channel in _multiChannelBuffer) // 遍历每个采集通道
                {
                    var data = channel.Value;
                    if (data.Count < 2) continue; // 不足 2 个点则跳过 RSD 计算

                    double average = data.Average();

                    // --- 计算 RSD (相对标准偏差) ---
                    // 标准差公式：sqrt( (Σ(x - avg)^2) / (n - 1) )
                    double sumOfSquares = data.Select(val => (val - average) * (val - average)).Sum();
                    double stdDev = Math.Sqrt(sumOfSquares / (data.Count - 1));
                    double rsd = (average != 0) ? (stdDev / average) * 100.0 : 0; // RSD = (StdDev / Avg) * 100%

                    // 【核心修复】：将计算结果写入 CurrentSample 的对应元素行
                    var targetRow = CurrentSample?.ElementConcentrations
                        .FirstOrDefault(e => e.ElementName == channel.Key); // Key就是元素名

                    if (targetRow != null)
                    {
                        targetRow.MeasuredIntensity = Math.Round(average, 2); // 保留两位小数
                        targetRow.MeasuredRsd = Math.Round(rsd, 2);           // 保留两位小数
                    }
                }

                if (CurrentSample != null) CurrentSample.Status = "已完成"; // 样品状态更新

                // 执行本地 JSON 数据落盘保存
                _configService.SaveResults(MeasurementSequence.ToList());

                // 逻辑完成后自动跳转到序列中的下一个样品
                int currentIndex = MeasurementSequence.IndexOf(CurrentSample!);
                if (currentIndex < MeasurementSequence.Count - 1)
                    CurrentSample = MeasurementSequence[currentIndex + 1];
                else
                    MessageBox.Show("全序列测量任务已全部结束！", "提示");
            }
            else
            {
                // 如果采集数据不足，提示警告并重置样品状态
                if (CurrentSample != null) CurrentSample.Status = "等待";
                MessageBox.Show("采集数据不足，无法计算有效统计结果！", "警告");
            }
            _multiChannelBuffer.Clear(); // 彻底清空，防止数据污染下一个样品
        }

        /// <summary>
        /// 紧急停止命令：强制停止采集。
        /// </summary>
        [RelayCommand] private void StopSequence() => IsCollecting = false;

        /// <summary>
        /// 导出本次会话测量数据为 JSON 文件。
        /// </summary>
        [RelayCommand]
        private void ExportCurrentSession()
        {
            if (MeasurementSequence.Count == 0) return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "实验快照文件 (*.json)|*.json",
                FileName = $"实验数据_{DateTime.Now:yyyyMMdd_HHmm}",
                Title = "导出当前测量序列及结果"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    _configService.ExportResults(dialog.FileName, MeasurementSequence.ToList());
                    MessageBox.Show("当前会话数据导出成功！", "提示");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导出失败: {ex.Message}", "错误");
                }
            }
        }

        #endregion
    }
}