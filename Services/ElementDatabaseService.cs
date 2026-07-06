using GD_ControlCenter_WPF.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GD_ControlCenter_WPF.Services
{
    public class ElementDatabaseService
    {
        private readonly string _filePath;

        public ElementDatabaseService()
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appFolder = Path.Combine(localData, "GD_ControlCenter");
            if (!Directory.Exists(appFolder)) Directory.CreateDirectory(appFolder);
            
            _filePath = Path.Combine(appFolder, "elements.json");
            
            // 如果不存在配置文件，则生成出厂默认配置并落盘
            if (!File.Exists(_filePath))
            {
                var defaultDb = GenerateDefaultDatabase();
                Save(defaultDb);
            }
        }

        public ElementDatabaseModel Load()
        {
            if (!File.Exists(_filePath)) return GenerateDefaultDatabase();
            try 
            { 
                string json = File.ReadAllText(_filePath);
                
                // 自动执行一次热迁移：如果存在旧的元素层级字段，将其下放到波长层级并重写
                bool needsMigration = false;
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("Elements", out var elementsProp))
                    {
                        foreach (var elem in elementsProp.EnumerateObject())
                        {
                            if (elem.Value.TryGetProperty("IntegrationTime", out _) || elem.Value.TryGetProperty("SavedCurves", out _))
                            {
                                needsMigration = true;
                                break;
                            }
                        }
                    }
                }

                if (needsMigration)
                {
                    var migratedDb = MigrateFromV1ToV2(json);
                    Save(migratedDb); // 迁移后立即保存回硬盘，完成平滑升级
                    return migratedDb;
                }

                var dbObj = JsonSerializer.Deserialize<ElementDatabaseModel>(json);
                return dbObj ?? GenerateDefaultDatabase(); 
            }
            catch 
            { 
                return GenerateDefaultDatabase(); 
            }
        }

        private ElementDatabaseModel MigrateFromV1ToV2(string json)
        {
            var db = GenerateDefaultDatabase(); // 用默认配置做底板
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Elements", out var elementsProp))
                {
                    foreach (var elem in elementsProp.EnumerateObject())
                    {
                        if (db.Elements.TryGetValue(elem.Name, out var newElemConfig))
                        {
                            var oldConfig = elem.Value;
                            
                            int integrationTime = 200;
                            if (oldConfig.TryGetProperty("IntegrationTime", out var intTimeProp)) integrationTime = intTimeProp.GetInt32();
                            
                            int averagingCount = 1;
                            if (oldConfig.TryGetProperty("AveragingCount", out var avgProp)) averagingCount = avgProp.GetInt32();

                            var fittingCurves = new List<string> { "测量校准曲线" };
                            if (oldConfig.TryGetProperty("FittingCurves", out var fcProp))
                            {
                                fittingCurves = JsonSerializer.Deserialize<List<string>>(fcProp.GetRawText()) ?? fittingCurves;
                            }

                            var savedCurves = new List<CalibrationCurveModel>();
                            if (oldConfig.TryGetProperty("SavedCurves", out var scProp))
                            {
                                savedCurves = JsonSerializer.Deserialize<List<CalibrationCurveModel>>(scProp.GetRawText()) ?? savedCurves;
                            }
                            
                            // 旧系统无法区分曲线属于哪个波长，故将历史参数与曲线复制到该元素的所有波长配置下，供用户自行甄别
                            foreach (var wConfig in newElemConfig.Wavelengths)
                            {
                                wConfig.IntegrationTime = integrationTime;
                                wConfig.AveragingCount = averagingCount;
                                wConfig.FittingCurves = new List<string>(fittingCurves);
                                wConfig.SavedCurves = JsonSerializer.Deserialize<List<CalibrationCurveModel>>(JsonSerializer.Serialize(savedCurves)) ?? new();
                            }
                        }
                    }
                }
            }
            catch { }
            return db;
        }

        public void Save(ElementDatabaseModel db)
        {
            try 
            { 
                var options = new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                };
                File.WriteAllText(_filePath, JsonSerializer.Serialize(db, options)); 
            }
            catch (Exception ex)
            { 
                System.Diagnostics.Debug.WriteLine($"Failed to save DB: {ex}");
            }
        }

        private ElementDatabaseModel GenerateDefaultDatabase()
        {
            var db = new ElementDatabaseModel();
            
            var defaultWavelengths = new Dictionary<string, List<double>>
            {
                { "Sn", new List<double> { 242.95, 236.48, 359.26, 442.43 } },
                { "Sr", new List<double> { 460.77, 242.81 } },
                { "Ta", new List<double> { 271.47, 255.94 } },
                { "Tb", new List<double> { 432.65, 390.14 } },
                { "Te", new List<double> { 214.30, 225.90 } },
                { "Ti", new List<double> { 364.27, 399.86 } },
                { "Tl", new List<double> { 377.60, 276.78 } },
                { "U", new List<double> { 351.46, 415.40 } },
                { "V", new List<double> { 318.39, 437.92 } },
                { "W", new List<double> { 255.14, 265.65 } },
                { "Y", new List<double> { 407.74, 410.24 } },
                { "Yb", new List<double> { 398.80, 346.44 } },
                { "Zn", new List<double> { 214.03, 213.09 } },
                { "Zr", new List<double> { 360.12, 301.18 } },
                { "Ag", new List<double> { 328.96 } },
                { "As", new List<double> { 338.81, 228.20, 193.69 } },
                { "Al", new List<double> { 309.27, 396.15 } },
                { "Au", new List<double> { 242.79, 267.59 } },
                { "B", new List<double> { 249.68, 249.77 } },
                { "Ba", new List<double> { 455.40 } },
                { "Be", new List<double> { 234.86 } },
                { "Bi", new List<double> { 313.04 } },
                { "Ca", new List<double> { 423.28, 422.81 } },
                { "Co", new List<double> { 240.72, 242.49 } },
                { "Cd", new List<double> { 228.78, 361.05 } },
                { "Cr", new List<double> { 357.56, 359.35 } },
                { "Cs", new List<double> { 852.30, 894.35 } },
                { "Cu", new List<double> { 324.75, 327.39 } },
                { "Pd", new List<double> { 344.14, 340.45 } },
                { "Pr", new List<double> { 390.84, 414.31 } },
                { "Pt", new List<double> { 265.95, 214.42 } },
                { "Rb", new List<double> { 780.60, 795.10 } },
                { "Re", new List<double> { 204.90, 228.75 } },
                { "Rh", new List<double> { 437.50, 339.68 } },
                { "Ru", new List<double> { 349.89, 372.80 } },
                { "Sb", new List<double> { 231.10, 206.83 } },
                { "Se", new List<double> { 361.38, 363.07, 196.09, 203.99 } },
                { "Si", new List<double> { 251.61, 251.43 } },
                { "K", new List<double> { 766.49, 766.95 } },
                { "La", new List<double> { 333.75, 379.47 } },
                { "Li", new List<double> { 670.78, 670.95 } },
                { "Lu", new List<double> { 261.54, 296.33 } },
                { "Mg", new List<double> { 285.63, 518.27 } },
                { "Mn", new List<double> { 279.48, 279.96 } },
                { "Mo", new List<double> { 313.26, 317.04 } },
                { "Na", new List<double> { 589.38, 589.59 } },
                { "Nb", new List<double> { 309.42, 316.34 } },
                { "Nd", new List<double> { 401.23, 430.36 } },
                { "Ni", new List<double> { 341.63, 352.88 } },
                { "Os", new List<double> { 225.50, 305.86 } },
                { "Pb", new List<double> { 368.78, 406.08 } },
                { "Dy", new List<double> { 353.17, 394.47 } },
                { "Er", new List<double> { 337.27, 349.91 } },
                { "Eu", new List<double> { 381.96, 412.97 } },
                { "Fe", new List<double> { 248.72, 252.28 } },
                { "Ga", new List<double> { 294.36, 417.21 } },
                { "Gd", new List<double> { 342.25, 336.22 } },
                { "Ge", new List<double> { 265.16, 209.43 } },
                { "Hf", new List<double> { 339.98, 277.33 } },
                { "Hg", new List<double> { 253.65, 404.66 } },
                { "Ho", new List<double> { 345.60, 339.89 } },
                { "In", new List<double> { 451.10, 230.60 } },
                { "Ir", new List<double> { 224.27, 212.68 } }
            };

            foreach (var kvp in defaultWavelengths)
            {
                var config = new ElementConfig
                {
                    Wavelengths = kvp.Value.Select(w => new WavelengthConfig { 
                        Wavelength = w,
                        IntegrationTime = 200,
                        AveragingCount = 1,
                        FittingCurves = new List<string> { "测量校准曲线" }
                    }).ToList()
                };
                db.Elements[kvp.Key] = config;
            }

            return db;
        }
    }
}
