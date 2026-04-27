using System.ComponentModel;
using System.Globalization;
using InvestPlusUI.Models;
using InvestPlusUI.Services;

namespace InvestPlusUI;

/// <summary>
/// 主窗体：可转债行情监测界面。
///
/// 界面布局（从上至下）：
///   工具栏   — 刷新按钮、自动刷新开关、刷新间隔选择、倒计时标签、导出 CSV 按钮
///   筛选区   — 价格区间、溢价率区间、剩余年限区间、剩余规模上限、实时搜索框
///   数据表格 — DataGridView（支持列排序、行色彩预警、涨跌着色）
///   状态栏   — 符合条件数量 / 总数、最后更新时间、数据源连通状态
///
/// 数据来源（无需 AKTools，全部后台自动获取）：
///   1. 东方财富 push2 实时行情
///   2. 集思录强赎数据
///   3. 东方财富 RPT_BOND_CB_LIST 债券详情（10 分钟缓存）
/// </summary>
public partial class MainForm : Form
{
    // ── 控件声明 ──────────────────────────────────────────────────────────────

    // 工具栏控件
    private readonly Button        _btnLoad;          // 立即刷新按钮
    private readonly Button        _btnExport;        // 导出 CSV 按钮
    private readonly CheckBox      _chkAutoRefresh;   // 自动刷新开关
    private readonly ComboBox      _cmbInterval;      // 刷新间隔下拉框
    private readonly Label         _lblCountdown;     // 倒计时标签（"下次刷新：xx 秒"）

    // 筛选控件
    private readonly NumericUpDown _numPriceMin;
    private readonly NumericUpDown _numPriceMax;
    private readonly NumericUpDown _numPremiumMin;
    private readonly NumericUpDown _numPremiumMax;
    private readonly NumericUpDown _numYearsMin;
    private readonly NumericUpDown _numYearsMax;
    private readonly NumericUpDown _numScaleMax;
    private readonly Button        _btnReset;
    private readonly TextBox       _txtSearch;        // 实时搜索框（按代码/名称过滤）

    // 数据表格与状态栏
    private readonly DataGridView         _grid;
    private readonly BindingList<BondInfo> _bindingList = new();
    private readonly StatusStrip          _statusStrip;
    private readonly ToolStripStatusLabel _lblStatus;
    private readonly ToolStripStatusLabel _lblUpdateTime;

    // ── 数据与状态 ────────────────────────────────────────────────────────────

    /// <summary>最近一次加载的全量转债列表（筛选前）</summary>
    private List<BondInfo> _allBonds = new();

    /// <summary>当前正在执行的加载任务的取消令牌，点"刷新"时先取消上次再新建</summary>
    private CancellationTokenSource? _loadCts;

    /// <summary>
    /// 数据服务（封装三路数据源的并行获取和缓存逻辑）。
    /// 使用同一实例贯穿整个应用生命周期，以便东方财富详情缓存持续生效。
    /// </summary>
    private readonly BondDataService _dataService = new();

    // ── 自动刷新相关 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 自动刷新计时器。
    /// 每隔 1 秒触发一次 Tick 事件：更新倒计时标签；倒计时归零时触发数据加载。
    /// </summary>
    private readonly System.Windows.Forms.Timer _refreshTimer = new()
    {
        Interval = 1000, // 每秒 tick 一次，用于更新倒计时显示
    };

    /// <summary>距下次自动刷新的剩余秒数</summary>
    private int _countdownSeconds;

    /// <summary>
    /// 自动刷新间隔选项（秒）与对应显示文字。
    /// 用户可以根据需要选择不同的刷新频率，交易时段建议 30~60 秒。
    /// </summary>
    private static readonly (int seconds, string label)[] RefreshIntervals =
    {
        (30,  "30 秒"),
        (60,  "1 分钟"),
        (120, "2 分钟"),
        (300, "5 分钟"),
    };

    // ── 颜色预警阈值与颜色 ────────────────────────────────────────────────────

    /// <summary>强赎预警背景色（浅黄）：强赎天数在 10~15 天之间，即将满足强赎条件</summary>
    private static readonly Color ColRedeemWarning = Color.FromArgb(255, 243, 200);

    /// <summary>回售风险背景色（浅红）：回售触发天数超过阈值</summary>
    private static readonly Color ColPutWarning = Color.FromArgb(255, 220, 220);

    /// <summary>强赎预警下限：强赎天数超过此值时开始预警（单位：天）</summary>
    private const int RedeemWarnMin = 10;

    /// <summary>强赎预警上限：正好满 15 天即可触发强赎</summary>
    private const int RedeemWarnMax = 15;

    /// <summary>回售风险阈值：回售触发天数超过此值时标红预警（单位：天）</summary>
    private const int PutWarnThreshold = 25;

    // ── 构造函数 ──────────────────────────────────────────────────────────────

    public MainForm()
    {
        Text          = "InvestPlus 可转债监测助手";
        Size          = new Size(1700, 900);
        MinimumSize   = new Size(1200, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font          = new Font("Microsoft YaHei UI", 9f);
        BackColor     = Color.White;

        // ── 工具栏 ──────────────────────────────────────────────────────────
        var toolbar = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 44,
            Padding   = new Padding(8, 6, 8, 0),
            BackColor = Color.FromArgb(38, 50, 56), // 深色工具栏背景
        };

        // 立即刷新按钮
        _btnLoad = new Button
        {
            Text                    = "🔄 刷新数据",
            Width                   = 100,
            Height                  = 28,
            Margin                  = new Padding(0, 2, 8, 0),
            UseVisualStyleBackColor = false,
            BackColor               = Color.FromArgb(38, 166, 154),
            ForeColor               = Color.White,
            FlatStyle               = FlatStyle.Flat,
        };
        _btnLoad.FlatAppearance.BorderSize = 0;

        // 自动刷新开关复选框
        _chkAutoRefresh = new CheckBox
        {
            Text      = "自动刷新",
            ForeColor = Color.White,
            AutoSize  = true,
            Margin    = new Padding(0, 6, 6, 0),
            Checked   = false,
        };

        // 刷新间隔下拉框
        _cmbInterval = new ComboBox
        {
            Width         = 80,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin        = new Padding(0, 4, 10, 0),
        };
        foreach (var (_, label) in RefreshIntervals)
            _cmbInterval.Items.Add(label);
        _cmbInterval.SelectedIndex = 1; // 默认选择 1 分钟

        // 倒计时标签
        _lblCountdown = new Label
        {
            Text      = "",
            ForeColor = Color.FromArgb(200, 230, 200),
            AutoSize  = true,
            Margin    = new Padding(0, 6, 16, 0),
        };

        // 导出 CSV 按钮
        _btnExport = new Button
        {
            Text                    = "📥 导出 CSV",
            Width                   = 100,
            Height                  = 28,
            Margin                  = new Padding(0, 2, 0, 0),
            UseVisualStyleBackColor = false,
            BackColor               = Color.FromArgb(69, 90, 100),
            ForeColor               = Color.White,
            FlatStyle               = FlatStyle.Flat,
            Enabled                 = false,
        };
        _btnExport.FlatAppearance.BorderSize = 0;

        // 将工具栏控件横向排列
        var toolFlow = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
            BackColor     = Color.Transparent,
        };
        toolFlow.Controls.AddRange(new Control[]
        {
            _btnLoad, _chkAutoRefresh, _cmbInterval, _lblCountdown, _btnExport,
        });
        toolbar.Controls.Add(toolFlow);

        // ── 筛选区 ──────────────────────────────────────────────────────────
        var filterPanel = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 50,
            Padding   = new Padding(8, 4, 8, 2),
            BackColor = Color.FromArgb(245, 245, 250),
        };

        // 各筛选范围控件（最小值 / 最大值 对）
        (_numPriceMin,   _numPriceMax)   = MakeRangePair(0m,    10000m, 80m,  200m, dec: 1);
        (_numPremiumMin, _numPremiumMax) = MakeRangePair(-100m,  2000m, -50m, 100m, dec: 1);
        (_numYearsMin,   _numYearsMax)   = MakeRangePair(0m,      30m,  0m,    8m,  dec: 2);

        // 剩余规模上限（亿元）
        _numScaleMax = new NumericUpDown
        {
            Minimum = 0, Maximum = 5000, Value = 500,
            DecimalPlaces = 1, Increment = 0.5m, Width = 75,
        };

        // 重置筛选按钮
        _btnReset = new Button
        {
            Text                    = "🔃 重置",
            Width                   = 70,
            Height                  = 26,
            UseVisualStyleBackColor = true,
            Margin                  = new Padding(4, 4, 12, 0),
        };

        // 实时搜索框（按代码或名称过滤）
        _txtSearch = new TextBox
        {
            Width       = 140,
            PlaceholderText = "🔍 代码 / 名称搜索",
            Margin      = new Padding(4, 4, 0, 0),
        };

        var filterFlow = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
        };
        filterFlow.Controls.AddRange(new Control[]
        {
            MakeLabel("价格（元）"),       _numPriceMin,   MakeLabel("~"), _numPriceMax,
            MakeLabel("  溢价率（%）"),    _numPremiumMin, MakeLabel("~"), _numPremiumMax,
            MakeLabel("  剩余年限（年）"), _numYearsMin,   MakeLabel("~"), _numYearsMax,
            MakeLabel("  剩余规模 ≤"),     _numScaleMax,   MakeLabel("亿"),
            _btnReset,
            _txtSearch,
        });
        filterPanel.Controls.Add(filterFlow);

        // ── 数据表格 ─────────────────────────────────────────────────────────
        _grid = new DataGridView
        {
            Dock                          = DockStyle.Fill,
            ReadOnly                      = true,
            AllowUserToAddRows            = false,
            AllowUserToDeleteRows         = false,
            AllowUserToResizeRows         = false,
            AutoSizeColumnsMode           = DataGridViewAutoSizeColumnsMode.None,
            SelectionMode                 = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible             = false,
            BackgroundColor               = Color.White,
            BorderStyle                   = BorderStyle.None,
            GridColor                     = Color.FromArgb(230, 230, 230),
            // 交替行背景色（淡蓝白相间，便于视线扫行）
            AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(245, 248, 255),
            },
            // 表头样式（深色背景白字）
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor  = Color.FromArgb(38, 50, 56),
                ForeColor  = Color.White,
                Font       = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
                Alignment  = DataGridViewContentAlignment.MiddleCenter,
            },
            ColumnHeadersHeight       = 32,
            RowTemplate               = { Height = 24 },
            EnableHeadersVisualStyles = false,
            AutoGenerateColumns       = false,
        };
        BuildGridColumns();

        // ── 状态栏 ───────────────────────────────────────────────────────────
        _statusStrip = new StatusStrip { BackColor = Color.FromArgb(240, 240, 245) };
        _lblStatus = new ToolStripStatusLabel("就绪 — 正在获取数据，请稍候…")
        {
            Spring    = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _lblUpdateTime = new ToolStripStatusLabel("")
        {
            Alignment = ToolStripItemAlignment.Right,
            ForeColor = Color.FromArgb(80, 80, 80),
        };
        _statusStrip.Items.AddRange(new ToolStripItem[] { _lblStatus, _lblUpdateTime });

        // ── 布局组装（DockStyle.Fill 的控件最先 Add，确保填满剩余空间） ────────
        Controls.Add(_grid);         // 填满中央区域
        Controls.Add(filterPanel);   // 停靠顶部（在 grid 上方）
        Controls.Add(toolbar);       // 停靠顶部（在 filterPanel 上方）
        Controls.Add(_statusStrip);  // 停靠底部

        // ── 事件绑定 ─────────────────────────────────────────────────────────
        _btnLoad.Click           += async (_, _) => await LoadDataAsync();
        _btnExport.Click         += (_, _)        => ExportCsv();
        _btnReset.Click          += (_, _)        => ResetFilters();
        _chkAutoRefresh.CheckedChanged += OnAutoRefreshToggled;
        _cmbInterval.SelectedIndexChanged += OnIntervalChanged;
        _refreshTimer.Tick       += OnRefreshTimerTick;

        // 筛选条件变化时实时过滤（无需点击按钮）
        _numPriceMin.ValueChanged   += (_, _) => ApplyFilters();
        _numPriceMax.ValueChanged   += (_, _) => ApplyFilters();
        _numPremiumMin.ValueChanged += (_, _) => ApplyFilters();
        _numPremiumMax.ValueChanged += (_, _) => ApplyFilters();
        _numYearsMin.ValueChanged   += (_, _) => ApplyFilters();
        _numYearsMax.ValueChanged   += (_, _) => ApplyFilters();
        _numScaleMax.ValueChanged   += (_, _) => ApplyFilters();
        _txtSearch.TextChanged      += (_, _) => ApplyFilters(); // 搜索框实时过滤

        _grid.CellFormatting += Grid_CellFormatting;
        _grid.RowPrePaint    += Grid_RowPrePaint;

        // 窗体加载完成后自动触发第一次数据获取
        Load += async (_, _) => await LoadDataAsync();
    }

    // ── 列定义 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 构建数据表格的列定义。
    /// 参数含义：(列头文字, 绑定属性名, 列宽像素, 数字格式字符串, 是否居中对齐)
    /// </summary>
    private void BuildGridColumns()
    {
        var cols = new (string header, string prop, int w, string? fmt, bool center)[]
        {
            ("序号",        nameof(BondInfo.Index),             45,  null,  true),
            ("转债代码",    nameof(BondInfo.BondCode),          72,  null,  true),
            ("转债名称",    nameof(BondInfo.BondName),          92,  null,  false),
            ("现价(元)",    nameof(BondInfo.Price),             72,  "N2",  true),
            ("涨跌(%)",     nameof(BondInfo.ChangePercent),     70,  "N2",  true),
            ("正股名称",    nameof(BondInfo.StockName),         90,  null,  false),
            ("正股价",      nameof(BondInfo.StockPrice),        68,  "N2",  true),
            ("正股涨跌(%)", nameof(BondInfo.StockChange),       78,  "N2",  true),
            ("正股PB",      nameof(BondInfo.StockPB),           58,  "N2",  true),
            ("转股价",      nameof(BondInfo.ConversionPrice),   68,  "N2",  true),
            ("转股价值",    nameof(BondInfo.ConversionValue),   72,  "N2",  true),
            ("溢价率(%)",   nameof(BondInfo.PremiumRate),       72,  "N2",  true),
            ("债券评级",    nameof(BondInfo.CreditRating),      66,  null,  true),
            ("回售触发价",  nameof(BondInfo.PutTriggerPrice),   78,  "N2",  true),
            ("回售天数",    nameof(BondInfo.PutTriggerDays),    66,  null,  true),
            ("强赎触发价",  nameof(BondInfo.RedeemTriggerPrice),78,  "N2",  true),
            ("强赎天数",    nameof(BondInfo.RedeemTriggerDays), 66,  null,  true),
            ("强赎状态",    nameof(BondInfo.RedeemStatus),      88,  null,  true),
            ("转债占比(%)", nameof(BondInfo.BondRatio),         76,  "N2",  true),
            ("到期时间",    nameof(BondInfo.MaturityDate),      86,  null,  true),
            ("剩余年限",    nameof(BondInfo.RemainingYears),    68,  "N2",  true),
            ("剩余规模(亿)",nameof(BondInfo.RemainingScale),    82,  "N2",  true),
        };

        foreach (var (header, prop, w, fmt, center) in cols)
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText       = header,
                DataPropertyName = prop,
                Width            = w,
                SortMode         = DataGridViewColumnSortMode.Automatic,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Format    = fmt ?? string.Empty,
                    Alignment = center
                        ? DataGridViewContentAlignment.MiddleCenter
                        : DataGridViewContentAlignment.MiddleLeft,
                },
            });
        }
    }

    // ── 数据加载 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 异步加载（或刷新）可转债数据。
    /// 若上次加载尚未完成，先取消再重新发起，避免并发冲突。
    /// </summary>
    private async Task LoadDataAsync()
    {
        // 取消正在进行的上次加载
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        // 加载期间禁用刷新按钮，防止用户重复点击
        _btnLoad.Enabled = false;
        _btnLoad.Text    = "⏳ 加载中…";
        SetStatus("正在获取数据，请稍候…（首次加载约需 10~30 秒）");

        // 重置自动刷新倒计时
        ResetCountdown();

        try
        {
            // 调用数据服务（并行获取三路数据并合并）
            var (bonds, lastUpdate) = await _dataService.GetBondsAsync(ct);

            if (bonds.Count == 0)
            {
                // 非交易时段或数据源暂时无数据，属正常情况
                SetStatus("⚠ 暂无数据（可能为非交易时段，或数据源返回空）");
                return;
            }

            // 保存全量数据，应用当前筛选条件
            _allBonds = bonds;
            ApplyFilters();
            _btnExport.Enabled  = true;
            _lblUpdateTime.Text = $"最后更新：{lastUpdate}";
        }
        catch (OperationCanceledException)
        {
            // 被新的刷新请求取消，属正常流程，不提示错误
            SetStatus("已取消上次加载。");
        }
        catch (Exception ex)
        {
            // 网络错误或数据解析错误
            SetStatus($"❌ 加载失败：{ex.Message}");
            MessageBox.Show(
                $"获取数据时发生错误：\n{ex.Message}\n\n" +
                "请检查：\n" +
                "1. 网络连接是否正常\n" +
                "2. 是否可以访问 push2.eastmoney.com 和 datacenter-web.eastmoney.com\n" +
                "3. 若在非交易时段，部分接口可能返回空数据，属正常现象",
                "加载失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _btnLoad.Enabled = true;
            _btnLoad.Text    = "🔄 刷新数据";
        }
    }

    // ── 自动刷新逻辑 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 自动刷新开关状态变化时触发：开启则启动定时器，关闭则停止定时器并清空倒计时。
    /// </summary>
    private void OnAutoRefreshToggled(object? sender, EventArgs e)
    {
        if (_chkAutoRefresh.Checked)
        {
            ResetCountdown();
            _refreshTimer.Start();
        }
        else
        {
            _refreshTimer.Stop();
            _lblCountdown.Text = "";
        }
    }

    /// <summary>
    /// 用户修改刷新间隔时，重置倒计时（立即以新间隔重新计时）。
    /// </summary>
    private void OnIntervalChanged(object? sender, EventArgs e)
    {
        if (_chkAutoRefresh.Checked)
            ResetCountdown();
    }

    /// <summary>
    /// 每秒触发一次的定时器事件：更新倒计时显示；倒计时归零时触发数据刷新。
    /// </summary>
    private async void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        _countdownSeconds--;

        if (_countdownSeconds <= 0)
        {
            // 倒计时到零：重置倒计时后立即刷新数据
            ResetCountdown();
            await LoadDataAsync();
        }
        else
        {
            // 更新倒计时显示
            _lblCountdown.Text = $"下次刷新：{_countdownSeconds} 秒";
        }
    }

    /// <summary>
    /// 根据当前选择的刷新间隔，重置倒计时秒数并更新标签。
    /// </summary>
    private void ResetCountdown()
    {
        int idx = _cmbInterval.SelectedIndex;
        if (idx < 0 || idx >= RefreshIntervals.Length) idx = 1;
        _countdownSeconds  = RefreshIntervals[idx].seconds;
        _lblCountdown.Text = _chkAutoRefresh.Checked
            ? $"下次刷新：{_countdownSeconds} 秒"
            : "";
    }

    // ── 筛选逻辑 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 根据当前筛选条件和搜索关键词过滤 _allBonds，更新表格显示。
    /// 每次筛选条件或搜索词变化时自动调用，无需手动点击。
    /// </summary>
    private void ApplyFilters()
    {
        if (_allBonds.Count == 0) return;

        // 读取数值筛选范围
        double priceMin  = (double)_numPriceMin.Value;
        double priceMax  = (double)_numPriceMax.Value;
        double premMin   = (double)_numPremiumMin.Value;
        double premMax   = (double)_numPremiumMax.Value;
        double yearsMin  = (double)_numYearsMin.Value;
        double yearsMax  = (double)_numYearsMax.Value;
        double scaleMax  = (double)_numScaleMax.Value;

        // 搜索关键词（同时匹配代码和名称，大小写不敏感）
        var keyword = _txtSearch.Text.Trim();

        var filtered = _allBonds.Where(b =>
        {
            // 价格区间筛选
            if (b.Price.HasValue &&
                (b.Price < priceMin || b.Price > priceMax)) return false;

            // 转股溢价率区间筛选
            if (b.PremiumRate.HasValue &&
                (b.PremiumRate < premMin || b.PremiumRate > premMax)) return false;

            // 剩余年限区间筛选
            if (b.RemainingYears.HasValue &&
                (b.RemainingYears < yearsMin || b.RemainingYears > yearsMax)) return false;

            // 剩余规模上限筛选（设置为上限 500 时视为不筛选）
            if (scaleMax < 500 &&
                b.RemainingScale.HasValue &&
                b.RemainingScale > scaleMax) return false;

            // 代码/名称关键词搜索（空搜索词时跳过此筛选）
            if (!string.IsNullOrEmpty(keyword))
            {
                bool codeMatch = b.BondCode?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true;
                bool nameMatch = b.BondName?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true;
                if (!codeMatch && !nameMatch) return false;
            }

            return true;
        }).ToList();

        // 筛选后重新编号（序号从 1 开始）
        for (int i = 0; i < filtered.Count; i++)
            filtered[i].Index = i + 1;

        // 更新表格数据源（先清空再批量添加，减少闪烁）
        _grid.DataSource = null;
        _bindingList.Clear();
        foreach (var b in filtered) _bindingList.Add(b);
        _grid.DataSource = _bindingList;

        SetStatus($"🎯 符合条件：{filtered.Count} 只（共 {_allBonds.Count} 只）");
    }

    /// <summary>
    /// 重置所有筛选条件为默认值（等同于初始状态）。
    /// </summary>
    private void ResetFilters()
    {
        _numPriceMin.Value   = 80;
        _numPriceMax.Value   = 200;
        _numPremiumMin.Value = -50;
        _numPremiumMax.Value = 100;
        _numYearsMin.Value   = 0;
        _numYearsMax.Value   = 8;
        _numScaleMax.Value   = 500;
        _txtSearch.Clear();
    }

    // ── 行颜色预警 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 根据强赎天数和回售天数为整行设置背景色预警：
    ///   浅黄色 — 强赎天数在 10~15 天之间（即将满足强赎条件，需关注）
    ///   浅红色 — 回售触发天数超过阈值（存在回售风险）
    /// </summary>
    private void Grid_RowPrePaint(object? sender, DataGridViewRowPrePaintEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
        var row = _grid.Rows[e.RowIndex];
        if (row.DataBoundItem is not BondInfo bond) return;

        // 判断预警类型（强赎优先级高于回售）
        Color bg;
        if (bond.RedeemTriggerDays is > RedeemWarnMin and < RedeemWarnMax)
            bg = ColRedeemWarning;
        else if (bond.PutTriggerDays is > PutWarnThreshold)
            bg = ColPutWarning;
        else
            return; // 无需预警，保留默认背景色

        // 对本行每个单元格设置背景色
        foreach (DataGridViewCell cell in row.Cells)
            cell.Style.BackColor = bg;
    }

    /// <summary>
    /// 为涨跌幅类列（转债涨跌、正股涨跌）设置红涨绿跌的文字颜色。
    /// 符合中国股市的习惯：上涨红色，下跌绿色。
    /// </summary>
    private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.ColumnIndex < 0 || e.RowIndex < 0) return;
        if (_grid.Rows[e.RowIndex].DataBoundItem is not BondInfo) return;

        var colName = _grid.Columns[e.ColumnIndex].DataPropertyName;
        if (colName is nameof(BondInfo.ChangePercent) or nameof(BondInfo.StockChange))
        {
            if (e.Value is double v && e.CellStyle != null)
            {
                // 上涨红色、下跌绿色（A 股惯例）
                e.CellStyle.ForeColor = v >= 0 ? Color.Crimson : Color.SeaGreen;
                e.FormattingApplied   = true;
            }
        }
    }

    // ── 导出 CSV ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 将当前筛选结果导出为 UTF-8 BOM 编码的 CSV 文件。
    /// UTF-8 BOM 确保 Microsoft Excel 能正确识别中文编码。
    /// </summary>
    private void ExportCsv()
    {
        if (_bindingList.Count == 0) return;

        using var dlg = new SaveFileDialog
        {
            Filter   = "CSV 文件 (*.csv)|*.csv",
            FileName = $"可转债数据_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            // UTF-8 BOM 编码，Excel 可以直接打开且中文不乱码
            using var sw = new System.IO.StreamWriter(
                dlg.FileName, append: false,
                encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            // 写入表头行
            sw.WriteLine(
                "序号,转债代码,转债名称,现价,涨跌幅(%),正股名称,正股价,正股涨跌(%)," +
                "正股PB,转股价,转股价值,转股溢价率(%),债券评级,回售触发价," +
                "强赎触发价,强赎天数,强赎状态,转债占比(%),到期时间,剩余年限,剩余规模(亿)");

            // 辅助格式化：double? → 字符串（保留 2 位小数）
            static string N(double? v) =>
                v.HasValue ? v.Value.ToString("N2", CultureInfo.InvariantCulture) : "";
            // 辅助格式化：string? → 字符串（替换逗号以避免 CSV 格式错误）
            static string S(string? v) => v?.Replace(",", "，") ?? "";

            foreach (var b in _bindingList)
            {
                sw.WriteLine(string.Join(",",
                    b.Index, S(b.BondCode), S(b.BondName),
                    N(b.Price), N(b.ChangePercent),
                    S(b.StockName), N(b.StockPrice), N(b.StockChange), N(b.StockPB),
                    N(b.ConversionPrice), N(b.ConversionValue), N(b.PremiumRate),
                    S(b.CreditRating), N(b.PutTriggerPrice),
                    N(b.RedeemTriggerPrice), b.RedeemTriggerDays, S(b.RedeemStatus),
                    N(b.BondRatio), S(b.MaturityDate), N(b.RemainingYears), N(b.RemainingScale)));
            }

            SetStatus($"✅ 已导出 {_bindingList.Count} 条数据至：{dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── 窗体关闭清理 ──────────────────────────────────────────────────────────

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 停止定时器并释放数据服务资源
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _dataService.Dispose();
        base.OnFormClosing(e);
    }

    // ── 辅助方法 ──────────────────────────────────────────────────────────────

    /// <summary>更新状态栏左侧文字</summary>
    private void SetStatus(string text) => _lblStatus.Text = text;

    /// <summary>创建筛选区使用的简短标签控件</summary>
    private static Label MakeLabel(string text) => new()
    {
        Text      = text,
        AutoSize  = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin    = new Padding(4, 6, 2, 0),
    };

    /// <summary>
    /// 创建一对 NumericUpDown 控件（最小值 / 最大值输入框），用于数值区间筛选。
    /// </summary>
    private static (NumericUpDown min, NumericUpDown max) MakeRangePair(
        decimal absMin, decimal absMax, decimal defMin, decimal defMax, int dec)
    {
        var min = new NumericUpDown
        {
            Minimum = absMin, Maximum = absMax, Value = defMin,
            DecimalPlaces = dec, Increment = dec >= 2 ? 0.25m : 1m,
            Width = 72, Margin = new Padding(2, 4, 2, 0),
        };
        var max = new NumericUpDown
        {
            Minimum = absMin, Maximum = absMax, Value = defMax,
            DecimalPlaces = dec, Increment = dec >= 2 ? 0.25m : 1m,
            Width = 72, Margin = new Padding(2, 4, 8, 0),
        };
        return (min, max);
    }
}
