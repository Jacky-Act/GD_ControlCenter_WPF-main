using C_Sharp_Application;
using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Models.Spectrometer;
using GD_ControlCenter_WPF.Services.Spectrometer.Logic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Data;

/*
 * 文件名: SpectrometerManager.cs
 * 描述: 光谱仪全局管理类（单例），负责底层 SDK 初始化、多 USB 设备的扫描发现、生命周期管理以及核心数据路由。
 * 作为光谱仪硬件调度中心，它将多台物理光谱仪的数据汇总，执行饱和度过滤与多机数据拼接（Stitching）算法，并最终通过全局消息总线分发数据。
 * 维护指南: 
 * 1. 扫描设备时采用“两次探测法”调用 AVS_GetList，需确保内存分配与 SDK 返回大小匹配。
 * 2. 多机拼接采用“屏障同步”策略，需等待所有在线设备数据凑齐后方可执行数学运算。
 */

namespace GD_ControlCenter_WPF.Services.Spectrometer
{
    /// <summary>
    /// 光谱仪管理中心：负责硬件池的扫描、初始化、数据聚合与全局路由。
    /// </summary>
    public class SpectrometerManager : IDisposable
    {
        #region 1. 单例与并发基建

        /// <summary>
        /// 获取光谱仪管理器的全局唯一单例实例。
        /// </summary>
        public static SpectrometerManager Instance { get; } = new SpectrometerManager();

        /// <summary>
        /// 用于多线程环境下同步操作 ObservableCollection 的锁对象。
        /// </summary>
        private readonly object _devicesLock = new object();

        /// <summary>
        /// 暂存多台设备在同一采集周期内的光谱数据快照（Key 为序列号）。
        /// 用于多机联调场景下的数据对齐与拼接处理。
        /// </summary>
        private readonly ConcurrentDictionary<string, SpectralData> _latestDataCache = new();

        /// <summary>
        /// 标识底层动态库 SDK 是否已完成全局初始化。
        /// </summary>
        private bool _isInitialized = false;

        /// <summary>
        /// 当前系统中所有已在线并接入管理的单机光谱仪服务列表。
        /// </summary>
        public ObservableCollection<ISpectrometerService> Devices { get; } = new();

        /// <summary>
        /// 私有构造函数。配置跨线程集合同步，保障多线程添加设备时不引发 UI 崩溃。
        /// </summary>
        private SpectrometerManager()
        {
            // 启用 WPF 集合同步功能，允许在后台线程安全修改 Devices 集合
            BindingOperations.EnableCollectionSynchronization(Devices, _devicesLock);
        }

        #endregion

        #region 2. 生命周期与总线控制

        /// <summary>
        /// 异步扫描 USB 总线，初始化 SDK 并将新发现的物理设备同步至管理池。
        /// </summary>
        /// <returns>返回当前成功接入管理的光谱仪总数。初始化失败返回 -1。</returns>
        public async Task<int> DiscoverAndInitDevicesAsync()
        {
            return await Task.Run(() =>
            {
                // 全局 SDK 环境激活
                if (!_isInitialized)
                {
                    int initResult = AvantesSdk.AVS_Init(0); // 端口参数通常为 0
                    if (initResult < 0) return -1;
                    _isInitialized = true;
                }

                // 刷新 USB 设备列表
                AvantesSdk.AVS_UpdateUSBDevices();

                // 探测当前连接的设备数量与内存空间
                uint listSize = 0;
                uint requiredSize = 0;
                AvantesSdk.AVS_GetList(listSize, ref requiredSize, null!);

                if (requiredSize == 0) return 0;

                // 根据 SDK 要求分配内存并正式抓取设备身份信息
                var deviceList = new AvantesSdk.AvsIdentityType[requiredSize / (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(AvantesSdk.AvsIdentityType))];
                AvantesSdk.AVS_GetList(requiredSize, ref requiredSize, deviceList);

                // 执行增量同步，将底层结构体包装为服务实例
                SyncDevices(deviceList);

                return Devices.Count;
            });
        }

        /// <summary>
        /// 控制池内所有已初始化的光谱仪开启持续采集模式。
        /// </summary>
        public void StartAll()
        {
            foreach (var device in Devices)
            {
                device.StartContinuousMeasurement();             
                Thread.Sleep(50);   // 物理缓冲：避免大量 USB 开始采集指令瞬间挤压总线
            }
        }

        /// <summary>
        /// 控制池内所有在线设备停止采集。
        /// </summary>
        public void StopAll()
        {
            foreach (var device in Devices)
            {
                device.StopMeasurement();                
                Thread.Sleep(100);  // 给予底层句柄释放 IO 事务的时间
            }
        }

        /// <summary>
        /// 断开所有硬件连接，销毁所有服务实例并彻底卸载 SDK 全局资源。
        /// </summary>
        public void DisconnectAll()
        {
            StopAll();

            foreach (var device in Devices)
            {
                // 移除中央数据路由监听
                device.DataReady -= OnDeviceDataReady;
                device.Dispose();
            }

            // 彻底销毁 SDK 实例，释放 DLL 资源
            if (_isInitialized)
            {
                AvantesSdk.AVS_Done();
                _isInitialized = false;
            }

            Devices.Clear();
            _latestDataCache.Clear();
        }

        /// <summary>
        /// 资源清理。
        /// </summary>
        public void Dispose()
        {
            DisconnectAll();
        }

        #endregion

        #region 3. 设备同步与事件挂载

        /// <summary>
        /// 将扫描到的硬件标识数组同步至托管列表，并建立中央数据路由。
        /// </summary>
        /// <param name="newIdentities">底层回传的设备身份列表。</param>
        private void SyncDevices(AvantesSdk.AvsIdentityType[] newIdentities)
        {
            foreach (var identity in newIdentities)
            {
                // 已连接的序列号不重复添加
                if (Devices.Any(d => d.Config.SerialNumber == identity.m_SerialNumber))
                    continue;

                // 实例化设备配置与驱动服务
                var config = new SpectrometerConfig
                {
                    SerialNumber = identity.m_SerialNumber,
                    DeviceName = identity.m_UserFriendlyName
                };

                var service = new SpectrometerService(config);

                // 挂载统一的数据就绪回调，所有数据第一站都会流向 Manager
                service.DataReady += OnDeviceDataReady;

                Devices.Add(service);
            }
        }

        #endregion

        #region 4. 数据总线路由引擎

        /// <summary>
        /// 中央数据接收站：负责对所有设备产出的原始数据进行分流、安全检查与后处理。
        /// </summary>
        /// <param name="data">任意物理光谱仪产生的单次测量快照。</param>
        private void OnDeviceDataReady(SpectralData data)
        {
            // 安全预处理（饱和度过曝检测）
            HandleSaturationWarning(data);

            //路由分发（决定是单通道透传还是执行多机拼接）
            HandleDataDispatching(data);
        }

        /// <summary>
        /// 饱和度安全拦截：检测当前帧是否存在像素过曝，并向总线发送状态告警。
        /// </summary>
        /// <param name="data">待检测的光谱数据包。</param>
        private void HandleSaturationWarning(SpectralData data)
        {
            var sourceDevice = Devices.FirstOrDefault(d => d.Config.SerialNumber == data.SourceDeviceSerial);

            // 若开启了检测开关，则调用算法库检查 Y 轴峰值
            if (sourceDevice != null && sourceDevice.Config.IsSaturationDetectionEnabled)
            {
                if (SpectrometerLogic.CheckSaturation(data))
                {
                    // 通过消息总线通知 UI 层显示过曝图标或警告
                    WeakReferenceMessenger.Default.Send(new SpectrometerStatusMessage(sourceDevice.Config));
                }
            }
        }

        /// <summary>
        /// 数据分发分流器：根据当前在线设备数量决定输出模式。
        /// </summary>
        private void HandleDataDispatching(SpectralData data)
        {
            int deviceCount = Devices.Count;

            // 单机模式：直接输出原始数据
            if (deviceCount <= 1)
            {
                DispatchSingleDeviceData(data);
            }
            // 联机模式：执行异步屏障同步与数据拼接
            else
            {
                DispatchMultiDeviceStitchedData(data, deviceCount);
            }
        }

        /// <summary>
        /// 执行单机透传路由：将采集数据封装为消息包发送至全局。
        /// </summary>
        private void DispatchSingleDeviceData(SpectralData data)
        {
            var sourceConfig = Devices[0].Config;

            // 构建包含当前硬件配置上下文的消息
            WeakReferenceMessenger.Default.Send(new SpectralDataMessage(data)
            {
                IntegrationTime = sourceConfig.IntegrationTimeMs,
                AveragingCount = sourceConfig.AveragingCount
            });
        }

        /// <summary>
        /// 执行多机拼接路由：收集所有设备数据后执行波长对齐与拼接。
        /// </summary>
        /// <param name="data">当前到达的单帧数据。</param>
        /// <param name="totalOnlineDevices">参与拼接的设备总数。</param>
        private void DispatchMultiDeviceStitchedData(SpectralData data, int totalOnlineDevices)
        {
            // 将最新数据存入缓存
            _latestDataCache[data.SourceDeviceSerial] = data;

            // 只有当所有在线设备的数据都到达缓存时，才触发拼接数学运算
            if (_latestDataCache.Count >= totalOnlineDevices)
            {
                // 调用数学逻辑层执行多光谱段拼接
                var combinedData = SpectrometerLogic.PerformStitching(_latestDataCache.Values);

                if (combinedData != null)
                {
                    // 拼接后的消息参数统一参照主设备（Index 0）
                    var mainConfig = Devices[0].Config;
                    WeakReferenceMessenger.Default.Send(new SpectralDataMessage(combinedData)
                    {
                        IntegrationTime = mainConfig.IntegrationTimeMs,
                        AveragingCount = mainConfig.AveragingCount
                    });
                }

                // 清理缓存，为下一个同步周期做准备
                _latestDataCache.Clear();
            }
        }

        #endregion
    }
}