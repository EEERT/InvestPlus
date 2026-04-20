# InvestPlus 📈

InvestPlus 是一款基于 [Streamlit](https://streamlit.io/) 的投资监测助手，目前支持**可转债**行情实时查看与筛选，后续将陆续接入中国A股、ETF基金等模块。

---

## 功能特性

- 🔄 **可转债监测**：实时获取可转债行情、转股溢价率、强赎/回售状态等核心数据
- 🔍 **多维度筛选**：按现价、转股溢价率、剩余年限、债券评级、强赎状态灵活过滤
- 🟡🔴 **高亮预警**：自动标注接近强赎（10~15天）与回售风险（>25天）的标的
- 📥 **一键导出**：将筛选结果导出为 Excel 文件
- 🔁 **自动刷新**：可选 120 秒自动刷新（需安装 `streamlit-autorefresh`）

---

## 本地运行环境要求

| 依赖 | 版本要求 |
|------|----------|
| Python | **3.9 +** |
| streamlit | ≥ 1.32.0 |
| akshare | ≥ 1.12.0 |
| pandas | ≥ 2.0.0 |
| openpyxl | ≥ 3.1.0 |
| requests | ≥ 2.31.0 |

> 推荐使用 Python 3.10 或 3.11。

---

## 本地运行步骤

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

### 4. 启动应用

```bash
streamlit run app.py
```

启动成功后，终端会输出类似：

```
You can now view your Streamlit app in your browser.
Local URL: http://localhost:8501
```

在浏览器中打开 <http://localhost:8501> 即可使用。

---

## 项目结构

```
InvestPlus/
├── app.py              # 主应用入口（Streamlit）
├── requirements.txt    # Python 依赖列表
├── data/
│   └── bond_data.py    # 可转债数据获取（基于 akshare）
└── utils/
    └── calculations.py # 数据合并与计算逻辑
```

---

## 常见问题

**Q: 数据加载失败怎么办？**  
A: 数据来源为 [AKShare](https://akshare.akfamily.xyz/)，请确保网络可正常访问该数据源。部分数据接口在非交易时段可能返回空数据，属正常现象。

**Q: 自动刷新功能无效？**  
A: 请先安装 `streamlit-autorefresh`：`pip install streamlit-autorefresh`，然后勾选侧边栏的「自动刷新」选项。