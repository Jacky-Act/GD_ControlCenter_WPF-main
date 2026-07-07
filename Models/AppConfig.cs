/*
 * 文件名: AppConfig.cs
 * 描述: 定义软件全局运行配置模型，用于持久化存储硬件参数、实验设定及 UI 界面状态。
 * 本模型作为纯数据容器 (POCO) 支持 JSON 序列化，确保软件重启后能精准恢复上一次实验的运行上下文。
 * 维护指南: 新增属性必须赋予合理的初始默认值，以防止配置文件损坏或初次运行时引发空引用异常；修改物理换算参数（如泵流速斜率）需同步检查相关算法。
 */

namespace GD_ControlCenter_WPF.Models
{
    #region 1. 附属实体结构

    /// <summary>
    /// 时序图采样通道配置实体。
    /// 负责记录时序图表中的通道名称及其绑定的特征峰物理坐标。
    /// </summary>
    public class TimeSeriesSampleNode
    {
        /// <summary>
        /// 通道或样品的自定义显示名称。
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 用户在光谱图上选定的特征峰波长值 (X坐标)。
        /// 采用可空类型以支持“通道已创建但未绑定波长”的业务状态。
        /// </summary>
        public double? SelectedPeakX { get; set; }
    }

    /// <summary>
    /// 三维平台运行配置实体。
    /// 存储平台的实时坐标记忆、物理限位状态以及用户操作偏好。
    /// </summary>
    public class Platform3DConfig
    {
        /// <summary>
        /// 上次运行结束时的 X 轴脉冲位置。
        /// </summary>
        public int X { get; set; } = 0;

        /// <summary>
        /// 上次运行结束时的 Y 轴脉冲位置。
        /// </summary>
        public int Y { get; set; } = 0;

        /// <summary>
        /// 上次运行结束时的 Z 轴脉冲位置。
        /// </summary>
        public int Z { get; set; } = 0;

        /// <summary>
        /// 各轴零点限位 (Min) 的触发记忆。
        /// </summary>
        public Dictionary<string, bool> IsAtMin { get; set; } = new()
        {
            { "X", false }, { "Y", false }, { "Z", false }
        };

        /// <summary>
        /// 各轴最大值限位 (Max) 的触发记忆。
        /// </summary>
        public Dictionary<string, bool> IsAtMax { get; set; } = new()
        {
            { "X", false }, { "Y", false }, { "Z", false }
        };

        /// <summary>
        /// 用户设定的默认手动移动步距。
        /// </summary>
        public double DefaultStepDistance { get; set; } = 100;
    }

    #endregion

    /// <summary>
    /// 系统全局配置根实体类。
    /// 集中管理软件所有可持久化的运行参数。
    /// </summary>
    public class AppConfig
    {
        #region 2. 电力与流体控制参数

        // ================== 高压电源记忆 ==================

        /// <summary>
        /// 上次设定的高压电源输出电压 (V)。
        /// </summary>
        public int LastHvVoltage { get; set; } = 0;

        /// <summary>
        /// 上次设定的高压电源限制电流 (mA)。
        /// </summary>
        public int LastHvCurrent { get; set; } = 0;

        // ================== 蠕动泵控制记忆 ==================

        /// <summary>
        /// 蠕动泵运行转速 (RPM)。
        /// </summary>
        public int LastPumpSpeed { get; set; } = 0;

        /// <summary>
        /// 蠕动泵旋转方向逻辑：True 为顺时针（前向），False 为逆时针（后向）。
        /// </summary>
        public bool IsPumpClockwise { get; set; } = true;

        /// <summary>
        /// 蠕动泵流速换算方程斜率 K。用于执行 RPM 到实际流量的转换逻辑。
        /// </summary>
        public double PumpFlowRateK { get; set; } = 1.0;

        /// <summary>
        /// 蠕动泵流速换算方程截距 B。
        /// </summary>
        public double PumpFlowRateB { get; set; } = 0.0;

        // ================== 注射泵控制记忆 ==================

        /// <summary>
        /// 注射泵移动的脉冲步数（行程控制），典型范围 0-3000。
        /// </summary>
        public int LastSyringeDistance { get; set; } = 0;

        /// <summary>
        /// 注射泵推拉方向：True 为推（输出），False 为拉（抽取）。
        /// </summary>
        public bool IsSyringeOutput { get; set; } = true;

        // ================== 点火系统配置 ==================

        /// <summary>
        /// 是否启用熄火自动重燃安全机制。
        /// </summary>
        public bool IsAutoReigniteEnabled { get; set; } = false;

        /// <summary>
        /// 点火过程中的蠕动泵加速转动时间（单位：秒），范围 1.0 - 5.0。
        /// </summary>
        public double IgnitionDelaySeconds { get; set; } = 1.0;

        /// <summary>
        /// 点火转速，设定点火过程中的蠕动泵转速，范围 50 - 100。
        /// </summary>
        public short IgnitionSpeed { get; set; } = 50;

        #endregion

        #region 3. 光谱采集与任务控制

        /// <summary>
        /// 自动化实验任务的预设采样总次数。
        /// </summary>
        public int LastSampleCount { get; set; } = 1;

        /// <summary>
        /// 自动化实验任务的采样时间间隔（单位：秒）。
        /// </summary>
        public int LastSampleInterval { get; set; } = 1;

        /// <summary>
        /// 样品序列预设的标准样品数量。
        /// </summary>
        public int LastBatchStandardCount { get; set; } = 5;

        /// <summary>
        /// 样品序列预设的待测样品数量。
        /// </summary>
        public int LastBatchUnknownCount { get; set; } = 10;

        /// <summary>
        /// 样品序列预设的全局重复次数。
        /// </summary>
        public int LastSampleRepeats { get; set; } = 1;

        /// <summary>
        /// 样品序列预设的全局采集间隔(秒)。
        /// </summary>
        public double LastGlobalInterval { get; set; } = 0.0;

        /// <summary>
        /// 样品序列预设的浓度单位。
        /// </summary>
        public string LastConcentrationUnit { get; set; } = "ppm";

        /// <summary>
        /// 光谱仪硬件连接全局开关。
        /// </summary>
        public bool IsSpectrometerEnabled { get; set; } = true;

        /// <summary>
        /// 光谱仪曝光积分时间（单位：ms）。
        /// </summary>
        public float LastIntegrationTime { get; set; } = 0f;

        /// <summary>
        /// 光谱仪硬件底层采样平均次数。
        /// </summary>
        public uint LastAveragingCount { get; set; } = 0;

        #endregion

        #region 4. 时序图追踪与存储配置

        /// <summary>
        /// 用户自定义的时序图监控通道列表。
        /// </summary>
        public List<TimeSeriesSampleNode> TimeSeriesSampleNodes { get; set; } = new List<TimeSeriesSampleNode>();

        /// <summary>
        /// 上次成功通信的系统串口号（如 "COM2"）。
        /// </summary>
        public string LastSerialPort { get; set; } = string.Empty;

        /// <summary>
        /// 手动保存单次光谱图时的默认导出路径。
        /// </summary>
        public string SingleSaveExportPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        /// <summary>
        /// 时序图自动保存数据的目标文件夹。
        /// </summary>
        public string TimeSeriesSaveDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        /// <summary>
        /// 自动化采集脚本预设的保存文件次数。
        /// </summary>
        public int ScriptSaveCount { get; set; } = 11;

        /// <summary>
        /// 自动化采集脚本预设的物理保存间隔（秒）。
        /// </summary>
        public int ScriptSaveInterval { get; set; } = 5;

        /// <summary>
        /// 自动化脚本运行结果的默认存储目录。
        /// </summary>
        public string ScriptSaveDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        #endregion

        #region 5. 运动控制与 UI 记忆

        /// <summary>
        /// 三维平台的物理与逻辑配置实体。
        /// </summary>
        public Platform3DConfig Platform3D { get; set; } = new Platform3DConfig();

        /// <summary>
        /// 主窗口退出前的显示宽度。
        /// </summary>
        public double MainWindowWidth { get; set; } = 1280;

        /// <summary>
        /// 主窗口退出前的显示高度。
        /// </summary>
        public double MainWindowHeight { get; set; } = 800;

        /// <summary>
        /// 主窗口在退出时是否处于最大化状态。
        /// </summary>
        public bool IsMainWindowMaximized { get; set; } = false;

        #endregion
        // --- 核心：必须加上这个枚举定义 ---
        public enum MeasurementMode { Continuous, FlowInjection }

        // --- 核心：必须加上这个持久化属性 ---
        public MeasurementMode CurrentMeasurementMode { get; set; } = MeasurementMode.Continuous;
    }

}
