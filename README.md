# InvestPlus 📈

InvestPlus 是一款**可转债行情监测工具**，采用纯 **C# / WinForms .NET 8** 架构。

默认优先通过 **AKTools API** 获取可转债与正股数据，并在异常时自动回退到东方财富/集思录直连数据源。

---

## 功能特性

- 🔄 **实时可转债监测**：直接调用东方财富 push2 接口，获取转债现价、涨跌幅、溢价率等实时行情
- ⏰ **自动定时刷新**：支持 30 秒 / 1 分钟 / 2 分钟 / 5 分钟自动刷新，附带倒计时显示
- 🔍 **多维度筛选 + 实时搜索**：按价格、溢价率、剩余年限、剩余规模筛选；代码/名称实时搜索
- 🟡🔴 **高亮预警**：自动标注接近强赎（10~15天）与回售风险（>25天）的标的
- 📥 **一键导出 CSV**：将筛选结果导出为 UTF-8 BOM 编码的 CSV 文件（Excel 可直接打开）
- 💾 **详情数据缓存**：债券评级、到期时间等静态数据本地缓存 10 分钟，提高刷新速度

---

## 数据来源

| 数据 | 来源 | 获取方式 |
|------|------|---------|
| 可转债基础数据 | AKTools `bond_cov_comparison` | HTTP 调用 AKTools API |
| 正股实时数据 | AKTools `stock_zh_a_spot_em` / `stock_zh_a_spot` | HTTP 调用 AKTools API |
| 强赎/回售天计数 | AKTools `bond_cb_redeem_jsl` | HTTP 调用 AKTools API |
| 转债名称（修正）、债券评级、到期时间、剩余规模、正股PB | 东方财富数据中心 RPT_BOND_CB_LIST | 直接 HTTP 调用 |
| 兜底行情与强赎数据 | 东方财富 push2 / 集思录 | AKTools 异常时自动降级 |

> **注意**：AKTools API 默认地址为 `http://127.0.0.1:8080/api/public`，可通过环境变量 `AKTOOLS_BASE_URL` 覆盖。

---

## 系统要求

| 依赖 | 版本要求 |
|------|----------|
| .NET SDK | **8.0** |
| Windows | 10 / 11 |

> ✅ 若启用 AKTools 主数据源，请确保本机 AKTools API 服务可访问。

---

## 快速开始

**前提条件**：已安装 [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```powershell
# 进入 WinForms 项目目录
cd InvestPlusUI

# 编译并运行
dotnet run

# 或发布为独立可执行文件
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\win-x64
publish\win-x64\InvestPlusUI.exe
```

启动后程序自动获取数据，无需任何额外配置。

---

## 使用说明

1. **启动程序**：程序启动后自动加载数据（首次约 10~30 秒）
2. **手动刷新**：点击「🔄 刷新数据」按钮立即重新获取数据
3. **自动刷新**：勾选「自动刷新」，在下拉框中选择刷新间隔（30秒 / 1分钟 / 2分钟 / 5分钟）
4. **筛选数据**：在筛选区修改价格/溢价率/年限/规模范围，表格实时更新
5. **搜索**：在搜索框输入转债代码或名称关键词实时过滤
6. **预警颜色**：
   - 🟡 浅黄色：强赎天数在 10~15 天之间（即将满足强赎触发条件）
   - 🔴 浅红色：回售触发天数 > 25 天
7. **导出**：点击「📥 导出 CSV」将当前筛选结果保存为 CSV 文件

---

## 项目结构

```
InvestPlus/
└── InvestPlusUI/                         # C# WinForms .NET 8.0 桌面客户端
    ├── InvestPlusUI.csproj
    ├── Program.cs                        # 应用程序入口
    ├── MainForm.cs                       # 主窗体（行情展示、筛选、自动刷新、导出）
    ├── Models/
    │   └── BondInfo.cs                   # 可转债数据模型
    └── Services/
        ├── EastmoneyPush2Service.cs      # 东方财富 push2 实时行情
        ├── EastmoneyService.cs           # 东方财富 RPT_BOND_CB_LIST 债券详情
        ├── JisiluService.cs              # 集思录强赎倒计时数据
        └── BondDataService.cs            # 多源数据合并与缓存协调
```

---

## 常见问题

**Q: 数据加载失败怎么办？**
A: 请确保网络可正常访问 `push2.eastmoney.com` 和 `datacenter-web.eastmoney.com`。非交易时段部分接口可能返回空数据，属正常现象。

**Q: 强赎天数显示为空？**
A: 集思录 API 有时需要登录鉴权，若鉴权失败会自动跳过集思录数据，其余所有字段不受影响。可等待下次刷新重试。

**Q: 转债名称显示的是正股名称？**
A: 程序优先使用东方财富 RPT_BOND_CB_LIST 中的债券简称（SECURITY_SHORT_NAME）进行修正。若该字段数据也异常，请手动刷新。

**Q: 如何设置合理的刷新频率？**
A: 交易时段建议 30~60 秒；非交易时段数据不变化，建议关闭自动刷新或设置 5 分钟间隔。
