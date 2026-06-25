using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Models.Platform3D;
using GD_ControlCenter_WPF.Services.Commands;

/*
 * 文件名: Platform3DService.cs
 * 描述: 三维平台控制服务的具体实现类。
 * 本服务通过监听底层 8 字节位置反馈帧实现物理限位拦截；生成移动控制命令。
 * 维护指南: 
 * 1. 移动延时由 CalculateMoveDelay 计算，其经验公式 (step / 500) * 1000 + 500 需根据电机步进频率微调。
 * 2. Z 轴软限位支持 -2000 的特殊负向行程（适配左边的机器），只有手动模式下起作用；根据实际调整。
 */

namespace GD_ControlCenter_WPF.Services.Platform3D
{
    /// <summary>
    /// 三维平台控制服务：实现包含安全限位保护、坐标自动对齐及持久化记忆的运动控制逻辑。
    /// </summary>
    public class Platform3DService : IPlatform3DService, IDisposable
    {
        /// <summary>
        /// 串口服务引用，用于下发物理移动指令。
        /// </summary>
        private readonly ISerialPortService _serialPortService;

        /// <summary>
        /// 全局配置服务，用于读写 AppConfig 中的坐标记忆。
        /// </summary>
        private readonly JsonConfigService _jsonConfigService;

        /// <summary>
        /// 实例内部互斥锁，防止单轴任务并发导致的状态机混乱。
        /// </summary>
        private readonly object _lockObj = new();

        /// <summary>
        /// 当前正在执行的移动任务取消令牌源。用于在碰到物理限位时瞬间中断异步延时。
        /// </summary>
        private CancellationTokenSource? _currentMoveCts;

        /// <summary>
        /// 平台当前实时脉冲坐标实体。
        /// </summary>
        public PlatformPosition CurrentPosition { get; } = new();

        /// <summary>
        /// 平台当前运行状态实体（含 IsMoving 及各轴限位标志）。
        /// </summary>
        public PlatformStatus Status { get; } = new();

        /// <summary>
        /// 构造函数：初始化通讯引用并自动加载历史坐标与限位状态。
        /// </summary>
        public Platform3DService(ISerialPortService serialPortService, JsonConfigService jsonConfigService)
        {
            _serialPortService = serialPortService;
            _jsonConfigService = jsonConfigService;

            // 启动时优先恢复历史坐标
            LoadPositionInternal();

            // 注册消息监听：接收来自底层 ProtocolService 解析出的 8 字节平台消息
            WeakReferenceMessenger.Default.Register<Platform3DMessage>(this, (r, m) =>
            {
                HandleHardwareResponse(m.Value);
            });
        }

        /// <summary>
        /// 初始化接口实现：同步内存与本地持久化配置。
        /// </summary>
        public Task InitializeAsync()
        {
            LoadPositionInternal();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 执行单轴异步移动。
        /// 包含运动前的软限位拦截、指令下发、以及受限位保护的异步时间等待。
        /// </summary>
        public async Task<bool> MoveAxisAsync(AxisType axis, int step, bool isPositive, CancellationToken ct = default)
        {
            // 安全预检：拦截超出软限位或已在物理限位点上的非法指令
            if (!CheckSafety(axis, step, isPositive)) return false;

            lock (_lockObj)
            {
                // 若当前已有轴在运动，则拒绝新指令（三轴互斥）
                if (Status.IsMoving) return false;
                Status.IsMoving = true;
            }

            // 级联取消令牌：将外部 ct 与内部限位触发逻辑关联
            _currentMoveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            try
            {
                // 向物理串口下发移动报文
                SendMoveCommand(axis, step, isPositive);

                // 核心限位保护等待：延时由步长估算。若移动中碰到限位，HandleBoundarySignal 会触发 _currentMoveCts.Cancel()
                await Task.Delay(CalculateMoveDelay(step), _currentMoveCts.Token);

                // 只有任务未被取消（即未碰壁），才执行软件坐标累加
                UpdateLocalPosition(axis, step, isPositive);
                await SavePositionInternalAsync();
                return true;
            }
            catch (OperationCanceledException)
            {
                // 运动被物理信号强行打断，不执行常规坐标累加逻辑
                return false;
            }
            finally
            {
                // 无论何种结果，均复位运动状态并释放令牌
                Status.IsMoving = false;
                _currentMoveCts?.Dispose();
                _currentMoveCts = null;
            }
        }

        /// <summary>
        /// 强制复位移动状态标识。
        /// </summary>
        public void StopAll()
        {
            Status.IsMoving = false;
        }

        #region 内部硬件与信号处理逻辑

        /// <summary>
        /// 解析底层反馈的 8 字节平台响应帧。
        /// </summary>
        /// <param name="rawData">原始字节数组。</param>
        private void HandleHardwareResponse(byte[] rawData)
        {
            if (rawData == null || rawData.Length < 8) return;

            // 字节索引 6 存储了轴状态信息：A/B/C 对应 X/Y/Z，末尾 1 对应 Min，3 对应 Max
            switch (rawData[6])
            {
                case 0xA1: HandleBoundarySignal(AxisType.X, true); break;  // X 轴触发 Min (零点)
                case 0xA3: HandleBoundarySignal(AxisType.X, false); break; // X 轴触发 Max
                case 0xB1: HandleBoundarySignal(AxisType.Y, true); break;  // Y 轴触发 Min
                case 0xB3: HandleBoundarySignal(AxisType.Y, false); break; // Y 轴触发 Max
                case 0xC1: HandleBoundarySignal(AxisType.Z, true); break;  // Z 轴触发 Min
                case 0xC3: HandleBoundarySignal(AxisType.Z, false); break; // Z 轴触发 Max
            }
        }

        /// <summary>
        /// 物理限位信号处理核心逻辑：修正坐标、保存状态并中断运动。
        /// </summary>
        /// <param name="axis">触发限位的轴。</param>
        /// <param name="isZeroPosition">是否为零点（Min）限位。</param>
        public void HandleBoundarySignal(AxisType axis, bool isZeroPosition)
        {
            // 防抖：若状态已一致，则忽略重复报文
            if (isZeroPosition && Status.IsAtMin[axis]) return;
            if (!isZeroPosition && Status.IsAtMax[axis]) return;

            // 通过取消令牌瞬间中止 MoveAxisAsync 中的 Task.Delay，防止坐标错误累加
            _currentMoveCts?.Cancel();

            // 直接将软件坐标重置为物理绝对边界值
            if (isZeroPosition)
            {
                CurrentPosition[axis] = 0;
                Status.IsAtMin[axis] = true;
                Status.IsAtMax[axis] = false;

                // 新增：一旦收到 Z 轴零点信号，激活负向软限位逻辑
                if (axis == AxisType.Z)
                {
                    Status.HasReceivedZZero = true;
                }
            }
            else
            {
                CurrentPosition[axis] = GetMaxStep(axis);
                Status.IsAtMin[axis] = false;
                Status.IsAtMax[axis] = true;
            }

            // 后台异步保存修正后的坐标与状态
            _ = SavePositionInternalAsync();

            // 发送高阶业务消息，驱动 ViewModel 更新坐标 UI 或弹出限位告警
            WeakReferenceMessenger.Default.Send(new PlatformBoundaryMessage(axis, isZeroPosition));
        }

        /// <summary>
        /// 调用工厂类构建移动指令并发送。
        /// </summary>
        private void SendMoveCommand(AxisType axis, int step, bool isPositive)
        {
            var cmd = ControlCommandFactory.CreatePlatformMove((int)axis, isPositive, step);
            _serialPortService.Send(cmd);
        }

        /// <summary>
        /// 运动前的软/硬限位安全检查逻辑。
        /// </summary>
        /// <returns>若检测到风险返回 False 以拦截运动。</returns>
        private bool CheckSafety(AxisType axis, int step, bool isPositive)
        {
            if (!isPositive)
            {
                if (axis == AxisType.Z)
                {
                    // 只有在收到硬件零点信号后，才执行 -2000 的软限位拦截
                    if (Status.HasReceivedZZero)
                    {
                        if (CurrentPosition[axis] - step < PlatformLimits.MinStepZ)
                        {
                            return false;
                        }
                    }
                }
                else
                {
                    // X/Y 轴常规零点限位拦截
                    if (Status.IsAtMin[axis]) return false;
                }
            }

            // 正向最大值硬限位拦截
            if (Status.IsAtMax[axis] && isPositive) return false;

            return true;
        }

        /// <summary>
        /// 内部逻辑坐标累加。仅在确认运动未碰壁、未被取消时调用。
        /// </summary>
        private void UpdateLocalPosition(AxisType axis, int step, bool isPositive)
        {
            int delta = isPositive ? step : -step;
            CurrentPosition[axis] += delta;

            // 既然已经移动了，说明暂时脱离了相反方向的物理限位
            if (isPositive) Status.IsAtMin[axis] = false;
            else Status.IsAtMax[axis] = false;
        }

        /// <summary>
        /// 获取轴对应的硬编码物理最大行程。
        /// </summary>
        private int GetMaxStep(AxisType axis) => axis switch
        {
            AxisType.X => PlatformLimits.MaxStepX,
            AxisType.Y => PlatformLimits.MaxStepY,
            AxisType.Z => PlatformLimits.MaxStepZ,
            _ => 0
        };

        /// <summary>
        /// 移动时间估算公式。
        /// </summary>
        private int CalculateMoveDelay(int step) => (step / 500) * 1000 + 300;

        #endregion

        #region 本地持久化逻辑

        /// <summary>
        /// 从 JsonConfigService 加载 AppConfig 记忆的历史坐标。
        /// </summary>
        private void LoadPositionInternal()
        {
            var config = _jsonConfigService.Load();
            if (config?.Platform3D == null) return;

            CurrentPosition.X = config.Platform3D.X;
            CurrentPosition.Y = config.Platform3D.Y;
            CurrentPosition.Z = config.Platform3D.Z;

            // 恢复限位触发记忆
            if (config.Platform3D.IsAtMin != null)
            {
                Status.IsAtMin[AxisType.X] = config.Platform3D.IsAtMin.GetValueOrDefault("X", false);
                Status.IsAtMin[AxisType.Y] = config.Platform3D.IsAtMin.GetValueOrDefault("Y", false);
                Status.IsAtMin[AxisType.Z] = config.Platform3D.IsAtMin.GetValueOrDefault("Z", false);

                // 新增：如果历史状态记录 Z 轴在零点，则认为零点已确立
                if (Status.IsAtMin[AxisType.Z])
                {
                    Status.HasReceivedZZero = true;
                }
            }

            if (config.Platform3D.IsAtMax != null)
            {
                Status.IsAtMax[AxisType.X] = config.Platform3D.IsAtMax.GetValueOrDefault("X", false);
                Status.IsAtMax[AxisType.Y] = config.Platform3D.IsAtMax.GetValueOrDefault("Y", false);
                Status.IsAtMax[AxisType.Z] = config.Platform3D.IsAtMax.GetValueOrDefault("Z", false);
            }
        }

        /// <summary>
        /// 将当前内存坐标同步回 AppConfig 并执行异步落盘。
        /// </summary>
        private Task SavePositionInternalAsync()
        {
            var config = _jsonConfigService.Load();

            if (config.Platform3D == null)
                config.Platform3D = new Platform3DConfig();

            config.Platform3D.X = CurrentPosition.X;
            config.Platform3D.Y = CurrentPosition.Y;
            config.Platform3D.Z = CurrentPosition.Z;

            config.Platform3D.IsAtMin["X"] = Status.IsAtMin[AxisType.X];
            config.Platform3D.IsAtMin["Y"] = Status.IsAtMin[AxisType.Y];
            config.Platform3D.IsAtMin["Z"] = Status.IsAtMin[AxisType.Z];

            config.Platform3D.IsAtMax["X"] = Status.IsAtMax[AxisType.X];
            config.Platform3D.IsAtMax["Y"] = Status.IsAtMax[AxisType.Y];
            config.Platform3D.IsAtMax["Z"] = Status.IsAtMax[AxisType.Z];

            // 异步执行配置文件的实体化保存
            _ = Task.Run(() => _jsonConfigService.Save(config));

            return Task.CompletedTask;
        }

        #endregion

        /// <summary>
        /// 销毁服务：注销所有消息订阅。
        /// </summary>
        public void Dispose() => WeakReferenceMessenger.Default.UnregisterAll(this);
    }
}