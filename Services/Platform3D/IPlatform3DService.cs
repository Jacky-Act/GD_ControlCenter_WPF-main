using GD_ControlCenter_WPF.Models.Platform3D;

/*
 * 文件名: IPlatform3DService.cs
 * 描述: 定义三维运动平台的基础控制服务标准。
 * 涵盖了平台实时坐标追踪、运行状态监控、各轴独立异步移动以及紧急停止操作。
 * 维护指南: 任何实现此接口的类必须确保坐标系统与物理运动状态的强一致性。
 */

namespace GD_ControlCenter_WPF.Services.Platform3D
{
    /// <summary>
    /// 三维平台控制服务接口。
    /// </summary>
    public interface IPlatform3DService
    {
        /// <summary>
        /// 获取平台当前各轴的实时脉冲坐标。
        /// </summary>
        PlatformPosition CurrentPosition { get; }

        /// <summary>
        /// 获取平台当前运行状态（包含移动中标识、各轴限位触发状态等）。
        /// </summary>
        PlatformStatus Status { get; }

        /// <summary>
        /// 异步初始化平台：从持久化配置文件中加载历史坐标并同步初始限位状态。
        /// </summary>
        /// <returns>异步任务结果。</returns>
        Task InitializeAsync();

        /// <summary>
        /// 异步驱动指定轴移动。
        /// 内部包含软限位预检，并支持通过取消令牌进行紧急运动中断。
        /// </summary>
        /// <param name="axis">目标移动轴 (X/Y/Z)。</param>
        /// <param name="step">移动脉冲步长。</param>
        /// <param name="isPositive">移动方向：True 为正向，False 为反向。</param>
        /// <param name="ct">外部传入的取消令牌，用于中途取消移动任务。</param>
        /// <returns>移动任务是否成功执行并走完预定步长。</returns>
        Task<bool> MoveAxisAsync(AxisType axis, int step, bool isPositive, CancellationToken ct = default);

        /// <summary>
        /// 立即重置所有轴的运行状态标志位（强制停止逻辑状态）。
        /// </summary>
        void StopAll();
    }
}