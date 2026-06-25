using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Models.Spectrometer;
using GD_ControlCenter_WPF.Services.Spectrometer.Logic;
using System.Collections.ObjectModel;

/*
 * 文件名: PeakTrackingService.cs
 * 描述: 后台特征峰自动追踪服务类。负责监听全局光谱数据消息，并实时计算所有标记特征峰在当前帧中的物理偏移量。
 * 本服务作为“无头”业务组件运行，通过驱动底层寻峰算法更新 TrackedPeak 模型；寻峰点数据结构从这里拿。
 * 维护指南: 追踪逻辑依赖于 SpectralDataMessage 的高频触发。
 */

namespace GD_ControlCenter_WPF.Services.Spectrometer
{
    /// <summary>
    /// 特征峰追踪服务：实现光谱特征点的动态捕捉、管理及实时坐标更新。
    /// </summary>
    public class PeakTrackingService
    {
        #region 1. 状态与数据集合

        /// <summary>
        /// 当前活跃的特征峰追踪集合。
        /// 采用 ObservableCollection 以支持 UI 列表或图表标注线的双向数据绑定。
        /// </summary>
        public ObservableCollection<TrackedPeak> TrackedPeaks { get; } = new ObservableCollection<TrackedPeak>();

        #endregion

        #region 2. 初始化与消息订阅

        /// <summary>
        /// 初始化追踪服务并注册全局消息监听。
        /// </summary>
        public PeakTrackingService()
        {
            // 订阅全局光谱数据消息：每当硬件产出新帧，立即驱动所有追踪点执行位移计算
            WeakReferenceMessenger.Default.Register<SpectralDataMessage>(this, (r, m) =>
            {
                UpdatePeaksWithNewData(m.Value);
            });
        }

        #endregion

        #region 3. 追踪点管理 (CRUD)

        /// <summary>
        /// 向追踪列表中添加一个新的特征峰。
        /// </summary>
        /// <param name="targetWavelength">用户在 UI 上选定的基准中心波长（单位：nm）。</param>
        public void AddPeak(double targetWavelength)
        {
            // 防重复校验：若 0.5nm 范围内已存在追踪点，则视为无效操作，防止 UI 元素重叠
            if (TrackedPeaks.Any(p => Math.Abs(p.BaseWavelength - targetWavelength) < 0.5))
            {
                return;
            }

            // 实例化并加入集合，触发 UI 刷新
            TrackedPeaks.Add(new TrackedPeak(targetWavelength));
        }

        /// <summary>
        /// 移除指定坐标附近的追踪峰。
        /// </summary>
        /// <param name="clickWavelength">点击处的波长位置。</param>
        /// <param name="clickTolerance">判定点击生效的搜索半径（默认 ±5.0nm）。</param>
        public void RemovePeakNear(double clickWavelength, double clickTolerance = 5.0)
        {
            if (TrackedPeaks.Count == 0) return;

            // 寻找在宽容度范围内距离点击点最近的特征峰
            var closestPeak = TrackedPeaks
                .Select(p => new { Peak = p, Distance = Math.Abs(p.CurrentWavelength - clickWavelength) })
                .OrderBy(x => x.Distance)
                .FirstOrDefault();

            if (closestPeak != null && closestPeak.Distance <= clickTolerance)
            {
                TrackedPeaks.Remove(closestPeak.Peak);
            }
        }

        /// <summary>
        /// 清空所有已标记的追踪点。
        /// </summary>
        public void ClearAll()
        {
            TrackedPeaks.Clear();
        }

        #endregion

        #region 4. 核心刷新引擎

        /// <summary>
        /// 核心计算逻辑：利用最新光谱帧驱动数学算法，锁定所有追踪峰的实时物理位置。
        /// </summary>
        /// <param name="currentData">刚采集到的原始光谱数据包。</param>
        public void UpdatePeaksWithNewData(SpectralData currentData)
        {
            if (currentData == null || currentData.Wavelengths == null || TrackedPeaks.Count == 0)
                return;

            foreach (var peak in TrackedPeaks)
            {
                // 调用算法层执行局部寻峰：在基准值附近寻找真实最高点 (X)
                double realX = SpectrometerLogic.GetActualPeakWavelength(currentData, peak.BaseWavelength, peak.ToleranceWindow);

                // 映射光强值：获取该物理位置对应的实时光强计数 (Y)
                double realY = SpectrometerLogic.GetIntensityAtWavelength(currentData, realX);

                // 更新模型属性：TrackedPeak 内部会发出 PropertyChanged 通知，从而实时更新 UI 线条位置
                peak.CurrentWavelength = realX;
                peak.CurrentIntensity = realY;
            }
        }

        #endregion
    }
}