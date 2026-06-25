using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.ViewModels;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace GD_ControlCenter_WPF.Views.Pages
{
    public partial class SampleSequenceView : UserControl
    {
        public SampleSequenceView()
        {
            InitializeComponent();

            // 监听 ViewModel 发来的重绘列消息
            WeakReferenceMessenger.Default.Register<RebuildColumnsMessage>(this, (r, m) =>
            {
                Dispatcher.Invoke(() => RebuildDynamicColumns(m.Value));
            });
        }

        // 新增：每次界面加载时执行
        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // 获取关联的 ViewModel
            if (this.DataContext is SampleSequenceViewModel vm)
            {
                // 从 VM 中获取当前“大管家”里记录的已选元素名单
                // 注意：由于 ViewModel 里的 _activeElements 是私有的，
                // 我们需要在 ViewModel 里暴露一个属性，或者直接从关联的 ElementConfigVM 拿
                var currentElements = vm.ActiveElementNames;

                if (currentElements != null && currentElements.Count > 0)
                {
                    RebuildDynamicColumns(currentElements);
                }
            }
        }
        /// <summary>
        /// 核心黑魔法：动态生成元素浓度列
        /// </summary>
        private void RebuildDynamicColumns(System.Collections.Generic.List<string> activeElements)
        {
            // 1. 找到所有由代码动态生成的旧“浓度列”并删除它们
            var oldColumns = SequenceDataGrid.Columns.Where(c => c.Header != null && c.Header.ToString().EndsWith("标液浓度")).ToList();
            foreach (var col in oldColumns)
            {
                SequenceDataGrid.Columns.Remove(col);
            }

            // 2. 根据最新的大名单，为每个元素创建一列
            for (int i = 0; i < activeElements.Count; i++)
            {
                string elName = activeElements[i];

                var newCol = new DataGridTextColumn
                {
                    Header = $"{elName} 标液浓度",
                    // 这里极其精妙：由于内部存的是 ObservableCollection，我们直接按索引绑定它的 ConcentrationValue 属性！
                    // 这样输入的值会自动同步回后台，且支持双向触发！
                    Binding = new Binding($"ElementConcentrations[{i}].ConcentrationValue")
                    {
                        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                    },
                    Width = new DataGridLength(1, DataGridLengthUnitType.Star)
                };

                // 将新列追加到 DataGrid 的末尾
                SequenceDataGrid.Columns.Add(newCol);
            }
        }
    }
}