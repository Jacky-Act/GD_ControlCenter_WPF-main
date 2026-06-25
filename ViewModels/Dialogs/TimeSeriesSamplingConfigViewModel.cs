using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GD_ControlCenter_WPF.Models;
using GD_ControlCenter_WPF.Services;
using GD_ControlCenter_WPF.Services.Spectrometer;
using System.Collections.ObjectModel;
using System.ComponentModel;

/*
 * 文件名: TimeSeriesSamplingConfigViewModel.cs
 * 描述: 时序图采样节点配置弹窗的视图模型。
 * 负责管理用户定义的监测点与实时追踪特征峰之间的关联映射。
 * 核心逻辑:
 * 1. 采用可用性检查机制，确保一个特征峰不会被重复关联到多个监测点。
 * 2. 在选项列表首位提供“无（不关联）”选项，支持解除关联映射。
 * 3. 应用配置后同步更新本地 JSON 配置文件中的 TimeSeriesSampleNodes 集合。
 */

namespace GD_ControlCenter_WPF.ViewModels.Dialogs
{
    /// <summary>
    /// 时序采样配置视图模型：处理监测点的新增、删除及特征峰关联。
    /// </summary>
    public partial class TimeSeriesSamplingConfigViewModel : ObservableObject
    {
        #region 1. 依赖服务与私有字段

        /// <summary> 配置持久化服务。 </summary>
        private readonly JsonConfigService _configService;

        /// <summary> 特征峰追踪服务，用于获取当前活跃的波长列表。 </summary>
        private readonly PeakTrackingService _peakTrackingService;

        /// <summary> 关闭弹窗的回调动作。 </summary>
        private readonly Action _closeAction;

        #endregion

        #region 2. UI 绑定属性

        /// <summary> 当前已定义的监测点列表集合。 </summary>
        [ObservableProperty]
        private ObservableCollection<SamplingPointItem> _samplingPoints = new();

        /// <summary> 下拉框中可供选择的特征峰选项集合（含“无”选项）。 </summary>
        [ObservableProperty]
        private ObservableCollection<PeakOptionItem> _availablePeaks = new();

        /// <summary> 下拉框未就绪时的占位提示文本。 </summary>
        [ObservableProperty]
        private string _comboBoxHintText = "等待寻峰...";

        /// <summary> 准备新增的监测点名称输入值。 </summary>
        [ObservableProperty]
        private string _newSampleName = string.Empty;

        #endregion

        #region 3. 初始化与配置加载

        /// <summary>
        /// 构造时序采样配置视图模型。
        /// </summary>
        public TimeSeriesSamplingConfigViewModel(JsonConfigService configService, PeakTrackingService peakTrackingService, Action closeAction)
        {
            _configService = configService;
            _peakTrackingService = peakTrackingService;
            _closeAction = closeAction;

            // 加载实时寻峰数据
            LoadAvailablePeaks();
            // 加载本地保存的节点配置
            LoadConfig();
            // 执行首次互斥可用性计算
            UpdatePeakAvailability();
        }

        /// <summary>
        /// 从寻峰服务中提取当前正在追踪的所有波长，并封装为 UI 选项模型。
        /// </summary>
        private void LoadAvailablePeaks()
        {
            AvailablePeaks.Clear();

            // 插入首位“空”选项，允许用户取消该节点的波长关联
            AvailablePeaks.Add(new PeakOptionItem(null));

            foreach (var peak in _peakTrackingService.TrackedPeaks)
            {
                AvailablePeaks.Add(new PeakOptionItem(Math.Round(peak.BaseWavelength, 2)));
            }

            ComboBoxHintText = AvailablePeaks.Count > 1 ? "请选择关联特征峰" : "等待寻峰...";
        }

        /// <summary>
        /// 从本地配置文件加载历史节点。
        /// </summary>
        private void LoadConfig()
        {
            var config = _configService.Load();

            if (config.TimeSeriesSampleNodes != null)
            {
                foreach (var node in config.TimeSeriesSampleNodes)
                {
                    var item = new SamplingPointItem(node.Name);

                    if (node.SelectedPeakX.HasValue)
                    {
                        double targetX = node.SelectedPeakX.Value;

                        // 匹配已加载的选项，需跳过 null 选项进行浮点数近似比对
                        var matchedPeak = AvailablePeaks.FirstOrDefault(p =>
                            p.Value.HasValue && Math.Abs(p.Value.Value - targetX) < 0.01);

                        if (matchedPeak != null)
                        {
                            item.SelectedPeakX = matchedPeak.Value;
                        }
                    }

                    item.PropertyChanged += OnSamplingPointPropertyChanged;
                    SamplingPoints.Add(item);
                }
            }
        }

        #endregion

        #region 4. 互斥可用性逻辑

        /// <summary>
        /// 响应监测点属性变更。
        /// 若关联波长发生改变，则触发全局可用性重算。
        /// </summary>
        private void OnSamplingPointPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SamplingPointItem.SelectedPeakX))
            {
                UpdatePeakAvailability();
            }
        }

        /// <summary>
        /// 核心互斥算法：遍历所有选项，若该波长已被其他节点占用，则将其 IsAvailable 设为 False。
        /// “无”选项始终保持可用。
        /// </summary>
        private void UpdatePeakAvailability()
        {
            // 提取当前所有已选中的有效波长
            var selectedPeaks = SamplingPoints
                .Where(p => p.SelectedPeakX.HasValue)
                .Select(p => p.SelectedPeakX!.Value)
                .ToList();

            foreach (var peakOption in AvailablePeaks)
            {
                // “无”选项不参与互斥逻辑
                if (!peakOption.Value.HasValue)
                {
                    peakOption.IsAvailable = true;
                    continue;
                }

                // 检查该波长是否出现在已选名单中
                peakOption.IsAvailable = !selectedPeaks.Any(selected =>
                    Math.Abs(selected - peakOption.Value!.Value) < 0.01);
            }
        }

        #endregion

        #region 5. 交互命令

        /// <summary>
        /// 新增监测点。包含重名检查与空值校验。
        /// </summary>
        [RelayCommand]
        private void AddPoint()
        {
            if (string.IsNullOrWhiteSpace(NewSampleName)) return;

            string nameToAdd = NewSampleName.Trim();
            if (SamplingPoints.Any(p => p.Name == nameToAdd)) return;

            var newItem = new SamplingPointItem(nameToAdd);
            newItem.PropertyChanged += OnSamplingPointPropertyChanged;

            SamplingPoints.Add(newItem);
            NewSampleName = string.Empty;
        }

        /// <summary>
        /// 删除监测点。
        /// </summary>
        [RelayCommand]
        private void DeletePoint(SamplingPointItem item)
        {
            if (item != null && SamplingPoints.Contains(item))
            {
                item.PropertyChanged -= OnSamplingPointPropertyChanged;
                SamplingPoints.Remove(item);
                UpdatePeakAvailability();
            }
        }

        /// <summary>
        /// 持久化配置并关闭窗口。
        /// </summary>
        [RelayCommand]
        private void Apply()
        {
            var config = _configService.Load();

            // 映射 UI 模型至持久化实体
            config.TimeSeriesSampleNodes = SamplingPoints.Select(p => new TimeSeriesSampleNode
            {
                Name = p.Name,
                SelectedPeakX = p.SelectedPeakX
            }).ToList();

            _configService.Save(config);
            _closeAction?.Invoke();
        }

        #endregion
    }

    #region 辅助数据模型

    /// <summary>
    /// 监测点列表项。
    /// </summary>
    public partial class SamplingPointItem : ObservableObject
    {
        /// <summary> 监测点业务名称。 </summary>
        [ObservableProperty]
        private string _name;

        /// <summary> 关联的特征峰 X 轴坐标（波长）。若为 Null 则表示未关联。 </summary>
        [ObservableProperty]
        private double? _selectedPeakX;

        public SamplingPointItem(string name)
        {
            Name = name;
        }
    }

    /// <summary>
    /// 特征峰选项模型。用于 ComboBox 渲染，具备可用性标识。
    /// </summary>
    public partial class PeakOptionItem : ObservableObject
    {
        /// <summary> 物理波长值。Null 代表“不关联”。 </summary>
        [ObservableProperty]
        private double? _value;

        /// <summary> 互斥标识。若为 False，UI 表现为灰色不可点击。 </summary>
        [ObservableProperty]
        private bool _isAvailable = true;

        /// <summary> 动态显示文本。 </summary>
        public string DisplayText => Value.HasValue ? $"{Value.Value:F2} nm" : "无 (不关联)";

        public PeakOptionItem(double? value)
        {
            Value = value;
        }
    }

    #endregion
}