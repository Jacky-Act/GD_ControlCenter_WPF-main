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

        [ObservableProperty]
        private string _newTemplateName = string.Empty;

        [ObservableProperty]
        private int _batchStandardCount = 3;

        [ObservableProperty]
        private int _batchUnknownCount = 10;

        // 绑定到界面的 ComboBox 选项
        public Array SampleTypes => Enum.GetValues(typeof(SampleType));
        public List<int> AvailableRepeats { get; } = Enumerable.Range(1, 99).ToList();

        // --- 构造函数 ---

        public SampleSequenceViewModel(ElementConfigViewModel elementConfigVM)
        {
            _elementConfigVM = elementConfigVM;

            // --- 关键改动：每次启动软件，物理删除本地所有历史模板文件 ---
            _storageService.ClearAllTemplates();

            // 删除文件后刷新列表，此时下拉框将变为空白
            RefreshTemplates();

            // 初始化时确保样品列表为空
            Samples = new ObservableCollection<SampleItemModel>();

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

        /// <summary>
        /// 更新当前的活跃元素名单，补齐所有样品的浓度槽位，并通知 View 重绘列
        /// </summary>
        private void UpdateActiveElements(List<AnalysisConfigItem> configs)
        {
            _activeElements = configs.Select(x => x.ElementName).Distinct().ToList();

            // 补齐现有样品中缺失的元素槽位
            foreach (var sample in Samples)
            {
                foreach (var elName in _activeElements)
                {
                    if (sample.ElementConcentrations.All(c => c.ElementName != elName))
                    {
                        sample.ElementConcentrations.Add(new ElementConcentrationModel { ElementName = elName, ConcentrationValue = "" });
                    }
                }
            }

            // 发送消息让 View (SampleSequenceView.xaml.cs) 执行动态列构建
            WeakReferenceMessenger.Default.Send(new RebuildColumnsMessage(_activeElements));
        }

        private void RefreshTemplates()
        {
            SavedTemplates.Clear();
            foreach (var t in _storageService.GetSavedTemplates()) SavedTemplates.Add(t);
        }

        // --- 顶栏命令实现 ---

        [RelayCommand]
        private void LoadTemplate()
        {
            if (string.IsNullOrEmpty(SelectedTemplate)) return;

            var data = _storageService.LoadTemplate(SelectedTemplate);
            if (data != null)
            {
                Samples.Clear();
                // 识别模板中的元素名单
                var templateElements = data.FirstOrDefault()?.ElementConcentrations
                                           .Select(c => new AnalysisConfigItem { ElementName = c.ElementName })
                                           .ToList() ?? new List<AnalysisConfigItem>();

                foreach (var item in data) Samples.Add(item);

                // 同步更新列显示
                UpdateActiveElements(templateElements);
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
            _storageService.SaveTemplate(NewTemplateName, Samples.ToList());
            RefreshTemplates();
            SelectedTemplate = NewTemplateName;
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
            Samples.Clear();
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
            if (SelectedSample != null) Samples.Remove(SelectedSample);
        }

        [RelayCommand]
        private void ApplySequence()
        {
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