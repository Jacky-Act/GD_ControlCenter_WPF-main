using CommunityToolkit.Mvvm.Messaging.Messages;

/*
 * 文件名: DeviceDataMessages.cs
 * 描述: 定义与底层硬件原始数据帧关联的消息模型。
 * 用于在 Service 层解析出原始字节数组后，通过强类型消息将其分发至对应的数据处理模块或 ViewModel。
 */

namespace GD_ControlCenter_WPF.Models.Messages
{
    /// <summary>
    /// 电池数据消息。
    /// 承载来自控制板的 13 字节标准帧，包含电池状态反馈数据。
    /// </summary>
    public class BatteryFrameMessage : ValueChangedMessage<byte[]>
    {
        public BatteryFrameMessage(byte[] value) : base(value) { }
    }

    /// <summary>
    /// 三维平台位置消息。
    /// 承载 8 字节的位置反馈帧，用于同步 X/Y/Z 轴的起始和终点位置。
    /// </summary>
    public class Platform3DMessage : ValueChangedMessage<byte[]>
    {
        public Platform3DMessage(byte[] value) : base(value) { }
    }

    /// <summary>
    /// 控制板标准响应消息。
    /// 承载 13 字节的标准指令回执，用于确认下位机向上位机的回传指令。
    /// </summary>
    public class ControlResponseMessage : ValueChangedMessage<byte[]>
    {
        public ControlResponseMessage(byte[] value) : base(value) { }
    }
}
