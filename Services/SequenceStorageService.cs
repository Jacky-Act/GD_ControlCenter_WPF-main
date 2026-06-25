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

        // 导出 CSV (包含动态浓度列)
        public void ExportToCsv(string filePath, List<SampleItemModel> sequence, List<string> activeElements)
        {
            using var writer = new StreamWriter(filePath, false, System.Text.Encoding.UTF8);
            var header = new List<string> { "样品名称", "类型", "重复次数" };
            header.AddRange(activeElements.Select(e => $"{e}浓度"));
            writer.WriteLine(string.Join(",", header));

            foreach (var item in sequence)
            {
                var row = new List<string> { item.SampleName, item.Type.ToString(), item.Repeats.ToString() };
                foreach (var el in activeElements)
                {
                    var conc = item.ElementConcentrations.FirstOrDefault(c => c.ElementName == el)?.ConcentrationValue ?? "";
                    row.Add(conc);
                }
                writer.WriteLine(string.Join(",", row));
            }
        }

        // 导入 CSV
        public List<SampleItemModel> ImportFromCsv(string filePath, out List<string> detectedElements)
        {
            var list = new List<SampleItemModel>();
            detectedElements = new List<string>();
            var lines = File.ReadAllLines(filePath);
            if (lines.Length < 1) return list;

            var headers = lines[0].Split(',');
            for (int i = 3; i < headers.Length; i++) detectedElements.Add(headers[i].Replace("浓度", ""));

            for (int i = 1; i < lines.Length; i++)
            {
                var cells = lines[i].Split(',');
                if (cells.Length < 3) continue;
                var item = new SampleItemModel
                {
                    SampleName = cells[0],
                    Type = Enum.TryParse(cells[1], out SampleType t) ? t : SampleType.待测液,
                    Repeats = int.TryParse(cells[2], out int r) ? r : 1
                };
                for (int j = 0; j < detectedElements.Count; j++)
                {
                    item.ElementConcentrations.Add(new ElementConcentrationModel { ElementName = detectedElements[j], ConcentrationValue = (cells.Length > j + 3) ? cells[j + 3] : "" });
                }
                list.Add(item);
            }
            return list;
        }
    }
}