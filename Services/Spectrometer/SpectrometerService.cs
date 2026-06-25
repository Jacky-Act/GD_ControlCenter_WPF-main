using C_Sharp_Application; 
using CommunityToolkit.Mvvm.ComponentModel;
using GD_ControlCenter_WPF.Models.Spectrometer;
using System.Runtime.InteropServices;

/*
 * 文件名: SpectrometerService.cs
 * 描述: Avantes 光谱仪 SDK 的面向对象封装实现，处理非托管内存回调与多线程硬件访问。
 * 定义了操控单个光谱仪的方法
 * 维护指南: 必须保持 _callback 委托为类成员以规避 GC 回收引发的内存违例。
 */

namespace GD_ControlCenter_WPF.Services.Spectrometer
{
    /// <summary>
    /// 光谱仪单机服务实现类：负责单台设备的生命周期、指令同步及数据流解析。
    /// </summary>
    public partial class SpectrometerService : ObservableObject, ISpectrometerService, IDisposable
    {
        #region 1. 锁与底层资源管理相关字段

        /// <summary>
        /// 全局静态锁对象：用于跨实例保护底层非线程安全的 C++ SDK 指令调用（如启停指令）。
        /// </summary>
        private static readonly object _globalSdkLock = new object();

        /// <summary>
        /// 实例级信号量：保护单台设备的状态机，防止参数更新与测量指令发生冲突。
        /// </summary>
        private readonly SemaphoreSlim _hardwareLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// 非托管回调委托：必须作为全局成员持有，防止被 .NET 垃圾回收器回收导致 C++ 访问空指针。
        /// </summary>
        private readonly MeasurementCompletedCallback _callback;

        /// <summary>
        /// 异步任务源：用于将异步回调结果（Event）转化为可等待的任务（Task）。
        /// </summary>
        private TaskCompletionSource<SpectralData>? _measureTcs;

        /// <summary>
        /// 持续测量模式的生命周期控制，用于安全中断内部异步逻辑。
        /// </summary>
        private CancellationTokenSource? _continuousCts;

        /// <summary>
        /// 内部标志位：指示当前是否正在执行单次触发测量。
        /// </summary>
        private bool _isSingleMeasuring = false;

        /// <summary>
        /// 内部标志位：指示对象是否已被销毁。
        /// </summary>
        private bool _isDisposed = false;

        #endregion

        #region 2. 接口属性与事件实现

        /// <summary>
        /// 数据准备就绪事件：在非托管线程触发，订阅者需自行处理线程上下文切换。
        /// </summary>
        public event Action<SpectralData>? DataReady;

        /// <summary>
        /// 硬件配置与状态模型。
        /// </summary>
        public SpectrometerConfig Config { get; }

        /// <summary>
        /// 实时指示设备是否处于持续采集状态。
        /// </summary>
        public bool IsMeasuring { get; private set; }

        /// <summary>
        /// 构造光谱仪服务实例。
        /// </summary>
        /// <param name="config">光谱仪初始配置实体。</param>
        public SpectrometerService(SpectrometerConfig config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));

            // 将私有方法绑定到委托成员，固定其内存地址
            _callback = OnMeasurementCompleted;
        }

        #endregion

        #region 3. 生命周期与配置管理逻辑

        /// <summary>
        /// 异步初始化硬件：包含句柄激活、16-bit ADC 协商及像素数探测。
        /// </summary>
        /// <returns>成功连接返回 true。</returns>
        public async Task<bool> InitializeAsync()
        {
            return await Task.Run(() =>
            {
                // 构建 SDK 识别结构体
                var identity = new AvantesSdk.AvsIdentityType
                {
                    m_SerialNumber = Config.SerialNumber,
                    m_UserFriendlyName = Config.DeviceName
                };

                // 调用激活函数，获取硬件操作句柄
                int handle = AvantesSdk.AVS_Activate(ref identity);
                if (handle < AvantesSdk.ERR_SUCCESS) return false;

                // ADC 分辨率协商：优先尝试 16位 (0-65535)，失败则降级到 14位 (0-16383)
                int resHighRes = AvantesSdk.AVS_UseHighResAdc(handle, true);
                if (resHighRes != AvantesSdk.ERR_SUCCESS)
                {
                    // 降级尝试
                    int resLowRes = AvantesSdk.AVS_UseHighResAdc(handle, false);
                    if (resLowRes != AvantesSdk.ERR_SUCCESS)
                    {
                        // 彻底失败时必须反激活句柄，否则串口/USB口将被死锁
                        AvantesSdk.AVS_Deactivate(handle);
                        return false;
                    }
                }

                // 回写配置参数
                Config.DeviceHandle = handle;
                Config.IsConnected = true;

                // 获取物理探测器像素总数（通常为 2048 或 4096）
                ushort pixels = 0;
                AvantesSdk.AVS_GetNumPixels(handle, ref pixels);
                // 设置停止像素索引（像素数 - 1）
                Config.StopPixel = (ushort)(pixels - 1);

                return true;
            });
        }

        /// <summary>
        /// 执行参数下发：将托管内存中的积分时间、平均次数、过曝检测等参数写入 SDK 寄存器。
        /// </summary>
        /// <returns>SDK 状态码。</returns>
        public int ApplyConfiguration()
        {
            // 构建底层测量配置结构体
            var measConfig = new AvantesSdk.MeasConfigType
            {
                m_StartPixel = Config.StartPixel,
                m_StopPixel = Config.StopPixel,
                m_IntegrationTime = Config.IntegrationTimeMs,
                m_NrAverages = Config.AveragingCount,
                m_SaturationDetection = (byte)(Config.IsSaturationDetectionEnabled ? 1 : 0),                
                m_Trigger = new AvantesSdk.TriggerType { m_Mode = 0, m_Source = 0, m_SourceType = 0 }   // 使用内部软件触发模式 (Mode=0)
            };

            return AvantesSdk.AVS_PrepareMeasure(Config.DeviceHandle, ref measConfig);
        }

        /// <summary>
        /// 异步安全更新参数
        /// </summary>
        public async Task<int> UpdateConfigurationAsync(float newIntegrationTime, uint newAverages)
        {
            // 单次测量正在执行时拒绝修改，防止 TaskCompletionSource 冲突
            if (_isSingleMeasuring) return -999;

            // 获取实例级独占锁，防止并发操作
            await _hardwareLock.WaitAsync();
            try
            {
                return await Task.Run(async () =>
                {
                    bool wasMeasuring = IsMeasuring;

                    // 若正在采集，必须先停止硬件循环
                    if (wasMeasuring)
                    {
                        StopMeasurement();
                        // 给予底层驱动 100ms 释放 USB 事务的缓冲时间
                        await Task.Delay(100);
                    }

                    // 更新本地内存模型
                    Config.IntegrationTimeMs = newIntegrationTime;
                    Config.AveragingCount = newAverages;

                    // 执行下发指令
                    int result = ApplyConfiguration();

                    // 若更新前为采集状态且更新成功，则自动恢复持续采集
                    if (wasMeasuring && result == AvantesSdk.ERR_SUCCESS)
                    {
                        StartContinuousMeasurement();
                    }

                    return result;
                });
            }
            finally
            {
                _hardwareLock.Release();
            }
        }

        #endregion

        #region 4. 测量模式控制 (单次 / 持续)

        /// <summary>
        /// 异步执行单次采集。
        /// </summary>
        public async Task<SpectralData> MeasureOnceAsync(CancellationToken ct = default)
        {
            // 连接校验与互斥校验
            if (!Config.IsConnected || IsMeasuring || _isSingleMeasuring) return null!;

            _isSingleMeasuring = true;
            try
            {
                // 初始化异步结果通道
                _measureTcs = new TaskCompletionSource<SpectralData>();

                // 刷新参数
                ApplyConfiguration();

                // 获取非托管回调指针
                IntPtr pCallback = Marshal.GetFunctionPointerForDelegate(_callback);

                // 发起单次测量 (Count = 1)
                int result = AvantesSdk.AVS_MeasureCallback(Config.DeviceHandle, pCallback, 1);
                if (result != AvantesSdk.ERR_SUCCESS) return null!;

                // 绑定取消令牌：支持外部强制中断等待
                using (ct.Register(() => _measureTcs.TrySetCanceled()))
                {                    
                    return await _measureTcs.Task;  // 挂起并等待回调函数通过 _measureTcs 返回结果
                }
            }
            finally
            {               
                _isSingleMeasuring = false; // 重置状态锁
            }
        }

        /// <summary>
        /// 开启硬件持续采集流。
        /// </summary>
        public void StartContinuousMeasurement()
        {
            // 防止重复开启
            if (!Config.IsConnected || IsMeasuring) return;

            IsMeasuring = true;
            _continuousCts = new CancellationTokenSource();

            // 加全局静态锁防止多台设备同时执行启停导致 SDK 崩溃
            lock (_globalSdkLock)
            {
                ApplyConfiguration();
                IntPtr pCallback = Marshal.GetFunctionPointerForDelegate(_callback);

                // 发起无限次测量 (Count = -1)
                int result = AvantesSdk.AVS_MeasureCallback(Config.DeviceHandle, pCallback, -1);

                if (result != AvantesSdk.ERR_SUCCESS)
                {
                    IsMeasuring = false;
                }
            }
        }

        /// <summary>
        /// 停止硬件采集循环。
        /// </summary>
        public void StopMeasurement()
        {
            if (!Config.IsConnected || !IsMeasuring) return;

            IsMeasuring = false;

            // 销毁取消令牌
            if (_continuousCts != null)
            {
                _continuousCts.Cancel();
                _continuousCts.Dispose();
                _continuousCts = null;
            }

            // 加全局静态锁保护 SDK 指令
            lock (_globalSdkLock)
            {
                AvantesSdk.AVS_StopMeasure(Config.DeviceHandle);
            }
        }

        #endregion

        #region 5. 底层 C++ 回调与数据解析核心

        /// <summary>
        /// 硬件回调入口：此方法由 C++ 后台线程直接调用，严禁在此执行 UI 操作或长时间阻塞。
        /// </summary>
        /// <param name="handle">触发事件的设备句柄。</param>
        /// <param name="status">执行状态码 (0 为 ERR_SUCCESS)。</param>
        private void OnMeasurementCompleted(ref int handle, ref int status)
        {
            // 路由 A：单次测量模式逻辑
            if (_measureTcs != null && !_measureTcs.Task.IsCompleted)
            {
                ProcessAndSetTcs(handle, status);
                return;
            }

            // 路由 B：持续测量模式逻辑 (需校验运行状态)
            if (!IsMeasuring) return;

            if (status == AvantesSdk.ERR_SUCCESS)
            {
                // 从非托管内存提取数据
                var data = FetchSpectralData(handle);
                if (data != null)
                {
                    // 通过事件向业务层推送数据包
                    DataReady?.Invoke(data);
                }
            }
        }

        /// <summary>
        /// 从 SDK 缓冲区提取波长映射表与光强计数表。
        /// </summary>
        /// <param name="handle">硬件句柄。</param>
        /// <returns>封装好的光谱实体模型。</returns>
        private SpectralData? FetchSpectralData(int handle)
        {
            try
            {
                uint timestamp = 0;

                // 声明接收非托管数组的托管封装结构体
                var wavelengths = new AvantesSdk.PixelArrayType();
                var intensities = new AvantesSdk.PixelArrayType();

                // 提取波长校准数组与原始 Scope 数据
                int resLambda = AvantesSdk.AVS_GetLambda(handle, ref wavelengths);
                int resData = AvantesSdk.AVS_GetScopeData(handle, ref timestamp, ref intensities);

                // 仅当两个底层接口均成功返回时才进行数据裁剪
                if (resLambda == AvantesSdk.ERR_SUCCESS && resData == AvantesSdk.ERR_SUCCESS)
                {
                    // 根据传感器有效像素边界进行裁剪 (防止读取末尾的无效内存位 0 )
                    int validLength = Config.StopPixel + 1;

                    double[] validWavelengths = wavelengths.Value.Take(validLength).ToArray();
                    double[] validIntensities = intensities.Value.Take(validLength).ToArray();

                    // 实例化 DTO 实体返回
                    return new SpectralData(validWavelengths, validIntensities, Config.SerialNumber);
                }
            }
            catch (Exception)
            { }

            return null;
        }

        /// <summary>
        /// 单次测量专用处理：提取数据并终结挂起的异步任务隧道。
        /// </summary>
        private void ProcessAndSetTcs(int handle, int status)
        {
            // 状态异常时，向 Task 注入 null 防止外部 await 永久挂起
            if (status != AvantesSdk.ERR_SUCCESS)
            {
                _measureTcs?.TrySetResult(null!);
                return;
            }

            var data = FetchSpectralData(handle);
            _measureTcs?.TrySetResult(data!);
        }

        #endregion

        #region 6. 资源释放逻辑

        /// <summary>
        /// 释放硬件资源。包含停止采集、反激活句柄及注销事件订阅。
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;
         
            StopMeasurement();  // 物理停机

            // 释放 SDK 占用的 USB 资源句柄
            if (Config.DeviceHandle != -1)
            {
                AvantesSdk.AVS_Deactivate(Config.DeviceHandle);
            }

            // 断开消息链
            DataReady = null;
            _isDisposed = true;
        }

        #endregion
    }
}