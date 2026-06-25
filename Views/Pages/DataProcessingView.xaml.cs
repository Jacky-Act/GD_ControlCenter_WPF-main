using System;
using System.Collections.Generic;
using GD_ControlCenter_WPF.ViewModels;
using System.Windows.Controls;
using System.Linq;

namespace GD_ControlCenter_WPF.Views.Pages
{
    public partial class DataProcessingView : UserControl
    {
        public DataProcessingView()
        {
            InitializeComponent();
            this.DataContextChanged += (s, e) => {
                if (DataContext is DataProcessingViewModel vm)
                {
                    // 订阅绘图请求
                    vm.RequestPlotUpdate = (slope, intercept, points) => {
                        UpdateChart(slope, intercept, points);
                    };
                }
            };
        }

        private void UpdateChart(double k, double b, System.Collections.Generic.List<StandardPointRow> points)
        {
            CalibrationPlot.Plot.Clear();

            // 1. 画标准点 (散点)
            double[] xs = points.Select(p => p.Concentration).ToArray();
            double[] ys = points.Select(p => p.Intensity).ToArray();
            var sp = CalibrationPlot.Plot.Add.Scatter(xs, ys);
            sp.LineWidth = 0; // 不连线
            sp.MarkerSize = 10;
            sp.Color = ScottPlot.Colors.Orange;

            // 2. 画拟合线
            if (xs.Length > 0)
            {
                double minX = xs.Min();
                double maxX = xs.Max() * 1.1;
                // 根据 y = kx + b 计算起始和终止点
                var line = CalibrationPlot.Plot.Add.Line(minX, k * minX + b, maxX, k * maxX + b);
                line.Color = ScottPlot.Colors.Cyan;
                line.LineWidth = 2;
            }

            CalibrationPlot.Plot.Axes.AutoScale();
            CalibrationPlot.Refresh();
        }
    }
}