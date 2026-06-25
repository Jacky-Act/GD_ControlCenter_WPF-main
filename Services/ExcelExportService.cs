using GD_ControlCenter_WPF.Models.Spectrometer;
using OfficeOpenXml;
using System.IO;
using System.Text;

/*
 * 文件名: ExcelExportService.cs
 * 描述: 光谱数据导出与持久化服务类。支持将实时采集数据、脚本批量全谱数据及时间序列轨迹导出为标准 XLSX 或 CSV 格式。
 * 本服务采用 EPPlus 引擎，核心优化点在于通过内存二维矩阵（object[,]）进行批量数据注入（Bulk Insert），以保障百万级数据点落盘时的高性能与 UI 零卡顿。
 * 维护指南: 修改导出模板（如行偏移、列索引）时必须同步更新 Import 反向解析引擎逻辑；导出 Excel 需确保在 Task.Run 异步上下文中执行。
 */

namespace GD_ControlCenter_WPF.Services
{
    /// <summary>
    /// Excel/CSV 导出引擎：负责光谱测量数据的持久化存储与反序列化导入。
    /// </summary>
    public class ExcelExportService : IDisposable
    {
        #region 1. 状态与流对象

        /// <summary>
        /// 用于 CSV 流式持续写入的文本流包装器。
        /// </summary>
        private StreamWriter? _writer;

        /// <summary>
        /// 当前正在操作的临时 CSV 文件物理路径。
        /// </summary>
        private string? _currentCsvPath;

        #endregion

        #region 2. 传统 CSV 流式追加引擎

        /// <summary>
        /// 开启一个新的流式写入会话。
        /// 适用于长时间运行且无法一次性完全放入内存的超大规模数据记录任务。
        /// </summary>
        /// <param name="fullPath">预定的目标文件路径。</param>
        public void StartSession(string fullPath)
        {
            // 开启新会话前强制清理并关闭可能存在的旧会话
            EndSession();

            // 临时存储为 CSV 格式以支持极速追加，并确保后续转换逻辑可识别
            _currentCsvPath = Path.ChangeExtension(fullPath, ".csv");

            // 使用 UTF-8 with BOM 编码，确保导出的 CSV 文件直接使用 Windows Excel 打开时中文不乱码
            _writer = new StreamWriter(_currentCsvPath, false, Encoding.UTF8);
        }

        /// <summary>
        /// 向当前 CSV 会话中写入单行数据。
        /// </summary>
        /// <param name="csvLine">已格式化的 CSV 字符串行。</param>
        public void WriteLine(string csvLine)
        {
            if (_writer == null) return;

            _writer.WriteLine(csvLine);

            // 实时刷新缓冲区。虽然牺牲极小性能，但能确保程序崩溃时数据不丢失。
            _writer.Flush();
        }

        /// <summary>
        /// 结束当前的写入会话，释放文件系统级锁定并销毁对象。
        /// </summary>
        public void EndSession()
        {
            if (_writer != null)
            {
                _writer.Flush();
                _writer.Close();
                _writer.Dispose();
                _writer = null;
            }
        }

        /// <summary>
        /// 异步将累积生成的临时 CSV 文件转换为带有格式样式的正式 XLSX 电子表格。
        /// </summary>
        /// <param name="deleteCsv">转换完成后是否物理删除原始 CSV 临时文件。</param>
        /// <returns>最终生成的 XLSX 文件完整路径。</returns>
        public async Task<string> ConvertToExcelAsync(bool deleteCsv = true)
        {
            if (string.IsNullOrEmpty(_currentCsvPath) || !File.Exists(_currentCsvPath))
                return string.Empty;

            string xlsxPath = Path.ChangeExtension(_currentCsvPath, ".xlsx");

            await Task.Run(() =>
            {
                // EPPlus 8+ 强制要求声明许可类型
                ExcelPackage.License.SetNonCommercialPersonal("个人用户");

                using (var package = new ExcelPackage(new FileInfo(xlsxPath)))
                {
                    var sheet = package.Workbook.Worksheets.Add("Data Log");

                    // 利用 EPPlus 原生 LoadFromText 方法执行批量 CSV 载入，速度远超逐行读取写入
                    var format = new ExcelTextFormat { Delimiter = ',', Encoding = Encoding.UTF8 };
                    sheet.Cells["A1"].LoadFromText(new FileInfo(_currentCsvPath), format);

                    // 界面冻结首行表头
                    sheet.View.FreezePanes(2, 1);
                    package.Save();
                }
            });

            // 清理临时文件逻辑
            if (deleteCsv && File.Exists(_currentCsvPath))
            {
                try { File.Delete(_currentCsvPath); } catch { /* 容错：忽略删除失败 */ }
            }

            return xlsxPath;
        }

        #endregion

        #region 3. 内存二维矩阵直接落盘引擎 (Bulk Memory Insert)

        /// <summary>
        /// 异步导出单幅光谱快照数据。
        /// 通过在内存中构建 object[,] 矩阵并一次性拍入 Excel 的方式实现极致导出性能。
        /// </summary>
        /// <param name="targetPath">输出路径。</param>
        /// <param name="captureTime">测量时间戳。</param>
        /// <param name="integrationTime">曝光积分时间。</param>
        /// <param name="avgCount">硬件平均次数。</param>
        /// <param name="wavelengths">波长 X 轴数组。</param>
        /// <param name="intensities">光强 Y 轴数组。</param>
        /// <returns>成功后的文件路径。</returns>
        public async Task<string> ExportSingleSpectrumAsync(string targetPath, DateTime captureTime, float integrationTime, uint avgCount, double[] wavelengths, double[] intensities)
        {
            if (wavelengths == null || intensities == null || wavelengths.Length != intensities.Length)
                return string.Empty;

            return await Task.Run(() =>
            {
                ExcelPackage.License.SetNonCommercialPersonal("个人用户");

                using (var package = new ExcelPackage(new FileInfo(targetPath)))
                {
                    var sheet = package.Workbook.Worksheets.Add("单幅光谱");

                    // --- 1. 写入表头元数据 (Metadata) ---
                    sheet.Cells["A1"].Value = "时间:";
                    sheet.Cells["B1"].Value = captureTime.ToString("yyyy/MM/dd HH:mm:ss");
                    sheet.Cells["A2"].Value = "积分时间:";
                    sheet.Cells["B2"].Value = "平均次数:";
                    sheet.Cells["A3"].Value = integrationTime;
                    sheet.Cells["B3"].Value = avgCount;
                    sheet.Cells["A5"].Value = "波长";
                    sheet.Cells["B5"].Value = "强度";

                    // --- 2. 二维矩阵极速注入 (Core Magic) ---
                    // 先分配 object[,] 空间，避免在循环中访问 EPPlus 的 Cells 集合。
                    int startRow = 6;
                    int dataCount = wavelengths.Length;
                    object[,] dataRange = new object[dataCount, 2];

                    for (int i = 0; i < dataCount; i++)
                    {
                        dataRange[i, 0] = wavelengths[i];
                        dataRange[i, 1] = intensities[i];
                    }

                    // 将构建好的内存矩阵一次性赋值给指定的单元格范围
                    sheet.Cells[startRow, 1, startRow + dataCount - 1, 2].Value = dataRange;

                    // --- 3. 格式美化与冻结窗格 ---
                    sheet.View.FreezePanes(6, 1);
                    sheet.Cells["A1:B2"].Style.Font.Bold = true;
                    sheet.Cells["A5:B5"].Style.Font.Bold = true;
                    sheet.Cells["A1:B5"].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;
                    sheet.Cells[startRow, 1, startRow + dataCount - 1, 2].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Right;

                    // 数值格式设定为两位小数
                    sheet.Cells[startRow, 1, startRow + dataCount - 1, 1].Style.Numberformat.Format = "0.00";
                    sheet.Cells[startRow, 2, startRow + dataCount - 1, 2].Style.Numberformat.Format = "0.00";

                    sheet.Column(1).AutoFit();
                    sheet.Column(2).AutoFit();

                    package.Save();
                }
                return targetPath;
            });
        }

        /// <summary>
        /// 异步批量导出自动化脚本采集的多帧光谱数据。
        /// 采用“按列填充”策略，左侧为基准波长，右侧顺序排列各帧强度。
        /// </summary>
        public async Task<bool> ExportScriptSpectraBulkAsync(string targetPath, List<SpectralData> dataList, float integrationTime, uint averagingCount)
        {
            if (dataList == null || dataList.Count == 0) return false;

            return await Task.Run(() =>
            {
                try
                {
                    ExcelPackage.License.SetNonCommercialPersonal("个人用户");
                    if (File.Exists(targetPath)) File.Delete(targetPath);

                    using (var package = new ExcelPackage(new FileInfo(targetPath)))
                    {
                        var sheet = package.Workbook.Worksheets.Add("自动保存记录");
                        var firstFrame = dataList[0];

                        // --- 1. 写入公共基准信息与 A 列波长 ---
                        sheet.Cells["A1"].Value = "时间:";
                        sheet.Cells["A2"].Value = "积分时间:";
                        sheet.Cells["A3"].Value = "平均次数:";
                        sheet.Cells["A5"].Value = "波长";

                        int totalPoints = firstFrame.Wavelengths.Length;
                        object[,] waveData = new object[totalPoints, 1];
                        for (int i = 0; i < totalPoints; i++) waveData[i, 0] = firstFrame.Wavelengths[i];

                        sheet.Cells[6, 1, totalPoints + 5, 1].Value = waveData;
                        sheet.Column(1).AutoFit();
                        sheet.View.FreezePanes(6, 2);

                        // --- 2. 遍历列表，按列批量写入强度数据 ---
                        for (int i = 0; i < dataList.Count; i++)
                        {
                            var frame = dataList[i];
                            int col = i + 2; // 从 B 列开始写入强度数据

                            // 单列元数据表头
                            sheet.Cells[1, col].Value = frame.AcquisitionTime.ToString("HH:mm:ss.fff");
                            sheet.Cells[2, col].Value = integrationTime;
                            sheet.Cells[3, col].Value = averagingCount;
                            sheet.Cells[5, col].Value = $"强度_{i + 1}";

                            // 构建单列强度矩阵
                            object[,] intensityData = new object[totalPoints, 1];
                            for (int j = 0; j < totalPoints; j++) intensityData[j, 0] = frame.Intensities[j];

                            sheet.Cells[6, col, totalPoints + 5, col].Value = intensityData;
                            sheet.Cells[6, col, totalPoints + 5, col].Style.Numberformat.Format = "0.00";
                            sheet.Column(col).AutoFit();
                        }

                        // 批量设置表头样式
                        sheet.Cells[1, 1, 5, dataList.Count + 1].Style.Font.Bold = true;
                        sheet.Cells[1, 1, 5, dataList.Count + 1].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;

                        package.Save();
                    }
                    return true;
                }
                catch { return false; }
            });
        }

        /// <summary>
        /// 异步批量导出时序图轨迹快照。
        /// 职责：将多组特征峰的强度轨迹按时间维度平铺落盘。
        /// </summary>
        public async Task<bool> ExportTimeSeriesBulkAsync(string targetPath, List<string> trackPointNames, List<TimeSeriesSnapshot> dataList, float integrationTime, uint averagingCount)
        {
            if (dataList == null || dataList.Count == 0 || trackPointNames == null || trackPointNames.Count == 0)
                return false;

            return await Task.Run(() =>
            {
                try
                {
                    ExcelPackage.License.SetNonCommercialPersonal("个人用户");
                    if (File.Exists(targetPath)) File.Delete(targetPath);

                    using (var package = new ExcelPackage(new FileInfo(targetPath)))
                    {
                        var sheet = package.Workbook.Worksheets.Add("时序图数据");

                        // --- 1. 写入元数据与特征峰列名表头 ---
                        sheet.Cells["A1"].Value = "任务开始时间:";
                        sheet.Cells["B1"].Value = dataList[0].Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                        sheet.Cells["A2"].Value = "积分时间(ms):";
                        sheet.Cells["B2"].Value = integrationTime;
                        sheet.Cells["A3"].Value = "平均次数:";
                        sheet.Cells["B3"].Value = averagingCount;

                        sheet.Cells["A5"].Value = "时间 (Time)";
                        for (int i = 0; i < trackPointNames.Count; i++)
                        {
                            sheet.Cells[5, i + 2].Value = trackPointNames[i];
                        }

                        // --- 2. 构建全域大矩阵：单次拍入性能最高 ---
                        int rowCount = dataList.Count;
                        int colCount = trackPointNames.Count + 1;
                        object[,] exportDataMatrix = new object[rowCount, colCount];

                        for (int r = 0; r < rowCount; r++)
                        {
                            var snapshot = dataList[r];
                            exportDataMatrix[r, 0] = snapshot.Timestamp.ToString("HH:mm:ss.fff");

                            for (int c = 0; c < trackPointNames.Count; c++)
                            {
                                // 安全校验：防止 UI 在导出瞬间动态删减峰线导致的数组越界
                                exportDataMatrix[r, c + 1] = (c < snapshot.Intensities.Length) ? snapshot.Intensities[c] : 0;
                            }
                        }

                        // 执行矩阵块状写入
                        sheet.Cells[6, 1, rowCount + 5, colCount].Value = exportDataMatrix;

                        // 最终格式渲染
                        sheet.Cells[1, 1, 5, colCount].Style.Font.Bold = true;
                        sheet.Cells[6, 2, rowCount + 5, colCount].Style.Numberformat.Format = "0.00";
                        sheet.Cells[1, 1, rowCount + 5, colCount].AutoFitColumns();
                        sheet.View.FreezePanes(6, 2);

                        package.Save();
                    }
                    return true;
                }
                catch { return false; }
            });
        }

        #endregion

        #region 4. 反向解析引擎 (Import)

        /// <summary>
        /// 异步反解析 XLSX 光谱文件，将其恢复为内存中的 SpectralData 对象。
        /// 常用于前端 UI 的“参考图层”比对。
        /// </summary>
        /// <param name="filePath">XLSX 文件物理路径。</param>
        /// <returns>反序列化后的光谱实体数据对象。</returns>
        public async Task<SpectralData?> ImportSingleSpectrumAsync(string filePath)
        {
            if (!File.Exists(filePath)) return null;

            return await Task.Run(() =>
            {
                try
                {
                    ExcelPackage.License.SetNonCommercialPersonal("个人用户");
                    using var package = new ExcelPackage(new FileInfo(filePath));
                    var sheet = package.Workbook.Worksheets.FirstOrDefault();

                    if (sheet == null) return null;

                    // 读取元数据信息
                    string timeStr = sheet.Cells["B1"].Text;
                    DateTime.TryParse(timeStr, out DateTime acqTime);

                    // 数据有效性评估：排除 5 行固定表头
                    int rowCount = sheet.Dimension.Rows;
                    if (rowCount < 6) return null;

                    int dataCount = rowCount - 5;
                    double[] wavelengths = new double[dataCount];
                    double[] intensities = new double[dataCount];

                    // 循环读取单元格内容：Import 场景下读取量级通常较小，允许单点读取
                    for (int i = 0; i < dataCount; i++)
                    {
                        int row = i + 6;
                        double.TryParse(sheet.Cells[row, 1].Text, out wavelengths[i]);
                        double.TryParse(sheet.Cells[row, 2].Text, out intensities[i]);
                    }

                    return new SpectralData
                    {
                        Wavelengths = wavelengths,
                        Intensities = intensities,
                        AcquisitionTime = acqTime,
                        SourceDeviceSerial = "Reference" // 标记特殊序列号，告知 UI 这是一个静态比对图层
                    };
                }
                catch
                {
                    return null;
                }
            });
        }

        #endregion

        #region 5. 资源回收

        /// <summary>
        /// 销毁资源，确保活动中的 CSV 文件流被正常关闭，防止文件锁定。
        /// </summary>
        public void Dispose()
        {
            EndSession();
        }

        #endregion
    }
}