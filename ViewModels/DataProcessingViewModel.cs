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
        [ObservableProperty] private string _status = "等待";
        [ObservableProperty] private double _intensity;
        [ObservableProperty] private double _rSD;
        [ObservableProperty] private string _calculatedConc = "-";
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

        public bool IsBlank => Type == SampleType.空白;

        [ObservableProperty] private bool _isEnabled = true;
    }

    #endregion


    public partial class DataProcessingViewModel : ObservableObject
    {
        private readonly JsonConfigService _configService;
        private readonly ElementDatabaseService _elementDbService;
        private List<SampleItemModel> _rawFullSequence = new(); // 实验数据快照缓冲区
        private List<AnalysisConfigItem> _activeConfigs = new();

        // 绘图回调，由 DataProcessingView.xaml.cs 订阅
        public Action<double, double, List<StandardPointRow>, string, bool>? RequestPlotUpdate { get; set; }

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

        // 是否可以使用保存曲线功能 (历史曲线模式下不可用)
        [ObservableProperty] private bool _canSaveCurve = true;

        // 当前是否包含待测样品 (用于控制右下角表格遮罩)
        [ObservableProperty] private bool _hasUnknownSamples = false;

        // 当前是否包含标准样品 (用于控制右上角表格遮罩)
        [ObservableProperty] private bool _hasStandardSamples = false;

        #endregion

        #region 4. 数据表格集合

        // 左下角：仅展示“待测液”
        [ObservableProperty] private ObservableCollection<ContinuousElementResultRow> _filteredResults = new();

        // 右侧：展示“空白”和“标液”
        [ObservableProperty] private ObservableCollection<StandardPointRow> _standardPoints = new();

        #endregion

        public DataProcessingViewModel(JsonConfigService configService, ElementDatabaseService elementDbService)
        {
            _configService = configService;
            _elementDbService = elementDbService;

            // 监听：从配置页同步元素名单
            WeakReferenceMessenger.Default.Register<ActiveConfigsChangedMessage>(this, (r, m) => {
                Application.Current.Dispatcher.Invoke(() => {
                    _activeConfigs = m.Value;
                    ActiveElements.Clear();
                    foreach (var c in _activeConfigs)
                    {
                        string name = c.ElementName.Contains("(") ? c.ElementName : $"{c.ElementName}({c.Wavelength})";
                        ActiveElements.Add(name);
                    }
                    if (ActiveElements.Count > 0 && string.IsNullOrEmpty(SelectedElement))
                        SelectedElement = ActiveElements[0];
                    else
                        UpdateFilteredData();
                });
            });

            // 监听：接收来自测量模块或导入的实验全量数据包
            WeakReferenceMessenger.Default.Register<SampleSequenceChangedMessage>(this, (r, m) => {
                _rawFullSequence = m.Value;
                UpdateFilteredData();
            });

            // 监听：测样完成后强度数据的更新通知
            WeakReferenceMessenger.Default.Register<MeasurementDataUpdatedMessage>(this, (r, m) => {
                _rawFullSequence = m.Value;
                UpdateFilteredData();
            });
        }

        #region 5. 核心逻辑引擎

        [ObservableProperty] private bool _showAlert;
        [ObservableProperty] private string _alertMessage = string.Empty;

        /// <summary>
        /// 数据分区逻辑：将原始数据根据[当前选定元素]拆分为“左待测”与“右标准”
        /// </summary>
        private void UpdateFilteredData()
        {
            // 安全检查
            if (string.IsNullOrEmpty(SelectedElement)) return;

            var config = _activeConfigs.FirstOrDefault(c => (c.ElementName.Contains("(") ? c.ElementName : $"{c.ElementName}({c.Wavelength})") == SelectedElement);
            if (config != null)
            {
                CanSaveCurve = config.FittingCurve == "测量校准曲线";
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                FilteredResults.Clear();
                StandardPoints.Clear();

                if (_rawFullSequence != null)
                {
                    foreach (var sample in _rawFullSequence)
                    {
                        var target = sample.ElementConcentrations
                            .FirstOrDefault(e => e.ElementName.Trim() == SelectedElement.Trim());

                        if (target == null) continue;

                        if (sample.Type == SampleType.待测液)
                        {
                            FilteredResults.Add(new ContinuousElementResultRow
                            {
                                SampleName = sample.SampleName,
                                SampleType = "待测液",
                                Status = sample.Status,
                                Intensity = target.MeasuredIntensity,
                                RSD = target.MeasuredRsd
                            });
                        }
                        else if (sample.Type == SampleType.标液 || sample.Type == SampleType.空白)
                        {
                            double conc = (sample.Type == SampleType.空白) ? 0 :
                                         (double.TryParse(target.ConcentrationValue, out var d) ? d : 0);

                            StandardPoints.Add(new StandardPointRow
                            {
                                Name = sample.SampleName,
                                Type = sample.Type,
                                Concentration = conc,
                                Intensity = target.MeasuredIntensity,
                                RSD = target.MeasuredRsd
                            });
                        }
                    }
                }

                // 【核心修复】：如果使用了历史曲线，强制从数据库加载其关联的校准点数据覆盖右侧列表
                if (config != null && config.FittingCurve != "测量校准曲线")
                {
                    var db = _elementDbService.Load();
                    var elementConfig = db.Elements.GetValueOrDefault(config.ElementName);
                    var savedCurve = elementConfig?.SavedCurves?.FirstOrDefault(c => c.Name == config.FittingCurve);
                    
                    if (savedCurve != null && savedCurve.Points != null && savedCurve.Points.Count > 0)
                    {
                        StandardPoints.Clear();
                        foreach (var p in savedCurve.Points)
                        {
                            StandardPoints.Add(new StandardPointRow
                            {
                                Name = p.Name,
                                Type = p.Concentration == 0 ? SampleType.空白 : SampleType.标液,
                                Concentration = p.Concentration,
                                Intensity = p.Intensity,
                                RSD = p.RSD
                            });
                        }
                    }
                }

                HasUnknownSamples = FilteredResults.Any();
                HasStandardSamples = StandardPoints.Any(p => p.Type == SampleType.标液);

                bool isUsingHistory = config != null && config.FittingCurve != "测量校准曲线";
                bool allStandardsCompleted = true;
                
                if (!isUsingHistory && _rawFullSequence != null && _rawFullSequence.Any(s => s.Type == SampleType.标液 || s.Type == SampleType.空白))
                {
                    allStandardsCompleted = _rawFullSequence
                        .Where(s => s.Type == SampleType.标液 || s.Type == SampleType.空白)
                        .All(s => s.Status == "已完成");
                }

                if (isUsingHistory || allStandardsCompleted)
                {
                    _ = CalculateFitting(false); // 不弹窗
                }
                else
                {
                    EquationText = "未完成测量";
                    RSquaredText = "0.0000";
                    LodValue = 0;
                    var plotPoints = StandardPoints.ToList();
                    string unit = _rawFullSequence?.FirstOrDefault()?.ConcentrationUnit ?? "ppm";
                    RequestPlotUpdate?.Invoke(0, 0, plotPoints, unit, false);
                }
            });
        }

        /// <summary>
        /// 执行线性拟合命令：计算回归方程、LOD 并通知 View 绘图
        /// 如果选择了历史曲线，则直接套用历史曲线不拟合。
        /// </summary>
        [RelayCommand]
        public async System.Threading.Tasks.Task CalculateFitting()
        {
            await CalculateFitting(true);
        }

        private async System.Threading.Tasks.Task CalculateFitting(bool showToast)
        {
            var config = _activeConfigs.FirstOrDefault(c => (c.ElementName.Contains("(") ? c.ElementName : $"{c.ElementName}({c.Wavelength})") == SelectedElement);

            if (config == null || config.FittingCurve == "测量校准曲线")
            {
                if (_rawFullSequence != null && _rawFullSequence.Any(s => s.Type == SampleType.标液 || s.Type == SampleType.空白))
                {
                    bool allStandardsCompleted = _rawFullSequence
                        .Where(s => s.Type == SampleType.标液 || s.Type == SampleType.空白)
                        .All(s => s.Status == "已完成");
                    
                    if (!allStandardsCompleted)
                    {
                        if (showToast)
                        {
                            AlertMessage = "标准和空白序列尚未全部测量完成！";
                            ShowAlert = true;
                            await System.Threading.Tasks.Task.Delay(1000);
                            ShowAlert = false;
                        }
                        return;
                    }
                }
            }
            
            double slope = 0;
            double intercept = 0;
            double r2 = 0;

            List<StandardPointRow> plotPoints = new();

            if (config != null && config.FittingCurve != "测量校准曲线")
            {
                // 使用历史曲线
                CanSaveCurve = false;
                
                var db = _elementDbService.Load();
                var elementConfig = db.Elements.GetValueOrDefault(config.ElementName);
                var savedCurve = elementConfig?.SavedCurves?.FirstOrDefault(c => c.Name == config.FittingCurve);
                
                if (savedCurve != null)
                {
                    slope = savedCurve.Slope;
                    intercept = savedCurve.Intercept;
                    r2 = savedCurve.RSquared;
                    EquationText = savedCurve.Equation;
                    RSquaredText = r2.ToString("F4");
                    LodValue = savedCurve.Lod;
                }
                else
                {
                    EquationText = "未找到指定的历史曲线";
                    RSquaredText = "0.0000";
                    return;
                }
                plotPoints = StandardPoints.Where(p => p.IsEnabled && !p.IsBlank).ToList();
            }
            else
            {
                // 使用当前标准点拟合
                CanSaveCurve = true;

                var validPoints = StandardPoints.Where(p => p.IsEnabled && !p.IsBlank).ToList();
                if (validPoints.Count < 2)
                {
                    EquationText = "拟合点不足";
                    RSquaredText = "0.0000";
                    LodValue = 0;
                    RequestPlotUpdate?.Invoke(0, 0, StandardPoints.ToList(), _rawFullSequence?.FirstOrDefault()?.ConcentrationUnit ?? "ppm", false);
                    return;
                }

                plotPoints = validPoints;

                // --- 2. 最小二乘法计算逻辑 ---
                int n = validPoints.Count;
                double sumX = validPoints.Sum(p => p.Concentration);
                double sumY = validPoints.Sum(p => p.Intensity);
                double sumXY = validPoints.Sum(p => p.Concentration * p.Intensity);
                double sumX2 = validPoints.Sum(p => p.Concentration * p.Concentration);

                double denominator = (n * sumX2 - sumX * sumX);
                if (Math.Abs(denominator) < 1e-10) return;

                slope = (n * sumXY - sumX * sumY) / denominator;
                intercept = (sumY - slope * sumX) / n;

                // 计算相关系数 R²
                double yAvg = sumY / n;
                double ssRes = validPoints.Sum(p => Math.Pow(p.Intensity - (slope * p.Concentration + intercept), 2));
                double ssTot = validPoints.Sum(p => Math.Pow(p.Intensity - yAvg, 2));
                r2 = (ssTot == 0) ? 1 : 1 - (ssRes / ssTot);

                // --- 3. 计算分析性能指标 (LOD) ---
                var blank = StandardPoints.FirstOrDefault(p => p.IsBlank) ?? validPoints[0];
                double blankSD = blank.Intensity * (blank.RSD / 100.0);
                LodValue = (slope != 0) ? (3.0 * blankSD) / slope : 0;

                EquationText = $"y = {slope:F4}x + {(intercept >= 0 ? "+" : "")}{intercept:F4}";
                RSquaredText = r2.ToString("F4");
            }

            // --- 4. 浓度回算：更新左侧所有待测溶液的浓度结果 ---
            foreach (var row in FilteredResults)
            {
                if (slope != 0 && row.Status == "已完成")
                {
                    // x = (y - b) / k
                    row.CalculatedConc = Math.Round((row.Intensity - intercept) / slope, 3).ToString("F3");
                }
                else
                {
                    row.CalculatedConc = "-";
                }
            }

            // --- 5. 发送重绘信号给 View 层进行 ScottPlot 渲染 ---
            string unit = _rawFullSequence?.FirstOrDefault()?.ConcentrationUnit ?? "ppm";
            RequestPlotUpdate?.Invoke(slope, intercept, plotPoints, unit, true);
        }

        [RelayCommand]
        private void SaveCurrentCurve()
        {
            if (!CanSaveCurve || EquationText.Contains("拟合点不足") || EquationText.Contains("未执行拟合")) return;

            var config = _activeConfigs.FirstOrDefault(c => (c.ElementName.Contains("(") ? c.ElementName : $"{c.ElementName}({c.Wavelength})") == SelectedElement);
            if (config == null) return;

            // 使用方程式本身作为曲线的名称
            string curveName = EquationText;
            
            var db = _elementDbService.Load();
            if (!db.Elements.ContainsKey(config.ElementName))
                db.Elements[config.ElementName] = new ElementConfig();
            
            var elementConfig = db.Elements[config.ElementName];
            if (elementConfig.SavedCurves == null) elementConfig.SavedCurves = new();

            // 解析当前斜率和截距
            double slope = 0, intercept = 0, r2 = 0;
            var parts = EquationText.Replace("y = ", "").Replace("(", "").Replace(")", "").Split(new[] { "x + " }, StringSplitOptions.None);
            if (parts.Length == 2)
            {
                double.TryParse(parts[0], out slope);
                double.TryParse(parts[1], out intercept);
            }
            double.TryParse(RSquaredText, out r2);

            var pts = StandardPoints.Where(p => !p.IsBlank).Select(p => new PointModel
            {
                Name = p.Name,
                Concentration = p.Concentration,
                Intensity = p.Intensity,
                RSD = p.RSD
            }).ToList();

            // 如果有空白，把空白点也存进去，以便画图时有 (0, y)
            var blanks = StandardPoints.Where(p => p.IsBlank).Select(p => new PointModel
            {
                Name = p.Name,
                Concentration = 0,
                Intensity = p.Intensity,
                RSD = p.RSD
            });
            pts.InsertRange(0, blanks);

            elementConfig.SavedCurves.Add(new CalibrationCurveModel
            {
                Name = curveName,
                Slope = slope,
                Intercept = intercept,
                RSquared = r2,
                Equation = EquationText,
                Lod = LodValue,
                Points = pts
            });

            // 存入旧列表，供ComboBox下拉选择兼容
            if (!elementConfig.FittingCurves.Contains(curveName))
            {
                elementConfig.FittingCurves.Add(curveName);
            }

            _elementDbService.Save(db);

            // 发送消息通知“元素配置”页面自动刷新该元素的下拉框
            WeakReferenceMessenger.Default.Send(new CurveSavedMessage(config.ElementName));

            string saveTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            MessageBox.Show($"保存时间: {saveTime}\n\n曲线已成功保存为:\n[{curveName}]\n\n下次配置元素时即可直接选择该方程！", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion

        #region 6. 数据交换命令



        [RelayCommand] private void ExportData() => MessageBox.Show("功能开发中：导出结果报表...");
        [RelayCommand] private void GenerateReport() => MessageBox.Show("功能开发中：生成分析报告...");

        #endregion
    }
}