using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Services;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Windows;
using Microsoft.Win32;

namespace GD_ControlCenter_WPF.ViewModels
{
    #region 1. 辅助展示数据模型 (必须标记为 partial 以支持通知)

    /// <summary> 测定结果行：仅用于展示“待测液”样品的计算结果 </summary>
    public partial class ContinuousElementResultRow : ObservableObject
    {
        [ObservableProperty] private string _sampleName = string.Empty;
        [ObservableProperty] private string _sampleType = string.Empty;
        [ObservableProperty] private double _intensity;
        [ObservableProperty] private double _rSD;
        [ObservableProperty] private double _calculatedConc;
    }

    /// <summary> 标准点/空白明细行：用于构建校准曲线 </summary>
    public partial class StandardPointRow : ObservableObject
    {
        // Name 必须是 ObservableProperty，防止生成名称全为重复值
        [ObservableProperty] private string _name = string.Empty;

        public SampleType Type { get; set; }
        public double Concentration { get; set; }
        public double Intensity { get; set; }
        public double RSD { get; set; }

        [ObservableProperty] private bool _isEnabled = true;
    }

    #endregion


    public partial class DataProcessingViewModel : ObservableObject
    {
        private readonly JsonConfigService _configService;
        private List<SampleItemModel> _rawFullSequence = new(); // 实验数据快照缓冲区

        // 绘图回调，由 DataProcessingView.xaml.cs 订阅
        public Action<double, double, List<StandardPointRow>>? RequestPlotUpdate { get; set; }

        #region 2. UI 界面属性

        [ObservableProperty] private bool _isContinuousMode = true;
        [ObservableProperty] private string _currentModeTitle = "连续进样数据解析";
        [ObservableProperty] private ObservableCollection<string> _activeElements = new();

        private string _selectedElement = string.Empty;
        public string SelectedElement
        {
            get => _selectedElement;
            set
            {
                if (SetProperty(ref _selectedElement, value))
                {
                    // 元素一切换，立刻重新过滤左右表格
                    UpdateFilteredData();
                }
            }
        }

        #endregion

        #region 3. 拟合指标与性能 (右上角卡片)

        [ObservableProperty] private string _equationText = "未执行拟合";
        [ObservableProperty] private string _rSquaredText = "0.0000";
        [ObservableProperty] private double _lodValue; // 检出限 (3*SD_blank/slope)
        [ObservableProperty] private double _becValue; // 背景等效浓度 (intercept/slope)

        #endregion

        #region 4. 数据表格集合

        // 左下角：仅展示“待测液”
        [ObservableProperty] private ObservableCollection<ContinuousElementResultRow> _filteredResults = new();

        // 右侧：展示“空白”和“标液”
        [ObservableProperty] private ObservableCollection<StandardPointRow> _standardPoints = new();

        #endregion

        public DataProcessingViewModel(JsonConfigService configService)
        {
            _configService = configService;

            // 监听：从配置页同步元素名单
            WeakReferenceMessenger.Default.Register<ActiveConfigsChangedMessage>(this, (r, m) => {
                Application.Current.Dispatcher.Invoke(() => {
                    ActiveElements.Clear();
                    foreach (var c in m.Value)
                    {
                        string name = c.ElementName.Contains("(") ? c.ElementName : $"{c.ElementName}({c.Wavelength})";
                        ActiveElements.Add(name);
                    }
                    if (ActiveElements.Count > 0 && string.IsNullOrEmpty(SelectedElement))
                        SelectedElement = ActiveElements[0];
                });
            });

            // 监听：接收来自测量模块或导入的实验全量数据包
            WeakReferenceMessenger.Default.Register<SampleSequenceChangedMessage>(this, (r, m) => {
                _rawFullSequence = m.Value;
                UpdateFilteredData();
            });
        }

        #region 5. 核心逻辑引擎

        /// <summary>
        /// 数据分区逻辑：将原始数据根据[当前选定元素]拆分为“左待测”与“右标准”
        /// </summary>
        private void UpdateFilteredData()
        {
            // 安全检查
            if (string.IsNullOrEmpty(SelectedElement) || _rawFullSequence == null || _rawFullSequence.Count == 0) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                FilteredResults.Clear();
                StandardPoints.Clear();

                foreach (var sample in _rawFullSequence)
                {
                    // 【核心修复】：使用 Trim 消除不可见空格干扰，精准匹配元素数据
                    var target = sample.ElementConcentrations
                        .FirstOrDefault(e => e.ElementName.Trim() == SelectedElement.Trim());

                    if (target == null) continue;

                    // 场景 1：待测溶液 -> 填入左下角测定表
                    if (sample.Type == SampleType.待测液)
                    {
                        FilteredResults.Add(new ContinuousElementResultRow
                        {
                            SampleName = sample.SampleName,
                            SampleType = "待测液",
                            Intensity = target.MeasuredIntensity,
                            RSD = target.MeasuredRsd
                        });
                    }
                    // 场景 2：空白 或 标液 -> 填入右侧拟合表
                    else if (sample.Type == SampleType.标液 || sample.Type == SampleType.空白)
                    {
                        // 解析浓度：如果是空白点，浓度强制设为 0
                        double conc = (sample.Type == SampleType.空白) ? 0 :
                                     (double.TryParse(target.ConcentrationValue, out var d) ? d : 0);

                        StandardPoints.Add(new StandardPointRow
                        {
                            Name = sample.SampleName, // 确保这里拿到的是唯一的名称
                            Type = sample.Type,
                            Concentration = conc,
                            Intensity = target.MeasuredIntensity,
                            RSD = target.MeasuredRsd
                        });
                    }
                }

                // 填充完数据后，手动触发一次拟合逻辑以刷新指标和图表
                CalculateFitting();
            });
        }

        /// <summary>
        /// 执行线性拟合命令：计算回归方程、LOD、BEC 并通知 View 绘图
        /// </summary>
        [RelayCommand]
        public void CalculateFitting()
        {
            // 1. 提取所有被勾选为“启用”的标准点（包含空白点）
            var validPoints = StandardPoints.Where(p => p.IsEnabled).ToList();
            if (validPoints.Count < 2)
            {
                EquationText = "拟合点不足";
                RSquaredText = "0.0000";
                return;
            }

            // --- 2. 最小二乘法计算逻辑 ---
            int n = validPoints.Count;
            double sumX = validPoints.Sum(p => p.Concentration);
            double sumY = validPoints.Sum(p => p.Intensity);
            double sumXY = validPoints.Sum(p => p.Concentration * p.Intensity);
            double sumX2 = validPoints.Sum(p => p.Concentration * p.Concentration);

            double denominator = (n * sumX2 - sumX * sumX);
            if (Math.Abs(denominator) < 1e-10) return;

            double slope = (n * sumXY - sumX * sumY) / denominator;
            double intercept = (sumY - slope * sumX) / n;

            // 计算相关系数 R²
            double yAvg = sumY / n;
            double ssRes = validPoints.Sum(p => Math.Pow(p.Intensity - (slope * p.Concentration + intercept), 2));
            double ssTot = validPoints.Sum(p => Math.Pow(p.Intensity - yAvg, 2));
            double r2 = (ssTot == 0) ? 1 : 1 - (ssRes / ssTot);

            // --- 3. 计算分析性能指标 (LOD / BEC) ---

            // 【核心公式】：LOD = 3 * SD(空白) / Slope
            // 找到勾选点里的第一个空白样品。如果没有，则取第一个有效点估算。
            var blank = validPoints.FirstOrDefault(p => p.Type == SampleType.空白) ?? validPoints[0];
            // 标准偏差 = 强度 * (RSD / 100)
            double blankSD = blank.Intensity * (blank.RSD / 100.0);

            // 计算 LOD
            LodValue = (slope != 0) ? (3.0 * blankSD) / slope : 0;
            // 计算 BEC = 截距 / 斜率
            BecValue = (slope != 0) ? Math.Abs(intercept / slope) : 0;

            // 更新 UI 指标
            EquationText = $"y = {slope:F4}x + ({intercept:F4})";
            RSquaredText = r2.ToString("F4");

            // --- 4. 浓度回算：更新左侧所有待测溶液的浓度结果 ---
            foreach (var row in FilteredResults)
            {
                if (slope != 0)
                {
                    // x = (y - b) / k
                    row.CalculatedConc = Math.Round((row.Intensity - intercept) / slope, 3);
                }
            }

            // --- 5. 发送重绘信号给 View 层进行 ScottPlot 渲染 ---
            RequestPlotUpdate?.Invoke(slope, intercept, validPoints);
        }

        #endregion

        #region 6. 数据交换命令

        [RelayCommand]
        private void ImportExternalData()
        {
            OpenFileDialog dialog = new OpenFileDialog { Filter = "实验快照 (*.json)|*.json", Title = "载入已完成的测量数据" };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var data = _configService.ImportResults(dialog.FileName);
                    if (data != null)
                    {
                        _rawFullSequence = data;
                        ActiveElements.Clear();
                        // 从导入的文件中自动提取元素名单
                        var els = data.SelectMany(s => s.ElementConcentrations).Select(e => e.ElementName).Distinct().ToList();
                        foreach (var el in els) ActiveElements.Add(el);

                        if (ActiveElements.Count > 0) SelectedElement = ActiveElements[0];
                        UpdateFilteredData(); // 立即触发清洗与拟合展示
                        MessageBox.Show("外部实验快照载入成功。");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导入失败：{ex.Message}");
                }
            }
        }

        [RelayCommand] private void ExportData() => MessageBox.Show("功能开发中：导出结果报表...");
        [RelayCommand] private void GenerateReport() => MessageBox.Show("功能开发中：生成分析报告...");

        #endregion
    }
}