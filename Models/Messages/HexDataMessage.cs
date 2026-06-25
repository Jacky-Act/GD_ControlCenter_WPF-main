using CommunityToolkit.Mvvm.Messaging.Messages;

/*
 * 文件名: HexDataMessage.cs
 * 描述: 通用的十六进制原始数据传输包。
 * 用于在不同模块间传递未经解析的原始串口字节流。
 */

namespace GD_ControlCenter_WPF.Models.Messages
{
    /// <summary>
    /// 通用十六进制字节数组消息。
    /// 提供一种标准化的方式来传递原始数据负载，不限制具体的业务含义。
    /// </summary>
    public class HexDataMessage : ValueChangedMessage<byte[]>
    {
        public HexDataMessage(byte[] value) : base(value)
        {
        }
    }
}
