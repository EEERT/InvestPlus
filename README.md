# InvestPlus 📈

InvestPlus 是一款可转债行情监测工具，提供两种前端界面：

- **Streamlit 网页版**（Python）— 浏览器中直接使用
- **WinForms 桌面版**（C# / .NET 8.0）— Windows 原生桌面应用，连接 Python FastAPI 后端

---

## 功能特性

- 🔄 **可转债监测**：实时获取可转债行情、转股溢价率、强赎/回售状态等核心数据
- 🔍 **多维度筛选**：按现价、转股溢价率、剩余年限、债券评级、强赎状态灵活过滤
- 🟡🔴 **高亮预警**：自动标注接近强赎（10~15天）与回售风险（>25天）的标的
- 📥 **一键导出**：将筛选结果导出为 Excel（Streamlit）或 CSV（WinForms）
- 🔁 **自动刷新**：可选 120 秒自动刷新（Streamlit 版需安装 `streamlit-autorefresh`）
- 🛡️ **反爬绕过**：模拟人工浏览（持久 Session + 随机延迟 + UA 轮换）突破东方财富 push2 API 限制
- ✅ **名称修正**：以东方财富 RPT_BOND_CB_LIST（SECURITY_SHORT_NAME）为准覆盖正股名称，确保展示可转债名称

---

## 系统要求

### Python 后端（Streamlit 版 & WinForms 版共用）

| 依赖 | 版本要求 |
|------|----------|
| Python | **3.9 +** |
| streamlit | ≥ 1.32.0 |
| akshare | ≥ 1.12.0 |
| pandas | ≥ 2.0.0 |
| openpyxl | ≥ 3.1.0 |
| requests | ≥ 2.31.0 |
| fastapi | ≥ 0.110.0 |
| uvicorn | ≥ 0.29.0 |

> 推荐使用 Python 3.10 或 3.11。

### WinForms 桌面客户端（仅 Windows）

| 依赖 | 版本要求 |
|------|----------|
| .NET SDK | **8.0** |
| Windows | 10 / 11 |

---

## 方式一：Streamlit 网页版（本地运行）

### 1. 克隆仓库

```bash
git clone https://github.com/EEERT/InvestPlus.git
cd InvestPlus
```

### 2. 创建并激活虚拟环境（推荐）

```bash
# 创建虚拟环境
python -m venv .venv

# macOS / Linux
source .venv/bin/activate

# Windows (PowerShell)
.venv\Scripts\Activate.ps1
```

### 3. 安装依赖

```bash
pip install -r requirements.txt
```

如需使用**自动刷新**功能，额外安装：

```bash
pip install streamlit-autorefresh
```

### 4. 启动 Streamlit 应用

```bash
streamlit run app.py
```

启动成功后，在浏览器中打开 <http://localhost:8501> 即可使用。

---

## 方式二：WinForms 桌面版（Windows）

WinForms 客户端需要先启动 Python FastAPI 后端，再运行桌面程序。

### 步骤 1：启动 Python FastAPI 后端

```bash
# 克隆并进入项目目录（若尚未克隆）
git clone https://github.com/EEERT/InvestPlus.git
cd InvestPlus

# 创建虚拟环境并安装依赖
python -m venv .venv
.venv\Scripts\Activate.ps1      # Windows PowerShell
pip install -r requirements.txt

# 启动 FastAPI 后端（默认监听 http://localhost:8000）
python api.py
```

或使用 uvicorn：

```bash
uvicorn api:app --host 0.0.0.0 --port 8000
```

终端输出类似以下内容即表示启动成功：

```
INFO:     Uvicorn running on http://0.0.0.0:8000 (Press CTRL+C to quit)
```

验证后端可访问：浏览器打开 <http://localhost:8000/api/health>，应看到 `{"status":"ok",...}`。

### 步骤 2：编译并运行 WinForms 客户端

**前提条件**：已安装 [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（Windows）

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

### 步骤 3：使用桌面客户端

1. 确认「后端地址」输入框为 `http://localhost:8000`（默认值）
2. 点击「🔄 加载 / 刷新数据」按钮
3. 等待数据加载完成（首次约 10~30 秒，视网络情况而定）
4. 使用顶部筛选栏过滤数据
5. 点击「📥 导出 CSV」导出当前筛选结果

---

## 项目结构

```
InvestPlus/
├── app.py              # Streamlit 主应用入口
├── api.py              # FastAPI 后端（供 WinForms 客户端调用）
├── requirements.txt    # Python 依赖列表
├── data/
│   └── bond_data.py    # 可转债数据获取（含反爬与名称修正）
├── utils/
│   └── calculations.py # 数据合并与计算逻辑
└── InvestPlusUI/       # C# WinForms .NET 8.0 桌面客户端
    ├── InvestPlusUI.csproj
    ├── Program.cs
    ├── MainForm.cs
    ├── Models/
    │   └── BondInfo.cs     # 数据模型（与 API JSON 字段对应）
    └── Services/
        └── ApiService.cs   # HTTP 客户端封装
```

---

## 常见问题

**Q: WinForms 客户端提示「连接失败」？**  
A: 请先确认 Python 后端已启动（`python api.py`），并检查后端地址是否正确（默认 `http://localhost:8000`）。

**Q: 数据加载失败怎么办？**  
A: 数据来源为东方财富、新浪财经等，请确保网络可正常访问。部分数据接口在非交易时段可能返回空数据，属正常现象。

**Q: 转债名称显示的是正股名称？**  
A: 已修复。程序优先使用东方财富 RPT_BOND_CB_LIST 中的债券简称（SECURITY_SHORT_NAME），以新浪财经实时行情名称作为补充兜底。

**Q: 自动刷新功能无效（Streamlit 版）？**  
A: 请先安装 `streamlit-autorefresh`：`pip install streamlit-autorefresh`，然后勾选侧边栏的「自动刷新」选项。
