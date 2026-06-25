using CommunityToolkit.Mvvm.ComponentModel;

/*
 * 文件名: SpectrometerModel.cs
 * 描述: 定义光谱仪的硬件配置实体、核心测量数据载体以及特征峰追踪模型。
 * 作为核心领域模型层，本文件统一管理设备参数同步与采集数据的跨线程流转。
 * 维护指南: SpectralData 实例化后应视为不可变对象，严禁在多线程流转中修改内部数组；调整配置参数时需参考硬件手册中的最小积分时间与像素范围。
 */

namespace GD_ControlCenter_WPF.Models.Spectrometer
{
    #region 1. 硬件配置模型

    /// <summary>
    /// 光谱仪硬件配置实体。
    /// 用于存储设备身份信息、SDK 句柄及采集参数，支持与 UI 层的双向绑定。
    /// </summary>
    public partial class SpectrometerConfig
    {
        /// <summary>
        /// 设备的唯一出厂序列号。
        /// </summary>
        public string SerialNumber = string.Empty;

        /// <summary>
        /// 设备的友好显示名称。
        /// </summary>
        public string DeviceName = string.Empty;

        /// <summary>
        /// SDK 操作句柄。在设备激活后由 AVS_Activate 返回，用于后续所有硬件通信。
        /// </summary>
        public int DeviceHandle = -1;

        /// <summary>
        /// 标记当前设备是否已成功建立物理连接并完成初始化。
        /// </summary>
        public bool IsConnected = false;

        /// <summary>
        /// 积分时间，单位为毫秒 (ms)。
        /// 控制传感器曝光时长。
        /// </summary>
        public float IntegrationTimeMs = 100f;

        /// <summary>
        /// 硬件内部采样的平均次数。
        /// 通过多次采集取平均值来提升信噪比。
        /// </summary>
        public uint AveragingCount = 5;

        /// <summary>
        /// 数据采集的起始像素索引（通常为 0）。
        /// </summary>
        public ushort StartPixel = 0;

        /// <summary>
        /// 数据采集的终止像素索引（根据传感器硬件定义，如 2047 或 4095）。
        /// </summary>
        public ushort StopPixel = 4095;

        /// <summary>
        /// 是否开启硬件过曝（饱和）检测拦截。
        /// 开启后，若数据达到计数上限将触发警告。
        /// </summary>
        public bool IsSaturationDetectionEnabled = true;

        /// <summary>
        /// 硬件允许的最小积分时间下限（单位：ms）。
        /// 防止设定过小值导致 SDK 报错。
        /// </summary>
        public float MinIntegrationTime = 5.0f;
    }

    #endregion

    #region 2. 核心数据载体

    /// <summary>
    /// 光谱测量数据。
    /// 承载单次测量生成的完整波长与强度信息，作为跨模块通信的标准化信使。
    /// </summary>
    public class SpectralData
    {
        /// <summary>
        /// 波长数组（X 轴数据），单位：纳米 (nm)。
        /// 对应每个像素点经过校准后的物理波长。
        /// </summary>
        public double[] Wavelengths { get; set; } = Array.Empty<double>();

        /// <summary>
        /// 光强/计数数组（Y 轴数据），单位：Counts。
        /// 记录传感器感应到的原始光子计数。
        /// </summary>
        public double[] Intensities { get; set; } = Array.Empty<double>();

        /// <summary>
        /// 原始光谱数据生成的精确时间戳。
        /// </summary>
        public DateTime AcquisitionTime { get; set; } = DateTime.Now;

        /// <summary>
        /// 数据来源设备的序列号。
        /// 在多机系统中用于区分数据归属。
        /// </summary>
        public string SourceDeviceSerial { get; set; } = string.Empty;

        /// <summary>
        /// 初始化 SpectralData 的空实例。
        /// </summary>
        public SpectralData() { }

        /// <summary>
        /// 使用指定的波长、强度和设备标识初始化 SpectralData 实例。
        /// </summary>
        /// <param name="wavelengths">波长数组</param>
        /// <param name="intensities">光强数组</param>
        /// <param name="serial">来源设备序列号</param>
        public SpectralData(double[] wavelengths, double[] intensities, string serial = "")
        {
            Wavelengths = wavelengths;
            Intensities = intensities;
            SourceDeviceSerial = serial;
        }
    }

    #endregion

    #region 3. 动态追踪模型

    /// <summary>
    /// 特征峰实时追踪模型。
    /// 封装了寻峰算法所需的基准参数与实时计算结果。
    /// </summary>
    public partial class TrackedPeak : ObservableObject
    {
        /// <summary>
        /// 用户在 UI 上手动标记或设定的基准波长中心点。
        /// </summary>
        [ObservableProperty]
        private double _baseWavelength;

        /// <summary>
        /// 寻峰算法在基准波长左右波及的搜索范围容差 (±nm)。
        /// </summary>
        [ObservableProperty]
        private double _toleranceWindow = 0.2;

        /// <summary>
        /// 算法当前在窗口内实时追踪到的最高点波长位置 (X)。
        /// </summary>
        [ObservableProperty]
        private double _currentWavelength;

        /// <summary>
        /// 算法当前实时追踪到的最高点光强数值 (Y)。
        /// </summary>
        [ObservableProperty]
        private double _currentIntensity;

        /// <summary>
        /// 构造一个特征峰追踪实例。
        /// </summary>
        /// <param name="baseWavelength">初始锚点波长</param>
        /// <param name="toleranceWindow">搜索带宽（默认为 2.0nm）</param>
        public TrackedPeak(double baseWavelength, double toleranceWindow = 2.0)
        {
            BaseWavelength = baseWavelength;
            ToleranceWindow = toleranceWindow;
            CurrentWavelength = baseWavelength;
            CurrentIntensity = 0;
        }
    }

    #endregion
}