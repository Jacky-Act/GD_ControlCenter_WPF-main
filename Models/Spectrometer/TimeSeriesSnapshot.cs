/*
 * 文件名: TimeSeriesSnapshot.cs
 * 描述: 定义用于时序图显示的极简数据快照结构。
 * 本模型专为高频数据采样场景设计，通过剥离冗余的波长信息，降低内存占用，支持在内存中积攒海量数据点后进行高效的批量落盘。
 * 维护指南: Intensities 数组的元素索引必须与业务逻辑中“特征峰追踪列表”的顺序严格一致，以确保数据在 UI 渲染与数据导出时的物理含义正确。
 */

namespace GD_ControlCenter_WPF.Models.Spectrometer
{
    /// <summary>
    /// 时序图单帧数据快照。
    /// 职责：仅保留核心的时间戳与抽样后的强度集合，作为高频实时监控与历史轨迹存储的轻量级数据结构。
    /// </summary>
    public class TimeSeriesSnapshot
    {
        /// <summary>
        /// 数据采集生成的精确时刻。
        /// 用于在时序图表的 X 轴上进行线性定位。
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// 该时刻对应各个追踪波长点的光强值数组（单位：Counts）。
        /// 本数组不包含波长信息，仅按顺序存储特定特征峰的强度值。
        /// </summary>
        public double[] Intensities { get; set; } = Array.Empty<double>();

        /// <summary>
        /// 初始化 TimeSeriesSnapshot 的默认实例。
        /// </summary>
        public TimeSeriesSnapshot() { }

        /// <summary>
        /// 使用指定的时间戳与强度数组构造快照实例。
        /// </summary>
        /// <param name="timestamp">采样发生的物理时间</param>
        /// <param name="intensities">特征点强度数据集合</param>
        public TimeSeriesSnapshot(DateTime timestamp, double[] intensities)
        {
            Timestamp = timestamp;
            Intensities = intensities;
        }
    }
}