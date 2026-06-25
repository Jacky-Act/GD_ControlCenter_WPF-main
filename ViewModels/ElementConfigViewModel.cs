using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
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
    public class PeriodicElement
    {
        public int AtomicNumber { get; set; }     // 原子序数
        public string Symbol { get; set; }        // 元素符号
        public int Row { get; set; }               // 所在行 (0-9)
        public int Column { get; set; }            // 所在列 (0-17)
        public string HexColor { get; set; }       // 界面显示颜色

        public PeriodicElement(int num, string symbol, int row, int col, string color)
        {
            AtomicNumber = num; Symbol = symbol; Row = row; Column = col; HexColor = color;
        }
    }

    /// <summary> 待选区的暂存项 </summary>
    public partial class StagedConfigItem : ObservableObject
    {
        [ObservableProperty] private string _elementName = string.Empty;
        [ObservableProperty] private double _wavelength;
    }
    #endregion

    /// <summary>
    /// 元素配置视图模型：管理元素谱线库、周期表交互及分析配置的下发。
    /// </summary>
    public partial class ElementConfigViewModel : ObservableObject
    {
        private readonly JsonConfigService _configService;

        #region 1. 界面绑定集合与属性

        // 元素周期表展示集合
        public ObservableCollection<PeriodicElement> PeriodicElements { get; } = new();

        // 核心波长数据库 (由 PDF 数据驱动)
        private readonly Dictionary<string, List<double>> _wavelengthDatabase = new();

        [ObservableProperty] private string _selectedElementSymbol = "未选择";

        // 当前选中元素对应的可用波长列表
        [ObservableProperty] private ObservableCollection<double> _availableWavelengths = new();

        // 中间下方的暂存候选区（篮子）
        [ObservableProperty] private ObservableCollection<StagedConfigItem> _stagedConfigs = new();
        [ObservableProperty] private StagedConfigItem? _currentStagedConfig;

        // 右侧最终已选的分析配置列表（正式生效）
        [ObservableProperty] private ObservableCollection<AnalysisConfigItem> _selectedConfigs = new();
        [ObservableProperty] private AnalysisConfigItem? _currentSelectedConfig;

        // 同步仪表台显示的全局硬件参数快照
        [ObservableProperty] private string _globalCurrent = "-";
        [ObservableProperty] private string _globalPumpSpeed = "-";
        [ObservableProperty] private string _globalSampleCount = "-";
        [ObservableProperty] private string _globalSampleInterval = "-";

        #endregion

        public ElementConfigViewModel(JsonConfigService configService)
        {
            _configService = configService;

            // 初始化基础数据
            InitializePeriodicTable();
            InitializeWavelengthDatabase();

            // 加载当前硬件参数快照
            RefreshGlobalSettings();
        }

        /// <summary> 从本地配置文件同步最新的硬件参数显示 </summary>
        public void RefreshGlobalSettings()
        {
            var config = _configService.Load();
            GlobalCurrent = config.LastHvCurrent.ToString();
            GlobalPumpSpeed = config.LastPumpSpeed.ToString();
            GlobalSampleCount = config.LastSampleCount.ToString();
            GlobalSampleInterval = config.LastSampleInterval.ToString();
        }

        #region 2. 业务命令 (Commands)

        /// <summary> 选中周期表中的某个元素 </summary>
        [RelayCommand]
        private void SelectElement(string symbol)
        {
            SelectedElementSymbol = symbol;
            AvailableWavelengths.Clear();

            // 从数据库调取该元素的所有特征波长
            if (_wavelengthDatabase.TryGetValue(symbol, out var wls) && wls.Count > 0)
            {
                foreach (var w in wls) AvailableWavelengths.Add(w);
            }
            else
            {
                MessageBox.Show($"谱库中暂无【{symbol}】元素的特征波长数据。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary> 将选中的波长加入中间暂存区 </summary>
        [RelayCommand]
        private void StageWavelength(double wavelength)
        {
            if (SelectedElementSymbol == "未选择") return;

            // 防止重复添加同一元素的同一波长
            if (!StagedConfigs.Any(x => x.ElementName == SelectedElementSymbol && x.Wavelength == wavelength))
            {
                StagedConfigs.Add(new StagedConfigItem { ElementName = SelectedElementSymbol, Wavelength = wavelength });
            }
        }

        /// <summary> 从暂存区移除 </summary>
        [RelayCommand]
        private void RemoveStagedConfig()
        {
            if (CurrentStagedConfig != null) StagedConfigs.Remove(CurrentStagedConfig);
        }

        /// <summary> 核心：将暂存区所有项提交至最终分析配置，并广播给“样品序列”模块 </summary>
        [RelayCommand]
        private void CommitToActiveConfigs()
        {
            if (StagedConfigs.Count == 0) return;
            RefreshGlobalSettings(); // 提交前刷新一次参数

            foreach (var staged in StagedConfigs)
            {
                // 最终名单查重
                if (SelectedConfigs.Any(x => x.ElementName == staged.ElementName && x.Wavelength == staged.Wavelength)) continue;

                SelectedConfigs.Add(new AnalysisConfigItem
                {
                    ElementName = staged.ElementName,
                    Wavelength = staged.Wavelength,
                    SampleCountText = GlobalSampleCount,
                    SampleIntervalText = GlobalSampleInterval
                });
            }
            StagedConfigs.Clear();

            // 【跨模块通讯】：发送消息通知“样品序列”和“测样分析”页面更新表头和配置
            WeakReferenceMessenger.Default.Send(new ActiveConfigsChangedMessage(SelectedConfigs.ToList()));
        }

        /// <summary> 从最终配置中删除某项 </summary>
        [RelayCommand]
        private void RemoveFinalConfig()
        {
            if (CurrentSelectedConfig != null)
            {
                SelectedConfigs.Remove(CurrentSelectedConfig);
                WeakReferenceMessenger.Default.Send(new ActiveConfigsChangedMessage(SelectedConfigs.ToList()));
            }
        }

        #endregion

        #region 3. 谱线数据库初始化 (由 PDF 数据驱动)

        private void InitializeWavelengthDatabase()
        {
            // 按照 PDF 提供的波长进行录入 (部分示例，已涵盖 PDF 核心数据)
            _wavelengthDatabase["Sn"] = new List<double> { 242.95, 236.48, 359.26, 442.43 };
            _wavelengthDatabase["Sr"] = new List<double> { 460.77, 242.81 };
            _wavelengthDatabase["Ta"] = new List<double> { 271.47, 255.94 };
            _wavelengthDatabase["Tb"] = new List<double> { 432.65, 390.14 };
            _wavelengthDatabase["Te"] = new List<double> { 214.30, 225.90 };
            _wavelengthDatabase["Ti"] = new List<double> { 364.27, 399.86 };
            _wavelengthDatabase["Tl"] = new List<double> { 377.60, 276.78 };
            _wavelengthDatabase["U"] = new List<double> { 351.46, 415.40 };
            _wavelengthDatabase["V"] = new List<double> { 318.39, 437.92 };
            _wavelengthDatabase["W"] = new List<double> { 255.14, 265.65 };
            _wavelengthDatabase["Y"] = new List<double> { 407.74, 410.24 };
            _wavelengthDatabase["Yb"] = new List<double> { 398.80, 346.44 };
            _wavelengthDatabase["Zn"] = new List<double> { 214.03, 213.09 };
            _wavelengthDatabase["Zr"] = new List<double> { 360.12, 301.18 };
            _wavelengthDatabase["Ag"] = new List<double> { 328.96 };
            _wavelengthDatabase["As"] = new List<double> { 338.81, 228.20, 193.69 };
            _wavelengthDatabase["Al"] = new List<double> { 309.27, 396.15 };
            _wavelengthDatabase["Au"] = new List<double> { 242.79, 267.59 };
            _wavelengthDatabase["B"] = new List<double> { 249.68, 249.77 };
            _wavelengthDatabase["Ba"] = new List<double> { 455.40 };
            _wavelengthDatabase["Be"] = new List<double> { 234.86 };
            _wavelengthDatabase["Bi"] = new List<double> { 313.04 };
            _wavelengthDatabase["Ca"] = new List<double> { 423.28, 422.81 };
            _wavelengthDatabase["Co"] = new List<double> { 240.72, 242.49 };
            _wavelengthDatabase["Cd"] = new List<double> { 228.78, 361.05 };
            _wavelengthDatabase["Cr"] = new List<double> { 357.56, 359.35 };
            _wavelengthDatabase["Cs"] = new List<double> { 852.30, 894.35 };
            _wavelengthDatabase["Cu"] = new List<double> { 324.75, 327.39 };
            _wavelengthDatabase["Pd"] = new List<double> { 344.14, 340.45 };
            _wavelengthDatabase["Pr"] = new List<double> { 390.84, 414.31 };
            _wavelengthDatabase["Pt"] = new List<double> { 265.95, 214.42 };
            _wavelengthDatabase["Rb"] = new List<double> { 780.60, 795.10 };
            _wavelengthDatabase["Re"] = new List<double> { 204.90, 228.75 };
            _wavelengthDatabase["Rh"] = new List<double> { 437.50, 339.68 };
            _wavelengthDatabase["Ru"] = new List<double> { 349.89, 372.80 };
            _wavelengthDatabase["Sb"] = new List<double> { 231.10, 206.83 };
            _wavelengthDatabase["Se"] = new List<double> { 361.38, 363.07, 196.09, 203.99 };
            _wavelengthDatabase["Si"] = new List<double> { 251.61, 251.43 };
            _wavelengthDatabase["K"] = new List<double> { 766.49, 766.95 };
            _wavelengthDatabase["La"] = new List<double> { 333.75, 379.47 };
            _wavelengthDatabase["Li"] = new List<double> { 670.78, 670.95 };
            _wavelengthDatabase["Lu"] = new List<double> { 261.54, 296.33 };
            _wavelengthDatabase["Mg"] = new List<double> { 285.63, 518.27 };
            _wavelengthDatabase["Mn"] = new List<double> { 279.48, 279.96 };
            _wavelengthDatabase["Mo"] = new List<double> { 313.26, 317.04 };
            _wavelengthDatabase["Na"] = new List<double> { 589.38, 589.59 };
            _wavelengthDatabase["Nb"] = new List<double> { 309.42, 316.34 };
            _wavelengthDatabase["Nd"] = new List<double> { 401.23, 430.36 };
            _wavelengthDatabase["Ni"] = new List<double> { 341.63, 352.88 };
            _wavelengthDatabase["Os"] = new List<double> { 225.50, 305.86 };
            _wavelengthDatabase["Pb"] = new List<double> { 368.78, 406.08 };
            _wavelengthDatabase["Dy"] = new List<double> { 353.17, 394.47 };
            _wavelengthDatabase["Er"] = new List<double> { 337.27, 349.91 };
            _wavelengthDatabase["Eu"] = new List<double> { 381.96, 412.97 };
            _wavelengthDatabase["Fe"] = new List<double> { 248.72, 252.28 };
            _wavelengthDatabase["Ga"] = new List<double> { 294.36, 417.21 };
            _wavelengthDatabase["Gd"] = new List<double> { 342.25, 336.22 };
            _wavelengthDatabase["Ge"] = new List<double> { 265.16, 209.43 };
            _wavelengthDatabase["Hf"] = new List<double> { 339.98, 277.33 };
            _wavelengthDatabase["Hg"] = new List<double> { 253.65, 404.66 };
            _wavelengthDatabase["Ho"] = new List<double> { 345.60, 339.89 };
            _wavelengthDatabase["In"] = new List<double> { 451.10, 230.60 };
            _wavelengthDatabase["Ir"] = new List<double> { 224.27, 212.68 };
        }

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