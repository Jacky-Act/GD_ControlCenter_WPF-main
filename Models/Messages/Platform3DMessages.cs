/*
 * 文件名: Platform3DMessages.cs
 * 描述: 定义三维平台在业务逻辑层（Service -> ViewModel）流转的高阶强类型消息模型。
 * 用于在 Service 层解析底层信号后，将具体的业务状态或事件（如限位触发）分发至对应的 ViewModel 以更新 UI。
 */

namespace GD_ControlCenter_WPF.Models.Messages
{
    /// <summary>
    /// 三维平台触发物理边界限位消息。
    /// 发送方: Platform3DService (当解析到底层限位信号并强制停机时)
    /// 订阅方: Platform3DViewModel (用于刷新 UI 坐标并弹窗警告用户)
    /// </summary>
    public class PlatformBoundaryMessage
    {
        /// <summary>
        /// 触发限位的轴 (X / Y / Z)
        /// </summary>
        public Models.Platform3D.AxisType Axis { get; }

        /// <summary>
        /// 是否为零点限位 (True: 零点/Min, False: 最大值极限/Max)
        /// </summary>
        public bool IsMin { get; }

        public PlatformBoundaryMessage(Models.Platform3D.AxisType axis, bool isMin)
        {
            Axis = axis;
            IsMin = isMin;
        }
    }
}