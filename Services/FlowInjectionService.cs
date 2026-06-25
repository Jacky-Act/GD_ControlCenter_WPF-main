using CommunityToolkit.Mvvm.Messaging;
using GD_ControlCenter_WPF.Models.Messages;
using GD_ControlCenter_WPF.Models.Spectrometer;
using GD_ControlCenter_WPF.Services.Spectrometer.Logic;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace GD_ControlCenter_WPF.Services
{
    /// <summary>
    /// 流动注射/空间扫描的核心寻峰算法与状态机。
    /// 在人工注入模式下，它默默监听光谱数据，一旦发现强度越过背景阈值，即自动记录峰值参数。
    /// </summary>
    public class FlowInjectionLogic
    {
        // 寻峰状态机枚举
        private enum PeakState { Idle, InPeak }

        // 内部类：追踪单个元素的寻峰状态
        private class ElementPeakTracker
        {
            public PeakState State = PeakState.Idle;
            public int CurrentScanIndex = 1;
            public double Background = 0;
            public double MaxIntensity = 0;
            public double StartTime = 0;
            // 简单动态阈值：背景强度 + 偏置 (可根据实际噪声调整)
            public double Threshold => Background + 150.0;
        }

        private bool _isScanning = false;
        private readonly Stopwatch _stopwatch = new();
        private ConcurrentDictionary<string, ElementPeakTracker> _trackers = new();

        // 当前需要监听的元素及其目标波长字典
        private Dictionary<string, double> _targetElements = new();

        public FlowInjectionLogic()
        {
            // 订阅底层传来的实时光谱数据
            WeakReferenceMessenger.Default.Register<SpectralDataMessage>(this, (r, m) =>
            {
                if (_isScanning) ProcessSpectralData(m.Value);
            });
        }

        /// <summary>
        /// 启动连续监听
        /// </summary>
        /// <param name="elements">需要追踪的元素及波长字典，例如 {"Pb": 283.3, "Cd": 228.8}</param>
        public void StartScanning(Dictionary<string, double> elements)
        {
            _targetElements = elements;
            _trackers.Clear();
            foreach (var el in elements.Keys)
            {
                _trackers[el] = new ElementPeakTracker();
            }

            _isScanning = true;
            _stopwatch.Restart();
        }

        /// <summary>
        /// 停止监听
        /// </summary>
        public void StopScanning()
        {
            _isScanning = false;
            _stopwatch.Stop();
        }

        /// <summary>
        /// 采集背景：将当前光谱的一帧作为所有元素的背景基线
        /// </summary>
        public void CollectBackground(SpectralData data)
        {
            foreach (var kvp in _targetElements)
            {
                string elName = kvp.Key;
                double wl = kvp.Value;
                double intensity = SpectrometerLogic.GetIntensityAtWavelength(data, wl);

                if (_trackers.TryGetValue(elName, out var tracker))
                {
                    tracker.Background = intensity;
                }
            }
        }

        /// <summary>
        /// 核心处理：逐帧剥离强度，推送绘图点，并执行寻峰状态机
        /// </summary>
        private void ProcessSpectralData(SpectralData data)
        {
            double currentTimeSec = _stopwatch.Elapsed.TotalSeconds;

            foreach (var kvp in _targetElements)
            {
                string elName = kvp.Key;
                double targetWl = kvp.Value;

                // 1. 提取该元素在当前帧的强度
                double currentIntensity = SpectrometerLogic.GetIntensityAtWavelength(data, targetWl);

                // 2. 发送时序图绘图消息
                WeakReferenceMessenger.Default.Send(new FlowInjectionPlotMessage(new PlotPoint(elName, currentTimeSec, currentIntensity)));

                // 3. 执行寻峰状态机
                var tracker = _trackers[elName];

                if (tracker.State == PeakState.Idle)
                {
                    // 上升沿：越过阈值，说明人工注入的样品到了
                    if (currentIntensity > tracker.Threshold)
                    {
                        tracker.State = PeakState.InPeak;
                        tracker.StartTime = currentTimeSec;
                        tracker.MaxIntensity = currentIntensity;
                    }
                }
                else if (tracker.State == PeakState.InPeak)
                {
                    // 追踪最高峰值
                    if (currentIntensity > tracker.MaxIntensity)
                    {
                        tracker.MaxIntensity = currentIntensity;
                    }

                    // 下降沿：回到背景阈值附近，说明样品流过去了
                    if (currentIntensity < tracker.Threshold)
                    {
                        tracker.State = PeakState.Idle;

                        // 生成并发送结果消息
                        var result = new FlowInjectionResultData
                        {
                            ElementName = elName,
                            ScanIndex = tracker.CurrentScanIndex++,
                            BackgroundIntensity = tracker.Background,
                            PeakIntensity = tracker.MaxIntensity,
                            StartTime = tracker.StartTime,
                            EndTime = currentTimeSec
                        };

                        WeakReferenceMessenger.Default.Send(new FlowInjectionResultMessage(result));
                    }
                }
            }
        }
    }
}