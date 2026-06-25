using GD_ControlCenter_WPF.Models.Spectrometer;

/*
 * 文件名: SpectrometerLogic.cs
 * 描述: 光谱数据算法中心。提供纯数学计算支持，包括多设备光谱拼接、硬件饱和度校验及动态寻峰算法。
 * 本类采用静态内存池设计，核心拼接算法实现了零内存分配，以规避高频采集场景下的 GC 抖动。
 * 维护指南: 
 * 1. 拼接算法采用“最大值保持”而非平均值，以防止重叠区域的尖峰强度被削弱。
 * 2. 修改 _maxBufferSize 时需确保预留足够的静态空间以容纳所有在线设备的像素总和。
 */

namespace GD_ControlCenter_WPF.Services.Spectrometer.Logic
{
    /// <summary>
    /// 光谱算法逻辑类：包含拼接、寻峰、校验等纯数学计算逻辑。
    /// </summary>
    public static class SpectrometerLogic
    {
        #region 1. 全局静态内存池

        /// <summary>
        /// 静态缓冲区初始容量。支持 4 台 4096 像素设备的全量拼接需求。
        /// </summary>
        private static int _maxBufferSize = 16384;

        /// <summary>
        /// 波长静态内存池，用于原地排序与合并。
        /// </summary>
        private static double[] _bufferWavelengths = new double[_maxBufferSize];

        /// <summary>
        /// 强度静态内存池，与波长池同步移动。
        /// </summary>
        private static double[] _bufferIntensities = new double[_maxBufferSize];

        /// <summary>
        /// 拼接算法专属并发锁，防止多台管理类同时调用导致缓冲区污染。
        /// </summary>
        private static readonly object _stitchLock = new object();

        #endregion

        #region 2. 多机光谱拼接算法 (Stitching Algorithm)

        /// <summary>
        /// 多设备光谱拼接算法。
        /// 采用“最大值保持”策略处理重叠波长点，确保窄带尖峰信号不被平均算法削弱。
        /// </summary>
        /// <param name="dataCollection">待合并的光谱数据集合（来自不同物理设备）。</param>
        /// <param name="mergeTolerance">合并容差 (单位: nm)。在此范围内的像素点被视为同一个物理坐标。</param>
        /// <returns>拼接完成后的全局光谱数据实体。</returns>
        public static SpectralData? PerformStitching(IEnumerable<SpectralData> dataCollection, double mergeTolerance = 0.05)
        {
            if (dataCollection == null) return null;

            lock (_stitchLock)
            {
                int totalLength = 0;
                int deviceCount = 0;

                // 预扫描：确定本次拼接所需的总原始像素量
                foreach (var data in dataCollection)
                {
                    if (data.Wavelengths != null) totalLength += data.Wavelengths.Length;
                    deviceCount++;
                }

                if (deviceCount < 2 || totalLength == 0) return null;

                // 若当前缓冲区不足以容纳全量数据，执行倍增扩容
                if (totalLength > _bufferWavelengths.Length)
                {
                    _maxBufferSize = totalLength * 2;
                    _bufferWavelengths = new double[_maxBufferSize];
                    _bufferIntensities = new double[_maxBufferSize];
                }

                // --- 步骤 1: 数据线性平铺 ---
                int currentOffset = 0;
                foreach (var data in dataCollection)
                {
                    if (data.Wavelengths != null && data.Intensities != null)
                    {
                        int len = Math.Min(data.Wavelengths.Length, data.Intensities.Length);
                        Array.Copy(data.Wavelengths, 0, _bufferWavelengths, currentOffset, len);
                        Array.Copy(data.Intensities, 0, _bufferIntensities, currentOffset, len);
                        currentOffset += len;
                    }
                }

                // --- 步骤 2: 物理波长排序 ---              
                Array.Sort(_bufferWavelengths, _bufferIntensities, 0, totalLength); // 基于波长池对强度池进行联动升序排列

                // --- 步骤 3: 峰值保护去重 ---
                int validCount = 0; // 慢指针：指向已处理好的有效数据末尾

                // 快指针 i 向后扫描，寻找重叠区间
                for (int i = 1; i < totalLength; i++)
                {
                    // 若当前点与上一个有效点距离小于容差，则视为物理重叠
                    if (Math.Abs(_bufferWavelengths[i] - _bufferWavelengths[validCount]) <= mergeTolerance)
                    {
                        // 执行峰值保持：重合区域仅保留能量最高的点，丢弃低强度噪声点
                        if (_bufferIntensities[i] > _bufferIntensities[validCount])
                        {
                            _bufferWavelengths[validCount] = _bufferWavelengths[i];
                            _bufferIntensities[validCount] = _bufferIntensities[i];
                        }
                    }
                    else
                    {
                        // 距离足够，判定为独立波长点，慢指针步进
                        validCount++;
                        _bufferWavelengths[validCount] = _bufferWavelengths[i];
                        _bufferIntensities[validCount] = _bufferIntensities[i];
                    }
                }

                // 最终合并后的有效点数
                int finalLength = validCount + 1;

                // --- 步骤 4: 结果输出 (实例化) ---
                double[] finalW = new double[finalLength];
                double[] finalI = new double[finalLength];
                Array.Copy(_bufferWavelengths, 0, finalW, 0, finalLength);
                Array.Copy(_bufferIntensities, 0, finalI, 0, finalLength);

                return new SpectralData(finalW, finalI, "Combined_System");
            }
        }

        #endregion

        #region 3. 单源数据分析与校验

        /// <summary>
        /// 硬件过曝校验。
        /// 基于 16-bit ADC 理论上限 65535，设定 65000 为业务安全预警线。
        /// </summary>
        /// <param name="data">待校验的光谱帧。</param>
        /// <returns>若任意像素超过 65000 Counts 则返回 True。</returns>
        public static bool CheckSaturation(SpectralData data)
        {
            if (data?.Intensities == null || data.Intensities.Length == 0) return false;
            // 使用 Any 快速查找过曝点
            return data.Intensities.Any(i => i > 65000);
        }

        /// <summary>
        /// 全局最高峰寻优。
        /// </summary>
        /// <param name="data">光谱数据帧。</param>
        /// <returns>返回包含 (波长, 最大强度) 的元组。</returns>
        public static (double wavelength, double intensity) FindPeak(SpectralData data)
        {
            if (data?.Intensities == null || data.Intensities.Length == 0) return (0, 0);

            double maxIntensity = data.Intensities.Max();
            int index = Array.IndexOf(data.Intensities, maxIntensity);
            double wavelength = data.Wavelengths[index];

            return (wavelength, maxIntensity);
        }

        /// <summary>
        /// 局部动态寻峰 (防跳峰算法)。
        /// 在目标波长窗口内通过“局部山头检测”寻找真实最高点。若存在多个局部极大值，则优先选择物理位置最接近目标值的山头。
        /// </summary>
        /// <param name="data">当前光谱帧。</param>
        /// <param name="targetWavelength">理论中心波长 (通常来自 UI 交互点击)。</param>
        /// <param name="tolerance">搜索容差带宽 (± nm)。</param>
        /// <returns>寻找到的实际物理特征峰波长。</returns>
        public static double GetActualPeakWavelength(SpectralData data, double targetWavelength, double tolerance)
        {
            if (data?.Wavelengths == null || data.Wavelengths.Length == 0) return targetWavelength;

            double maxIntensityInWindow = -1;
            int maxIndexInWindow = -1;

            // 1. 设定搜索范围的索引边界（性能优化：不需要遍历全谱）
            for (int i = 0; i < data.Wavelengths.Length; i++)
            {
                double currentW = data.Wavelengths[i];

                // 检查是否落入用户点击的波长窗口 [target - tolerance, target + tolerance]
                if (Math.Abs(currentW - targetWavelength) <= tolerance)
                {
                    double currentI = data.Intensities[i];

                    // 2. 核心逻辑：寻找窗口内的绝对最大强度
                    // 不再判断“山头”，而是直接找最高点，这样可以有效过滤掉点击点附近的微小毛刺
                    if (currentI > maxIntensityInWindow)
                    {
                        maxIntensityInWindow = currentI;
                        maxIndexInWindow = i;
                    }
                }

                // 性能优化：因为波长是升序的，超过窗口上限即可停止循环
                if (currentW > targetWavelength + tolerance) break;
            }

            // 3. 返回结果：如果找到了最高点则返回其物理波长，否则返回原始点击位置
            if (maxIndexInWindow != -1)
            {
                return data.Wavelengths[maxIndexInWindow];
            }

            return targetWavelength;
        }

        /// <summary>
        /// 定点光强映射。
        /// 寻找光谱中与指定理论波长在物理上最接近的像素点强度。
        /// </summary>
        /// <param name="data">光谱数据。</param>
        /// <param name="targetWavelength">目标波长坐标。</param>
        /// <returns>对应的光强计数 (Counts)。</returns>
        public static double GetIntensityAtWavelength(SpectralData data, double targetWavelength)
        {
            if (data?.Wavelengths == null || data.Wavelengths.Length == 0) return 0;

            int bestIndex = 0;
            double minDiff = double.MaxValue;

            // 简单的最接近搜索
            for (int i = 0; i < data.Wavelengths.Length; i++)
            {
                double diff = Math.Abs(data.Wavelengths[i] - targetWavelength);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    bestIndex = i;
                }
            }
            return data.Intensities[bestIndex];
        }

        /// <summary>
        /// 基于像素邻域窗口的峰高度寻峰算法（对应原系统的 peak_height_8nbnalgorithm 逻辑）
        /// </summary>
        /// <param name="targetWavelength">特征波长中心点 (nm)</param>
        /// <param name="data">光谱数据模型</param>
        /// <param name="neighborCount">单侧邻域像素数，默认值为 5（即滑动窗口总宽度为 2 * neighborCount + 1）</param>
        /// <returns>寻峰最大值在光谱数据 Intensity 数组中的绝对索引</returns>
        public static int GetPeakIndexByPixelWindow(double targetWavelength, SpectralData data, int neighborCount = 5)
        {
            if (data?.Wavelengths == null || data.Intensities == null || data.Wavelengths.Length == 0)
            {
                return 0;
            }

            double[] wavelengths = data.Wavelengths;
            double[] intensities = data.Intensities;

            // 1. 寻找在光谱数据中最接近目标波长的像素索引
            int closedWavelengthIndex = 0;
            double minDiff = double.MaxValue;
            for (int i = 0; i < wavelengths.Length; i++)
            {
                double diff = Math.Abs(wavelengths[i] - targetWavelength);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    closedWavelengthIndex = i;
                }
            }

            // 2. 安全边界控制：防止局部窗口超出原始光谱数组边界
            int startIndex = Math.Max(0, closedWavelengthIndex - neighborCount);
            int endIndex = Math.Min(intensities.Length - 1, closedWavelengthIndex + neighborCount);

            // 3. 在安全窗口内检索最大强度点及其索引
            double maxIntensity = double.MinValue;
            int selectedIntensityIndex = closedWavelengthIndex;

            for (int i = startIndex; i <= endIndex; i++)
            {
                if (intensities[i] > maxIntensity)
                {
                    maxIntensity = intensities[i];
                    selectedIntensityIndex = i;
                }
            }

            return selectedIntensityIndex;
        }

        /// <summary>
        /// 配合上述寻峰算法，直接返回寻峰后的实际物理波长
        /// </summary>
        public static double GetPeakWavelengthByPixelWindow(double targetWavelength, SpectralData data, int neighborCount = 5)
        {
            if (data?.Wavelengths == null || data.Wavelengths.Length == 0) return targetWavelength;
            int index = GetPeakIndexByPixelWindow(targetWavelength, data, neighborCount);
            return data.Wavelengths[index];
        }

        #endregion
    }
}