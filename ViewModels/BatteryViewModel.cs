using CommunityToolkit.Mvvm.ComponentModel;
using GD_ControlCenter_WPF.Services;
using System.Windows;

/*
 * 文件名: BatteryViewModel.cs
 * 描述: 电池视图模型类，负责将底层 BatteryService 的实时状态（电量、在线状态、充电工况）转换为 UI 可绑定的属性。
 * 本类通过订阅服务的事件实现被动更新，并利用 Dispatcher 确保跨线程 UI 刷新的安全性。
 * 维护指南: 
 * 1. 属性变更通知依赖于 ObservableProperty 生成的 partial 成员。
 * 2. 状态同步逻辑集中在 SyncStatus 方法中，修改物理量映射时需同步检查该方法。
 */

namespace GD_ControlCenter_WPF.ViewModels
{
    /// <summary>
    /// 电池状态视图模型：连接硬件服务与前端页面的电量显示组件。
    /// </summary>
    public partial class BatteryViewModel : ObservableObject
    {
        /// <summary>
        /// 关联的电池业务服务引用。
        /// </summary>
        private readonly BatteryService _batteryService;

        /// <summary>
        /// 电池剩余电量百分比 (0-100)。对应 UI 进度条或数值显示。
        /// </summary>
        [ObservableProperty]
        private int _percentage;

        /// <summary>
        /// 电池通讯在线状态标识。用于控制 UI 离线图标的可见性。
        /// </summary>
        [ObservableProperty]
        private bool _isOnline;

        /// <summary>
        /// 充电状态标识。True 表示正在充电，False 表示放电或静置。
        /// </summary>
        [ObservableProperty]
        private bool _isCharging;

        /// <summary>
        /// 构造函数：注入服务实例，挂载事件监听并启动监控。
        /// </summary>
        /// <param name="batteryService">电池业务服务实例。</param>
        public BatteryViewModel(BatteryService batteryService)
        {
            _batteryService = batteryService;

            // 订阅底层服务的状态变更通知
            _batteryService.StatusUpdated += OnStatusUpdated;

            // 执行首次数据同步
            SyncStatus();

            // 激活服务内部的定时查询逻辑
            _batteryService.Start();
        }

        /// <summary>
        /// 响应服务层的状态更新事件。
        /// 职责：将后台线程触发的事件调度至主线程执行属性赋值。
        /// </summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void OnStatusUpdated(object? sender, EventArgs e)
        {
            // 利用 WPF Dispatcher 确保在 UI 线程执行 SyncStatus，防止跨线程操作异常
            Application.Current.Dispatcher.Invoke(SyncStatus);
        }

        /// <summary>
        /// 核心同步逻辑：从服务层抓取最新物理状态并赋值给本地 Observable 属性。
        /// </summary>
        private void SyncStatus()
        {
            Percentage = _batteryService.Percentage;
            IsOnline = _batteryService.IsOnline;
            IsCharging = _batteryService.IsCharging;
        }
    }
}