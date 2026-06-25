using CommunityToolkit.Mvvm.Messaging.Messages;
using static GD_ControlCenter_WPF.Models.AppConfig; // 重要：引用枚举

namespace GD_ControlCenter_WPF.Models.Messages
{
    // 定义切换模式的消息
    public class MeasurementModeChangedMessage : ValueChangedMessage<MeasurementMode>
    {
        public MeasurementModeChangedMessage(MeasurementMode value) : base(value) { }
    }
}