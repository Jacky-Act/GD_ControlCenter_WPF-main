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
        [ObservableProperty] private bool _isSelected;
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

        // 波长列表
        [ObservableProperty] private ObservableCollection<WavelengthWrapper> _currentWavelengths = new();
        [ObservableProperty] private WavelengthWrapper? _selectedWavelengthWrapper;

        // 当前选中波长的参数
        private int _currentIntegrationTime = 200;
        public int CurrentIntegrationTime
        {
            get => _currentIntegrationTime;
            set
            {
                if (_currentIntegrationTime == value || _isLoadingElement || SelectedWavelengthWrapper == null)
                {
                    SetProperty(ref _currentIntegrationTime, value);
                    return;
                }
                var res = MessageBox.Show($"是否将积分时间修改为 {value} ms？", "确认修改", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.Yes)
                {
                    SetProperty(ref _currentIntegrationTime, value);
                    SaveCurrentWavelengthToDb();
                    UpdateCanAddToConfigState();
                }
                else 
                { 
                    OnPropertyChanged(nameof(CurrentIntegrationTime)); 
                }
            }
        }

        private int _currentAveragingCount = 1;
        public int CurrentAveragingCount
        {
            get => _currentAveragingCount;
            set
            {
                if (_currentAveragingCount == value || _isLoadingElement || SelectedWavelengthWrapper == null)
                {
                    SetProperty(ref _currentAveragingCount, value);
                    return;
                }
                var res = MessageBox.Show($"是否将平均次数修改为 {value} 次？", "确认修改", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.Yes)
                {
                    SetProperty(ref _currentAveragingCount, value);
                    SaveCurrentWavelengthToDb();
                    UpdateCanAddToConfigState();
                }
                else 
                { 
                    OnPropertyChanged(nameof(CurrentAveragingCount)); 
                }
            }
        }

        [ObservableProperty] private ObservableCollection<string> _currentFittingCurves = new();
        
        private string _selectedFittingCurve = "测量校准曲线";
        public string SelectedFittingCurve
        {
            get => _selectedFittingCurve;
            set
            {
                if (_selectedFittingCurve == value) return;
                
                if (!_isLoadingElement)
                {
                    bool hasSequence = false;
                    try { hasSequence = WeakReferenceMessenger.Default.Send<SequenceStatusRequestMessage>().Response; } catch { }

                    if (hasSequence)
                    {
                        var res = MessageBox.Show("当前已有待测样品序列或测量数据，更改拟合曲线将会导致后续计算逻辑变更，甚至可能需要清空当前序列重新应用！\n\n您确定要更改曲线吗？", "警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (res != MessageBoxResult.Yes) 
                        {
                            OnPropertyChanged(nameof(SelectedFittingCurve));
                            return;
                        }
                        
                        WeakReferenceMessenger.Default.Send(new ClearSequenceRequestMessage());
                    }
                }

                SetProperty(ref _selectedFittingCurve, value);
                
                if (!_isLoadingElement && SelectedWavelengthWrapper != null)
                {
                    var currentSelected = SelectedConfigs.FirstOrDefault(c => c.ElementName == SelectedElementSymbol && c.Wavelength == SelectedWavelengthWrapper.Value);
                    if (currentSelected != null)
                    {
                        currentSelected.FittingCurve = value;
                        WeakReferenceMessenger.Default.Send(new ActiveConfigsChangedMessage(SelectedConfigs.ToList()));
                    }
                    SaveCurrentWavelengthToDb();
                    UpdateCanAddToConfigState();
                }
            }
        }

        // 编辑状态
        [ObservableProperty] private bool _isWavelengthEditing;
        [ObservableProperty] private bool _canAddToConfig;

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
                        
                        var wConfig = config.Wavelengths.FirstOrDefault(w => w.Wavelength == item.Wavelength);
                        if (wConfig != null)
                        {
                            item.IntegrationTime = wConfig.IntegrationTime;
                            item.AveragingCount = wConfig.AveragingCount;
                            if (wConfig.FittingCurves.Count > 0)
                            {
                                item.FittingCurve = wConfig.FittingCurves[0];
                            }
                        }
                    }
                    SelectedConfigs.Add(item);
                }
                WeakReferenceMessenger.Default.Send(new ActiveConfigsChangedMessage(SelectedConfigs.ToList()));
            });

            // 监听：保存了新的曲线后，如果当前恰好处于该元素页面，则刷新它
            WeakReferenceMessenger.Default.Register<CurveSavedMessage>(this, (r, m) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (SelectedElementSymbol == m.Value && SelectedWavelengthWrapper != null)
                    {
                        LoadWavelengthConfig(SelectedElementSymbol, SelectedWavelengthWrapper.Value);
                    }
                });
            });
        }

        #region 2. 业务命令 (Commands)

        /// <summary> 选中周期表中的某个元素 </summary>
        [RelayCommand]
        private void SelectElement(string symbol)
        {
            if (IsWavelengthEditing)
            {
                var res = MessageBox.Show("您有未保存的修改，是否确认放弃并切换元素？", "提示", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
                
                IsWavelengthEditing = false;
            }

            SelectedElementSymbol = symbol;
            SelectedWavelengthWrapper = null;
            UpdateCanAddToConfigState();

            // 更新所有元素的选中状态
            foreach (var element in PeriodicElements)
            {
                element.IsSelected = (element.Symbol == symbol);
            }

            LoadElementConfig(symbol);
        }

        private bool _isLoadingElement = false;

        private void LoadElementConfig(string symbol)
        {
            _isLoadingElement = true;
            try
            {
                var db = _elementDbService.Load();
                if (db.Elements.TryGetValue(symbol, out var config))
                {
                    CurrentWavelengths.Clear();
                    foreach (var w in config.Wavelengths)
                    {
                        CurrentWavelengths.Add(new WavelengthWrapper(w.Wavelength));
                    }
                }
                else
                {
                    // 如果库里没有，给个默认空状态
                    CurrentWavelengths.Clear();
                }
            }
            finally
            {
                _isLoadingElement = false;
            }
        }

        [RelayCommand]
        private void SelectWavelength(WavelengthWrapper wrapper)
        {
            if (wrapper == null) return;
            if (IsWavelengthEditing) return;

            // Highlight
            foreach (var w in CurrentWavelengths) w.IsSelected = false;
            wrapper.IsSelected = true;
            
            SelectedWavelengthWrapper = wrapper;
            LoadWavelengthConfig(SelectedElementSymbol, wrapper.Value);
            UpdateCanAddToConfigState();
        }

        private void LoadWavelengthConfig(string element, double wavelength)
        {
            _isLoadingElement = true;
            try
            {
                var db = _elementDbService.Load();
                if (db.Elements.TryGetValue(element, out var config))
                {
                    var wConfig = config.Wavelengths.FirstOrDefault(w => w.Wavelength == wavelength);
                    if (wConfig != null)
                    {
                        _currentIntegrationTime = wConfig.IntegrationTime;
                        OnPropertyChanged(nameof(CurrentIntegrationTime));
                        
                        _currentAveragingCount = wConfig.AveragingCount;
                        OnPropertyChanged(nameof(CurrentAveragingCount));
                        
                        CurrentFittingCurves.Clear();
                        foreach (var curve in wConfig.FittingCurves)
                        {
                            CurrentFittingCurves.Add(curve);
                        }
                        if (CurrentFittingCurves.Count > 0) SelectedFittingCurve = CurrentFittingCurves[0];
                        return;
                    }
                }
                
                // default if not found
                _currentIntegrationTime = 200;
                OnPropertyChanged(nameof(CurrentIntegrationTime));
                _currentAveragingCount = 1;
                OnPropertyChanged(nameof(CurrentAveragingCount));
                CurrentFittingCurves.Clear();
                CurrentFittingCurves.Add("测量校准曲线");
                SelectedFittingCurve = "测量校准曲线";
            }
            finally
            {
                _isLoadingElement = false;
            }
        }

        private void UpdateCanAddToConfigState()
        {
            if (SelectedWavelengthWrapper == null) 
            {
                CanAddToConfig = false;
                return;
            }
            
            var existing = SelectedConfigs.FirstOrDefault(x => x.ElementName == SelectedElementSymbol && x.Wavelength == SelectedWavelengthWrapper.Value);
            if (existing == null)
            {
                CanAddToConfig = true; // Not added yet
            }
            else
            {
                // Added. Check if parameters changed
                if (existing.IntegrationTime != CurrentIntegrationTime ||
                    existing.AveragingCount != CurrentAveragingCount ||
                    existing.FittingCurve != SelectedFittingCurve)
                {
                    CanAddToConfig = true;
                }
                else
                {
                    CanAddToConfig = false;
                }
            }
        }

        /// <summary> 点击波长加入分析配置 </summary>
        [RelayCommand]
        private void AddCurrentWavelengthToActive()
        {
            if (SelectedElementSymbol == "未选择" || SelectedWavelengthWrapper == null) return;

            var config = _configService.Load();
            double wavelength = SelectedWavelengthWrapper.Value;

            // 查重，移除旧的（视为更新参数）
            var existing = SelectedConfigs.FirstOrDefault(x => x.ElementName == SelectedElementSymbol && x.Wavelength == wavelength);
            if (existing != null)
            {
                SelectedConfigs.Remove(existing);
            }

            // 曲线类型冲突拦截：要么都选“测量校准曲线”，要么都选已保存的曲线
            if (SelectedConfigs.Count > 0)
            {
                bool isNewCurveSaved = SelectedFittingCurve != "测量校准曲线";
                bool isExistingCurveSaved = SelectedConfigs[0].FittingCurve != "测量校准曲线";
                
                if (isNewCurveSaved != isExistingCurveSaved)
                {
                    MessageBox.Show("当前选中的拟合曲线类型与已加入的元素曲线类型冲突！\n\n规则限制：要么所有元素都选择“测量校准曲线”，要么所有元素都选择已保存的曲线。请修改当前元素的拟合曲线或清空已有配置后再试。", "添加拦截", MessageBoxButton.OK, MessageBoxImage.Warning);
                    if (existing != null) SelectedConfigs.Add(existing); // restore
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
            UpdateCanAddToConfigState();
        }

        [RelayCommand]
        private void RemoveSelectedConfig()
        {
            if (CurrentSelectedConfig != null)
            {
                var removed = CurrentSelectedConfig;
                SelectedConfigs.Remove(CurrentSelectedConfig);
                WeakReferenceMessenger.Default.Send(new ActiveConfigsChangedMessage(SelectedConfigs.ToList()));
                
                if (SelectedElementSymbol == removed.ElementName && SelectedWavelengthWrapper?.Value == removed.Wavelength)
                {
                    UpdateCanAddToConfigState();
                }
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
                    SaveCurrentElementWavelengthsToDb();
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
                SelectedWavelengthWrapper = null;
                UpdateCanAddToConfigState();
            }
        }

        [RelayCommand]
        private void AddNewWavelength()
        {
            CurrentWavelengths.Add(new WavelengthWrapper(0.0));
        }

        [RelayCommand]
        private void DeleteFittingCurve(string curveName)
        {
            if (curveName == "测量校准曲线")
            {
                MessageBox.Show("【测量校准曲线】为系统默认必须项，不可删除！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (SelectedWavelengthWrapper == null) return;

            var res = MessageBox.Show($"确定要删除拟合曲线\n【{curveName}】吗？", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res == MessageBoxResult.Yes)
            {
                var db = _elementDbService.Load();
                var filesToDelete = new HashSet<string>();
                
                if (db.Elements.TryGetValue(SelectedElementSymbol, out var configToDelete))
                {
                    var wConfig = configToDelete.Wavelengths.FirstOrDefault(w => w.Wavelength == SelectedWavelengthWrapper.Value);
                    if (wConfig != null)
                    {
                        var curveToDelete = wConfig.SavedCurves?.FirstOrDefault(c => c.Name == curveName);
                        if (curveToDelete != null && curveToDelete.Points != null)
                        {
                            foreach (var pt in curveToDelete.Points)
                            {
                                if (!string.IsNullOrEmpty(pt.CsvFilePath) && System.IO.File.Exists(pt.CsvFilePath))
                                {
                                    filesToDelete.Add(pt.CsvFilePath);
                                }
                            }
                        }
                        wConfig.SavedCurves?.RemoveAll(c => c.Name == curveName);
                        wConfig.FittingCurves.Remove(curveName);
                    }
                }

                CurrentFittingCurves.Remove(curveName);
                if (SelectedFittingCurve == curveName && CurrentFittingCurves.Count > 0)
                {
                    _isLoadingElement = true;
                    SelectedFittingCurve = CurrentFittingCurves[0];
                    _isLoadingElement = false;
                }
                
                _elementDbService.Save(db);
                
                if (filesToDelete.Count > 0)
                {
                    var allRemainingCsvPaths = new HashSet<string>();
                    foreach (var elem in db.Elements.Values)
                    {
                        foreach (var w in elem.Wavelengths)
                        {
                            if (w.SavedCurves == null) continue;
                            foreach (var c in w.SavedCurves)
                            {
                                if (c.Points == null) continue;
                                foreach (var p in c.Points)
                                {
                                    if (!string.IsNullOrEmpty(p.CsvFilePath))
                                    {
                                        allRemainingCsvPaths.Add(p.CsvFilePath);
                                    }
                                }
                            }
                        }
                    }

                    foreach (var file in filesToDelete)
                    {
                        if (!allRemainingCsvPaths.Contains(file))
                        {
                            try { System.IO.File.Delete(file); } catch { }
                        }
                    }
                }

                SaveCurrentWavelengthToDb(); 
            }
        }

        private void SaveCurrentElementWavelengthsToDb()
        {
            var db = _elementDbService.Load();
            
            if (!db.Elements.ContainsKey(SelectedElementSymbol))
                db.Elements[SelectedElementSymbol] = new ElementConfig();
            
            var config = db.Elements[SelectedElementSymbol];
            
            var updatedWavelengths = new List<WavelengthConfig>();
            foreach (var wWrapper in CurrentWavelengths)
            {
                var existing = config.Wavelengths.FirstOrDefault(w => w.Wavelength == wWrapper.Value);
                if (existing != null)
                {
                    updatedWavelengths.Add(existing);
                }
                else
                {
                    updatedWavelengths.Add(new WavelengthConfig { Wavelength = wWrapper.Value });
                }
            }
            config.Wavelengths = updatedWavelengths;
            _elementDbService.Save(db);
        }

        private void SaveCurrentWavelengthToDb()
        {
            if (SelectedElementSymbol == "未选择" || SelectedWavelengthWrapper == null) return;
            
            var db = _elementDbService.Load();
            if (db.Elements.TryGetValue(SelectedElementSymbol, out var config))
            {
                var wConfig = config.Wavelengths.FirstOrDefault(w => w.Wavelength == SelectedWavelengthWrapper.Value);
                if (wConfig != null)
                {
                    wConfig.IntegrationTime = CurrentIntegrationTime;
                    wConfig.AveragingCount = CurrentAveragingCount;
                    wConfig.FittingCurves = CurrentFittingCurves.ToList();
                    _elementDbService.Save(db);
                }
            }
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