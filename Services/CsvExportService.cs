using GD_ControlCenter_WPF.Models.Spectrometer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace GD_ControlCenter_WPF.Services
{
    public class CsvExportService
    {
        /// <summary>
        /// 将多次光谱采集数据按列格式导出为 CSV，第一列是波长，后续每列是一次测量的全谱数据。
        /// </summary>
        /// <param name="filePath">输出文件路径</param>
        /// <param name="columns">列头名称和对应的光谱数据，列头中可包含测试时间、元素、参数等信息</param>
        public static async Task ExportSpectralDataColumnsAsync(string filePath, List<(string Header, SpectralData Data)> columns)
        {
            if (columns == null || columns.Count == 0) return;

            var firstData = columns[0].Data;
            if (firstData?.Wavelengths == null) return;

            int pixelCount = firstData.Wavelengths.Length;

            // 确保目录存在
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // 使用 StreamWriter 异步写入以减少 IO 阻塞
            using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
            {
                // 1. 写入表头行
                var headerLine = new StringBuilder();
                headerLine.Append("波长(nm)");
                foreach (var col in columns)
                {
                    headerLine.Append($",{col.Header.Replace(",", " ")}"); // 防止表头中有逗号破坏 CSV 格式
                }
                await writer.WriteLineAsync(headerLine.ToString());

                // 2. 逐行写入光谱数据点
                for (int i = 0; i < pixelCount; i++)
                {
                    var line = new StringBuilder();
                    // 波长第一列
                    line.Append($"{firstData.Wavelengths[i]:F4}");

                    // 遍历每一列的光强
                    foreach (var col in columns)
                    {
                        if (col.Data?.Intensities != null && i < col.Data.Intensities.Length)
                        {
                            line.Append($",{col.Data.Intensities[i]:F2}");
                        }
                        else
                        {
                            line.Append(","); // 如果该列数据异常，则留空
                        }
                    }
                    await writer.WriteLineAsync(line.ToString());
                }
            }
        }
    }
}
