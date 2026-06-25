using GD_ControlCenter_WPF.Helpers;
using GD_ControlCenter_WPF.Models.Protocols;

/*
 * 文件名: ControlCommandFactory.cs
 * 描述: 控制指令工厂类，负责将应用层的业务逻辑参数（如电压、转速、行程等）翻译为符合硬件通讯协议的 13 字节标准指令帧。
 * 本类集成了统一的封包逻辑，自动处理帧头、设备地址、功能码、CRC 校验及帧尾填充。
 * 维护指南: 填充数据段（索引 6-9）时需严格参照各硬件模块定义的私有子协议（ControlProtocol.cs）；CRC 校验范围固定为帧的前 10 个字节。
 */

namespace GD_ControlCenter_WPF.Services.Commands
{
    /// <summary>
    /// 控制指令工厂：负责将抽象的业务参数转化为 13 字节强类型硬件指令。
    /// </summary>
    public static class ControlCommandFactory
    {
        #region 核心包装逻辑

        /// <summary>
        /// 通用帧包装方法。
        /// 按照协议标准构建包含校验位在内的完整报文。
        /// </summary>
        /// <param name="addr">目标硬件设备地址。</param>
        /// <param name="type">通信链路流向类型。</param>
        /// <param name="func">业务功能码。</param>
        /// <param name="dataSegment">4 字节业务载荷数据段。</param>
        /// <returns>构建完成的 13 字节标准指令包。</returns>
        private static byte[] PackFrame(DeviceAddr addr, CommandType type, FunctionCode func, byte[] dataSegment)
        {
            byte[] frame = new byte[ControlProtocol.CommandTotalLength];

            // 填充协议基础头信息 (索引 0-5)
            frame[0] = ControlProtocol.FrameHeader; // 帧起始符 0x68
            frame[1] = (byte)addr;                  // 目标地址
            frame[2] = (byte)type;                  // 链路类型
            frame[3] = (byte)func;                  // 功能识别码
            frame[4] = ControlProtocol.DataLength;  // 数据区长度标识 0x0D
            frame[5] = 0x00;                        // 协议预留位

            // 填充 4 字节业务数据载荷 (索引 6-9)
            if (dataSegment != null && dataSegment.Length == 4)
            {
                Array.Copy(dataSegment, 0, frame, 6, 4);
            }

            // 计算并填充 CRC 校验码 (计算范围：索引 0 至 9，共 10 字节)
            byte[] crc = CrcHelper.Compute(frame, 10);
            frame[10] = crc[0]; // CRC 校验高位
            frame[11] = crc[1]; // CRC 校验低位

            // 填充帧结束符
            frame[12] = ControlProtocol.FrameFooter; // 帧尾 0x16

            return frame;
        }

        #endregion

        #region 具体业务指令生成

        /// <summary>
        /// 生成高压电源控制指令。
        /// </summary>
        /// <param name="voltage">目标电压值 (V)。</param>
        /// <param name="current">限制电流值 (mA)。</param>
        /// <returns>13 字节电源控制报文。</returns>
        public static byte[] CreateHighVoltage(int voltage, int current)
        {
            byte[] data = new byte[4];
            data[0] = (byte)(voltage % 256); // 电压低 8 位
            data[1] = (byte)(voltage / 256); // 电压高 8 位
            data[2] = (byte)(current % 256); // 电流低 8 位
            data[3] = (byte)(current / 256); // 电流高 8 位

            return PackFrame(DeviceAddr.Controller, CommandType.PC_To_Controller, FunctionCode.HighVoltage, data);
        }

        /// <summary>
        /// 生成蠕动泵控制指令。
        /// </summary>
        /// <param name="speed">设定转速 (0-100 RPM)。</param>
        /// <param name="isClockwise">旋转方向：True 为顺时针（前向），False 为逆时针（后向）。</param> 
        /// <param name="isStart">启停控制：True 为启动运行，False 为强制停止。</param>
        /// <returns>13 字节蠕动泵控制报文。</returns>
        public static byte[] CreatePeristalticPump(short speed, bool isClockwise, bool isStart)
        {
            // 物理安全限速
            short safeSpeed = Math.Clamp(speed, (short)0, (short)100);

            byte[] data = new byte[4];
            data[0] = (byte)(isStart ? 1 : 0);      // 运行状态位
            data[1] = (byte)(isClockwise ? 1 : 0);  // 方向位

            // 转速换算（协议要求放大 100 倍传递）
            short scaledSpeed = (short)(safeSpeed * 100);
            data[2] = (byte)(scaledSpeed / 256);    // 转速高位
            data[3] = (byte)(scaledSpeed % 256);    // 转速低位

            return PackFrame(DeviceAddr.Controller, CommandType.PC_To_Controller, FunctionCode.PeristalticSetting, data);
        }

        /// <summary>
        /// 生成转向阀（双通道切换阀）控制指令。
        /// </summary>
        /// <param name="isChannel2">通道选择：True 切换至通道 2，False 切换至通道 1。</param>
        /// <returns>13 字节转向阀控制报文。</returns>
        public static byte[] CreateSteeringValve(bool isChannel2)
        {
            byte channel = (byte)(isChannel2 ? 0x02 : 0x01);
            byte[] data = new byte[4] { channel, 0x00, 0x00, 0x00 };

            return PackFrame(DeviceAddr.Controller, CommandType.PC_To_Controller, FunctionCode.SteeringValve, data);
        }

        /// <summary>
        /// 生成注射泵行程控制指令。
        /// </summary>
        /// <param name="isOutput">运行方向：True 为推（输出），False 为拉（吸取）。</param>
        /// <param name="distance">移动脉冲步数，安全范围 0-3000。</param>
        /// <returns>13 字节注射泵控制报文。</returns>
        public static byte[] CreateSyringePump(bool isOutput, short distance)
        {
            short safeDistance = Math.Clamp(distance, (short)0, (short)3000);

            byte[] data = new byte[4];
            data[0] = (byte)(isOutput ? 1 : 0);     // 方向位
            data[1] = (byte)(safeDistance / 256);   // 距离高 8 位
            data[2] = (byte)(safeDistance % 256);   // 距离低 8 位
            data[3] = 0x00;

            return PackFrame(DeviceAddr.Controller, CommandType.PC_To_Controller, FunctionCode.SyringeSetting, data);
        }

        /// <summary>
        /// 生成三维平台单轴移动指令。
        /// </summary>
        /// <param name="dimension">轴编号：1=X轴, 2=Y轴, 3=Z轴。</param>
        /// <param name="isPositive">移动方向：True 为正向(1)，False 为反向(0)。</param>
        /// <param name="step">移动的脉冲步长。</param>
        /// <returns>13 字节三维平台移动报文。</returns>
        public static byte[] CreatePlatformMove(int dimension, bool isPositive, int step)
        {
            byte[] data = new byte[4];
            data[0] = (byte)dimension;              // 轴标识
            data[1] = (byte)(isPositive ? 0x01 : 0x00); // 方向标识
            data[2] = (byte)(step % 256);           // 步长低字节
            data[3] = (byte)(step / 256);           // 步长高字节

            return PackFrame(DeviceAddr.Controller, CommandType.PC_To_Controller, FunctionCode.Platform3D, data);
        }

        /// <summary>
        /// 生成多通道选择阀控制指令。
        /// </summary>
        /// <param name="channel">目标通道索引（1-10）。</param>
        /// <returns>13 字节多通道阀控制报文。</returns>
        public static byte[] CreateMultiChannelValve(int channel)
        {
            int safeChannel = Math.Clamp(channel, 1, 10);
            byte[] data = new byte[4] { (byte)safeChannel, 0x00, 0x00, 0x00 };

            return PackFrame(DeviceAddr.Controller, CommandType.PC_To_Controller, FunctionCode.MultiChannelValve, data);
        }

        /// <summary>
        /// 生成点火序列控制指令。
        /// 触发该指令后，下位机将执行特定的点火逻辑流。
        /// </summary>
        /// <param name="delayMs">点火前的流体预充延时（毫秒），安全范围 1000-5000ms。</param>
        /// <returns>13 字节点火控制报文。</returns>
        public static byte[] CreateIgnition(int delayMs)
        {
            int safeDelay = Math.Clamp(delayMs, 1000, 5000);

            byte[] data = new byte[4];
            data[0] = 0x00;
            data[1] = 0x00;
            data[2] = (byte)(safeDelay % 256); // 延时低字节
            data[3] = (byte)(safeDelay / 256); // 延时高字节

            return PackFrame(DeviceAddr.Controller, CommandType.PC_To_Controller, FunctionCode.Ignition, data);
        }

        /// <summary>
        /// 生成高压电源实时状态查询指令。
        /// 用于获取下位机回传的实际输出电压与电流。
        /// </summary>
        /// <returns>13 字节查询报文。</returns>
        public static byte[] CreateHighVoltageQuery()
        {
            byte[] data = new byte[4] { 0x00, 0x00, 0x00, 0x00 };

            return PackFrame(DeviceAddr.Controller, CommandType.PC_To_Controller, FunctionCode.ReturnPowerInfo, data);
        }

        /// <summary>
        /// 生成系统电池电量与状态查询指令。
        /// </summary>
        /// <returns>13 字节查询报文。</returns>
        public static byte[] CreateBatteryQuery()
        {
            byte[] data = new byte[4] { 0x00, 0x00, 0x00, 0x00 };

            return PackFrame(DeviceAddr.Controller, CommandType.PC_To_Controller, FunctionCode.Battery, data);
        }

        #endregion
    }
}