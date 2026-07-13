/*
 * 文件名: PlatformModel.cs
 * 描述: 定义三维运动平台的物理模型、实时运行状态以及物理限位常量。
 * 作为纯数据模型 (POCO)，它不依赖 MVVM 框架，用于在 Service 层和 ViewModel 层之间传递平台的坐标、状态及边界限制数据。
 * 维护指南: 修改 PlatformLimits 中的常量需参考硬件实际物理行程；AxisType 的枚举值必须与底层通信协议严格对应。
 */

using System.Collections.Generic;

namespace GD_ControlCenter_WPF.Models.Platform3D
{
    /// <summary>
    /// 三维平台轴类型枚举
    /// 直接映射硬件协议：X=1, Y=2, Z=3
    /// </summary>
    public enum AxisType : byte
    {
        X = 0x01,
        Y = 0x02,
        Z = 0x03
    }

    /// <summary>
    /// 三维平台实时位置模型 (纯数据容器)
    /// 用于记录 X、Y、Z 三轴当前的脉冲步数。
    /// </summary>
    public class PlatformPosition
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }

        /// <summary>
        /// 快捷获取/设置指定轴的位置值
        /// </summary>
        public int this[AxisType axis]
        {
            get => axis switch
            {
                AxisType.X => X,
                AxisType.Y => Y,
                AxisType.Z => Z,
                _ => 0
            };
            set
            {
                switch (axis)
                {
                    case AxisType.X: X = value; break;
                    case AxisType.Y: Y = value; break;
                    case AxisType.Z: Z = value; break;
                }
            }
        }
    }

    /// <summary>
    /// 三维平台运行状态模型 (纯数据容器)
    /// 集中管理平台的运动状态、复位状态以及各轴的物理边界触发标志。
    /// </summary>
    public class PlatformStatus
    {
        public bool IsMoving { get; set; }
        public bool IsHomed { get; set; }

        /// <summary>
        /// 标记 Z 轴是否已收到过物理零点信号
        /// </summary>
        public bool HasReceivedZZero { get; set; }

        /// <summary>
        /// 独立布尔属性：代替字典消除哈希开销
        /// </summary>
        public bool IsXAtMin { get; set; } = false;
        public bool IsYAtMin { get; set; } = false;
        public bool IsZAtMin { get; set; } = false;
        public bool IsXAtMax { get; set; } = false;
        public bool IsYAtMax { get; set; } = false;
        public bool IsZAtMax { get; set; } = false;

        /// <summary>
        /// 辅助读取 Min 限位
        /// </summary>
        public bool GetIsAtMin(AxisType axis) => axis switch
        {
            AxisType.X => IsXAtMin,
            AxisType.Y => IsYAtMin,
            AxisType.Z => IsZAtMin,
            _ => false
        };

        /// <summary>
        /// 辅助设置 Min 限位
        /// </summary>
        public void SetIsAtMin(AxisType axis, bool value)
        {
            switch (axis)
            {
                case AxisType.X: IsXAtMin = value; break;
                case AxisType.Y: IsYAtMin = value; break;
                case AxisType.Z: IsZAtMin = value; break;
            }
        }

        /// <summary>
        /// 辅助读取 Max 限位
        /// </summary>
        public bool GetIsAtMax(AxisType axis) => axis switch
        {
            AxisType.X => IsXAtMax,
            AxisType.Y => IsYAtMax,
            AxisType.Z => IsZAtMax,
            _ => false
        };

        /// <summary>
        /// 辅助设置 Max 限位
        /// </summary>
        public void SetIsAtMax(AxisType axis, bool value)
        {
            switch (axis)
            {
                case AxisType.X: IsXAtMax = value; break;
                case AxisType.Y: IsYAtMax = value; break;
                case AxisType.Z: IsZAtMax = value; break;
            }
        }
    }

}