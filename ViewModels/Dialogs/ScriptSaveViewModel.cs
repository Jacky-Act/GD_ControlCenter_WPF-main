using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Services;
using System.Windows;

/*
 * 文件名: ScriptSaveViewModel.cs
 * 描述: 自动化脚本保存配置弹窗的视图模型。
 * 负责管理保存任务的核心参数（次数、频率、存储路径），执行本地持久化，并通过全局消息中心与后台采集任务保持状态同步。
 * 维护指南: 
 * 1. 跨窗口同步通过 ScriptSaveStateChangedMessage 实现，确保弹窗状态与主界面采集引擎一致。
 * 2. 文件夹选择功能依赖于 Windows Forms 的 FolderBrowserDialog。
 * 3. 参数校验逻辑集中在 SaveConfigToLocal 方法中，物理边界由 UI 命令层拦截。
 */

namespace GD_ControlCenter_WPF.ViewModels.Dialogs
{
    /// <summary>
    /// 脚本保存设置视图模型：处理自动化采集任务的参数交互、校验与持久化。
    /// </summary>
    public partial class ScriptSaveViewModel : ObservableObject
    {
        #region 1. 依赖注入与回调行为

        /// <summary>
        /// JSON 配置管理服务。
        /// </summary>
        private readonly JsonConfigService _configService;

        /// <summary>
        /// 窗口关闭回调委托。
        /// 用于在业务逻辑完成后通知视图层销毁当前对话框。
        /// </summary>
        private readonly Action _closeAction;

        /// <summary>
        /// 启停主控保存逻辑的回调委托。
        /// 用于触发主界面中定义的自动化采集生命周期。
        /// </summary>
        private readonly Action _toggleSaveAction;

        #endregion

        #region 2. 核心运行状态与 UI 动态绑定

        /// <summary>
        /// 当前是否正在执行后台脚本保存任务。
        /// 变更时会自动刷新 ToggleBtnText、ToggleBtnColor 等派生属性。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotSaving))]
        [NotifyPropertyChangedFor(nameof(ToggleBtnText))]
        [NotifyPropertyChangedFor(nameof(ToggleBtnColor))]
        private bool _isScriptSaving;

        /// <summary>
        /// 标识是否未在保存中。
        /// 用于在运行期间锁定 UI 输入控件 (IsEnabled 绑定)。
        /// </summary>
        public bool IsNotSaving => !IsScriptSaving;

        /// <summary>
        /// 启停按钮的动态文本描述。
        /// </summary>
        public string ToggleBtnText => IsScriptSaving ? "停止保存" : "启动保存";

        /// <summary>
        /// 启停按钮的动态背景颜色（十六进制字符串）。
        /// 运行中为红色报警色，待机时为品牌主色调。
        /// </summary>
        public string ToggleBtnColor => IsScriptSaving ? "#F44336" : "#673ab7";

        #endregion

        #region 3. 脚本存储配置参数

        /// <summary>
        /// 预设的连续保存帧数。
        /// </summary>
        [ObservableProperty]
        private int _scriptSaveCount;

        /// <summary>
        /// 两次保存动作之间的时间间隔（单位：秒）。
        /// </summary>
        [ObservableProperty]
        private int _scriptSaveInterval;

        /// <summary>
        /// 目标存储文件夹的绝对物理路径。
        /// </summary>
        [ObservableProperty]
        private string _scriptSaveDirectory = string.Empty;

        #endregion

        #region 4. 初始化与跨窗口消息监听

        /// <summary>
        /// 构造脚本保存视图模型。
        /// 执行参数恢复、状态同步及全局消息订阅。
        /// </summary>
        /// <param name="configService">配置服务实例。</param>
        /// <param name="currentConfig">当前全局配置快照。</param>
        /// <param name="isCurrentlySaving">主界面当前的保存状态标识。</param>
        /// <param name="closeAction">对话框关闭委托。</param>
        /// <param name="toggleSaveAction">采集任务控制委托。</param>
        public ScriptSaveViewModel(JsonConfigService configService, AppConfig currentConfig, bool isCurrentlySaving,
            Action closeAction, Action toggleSaveAction)
        {
            _configService = configService;
            _closeAction = closeAction;
            _toggleSaveAction = toggleSaveAction;
            IsScriptSaving = isCurrentlySaving;

            // 初始化参数加载与物理有效性检查
            ScriptSaveCount = currentConfig.ScriptSaveCount > 0 ? currentConfig.ScriptSaveCount : 100;
            ScriptSaveInterval = currentConfig.ScriptSaveInterval > 0 ? currentConfig.ScriptSaveInterval : 5;

            // 路径初始化：若无记录则默认定位至系统桌面
            ScriptSaveDirectory = string.IsNullOrWhiteSpace(currentConfig.ScriptSaveDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                : currentConfig.ScriptSaveDirectory;

            // 注册跨窗口状态同步：监听后台采集引擎的意外中止或计划完成
            WeakReferenceMessenger.Default.Register<ScriptSaveStateChangedMessage>(this, (r, m) =>
            {
                // 通过 Dispatcher 确保在主 UI 线程安全更新 ObservableProperty
                Application.Current.Dispatcher.Invoke(() =>
                {
                    IsScriptSaving = m.Value;
                });
            });
        }

        #endregion

        #region 5. 参数增减控制命令 (UI 命令层拦截)

        /// <summary>
        /// 增加保存次数。上限限制为 10000 帧。
        /// </summary>
        [RelayCommand]
        private void IncreaseCount() { if (ScriptSaveCount < 10000) ScriptSaveCount++; }

        /// <summary>
        /// 减少保存次数。下限限制为 1 帧。
        /// </summary>
        [RelayCommand]
        private void DecreaseCount() { if (ScriptSaveCount > 1) ScriptSaveCount--; }

        /// <summary>
        /// 增加保存间隔秒数。上限限制为 3600 秒（1小时）。
        /// </summary>
        [RelayCommand]
        private void IncreaseInterval() { if (ScriptSaveInterval < 3600) ScriptSaveInterval++; }

        /// <summary>
        /// 减少保存间隔秒数。下限限制为 1 秒，防止 0 间隔引发 IO 堆塞。
        /// </summary>
        [RelayCommand]
        private void DecreaseInterval() { if (ScriptSaveInterval > 1) ScriptSaveInterval--; }

        #endregion

        #region 6. 业务提交与持久化逻辑

        /// <summary>
        /// 弹出系统文件夹浏览器，用于用户手动选择存储目标。
        /// 备注：依赖于 System.Windows.Forms 互操作。
        /// </summary>
        [RelayCommand]
        private void BrowseDirectory()
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择脚本保存目录",
                UseDescriptionForTitle = true,
                SelectedPath = ScriptSaveDirectory
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                ScriptSaveDirectory = dialog.SelectedPath;
            }
        }

        /// <summary>
        /// 执行核心持久化逻辑：将当前配置写入本地磁盘。
        /// </summary>
        /// <returns>若参数合法且保存成功则返回 true。</returns>
        private bool SaveConfigToLocal()
        {
            // 物理边界二次防御
            if (ScriptSaveCount < 1 || ScriptSaveInterval <= 0)
            {
                MessageBox.Show("保存次数和间隔必须大于 0", "参数校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var config = _configService.Load();
            config.ScriptSaveCount = ScriptSaveCount;
            config.ScriptSaveInterval = ScriptSaveInterval;
            config.ScriptSaveDirectory = ScriptSaveDirectory;
            _configService.Save(config);

            return true;
        }

        /// <summary>
        /// 响应“应用”按钮点击：仅同步配置并关闭窗口。
        /// </summary>
        [RelayCommand]
        private void Apply()
        {
            if (SaveConfigToLocal())
            {
                _closeAction?.Invoke();
            }
        }

        /// <summary>
        /// 响应“启动/停止”切换请求。
        /// 在启动前执行强制校验，随后触发主界面业务逻辑并关闭当前弹窗。
        /// </summary>
        [RelayCommand]
        private void ToggleSave()
        {
            // 准入逻辑：仅在启动采集前执行校验，停止指令直接透传
            if (!IsScriptSaving)
            {
                if (!SaveConfigToLocal()) return;
            }

            // 执行主界面注册的启停业务
            _toggleSaveAction?.Invoke();

            // 关闭交互窗口
            _closeAction?.Invoke();
        }

        #endregion
    }
}