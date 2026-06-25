using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Models.Spectrometer;
using GD_ControlCenter_WPF.Services.Spectrometer;

/*
 * 文件名: SpectrometerViewModel.cs
 * 描述: 单台光谱仪的 UI 逻辑包装器。职责包括同步 Model 数据、接收硬件消息、管理按钮状态与数据分发。
 * 维护指南: 
 * 1. UI 修改参数（积分时间/平均次数）会立即同步至底层 Model 实体。
 * 2. 通过校验 SerialNumber 确保全局总线上的光谱数据准确分发至对应的实例。
 */

namespace GD_ControlCenter_WPF.ViewModels
{
    /// <summary>
    /// 光谱仪 UI 包装器：封装单台设备的业务逻辑与界面交互属性。
    /// </summary>
    public partial class SpectrometerViewModel : ObservableObject
    {
        #region 1. 依赖服务与 Model 引用

        /// <summary>
        /// 底层光谱仪配置 Model（持久化实体）。
        /// </summary>
        private readonly SpectrometerConfig _model;

        /// <summary>
        /// 关联的硬件驱动服务接口。
        /// </summary>
        private ISpectrometerService _service;

        #endregion

        #region 2. UI 交互属性

        /// <summary>
        /// 指示当前设备是否正处于数据采集测量状态。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MeasurementButtonText))]
        private bool _isCurrentlyMeasuring;

        /// <summary>
        /// 按钮显示文本。由 IsCurrentlyMeasuring 状态自动驱动。
        /// </summary>
        public string MeasurementButtonText => IsCurrentlyMeasuring ? "停止光谱" : "启动光谱";

        #endregion

        #region 3. 硬件参数属性 (Model 镜像)

        /// <summary>
        /// 设备唯一的硬件序列号。
        /// </summary>
        [ObservableProperty] private string _serialNumber = string.Empty;

        /// <summary>
        /// 标识设备当前是否已物理连接并成功初始化。
        /// </summary>
        [ObservableProperty] private bool _isConnected;

        /// <summary>
        /// 曝光积分时间（单位：ms）。
        /// </summary>
        [ObservableProperty] private float _integrationTimeMs;

        /// <summary>
        /// 硬件采样平均次数，用于提升信号信噪比。
        /// </summary>
        [ObservableProperty] private uint _averagingCount;

        #endregion

        #region 4. 事件与消息

        /// <summary>
        /// 绘图更新请求事件。由 View 层（如 ControlPanelView）订阅以驱动渲染引擎。
        /// </summary>
        public event Action<SpectralData>? RequestPlotUpdate;

        /// <summary>
        /// 配置保存委托。当关键物理参数变更后触发全局持久化逻辑。
        /// </summary>
        public Action? SaveConfigAction { get; set; }

        /// <summary>
        /// 获取当前 ViewModel 包装的原始 Model 引用。
        /// </summary>
        public SpectrometerConfig Model => _model;

        #endregion

        #region 5. 初始化与业务逻辑

        /// <summary>
        /// 构造光谱仪视图模型。
        /// </summary>
        /// <param name="model">光谱仪配置数据模型。</param>
        /// <param name="service">光谱仪硬件驱动服务。</param>
        public SpectrometerViewModel(SpectrometerConfig model, ISpectrometerService service)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _service = service;

            // 构造时同步物理 Model 数据
            UpdateFromModel();

            // 注册弱引用消息订阅：监听底层硬件产生的光谱数据包
            WeakReferenceMessenger.Default.Register<SpectralDataMessage>(this, (r, m) =>
            {
                // 核心路由逻辑：仅处理来源序列号与本设备一致的消息
                if (m.Value.SourceDeviceSerial == this.SerialNumber)
                {
                    RequestPlotUpdate?.Invoke(m.Value);
                }
            });
        }

        /// <summary>
        /// 从物理 Model 全量更新基础数据至 ViewModel 属性。
        /// </summary>
        public void UpdateFromModel()
        {
            SerialNumber = _model.SerialNumber;
            IsConnected = _model.IsConnected;
            IntegrationTimeMs = _model.IntegrationTimeMs;
            AveragingCount = _model.AveragingCount;
        }

        /// <summary>
        /// 注入硬件控制服务。
        /// </summary>
        public void SetService(ISpectrometerService service)
        {
            _service = service;
        }

        /// <summary> 拦截积分时间变更并同步至 Model。 </summary>
        partial void OnIntegrationTimeMsChanged(float value) => _model.IntegrationTimeMs = value;

        /// <summary> 拦截平均次数变更并同步至 Model。 </summary>
        partial void OnAveragingCountChanged(uint value) => _model.AveragingCount = value;

        #endregion

        #region 6. 参数修改逻辑 (异步重启机制)

        /// <summary>
        /// 异步修改积分时间命令。
        /// </summary>
        [RelayCommand]
        private async Task ChangeIntegrationTimeAsync(string timeStr)
        {
            if (float.TryParse(timeStr, out float newTime))
            {
                IntegrationTimeMs = newTime;
                _model.IntegrationTimeMs = newTime;
                SaveConfigAction?.Invoke();

                await ApplyHardwareConfigurationAsync(newTime, AveragingCount);
            }
        }

        /// <summary>
        /// 异步修改采样平均次数命令。
        /// </summary>
        [RelayCommand]
        private async Task ChangeAveragingCountAsync(string countStr)
        {
            if (uint.TryParse(countStr, out uint newCount))
            {
                AveragingCount = newCount;
                _model.AveragingCount = newCount;
                SaveConfigAction?.Invoke();

                await ApplyHardwareConfigurationAsync(IntegrationTimeMs, newCount);
            }
        }

        /// <summary>
        /// 核心配置应用逻辑：执行“停止-更新参数-重启”流水线。
        /// </summary>
        private async Task ApplyHardwareConfigurationAsync(float integrationTime, uint averagingCount)
        {
            var devices = SpectrometerManager.Instance.Devices;
            if (devices.Count == 0) return;

            // 在后台执行硬件状态机操作，避免阻塞 UI 线程
            await Task.Run(async () =>
            {
                bool wasMeasuring = IsCurrentlyMeasuring;

                // 物理熔断：修改寄存器前必须停止推流
                if (wasMeasuring) SpectrometerManager.Instance.StopAll();

                // 参数重写：并发更新所有物理设备的配置
                var updateTasks = devices.Select(device => device.UpdateConfigurationAsync(integrationTime, averagingCount));
                await Task.WhenAll(updateTasks);

                // 恢复测量：参数应用成功后重启采集
                if (wasMeasuring) SpectrometerManager.Instance.StartAll();
            });
        }

        #endregion
    }
}