using GD_ControlCenter_WPF.Models.Messages;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GD_ControlCenter_WPF.Services
{
    public class SequenceStorageService
    {
        private readonly string _folderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sequences");

        public SequenceStorageService()
        {
            if (!Directory.Exists(_folderPath)) Directory.CreateDirectory(_folderPath);
        }

        public string GetFolderPath() => _folderPath;

        // --- 新增：物理删除本地所有模板文件 ---
        public void ClearAllTemplates()
        {
            if (Directory.Exists(_folderPath))
            {
                // 获取所有以 .seq 结尾的文件
                var files = Directory.GetFiles(_folderPath, "*.seq");
                foreach (var file in files)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"删除模板文件失败: {file}, 错误: {ex.Message}");
                    }
                }
            }
        }

        // 获取所有保存的模板名称
        public List<string> GetSavedTemplates()
        {
            if (!Directory.Exists(_folderPath)) return new List<string>();
            var files = Directory.GetFiles(_folderPath, "*.seq");
            var names = new List<string>();
            foreach (var f in files) names.Add(Path.GetFileNameWithoutExtension(f));
            return names;
        }

        // 保存为专属模板
        public void SaveTemplate(string templateName, List<SampleItemModel> sequence)
        {
            string path = Path.Combine(_folderPath, $"{templateName}.seq");
            string json = JsonSerializer.Serialize(sequence, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        // 加载专属模板
        public List<SampleItemModel>? LoadTemplate(string templateName)
        {
            string path = Path.Combine(_folderPath, $"{templateName}.seq");
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<SampleItemModel>>(json);
        }
    }
}