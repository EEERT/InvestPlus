# InvestPlus 📈

InvestPlus 是一款可转债行情监测工具，采用**纯 C# / WinForms .NET 8** 架构，通过 [AKTools](https://github.com/akfamily/aktools)（AKShare 的 HTTP API 版本）直接获取数据，无需任何 Python 自定义后端。

---

## 功能特性

- 🔄 **可转债监测**：实时获取可转债行情、转股溢价率、强赎/回售状态等核心数据
- 🔍 **多维度筛选**：按现价、转股溢价率、剩余年限、剩余规模灵活过滤
- 🟡🔴 **高亮预警**：自动标注接近强赎（10~15天）与回售风险（>25天）的标的
- 📥 **一键导出 CSV**：将筛选结果导出为 CSV 文件
- ✅ **名称修正**：以东方财富 RPT_BOND_CB_LIST（SECURITY_SHORT_NAME）为准覆盖转债名称

---

## 数据来源

| 数据 | 来源 | 获取方式 |
|------|------|---------|
| 实时行情、转股溢价率 | 东方财富（通过 AKTools `bond_cov_comparison`）| AKTools HTTP API |
| 强赎触发天数、强赎状态 | 集思录（通过 AKTools `bond_cb_redeem_jsl`）| AKTools HTTP API |
| 债券评级、到期时间、剩余规模、正股PB | 东方财富数据中心 RPT_BOND_CB_LIST | C# 直接 HTTP 调用 |

---

## 系统要求

### AKTools（数据服务，所有平台）

| 依赖 | 版本要求 |
|------|----------|
| Python | **3.9 +** |
| aktools | 最新版 |
| akshare | 最新版（由 aktools 自动安装） |

> 推荐使用 Python 3.10 或 3.11。

### WinForms 桌面客户端（仅 Windows）

| 依赖 | 版本要求 |
|------|----------|
| .NET SDK | **8.0** |
| Windows | 10 / 11 |

---

## 快速开始

### 步骤 1：安装并启动 AKTools

AKTools 是 AKShare 的 HTTP 服务版本，提供标准化的 RESTful 数据接口。

```bash
# 安装 AKTools
pip install aktools

# 启动 AKTools（默认监听 http://127.0.0.1:8080）
python -m aktools
```

启动成功后，浏览器访问 `http://127.0.0.1:8080` 可查看 AKTools 主页。

> **提示**：每次使用前需先启动 AKTools。建议保持终端窗口不关闭。

### 步骤 2：编译并运行 WinForms 客户端

**前提条件**：已安装 [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（仅 Windows）

```powershell
# 进入 WinForms 项目目录
cd InvestPlusUI

# 还原 NuGet 包并编译
dotnet build -c Release

# 直接运行（开发模式）
dotnet run
```

或发布为独立可执行文件：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\win-x64
# 生成的 InvestPlusUI.exe 位于 publish\win-x64\ 目录
publish\win-x64\InvestPlusUI.exe
```

### 步骤 3：使用客户端

1. 确认 AKTools 已启动（见步骤 1）
2. 启动 InvestPlusUI 后，程序自动连接 AKTools 并加载数据
3. 等待数据加载完成（首次约 10~30 秒，视网络情况而定）
4. 使用顶部筛选栏过滤数据
5. 点击「📥 导出 CSV」导出当前筛选结果

> **提示**：如需更换 AKTools 地址（如非本机部署），可在顶部「AKTools 地址」输入框中修改后点击「🔄 加载 / 刷新数据」。

---

## 项目结构

```
InvestPlus/
└── InvestPlusUI/                    # C# WinForms .NET 8.0 桌面客户端
    ├── InvestPlusUI.csproj
    ├── Program.cs
    ├── MainForm.cs                  # 主窗体（行情展示、筛选、CSV 导出）
    ├── Models/
    │   └── BondInfo.cs              # 数据模型
    └── Services/
        ├── AkToolsService.cs        # AKTools HTTP API 客户端
        ├── EastmoneyService.cs      # 东方财富数据中心直接 HTTP 调用
        └── BondDataService.cs       # 多源数据合并与衍生字段计算
```

---

## 常见问题

**Q: 点击「加载 / 刷新数据」提示「无法连接到 AKTools 服务」？**
A: 请先启动 AKTools（`python -m aktools`），确认终端中出现 `Uvicorn running on http://0.0.0.0:8080` 字样后再重试。

**Q: 数据加载失败怎么办？**
A: 数据来源为东方财富、集思录等，请确保网络可正常访问。部分数据接口在非交易时段可能返回空数据，属正常现象。

**Q: 转债名称显示的是正股名称？**
A: 程序优先使用东方财富 RPT_BOND_CB_LIST 中的债券简称（SECURITY_SHORT_NAME），确保显示转债名称而非正股名称。

**Q: 如何在非 Windows 平台使用？**
A: AKTools 服务本身跨平台，可部署在 macOS/Linux。但 WinForms 客户端仅支持 Windows。如需跨平台，可基于现有 `BondDataService` 开发 Web 或 CLI 前端。

**Q: 如何升级 AKTools / AKShare？**

```bash
pip install aktools --upgrade
pip install akshare --upgrade
```
