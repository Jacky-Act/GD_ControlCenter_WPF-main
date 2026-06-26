using System.Collections.Generic;

namespace GD_ControlCenter_WPF.Models
{
    public class WavelengthConfig
    {
        public double Wavelength { get; set; }
    }

    public class ElementConfig
    {
        public List<WavelengthConfig> Wavelengths { get; set; } = new();
        public int IntegrationTime { get; set; } = 200;
        public int AveragingCount { get; set; } = 1;
        public List<string> FittingCurves { get; set; } = new() { "测量校准曲线" };
    }

    public class ElementDatabaseModel
    {
        public Dictionary<string, ElementConfig> Elements { get; set; } = new();
    }
}
