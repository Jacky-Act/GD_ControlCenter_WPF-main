using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Services;

/*
 * 文件名: MainViewModel.cs
 * 描述: 系统主视图模型。作为全局 UI 的顶层容器，负责多页面（UserControl）的导航调度、
 * 仪表盘布局高度的管理，以及子模块 ViewModel 的生命周期维护。
 * 维护指南: 
 * 1. 所有的主页面 ViewModel 均通过构造函数由 DI 容器注入。
 * 2. 页面切换通过修改 CurrentPage 属性实现，前端利用 DataTemplate 自动进行视图映射。
 * 3. SaveCurrentSettings 方法用于在特定时机执行全局参数的紧急快照保存。
 */

namespace GD_ControlCenter_WPF.ViewModels
{
    /// <summary>
    /// 主界面中枢视图模型：管理程序主框架的状态与导航流转。
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        #region 1. UI 导航与布局属性

        /// <summary>
        /// 当前活动页面的 ViewModel 对象。
        /// 绑定至 MainWindow 的 ContentPresenter，驱动界面在不同功能模块间切换。
        /// </summary>
        [ObservableProperty] private object _currentPage;

        /// <summary>
        /// 全局仪表盘卡片区域的高度快照。
        /// 用于在不同子页面间保持 UI 布局比例的一致性。
        /// </summary>
        [ObservableProperty] private double _dashboardHeight;

        /// <summary>
        /// 缓存所有页面的 ViewModel，供前台 ItemsControl 生成并常驻 UI (空间换时间解决卡顿)。
        /// </summary>
        public System.Collections.Generic.List<object> AllPages { get; } = new();

        #endregion

        #region 2. 核心子视图模型 (由容器注入)

        /// <summary> 获取主控制面板视图模型引用。 </summary>
        public ControlPanelViewModel ControlPanelVM { get; }

        /// <summary> 获取系统设置页面视图模型引用。 </summary>
        public SettingsViewModel SettingsVM { get; }

        /// <summary> 获取多通道时序图视图模型引用。 </summary>
        public TimeSeriesViewModel TimeSeriesVM { get; }

        #endregion

        #region 3. 辅助功能视图模型 (手动实例化)

        /// <summary> 获取元素谱线库配置视图模型。 </summary>
        public ElementConfigViewModel ElementConfigVM { get; } 

        /// <summary> 获取样本测量业务视图模型。 </summary>
        public SampleMeasurementViewModel SampleMeasurementVM { get; } 
        /// <summary> 获取样品序列业务视图模型。 </summary>
        public SampleSequenceViewModel SampleSequenceVM { get; }

        // <summary> 获取测样分析业务视图模型。 </summary>
        public AnalysisWorkstationViewModel AnalysisWorkstationVM { get; }
        // <summary> 获取数据处理业务视图模型。 </summary>
        public DataProcessingViewModel DataProcessingVM { get; }
        
        // <summary> 获取报告生成业务视图模型。 </summary>
        public ReportGenerationViewModel ReportGenerationVM { get; }


        #endregion

        #region 4. 私有依赖服务

        /// <summary> 高压硬件业务服务引用。 </summary>
        private readonly HighVoltageService _hvService;

        /// <summary> JSON 配置持久化服务引用。 </summary>
        private readonly JsonConfigService _configService;

        /// <summary> 通讯协议解析服务。 </summary>
        private readonly ProtocolService _protocolService;

        #endregion

        #region 5. 初始化与构造

        /// <summary>
        /// 构造主视图模型。
        /// 接收由 App.xaml.cs 构建的单例服务与视图模型实例。
        /// </summary>
        public MainViewModel(ControlPanelViewModel controlPanelVM, SettingsViewModel settingsVM, TimeSeriesViewModel timeSeriesVM, ElementConfigViewModel elementConfigVM, SampleSequenceViewModel sampleSequenceVM, SampleMeasurementViewModel sampleMeasurementVM, 
            AnalysisWorkstationViewModel analysisWorkstationVM, DataProcessingViewModel dataProcessingVM, ReportGenerationViewModel reportergenerationVM, HighVoltageService hvService, JsonConfigService configService, ProtocolService protocolService)
        {
            ControlPanelVM = controlPanelVM;
            SettingsVM = settingsVM;
            TimeSeriesVM = timeSeriesVM;
            ElementConfigVM = elementConfigVM;
            SampleSequenceVM = sampleSequenceVM;
            SampleMeasurementVM = sampleMeasurementVM;
            AnalysisWorkstationVM = analysisWorkstationVM;
            DataProcessingVM = dataProcessingVM;
            ReportGenerationVM = reportergenerationVM;

            _hvService = hvService;
            _configService = configService;
            _protocolService = protocolService;

            // 为了实现界面缓存(0秒切换)，将所有主页面加入缓冲列表
            AllPages.Add(ControlPanelVM);
            AllPages.Add(TimeSeriesVM);
            AllPages.Add(ElementConfigVM);
            AllPages.Add(SampleSequenceVM);
            AllPages.Add(AnalysisWorkstationVM);
            AllPages.Add(DataProcessingVM);
            AllPages.Add(ReportGenerationVM);
            AllPages.Add(SettingsVM);

            // 软件启动后默认展示主控制面板
            _currentPage = ControlPanelVM;

            // 监听导航请求消息
            WeakReferenceMessenger.Default.Register<NavigateMessage>(this, (r, m) =>
            {
                if (m.Value == "AnalysisWorkstation")
                {
                    CurrentPage = AnalysisWorkstationVM;
                }
            });
        }

        #endregion

        #region 6. 全局业务逻辑

        /// <summary>
        /// 执行当前关键硬件设置的持久化落盘。
        /// 将高压电源当前的实时输出值（电压/电流）保存至配置文件，防止由于异常关机导致的配置丢失。
        /// </summary>
        public void SaveCurrentSettings()
        {
            var config = new AppConfig
            {
                LastHvVoltage = _hvService.Voltage,
                LastHvCurrent = _hvService.Current
            };
            _configService.Save(config);
        }

        #endregion

    }
}