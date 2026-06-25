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
            if (_configService == null) return;
            var config = _configService.Load();
            config.LastBatchStandardCount = value;
            _configService.Save(config);
        }

        [ObservableProperty]
        private int _batchUnknownCount;

        partial void OnBatchUnknownCountChanged(int value)
        {
            if (_configService == null) return;
            var config = _configService.Load();
            config.LastBatchUnknownCount = value;
            _configService.Save(config);
        }

        [ObservableProperty]
        private int _globalRepeats;

        partial void OnGlobalRepeatsChanged(int value)
        {
            if (_configService == null) return;
            var config = _configService.Load();
            config.LastSampleRepeats = value;
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

        // 绑定到界面的 ComboBox 选项
        public Array SampleTypes => Enum.GetValues(typeof(SampleType));
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

        // --- 表格操作命令 ---

        [RelayCommand]
        private void BatchGenerate()
        {
            if (Samples.Count > 0)
            {
                var result = MessageBox.Show("一键生成将覆盖当前序列中的所有内容，是否继续？", "操作确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
            }

            _isHandlingTemplateChange = true;
            if (!SavedTemplates.Contains("未使用模板"))
                SavedTemplates.Insert(0, "未使用模板");
            SelectedTemplate = "未使用模板";
            _isHandlingTemplateChange = false;
            Samples.Clear();

            // 生成新序列时，通知元素配置页面清空数据，从而联动清空本页面的动态列
            WeakReferenceMessenger.Default.Send(new SyncTemplateElementsMessage(new List<AnalysisConfigItem>()));

            // 生成空白
            Samples.Add(CreateNewSample(SampleType.空白, "BLK-1"));
            // 生成标准品序列
            for (int i = 1; i <= BatchStandardCount; i++)
                Samples.Add(CreateNewSample(SampleType.标液, $"STD-{i}"));
            // 生成待测样序列
            for (int i = 1; i <= BatchUnknownCount; i++)
                Samples.Add(CreateNewSample(SampleType.待测液, $"待测液-{i}"));

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
            if (_activeElements == null || _activeElements.Count == 0)
            {
                MessageBox.Show("当前未选择任何分析元素，请先在“元素配置”界面加入元素！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
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
            }

            // 将当前序列推送到流动注射/测量模块
            WeakReferenceMessenger.Default.Send(new SampleSequenceChangedMessage(Samples.ToList()));
            MessageBox.Show("进样序列已成功下发至测量模块！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
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
    }
}