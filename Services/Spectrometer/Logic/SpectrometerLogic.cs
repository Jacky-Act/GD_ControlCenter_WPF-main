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

        public static SpectralData? PerformStitching(IEnumerable<SpectralData> dataCollection, double mergeTolerance = 0.01)
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

                // 拼接段波长对齐校正（消除设备间波长微差）
                for (int i = 1; i < totalLength; i++)
                {
                    double prev = _bufferWavelengths[i - 1];
                    double curr = _bufferWavelengths[i];
                    // 相邻波长异常跳变>1nm判定为拼接缝，强制平滑
                    if (curr - prev > 1.0)
                    {
                        _bufferWavelengths[i] = prev + 0.1;
                    }
                }

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
                // 逐元素赋值，避免Array.Copy浮点尾差
                for (int i = 0; i < finalLength; i++)
                {
                    finalW[i] = _bufferWavelengths[i];
                    finalI[i] = _bufferIntensities[i];
                }

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

            int bestPeakIndex = -1;
            double minDistanceToTarget = double.MaxValue;
            double absoluteMaxIntensity = -1;
            int absoluteMaxIndex = -1;

            // 1. 遍历寻找窗口内的所有“局部极大值（山头）”
            for (int i = 1; i < data.Wavelengths.Length - 1; i++)
            {
                double currentW = data.Wavelengths[i];

                if (Math.Abs(currentW - targetWavelength) <= tolerance)
                {
                    double currentI = data.Intensities[i];
                    double prevI = data.Intensities[i - 1];
                    double nextI = data.Intensities[i + 1];

                    // 记录窗口内的绝对最大值，作为备用防退化方案
                    if (currentI > absoluteMaxIntensity)
                    {
                        absoluteMaxIntensity = currentI;
                        absoluteMaxIndex = i;
                    }

                    // 核心逻辑：判断是否为局部极大值 (山头)
                    if (currentI > prevI && currentI > nextI)
                    {
                        // 计算该山头与目标波长的物理距离
                        double distance = Math.Abs(currentW - targetWavelength);
                        
                        // 距离优先原则：选择距离目标波长最近的山头
                        if (distance < minDistanceToTarget)
                        {
                            minDistanceToTarget = distance;
                            bestPeakIndex = i;
                        }
                    }
                }

                // 性能优化：因为波长是升序的，超过窗口上限即可停止循环
                if (currentW > targetWavelength + tolerance) break;
            }

            // 2. 结果裁定
            // 优先返回距离最近的真实局部山头
            if (bestPeakIndex != -1)
            {
                return data.Wavelengths[bestPeakIndex];
            }
            // 防退化处理：如果窗口内找不到任何“山头”（例如落在一段平缓下坡上），则退化为找绝对最大值
            else if (absoluteMaxIndex != -1)
            {
                return data.Wavelengths[absoluteMaxIndex];
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

        #endregion
    }
}