using GD_ControlCenter_WPF.Services;
using GD_ControlCenter_WPF.Services.Platform3D;
using GD_ControlCenter_WPF.Services.Spectrometer;
using GD_ControlCenter_WPF.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;

/*
 * 文件名: App.xaml.cs
 * 描述: 应用程序的入口逻辑类。负责构建全局依赖注入容器、管理系统生命周期、
 * 以及在程序异常或正常退出时执行硬件的安全熔断与数据保存。
 * 维护指南: 
 * 1. 所有的 Service 与 ViewModel 均在此处的 OnStartup 方法中注册。
 * 2. OnExit 包含“自杀式”清理逻辑，通过 Process.Kill() 确保在非托管驱动卡死时强制回收内存。
 * 3. 硬件关闭顺序：先停蠕动泵，后关高压电源，最后释放串口与 USB 句柄。
 */

namespace GD_ControlCenter_WPF
{
    /// <summary>
    /// 应用程序启动与退出逻辑包装类。
    /// </summary>
    public partial class App : Application
    {
        #region 1. 全局依赖注入容器 (DI Container)

        /// <summary>
        /// 全局服务提供者。
        /// 允许通过 App.Services.GetRequiredService 获取单例服务实例。
        /// </summary>
        public static IServiceProvider Services { get; private set; } = null!;

        #endregion

        #region 2. 启动与初始化流程

        /// <summary>
        /// 程序启动钩子。
        /// 职责：配置 DI 容器、注册服务依赖、执行硬件前置初始化。
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var services = new ServiceCollection();

            // 1. 注册核心基础服务 (单例)
            services.AddSingleton<JsonConfigService>();
            services.AddSingleton<ElementDatabaseService>();
            services.AddSingleton<ISerialPortService, SerialPortService>();
            services.AddSingleton<ProtocolService>();

            // 2. 注册硬件业务逻辑层 (单例)
            services.AddSingleton<GeneralDeviceService>();
            services.AddSingleton<BatteryService>();
            services.AddSingleton<HighVoltageService>();
            services.AddSingleton<PeakTrackingService>();
            services.AddSingleton<IPlatform3DService, Platform3DService>();
            services.AddSingleton<PlatformCalibrationService>();
            services.AddSingleton<SequenceStorageService>();
            services.AddSingleton<FlowInjectionLogic>();

            // 3. 注册视图模型层 (单例)
            services.AddSingleton<BatteryViewModel>();
            services.AddSingleton<HighVoltageViewModel>();
            services.AddSingleton<ControlPanelViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<PeristalticPumpViewModel>();
            services.AddSingleton<SyringePumpViewModel>();
            services.AddSingleton<TimeSeriesViewModel>();
            services.AddSingleton<ElementConfigViewModel>();
            services.AddSingleton<SampleSequenceViewModel>();
            services.AddSingleton<SampleMeasurementViewModel>();
            services.AddSingleton<FlowInjectionViewModel>();
            services.AddSingleton<AnalysisWorkstationViewModel>();
            services.AddSingleton<DataProcessingViewModel>();

            // 构建服务提供者容器
            Services = services.BuildServiceProvider();

            // --- 4. 自动恢复硬件状态 ---
            // 从物理磁盘加载上次保存的高压设定值并同步至硬件服务
            var config = Services.GetRequiredService<JsonConfigService>().Load();
            var hvService = Services.GetRequiredService<HighVoltageService>();
            hvService.SetHighVoltage(config.LastHvVoltage, config.LastHvCurrent);

            // --- 5. 清理过期废弃的孤儿 CSV 文件 ---
            Task.Run(() => CleanupOrphanCsvFiles());
        }

        private void CleanupOrphanCsvFiles()
        {
            try
            {
                string recordsDir = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Records");
                if (!System.IO.Directory.Exists(recordsDir)) return;

                var allCsvs = System.IO.Directory.GetFiles(recordsDir, "*.csv");
                if (allCsvs.Length == 0) return;

                var inUseFiles = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1. 提取所有历史曲线依赖的文件
                var dbService = Services.GetRequiredService<ElementDatabaseService>();
                var db = dbService.Load();
                foreach (var elem in db.Elements.Values)
                {
                    foreach (var wConfig in elem.Wavelengths)
                    {
                        if (wConfig.SavedCurves == null) continue;
                        foreach (var curve in wConfig.SavedCurves)
                        {
                            if (curve.Points == null) continue;
                            foreach (var pt in curve.Points)
                            {
                                if (!string.IsNullOrEmpty(pt.CsvFilePath))
                                    inUseFiles.Add(System.IO.Path.GetFullPath(pt.CsvFilePath));
                            }
                        }
                    }
                }

                // 2. 提取所有本地用户自行保存的序列模板文件依赖的 CSV
                var seqStorage = Services.GetRequiredService<SequenceStorageService>();
                string seqDir = seqStorage.GetFolderPath();
                if (System.IO.Directory.Exists(seqDir))
                {
                    foreach (var seqFile in System.IO.Directory.GetFiles(seqDir, "*.seq"))
                    {
                        try
                        {
                            string json = System.IO.File.ReadAllText(seqFile);
                            var seqList = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<GD_ControlCenter_WPF.Models.Messages.SampleItemModel>>(json);
                            if (seqList != null)
                            {
                                foreach (var sample in seqList)
                                {
                                    if (!string.IsNullOrEmpty(sample.CsvFilePath))
                                        inUseFiles.Add(System.IO.Path.GetFullPath(sample.CsvFilePath));
                                }
                            }
                        }
                        catch { }
                    }
                }

                // 3. 开始执行静默清理（保留近 7 天的数据，或者被依赖的数据）
                DateTime threshold = DateTime.Now.AddDays(-7);
                foreach (var file in allCsvs)
                {
                    string fullPath = System.IO.Path.GetFullPath(file);
                    if (inUseFiles.Contains(fullPath)) continue; // 此文件仍在某处被依赖，不杀

                    var fileInfo = new System.IO.FileInfo(file);
                    if (fileInfo.LastWriteTime < threshold) // 如果生成时间距今已超过7天，杀
                    {
                        try { fileInfo.Delete(); } catch { }
                    }
                }
            }
            catch { }
        }

        #endregion

        #region 3. 安全退出与紧急清理流程

        /// <summary>
        /// 应用程序退出时的生命周期钩子。
        /// 职责：执行数据紧急保存、下发硬件关闭指令、强制终止后台顽固进程。
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                // 触发所有记录模块的紧急静默保存，防止内存数据丢失
                var timeSeriesVM = Services.GetRequiredService<TimeSeriesViewModel>();
                timeSeriesVM.EmergencySave();

                var controlPanelVM = Services.GetRequiredService<ControlPanelViewModel>();
                controlPanelVM.EmergencySave();

                // 按照安全顺序下发关闭指令
                var hvService = Services.GetRequiredService<HighVoltageService>();
                var generalService = Services.GetRequiredService<GeneralDeviceService>();
                var serialService = Services.GetRequiredService<ISerialPortService>();

                // 停止蠕动泵供液
                generalService.ControlPeristalticPump(0, true, false);
                Thread.Sleep(300); // 预留总线通讯时间

                // 关闭高压电源输出
                hvService.SetHighVoltage(0, 0);
                Thread.Sleep(300);

                // 将易引发死锁的断开操作（USB 句柄释放）丢入独立任务
                var cleanupTask = Task.Run(() =>
                {
                    try { SpectrometerManager.Instance.StopAll(); } catch { }
                    try { serialService.Close(); } catch { }
                });

                // 设置 1.5 秒硬超时。若底层驱动在此时间内未能响应，将不再等待直接进入强杀流程。
                cleanupTask.Wait(1500);
            }
            catch (Exception)
            {
                // 捕获所有退出异常，确保不弹出崩溃对话框
            }
            finally
            {
                base.OnExit(e);

                // 调用系统底层 Kill()，绕过所有托管与非托管的常规释放逻辑，直接从任务管理器级别销毁进程，防止“僵尸进程”残留。
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
        }

        #endregion
    }
}