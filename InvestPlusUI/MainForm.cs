using System.ComponentModel;
using System.Globalization;
using InvestPlusUI.Models;
using InvestPlusUI.Services;

namespace InvestPlusUI;

/// <summary>
/// 主窗体：可转债行情监测界面。
///
/// 布局（从上至下）：
///   工具栏   ─ 服务器地址输入框、「加载/刷新数据」按钮、「导出 CSV」按钮
///   筛选区   ─ 价格区间、转股溢价率区间、剩余年限区间、剩余规模上限
///   数据表格 ─ DataGridView，支持列排序、行色彩预警
///   状态栏   ─ 符合条件数量、最后更新时间
/// </summary>
public partial class MainForm : Form
{
    // ── 控件 ─────────────────────────────────────────────────────────────────
    private readonly TextBox _txtServer;
    private readonly Button _btnLoad;
    private readonly Button _btnExport;

    private readonly NumericUpDown _numPriceMin;
    private readonly NumericUpDown _numPriceMax;
    private readonly NumericUpDown _numPremiumMin;
    private readonly NumericUpDown _numPremiumMax;
    private readonly NumericUpDown _numYearsMin;
    private readonly NumericUpDown _numYearsMax;
    private readonly NumericUpDown _numScaleMax;
    private readonly Button _btnReset;

    private readonly DataGridView _grid;
    private readonly BindingList<BondInfo> _bindingList = new();
    private readonly StatusStrip _statusStrip;
    private readonly ToolStripStatusLabel _lblStatus;
    private readonly ToolStripStatusLabel _lblUpdateTime;

    // ── 数据 ─────────────────────────────────────────────────────────────────
    private List<BondInfo> _allBonds = new();
    private ApiService? _api;

    // ── 颜色预警阈值 ─────────────────────────────────────────────────────────
    private static readonly Color ColYellow = Color.FromArgb(255, 243, 205);   // 接近强赎
    private static readonly Color ColRed    = Color.FromArgb(248, 215, 218);   // 回售风险
    private const int RedeemWarningMin = 10;   // 强赎预警下限（天）
    private const int RedeemWarningMax = 15;   // 强赎预警上限（天）
    private const int PutWarningThreshold = 25; // 回售风险阈值（天）

    public MainForm()
    {
        Text = "InvestPlus 可转债监测助手";
        Size = new Size(1600, 900);
        MinimumSize = new Size(1100, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9f);

        // ── 工具栏 ─────────────────────────────────────────────────────────
        var toolbar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(6, 6, 6, 0),
            BackColor = Color.FromArgb(240, 240, 240),
        };

        var lblServer = new Label
        {
            Text = "后端地址：",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 4, 4, 0),
        };
        _txtServer = new TextBox
        {
            Text = "http://localhost:8000",
            Width = 220,
            Margin = new Padding(0, 4, 8, 0),
        };
        _btnLoad = new Button
        {
            Text = "🔄 加载 / 刷新数据",
            Width = 140,
            Height = 28,
            Margin = new Padding(0, 2, 8, 0),
            UseVisualStyleBackColor = true,
        };
        _btnExport = new Button
        {
            Text = "📥 导出 CSV",
            Width = 110,
            Height = 28,
            Margin = new Padding(0, 2, 0, 0),
            UseVisualStyleBackColor = true,
            Enabled = false,
        };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
        };
        flow.Controls.AddRange(new Control[] { lblServer, _txtServer, _btnLoad, _btnExport });
        toolbar.Controls.Add(flow);

        // ── 筛选区 ─────────────────────────────────────────────────────────
        var filterPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(6, 4, 6, 2),
            BackColor = Color.FromArgb(250, 250, 250),
        };

        (_numPriceMin, _numPriceMax)     = MakeRangePair(0m, 10000m, 80m, 200m, "元");
        (_numPremiumMin, _numPremiumMax) = MakeRangePair(-100m, 2000m, -50m, 100m, "%");
        (_numYearsMin, _numYearsMax)     = MakeRangePair(0m, 30m, 0m, 8m, "年", dec: 2);
        _numScaleMax = new NumericUpDown
        {
            Minimum = 0, Maximum = 5000, Value = 500, DecimalPlaces = 1, Increment = 0.5m,
            Width = 75,
        };
        _btnReset = new Button
        {
            Text = "🔃 重置",
            Width = 70,
            Height = 28,
            UseVisualStyleBackColor = true,
        };

        var filterFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        filterFlow.Controls.AddRange(new Control[]
        {
            MakeLabel("价格（元）"),     _numPriceMin,   MakeLabel("~"), _numPriceMax,
            MakeLabel("  溢价率（%）"), _numPremiumMin, MakeLabel("~"), _numPremiumMax,
            MakeLabel("  剩余年限（年）"), _numYearsMin, MakeLabel("~"), _numYearsMax,
            MakeLabel("  剩余规模 ≤"),  _numScaleMax,   MakeLabel("亿"),
            _btnReset,
        });
        filterPanel.Controls.Add(filterFlow);

        // ── DataGridView ───────────────────────────────────────────────────
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(248, 248, 255),
            },
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(52, 73, 94),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleCenter,
            },
            ColumnHeadersHeight = 32,
            RowTemplate = { Height = 24 },
            EnableHeadersVisualStyles = false,
            AutoGenerateColumns = false,
        };
        BuildGridColumns();

        // ── 状态栏 ─────────────────────────────────────────────────────────
        _statusStrip = new StatusStrip();
        _lblStatus = new ToolStripStatusLabel("请点击「加载 / 刷新数据」") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _lblUpdateTime = new ToolStripStatusLabel("") { Alignment = ToolStripItemAlignment.Right };
        _statusStrip.Items.AddRange(new ToolStripItem[] { _lblStatus, _lblUpdateTime });

        // ── 布局 ───────────────────────────────────────────────────────────
        Controls.Add(_grid);
        Controls.Add(filterPanel);
        Controls.Add(toolbar);
        Controls.Add(_statusStrip);

        // ── 事件 ───────────────────────────────────────────────────────────
        _btnLoad.Click   += async (_, _) => await LoadDataAsync();
        _btnExport.Click += (_, _) => ExportCsv();
        _btnReset.Click  += (_, _) => ResetFilters();
        _numPriceMin.ValueChanged   += (_, _) => ApplyFilters();
        _numPriceMax.ValueChanged   += (_, _) => ApplyFilters();
        _numPremiumMin.ValueChanged += (_, _) => ApplyFilters();
        _numPremiumMax.ValueChanged += (_, _) => ApplyFilters();
        _numYearsMin.ValueChanged   += (_, _) => ApplyFilters();
        _numYearsMax.ValueChanged   += (_, _) => ApplyFilters();
        _numScaleMax.ValueChanged   += (_, _) => ApplyFilters();
        _grid.CellFormatting += Grid_CellFormatting;
        _grid.RowPrePaint    += Grid_RowPrePaint;
    }

    // ── 列定义 ────────────────────────────────────────────────────────────────
    private void BuildGridColumns()
    {
        var cols = new (string header, string prop, int w, string? fmt, bool center)[]
        {
            ("序号",       nameof(BondInfo.Index),             45, null,    true),
            ("转债代码",   nameof(BondInfo.BondCode),          70, null,    true),
            ("转债名称",   nameof(BondInfo.BondName),          90, null,    false),
            ("现价(元)",   nameof(BondInfo.Price),             72, "N2",    true),
            ("涨跌幅(%)",  nameof(BondInfo.ChangePercent),     72, "N2",    true),
            ("正股名称",   nameof(BondInfo.StockName),         90, null,    false),
            ("正股价",     nameof(BondInfo.StockPrice),        70, "N2",    true),
            ("正股涨跌(%)",nameof(BondInfo.StockChange),       80, "N2",    true),
            ("正股PB",     nameof(BondInfo.StockPB),           60, "N2",    true),
            ("转股价",     nameof(BondInfo.ConversionPrice),   70, "N2",    true),
            ("转股价值",   nameof(BondInfo.ConversionValue),   75, "N2",    true),
            ("溢价率(%)",  nameof(BondInfo.PremiumRate),       75, "N2",    true),
            ("债券评级",   nameof(BondInfo.CreditRating),      68, null,    true),
            ("回售触发价", nameof(BondInfo.PutTriggerPrice),   80, "N2",    true),
            ("回售天数",   nameof(BondInfo.PutTriggerDays),    68, null,    true),
            ("强赎触发价", nameof(BondInfo.RedeemTriggerPrice),80, "N2",    true),
            ("强赎天数",   nameof(BondInfo.RedeemTriggerDays), 68, null,    true),
            ("强赎状态",   nameof(BondInfo.RedeemStatus),      90, null,    true),
            ("转债占比(%)",nameof(BondInfo.BondRatio),         78, "N2",    true),
            ("到期时间",   nameof(BondInfo.MaturityDate),      86, null,    true),
            ("剩余年限",   nameof(BondInfo.RemainingYears),    70, "N2",    true),
            ("剩余规模(亿)",nameof(BondInfo.RemainingScale),   82, "N2",    true),
        };

        foreach (var (header, prop, w, fmt, center) in cols)
        {
            var col = new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                DataPropertyName = prop,
                Width = w,
                SortMode = DataGridViewColumnSortMode.Automatic,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Format = fmt ?? string.Empty,
                    Alignment = center
                        ? DataGridViewContentAlignment.MiddleCenter
                        : DataGridViewContentAlignment.MiddleLeft,
                },
            };
            _grid.Columns.Add(col);
        }
    }

    // ── 数据加载 ──────────────────────────────────────────────────────────────
    private async Task LoadDataAsync()
    {
        _btnLoad.Enabled = false;
        _btnLoad.Text = "⏳ 加载中…";
        SetStatus("正在连接后端并获取数据，请稍候…");

        try
        {
            _api?.Dispose();
            _api = new ApiService(_txtServer.Text.Trim());

            var result = await _api.GetBondsAsync();
            if (result?.Bonds == null || result.Bonds.Count == 0)
            {
                SetStatus("⚠ 后端返回了空数据，请检查网络或数据源。");
                return;
            }

            _allBonds = result.Bonds;
            ApplyFilters();
            _btnExport.Enabled = true;
            _lblUpdateTime.Text = $"最后更新：{result.LastUpdate}";
        }
        catch (Exception ex)
        {
            SetStatus($"❌ 加载失败：{ex.Message}");
            MessageBox.Show(
                $"无法从后端获取数据：\n{ex.Message}\n\n"
                + "请确认：\n"
                + "1. Python 后端已启动（python api.py 或 uvicorn api:app --port 8000）\n"
                + "2. 后端地址输入正确",
                "连接失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _btnLoad.Enabled = true;
            _btnLoad.Text = "🔄 加载 / 刷新数据";
        }
    }

    // ── 筛选 ──────────────────────────────────────────────────────────────────
    private void ApplyFilters()
    {
        if (_allBonds.Count == 0) return;

        double priceMin  = (double)_numPriceMin.Value;
        double priceMax  = (double)_numPriceMax.Value;
        double premMin   = (double)_numPremiumMin.Value;
        double premMax   = (double)_numPremiumMax.Value;
        double yearsMin  = (double)_numYearsMin.Value;
        double yearsMax  = (double)_numYearsMax.Value;
        double scaleMax  = (double)_numScaleMax.Value;

        var filtered = _allBonds.Where(b =>
        {
            if (b.Price.HasValue && (b.Price < priceMin || b.Price > priceMax)) return false;
            if (b.PremiumRate.HasValue && (b.PremiumRate < premMin || b.PremiumRate > premMax)) return false;
            if (b.RemainingYears.HasValue && (b.RemainingYears < yearsMin || b.RemainingYears > yearsMax)) return false;
            if (scaleMax < 500 && b.RemainingScale.HasValue && b.RemainingScale > scaleMax) return false;
            return true;
        }).ToList();

        // Renumber after filtering
        for (int i = 0; i < filtered.Count; i++)
            filtered[i].Index = i + 1;

        _grid.DataSource = null;
        _bindingList.Clear();
        foreach (var b in filtered) _bindingList.Add(b);
        _grid.DataSource = _bindingList;

        SetStatus($"🎯 符合条件：{filtered.Count} 只（共 {_allBonds.Count} 只）");
    }

    private void ResetFilters()
    {
        _numPriceMin.Value   = 80;
        _numPriceMax.Value   = 200;
        _numPremiumMin.Value = -50;
        _numPremiumMax.Value = 100;
        _numYearsMin.Value   = 0;
        _numYearsMax.Value   = 8;
        _numScaleMax.Value   = 500;
    }

    // ── 行颜色预警 ────────────────────────────────────────────────────────────
    private void Grid_RowPrePaint(object? sender, DataGridViewRowPrePaintEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
        var row = _grid.Rows[e.RowIndex];
        if (row.DataBoundItem is not BondInfo bond) return;

        Color bg = _grid.DefaultCellStyle.BackColor;
        if (bond.RedeemTriggerDays is > RedeemWarningMin and < RedeemWarningMax)
            bg = ColYellow;
        else if (bond.PutTriggerDays is > PutWarningThreshold)
            bg = ColRed;

        foreach (DataGridViewCell cell in row.Cells)
            cell.Style.BackColor = bg;
    }

    private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.ColumnIndex < 0 || e.RowIndex < 0) return;
        var row = _grid.Rows[e.RowIndex];
        if (row.DataBoundItem is not BondInfo bond) return;

        // Colour-code change percentage cells
        var col = _grid.Columns[e.ColumnIndex];
        if (col.DataPropertyName is nameof(BondInfo.ChangePercent) or nameof(BondInfo.StockChange))
        {
            if (e.Value is double v && e.CellStyle != null)
            {
                e.CellStyle.ForeColor = v >= 0 ? Color.DarkRed : Color.DarkGreen;
                e.FormattingApplied = true;
            }
        }
    }

    // ── 导出 CSV ──────────────────────────────────────────────────────────────
    private void ExportCsv()
    {
        if (_bindingList.Count == 0) return;

        using var dlg = new SaveFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"可转债数据_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            using var sw = new System.IO.StreamWriter(
                dlg.FileName,
                append: false,
                encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            // Header
            sw.WriteLine(
                "序号,转债代码,转债名称,现价,涨跌幅,正股名称,正股价,正股涨跌,正股PB," +
                "转股价,转股价值,转股溢价率,债券评级,回售触发价,回售触发天数," +
                "强赎触发价,强赎触发天数,强赎状态,转债占比,到期时间,剩余年限,剩余规模");

            static string N(double? v) => v.HasValue ? v.Value.ToString("N2", CultureInfo.InvariantCulture) : "";
            static string S(string? v) => v?.Replace(",", "，") ?? "";

            foreach (var b in _bindingList)
            {
                sw.WriteLine(string.Join(",",
                    b.Index, S(b.BondCode), S(b.BondName), N(b.Price), N(b.ChangePercent),
                    S(b.StockName), N(b.StockPrice), N(b.StockChange), N(b.StockPB),
                    N(b.ConversionPrice), N(b.ConversionValue), N(b.PremiumRate),
                    S(b.CreditRating), N(b.PutTriggerPrice), b.PutTriggerDays,
                    N(b.RedeemTriggerPrice), b.RedeemTriggerDays, S(b.RedeemStatus),
                    N(b.BondRatio), S(b.MaturityDate), N(b.RemainingYears), N(b.RemainingScale)));
            }

            SetStatus($"✅ 已导出 {_bindingList.Count} 条数据至：{dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── 辅助方法 ──────────────────────────────────────────────────────────────
    private void SetStatus(string text) =>
        _lblStatus.Text = text;

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(4, 6, 2, 0),
    };

    private static (NumericUpDown min, NumericUpDown max) MakeRangePair(
        decimal absMin, decimal absMax, decimal defMin, decimal defMax,
        string _unit, int dec = 1)
    {
        var min = new NumericUpDown
        {
            Minimum = absMin, Maximum = absMax, Value = defMin,
            DecimalPlaces = dec, Increment = dec == 2 ? 0.25m : 1m,
            Width = 75, Margin = new Padding(2, 4, 2, 0),
        };
        var max = new NumericUpDown
        {
            Minimum = absMin, Maximum = absMax, Value = defMax,
            DecimalPlaces = dec, Increment = dec == 2 ? 0.25m : 1m,
            Width = 75, Margin = new Padding(2, 4, 8, 0),
        };
        return (min, max);
    }
}
