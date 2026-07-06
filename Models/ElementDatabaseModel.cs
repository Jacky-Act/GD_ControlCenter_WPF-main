using System.Collections.Generic;

namespace GD_ControlCenter_WPF.Models
{
    public class WavelengthConfig
    {
        public double Wavelength { get; set; }
    }

    public class PointModel
    {
        public string Name { get; set; } = string.Empty;
        public double Concentration { get; set; }
        public double Intensity { get; set; }
        public double RSD { get; set; }
    }

    public class CalibrationCurveModel
    {
        public string Name { get; set; } = string.Empty;
        public double Slope { get; set; }
        public double Intercept { get; set; }
        public double RSquared { get; set; }
        public string Equation { get; set; } = string.Empty;
        public double Lod { get; set; }
        public string SaveTime { get; set; } = string.Empty;
        public List<PointModel> Points { get; set; } = new();
    }

    public class ElementConfig
    {
        public List<WavelengthConfig> Wavelengths { get; set; } = new();
        public int IntegrationTime { get; set; } = 200;
        public int AveragingCount { get; set; } = 1;
        // 兼容旧数据的名字列表，UI下拉框绑定
        public List<string> FittingCurves { get; set; } = new() { "测量校准曲线" };
        
        // 新增：保存的真实曲线参数
        public List<CalibrationCurveModel> SavedCurves { get; set; } = new();
    }

    public class ElementDatabaseModel
    {
        public Dictionary<string, ElementConfig> Elements { get; set; } = new();
    }
}
