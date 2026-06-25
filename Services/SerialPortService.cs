using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using System.Collections.Concurrent;
using System.IO.Ports;

/*
 * 文件名: SerialPortService.cs
 * 描述: 标准串口服务的具体实现类，负责物理层交互、协议数据转发及发送任务调度。
 * 内部集成生产者-消费者队列模型，利用 SemaphoreSlim 实现高效非阻塞的优先级指令调度，保障并发写入时的线程安全。
 * 维护指南: ProcessSendQueueAsync 中的 Task.Delay 需根据下位机 MCU 的解析性能进行微调；Close 方法通过后台 Task 隔离以规避底层驱动引发的 UI 线程死锁。
 */

namespace GD_ControlCenter_WPF.Services
{
    public partial class SerialPortService : ISerialPortService, IDisposable
    {
        /// <summary>
        /// 核心 SerialPort 实例，负责底层的串口 IO 操作。
        /// </summary>
        private SerialPort? _serialPort;

        /// <summary>
        /// 配置服务引用，用于读取和保存上次连接的串口记录。
        /// </summary>
        private readonly JsonConfigService _configService;

        /// <summary>
        /// 高优先级指令并发队列（存储硬件控制类报文）。
        /// </summary>
        private readonly ConcurrentQueue<byte[]> _highPriorityQueue = new();

        /// <summary>
        /// 低优先级指令并发队列（存储轮询查询类报文）。
        /// </summary>
        private readonly ConcurrentQueue<byte[]> _lowPriorityQueue = new();

        /// <summary>
        /// 异步信号量，用于在指令入队时唤醒后台发送线程。
        /// </summary>
        private readonly SemaphoreSlim _signal = new(0);

        /// <summary>
        /// 用于管理后台任务生命周期的取消令牌源。
        /// </summary>
        private readonly CancellationTokenSource _cts = new();

        /// <summary>
        /// 连接状态变更事件。
        /// </summary>
        public event Action<bool>? ConnectionStatusChanged;

        /// <summary>
        /// 串口是否处于开启状态。
        /// </summary>
        public bool IsOpen { get; private set; }

        /// <summary>
        /// 当前操作的串口名称。
        /// </summary>
        public string? CurrentPortName { get; private set; }

        /// <summary>
        /// 构造函数：初始化串口服务并启动后台消费处理线程。
        /// </summary>
        /// <param name="configService">注入的配置管理服务。</param>
        public SerialPortService(JsonConfigService configService)
        {
            _configService = configService;

            // 启动独立线程/任务处理发送队列，避免阻塞调用方
            Task.Run(() => ProcessSendQueueAsync(_cts.Token));
        }

        /// <summary>
        /// 执行自动连接逻辑：读取配置文件并尝试激活上次使用的物理串口。
        /// </summary>
        public void AutoConnect()
        {
            var config = _configService.Load();
            var lastPort = config.LastSerialPort;
            var ports = GetAvailablePorts();

            if (!string.IsNullOrEmpty(lastPort) && ports.Contains(lastPort) && !IsOpen)
            {
                Open(lastPort, 9600);
            }
        }

        /// <summary>
        /// 获取当前计算设备上所有已识别的串口名称。
        /// </summary>
        public string[] GetAvailablePorts() => SerialPort.GetPortNames();

        /// <summary>
        /// 初始化并打开指定的串口，同时更新全局配置。
        /// </summary>
        /// <param name="portName">目标串口名称。</param>
        /// <param name="baudRate">通讯波特率。</param>
        /// <returns>成功打开返回 True，发生异常返回 False。</returns>
        public bool Open(string portName, int baudRate)
        {
            try
            {
                if (IsOpen) Close();

                _serialPort = new SerialPort
                {
                    PortName = portName,
                    BaudRate = baudRate,
                    DataBits = 8,
                    StopBits = StopBits.One,
                    Parity = Parity.None,
                    ReadTimeout = 500,
                    WriteTimeout = 500
                };

                _serialPort.DataReceived += OnSerialDataReceived;
                _serialPort.Open();

                // 同步内部状态并发布连接成功事件
                UpdateConnectionStatus(true, portName);

                // 持久化串口选择
                var config = _configService.Load();
                config.LastSerialPort = portName;
                _configService.Save(config);

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 安全关闭串口，采用后台隔离执行方式防止 UI 线程死锁，并清空待发队列。
        /// </summary>
        public void Close()
        {
            if (_serialPort != null)
            {
                // 立即解绑事件处理器，防止关闭过程中继续触发回调
                _serialPort.DataReceived -= OnSerialDataReceived;

                var spToClose = _serialPort;
                _serialPort = null;

                // 串口关闭在某些驱动下可能耗时极长，故放入后台执行并设定超时等待
                var closeTask = Task.Run(() =>
                {
                    try
                    {
                        if (spToClose.IsOpen)
                        {
                            spToClose.DiscardInBuffer();
                            spToClose.DiscardOutBuffer();
                            spToClose.Close();
                        }
                        spToClose.Dispose();
                    }
                    catch { /* 忽略 IO 释放过程中的异常 */ }
                });

                closeTask.Wait(TimeSpan.FromMilliseconds(500));
            }

            // 清理所有积压的指令，防止重连后发送过期的“僵尸”指令
            _highPriorityQueue.Clear();
            _lowPriorityQueue.Clear();

            UpdateConnectionStatus(false, null);
        }

        /// <summary>
        /// 更新服务内部连接状态，并视情况触发外部事件通知。
        /// </summary>
        /// <param name="isOpen">连接是否建立。</param>
        /// <param name="portName">串口名。</param>
        private void UpdateConnectionStatus(bool isOpen, string? portName)
        {
            if (IsOpen != isOpen || CurrentPortName != portName)
            {
                IsOpen = isOpen;
                CurrentPortName = portName;
                ConnectionStatusChanged?.Invoke(IsOpen);
            }
        }

        /// <summary>
        /// 生产者：将指令数据按优先级推入队列，并激活消费信号。
        /// </summary>
        /// <param name="data">指令字节数组。</param>
        /// <param name="priority">发送优先级。</param>
        public void Send(byte[] data, CommandPriority priority = CommandPriority.High)
        {
            if (priority == CommandPriority.High)
                _highPriorityQueue.Enqueue(data);
            else
                _lowPriorityQueue.Enqueue(data);

            _signal.Release();
        }

        /// <summary>
        /// 消费者任务：独立循环监听队列，执行优先级调度发送逻辑。
        /// </summary>
        /// <param name="token">任务取消令牌。</param>
        private async Task ProcessSendQueueAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // 挂起等待指令信号
                    await _signal.WaitAsync(token);

                    byte[]? commandToSend = null;

                    // 优先级调度逻辑：优先处理高优先级队列中的所有积压指令
                    if (_highPriorityQueue.TryDequeue(out var highCmd))
                        commandToSend = highCmd;
                    else if (_lowPriorityQueue.TryDequeue(out var lowCmd))
                        commandToSend = lowCmd;

                    if (commandToSend != null && _serialPort is { IsOpen: true })
                    {
                        try
                        {
                            _serialPort.Write(commandToSend, 0, commandToSend.Length);

                            // 强制硬件安全间隔：给予下位机 MCU 响应与数据落盘的物理时间
                            await Task.Delay(200, token);
                        }
                        catch { /* 捕捉瞬时物理断开等 IO 异常 */ }
                    }
                }
            }
            catch (OperationCanceledException) { /* 正常退出服务 */ }
        }

        /// <summary>
        /// 串口接收回调处理。
        /// </summary>
        private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (sender is not SerialPort sp || !sp.IsOpen) return;

            try
            {
                // 等待硬件数据完全到达缓冲区，防止半帧/分段现象
                Thread.Sleep(200);

                if (!sp.IsOpen) return;

                int bytesToRead = sp.BytesToRead;
                if (bytesToRead <= 0) return;

                byte[] buffer = new byte[bytesToRead];
                sp.Read(buffer, 0, bytesToRead);

                // 通过全局消息总线分发原始 Hex 报文，由专门的消息订阅者进行协议解析
                WeakReferenceMessenger.Default.Send(new HexDataMessage(buffer));
            }
            catch { /* 捕捉接收过程中可能出现的设备意外拔出异常 */ }
        }

        /// <summary>
        /// 释放资源，确保后台消费任务终止且串口关闭。
        /// </summary>
        public void Dispose()
        {
            _cts.Cancel();
            Close();
            _cts.Dispose();
            _signal.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}