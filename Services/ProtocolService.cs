using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Helpers;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Models.Protocols;

/*
 * 文件名: ProtocolService.cs
 * 描述: 协议解析服务类，负责接收串口原始字节流，执行拼包、校验及协议类型识别。
 * 本服务通过维护内部缓冲区，将碎片化的原始报文还原为完整的业务帧，并通过消息总线分发至对应的功能模块。
 * 维护指南: 新增报文协议时，需在 OnDataReceived 中注册对应的帧长识别逻辑与功能码分支；CRC 校验算法必须与下位机保持一致（取前 10 字节）。
 */

namespace GD_ControlCenter_WPF.Services
{
    /// <summary>
    /// 协议解析服务：实现底层字节流到高层业务消息的转换核心。
    /// </summary>
    public class ProtocolService
    {
        /// <summary>
        /// 内部字节缓冲区，用于存储尚未凑齐完整帧的碎片数据。
        /// </summary>
        private readonly List<byte> _buffer = new();

        /// <summary>
        /// 初始化协议解析服务，并订阅串口原始数据消息。
        /// </summary>
        public ProtocolService()
        {
            // 注册消息订阅：接收来自串口服务的原始 Hex 数据
            WeakReferenceMessenger.Default.Register<HexDataMessage>(this, (r, m) =>
            {
                // 锁定缓冲区，防止多线程环境下数据竞争导致的索引异常
                lock (_buffer)
                {
                    OnDataReceived(m.Value);
                }
            });
        }

        /// <summary>
        /// 处理新到达的字节数据，执行流式解析逻辑。
        /// </summary>
        /// <param name="data">从串口读入的新字节数组。</param>
        private void OnDataReceived(byte[] data)
        {
            // 将新数据追加至缓冲区末尾
            _buffer.AddRange(data);

            // 循环扫描缓冲区，直至无法解析出完整帧
            while (_buffer.Count > 0)
            {
                // 寻找帧起始符 (0x68)
                if (_buffer[0] == ControlProtocol.FrameHeader)
                {
                    // --- 分支 A: 三维平台反馈帧 (固定 8 字节) ---
                    if (_buffer.Count >= 4 && (FunctionCode)_buffer[3] == FunctionCode.Platform3D)
                    {
                        const int platformFrameLength = 8;

                        // 若缓冲区不足 8 字节，等待更多数据到达
                        if (_buffer.Count < platformFrameLength) break;

                        // 验证帧尾是否合法
                        if (_buffer[platformFrameLength - 1] == ControlProtocol.FrameFooter)
                        {
                            byte[] frame = _buffer.Take(platformFrameLength).ToArray();

                            // 解析成功，分发三维平台专用消息
                            WeakReferenceMessenger.Default.Send(new Platform3DMessage(frame));

                            // 从缓冲区移除已处理的报文
                            _buffer.RemoveRange(0, platformFrameLength);
                            continue;
                        }
                    }

                    // --- 分支 B: 标准控制响应帧 (固定 13 字节) ---
                    if (_buffer.Count >= ControlProtocol.CommandTotalLength)
                    {
                        // 验证帧尾 (索引 12)
                        if (_buffer[ControlProtocol.CommandTotalLength - 1] == ControlProtocol.FrameFooter)
                        {
                            byte[] frame = _buffer.Take(ControlProtocol.CommandTotalLength).ToArray();

                            // 计算并核对 CRC 校验码 (计算范围 0-9 字节)
                            byte[] crc = CrcHelper.Compute(frame, 10);

                            if (frame[10] == crc[0] && frame[11] == crc[1])
                            {
                                // 根据功能码进行二级路由分发
                                byte code = frame[3];

                                if (code == (byte)FunctionCode.Battery)
                                {
                                    // 转发至电池状态监控模块
                                    WeakReferenceMessenger.Default.Send(new BatteryFrameMessage(frame));
                                }
                                else
                                {
                                    // 转发至通用硬件响应处理模块（如高压电源、泵组回执）
                                    WeakReferenceMessenger.Default.Send(new ControlResponseMessage(frame));
                                }

                                _buffer.RemoveRange(0, ControlProtocol.CommandTotalLength);
                                continue;
                            }
                        }
                    }
                }

                // 干扰数据处理：若当前字节无法构成有效协议头，则逐字节剔除
                _buffer.RemoveAt(0);
            }
        }
    }
}