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
            string bakPath = _filePath + ".bak";
            AppConfig? config = null;

            // 1. 先尝试读取主配置文件
            if (File.Exists(_filePath))
            {
                try { config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_filePath)); }
                catch { /* 主文件损坏，忽略并尝试读取备份 */ }
            }

            // 2. 如果主文件损坏或不存在，尝试读取备份文件
            if (config == null && File.Exists(bakPath))
            {
                try { config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(bakPath)); }
                catch { }
            }

            // 3. 如果都失败，返回新的默认配置
            return config ?? new AppConfig();
        }

        public void Save(AppConfig config)
        {
            lock (this) // 防止并发写入竞争
            {
                string tmpPath = _filePath + ".tmp";
                string bakPath = _filePath + ".bak";

                try 
                { 
                    string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

                    // 1. 写入临时文件，并强制系统将缓存直接刷入物理介质（防突然断电的核心）
                    using (FileStream fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (StreamWriter sw = new StreamWriter(fs))
                    {
                        sw.Write(json);
                        sw.Flush();
                        fs.Flush(true); // 这一步强制操作系统不要仅仅把数据留在内存中，而是必须落盘
                    }

                    // 2. 备份主配置文件
                    if (File.Exists(_filePath))
                    {
                        File.Copy(_filePath, bakPath, true);
                    }

                    // 3. 原子化替换：将临时文件重命名为主配置文件
                    File.Move(tmpPath, _filePath, true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[JsonConfigService.Save] 保存配置失败: {ex.Message}");
                }
            }
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