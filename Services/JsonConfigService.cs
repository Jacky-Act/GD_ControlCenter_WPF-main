using GD_ControlCenter_WPF.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GD_ControlCenter_WPF.Services
{
    public class JsonConfigService
    {
        private readonly string _filePath;
        private readonly string _appFolder;

        public JsonConfigService()
        {
            // 立即初始化路径，防止为 null
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _appFolder = Path.Combine(localData, "GD_ControlCenter");

            if (!Directory.Exists(_appFolder)) Directory.CreateDirectory(_appFolder);
            _filePath = Path.Combine(_appFolder, "config.json");
        }

        public AppConfig Load()
        {
            if (!File.Exists(_filePath)) return new AppConfig();
            try { return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_filePath)) ?? new AppConfig(); }
            catch { return new AppConfig(); }
        }

        public void Save(AppConfig config)
        {
            try { File.WriteAllText(_filePath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true })); }
            catch { }
        }

        public void SaveResults(List<GD_ControlCenter_WPF.Models.Messages.SampleItemModel> results)
        {
            try
            {
                string resFolder = Path.Combine(_appFolder, "Results");
                if (!Directory.Exists(resFolder)) Directory.CreateDirectory(resFolder);

                string path = Path.Combine(resFolder, $"Result_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                File.WriteAllText(path, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"保存失败: {ex.Message}"); }
        }
        #region 连续进样数据结果导出
        public void ExportResults(string path, List<GD_ControlCenter_WPF.Models.Messages.SampleItemModel> data)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(data, options);
            File.WriteAllText(path, json);
        }

        public List<GD_ControlCenter_WPF.Models.Messages.SampleItemModel>? ImportResults(string path)
        {
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<GD_ControlCenter_WPF.Models.Messages.SampleItemModel>>(json);
        }
        #endregion
    }
}