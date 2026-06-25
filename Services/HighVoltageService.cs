using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Models.Protocols;
using GD_ControlCenter_WPF.Services.Commands;
using System.Timers;

/*
 * 文件名: HighVoltageService.cs
 * 描述: 高压电源控制与监控服务类。负责高压输出参数的设定（功能码 0x22）与运行数据（电压、电流）的实时回传解析（功能码 0x66）。
 * 维护指南: 
 * 1. 线性拟合参数（斜率与截距）需根据硬件出厂报告定期核对。
 * 2. 解析时使用的 0x7F 掩码是为了过滤硬件协议中的最高位（符号位/状态位）。
 * 3. 修改轮询间隔（1000ms）时需考虑实际情况。
 */

namespace GD_ControlCenter_WPF.Services
{
    /// <summary>
    /// 高压电源服务：实现对高压电源硬件的定时状态采集、参数下发以及通讯完整性监控。
    /// </summary>
    public partial class HighVoltageService : ObservableObject, IDisposable
    {
        /// <summary>
        /// 串口通讯接口，用于下发控制指令与查询请求。
        /// </summary>
        private readonly ISerialPortService _serialPortService;

        /// <summary>
        /// 状态轮询定时器，用于驱动固定频率的 0x66 查询指令下发。
        /// </summary>
        private readonly System.Timers.Timer _pollingTimer;

        /// <summary>
        /// 看门狗计时：记录最后一次收到有效回传报文的系统时刻。
        /// </summary>
        private DateTime _lastReceivedTime = DateTime.MinValue;

        /// <summary>
        /// 通讯判定离线的阈值。若连续 10 秒未收到回传，则视硬件为断开状态。
        /// </summary>
        private const int MaxOfflineSeconds = 10;

        /// <summary>
        /// 实时输出电压（单位：V）。对应 UI 界面显示的电压监测数值。
        /// </summary>
        public int Voltage { get; private set; }

        /// <summary>
        /// 实时输出电流（单位：mA）。对应 UI 界面显示的电流监测数值。
        /// </summary>
        public int Current { get; private set; }

        /// <summary>
        /// 硬件通讯在线状态标识。
        /// </summary>
        public bool IsOnline { get; private set; }

        /// <summary>
        /// 构造函数：初始化串口引用、配置定时器并注册消息订阅。
        /// </summary>
        /// <param name="serialPortService">通过依赖注入获取的串口服务实例。</param>
        public HighVoltageService(ISerialPortService serialPortService)
        {
            _serialPortService = serialPortService;

            // 初始化轮询定时器，设定为 1000 毫秒执行一次查询。
            _pollingTimer = new System.Timers.Timer(1000);
            _pollingTimer.Elapsed += OnPollingTimerElapsed;
            _pollingTimer.AutoReset = true;

            // 订阅由 ProtocolService 经过 CRC 校验分发的 13 字节标准回传消息。
            WeakReferenceMessenger.Default.Register<ControlResponseMessage>(this, (r, m) =>
            {
                // 收到消息后立即进入解析逻辑。
                ParseResponse(m.Value);
            });
        }

        /// <summary>
        /// 启动监控任务。开启定时器并立即下发首帧查询请求，减少启动等待。
        /// </summary>
        public void Start()
        {
            if (!_pollingTimer.Enabled)
            {
                _pollingTimer.Start();
                SendQuery();
            }
        }

        /// <summary>
        /// 停止监控任务。关闭定时器并重置在线状态。
        /// </summary>
        public void Stop()
        {
            _pollingTimer.Stop();
            IsOnline = false;
        }

        /// <summary>
        /// 设置高压电源参数（功能码 0x22）。
        /// </summary>
        /// <param name="targetVoltage">目标电压值 (V)。</param>
        /// <param name="targetCurrent">目标电流限制值 (mA)。</param>
        public void SetHighVoltage(int targetVoltage, int targetCurrent)
        {
            var cmd = ControlCommandFactory.CreateHighVoltage(targetVoltage, targetCurrent);
            _serialPortService.Send(cmd);
        }

        /// <summary>
        /// 定时器周期触发回调。执行看门狗逻辑并发送查询报文。
        /// </summary>
        /// <param name="sender">触发对象。</param>
        /// <param name="e">时间参数。</param>
        private void OnPollingTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            // 如果在线且距离上次收到数据的时间超过阈值，判定为离线。
            if (IsOnline && (DateTime.Now - _lastReceivedTime).TotalSeconds > MaxOfflineSeconds)
            {
                IsOnline = false;
                OnPropertyChanged(nameof(IsOnline));
            }
            // 下发轮询查询报文。
            SendQuery();
        }

        /// <summary>
        /// 构建并下发 0x66 功能码查询报文。
        /// 采用低优先级队列，避免影响高频电机控制指令的实时性。
        /// </summary>
        private void SendQuery()
        {
            var cmd = ControlCommandFactory.CreateHighVoltageQuery();
            _serialPortService.Send(cmd, CommandPriority.Low);
        }

        /// <summary>
        /// 核心报文解析逻辑。将硬件反馈的原始 16 位 ADC 数据转换为真实的物理单位。
        /// </summary>
        /// <param name="frame">接收到的 13 字节标准完整数据帧。</param>
        private void ParseResponse(byte[] frame)
        {
            // 校验功能码是否符合 0x66（电力信息回传）。
            if (frame.Length == 13 && (FunctionCode)frame[3] == FunctionCode.ReturnPowerInfo)
            {
                try
                {
                    // -------------------- 1. 电压计算 --------------------
                    // 提取原始 ADC 值：frame[6] 为高字节（取低7位掩码），frame[7] 为低字节。
                    double rawVoltage = frame[7] + (frame[6] & 0x7f) * 256.0;

                    // 应用线性拟合公式：实际值 = 截距 + (斜率 * 采样值)。
                    double realVoltage = -4.49957 + (0.04929 * rawVoltage);

                    // 边界修正：若由于偏差算出负值，则强制归零。
                    if (realVoltage < 0) realVoltage = 0;

                    // 将结果四舍五入并赋值给公开属性。
                    Voltage = (int)Math.Round(realVoltage);

                    // -------------------- 2. 电流计算 --------------------
                    // 提取原始 ADC 值：frame[8] 为高字节，frame[9] 为低字节。
                    double rawCurrent = frame[9] + (frame[8] & 0x7f) * 256.0;

                    double realCurrent = 0;

                    // 溢出保护逻辑：当原始值超过 32000 时，通常对应传感器开路或硬件极值报错。
                    if (rawCurrent < 32000)
                    {
                        // 应用电流拟合公式。
                        realCurrent = 3.01216 + (0.00309 * rawCurrent);

                        // 底部死区过滤：防止机器待机时由于截距产生的“幽灵电流”。
                        if (realCurrent < 3.5) realCurrent = 0;
                    }

                    // 将结果赋值给公开属性。
                    Current = (int)Math.Round(realCurrent);

                    // -------------------- 3. 状态与通知 --------------------
                    // 标记通讯正常，并更新最后活跃时间。
                    IsOnline = true;
                    _lastReceivedTime = DateTime.Now;

                    // 手动通知 MVVM 框架刷新 UI 绑定。
                    OnPropertyChanged(nameof(Voltage));
                    OnPropertyChanged(nameof(Current));
                    OnPropertyChanged(nameof(IsOnline));
                }
                catch (Exception ex)
                {
                    // 现场调试辅助：若计算溢出或数组越界，则弹出详细堆栈供分析。
                    System.Windows.MessageBox.Show($"电源解析异常: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        /// <summary>
        /// 释放资源。注销消息中心的所有订阅并销毁定时器。
        /// </summary>
        public void Dispose()
        {
            _pollingTimer?.Dispose();
            WeakReferenceMessenger.Default.Unregister<ControlResponseMessage>(this);
        }
    }
}