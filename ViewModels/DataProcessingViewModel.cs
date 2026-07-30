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
using System.Threading.Tasks;
using System.IO;
using System.IO.Compression;

namespace GD_ControlCenter_WPF.ViewModels
{
    public class ReportDataModel
    {
        public string ElementName { get; set; } = string.Empty;
        public string CurveTime { get; set; } = string.Empty;
        public string MeasurementDate { get; set; } = string.Empty;
        public int Repeats { get; set; }
        public double Interval { get; set; }
        public string ConcentrationUnit { get; set; } = string.Empty;
        
        public string Equation { get; set; } = string.Empty;
        public string RSquared { get; set; } = string.Empty;
        public double Lod { get; set; }
        
        public List<StandardPointRow> StandardPoints { get; set; } = new();
        public List<ContinuousElementResultRow> SampleResults { get; set; } = new();
    }
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
        
        // 获取图表截图回调
        public Func<string>? CapturePlotImageAction { get; set; }

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
        [ObservableProperty] private string _displayUnit = "ppm";

        // 是否可以使用保存曲线功能 (历史曲线模式下不可用)
        [ObservableProperty] private bool _canSaveCurve = true;
        // 当前显示的曲线是否已经被保存过，防止重复保存
        [ObservableProperty] private bool _isCurrentCurveSaved = false;

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
        public void TriggerPlotUpdate()
        {
            UpdateFilteredData();
        }

        private void UpdateFilteredData()
        {
            // 安全检查
            if (string.IsNullOrEmpty(SelectedElement)) return;

            var config = _activeConfigs.FirstOrDefault(c => (c.ElementName.Contains("(") ? c.ElementName : $"{c.ElementName}({c.Wavelength})") == SelectedElement);
            if (config != null)
            {
                CanSaveCurve = config.FittingCurve == "测量校准曲线";
                IsCurrentCurveSaved = false;
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
                    var wConfig = elementConfig?.Wavelengths.FirstOrDefault(w => w.Wavelength == config.Wavelength);
                    var savedCurve = wConfig?.SavedCurves?.FirstOrDefault(c => c.Name == config.FittingCurve);
                    
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
                var wConfig = elementConfig?.Wavelengths.FirstOrDefault(w => w.Wavelength == config.Wavelength);
                var savedCurve = wConfig?.SavedCurves?.FirstOrDefault(c => c.Name == config.FittingCurve);
                
                if (savedCurve != null)
                {
                    slope = savedCurve.Slope;
                    intercept = savedCurve.Intercept;
                    r2 = savedCurve.RSquared;
                    EquationText = savedCurve.Equation;
                    RSquaredText = r2.ToString("F4");
                    LodValue = savedCurve.Lod;
                    DisplayUnit = _rawFullSequence?.FirstOrDefault()?.ConcentrationUnit ?? "ppm";
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
                    string fallbackUnit = _rawFullSequence?.FirstOrDefault()?.ConcentrationUnit ?? "ppm";
                    DisplayUnit = fallbackUnit;
                    RequestPlotUpdate?.Invoke(0, 0, StandardPoints.ToList(), fallbackUnit, false);
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

                EquationText = $"y = {slope:0.00}x {(intercept >= 0 ? "+ " : "- ")}{Math.Abs(intercept):0.00}";
                RSquaredText = r2.ToString("0.####");
            }

            // --- 4. 浓度回算：更新左侧所有待测溶液的浓度结果 ---
            foreach (var row in FilteredResults)
            {
                if (slope != 0 && row.Status == "已完成")
                {
                    // x = (y - b) / k
                    row.CalculatedConc = Math.Round((row.Intensity - intercept) / slope, 3).ToString("0.###");
                }
                else
                {
                    row.CalculatedConc = "-";
                }
            }

            // --- 5. 发送重绘信号给 View 层进行 ScottPlot 渲染 ---
            string unit = _rawFullSequence?.FirstOrDefault()?.ConcentrationUnit ?? "ppm";
            DisplayUnit = unit;
            RequestPlotUpdate?.Invoke(slope, intercept, plotPoints, unit, true);
        }

        [RelayCommand]
        private void SaveCurrentCurve()
        {
            SaveCurrentCurveCore(false);
        }

        private void SaveCurrentCurveCore(bool isAutoSave)
        {
            if (!CanSaveCurve || EquationText.Contains("拟合点不足") || EquationText.Contains("未执行拟合")) return;
            
            if (IsCurrentCurveSaved)
            {
                if (!isAutoSave) MessageBox.Show("当前曲线已保存过，无需重复保存！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var config = _activeConfigs.FirstOrDefault(c => (c.ElementName.Contains("(") ? c.ElementName : $"{c.ElementName}({c.Wavelength})") == SelectedElement);
            if (config == null) return;

            // 使用方程式本身作为曲线的名称
            string curveName = EquationText;
            
            var db = _elementDbService.Load();
            if (!db.Elements.ContainsKey(config.ElementName))
                db.Elements[config.ElementName] = new ElementConfig();
            
            var elementConfig = db.Elements[config.ElementName];
            var wConfig = elementConfig.Wavelengths.FirstOrDefault(w => w.Wavelength == config.Wavelength);
            if (wConfig == null) 
            {
                wConfig = new WavelengthConfig { Wavelength = config.Wavelength };
                elementConfig.Wavelengths.Add(wConfig);
            }
            if (wConfig.SavedCurves == null) wConfig.SavedCurves = new();

            // 解析当前斜率和截距
            double slope = 0, intercept = 0, r2 = 0;
            string eq = EquationText.Replace("y = ", "").Replace("(", "").Replace(")", "").Trim();
            int xIndex = eq.IndexOf('x');
            if (xIndex > 0)
            {
                double.TryParse(eq.Substring(0, xIndex), out slope);
                string interceptStr = eq.Substring(xIndex + 1).Replace(" ", "");
                double.TryParse(interceptStr, out intercept);
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

            wConfig.SavedCurves.Add(new CalibrationCurveModel
            {
                Name = curveName,
                Slope = slope,
                Intercept = intercept,
                RSquared = r2,
                Equation = EquationText,
                Lod = LodValue,
                SaveTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Points = pts
            });

            // 存入旧列表，供ComboBox下拉选择兼容
            if (!wConfig.FittingCurves.Contains(curveName))
            {
                wConfig.FittingCurves.Add(curveName);
            }

            _elementDbService.Save(db);

            // 发送消息通知“元素配置”页面自动刷新该元素的下拉框
            WeakReferenceMessenger.Default.Send(new CurveSavedMessage(config.ElementName));

            if (!isAutoSave)
            {
                string saveTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                MessageBox.Show($"保存时间: {saveTime}\n\n曲线已成功保存为:\n[{curveName}]\n\n下次配置元素时即可直接选择该方程！", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            IsCurrentCurveSaved = true;
        }

        #endregion

        #region 6. 数据交换命令



        [RelayCommand] private void ExportData() => MessageBox.Show("功能开发中：导出结果报表...");
        
        [RelayCommand]
        private async Task GenerateReport()
        {
            if (_rawFullSequence == null || _rawFullSequence.Count == 0)
            {
                MessageBox.Show("当前没有可导出的测量数据！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 要在全部测量完成后，才能保存这个
            bool isAllCompleted = _rawFullSequence.All(s => s.Status == "已完成");
            if (!isAllCompleted)
            {
                MessageBox.Show("测量尚未全部完成，请在全部测量完成后再导出报告！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "PDF 报告文件 (*.pdf)|*.pdf",
                FileName = $"分析报告_{DateTime.Now:yyyyMMdd_HHmm}.pdf"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    List<ReportDataModel> allReports = new();
                    List<string> allImages = new();
                    List<string> autoSavedCurves = new();
                    
                    // 记录原始选中的元素，最后恢复
                    string originalSelection = SelectedElement;

                    foreach (var configItem in _activeConfigs)
                    {
                        string elementName = configItem.ElementName.Contains("(") ? configItem.ElementName : $"{configItem.ElementName}({configItem.Wavelength})";
                        
                        // 强制切换当前选中元素，触发重新过滤数据和拟合
                        SelectedElement = elementName;
                        
                        // 等待 300ms 使得后台拟合任务和 UI 绘图渲染完成
                        await Task.Delay(300);

                        if (CanSaveCurve && !IsCurrentCurveSaved && !EquationText.Contains("拟合点不足") && !EquationText.Contains("未执行拟合"))
                        {
                            SaveCurrentCurveCore(true);
                            autoSavedCurves.Add($"[{elementName}] {EquationText}");
                        }

                        string imagePath = string.Empty;
                        if (CapturePlotImageAction != null)
                        {
                            imagePath = CapturePlotImageAction.Invoke();
                        }
                        
                        string curveTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        if (configItem.FittingCurve != "测量校准曲线")
                        {
                            var db = _elementDbService.Load();
                            var elementConfig = db.Elements.GetValueOrDefault(configItem.ElementName);
                            var wConfig = elementConfig?.Wavelengths.FirstOrDefault(w => w.Wavelength == configItem.Wavelength);
                            var savedCurve = wConfig?.SavedCurves?.FirstOrDefault(c => c.Name == configItem.FittingCurve);
                            if (savedCurve != null && !string.IsNullOrEmpty(savedCurve.SaveTime))
                            {
                                curveTime = savedCurve.SaveTime;
                            }
                        }

                        var firstSample = _rawFullSequence.FirstOrDefault();

                        var reportData = new ReportDataModel
                        {
                            ElementName = SelectedElement,
                            CurveTime = curveTime,
                            MeasurementDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            Repeats = firstSample?.Repeats ?? 1,
                            Interval = firstSample?.Interval ?? 0,
                            ConcentrationUnit = firstSample?.ConcentrationUnit ?? "ppm",
                            Equation = EquationText,
                            RSquared = RSquaredText,
                            Lod = LodValue,
                            StandardPoints = StandardPoints.ToList(),
                            SampleResults = FilteredResults.ToList()
                        };
                        
                        allReports.Add(reportData);
                        allImages.Add(imagePath);
                    }
                    
                    // 恢复原始选中项
                    SelectedElement = originalSelection;
                    
                    // 生成多页 PDF
                    var pdfService = new PdfExportService();
                    await Task.Run(() => pdfService.ExportMultiElementReport(dialog.FileName, allReports, allImages));

                    // 清理临时图片
                    foreach (var img in allImages)
                    {
                        if (!string.IsNullOrEmpty(img) && System.IO.File.Exists(img))
                        {
                            try { System.IO.File.Delete(img); } catch { }
                        }
                    }

                    string msg = "PDF 分析报告已成功导出！";
                    if (autoSavedCurves.Count > 0)
                    {
                        msg += "\n\n以下元素的当前测量曲线已为您自动保存入库：\n" + string.Join("\n", autoSavedCurves);
                    }
                    MessageBox.Show(msg, "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        [RelayCommand]
        private async Task GenerateZipReport()
        {
            if (_rawFullSequence == null || _rawFullSequence.Count == 0)
            {
                MessageBox.Show("当前没有可导出的测量数据！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool isAllCompleted = _rawFullSequence.All(s => s.Status == "已完成");
            if (!isAllCompleted)
            {
                MessageBox.Show("测量尚未全部完成，请在全部测量完成后再导出报告！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_activeConfigs == null || _activeConfigs.Count == 0)
            {
                MessageBox.Show("当前没有活跃的分析元素！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "ZIP 压缩文件 (*.zip)|*.zip",
                FileName = $"详细数据打包_{DateTime.Now:yyyyMMdd_HHmm}.zip"
            };

            if (dialog.ShowDialog() == true)
            {
                string tempDir = Path.Combine(Path.GetTempPath(), $"GD_Export_{Guid.NewGuid()}");
                
                try
                {
                    Directory.CreateDirectory(tempDir);
                    string pdfPath = Path.Combine(tempDir, $"分析报告_{DateTime.Now:yyyyMMdd_HHmm}.pdf");

                    // 1. 生成 PDF 到临时目录
                    List<ReportDataModel> allReports = new();
                    List<string> allImages = new();
                    List<string> autoSavedCurves = new();
                    string originalSelection = SelectedElement;

                    foreach (var configItem in _activeConfigs)
                    {
                        string elementName = configItem.ElementName.Contains("(") ? configItem.ElementName : $"{configItem.ElementName}({configItem.Wavelength})";
                        SelectedElement = elementName;
                        await Task.Delay(300);

                        if (CanSaveCurve && !IsCurrentCurveSaved && !EquationText.Contains("拟合点不足") && !EquationText.Contains("未执行拟合"))
                        {
                            SaveCurrentCurveCore(true);
                            autoSavedCurves.Add($"[{elementName}] {EquationText}");
                        }

                        string imagePath = string.Empty;
                        if (CapturePlotImageAction != null)
                        {
                            imagePath = CapturePlotImageAction.Invoke();
                        }
                        
                        string curveTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        if (configItem.FittingCurve != "测量校准曲线")
                        {
                            var db = _elementDbService.Load();
                            var elementConfig = db.Elements.GetValueOrDefault(configItem.ElementName);
                            var wConfig = elementConfig?.Wavelengths.FirstOrDefault(w => w.Wavelength == configItem.Wavelength);
                            var savedCurve = wConfig?.SavedCurves?.FirstOrDefault(c => c.Name == configItem.FittingCurve);
                            if (savedCurve != null && !string.IsNullOrEmpty(savedCurve.SaveTime))
                            {
                                curveTime = savedCurve.SaveTime;
                            }
                        }

                        var firstSample = _rawFullSequence.FirstOrDefault();

                        var reportData = new ReportDataModel
                        {
                            ElementName = SelectedElement,
                            CurveTime = curveTime,
                            MeasurementDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            Repeats = firstSample?.Repeats ?? 1,
                            Interval = firstSample?.Interval ?? 0,
                            ConcentrationUnit = firstSample?.ConcentrationUnit ?? "ppm",
                            Equation = EquationText,
                            RSquared = RSquaredText,
                            Lod = LodValue,
                            StandardPoints = StandardPoints.ToList(),
                            SampleResults = FilteredResults.ToList()
                        };
                        
                        allReports.Add(reportData);
                        allImages.Add(imagePath);
                    }
                    
                    SelectedElement = originalSelection;
                    
                    var pdfService = new PdfExportService();
                    await Task.Run(() => pdfService.ExportMultiElementReport(pdfPath, allReports, allImages));

                    foreach (var img in allImages)
                    {
                        if (!string.IsNullOrEmpty(img) && File.Exists(img))
                        {
                            try { File.Delete(img); } catch { }
                        }
                    }

                    // 2. 拷贝所有的 CSV 文件
                    string recordsDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "GD_ControlCenter", "Records");
                    HashSet<string> filesToCopy = new HashSet<string>();

                    // 2.1 收集当前测量序列中的所有样品 CSV
                    foreach (var sample in _rawFullSequence)
                    {
                        string targetCsv = sample.CsvFilePath;
                        // Fallback：如果没有路径记录（如旧版测量的遗留数据），去文件夹里找名字匹配的最新文件
                        if (string.IsNullOrEmpty(targetCsv) || !File.Exists(targetCsv))
                        {
                            if (Directory.Exists(recordsDir))
                            {
                                var matchedFiles = Directory.GetFiles(recordsDir, $"{sample.SampleName}_*.csv");
                                if (matchedFiles.Any())
                                    targetCsv = matchedFiles.OrderByDescending(f => File.GetLastWriteTime(f)).First();
                            }
                        }

                        if (!string.IsNullOrEmpty(targetCsv) && File.Exists(targetCsv))
                        {
                            filesToCopy.Add(targetCsv);
                        }
                    }

                    // 2.2 收集校准曲线所用到的标准品 CSV（兼顾调用的历史曲线）
                    foreach (var report in allReports)
                    {
                        if (report.StandardPoints != null)
                        {
                            foreach (var stdPoint in report.StandardPoints)
                            {
                                if (Directory.Exists(recordsDir))
                                {
                                    var matchedFiles = Directory.GetFiles(recordsDir, $"{stdPoint.Name}_*.csv");
                                    if (matchedFiles.Any())
                                    {
                                        string targetCsv = matchedFiles.OrderByDescending(f => File.GetLastWriteTime(f)).First();
                                        filesToCopy.Add(targetCsv);
                                    }
                                }
                            }
                        }
                    }

                    // 2.3 执行所有搜集到的 CSV 文件拷贝
                    foreach (var file in filesToCopy)
                    {
                        string destFile = Path.Combine(tempDir, Path.GetFileName(file));
                        File.Copy(file, destFile, true);
                    }

                    // 3. 压缩打包
                    if (File.Exists(dialog.FileName))
                    {
                        File.Delete(dialog.FileName);
                    }
                    ZipFile.CreateFromDirectory(tempDir, dialog.FileName);

                    string msg = "压缩包导出成功！包含 PDF 报告以及对应的全部明细 CSV 文件。";
                    if (autoSavedCurves.Count > 0)
                    {
                        msg += "\n\n以下元素的当前测量曲线已为您自动保存入库：\n" + string.Join("\n", autoSavedCurves);
                    }
                    MessageBox.Show(msg, "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"打包失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    // 清理临时文件夹
                    if (Directory.Exists(tempDir))
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            }
        }

        #endregion
    }
}