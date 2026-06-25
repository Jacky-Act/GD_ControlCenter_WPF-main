/*
 * 文件名: ControlProtocol.cs
 * 描述: 定义与下位机通信的标准协议帧结构、设备物理地址映射及业务功能码。
 * 作为底层通讯的协议契约，本文件规定了报文的封包与解包标准，涵盖泵组、电源、三维平台等所有硬件模块的控制指令。
 * 维护指南: 修改功能码或设备地址时，必须确保与硬件固件协议文档严格同步；帧头、帧尾及固定长度常量关乎通讯对齐，不可随意更动。
 */

namespace GD_ControlCenter_WPF.Models.Protocols
{
    /// <summary>
    /// 帧结构固定常量。
    /// 定义了通讯协议的边界标识与固定报文长度，确保数据解析的对齐。
    /// </summary>
    public static class ControlProtocol
    {
        public const byte FrameHeader = 0x68;  // 帧头
        public const byte FrameFooter = 0x16;  // 帧尾
        public const byte DataLength = 0x0D;   // 固定数据长度
        public const int CommandTotalLength = 13; // 完整帧长度
    }

    /// <summary>
    /// 设备通讯地址。
    /// 用于在多机通讯网络中标识指令的发送源与目标接收者。
    /// </summary>
    public enum DeviceAddr : byte
    {
        PC = 0x55,           // 上位机（工控机）
        Controller = 0xAA,   // 主控制板
        SyringePump = 0xBB,  // 注射泵
        PeristalticPump = 0xCC, // 蠕动泵
        Broadcast = 0x00     // 广播地址（所有设备接收）
    }

    /// <summary>
    /// 通信链路类型。
    /// 明确标识数据的流向，使中间节点（主控板）进行数据转发。
    public enum CommandType : byte
    {
        PC_To_Controller = 0x01,      // PC 与控制板通信
        Controller_To_Syringe = 0x02, // 控制板与注射泵通信
        Controller_To_Peristaltic = 0x03, // 控制板与蠕动泵通信
        Broadcast = 0x04              // 广播通信
    }

    /// <summary>
    /// 业务功能码。
    /// 每一个十六进制值对应硬件层的一个具体动作或查询请求。
    /// </summary>
    public enum FunctionCode : byte
    {
        Handshake = 0x11,          // 通信握手
        HighVoltage = 0x22,        // 高压电源设置
        PeristalticSetting = 0x33, // 蠕动泵设置
        SyringeSetting = 0x44,     // 注射泵设置
        ReturnPowerInfo = 0x66,    // 回传电流电压信息
        MultiChannelValve = 0x77,  // 多通道阀控制
        Ignition = 0x88,           // 点火控制
        SteeringValve = 0x99,      // 转向阀（双通道切换）
        Battery = 0xEE,            // 电池通信
        Platform3D = 0xFF          // 三维平台移动
    }
}
