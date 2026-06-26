using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Services;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System;

namespace GD_ControlCenter_WPF.ViewModels
{
    #region 辅助数据模型

    /// <summary> 元素周期表单体模型 </summary>
    public partial class PeriodicElement : ObservableObject
    {
        public int AtomicNumber { get; set; }     // 原子序数
        public string Symbol { get; set; }        // 元素符号
        public int Row { get; set; }               // 所在行 (0-9)
        public int Column { get; set; }            // 所在列 (0-17)
        public string HexColor { get; set; }       // 界面显示颜色

        [ObservableProperty]
        private bool _isSelected;

        public PeriodicElement(int num, string symbol, int row, int col, string color)
        {
            AtomicNumber = num; Symbol = symbol; Row = row; Column = col; HexColor = color;
        }
    }

    /// <summary> 波长包装类，支持在界面双向绑定编辑 </summary>
    public partial class WavelengthWrapper : ObservableObject
    {
        [ObservableProperty] private double _value;
        public WavelengthWrapper(double val) { Value = val; }
    }

    #endregion

    /// <summary>
    /// 元素配置视图模型：管理元素谱线库、周期表交互及分析配置的下发。
    /// </summary>
    public partial class ElementConfigViewModel : ObservableObject
    {
        private readonly JsonConfigService _configService;
        private readonly ElementDatabaseService _elementDbService;

        #region 1. 界面绑定集合与属性

        // 元素周期表展示集合
        public ObservableCollection<PeriodicElement> PeriodicElements { get; } = new();

        [ObservableProperty] private string _selectedElementSymbol = "未选择";

        // 当前选中元素的参数
        [ObservableProperty] private ObservableCollection<WavelengthWrapper> _currentWavelengths = new();
        [ObservableProperty] private int _currentIntegrationTime = 200;
        [ObservableProperty] private int _currentAveragingCount = 1;
        [ObservableProperty] private ObservableCollection<string> _currentFittingCurves = new();
        [ObservableProperty] private string _selectedFittingCurve = "测量校准曲线";

        // 编辑状态
        [ObservableProperty] private bool _isWavelengthEditing;
        [ObservableProperty] private bool _isParameterEditing;

        // 右侧最终已选的分析配置列表（正式生效）
        [ObservableProperty] private ObservableCollection<AnalysisConfigItem> _selectedConfigs = new();
        [ObservableProperty] private AnalysisConfigItem? _currentSelectedConfig;

        #endregion

        public ElementConfigViewModel(JsonConfigService configService, ElementDatabaseService elementDbService)
        {
            _configService = configService;
            _elementDbService = elementDbService;

            // 初始化基础数据
            InitializePeriodicTable();

            WeakReferenceMessenger.Default.Register<SyncTemplateElementsMessage>(this, (r, m) =>
            {
                SelectedConfigs.Clear();
                var db = _elementDbService.Load();

                foreach (var item in m.Value)
                {
                    if (db.Elements.TryGetValue(item.ElementName, out var config))
                    {
                        if (item.Wavelength == 0 && config.Wavelengths.Count > 0)
                            item.Wavelength = config.Wavelengths[0].Wavelength;
                        
                        item.IntegrationTime = config.IntegrationTime;
                        item.AveragingCount = config.AveragingCount;
                        if (config.FittingCurves.Count > 0)
                        {
                            item.FittingCurve = config.FittingCurves[0];
                        }
                    }
                    SelectedConfigs.Add(item);
                }
                WeakReferenceMessenger.Default.Send(new ActiveConfigsChangedMessage(SelectedConfigs.ToList()));
            });
        }

        #region 2. 业务命令 (Commands)

        /// <summary> 选中周期表中的某个元素 </summary>
        [RelayCommand]
        private void SelectElement(string symbol)
        {
            if (IsWavelengthEditing || IsParameterEditing)
            {
                var res = MessageBox.Show("您有未保存的修改，是否确认放弃并切换元素？", "提示", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
                
                IsWavelengthEditing = false;
                IsParameterEditing = false;
            }

            SelectedElementSymbol = symbol;

            // 更新所有元素的选中状态
            foreach (var element in PeriodicElements)
            {
                element.IsSelected = (element.Symbol == symbol);
            }

            LoadElementConfig(symbol);
        }

        private void LoadElementConfig(string symbol)
        {
            var db = _elementDbService.Load();
            if (db.Elements.TryGetValue(symbol, out var config))
            {
                CurrentWavelengths.Clear();
                foreach (var w in config.Wavelengths)
                {
                    CurrentWavelengths.Add(new WavelengthWrapper(w.Wavelength));
                }
                CurrentIntegrationTime = config.IntegrationTime;
                CurrentAveragingCount = config.AveragingCount;
                
                CurrentFittingCurves.Clear();
                foreach (var curve in config.FittingCurves)
                {
                    CurrentFittingCurves.Add(curve);
                }
                if (CurrentFittingCurves.Count > 0) SelectedFittingCurve = CurrentFittingCurves[0];
            }
            else
            {
                // 如果库里没有，给个默认空状态
                CurrentWavelengths.Clear();
                CurrentIntegrationTime = 200;
                CurrentAveragingCount = 1;
                CurrentFittingCurves.Clear();
                CurrentFittingCurves.Add("测量校准曲线");
                SelectedFittingCurve = "测量校准曲线";
            }
        }

        /// <summary> 点击波长加入分析配置 </summary>
        [RelayCommand]
        private void AddWavelengthToActive(double wavelength)
        {
            if (IsWavelengthEditing) return; // 编辑模式下不能添加
            if (SelectedElementSymbol == "未选择") return;

            var config = _configService.Load();

            // 查重
            if (SelectedConfigs.Any(x => x.ElementName == SelectedElementSymbol && x.Wavelength == wavelength)) return;

            // 曲线类型冲突拦截：要么都选“测量校准曲线”，要么都选已保存的曲线
            if (SelectedConfigs.Count > 0)
            {
                bool isNewCurveSaved = SelectedFittingCurve != "测量校准曲线";
                bool isExistingCurveSaved = SelectedConfigs[0].FittingCurve != "测量校准曲线";
                
                if (isNewCurveSaved != isExistingCurveSaved)
                {
                    MessageBox.Show("当前选中的拟合曲线类型与已加入的元素曲线类型冲突！\n\n规则限制：要么所有元素都选择“测量校准曲线”，要么所有元素都选择已保存的曲线。请修改当前元素的拟合曲线或清空已有配置后再试。", "添加拦截", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            SelectedConfigs.Add(new AnalysisConfigItem
            {
                ElementName = SelectedElementSymbol,
                Wavelength = wavelength,
                SampleCountText = config.LastSampleCount.ToString(),
                SampleIntervalText = config.LastSampleInterval.ToString(),
                IntegrationTime = CurrentIntegrationTime,
                AveragingCount = CurrentAveragingCount,
                FittingCurve = SelectedFittingCurve
            });

            WeakReferenceMessenger.Default.Send(new ActiveConfigsChangedMessage(SelectedConfigs.ToList()));
        }

        [RelayCommand]
        private void RemoveSelectedConfig()
        {
            if (CurrentSelectedConfig != null)
            {
                SelectedConfigs.Remove(CurrentSelectedConfig);
                WeakReferenceMessenger.Default.Send(new ActiveConfigsChangedMessage(SelectedConfigs.ToList()));
            }
        }

        #endregion

        #region 编辑相关命令

        [RelayCommand]
        private void ToggleWavelengthEdit()
        {
            if (SelectedElementSymbol == "未选择") return;

            if (IsWavelengthEditing)
            {
                var res = MessageBox.Show("确定要保存对波长的修改吗？", "保存确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.Yes)
                {
                    SaveCurrentElementToDb();
                    IsWavelengthEditing = false;
                }
                else
                {
                    // 回滚
                    LoadElementConfig(SelectedElementSymbol);
                    IsWavelengthEditing = false;
                }
            }
            else
            {
                IsWavelengthEditing = true;
            }
        }

        [RelayCommand]
        private void AddNewWavelength()
        {
            // 添加新波长交互（在UI层绑定到ViewModel或在此处理简易逻辑）
            // 在实际WPF中，可以通过弹窗输入。这里我们先添加一个默认的0.0，让用户在原位编辑。
            CurrentWavelengths.Add(new WavelengthWrapper(0.0));
        }

        [RelayCommand]
        private void ToggleParameterEdit()
        {
            if (SelectedElementSymbol == "未选择") return;

            if (IsParameterEditing)
            {
                var res = MessageBox.Show("确定要保存对测样参数的修改吗？", "保存确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.Yes)
                {
                    SaveCurrentElementToDb();
                    IsParameterEditing = false;
                }
                else
                {
                    // 回滚
                    LoadElementConfig(SelectedElementSymbol);
                    IsParameterEditing = false;
                }
            }
            else
            {
                IsParameterEditing = true;
            }
        }

        [RelayCommand]
        private void DeleteFittingCurve(string curveName)
        {
            if (curveName == "测量校准曲线")
            {
                MessageBox.Show("【测量校准曲线】为系统默认必须项，不可删除！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var res = MessageBox.Show($"确定要删除拟合曲线【{curveName}】吗？", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res == MessageBoxResult.Yes)
            {
                CurrentFittingCurves.Remove(curveName);
                if (SelectedFittingCurve == curveName && CurrentFittingCurves.Count > 0)
                {
                    SelectedFittingCurve = CurrentFittingCurves[0];
                }
                SaveCurrentElementToDb(); // 立即保存
            }
        }

        private void SaveCurrentElementToDb()
        {
            var db = _elementDbService.Load();
            
            var config = new ElementConfig
            {
                IntegrationTime = CurrentIntegrationTime,
                AveragingCount = CurrentAveragingCount,
                FittingCurves = CurrentFittingCurves.ToList(),
                Wavelengths = CurrentWavelengths.Select(w => new WavelengthConfig { Wavelength = w.Value }).ToList()
            };
            
            db.Elements[SelectedElementSymbol] = config;
            _elementDbService.Save(db);
        }

        #endregion

        #region 3. 周期表初始化 (静态排版)

        private void InitializePeriodicTable()
        {
            // 颜色定义
            string nm = "#B2DFDB", ng = "#B39DDB", ak = "#FFCC80", akn = "#FFE082";
            string tr = "#BBDEFB", bm = "#CFD8DC", ml = "#D7CCC8", la = "#F8BBD0", ac = "#F48FB1";

            // 1-3 周期 (常规布局)
            PeriodicElements.Add(new PeriodicElement(1, "H", 0, 0, nm)); PeriodicElements.Add(new PeriodicElement(2, "He", 0, 17, ng));
            PeriodicElements.Add(new PeriodicElement(3, "Li", 1, 0, ak)); PeriodicElements.Add(new PeriodicElement(4, "Be", 1, 1, akn));
            PeriodicElements.Add(new PeriodicElement(5, "B", 1, 12, ml)); PeriodicElements.Add(new PeriodicElement(6, "C", 1, 13, nm));
            PeriodicElements.Add(new PeriodicElement(7, "N", 1, 14, nm)); PeriodicElements.Add(new PeriodicElement(8, "O", 1, 15, nm));
            PeriodicElements.Add(new PeriodicElement(9, "F", 1, 16, nm)); PeriodicElements.Add(new PeriodicElement(10, "Ne", 1, 17, ng));
            PeriodicElements.Add(new PeriodicElement(11, "Na", 2, 0, ak)); PeriodicElements.Add(new PeriodicElement(12, "Mg", 2, 1, akn));
            PeriodicElements.Add(new PeriodicElement(13, "Al", 2, 12, bm)); PeriodicElements.Add(new PeriodicElement(14, "Si", 2, 13, ml));
            PeriodicElements.Add(new PeriodicElement(15, "P", 2, 14, nm)); PeriodicElements.Add(new PeriodicElement(16, "S", 2, 15, nm));
            PeriodicElements.Add(new PeriodicElement(17, "Cl", 2, 16, nm)); PeriodicElements.Add(new PeriodicElement(18, "Ar", 2, 17, ng));

            // 4-6 周期 (包含过渡金属)
            string[] row4 = { "K", "Ca", "Sc", "Ti", "V", "Cr", "Mn", "Fe", "Co", "Ni", "Cu", "Zn", "Ga", "Ge", "As", "Se", "Br", "Kr" };
            for (int i = 0; i < 18; i++) PeriodicElements.Add(new PeriodicElement(19 + i, row4[i], 3, i, (i < 2 ? ak : (i < 12 ? tr : bm))));

            string[] row5 = { "Rb", "Sr", "Y", "Zr", "Nb", "Mo", "Tc", "Ru", "Rh", "Pd", "Ag", "Cd", "In", "Sn", "Sb", "Te", "I", "Xe" };
            for (int i = 0; i < 18; i++) PeriodicElements.Add(new PeriodicElement(37 + i, row5[i], 4, i, (i < 2 ? ak : (i < 12 ? tr : bm))));

            string[] row6 = { "Cs", "Ba", "La", "Hf", "Ta", "W", "Re", "Os", "Ir", "Pt", "Au", "Hg", "Tl", "Pb", "Bi", "Po", "At", "Rn" };
            for (int i = 0; i < 18; i++) PeriodicElements.Add(new PeriodicElement(55 + i, row6[i], 5, i, (i < 2 ? ak : (i < 12 ? tr : bm))));

            // 镧系 (底部展示)
            string[] lanth = { "La", "Ce", "Pr", "Nd", "Pm", "Sm", "Eu", "Gd", "Tb", "Dy", "Ho", "Er", "Tm", "Yb", "Lu" };
            for (int i = 0; i < 15; i++) PeriodicElements.Add(new PeriodicElement(57 + i, lanth[i], 8, i + 2, la));

            // 锕系 (底部展示)
            string[] actin = { "Ac", "Th", "Pa", "U", "Np", "Pu", "Am", "Cm", "Bk", "Cf", "Es", "Fm", "Md", "No", "Lr" };
            for (int i = 0; i < 15; i++) PeriodicElements.Add(new PeriodicElement(89 + i, actin[i], 9, i + 2, ac));
        }

        #endregion
    }
}