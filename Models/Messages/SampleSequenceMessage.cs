using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GD_ControlCenter_WPF.Models.Messages
{
    public enum SampleType { 空白, 标液, 待测液 }

    /// <summary>
    /// 单次测量数据实体 (用于测量强度预览)
    /// </summary>
    public partial class MeasurementRepModel : ObservableObject
    {
        [ObservableProperty] private int _repIndex;
        [ObservableProperty] private double? _intensity;
        [ObservableProperty] private bool _isMeasuring;
    }

    /// <summary>
    /// 单个元素的浓度实体（解决 WPF 字典绑定不更新的痛点）
    /// </summary>
    public partial class ElementConcentrationModel : ObservableObject
    {
        [ObservableProperty] private string _elementName = string.Empty;
        [ObservableProperty] private string _concentrationValue = string.Empty;
        [ObservableProperty] private double _measuredIntensity;
        [ObservableProperty] private double _measuredRsd;
        
        // 存放多次重复测量的详细数据集合
        public ObservableCollection<MeasurementRepModel> Reps { get; set; } = new();

        public ElementConcentrationModel Clone()
        {
            return new ElementConcentrationModel
            {
                ElementName = this.ElementName,
                ConcentrationValue = this.ConcentrationValue
            };
        }
    }

    /// <summary>
    /// 样品序列中的单行数据实体
    /// </summary>
    public partial class SampleItemModel : ObservableObject
    {
        [ObservableProperty] private string _sampleName = "新样品";
        [ObservableProperty] private SampleType _type = SampleType.待测液;
        [ObservableProperty] private int _repeats = 1;
        [ObservableProperty] private double _interval = 0.0; // 每次采集之间的间隔，单位秒
        [ObservableProperty] private string _concentrationUnit = "ppm"; // 附加单位记录，供模板恢复使用
        [ObservableProperty] private string _status = "等待";

        // 改用 ObservableCollection 存放各个元素的浓度，完美支持双向绑定
        public ObservableCollection<ElementConcentrationModel> ElementConcentrations { get; set; } = new();

        // 拦截类型变化，触发智能重命名
        partial void OnTypeChanged(SampleType value)
        {
            OnPropertyChanged(nameof(DisableConcentration));
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new SampleTypeChangedMessage(this));
        }

        // 非标液不允许输入浓度
        public bool DisableConcentration => Type != SampleType.标液;

        public SampleItemModel Clone()
        {
            var clone = new SampleItemModel
            {
                SampleName = this.SampleName + " - 副本",
                Type = this.Type,
                Repeats = this.Repeats,
                Interval = this.Interval,
                ConcentrationUnit = this.ConcentrationUnit,
                Status = "等待",
                ElementConcentrations = new ObservableCollection<ElementConcentrationModel>(this.ElementConcentrations.Select(c => c.Clone()))
            };
            return clone;
        }
    }

    // --- 跨模块通讯消息 ---

    public class SampleSequenceChangedMessage : ValueChangedMessage<List<SampleItemModel>>
    {
        public bool IsOverwrite { get; }
        public SampleSequenceChangedMessage(List<SampleItemModel> value, bool isOverwrite = true) : base(value) 
        {
            IsOverwrite = isOverwrite;
        }
    }

    public class MeasurementDataUpdatedMessage : ValueChangedMessage<List<SampleItemModel>>
    {
        public MeasurementDataUpdatedMessage(List<SampleItemModel> value) : base(value) { }
    }

    public class SampleTypeChangedMessage : ValueChangedMessage<SampleItemModel>
    {
        public SampleTypeChangedMessage(SampleItemModel value) : base(value) { }
    }

    // 新增：通知 View 层重绘动态列的消息
    public class RebuildColumnsMessage : ValueChangedMessage<List<string>>
    {
        public RebuildColumnsMessage(List<string> value) : base(value) { }
    }

    // 新增：全局页面导航消息
    public class NavigateMessage : ValueChangedMessage<string>
    {
        public NavigateMessage(string value) : base(value) { }
    }

    // 新增：请求查询当前是否已存在序列或数据的消息
    public class SequenceStatusRequestMessage : RequestMessage<bool>
    {
    }

    // 新增：请求全局清空所有序列和测量数据的消息
    public class ClearSequenceRequestMessage
    {
    }
}