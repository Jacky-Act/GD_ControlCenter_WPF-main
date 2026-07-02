using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace GD_ControlCenter_WPF.ViewModels
{
    /// <summary>
    /// 样品序列视图模型：管理进样列表、动态元素浓度列以及序列持久化。
    /// </summary>
    public partial class SampleSequenceViewModel : ObservableObject
    {
        private readonly SequenceStorageService _storageService = new();
        private readonly ElementConfigViewModel _elementConfigVM;
        private readonly JsonConfigService _configService;

        // 当前生效的元素名单（用于 View 层重绘动态列）
        private List<string> _activeElements = new();
        public List<string> ActiveElementNames => _activeElements;

        // --- UI 绑定属性 ---

        [ObservableProperty]
        private ObservableCollection<SampleItemModel> _samples = new();

        [ObservableProperty]
        private SampleItemModel? _selectedSample;

        [ObservableProperty]
        private ObservableCollection<string> _savedTemplates = new();

        [ObservableProperty]
        private string _selectedTemplate = string.Empty;

        private bool _isHandlingTemplateChange = false;
        private string _previousTemplate = "未使用模板";

        partial void OnSelectedTemplateChanging(string value)
        {
            if (!_isHandlingTemplateChange)
            {
                _previousTemplate = SelectedTemplate;
            }
        }

        partial void OnSelectedTemplateChanged(string value)
        {
            if (_isHandlingTemplateChange) return;
            if (string.IsNullOrEmpty(value) || value == "未使用模板") return;

            if (Samples.Count > 0)
            {
                var result = MessageBox.Show($"加载模板 '{value}' 将覆盖当前序列中的所有内容，是否继续？", "加载确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _isHandlingTemplateChange = true;
                        SelectedTemplate = string.IsNullOrEmpty(_previousTemplate) ? "未使用模板" : _previousTemplate;
                        _isHandlingTemplateChange = false;
                    }), System.Windows.Threading.DispatcherPriority.Background);
                    return;
                }
            }

            LoadTemplate();
        }

        [ObservableProperty]
        private string _newTemplateName = string.Empty;

        [ObservableProperty]
        private int _batchStandardCount;

        partial void OnBatchStandardCountChanged(int value)
        {
            if (value < 0) { BatchStandardCount = 0; return; }
            if (_configService == null) return;
            var config = _configService.Load();
            config.LastBatchStandardCount = value;
            _configService.Save(config);
        }

        [ObservableProperty]
        private int _batchUnknownCount;

        partial void OnBatchUnknownCountChanged(int value)
        {
            if (value < 0) { BatchUnknownCount = 0; return; }
            if (_configService == null) return;
            var config = _configService.Load();
            config.LastBatchUnknownCount = value;
            _configService.Save(config);
        }

        [ObservableProperty]
        private int _globalRepeats;

        partial void OnGlobalRepeatsChanged(int value)
        {
            if (value < 1) { GlobalRepeats = 1; return; }
            if (value > 10000) { GlobalRepeats = 10000; return; }
            if (_configService == null) return;
            var config = _configService.Load();
            config.LastSampleRepeats = value;
            _configService.Save(config);
        }

        [ObservableProperty]
        private double _globalInterval = 0.0;

        partial void OnGlobalIntervalChanged(double value)
        {
            if (value < 0.0) { GlobalInterval = 0.0; return; }
            if (value > 3600.0) { GlobalInterval = 3600.0; return; }
            if (_configService == null) return;
            var config = _configService.Load();
            config.LastGlobalInterval = value;
            _configService.Save(config);
        }

        [ObservableProperty]
        private string _concentrationUnit = "ppm";

        partial void OnConcentrationUnitChanged(string value)
        {
            if (_configService == null) return;
            var config = _configService.Load();
            config.LastConcentrationUnit = value;
            _configService.Save(config);
        }

        [ObservableProperty]
        private bool _isRestrictedToUnknownOnly;

        [ObservableProperty]
        private bool _isSequenceApplied;

        [ObservableProperty]
        private System.Collections.ObjectModel.ObservableCollection<SampleType> _availableSampleTypes = new(Enum.GetValues(typeof(SampleType)).Cast<SampleType>());

        partial void OnIsRestrictedToUnknownOnlyChanged(bool value)
        {
            AvailableSampleTypes.Clear();
            if (value)
            {
                AvailableSampleTypes.Add(SampleType.待测液);
            }
            else
            {
                foreach (SampleType type in Enum.GetValues(typeof(SampleType)))
                {
                    AvailableSampleTypes.Add(type);
                }
            }

            // 对当前的 Samples 重新赋值以避免出现非法的 SampleType
            if (value)
            {
                foreach (var sample in Samples)
                {
                    if (sample.Type != SampleType.待测液)
                        sample.Type = SampleType.待测液;
                }
            }
        }
        public List<int> AvailableRepeats { get; } = Enumerable.Range(1, 99).ToList();
        public string[] AvailableUnits { get; } = new[] { "ppm", "ppb" };

        // --- 构造函数 ---

        public SampleSequenceViewModel(ElementConfigViewModel elementConfigVM, JsonConfigService configService)
        {
            _elementConfigVM = elementConfigVM;
            _configService = configService;

            var config = _configService.Load();
            _batchStandardCount = config.LastBatchStandardCount;
            _batchUnknownCount = config.LastBatchUnknownCount;
            _globalRepeats = config.LastSampleRepeats;
            _globalInterval = config.LastGlobalInterval;
            _concentrationUnit = string.IsNullOrEmpty(config.LastConcentrationUnit) ? "ppm" : config.LastConcentrationUnit;

            // 删除文件后刷新列表，此时下拉框将变为空白
            RefreshTemplates();

            // 初始化时确保样品列表为空，并监听集合变化实现重名实时校验
            Samples.Clear();
            Samples.CollectionChanged += Samples_CollectionChanged;

            // 拉取当前已选的分析元素
            UpdateActiveElements(_elementConfigVM.SelectedConfigs.ToList());

            // 注册消息监听
            WeakReferenceMessenger.Default.Register<ActiveConfigsChangedMessage>(this, (r, m) =>
            {
                Application.Current.Dispatcher.Invoke(() => UpdateActiveElements(m.Value));
            });

            WeakReferenceMessenger.Default.Register<SampleTypeChangedMessage>(this, (r, m) => RenameSampleSmartly(m.Value));
        }

        // --- 核心逻辑方法 ---

        private void Samples_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            IsSequenceApplied = false;

            if (e.OldItems != null)
            {
                foreach (SampleItemModel item in e.OldItems)
                    item.PropertyChanged -= OnSamplePropertyChanged;
            }
            if (e.NewItems != null)
            {
                foreach (SampleItemModel item in e.NewItems)
                    item.PropertyChanged += OnSamplePropertyChanged;
            }
        }

        private void OnSamplePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            IsSequenceApplied = false;

            if (e.PropertyName == nameof(SampleItemModel.SampleName))
            {
                var changedSample = sender as SampleItemModel;
                if (changedSample == null || string.IsNullOrWhiteSpace(changedSample.SampleName)) return;

                var count = Samples.Count(s => s.SampleName == changedSample.SampleName);
                if (count > 1)
                {
                    MessageBox.Show($"样品名称 '{changedSample.SampleName}' 已存在！系统已自动添加后缀以区分。", "名称重复", MessageBoxButton.OK, MessageBoxImage.Warning);
                    
                    string baseName = changedSample.SampleName;
                    int index = 1;
                    while (Samples.Any(s => s != changedSample && s.SampleName == $"{baseName}_{index}"))
                    {
                        index++;
                    }
                    changedSample.SampleName = $"{baseName}_{index}";
                }
            }
        }

        [RelayCommand]
        private void DecreaseRepeats()
        {
            if (GlobalRepeats > 1) GlobalRepeats--;
        }

        [RelayCommand]
        private void IncreaseRepeats()
        {
            if (GlobalRepeats < 99) GlobalRepeats++;
        }

        /// <summary>
        /// 更新当前的活跃元素名单，补齐所有样品的浓度槽位，并通知 View 重绘列
        /// </summary>
        private void UpdateActiveElements(List<AnalysisConfigItem> configs)
        {
            if (configs.Count > 0)
            {
                // 只有当【所有】元素都使用了历史曲线时，才限制只能测“待测液”
                IsRestrictedToUnknownOnly = configs.All(c => c.FittingCurve != "测量校准曲线");
            }
            else
            {
                IsRestrictedToUnknownOnly = false;
            }

            _activeElements = configs.Select(x => 
                x.ElementName.Contains("(") ? x.ElementName : $"{x.ElementName}({x.Wavelength})"
            ).Distinct().ToList();

            // 严格对齐现有样品的元素槽位和顺序，防止动态列数据错位
            foreach (var sample in Samples)
            {
                var newConcentrations = new System.Collections.ObjectModel.ObservableCollection<ElementConcentrationModel>();
                foreach (var elName in _activeElements)
                {
                    var existing = sample.ElementConcentrations.FirstOrDefault(c => c.ElementName == elName);
                    if (existing != null)
                    {
                        newConcentrations.Add(existing);
                    }
                    else
                    {
                        newConcentrations.Add(new ElementConcentrationModel { ElementName = elName, ConcentrationValue = "" });
                    }
                }
                sample.ElementConcentrations = newConcentrations;
            }

            // 发送消息让 View (SampleSequenceView.xaml.cs) 执行动态列构建
            WeakReferenceMessenger.Default.Send(new RebuildColumnsMessage(_activeElements));
        }

        [RelayCommand]
        public void RefreshTemplates()
        {
            _isHandlingTemplateChange = true;
            
            var oldSelected = SelectedTemplate;
            SavedTemplates.Clear();
            
            // 只有在一开始没选择模板，或者被重置时，才在列表中加入“未使用模板”
            if (string.IsNullOrEmpty(oldSelected) || oldSelected == "未使用模板")
            {
                SavedTemplates.Add("未使用模板");
            }
            
            foreach (var t in _storageService.GetSavedTemplates()) SavedTemplates.Add(t);
            
            if (SavedTemplates.Contains(oldSelected))
                SelectedTemplate = oldSelected;
            else if (!string.IsNullOrEmpty(oldSelected) && oldSelected != "未使用模板")
            {
                // 如果之前选择的模板被删除了，回退到未使用模板
                SavedTemplates.Insert(0, "未使用模板");
                SelectedTemplate = "未使用模板";
            }
            else
                SelectedTemplate = "未使用模板";
                
            _isHandlingTemplateChange = false;
        }

        // --- 顶栏命令实现 ---

        [RelayCommand]
        private void LoadTemplate()
        {
            if (string.IsNullOrEmpty(SelectedTemplate) || SelectedTemplate == "未使用模板") return;

            var data = _storageService.LoadTemplate(SelectedTemplate);
            if (data != null)
            {
                _isDirtyOverwrite = true;
                Samples.Clear();
                // 识别模板中的元素名单，提取名称和波长
                var templateElements = data.FirstOrDefault()?.ElementConcentrations
                                           .Select(c =>
                                           {
                                               string raw = c.ElementName;
                                               string symbol = raw;
                                               double wl = 0;
                                               int idx = raw.IndexOf('(');
                                               if (idx > 0)
                                               {
                                                   symbol = raw.Substring(0, idx);
                                                   string wlStr = raw.Substring(idx + 1).TrimEnd(')');
                                                   double.TryParse(wlStr, out wl);
                                               }
                                               return new AnalysisConfigItem { ElementName = symbol, Wavelength = wl };
                                           })
                                           .ToList() ?? new List<AnalysisConfigItem>();

                foreach (var item in data) Samples.Add(item);

                // 从模板中恢复顶部的“标准样品”、“待测样品”、“重复次数”、“间隔”和“浓度单位”配置
                if (data.Count > 0)
                {
                    GlobalRepeats = data[0].Repeats > 0 ? data[0].Repeats : 1;
                    GlobalInterval = data[0].Interval >= 0 ? data[0].Interval : 0.0;
                    
                    if (!string.IsNullOrEmpty(data[0].ConcentrationUnit))
                    {
                        ConcentrationUnit = data[0].ConcentrationUnit;
                    }

                    // 统计模板中的标液和待测液数量，反向更新给上方生成器
                    BatchStandardCount = data.Count(s => s.Type == SampleType.标液);
                    BatchUnknownCount = data.Count(s => s.Type == SampleType.待测液);
                }

                // 通知元素配置页面同步更新底层数据
                WeakReferenceMessenger.Default.Send(new SyncTemplateElementsMessage(templateElements));
            }
        }

        [RelayCommand]
        private void OpenTemplateFolder()
        {
            string path = _storageService.GetFolderPath();
            if (System.IO.Directory.Exists(path))
            {
                System.Diagnostics.Process.Start("explorer.exe", path);
            }
        }

        [RelayCommand]
        private void SaveTemplate()
        {
            if (string.IsNullOrWhiteSpace(NewTemplateName))
            {
                MessageBox.Show("请输入模板名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var existing = _storageService.GetSavedTemplates();
            if (existing.Contains(NewTemplateName))
            {
                var result = MessageBox.Show($"模板 '{NewTemplateName}' 已存在，是否覆盖？", "重名确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.No) return;
            }

            // 保存前，将顶栏的全局参数强制写入每一行数据中，确保能被序列化保存
            foreach (var s in Samples)
            {
                s.Repeats = GlobalRepeats;
                s.Interval = GlobalInterval;
                s.ConcentrationUnit = ConcentrationUnit;
            }

            _storageService.SaveTemplate(NewTemplateName, Samples.ToList());
            RefreshTemplates();
            MessageBox.Show("序列模板保存成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        [RelayCommand]
        private void ImportCsv()
        {
            var dialog = new OpenFileDialog { Filter = "CSV 文件 (*.csv)|*.csv" };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    // 使用增强版 Service 导入
                    var importedData = _storageService.ImportFromCsv(dialog.FileName, out var detectedElements);

                    var newConfigs = detectedElements.Select(e => new AnalysisConfigItem { ElementName = e }).ToList();

                    _isDirtyOverwrite = true;
                    Samples.Clear();
                    foreach (var item in importedData) Samples.Add(item);

                    // 触发界面更新
                    UpdateActiveElements(newConfigs);
                    MessageBox.Show("CSV 导入成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导入失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        [RelayCommand]
        private void ExportCsv()
        {
            if (Samples.Count == 0) return;

            var dialog = new SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = $"序列导出_{DateTime.Now:yyyyMMdd_HHmm}"
            };

            if (dialog.ShowDialog() == true)
            {
                // 导出当前显示的样品及所有动态浓度列
                _storageService.ExportToCsv(dialog.FileName, Samples.ToList(), _activeElements);
                MessageBox.Show("导出 CSV 成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private bool _isDirtyOverwrite = true;

        // --- 表格操作命令 ---

        [RelayCommand]
        private void BatchGenerate()
        {
            if (Samples.Count > 0)
            {
                var result = MessageBox.Show("一键生成将清空当前所有数据，确认继续吗？", "警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.No) return;
            }

            _isDirtyOverwrite = true;
            _isHandlingTemplateChange = true;
            if (!SavedTemplates.Contains("未使用模板"))
                SavedTemplates.Insert(0, "未使用模板");
            SelectedTemplate = "未使用模板";
            _isHandlingTemplateChange = false;
            Samples.Clear();

            // 生成新序列时，保留当前已选的元素配置，不再清空
            // WeakReferenceMessenger.Default.Send(new SyncTemplateElementsMessage(new List<AnalysisConfigItem>()));

            if (IsRestrictedToUnknownOnly)
            {
                int unknownCount = BatchUnknownCount; // 允许为0
                for (int i = 1; i <= unknownCount; i++)
                    Samples.Add(CreateNewSample(SampleType.待测液, $"待测液-{i}"));
                
                MessageBox.Show("因当前元素配置使用了已保存的拟合曲线，已自动为您省略空白与标液，仅生成待测液。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                // 生成空白
                Samples.Add(CreateNewSample(SampleType.空白, "BLK-1"));
                // 生成标准品序列
                for (int i = 1; i <= BatchStandardCount; i++)
                    Samples.Add(CreateNewSample(SampleType.标液, $"STD-{i}"));
                // 生成待测样序列
                for (int i = 1; i <= BatchUnknownCount; i++)
                    Samples.Add(CreateNewSample(SampleType.待测液, $"待测液-{i}"));
            }

            WeakReferenceMessenger.Default.Send(new RebuildColumnsMessage(_activeElements));
        }

        [RelayCommand]
        private void AddRow() => Samples.Add(CreateNewSample(SampleType.待测液, "新样品"));

        [RelayCommand]
        private void DeleteRow()
        {
            if (SelectedSample != null)
            {
                var result = MessageBox.Show($"确定要删除选中行 ({SelectedSample.SampleName}) 吗？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    Samples.Remove(SelectedSample);
                    SelectedSample = null;
                }
            }
            else
            {
                MessageBox.Show("请先在表格中选中要删除的行！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        [RelayCommand]
        private void ApplySequence()
        {
            if (Samples == null || Samples.Count == 0)
            {
                MessageBox.Show("当前样品序列为空，请先添加样品或生成序列后再应用！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_activeElements == null || _activeElements.Count == 0)
            {
                MessageBox.Show("当前未选择任何分析元素，请先在“元素配置”界面加入元素！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (IsRestrictedToUnknownOnly && Samples.Any(s => s.Type != SampleType.待测液))
            {
                MessageBox.Show("应用失败：当前元素配置使用了已保存的拟合曲线，不允许测量标液或空白！\n\n请修改相关样品类型，或返回“元素配置”更改曲线设置。", "应用被拦截", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 检查标液的浓度是否都已填写
            var incompleteStandards = Samples.Where(s => s.Type == SampleType.标液 && 
                                                         s.ElementConcentrations.Any(c => string.IsNullOrWhiteSpace(c.ConcentrationValue)))
                                             .Select(s => s.SampleName).ToList();
            if (incompleteStandards.Count > 0)
            {
                MessageBox.Show($"以下标液的元素浓度未完全填写：\n{string.Join(", ", incompleteStandards)}\n\n请将标液的所有浓度补充完整后再应用！", "标液浓度缺失拦截", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 检查同一元素的标液浓度是否重复
            var standardSamples = Samples.Where(s => s.Type == SampleType.标液).ToList();
            if (standardSamples.Count > 1)
            {
                var elementNames = standardSamples.First().ElementConcentrations.Select(c => c.ElementName).ToList();
                foreach (var elementName in elementNames)
                {
                    var concentrations = new HashSet<double>();
                    foreach (var sample in standardSamples)
                    {
                        var concModel = sample.ElementConcentrations.FirstOrDefault(c => c.ElementName == elementName);
                        if (concModel != null && double.TryParse(concModel.ConcentrationValue, out double concValue))
                        {
                            if (!concentrations.Add(concValue))
                            {
                                MessageBox.Show($"应用失败：元素 '{elementName}' 在多个标准溶液中配置了相同的浓度值 ({concValue})！\n\n同一元素的标准溶液浓度必须各不相同，请修改后重试。", "标液浓度重复拦截", MessageBoxButton.OK, MessageBoxImage.Error);
                                return;
                            }
                        }
                    }
                }
            }

            // 检查重名
            var duplicateNames = Samples.GroupBy(s => s.SampleName).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicateNames.Count > 0)
            {
                MessageBox.Show($"存在重名的样品：{string.Join(", ", duplicateNames)}\n\n请修改样品名称，确保它们是唯一的！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

        // 【新需求】应用时，将全局的 Repeats 写入每个 SampleItemModel
        foreach (var sample in Samples)
        {
            sample.Repeats = GlobalRepeats;
            sample.Interval = GlobalInterval;
        }

        // 将当前序列深拷贝后推送到流动注射/测量模块，防止下游模块的修改污染本页面的原始数据
        var clonedSamples = Samples.Select(s => new SampleItemModel
        {
            SampleName = s.SampleName,
            Type = s.Type,
            Repeats = s.Repeats,
            Interval = s.Interval,
            Status = s.Status,
            ElementConcentrations = new ObservableCollection<ElementConcentrationModel>(
                s.ElementConcentrations.Select(c => new ElementConcentrationModel
                {
                    ElementName = c.ElementName,
                    ConcentrationValue = c.ConcentrationValue,
                    MeasuredIntensity = c.MeasuredIntensity,
                    MeasuredRsd = c.MeasuredRsd
                }))
        }).ToList();

        WeakReferenceMessenger.Default.Send(new SampleSequenceChangedMessage(clonedSamples, _isDirtyOverwrite));
        _isDirtyOverwrite = false;
        
        // 标记已应用
        IsSequenceApplied = true;

        MessageBox.Show("进样序列已成功下发至测量模块！\n点击确定后将为您自动跳转至测样分析界面。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);

        // 跳转到测样分析
        WeakReferenceMessenger.Default.Send(new NavigateMessage("AnalysisWorkstation"));
    }

    // --- 私有辅助 ---

    private SampleItemModel CreateNewSample(SampleType type, string name)
    {
            var sample = new SampleItemModel { Type = type, SampleName = name };
            foreach (var el in _activeElements)
            {
                sample.ElementConcentrations.Add(new ElementConcentrationModel { ElementName = el, ConcentrationValue = "" });
            }
            return sample;
        }

        private void RenameSampleSmartly(SampleItemModel item)
        {
            // 安全拦截
            // 如果当前样品实例尚未加入到当前的活动 Samples 列表中（例如正处于反序列化、CSV解析或克隆过程中），
            // 必须直接返回，保留其原有的 SampleName 属性，严禁执行自动重命名劫持。
            if (Samples == null || !Samples.Contains(item)) return;

            // 根据类型自动编号逻辑
            string prefix = item.Type switch
            {
                SampleType.空白 => "BLK-",
                SampleType.标液 => "STD-",
                _ => "待测样-"
            };

            int maxIndex = 0;
            foreach (var s in Samples)
            {
                if (s != item && s.SampleName.StartsWith(prefix))
                {
                    string numPart = s.SampleName.Substring(prefix.Length);
                    if (int.TryParse(numPart, out int idx) && idx > maxIndex) maxIndex = idx;
                }
            }
            item.SampleName = $"{prefix}{maxIndex + 1}";
        }

        public void CheckDuplicateStandardConcentration()
        {
            var standardSamples = Samples.Where(s => s.Type == SampleType.标液).ToList();
            if (standardSamples.Count <= 1) return;

            var elementNames = standardSamples.First().ElementConcentrations.Select(c => c.ElementName).ToList();
            foreach (var elementName in elementNames)
            {
                var concentrations = new HashSet<double>();
                foreach (var sample in standardSamples)
                {
                    var concModel = sample.ElementConcentrations.FirstOrDefault(c => c.ElementName == elementName);
                    if (concModel != null && double.TryParse(concModel.ConcentrationValue, out double concValue))
                    {
                        if (!concentrations.Add(concValue))
                        {
                            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                MessageBox.Show($"元素 '{elementName}' 在多个标准溶液中配置了相同的浓度值 ({concValue})！\n\n同一元素的标准溶液浓度必须各不相同，请修改。", "标液浓度重复", MessageBoxButton.OK, MessageBoxImage.Warning);
                                concModel.ConcentrationValue = string.Empty;
                            }));
                            return;
                        }
                    }
                }
            }
        }
    }
}