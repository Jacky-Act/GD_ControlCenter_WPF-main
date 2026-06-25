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

        private void ComboBox_DropDownOpened(object sender, System.EventArgs e)
        {
            var vm = this.DataContext as GD_ControlCenter_WPF.ViewModels.SampleSequenceViewModel;
            vm?.RefreshTemplatesCommand.Execute(null);
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
            var fixedHeaders = new string[] { "样品名称", "样品类型", "状态" };
            var oldColumns = SequenceDataGrid.Columns.Where(c => c.Header != null && !fixedHeaders.Contains(c.Header.ToString())).ToList();
            foreach (var col in oldColumns)
            {
                SequenceDataGrid.Columns.Remove(col);
            }

            // 寻找插入位置（在“样品类型”之后）
            int typeIndex = -1;
            for (int i = 0; i < SequenceDataGrid.Columns.Count; i++)
            {
                if (SequenceDataGrid.Columns[i].Header?.ToString() == "样品类型")
                {
                    typeIndex = i;
                    break;
                }
            }
            int insertPos = typeIndex >= 0 ? typeIndex + 1 : SequenceDataGrid.Columns.Count - 1; // 兜底：插在状态列之前

            // 2. 根据最新的大名单，为每个元素创建一列
            for (int i = 0; i < activeElements.Count; i++)
            {
                string elName = activeElements[i];

                // 使用 XamlReader 动态生成携带触发器的 DataTemplate
                string cellTemplateXaml = $@"
<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
              xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
              xmlns:md=""clr-namespace:MaterialDesignThemes.Wpf;assembly=MaterialDesignThemes.Wpf"">
    <Grid Background=""Transparent"">
        <TextBox x:Name=""EditTxt"" 
                 Text=""{{Binding ElementConcentrations[{i}].ConcentrationValue, UpdateSourceTrigger=PropertyChanged}}"" 
                 HorizontalAlignment=""Stretch"" VerticalAlignment=""Center"" HorizontalContentAlignment=""Center""
                 md:HintAssist.Hint=""{{Binding DataContext.ConcentrationUnit, RelativeSource={{RelativeSource AncestorType=UserControl}}}}""
                 md:HintAssist.IsFloating=""False""
                 BorderThickness=""0,0,0,1""/>
        
        <TextBlock x:Name=""TxtOverlay""
                   HorizontalAlignment=""Center"" VerticalAlignment=""Center""
                   IsHitTestVisible=""False""
                   Visibility=""Visible"">
            <TextBlock.Text>
                <MultiBinding StringFormat=""{{}}{{0}} {{1}}"">
                    <Binding Path=""ElementConcentrations[{i}].ConcentrationValue"" />
                    <Binding Path=""DataContext.ConcentrationUnit"" RelativeSource=""{{RelativeSource AncestorType=UserControl}}"" />
                </MultiBinding>
            </TextBlock.Text>
        </TextBlock>
    </Grid>
    <DataTemplate.Triggers>
        <DataTrigger Binding=""{{Binding ElementConcentrations[{i}].ConcentrationValue}}"" Value="""">
            <Setter TargetName=""TxtOverlay"" Property=""Visibility"" Value=""Hidden""/>
        </DataTrigger>
        <DataTrigger Binding=""{{Binding ElementConcentrations[{i}].ConcentrationValue}}"" Value=""{{x:Null}}"">
            <Setter TargetName=""TxtOverlay"" Property=""Visibility"" Value=""Hidden""/>
        </DataTrigger>
        
        <DataTrigger Binding=""{{Binding Visibility, ElementName=TxtOverlay}}"" Value=""Visible"">
            <Setter TargetName=""EditTxt"" Property=""Foreground"" Value=""Transparent""/>
        </DataTrigger>

        <Trigger SourceName=""EditTxt"" Property=""IsKeyboardFocusWithin"" Value=""True"">
            <Setter TargetName=""TxtOverlay"" Property=""Visibility"" Value=""Hidden""/>
        </Trigger>

        <DataTrigger Binding=""{{Binding DisableConcentration}}"" Value=""True"">
            <Setter TargetName=""EditTxt"" Property=""IsEnabled"" Value=""False""/>
            <Setter TargetName=""TxtOverlay"" Property=""Visibility"" Value=""Visible""/>
            <Setter TargetName=""TxtOverlay"" Property=""Text"" Value=""-""/>
        </DataTrigger>
    </DataTemplate.Triggers>
</DataTemplate>";

                var cellTemplate = (System.Windows.DataTemplate)System.Windows.Markup.XamlReader.Parse(cellTemplateXaml);

                var newCol = new System.Windows.Controls.DataGridTemplateColumn
                {
                    Header = elName,
                    CellTemplate = cellTemplate,
                    Width = System.Windows.Controls.DataGridLength.Auto,
                    MinWidth = 120
                };

                // 插入到正确位置
                SequenceDataGrid.Columns.Insert(insertPos + i, newCol);
            }
        }

        private System.Windows.Point? _dragStartPoint = null;

        private void SequenceDataGrid_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as System.Windows.DependencyObject;
            if (source == null) return;
            
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                _dragStartPoint = e.GetPosition(null);
            }
            
            // 检查点击的是否是下拉框的弹出层（Popup 不在 DataGrid 的视觉树中）
            var grid = FindVisualParent<System.Windows.Controls.DataGrid>(source);
            if (grid == null) 
            {
                return; // 放开对下拉列表的拦截，使其能够正常点击
            }

            // 向上寻找是否点击了某个 DataGridRow
            var row = FindVisualParent<System.Windows.Controls.DataGridRow>(source);
            
            // 如果点在任何行内（包括行的空白处），完全交还给 WPF 原生处理，不加干预，避免 Bug
            if (row != null)
            {
                return;
            }
            
            // 只有点在表格底部的绝对空白处时，才强制保存编辑并转移焦点
            SequenceDataGrid.CommitEdit();
            SequenceDataGrid.CommitEdit();
            SequenceDataGrid.Focus();
        }

        private void SequenceDataGrid_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed && _dragStartPoint.HasValue)
            {
                var source = e.OriginalSource as System.Windows.DependencyObject;
                
                // 放开文本框的选中和输入
                if (source is System.Windows.Controls.TextBox || source?.GetType().Name == "TextBoxView" || source is System.Windows.Controls.Primitives.TextBoxBase)
                    return;
                // 放开下拉框
                if (FindVisualParent<System.Windows.Controls.Primitives.Popup>(source) != null)
                    return;
                if (source is System.Windows.Controls.ComboBox || FindVisualParent<System.Windows.Controls.ComboBox>(source) != null)
                    return;

                System.Windows.Vector diff = _dragStartPoint.Value - e.GetPosition(null);
                if (Math.Abs(diff.X) > System.Windows.SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > System.Windows.SystemParameters.MinimumVerticalDragDistance)
                {
                    var row = FindVisualParent<System.Windows.Controls.DataGridRow>(source);
                    if (row != null) 
                    {
                        var item = row.Item;
                        if (item != null)
                        {
                            System.Windows.DragDrop.DoDragDrop(row, item, System.Windows.DragDropEffects.Move);
                        }
                    }
                    _dragStartPoint = null;
                }
            }
            else
            {
                _dragStartPoint = null;
            }
        }

        private void SequenceDataGrid_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(GD_ControlCenter_WPF.Models.Messages.SampleItemModel)))
            {
                var draggedItem = e.Data.GetData(typeof(GD_ControlCenter_WPF.Models.Messages.SampleItemModel)) as GD_ControlCenter_WPF.Models.Messages.SampleItemModel;
                var source = e.OriginalSource as System.Windows.DependencyObject;
                var row = FindVisualParent<System.Windows.Controls.DataGridRow>(source);
                
                if (row != null && draggedItem != null)
                {
                    var targetItem = row.Item as GD_ControlCenter_WPF.Models.Messages.SampleItemModel;
                    if (targetItem != null && !ReferenceEquals(draggedItem, targetItem))
                    {
                        var vm = this.DataContext as GD_ControlCenter_WPF.ViewModels.SampleSequenceViewModel;
                        if (vm != null)
                        {
                            int targetIndex = vm.Samples.IndexOf(targetItem);
                            int sourceIndex = vm.Samples.IndexOf(draggedItem);
                            
                            if (sourceIndex >= 0 && targetIndex >= 0)
                            {
                                vm.Samples.Move(sourceIndex, targetIndex);
                            }
                        }
                    }
                }
            }
        }

        private static T FindVisualParent<T>(System.Windows.DependencyObject child) where T : System.Windows.DependencyObject
        {
            System.Windows.DependencyObject parentObject = System.Windows.Media.VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            if (parentObject is T parent) return parent;
            return FindVisualParent<T>(parentObject);
        }
    }
}