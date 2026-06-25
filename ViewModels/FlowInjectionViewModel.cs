using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Models.Spectrometer;
using GD_ControlCenter_WPF.Services;
using GD_ControlCenter_WPF.Services.Spectrometer.Logic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace GD_ControlCenter_WPF.ViewModels
{
    public partial class FlowInjectionViewModel : ObservableObject
    {
        private readonly JsonConfigService _configService;
        private readonly ElementConfigViewModel _elementConfigVM;

        [ObservableProperty] private ObservableCollection<SampleItemModel> _measurementSequence = new();
        [ObservableProperty] private SampleItemModel? _currentSample;
        [ObservableProperty] private bool _isScanning; // 正在单次扫描

        // 右侧：扫描记录表
        [ObservableProperty] private ObservableCollection<FlowInjectionResultData> _scanRecords = new();
        [ObservableProperty] private FlowInjectionResultData? _selectedScanRecord;

        [ObservableProperty] private ObservableCollection<string> _pickedElements = new();
        [ObservableProperty] private string _selectedElement = string.Empty;

        private List<double> _scanBuffer = new(); // 记录本次扫描的所有强度点

        public FlowInjectionViewModel(JsonConfigService configService, ElementConfigViewModel elementConfigVM)
        {
            _configService = configService;
            _elementConfigVM = elementConfigVM;

            // 订阅序列
            WeakReferenceMessenger.Default.Register<SampleSequenceChangedMessage>(this, (r, m) => {
                MeasurementSequence = new ObservableCollection<SampleItemModel>(m.Value);
            });
        }

        // --- 核心按钮控制 ---

        // 按钮1：开始单次扫描
        [RelayCommand]
        private void StartScan()
        {
            if (CurrentSample == null) return;
            _scanBuffer.Clear();
            IsScanning = true;
            CurrentSample.Status = "正在扫描...";
        }

        // 按钮2：停止扫描并计算峰值
        [RelayCommand]
        private void StopScan()
        {
            IsScanning = false;
            if (_scanBuffer.Count > 0)
            {
                // 计算峰值数据
                var result = new FlowInjectionResultData
                {
                    ScanIndex = ScanRecords.Count + 1,
                    ElementName = SelectedElement,
                    PeakIntensity = _scanBuffer.Max(),
                    BackgroundIntensity = _scanBuffer.Min(),
                    StartTime = 0, // 可根据实际时间戳记录
                    EndTime = _scanBuffer.Count * 0.1
                };
                ScanRecords.Add(result);
            }
        }

        // 按钮3：完成该样品（清空记录，跳到下一个）
        [RelayCommand]
        private void FinishSample()
        {
            if (CurrentSample == null) return;
            CurrentSample.Status = "已完成";
            ScanRecords.Clear(); // 清空右侧记录，为下个样品腾位子

            int currentIndex = MeasurementSequence.IndexOf(CurrentSample);
            if (currentIndex < MeasurementSequence.Count - 1)
                CurrentSample = MeasurementSequence[currentIndex + 1];
            else
                MessageBox.Show("全序列流动注射测量完成！");
        }

        public void AddDataToScan(double intensity)
        {
            if (IsScanning) _scanBuffer.Add(intensity);
        }

        public double GetTargetWavelength()
        {
            var config = _elementConfigVM.SelectedConfigs.FirstOrDefault(x => x.ElementName == SelectedElement);
            return config?.Wavelength ?? 0;
        }
    }
}