using CommunityToolkit.Mvvm.Messaging.Messages;
using GD_ControlCenter_WPF.Models.Spectrometer;

/*
 * 文件名: SpectrometerMessages.cs
 * 描述: 定义系统全局范围内的弱引用消息实体。
 * 架构职责:
 * 1. 解耦：实现硬件采集服务、各功能模块 ViewModel 与 UI 视图层之间的零依赖通讯。
 * 2. 同步：确保主界面、时序图、设置弹窗等多个窗口能实时共享硬件状态与采集进度。
 * 维护指南: 
 * 1. 新增消息需归类至 Data Flow (数据)、Status Flow (状态) 或 View Control (视图控制) 区域。
 * 2. 必须详细标注“发送方”与“订阅方”以方便代码溯源。
 */

namespace GD_ControlCenter_WPF.Models.Messages
{
    #region 1. [数据流] 消息

    /// <summary>
    /// 核心光谱原始数据包送达消息。
    /// 发送方：SpectrometerManager (硬件管理中心分发)。
    /// 订阅方：
    ///   - ControlPanelViewModel (更新实时波形快照)
    ///   - TimeSeriesViewModel (泵入时序图缓存池)
    ///   - SpectrometerViewModel (触发单通道绘图更新事件)
    ///   - PeakTrackingService (执行寻峰与漂移校准)
    ///   - PlatformCalibrationService (三维平台自动寻优)
    ///   - ControlPanelView.xaml (主界面光谱的显示)
    /// </summary>
    public class SpectralDataMessage : ValueChangedMessage<SpectralData>
    {
        /// <summary> 产生该帧数据时的曝光积分时间 (ms)。 </summary>
        public float IntegrationTime { get; set; }

        /// <summary> 产生该帧数据时的硬件平均次数。 </summary>
        public uint AveragingCount { get; set; }

        public SpectralDataMessage(SpectralData value) : base(value) { }
    }

    #endregion

    #region 2. [状态流] 消息 (Status Flow)

    /// <summary>
    /// 光谱仪连接性或全局使能状态变更消息。
    /// 发送方：SettingsViewModel (当拨动全局光谱仪开关时)。
    /// 订阅方：
    ///   - ControlPanelViewModel (执行 UI 断开后的状态复位逻辑)。
    /// </summary>
    public class SpectrometerStatusMessage : ValueChangedMessage<SpectrometerConfig>
    {
        public SpectrometerStatusMessage(SpectrometerConfig value) : base(value) { }
    }

    #endregion

    #region 3. [视图控制流] 消息 (View Control Flow)

    // ================== ScottPlot 图表交互 ==================

    /// <summary>
    /// 请求光谱图视图执行自动缩放 (Auto-Axis) 的指令消息。
    /// 发送方：ControlPanelViewModel (响应用户点击)。
    /// 订阅方：ControlPanelView.xaml.cs (View 层 Code-Behind)。
    /// </summary>
    public class AutoRangeRequestMessage { }

    /// <summary>
    /// 请求光谱图视图恢复至全局极限最大范围的指令消息。
    /// 发送方：ControlPanelViewModel (响应用户点击)。
    /// 订阅方：ControlPanelView.xaml.cs (View 层 Code-Behind)。
    /// </summary>
    public class MaxRangeRequestMessage { }

    /// <summary>
    /// 时序图布局模式（矩阵/列表）切换请求消息。
    /// 发送方：TimeSeriesViewModel (响应布局切换命令)。
    /// 订阅方：TimeSeriesView.xaml.cs。
    /// </summary>
    public class ChangeTimeSeriesViewMessage
    {
        public bool IsMatrixView { get; }
        public ChangeTimeSeriesViewMessage(bool isMatrixView) => IsMatrixView = isMatrixView;
    }

    // ================== 参考图层控制 ==================

    /// <summary>
    /// 加载并显示外部参考光谱图层 (静态红色波形)。
    /// 发送方：ControlPanelViewModel (当 Excel 导入成功后)。
    /// 订阅方：ControlPanelView.xaml.cs。
    /// </summary>
    public class LoadReferencePlotMessage : ValueChangedMessage<SpectralData>
    {
        public LoadReferencePlotMessage(SpectralData value) : base(value) { }
    }

    /// <summary>
    /// 强制清除当前图表上的参考光谱图层。
    /// 发送方：ControlPanelViewModel (波长匹配失败或用户手动关闭时)。
    /// 订阅方：ControlPanelView.xaml.cs。
    /// </summary>
    public class ClearReferencePlotMessage { }

    // ================== 自动化业务同步 ==================

    /// <summary>
    /// 脚本采集任务的保存状态同步消息。
    /// 发送方：ControlPanelViewModel (当后台采集引擎启动或结束时)。
    /// 订阅方：
    ///   - ScriptSaveViewModel (同步弹窗内的按钮颜色与 UI 锁定状态)。
    /// </summary>
    public class ScriptSaveStateChangedMessage : ValueChangedMessage<bool>
    {
        public ScriptSaveStateChangedMessage(bool isSaving) : base(isSaving) { }
    }

    #endregion
}