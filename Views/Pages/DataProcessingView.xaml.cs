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
                    vm.RequestPlotUpdate = (slope, intercept, points, unit, isValidFit) => {
                        UpdateChart(slope, intercept, points, unit, isValidFit);
                    };

                    vm.CapturePlotImageAction = () => {
                        string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"plot_{Guid.NewGuid()}.png");
                        CalibrationPlot.Plot.SavePng(tempPath, 600, 400);
                        return tempPath;
                    };
                }
            };
        }

        private void UpdateChart(double k, double b, System.Collections.Generic.List<StandardPointRow> points, string unit, bool isValidFit)
        {
            CalibrationPlot.Plot.Clear();

            double minX = 0;
            double maxX = 100;

            if (points != null && points.Count > 0)
            {
                // 1. 画标准点 (散点)
                double[] xs = points.Select(p => p.Concentration).ToArray();
                double[] ys = points.Select(p => p.Intensity).ToArray();

                minX = xs.Min();
                maxX = xs.Max() * 1.1;
                if (Math.Abs(maxX - minX) < 1e-5) maxX = minX + 10; 

                var sp = CalibrationPlot.Plot.Add.Scatter(xs, ys);
                sp.LineWidth = 0; // 不连线
                sp.MarkerSize = 12;
                sp.Color = ScottPlot.Color.FromHex("#1976D2"); // 主题蓝
            }

            if (isValidFit)
            {
                // 2. 画拟合线 (从 x=0 开始绘制)
                var line = CalibrationPlot.Plot.Add.Line(0, b, maxX, k * maxX + b);
                line.Color = ScottPlot.Color.FromHex("#F44336"); // 醒目红
                line.LineWidth = 2;
            }

            // 设置坐标轴标签和字体，使用英文以防止中文在部分系统中显示为方块
            CalibrationPlot.Plot.Axes.Bottom.Label.Text = $"Concentration ({unit})";
            CalibrationPlot.Plot.Axes.Left.Label.Text = "Intensity";
            
            // 可以去掉中文字体的强制指定，使用默认字体
            // CalibrationPlot.Plot.Axes.Bottom.Label.FontName = "Microsoft YaHei";
            // CalibrationPlot.Plot.Axes.Left.Label.FontName = "Microsoft YaHei";

            // 设置网格颜色，让图表看起来更清爽
            CalibrationPlot.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#eeeeee");

            CalibrationPlot.Plot.Axes.AutoScale();

            CalibrationPlot.Refresh();
        }
    }
}