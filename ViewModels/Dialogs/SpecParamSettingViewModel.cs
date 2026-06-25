using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/*
 * 文件名: SpecParamSettingViewModel.cs
 * 描述: 通用的光谱参数（积分时间、平均次数）快捷设置对话框视图模型。
 * 维护指南: 参数最终通过 _applyAction 委托将修改后的数值以字符串形式回传给上层调用者。
 */

namespace GD_ControlCenter_WPF.ViewModels.Dialogs
{
    /// <summary>
    /// 光谱参数快捷设置视图模型。
    /// </summary>
    public partial class SpecParamSettingViewModel : ObservableObject
    {
        #region 1. 私有行为委托

        /// <summary>
        /// 关闭对话框的回调动作。
        /// </summary>
        private readonly Action _closeAction;

        /// <summary>
        /// 执行参数应用的回调动作，接收转换后的字符串参数。
        /// </summary>
        private readonly Action<string> _applyAction;

        #endregion

        #region 2. UI 绑定属性 (标题与量程控制)

        /// <summary>
        /// 对话框窗口左上角显示的标题文本。
        /// </summary>
        [ObservableProperty] private string _windowTitle;

        /// <summary>
        /// 内容区域大字显示的标题文本（如：参数配置 - 积分时间）。
        /// </summary>
        [ObservableProperty] private string _headerTitle;

        /// <summary>
        /// 当前正在编辑的物理数值。
        /// </summary>
        [ObservableProperty] private double _inputValue;

        /// <summary>
        /// 允许设定的物理数值下限（最小值）。
        /// </summary>
        [ObservableProperty] private double _minValue;

        /// <summary>
        /// 允许设定的物理数值上限（最大值）。
        /// </summary>
        [ObservableProperty] private double _maxValue;

        /// <summary>
        /// 每次点击增减按钮时的数值变化步进量。
        /// </summary>
        [ObservableProperty] private double _stepValue;

        #endregion

        #region 3. 初始化

        /// <summary>
        /// 构造函数：初始化参数量程、默认值及交互回调。
        /// </summary>
        /// <param name="windowTitle">窗口标题。</param>
        /// <param name="headerTitle">内容标题。</param>
        /// <param name="currentValue">当前初始值。</param>
        /// <param name="min">最小允许值。</param>
        /// <param name="max">最大允许值。</param>
        /// <param name="step">步进大小。</param>
        /// <param name="applyAction">点击应用时的回调。</param>
        /// <param name="closeAction">关闭窗口的回调。</param>
        public SpecParamSettingViewModel(string windowTitle, string headerTitle, double currentValue, double min, double max, double step, Action<string> applyAction, Action closeAction)
        {
            WindowTitle = windowTitle;
            HeaderTitle = headerTitle;
            InputValue = currentValue;
            MinValue = min;
            MaxValue = max;
            StepValue = step;
            _applyAction = applyAction;
            _closeAction = closeAction;
        }

        #endregion

        #region 4. 业务逻辑指令

        /// <summary>
        /// 增加数值指令。
        /// 逻辑：在不超过最大值的前提下累加步进量，否则锁定在最大值。
        /// </summary>
        [RelayCommand]
        private void Increase()
        {
            if (InputValue + StepValue <= MaxValue)
            {
                InputValue += StepValue;
            }
            else
            {
                InputValue = MaxValue;
            }
        }

        /// <summary>
        /// 减少数值指令。
        /// 逻辑：在不低于最小值的前提下递减步进量，否则锁定在最小值。
        /// </summary>
        [RelayCommand]
        private void Decrease()
        {
            if (InputValue - StepValue >= MinValue)
            {
                InputValue -= StepValue;
            }
            else
            {
                InputValue = MinValue;
            }
        }

        /// <summary>
        /// 应用并关闭指令。
        /// 逻辑：将当前数值通过委托回传并触发窗口关闭。
        /// </summary>
        [RelayCommand]
        private void Apply()
        {
            // 执行上层注入的应用逻辑
            _applyAction?.Invoke(InputValue.ToString());

            // 执行上层注入的关闭动作
            _closeAction?.Invoke();
        }

        #endregion
    }
}