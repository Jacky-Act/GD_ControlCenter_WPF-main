using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GD_ControlCenter_WPF.Models;
using GD_ControlCenter_WPF.Services;
using System.Windows;

/*
 * 文件名: SamplingSettingViewModel.cs
 * 模块: 业务弹窗视图模型 (Dialog ViewModels)
 * 描述: 采样参数设定对话框的视图模型。
 * 负责管理自动化采样过程中的核心参数：采样总次数与单次采样之间的时间间隔。
 */

namespace GD_ControlCenter_WPF.ViewModels.Dialogs
{
    /// <summary>
    /// 采样设置弹窗视图模型：处理采样频率与规模的参数交互与校验。
    /// </summary>
    public partial class SamplingSettingViewModel : ObservableObject
    {
        #region 1. 依赖服务与私有字段

        /// <summary>
        /// JSON 配置持久化服务。
        /// </summary>
        private readonly JsonConfigService _configService;

        /// <summary>
        /// 关闭当前对话框的委托回调。
        /// </summary>
        private readonly Action _closeAction;

        #endregion

        #region 2. UI 绑定属性

        /// <summary>
        /// 预设的采样总次数。
        /// 范围限制：1 - 10000。
        /// </summary>
        [ObservableProperty]
        private int _sampleCount;

        /// <summary>
        /// 两次连续采样之间的时间间隔（单位：秒）。
        /// 范围限制：1 - 3600s。
        /// </summary>
        [ObservableProperty]
        private int _sampleInterval;

        #endregion

        #region 3. 初始化

        /// <summary>
        /// 构造函数：注入配置服务并根据历史记录初始化数值。
        /// </summary>
        /// <param name="configService">配置管理服务。</param>
        /// <param name="currentConfig">当前内存中的配置快照。</param>
        /// <param name="closeAction">关闭窗口的动作。</param>
        public SamplingSettingViewModel(JsonConfigService configService, AppConfig currentConfig, Action closeAction)
        {
            _configService = configService;
            _closeAction = closeAction;

            // 恢复逻辑：优先读取上次保存的次数，若数据异常则给予出厂默认值（11次）
            SampleCount = currentConfig.LastSampleCount > 0 ? currentConfig.LastSampleCount : 11;

            // 恢复逻辑：优先读取上次保存的间隔，若无记录则默认 1 秒
            SampleInterval = currentConfig.LastSampleInterval > 0 ? currentConfig.LastSampleInterval : 1;
        }

        #endregion

        #region 4. 参数增减控制命令

        /// <summary>
        /// 增加采样次数。上限拦截为 10000 次。
        /// </summary>
        [RelayCommand]
        private void IncreaseSampleCount()
        {
            if (SampleCount < 10000) SampleCount++;
        }

        /// <summary>
        /// 减少采样次数。下限拦截为 1 次。
        /// </summary>
        [RelayCommand]
        private void DecreaseSampleCount()
        {
            if (SampleCount > 1) SampleCount--;
        }

        /// <summary>
        /// 增加采样间隔秒数。上限拦截为 3600 秒（1小时）。
        /// </summary>
        [RelayCommand]
        private void IncreaseSampleInterval()
        {
            if (SampleInterval < 3600) SampleInterval++;
        }

        /// <summary>
        /// 减少采样间隔秒数。下限拦截为 0 秒（代表极速连续采样）。
        /// </summary>
        [RelayCommand]
        private void DecreaseSampleInterval()
        {
            if (SampleInterval > 0) SampleInterval--;
        }

        #endregion

        #region 5. 核心业务逻辑：应用与保存

        /// <summary>
        /// 执行参数应用逻辑。
        /// 包含合法性二次校验、持久化落盘以及通知界面关闭。
        /// </summary>
        [RelayCommand]
        private void Apply()
        {
            // 1. 业务逻辑校验：采样次数不能为空
            if (SampleCount < 1)
            {
                MessageBox.Show("采样次数必须至少为 1", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 2. 业务逻辑校验：间隔必须为正整数
            if (SampleInterval <= 0)
            {
                MessageBox.Show("采样间隔必须大于 0", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 3. 数据持久化：更新本地 config.json 文件
            var config = _configService.Load();
            config.LastSampleCount = SampleCount;
            config.LastSampleInterval = SampleInterval;
            _configService.Save(config);

            // 4. 回调视图层关闭弹窗
            _closeAction?.Invoke();
        }

        #endregion
    }
}