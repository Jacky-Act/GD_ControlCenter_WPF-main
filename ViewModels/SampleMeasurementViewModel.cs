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
using System.Threading.Tasks;
using System.Windows; 
using Microsoft.Win32; 

namespace GD_ControlCenter_WPF.ViewModels
{
    /// <summary>
    /// 用于前端 UI 绑定的测量组数据模型
    /// </summary>
    public class MeasurementGroup : ObservableObject
    {
        public int IntegrationTime { get; set; }
        public int AverageCount { get; set; }
        public ObservableCollection<string> Elements { get; set; } = new();
    }

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
        
        [ObservableProperty] private MeasurementGroup _currentMeasurementGroup = new(); // 当前右上角展示的元素测量组

        // 测量强度预览：当前选中的用于预览强度的元素对象
        [ObservableProperty] private ElementConcentrationModel? _selectedPreviewElement;

        partial void OnCurrentSampleChanged(SampleItemModel? value)
        {
            UpdateMeasurementGroupDisplay();
            
            // 每次切换样品时，初始化该样品下每个元素的测量轮次(Reps)
            if (value != null)
            {
                foreach (var ec in value.ElementConcentrations)
                {
                    if (ec.Reps.Count != value.Repeats)
                    {
                        ec.Reps.Clear();
                        for (int i = 1; i <= value.Repeats; i++)
                        {
                            ec.Reps.Add(new MeasurementRepModel { RepIndex = i, Intensity = null, IsMeasuring = false });
                        }
                    }
                }
                
                // 默认选中第一个元素进行预览
                if (value.ElementConcentrations.Any())
                {
                    SelectedPreviewElement = value.ElementConcentrations.First();
                }
            }
            else
            {
                SelectedPreviewElement = null;
            }
        }

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
            // _peakTracker.TrackedPeaks.CollectionChanged += OnGlobalTrackedPeaksChanged; // 【已断开寻峰匹配逻辑】

            // 监听样品序列页面下发的测量任务名单
            WeakReferenceMessenger.Default.Register<SampleSequenceChangedMessage>(this, (r, m) =>
            {
                if (m.IsOverwrite)
                {
                    MeasurementSequence = new ObservableCollection<SampleItemModel>(m.Value);
                }
                else
                {
                    // 增量合并：保留相同名称样品的已测数据状态
                    var newSeq = new ObservableCollection<SampleItemModel>();
                    foreach (var newSample in m.Value)
                    {
                        var existing = MeasurementSequence?.FirstOrDefault(s => s.SampleName == newSample.SampleName);
                        if (existing != null)
                        {
                            newSample.Status = existing.Status;
                            foreach (var ec in newSample.ElementConcentrations)
                            {
                                var existingEc = existing.ElementConcentrations.FirstOrDefault(e => e.ElementName == ec.ElementName);
                                if (existingEc != null)
                                {
                                    ec.MeasuredIntensity = existingEc.MeasuredIntensity;
                                    ec.MeasuredRsd = existingEc.MeasuredRsd;
                                    ec.Reps = new ObservableCollection<MeasurementRepModel>(existingEc.Reps.Select(rep => new MeasurementRepModel 
                                    {
                                        RepIndex = rep.RepIndex,
                                        Intensity = rep.Intensity,
                                        IsMeasuring = rep.IsMeasuring
                                    }));
                                }
                            }
                            newSeq.Add(newSample);
                        }
                        else
                        {
                            newSeq.Add(newSample);
                        }
                    }
                    MeasurementSequence = newSeq;
                }

                if (MeasurementSequence.Count > 0 && CurrentSample == null) 
                {
                    CurrentSample = MeasurementSequence[0]; 
                }
                else if (CurrentSample != null)
                {
                    CurrentSample = MeasurementSequence.FirstOrDefault(s => s.SampleName == CurrentSample.SampleName) ?? MeasurementSequence.FirstOrDefault();
                }
                
                // 同步下拉框供图表局部查看使用
                PickedElements.Clear();
                if (CurrentSample != null)
                {
                    foreach (var ec in CurrentSample.ElementConcentrations)
                    {
                        PickedElements.Add(ec.ElementName);
                    }
                }
                if (PickedElements.Count > 0) SelectedElement = PickedElements[0];

                // 同步到其他模块（如数据处理），确保图表看到的是保留历史后的状态
                CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(
                    new GD_ControlCenter_WPF.Models.Messages.MeasurementDataUpdatedMessage(MeasurementSequence.ToList())
                );
            });

            // 软件启动或页面初始化时，主动同步一次主界面的峰线
            // RefreshElementsFromTracker(); // 【已断开寻峰匹配逻辑】
        }

        #endregion

        #region 4. 跨页面同步与元素匹配逻辑
        
        /// <summary>
        /// 新增空方法：全谱数据特征提取
        /// 之后将在这里实现：直接通过采集全谱数据，从中找到目标波长值和对应的强度
        /// </summary>
        public void ExtractIntensityFromFullSpectrum(double[] wavelengths, double[] intensities)
        {
            // TODO: 实现全谱中目标波长的强度提取逻辑
        }

        /// <summary>
        /// 公开的寻峰大管家实例，供 View 层获取追踪的峰线数据进行绘图。
        /// </summary>
        public PeakTrackingService PeakTracker => _peakTracker;

        /// <summary>
        /// 当全局追踪的特征峰集合发生变化时触发（例如在主界面新增/删除红线）。
        /// </summary>
        private void OnGlobalTrackedPeaksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // RefreshElementsFromTracker(); // 【已断开寻峰匹配逻辑】
        }

        /// <summary>
        /// 【已废弃/断开】：将主界面追踪的物理峰线转换为本页面的业务元素，并维护 UI。
        /// </summary>
        public void RefreshElementsFromTracker()
        {
            // 逻辑已断开，不再基于寻峰红线增删元素行
            /*
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
                
                // 5. 更新右上角的测量组信息展示
                UpdateMeasurementGroupDisplay();
            });
            */
            
            // 简单更新一下右上角的测量组信息即可
            Application.Current.Dispatcher.Invoke(() =>
            {
                UpdateMeasurementGroupDisplay();
            });
        }
        
        /// <summary>
        /// 更新右上角“样品信息”卡片中展示的元素分组（按相同的积分时间和平均次数分组）
        /// </summary>
        private void UpdateMeasurementGroupDisplay()
        {
            if (CurrentSample == null || _elementConfigVM.SelectedConfigs == null || _elementConfigVM.SelectedConfigs.Count == 0)
            {
                CurrentMeasurementGroup = new MeasurementGroup();
                return;
            }

            // 假设当前只取配置中的第一组作为展示（由于目前业务是同时采所有峰，如果有不同配置，硬件实现分组轮询测量的逻辑将在这里驱动）
            var firstConfig = _elementConfigVM.SelectedConfigs.FirstOrDefault();
            if (firstConfig == null) return;

            int currentIntegrationTime = firstConfig.IntegrationTime;
            int currentAverageCount = firstConfig.AveragingCount;

            // 找出所有和第一组具有相同【积分时间】和【平均次数】的元素
            var groupedElements = _elementConfigVM.SelectedConfigs
                .Where(c => c.IntegrationTime == currentIntegrationTime && c.AveragingCount == currentAverageCount)
                .Select(c => $"{c.ElementName}({c.Wavelength})")
                .ToList();

            // 业务逻辑修改：如果当前是“空白溶液”，并且（只有一个元素 或 所有元素的积分时间和平均次数都相同）
            // 那么不需要显示元素信息。多元素且配置不同时才需要显示。
            bool isBlankSample = CurrentSample.Type == SampleType.空白;
            var distinctConfigGroupsCount = _elementConfigVM.SelectedConfigs
                .Select(c => new { c.IntegrationTime, c.AveragingCount })
                .Distinct()
                .Count();

            if (isBlankSample && distinctConfigGroupsCount <= 1)
            {
                // 不需要显示元素信息，清空列表
                groupedElements.Clear();
            }

            CurrentMeasurementGroup = new MeasurementGroup
            {
                IntegrationTime = currentIntegrationTime,
                AverageCount = currentAverageCount,
                Elements = new ObservableCollection<string>(groupedElements)
            };
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
                return $"{matched.Config.ElementName}({matched.Config.Wavelength})"; // 匹配成功，返回格式化名称
            }
            return $"峰@{peakedWavelength:F2}"; // 匹配失败，返回原始波长格式
        }

        /// <summary>
        /// 获取当前下拉框选中项对应的物理波长值 (用于图表缩放和数据采集)
        /// </summary>
        public double GetTargetWavelength()
        {
            if (string.IsNullOrEmpty(SelectedElement)) return 0;

            // 场景 1：如果选中项是已配置的元素名 (如 "Pb(283.31)")
            var config = _elementConfigVM.SelectedConfigs.FirstOrDefault(x => $"{x.ElementName}({x.Wavelength})" == SelectedElement || x.ElementName == SelectedElement);
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

        [RelayCommand]
        private async Task StartCollecting()
        {
            if (CurrentSample == null) { MessageBox.Show("请先选择左侧的样品！", "警告"); return; }
            if (PickedElements.Count == 0) { MessageBox.Show("请先在主界面寻峰选择要监控的元素！", "警告"); return; }

            // 检查是否有在线的光谱仪设备
            if (SpectrometerManager.Instance.Devices.Count == 0)
            {
                MessageBox.Show("未检测到在线的光谱仪，请检查设备连接后再试！", "硬件未就绪", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 检查已连接的光谱仪是否开启了“连续采集”流
            if (SpectrometerManager.Instance.Devices.Any(d => !d.IsMeasuring))
            {
                MessageBox.Show("光谱仪已连接，但尚未启动连续采集流！\n请确保光谱仪已处于工作/读取状态后再开始全自动测量。", "采集未启动", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 启动确认弹窗（显示样品信息、元素波长及浓度）
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"当前测样序列：{CurrentSample.SampleName}");
            if (CurrentSample.Type == GD_ControlCenter_WPF.Models.Messages.SampleType.标液)
            {
                sb.AppendLine("类型：标液");
                sb.AppendLine("元素配置及浓度：");
                foreach (var ec in CurrentSample.ElementConcentrations)
                {
                    sb.AppendLine($" - {ec.ElementName}: {ec.ConcentrationValue} {CurrentSample.ConcentrationUnit}");
                }
            }
            else
            {
                sb.AppendLine("类型：待测液");
                sb.AppendLine("元素配置：");
                foreach (var ec in CurrentSample.ElementConcentrations)
                {
                    sb.AppendLine($" - {ec.ElementName}");
                }
            }
            sb.AppendLine("\n请确认放置好当前样品后，点击“确定”开始测量。");

            var result = MessageBox.Show(sb.ToString(), "开始采集确认", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (result != MessageBoxResult.OK) return;

            IsCollecting = true;
            await RunAutomatedMeasurementAsync();
        }

        private async Task RunAutomatedMeasurementAsync()
        {
            while (CurrentSample != null && IsCollecting)
            {
                // 如果该序列已经测量过，则清空之前的数据
                if (CurrentSample.Status == "已完成")
                {
                    foreach (var ec in CurrentSample.ElementConcentrations)
                    {
                        ec.MeasuredIntensity = 0;
                        ec.MeasuredRsd = 0;
                        foreach (var rep in ec.Reps)
                        {
                            rep.Intensity = null;
                        }
                    }
                    // 发送消息通知界面数据已被清空
                    CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(
                        new GD_ControlCenter_WPF.Models.Messages.MeasurementDataUpdatedMessage(MeasurementSequence.ToList())
                    );
                }

                CurrentSample.Status = "采集数据中...";

                // 按积分时间和平均次数分组
                var groups = _elementConfigVM.SelectedConfigs
                    .GroupBy(c => new { c.IntegrationTime, c.AveragingCount })
                    .ToList();

                var cachedColumns = new List<(string, SpectralData)>();

                foreach (var group in groups)
                {
                    if (!IsCollecting) break;

                    int intTime = group.Key.IntegrationTime;
                    int avgCount = group.Key.AveragingCount;

                    string elementsHeader = string.Join(" | ", group.Select(c => $"{c.ElementName}({c.Wavelength})"));
                    string groupHeader = $"{elementsHeader} - {intTime}ms x{avgCount}";

                    // 下发硬件配置
                    foreach(var device in SpectrometerManager.Instance.Devices)
                    {
                        await device.UpdateConfigurationAsync(intTime, (uint)avgCount);
                        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new GD_ControlCenter_WPF.Models.Messages.HardwareConfigChangedMessage(device.Config.SerialNumber, intTime, (uint)avgCount));
                    }

                    // 切换参数后等待硬件稳定: 3 * (积分时间 * 平均次数)
                    await Task.Delay(3 * intTime * avgCount);

                    for (int r = 0; r < CurrentSample.Repeats; r++)
                    {
                        if (!IsCollecting) break;

                        // UI 更新测量中状态
                        var repModels = new List<MeasurementRepModel>();
                        foreach (var conf in group)
                        {
                            string matchName = $"{conf.ElementName}({conf.Wavelength})";
                            var targetRow = CurrentSample.ElementConcentrations.FirstOrDefault(e => e.ElementName == matchName || e.ElementName == conf.ElementName);
                            if (targetRow != null && r < targetRow.Reps.Count)
                            {
                                targetRow.Reps[r].IsMeasuring = true;
                                repModels.Add(targetRow.Reps[r]);
                            }
                        }

                        // 从硬件连续数据流中截取最新一帧
                        SpectralData frame = await WaitForNextFrameAsync();

                        // 恢复状态
                        foreach (var rep in repModels) rep.IsMeasuring = false;

                        if (frame != null)
                        {
                            cachedColumns.Add((groupHeader + $" [第{r + 1}次]", frame));

                            // 提取波长强度
                            foreach (var conf in group)
                            {
                                double realWl = SpectrometerLogic.GetActualPeakWavelength(frame, conf.Wavelength, 1.0);
                                double realIntensity = SpectrometerLogic.GetIntensityAtWavelength(frame, realWl);

                                string matchName = $"{conf.ElementName}({conf.Wavelength})";
                                var targetRow = CurrentSample.ElementConcentrations.FirstOrDefault(e => e.ElementName == matchName || e.ElementName == conf.ElementName);
                                if (targetRow != null && r < targetRow.Reps.Count)
                                {
                                    targetRow.Reps[r].Intensity = Math.Round(realIntensity, 0);
                                }
                            }
                        }

                        // 间隔等待 (非最后一次)
                        if (r < CurrentSample.Repeats - 1)
                        {
                            int delayMs = (int)(CurrentSample.Interval * 1000);
                            if (delayMs > 0) await Task.Delay(delayMs);
                        }
                    }
                }

                if (!IsCollecting) break; // 中途取消

                // 计算 RSD 和 平均值
                foreach (var row in CurrentSample.ElementConcentrations)
                {
                    var validIntensities = row.Reps.Where(r => r.Intensity.HasValue).Select(r => r.Intensity.Value).ToList();
                    if (validIntensities.Count >= 2)
                    {
                        double avg = validIntensities.Average();
                        double sumOfSquares = validIntensities.Select(val => (val - avg) * (val - avg)).Sum();
                        double stdDev = Math.Sqrt(sumOfSquares / (validIntensities.Count - 1));
                        double rsd = (avg != 0) ? (stdDev / avg) * 100.0 : 0;

                        row.MeasuredIntensity = Math.Round(avg, 0);
                        row.MeasuredRsd = Math.Round(rsd, 2);
                    }
                    else if (validIntensities.Count == 1)
                    {
                        row.MeasuredIntensity = Math.Round(validIntensities[0], 0);
                        row.MeasuredRsd = 0;
                    }
                }

                CurrentSample.Status = "已完成";

                // 后台 CSV 落盘
                string folder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Records");
                string filePath = System.IO.Path.Combine(folder, $"{CurrentSample.SampleName}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                CurrentSample.CsvFilePath = filePath;
                
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await CsvExportService.ExportSpectralDataColumnsAsync(filePath, cachedColumns);
                    }
                    catch (Exception ex)
                    {
                        Application.Current.Dispatcher.Invoke(() => MessageBox.Show($"CSV导出失败: {ex.Message}"));
                    }
                });

                // 本地 JSON 序列保存
                _configService.SaveResults(MeasurementSequence.ToList());

                // 通知全系统数据已更新（这会让“数据处理”页面刷新拿到强度数据）
                CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(
                    new GD_ControlCenter_WPF.Models.Messages.MeasurementDataUpdatedMessage(MeasurementSequence.ToList())
                );

                // 自动跳向下一个样品，并终止本轮自动循环采集
                int currentIndex = MeasurementSequence.IndexOf(CurrentSample);
                if (currentIndex < MeasurementSequence.Count - 1)
                {
                    var nextSample = MeasurementSequence[currentIndex + 1];
                    MessageBox.Show($"样品 [{CurrentSample.SampleName}] 测量完毕！\n\n请准备下一个样品 [{nextSample.SampleName}]。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    CurrentSample = nextSample;
                }
                else
                {
                    MessageBox.Show("整个测量序列已全部完成！\n所有序列都采集完成后，请移步数据处理！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                
                IsCollecting = false;
                break;
            }
        }

        private Task<SpectralData> WaitForNextFrameAsync()
        {
            var tcs = new TaskCompletionSource<SpectralData>();
            var token = new object();

            WeakReferenceMessenger.Default.Register<SpectralDataMessage>(token, (r, m) =>
            {
                WeakReferenceMessenger.Default.Unregister<SpectralDataMessage>(token);
                tcs.TrySetResult(m.Value);
            });

            // 15秒超时保护
            Task.Delay(15000).ContinueWith(_ => 
            {
                WeakReferenceMessenger.Default.Unregister<SpectralDataMessage>(token);
                tcs.TrySetResult(null); 
            });

            return tcs.Task;
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