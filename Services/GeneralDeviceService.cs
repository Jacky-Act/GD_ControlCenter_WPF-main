using GD_ControlCenter_WPF.Services.Commands;

/*
 * 文件名: GeneralDeviceService.cs
 * 描述: 通用外设控制服务类。负责管理执行开环控制逻辑的硬件模块，如各类泵组、切换阀及点火系统。
 * 本服务不涉及复杂的状态机回传处理，主要作为业务逻辑层与指令工厂（ControlCommandFactory）之间的调用桥梁。
 * 维护指南: 增加新外设控制逻辑时，应确保指令工厂已存在对应报文构建方法；点火延时参数依赖于 JsonConfigService 的实时加载。
 */

namespace GD_ControlCenter_WPF.Services
{
    /// <summary>
    /// 通用设备服务：封装了蠕动泵、注射泵、多通道阀、转向阀及点火功能的业务操作接口。
    /// </summary>
    public class GeneralDeviceService
    {
        /// <summary>
        /// 串口通讯服务引用，用于执行报文的底层物理发送。
        /// </summary>
        private readonly ISerialPortService _serialPortService;

        /// <summary>
        /// 配置管理服务引用，用于读取点火延时等持久化参数。
        /// </summary>
        private readonly JsonConfigService _configService;

        /// <summary>
        /// 构造函数：注入必要的底层通讯与配置服务。
        /// </summary>
        /// <param name="serialPortService">串口通讯服务实例。</param>
        /// <param name="configService">JSON 配置管理服务实例。</param>
        public GeneralDeviceService(ISerialPortService serialPortService, JsonConfigService configService)
        {
            _serialPortService = serialPortService;
            _configService = configService;
        }

        /// <summary>
        /// 控制蠕动泵的运行状态。
        /// </summary>
        /// <param name="speed">设定转速值（具体量程需参考硬件协议说明）。 </param>
        /// <param name="isClockwise">旋转方向标识：True 为顺时针（前向），False 为逆时针（后向）。 </param>
        /// <param name="isStart">运行开关：True 启动泵体运行，False 停止。 </param>
        public void ControlPeristalticPump(short speed, bool isClockwise, bool isStart)
        {
            var cmd = ControlCommandFactory.CreatePeristalticPump(speed, isClockwise, isStart);
            _serialPortService.Send(cmd);
        }

        /// <summary>
        /// 控制双通道转向阀的状态切换。
        /// </summary>
        /// <param name="channel">目标通道标识：True 对应通道 2，False 对应通道 1。 </param>
        public void ControlSteeringValve(bool channel)
        {
            var cmd = ControlCommandFactory.CreateSteeringValve(channel);
            _serialPortService.Send(cmd);
        }

        /// <summary>
        /// 控制注射泵的精确位移。
        /// </summary>
        /// <param name="isOutput">移动方向：True 为执行推出动作，False 为执行吸入动作。 </param>
        /// <param name="distance">位移脉冲步数，典型有效范围为 0 至 3000 步。 </param>
        public void ControlSyringePump(bool isOutput, short distance)
        {
            var cmd = ControlCommandFactory.CreateSyringePump(isOutput, distance);
            _serialPortService.Send(cmd);
        }

        /// <summary>
        /// 控制十通道选择阀的物理定位。
        /// </summary>
        /// <param name="channel">目标物理通道号，范围应在 1 至 10 之间。 </param>
        public void ControlMultiChannelValve(int channel)
        {
            var cmd = ControlCommandFactory.CreateMultiChannelValve(channel);
            _serialPortService.Send(cmd);
        }

        /// <summary>
        /// 执行点火功能逻辑。
        /// 从全局配置中动态获取点火延时秒数，转换精度后下发指令。
        /// </summary>
        public void Fire()
        {
            // 实时从本地配置中获取点火相关的延时设定
            var config = _configService.Load();

            // 协议要求以毫秒 (ms) 为单位，故需将配置中的秒数 (double) 乘以 1000 并取整
            int delayMs = (int)(config.IgnitionDelaySeconds * 1000);

            var cmd = ControlCommandFactory.CreateIgnition(delayMs);
            _serialPortService.Send(cmd);
        }
    }
}