using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Models.Protocols;
using GD_ControlCenter_WPF.Services.Commands;
using System.Timers;

/*
 * 文件名: BatteryService.cs
 * 描述: 电池业务服务类，负责周期性轮询电池状态，并根据回传电流矢量判定充电/放电工况。
 * 内部集成看门狗机制与双优先查询逻辑，确保在复杂电磁环境下电池状态更新的可靠性与实时性。
 * 维护指南: 默认轮询间隔 30s，离线判定阈值 100s；ParseBatteryData 方法中的电流判定逻辑需与 BMS 通讯协议文档保持高度一致。
 */

namespace GD_ControlCenter_WPF.Services
{
    /// <summary>
    /// 电池业务服务：实现电池电量监控、状态轮询及通讯离线判定。
    /// </summary>
    public partial class BatteryService : ObservableObject, IDisposable
    {
        /// <summary>
        /// 串口通讯服务引用。
        /// </summary>
        private readonly ISerialPortService _serialPortService;

        /// <summary>
        /// 后台轮询定时器。
        /// </summary>
        private readonly System.Timers.Timer _pollingTimer;

        /// <summary>
        /// 记录最后一次收到有效报文的系统时间，用于离线判定。
        /// </summary>
        private DateTime _lastReceivedTime = DateTime.MinValue;

        /// <summary>
        /// 判定通讯离线的最大允许时长（秒）。
        /// </summary>
        private const int MaxOfflineSeconds = 100;

        /// <summary>
        /// 当电池电量、连接状态或充电工况发生变化时触发。
        /// </summary>
        public event EventHandler? StatusUpdated;

        /// <summary>
        /// 电池剩余百分比 (0-100)。
        /// </summary>
        public int Percentage { get; private set; }

        /// <summary>
        /// 指示电池通讯是否在线。
        /// </summary>
        public bool IsOnline { get; private set; }

        /// <summary>
        /// 指示电池是否处于充电状态（基于电流矢量判定）。
        /// </summary>
        public bool IsCharging { get; private set; }

        /// <summary>
        /// 初始化电池服务并注册消息订阅。
        /// </summary>
        /// <param name="serialPortService">注入的串口通讯服务。</param>
        public BatteryService(ISerialPortService serialPortService)
        {
            _serialPortService = serialPortService;

            // 初始化轮询定时器：设为 30s 间隔
            _pollingTimer = new System.Timers.Timer(30000);
            _pollingTimer.Elapsed += OnPollingTimerElapsed;
            _pollingTimer.AutoReset = true;

            // 订阅经过协议解析后的电池标准数据帧
            WeakReferenceMessenger.Default.Register<BatteryFrameMessage>(this, (r, m) =>
            {
                ParseBatteryData(m.Value);
            });
        }

        /// <summary>
        /// 开启电池状态监控任务。
        /// 立即执行首次查询，并在 1 秒后进行二次补偿查询以确保初始化成功。
        /// </summary>
        public void Start()
        {
            if (!_pollingTimer.Enabled)
            {
                _pollingTimer.Start();

                // 执行启动后的即时查询
                SendQuery();

                // 执行 1s 后的补偿查询（异步非阻塞）
                Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    if (_pollingTimer.Enabled)
                    {
                        SendQuery();
                    }
                });
            }
        }

        /// <summary>
        /// 停止电池监控，并强制标记为离线。
        /// </summary>
        public void Stop()
        {
            _pollingTimer.Stop();
            IsOnline = false;
        }

        /// <summary>
        /// 定时器周期回调：检测是否通讯超时并下发轮询指令。
        /// </summary>
        private void OnPollingTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            // 看门狗逻辑：若超时未收到数据，则判定为离线
            if (IsOnline && (DateTime.Now - _lastReceivedTime).TotalSeconds > MaxOfflineSeconds)
            {
                IsOnline = false;
                StatusUpdated?.Invoke(this, EventArgs.Empty);
            }

            SendQuery();
        }

        /// <summary>
        /// 下发电池查询指令，使用低优先级队列以保障核心控制指令的带宽。
        /// </summary>
        private void SendQuery()
        {
            byte[] cmd = ControlCommandFactory.CreateBatteryQuery();
            _serialPortService.Send(cmd, CommandPriority.Low);
        }

        /// <summary>
        /// 电池报文解析核心逻辑。
        /// 提取电量百分比、计算电流矢量方向并判定充电状态。
        /// </summary>
        /// <param name="frame">由解析服务识别出的 13 字节电池标准帧。</param>
        private void ParseBatteryData(byte[] frame)
        {
            // 协议安全检查
            if (frame.Length == 13 && (FunctionCode)frame[3] == FunctionCode.Battery)
            {
                try
                {
                    // 提取电流数据段（索引 6,7）
                    byte currentHigh = frame[6];
                    byte currentLow = frame[7];

                    // 提取电量百分比（索引 8）
                    Percentage = frame[8];

                    // 电流状态判定逻辑：
                    // 1. 最高位为 1 代表电流为负（放电）
                    // 2. 电流为 0 代表静置
                    bool isNegativeOrZero = (currentHigh >> 7 == 1) || (currentHigh == 0 && currentLow == 0);
                    IsCharging = !isNegativeOrZero;

                    // 更新通讯
                    IsOnline = true;
                    _lastReceivedTime = DateTime.Now;

                    // 通知 UI 或 ViewModel 数据已刷新
                    StatusUpdated?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    // 调试捕获：工控现场异常记录。注：上线后建议替换为日志系统。
                    System.Windows.MessageBox.Show($"电池解析异常: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        /// <summary>
        /// 释放资源，注销消息订阅并销毁定时器。
        /// </summary>
        public void Dispose()
        {
            _pollingTimer?.Dispose();
            WeakReferenceMessenger.Default.Unregister<BatteryFrameMessage>(this);
        }
    }
}