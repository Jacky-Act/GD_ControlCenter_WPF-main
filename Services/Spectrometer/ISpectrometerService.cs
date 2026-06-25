using GD_ControlCenter_WPF.Models.Spectrometer;

/*
 * 文件名: ISpectrometerService.cs
 * 描述: 定义单个光谱仪硬件操作的标准抽象接口。
 * 本接口屏蔽了底层 C++ SDK 的句柄操作细节，为业务层提供面向对象的配置管理与数据采集契约。
 * 维护指南: 扩展新功能（如外部触发模式）时，应在此定义契约并在实现类中通过全局锁保护 SDK 调用；DataReady 事件订阅者需注意线程同步问题。
 */

namespace GD_ControlCenter_WPF.Services.Spectrometer
{
    /// <summary>
    /// 光谱仪服务接口：标准化硬件初始化、参数配置及数据采集行为。
    /// </summary>
    public interface ISpectrometerService : IDisposable
    {
        #region 1. 状态与配置

        /// <summary>
        /// 获取当前光谱仪的硬件配置实体（包含句柄、序列号、积分时间等）。
        /// </summary>
        SpectrometerConfig Config { get; }

        /// <summary>
        /// 指示当前硬件是否正处于无限循环的“持续测量”模式中。
        /// </summary>
        bool IsMeasuring { get; }

        #endregion

        #region 2. 数据流事件

        /// <summary>
        /// 数据就绪事件：当底层硬件完成一次物理采集并解析为 C# 实体后异步触发。
        /// 参数：<see cref="SpectralData"/> 包含波长与强度的完整数据包。
        /// </summary>
        event Action<SpectralData> DataReady;

        #endregion

        #region 3. 核心控制动作

        /// <summary>
        /// 异步初始化光谱仪：执行硬件激活、ADC 位数协商及物理像素数获取。
        /// </summary>
        /// <returns>初始化成功返回 true，否则返回 false。</returns>
        Task<bool> InitializeAsync();

        /// <summary>
        /// 同步下发配置参数：将内存中的 Config 映射至底层 SDK 缓冲区。
        /// </summary>
        /// <returns>SDK 返回的状态码，0 表示准备就绪。</returns>
        int ApplyConfiguration();

        /// <summary>
        /// 异步安全更新配置：在不关闭设备的前提下，自动处理“停止->更新->重启”逻辑。
        /// </summary>
        /// <param name="newIntegrationTime">新的积分时间（单位：ms）。</param>
        /// <param name="newAverages">新的硬件采样平均次数。</param>
        /// <returns>操作状态码。</returns>
        Task<int> UpdateConfigurationAsync(float newIntegrationTime, uint newAverages);

        #endregion

        #region 4. 测量模式控制

        /// <summary>
        /// 异步单次测量：挂起当前任务直至硬件返回单帧数据。
        /// </summary>
        /// <param name="ct">用于取消等待的任务令牌。</param>
        /// <returns>采集到的光谱数据实体。</returns>
        Task<SpectralData> MeasureOnceAsync(CancellationToken ct = default);

        /// <summary>
        /// 启动持续测量：开启硬件回调模式，数据将通过 <see cref="DataReady"/> 持续推送。
        /// </summary>
        void StartContinuousMeasurement();

        /// <summary>
        /// 停止测量：中断硬件采集循环并安全释放底层总线占用。
        /// </summary>
        void StopMeasurement();

        #endregion
    }
}