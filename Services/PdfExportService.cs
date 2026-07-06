using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GD_ControlCenter_WPF.ViewModels;

namespace GD_ControlCenter_WPF.Services
{
    public class PdfExportService
    {
        public PdfExportService()
        {
            QuestPDF.Settings.License = LicenseType.Community;
            // QuestPDF 默认使用系统字体，在 Windows 上可以直接使用 "Microsoft YaHei"
        }

        public void ExportMultiElementReport(string targetPath, List<ReportDataModel> dataList, List<string> imagePaths)
        {
            Document.Create(container =>
            {
                for (int i = 0; i < dataList.Count; i++)
                {
                    var data = dataList[i];
                    var imgPath = i < imagePaths.Count ? imagePaths[i] : null;

                    container.Page(page =>
                    {
                        page.Size(PageSizes.A4);
                        page.Margin(1.5f, Unit.Centimetre);
                        page.PageColor(Colors.White);
                        page.DefaultTextStyle(x => x.FontSize(10).FontFamily("Microsoft YaHei"));

                        // Header
                        page.Header().Element(c => ComposeHeader(c, data));

                        // Content
                        page.Content().Element(c => ComposeContent(c, data, imgPath));

                        // Footer
                        page.Footer().AlignCenter().Text(x =>
                        {
                            x.CurrentPageNumber();
                            x.Span(" / ");
                            x.TotalPages();
                        });
                    });
                }
            })
            .GeneratePdf(targetPath);
        }

        private void ComposeHeader(IContainer container, ReportDataModel data)
        {
            container.Column(column =>
            {
                column.Item().AlignCenter().Text($"{data.ElementName} 测量分析报告").SemiBold().FontSize(16).FontColor(Colors.Blue.Darken2);
                column.Item().PaddingTop(4).Row(row =>
                {
                    row.RelativeItem().Text($"导出时间: {data.MeasurementDate}");
                });
                column.Item().PaddingTop(2).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
            });
        }

        private void ComposeContent(IContainer container, ReportDataModel data, string imgPath)
        {
            container.PaddingVertical(5).Column(column =>
            {
                // Sequence Info
                column.Item().PaddingBottom(5).Row(row =>
                {
                    row.RelativeItem().Text($"采样间隔 (s): {data.Interval}");
                    row.RelativeItem().Text($"重复测量次数: {data.Repeats}");
                    row.RelativeItem().Text($"");
                });

                // Fitting Info
                column.Item().PaddingBottom(2).Text("【拟合曲线结果】").SemiBold().FontSize(11);
                column.Item().Column(fitCol =>
                {
                    fitCol.Item().Text($"拟合曲线时间: {data.CurveTime}");
                    fitCol.Item().Text($"拟合方程: {data.Equation}");
                    fitCol.Item().Row(row =>
                    {
                        row.RelativeItem().Text($"相关系数 (R²): {data.RSquared}");
                        row.RelativeItem().Text($"检出限 (LOD): {data.Lod:F3} {data.ConcentrationUnit}");
                        row.RelativeItem().Text($""); // 占位保持对齐
                    });
                });

                // Image
                if (!string.IsNullOrEmpty(imgPath) && File.Exists(imgPath))
                {
                    column.Item().PaddingVertical(5).AlignCenter().Height(200).Image(imgPath);
                }

                // Standard Points Table
                column.Item().PaddingTop(5).PaddingBottom(2).Text("【校准点数据明细】").SemiBold().FontSize(11);
                column.Item().PaddingHorizontal(1, Unit.Centimetre).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                    });

                    table.Header(header =>
                    {
                        header.Cell().Element(CellStyle).AlignCenter().Text("标准品名称");
                        header.Cell().Element(CellStyle).AlignCenter().Text("已知浓度");
                        header.Cell().Element(CellStyle).AlignCenter().Text("响应强度");
                        header.Cell().Element(CellStyle).AlignCenter().Text("RSD(%)");

                        static IContainer CellStyle(IContainer container) => container.DefaultTextStyle(x => x.SemiBold()).PaddingVertical(3).BorderBottom(1).BorderColor(Colors.Black);
                    });

                    foreach (var p in data.StandardPoints)
                    {
                        table.Cell().Element(CellContent).AlignCenter().Text(p.Name);
                        table.Cell().Element(CellContent).AlignCenter().Text($"{p.Concentration} {data.ConcentrationUnit}");
                        table.Cell().Element(CellContent).AlignCenter().Text($"{p.Intensity:F0}");
                        table.Cell().Element(CellContent).AlignCenter().Text($"{p.RSD:F2}");

                        static IContainer CellContent(IContainer container) => container.BorderBottom(1).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3);
                    }
                });

                // Sample Results Table (仅在有待测样时渲染)
                if (data.SampleResults != null && data.SampleResults.Any())
                {
                    column.Item().PaddingTop(10).PaddingBottom(2).Text("【待测样品分析结果】").SemiBold().FontSize(11);
                    column.Item().PaddingHorizontal(1, Unit.Centimetre).Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn();
                            columns.RelativeColumn();
                            columns.RelativeColumn();
                            columns.RelativeColumn();
                        });

                        table.Header(header =>
                        {
                            header.Cell().Element(CellStyle).AlignCenter().Text("样品名称");
                            header.Cell().Element(CellStyle).AlignCenter().Text("测量强度");
                            header.Cell().Element(CellStyle).AlignCenter().Text("RSD(%)");
                            header.Cell().Element(CellStyle).AlignCenter().Text("算得浓度");

                            static IContainer CellStyle(IContainer container) => container.DefaultTextStyle(x => x.SemiBold()).PaddingVertical(3).BorderBottom(1).BorderColor(Colors.Black);
                        });

                        foreach (var r in data.SampleResults)
                        {
                            table.Cell().Element(CellContent).AlignCenter().Text(r.SampleName);
                            table.Cell().Element(CellContent).AlignCenter().Text($"{r.Intensity:F0}");
                            table.Cell().Element(CellContent).AlignCenter().Text($"{r.RSD:F2}");
                            
                            string concText = r.CalculatedConc;
                            if (!string.IsNullOrEmpty(concText) && concText != "-")
                            {
                                concText = $"{concText} {data.ConcentrationUnit}";
                            }
                            table.Cell().Element(CellContent).AlignCenter().Text(concText).FontColor(Colors.Blue.Darken2).SemiBold();

                            static IContainer CellContent(IContainer container) => container.BorderBottom(1).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3);
                        }
                    });
                }
            });
        }
    }
}
