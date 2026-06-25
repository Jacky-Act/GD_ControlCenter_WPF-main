using CommunityToolkit.Mvvm.Messaging.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

/*
 * 文件名: FlowInjectionMessages.cs (或者追加到您现有的 Messages 文件中)
 * 描述: 定义流动注射（空间分布扫描）实验的峰值计算结果消息模型。
 * 承载算法层计算得出的元素、扫描次序、峰起始/结束时间、背景与峰值强度等信息，
 * 用于通过 MVVM 消息机制跨层分发，最终在 UI 界面的 DataGrid 中展示。
 * 项目: GD_ControlCenter_WPF
 */

namespace GD_ControlCenter_WPF.Models.Messages
{
    /// <summary>
    /// 流动注射/空间扫描 单次峰值分析结果数据实体 (DTO)。
    /// 承载了 UI 界面中上下两个 DataGrid 所需展示的核心字段。
    /// </summary>
    public class FlowInjectionResultData
    {
        /// <summary>
        /// 目标元素种类 (对应界面：元素)
        /// </summary>
        public string ElementName { get; set; } = string.Empty;

        /// <summary>
        /// 当前扫描次序/采样序号 (对应界面：扫描次序)
        /// </summary>
        public int ScanIndex { get; set; }

        /// <summary>
        /// 测算出的背景强度 (对应界面：背景强度)
        /// </summary>
        public double BackgroundIntensity { get; set; }

        /// <summary>
        /// 提取出的最高峰值强度 (对应界面：峰值强度)
        /// </summary>
        public double PeakIntensity { get; set; }

        /// <summary>
        /// 峰起始时间点，单位秒 (对应界面：峰起始(s))
        /// </summary>
        public double StartTime { get; set; }

        /// <summary>
        /// 峰结束时间点，单位秒 (对应界面：峰结束(s))
        /// </summary>
        public double EndTime { get; set; }
    }

    /// <summary>
    /// 单次流动注射分析结果消息。
    /// 适用场景：在实验运行中，底层算法实时计算出某一个次序的峰值结果后发送。
    /// ViewModel 订阅后，将其解析并添加到 UI 绑定的集合中。
    /// </summary>
    public class FlowInjectionResultMessage : ValueChangedMessage<FlowInjectionResultData>
    {
        public FlowInjectionResultMessage(FlowInjectionResultData value) : base(value)
        {
        }
    }

    /// <summary>
    /// 批量流动注射分析结果消息。
    /// 适用场景：当实验结束统一生成结果，或者从数据库中读取历史记录恢复 UI 时发送。
    /// 传递包含多个记录的列表集合。
    /// </summary>
    public class FlowInjectionBatchResultMessage : ValueChangedMessage<List<FlowInjectionResultData>>
    {
        public FlowInjectionBatchResultMessage(List<FlowInjectionResultData> value) : base(value)
        {
        }
    }

    /// <summary>
    /// 用于封装实时绘图坐标点的数据结构
    /// </summary>
    /// <param name="ElementName">元素名称 (如 Pb, Cd)</param>
    /// <param name="Time">时间戳 (秒)</param>
    /// <param name="Intensity">当前强度</param>
    public record PlotPoint(string ElementName, double Time, double Intensity);

    /// <summary>
    /// 流动注射实时图表单点刷新消息。
    /// 适用场景：Logic 层每解析出一帧数据，就发给 View 层进行时序图绘制。
    /// </summary>
    public class FlowInjectionPlotMessage : ValueChangedMessage<PlotPoint>
    {
        public FlowInjectionPlotMessage(PlotPoint value) : base(value)
        {
        }
    }

    /// <summary>
    /// 切换图表显示元素的消息。
    /// 适用场景：用户在 ViewModel 改变了下拉框选项，通知 View 层清空并重绘目标元素的曲线。
    /// </summary>
    public class SwitchPlotElementMessage : ValueChangedMessage<string>
    {
        // 这里的 value 就是新选中的元素名称 (例如 "Pb")
        public SwitchPlotElementMessage(string value) : base(value)
        {
        }
    }
}