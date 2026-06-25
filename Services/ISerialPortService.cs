/*
 * 文件名: ISerialPortService.cs
 * 描述: 定义串口通讯服务的标准行为契约。
 * 涵盖了设备生命周期管理（打开/关闭/自动连接）、可用端口探测、以及基于优先级的异步数据发送队列机制。
 * 维护指南: 扩展 CommandPriority 枚举时，需确保实现类（如 SerialPortService）的消费调度算法能够正确处理新增的优先级。
 */

namespace GD_ControlCenter_WPF.Services
{
    /// <summary>
    /// 指令执行优先级类型。
    /// 用于区分即时硬件控制指令与后台轮询查询指令，确保操作响应性。
    /// </summary>
    public enum CommandPriority
    {
        /// <summary>
        /// 高优先级：用于即时硬件操控指令（如设备控制等）。
        /// </summary>
        High = 0,

        /// <summary>
        /// 低优先级：用于后台状态轮询指令（如电压查询、电池信息查询等）。
        /// </summary>
        Low = 1
    }

    /// <summary>
    /// 串口服务接口：定义硬件通讯层的标准操作接口。
    /// </summary>
    public interface ISerialPortService
    {
        /// <summary>
        /// 当串口连接状态发生变更（开启或断开）时触发的通知事件。
        /// </summary>
        event Action<bool>? ConnectionStatusChanged;

        /// <summary>
        /// 获取当前串口的开启状态。
        /// </summary>
        bool IsOpen { get; }

        /// <summary>
        /// 获取当前已连接或正在尝试连接的串口名称（如 "COM2"）。
        /// </summary>
        string? CurrentPortName { get; }

        /// <summary>
        /// 扫描并获取当前系统内所有可用的物理/虚拟串口列表。
        /// </summary>
        /// <returns>串口名称字符串数组。</returns>
        string[] GetAvailablePorts();

        /// <summary>
        /// 使用指定的波特率尝试打开目标串口。
        /// </summary>
        /// <param name="portName">串口名（如 "COM2"）。</param>
        /// <param name="baudRate">波特率（如 9600）。</param>
        /// <returns>操作是否成功。</returns>
        bool Open(string portName, int baudRate);

        /// <summary>
        /// 关闭当前活动的串口，并清理相关的收发缓冲区与 IO 句柄。
        /// </summary>
        void Close();

        /// <summary>
        /// 向硬件发送十六进制原始数据帧。
        /// 指令会被推入内部并发队列，并根据优先级由后台线程统一调度发送。
        /// </summary>
        /// <param name="data">待发送的字节数组。</param>
        /// <param name="priority">指令优先级，默认为高优先级。</param>
        void Send(byte[] data, CommandPriority priority = CommandPriority.High);

        /// <summary>
        /// 根据本地保存的配置尝试自动连接上次使用的串口。
        /// </summary>
        void AutoConnect();
    }
}