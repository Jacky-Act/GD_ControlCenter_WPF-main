# GD_ControlCenter_WPF

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
[![Framework](https://img.shields.io/badge/.NET-6.0_WPF-purple.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-x86-lightgrey.svg)]()

GD_ControlCenter_WPF 是用于实验室 SCGD（Solution Cathode Glow Discharge，溶液阴极辉光放电）机器的上位机控制软件。本项目基于 C# WPF 开发，实现了对底层硬件设备（光谱仪、三维平台、高压电源及泵体）的实时控制与数据采集。

## ✨ 主要功能 (Key Features)

* **硬件设备综合控制**：支持对光谱仪、三维平台、高压电源、蠕动泵及注射泵等底层实验设备进行统一指令下发与实时状态监控。
* **高频数据稳定解析**：优化了底层硬件高频通讯数据的解析流程，分离了后台数据处理与前端界面刷新，确保系统在持续高负载数据采集下依然保持流畅稳定。
* **多机型灵活适配**：支持系统运行参数与硬件极限值的动态加载，能够无缝适配不同型号机器的硬件差异，实现免编译快速部署。
* **实时数据可视化**：提供实验数据的实时图表绘制与追踪功能，帮助操作人员直观地监控实验过程与各项关键指标变化。
* **实验数据与报告导出**：支持将采集到的实验数据一键导出为数据表格，并自动生成标准化的实验报告，满足科研数据的存档与审查需求。

## 🏗️ 架构与技术栈

本项目采用标准的 **MVVM (Model-View-ViewModel)** 架构。

* **UI 框架**: WPF (Windows Presentation Foundation)
* **目标框架**: .NET 6.0 (x86)
* **核心依赖库**:
  * `CommunityToolkit.Mvvm`: MVVM 架构基础支撑。
  * `Microsoft.Extensions.DependencyInjection`: 实现底层服务的依赖注入。
  * `System.IO.Ports`: 串口通信接口。
  * `MaterialDesignThemes`: 提供现代化风格的 UI 控件。
  * `Ookii.Dialogs.Wpf`: 增强的文件与目录选择对话框。

## ⚙️ 环境依赖与构建

### 编译先决条件

1. 安装 [Visual Studio 2022](https://visualstudio.microsoft.com/) 或更高版本。
2. 安装 **.NET 6.0 SDK** 并勾选 **.NET 桌面开发** (WPF) 工作负载。

### 编译与运行

1. 克隆本仓库到本地：
   ```bash
   git clone https://github.com/Jacky-Act/GD_ControlCenter_WPF.git
   ```
2. 使用 Visual Studio 打开 `GD_ControlCenter_WPF.sln` 解决方案文件。
3. **重要**：因项目集成了光谱仪的 32 位驱动库，请务必将编译平台 (Platform Target) 切换为 **x86**。
4. 恢复 NuGet 依赖包，然后编译并启动项目。

## 📁 目录结构说明

```text
├── Assets/         # 静态资源文件（如图标、图片）
├── Helpers/        # 辅助工具类（数据转换、扩展方法等）
├── Libs/           # 外部依赖的非托管库
├── Models/         # 数据模型与实体类
├── Services/       # 业务逻辑服务（包含串口协议解析、设备控制服务等）
├── ViewModels/     # 视图模型，负责 UI 交互逻辑与数据绑定
├── Views/          # 视图文件（XAML 界面定义）
└── AppConfig.json  # 运行时的动态配置文件
```

## 📜 许可证 (License)

本项目基于 [MIT License](LICENSE) 开源。
