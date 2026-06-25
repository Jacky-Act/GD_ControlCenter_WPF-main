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
 * 维护指南: 初始轮询间隔 3s，收到信号后默认轮询30秒，离线判定阈值 60s；ParseBatteryData 方法中的电流判定逻辑需与 BMS 通讯协议文档保持高度一致。
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
        private const int MaxOfflineSeconds = 60;

        /// <summary>
        /// 快查模式下的查询次数计数器。
        /// </summary>
        private int _rapidQueryCount = 0;

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

            // 初始化轮询定时器：设为 3s 间隔（快查模式）
            _pollingTimer = new System.Timers.Timer(3000);
            _pollingTimer.Elapsed += OnPollingTimerElapsed;
            _pollingTimer.AutoReset = true;

            WeakReferenceMessenger.Default.Register<BatteryFrameMessage>(this, (r, m) =>
            {
                ParseBatteryData(m.Value);
            });
        }

        /// <summary>
        /// 开启电池状态监控任务。
        /// 默认以快查模式（3秒间隔）启动。
        /// </summary>
        public void Start()
        {
            if (!_pollingTimer.Enabled)
            {
                _pollingTimer.Interval = 3000;
                _rapidQueryCount = 0;
                _pollingTimer.Start();

                // 执行首次查询
                SendQuery();
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
        /// 定时器周期回调：处理双速率轮询与超时离线逻辑。
        /// </summary>
        private void OnPollingTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            if (_pollingTimer.Interval == 3000)
            {
                _rapidQueryCount++;

                // 3秒快查累计20次（约60秒）无反馈，停止查询并抛出离线状态
                if (_rapidQueryCount > 20)
                {
                    Stop();
                    StatusUpdated?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }
            else
            {
                // 30秒慢查模式：若超过60秒未收到有效数据，降级回快查模式
                if ((DateTime.Now - _lastReceivedTime).TotalSeconds >= MaxOfflineSeconds)
                {
                    _pollingTimer.Interval = 3000;
                    _rapidQueryCount = 1; // 算作新一轮快查的第一次
                    IsOnline = false;
                    StatusUpdated?.Invoke(this, EventArgs.Empty);
                }
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
        /// </summary>
        private void ParseBatteryData(byte[] frame)
        {
            if (frame.Length == 13 && (FunctionCode)frame[3] == FunctionCode.Battery)
            {
                try
                {
                    byte currentHigh = frame[6];
                    byte currentLow = frame[7];
                    Percentage = frame[8];

                    bool isNegativeOrZero = (currentHigh >> 7 == 1) || (currentHigh == 0 && currentLow == 0);
                    IsCharging = !isNegativeOrZero;

                    // 更新通讯状态
                    IsOnline = true;
                    _lastReceivedTime = DateTime.Now;

                    // 收到反馈，切换为 30 秒慢查模式
                    if (_pollingTimer.Interval != 30000)
                    {
                        _pollingTimer.Interval = 30000;
                        _rapidQueryCount = 0;
                    }

                    StatusUpdated?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
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