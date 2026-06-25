using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Models.Platform3D;
using GD_ControlCenter_WPF.Models.Spectrometer;
using GD_ControlCenter_WPF.Services.Spectrometer;
using GD_ControlCenter_WPF.Services.Spectrometer.Logic;
using System.Diagnostics;

/*
 * 文件名: PlatformCalibrationService.cs
 * 描述: 平台自动归零及三轴光路自动校准逻辑。
 * 核心逻辑：通过驱动三维平台执行全程扫描运动，实时监控特征峰强度变化，并利用“时间-行程”比例算法精准回退至信号最强的物理位置。
 * 维护指南: 归零操作单轴超时固定为 30s；校准前必须确保光谱仪已处于持续测量状态；校准过程中若检测到像素饱和（过曝）将立即停止。
 */

namespace GD_ControlCenter_WPF.Services.Platform3D
{
    /// <summary>
    /// 平台校准服务：实现三轴自动归零与基于光谱反馈的光路自动寻优。
    /// </summary>
    public class PlatformCalibrationService : IDisposable
    {
        #region 1. 依赖服务与状态字段

        /// <summary>
        /// 三维平台基础控制服务。
        /// </summary>
        private readonly IPlatform3DService _platformService;

        /// <summary>
        /// 全局特征峰追踪服务，用于在校准时提供实时的峰值强度反馈。
        /// </summary>
        private readonly PeakTrackingService _peakTrackingService;

        /// <summary>
        /// 扫描过程计时器，用于计算最高点出现的时间节点。
        /// </summary>
        private readonly Stopwatch _sweepStopwatch = new();

        /// <summary>
        /// 标识当前是否正处于自动寻优扫描流程中。
        /// </summary>
        private bool _isScanning = false;

        /// <summary>
        /// 寻优过程中捕获到的最大光强数值。
        /// </summary>
        private double _maxIntensityFound = -1;

        /// <summary>
        /// 捕获到最大光强时相对于扫描起始点的毫秒数。
        /// </summary>
        private long _maxIntensityTimeMs = 0;

        /// <summary>
        /// 扫描任务的取消令牌源，用于中断移动。
        /// </summary>
        private CancellationTokenSource? _scanCts;

        /// <summary>
        /// 记录导致任务中止的具体业务原因（如：检测到饱和）。
        /// </summary>
        private string _abortReason = string.Empty;

        /// <summary>
        /// 当前寻优任务针对的目标波长中心点。
        /// </summary>
        private double _targetWl;

        #endregion

        #region 2. 初始化与消息挂载

        /// <summary>
        /// 构造函数：注入平台与光谱服务，并注册全局光谱数据监听。
        /// </summary>
        public PlatformCalibrationService(IPlatform3DService platformService, PeakTrackingService peakTrackingService)
        {
            _platformService = platformService ?? throw new ArgumentNullException(nameof(platformService));
            _peakTrackingService = peakTrackingService ?? throw new ArgumentNullException(nameof(peakTrackingService));

            // 监听底层光谱数据，用于在扫描过程中实时分析强度
            WeakReferenceMessenger.Default.Register<SpectralDataMessage>(this, (r, m) =>
            {
                OnSpectralDataReceived(m.Value);
            });
        }

        #endregion

        #region 3. 自动归零逻辑

        /// <summary>
        /// 执行三轴自动归零序列（Z -> Y -> X）。
        /// 逻辑：强制反向移动至限位触发，利用 IPlatform3DService 的物理限位拦截机制完成零点标定。
        /// </summary>
        public async Task AutoHomeAllAxesAsync(CancellationToken parentCts = default)
        {
            var axesToHome = new List<AxisType> { AxisType.X, AxisType.Y, AxisType.Z };

            try
            {
                foreach (var axis in axesToHome)
                {
                    // 若已在零点位置则跳过，防止电机堵转
                    if (_platformService.Status.IsAtMin[axis]) continue;

                    // 设置 30 秒单轴强制超时保护
                    using var axisTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(parentCts, axisTimeoutCts.Token);

                    // 下发大幅度负向步进指令，由硬件限位信号执行物理切断
                    int homeDistance = PlatformLimits.MaxValidStep + 500;
                    await _platformService.MoveAxisAsync(axis, homeDistance, false, linkedCts.Token);

                    // 轴间切换缓冲延时
                    await Task.Delay(500, parentCts);
                }
            }
            catch (OperationCanceledException)
            {
                _platformService.StopAll();
                throw;
            }
        }

        #endregion

        #region 4. 自动寻优校准

        /// <summary>
        /// 启动指定轴的光路自动寻优。
        /// 算法原理：匀速扫描全程 -> 记录峰值时间比率 -> 物理回退计算。
        /// </summary>
        /// <param name="axis">待校准轴。</param>
        /// <param name="targetWl">目标追踪波长。</param>
        /// <param name="tolerance">搜索容差（本实现中由 PeakTrackingService 统一管理）。</param>
        public async Task StartAxisCalibrationAsync(AxisType axis, double targetWl, double tolerance, CancellationToken ct = default)
        {
            // 前置安全检查
            if (!_platformService.Status.IsAtMin[axis])
                throw new InvalidOperationException($"{axis} 轴未处于零点位，请先执行复位归零。");

            var activeSpectrometer = SpectrometerManager.Instance.Devices.FirstOrDefault();
            if (activeSpectrometer == null || !activeSpectrometer.IsMeasuring)
            {
                throw new InvalidOperationException("光谱反馈缺失：光谱仪未启动持续测量。");
            }

            // 初始化寻优上下文
            _targetWl = targetWl;
            _maxIntensityFound = -1;
            _maxIntensityTimeMs = 0;
            _isScanning = true;
            _abortReason = string.Empty;
            _scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            long totalSweepTimeMs = 0;
            int maxDistance = GetMaxStep(axis);

            try
            {
                // 开始执行全程匀速扫描
                _sweepStopwatch.Restart();

                // 下发全程步进。注意：移动过程中 OnSpectralDataReceived 会不断刷新最高点记录
                await _platformService.MoveAxisAsync(axis, maxDistance + 500, true, _scanCts.Token);

                totalSweepTimeMs = _sweepStopwatch.ElapsedMilliseconds;
            }
            catch (OperationCanceledException)
            {
                // 若由于熔断（饱和）退出，抛出具体的错误原因
                if (!string.IsNullOrEmpty(_abortReason))
                    throw new InvalidOperationException(_abortReason);
                throw;
            }
            finally
            {
                _isScanning = false;
                _sweepStopwatch.Stop();
                _scanCts?.Dispose();
                _scanCts = null;
            }

            // 执行“倒车”定位逻辑
            if (totalSweepTimeMs > 0)
            {
                if (_maxIntensityTimeMs == 0)
                    throw new InvalidOperationException($"校准中止：扫描全程未发现有效信号特征。");

                // 计算强度最高点的时间比例因子
                double ratio = (double)_maxIntensityTimeMs / totalSweepTimeMs;
                ratio = Math.Clamp(ratio, 0.0, 1.0);

                // 计算对应的物理脉冲位置
                int bestTargetStep = (int)Math.Round(maxDistance * ratio);
                int currentPos = _platformService.CurrentPosition[axis];
                int returnDistance = currentPos - bestTargetStep;

                // 若计算出的位置在后方，则执行回退动作
                if (returnDistance > 0)
                {
                    await Task.Delay(1000, ct); // 停顿缓冲，消除机械惯性
                    await _platformService.MoveAxisAsync(axis, returnDistance, false, ct);
                }
            }
        }

        #endregion

        #region 5. 数据反馈与熔断逻辑

        /// <summary>
        /// 响应光谱数据更新。在扫描期间负责寻找峰值点并执行过曝熔断。
        /// </summary>
        private void OnSpectralDataReceived(SpectralData data)
        {
            if (!_isScanning) return;

            // 若信号饱和，立即停止扫描以防止校准数据畸变
            if (SpectrometerLogic.CheckSaturation(data))
            {
                _isScanning = false;
                _abortReason = "光谱信号饱和（过曝）。请调低积分时间后重试。";
                _scanCts?.Cancel(); // 瞬间切断平台移动任务
                return;
            }

            // 从寻峰服务中匹配当前目标波长的实时强度
            var matchedPeak = _peakTrackingService.TrackedPeaks
                .FirstOrDefault(p => Math.Abs(p.BaseWavelength - _targetWl) < 0.01);

            if (matchedPeak == null) return;

            double currentIntensity = matchedPeak.CurrentIntensity;

            // 记录扫描轨迹中的光强最高峰及其时间戳
            if (currentIntensity > _maxIntensityFound)
            {
                _maxIntensityFound = currentIntensity;
                _maxIntensityTimeMs = _sweepStopwatch.ElapsedMilliseconds;
            }
        }

        /// <summary>
        /// 辅助方法：获取物理轴的最大行程限位值。
        /// </summary>
        private int GetMaxStep(AxisType axis) => axis switch
        {
            AxisType.X => PlatformLimits.MaxStepX,
            AxisType.Y => PlatformLimits.MaxStepY,
            AxisType.Z => PlatformLimits.MaxStepZ,
            _ => 0
        };

        #endregion

        /// <summary>
        /// 释放资源：注销消息总线。
        /// </summary>
        public void Dispose()
        {
            WeakReferenceMessenger.Default.UnregisterAll(this);
            _scanCts?.Dispose();
        }
    }
}