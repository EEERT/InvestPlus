namespace InvestPlusUI.Models;

/// <summary>
/// 单只可转债的完整行情与基本面数据模型。
///
/// 数据来源说明：
///   - 现价、涨跌幅、正股价/涨跌、转股价/价值/溢价率、占比、强赎/回售触发价
///     → 东方财富 push2 实时接口（EastmoneyPush2Service）
///   - 强赎天计数、强赎状态
///     → 集思录（JisiluService）
///   - 转债名称（修正）、债券评级、到期时间、剩余规模、正股 P/B
///     → 东方财富 RPT_BOND_CB_LIST 报表（EastmoneyService）
/// </summary>
public class BondInfo
{
    /// <summary>在当前筛选结果中的显示序号（从 1 开始，每次筛选后重新编号）</summary>
    public int? Index { get; set; }

    /// <summary>转债代码（6 位纯数字，如 "127018"）</summary>
    public string? BondCode { get; set; }

    /// <summary>转债简称（如"海澜转债"），以东方财富 RPT_BOND_CB_LIST 为准</summary>
    public string? BondName { get; set; }

    /// <summary>转债最新成交价格（元，面值 100 元，正常交易区间约 80~200 元）</summary>
    public double? Price { get; set; }

    /// <summary>转债当日涨跌幅（%，正数为上涨，负数为下跌）</summary>
    public double? ChangePercent { get; set; }

    /// <summary>对应正股简称（如"海澜之家"）</summary>
    public string? StockName { get; set; }

    /// <summary>正股最新成交价格（元）</summary>
    public double? StockPrice { get; set; }

    /// <summary>正股当日涨跌幅（%）</summary>
    public double? StockChange { get; set; }

    /// <summary>
    /// 正股市净率 P/B（Price-to-Book Ratio）。
    /// P/B &lt; 1 通常意味着正股股价低于每股净资产，具有一定安全边际。
    /// </summary>
    public double? StockPB { get; set; }

    /// <summary>
    /// 当前有效转股价格（元）。
    /// 是转债转换为正股时使用的换算价格，可能因特定条件（如下修）发生变化。
    /// </summary>
    public double? ConversionPrice { get; set; }

    /// <summary>
    /// 转股价值（元）= 正股价 / 转股价 × 100。
    /// 表示"若立即将转债全部转换为正股，按当前正股价格折算的价值"。
    /// 转股价值 &gt; 100 时转债处于"实值"状态（转股合算）。
    /// </summary>
    public double? ConversionValue { get; set; }

    /// <summary>
    /// 转股溢价率（%）= (转债现价 / 转股价值 - 1) × 100。
    /// 表示"转债相对于其转股价值的溢价程度"。
    /// 溢价率越低，转债价格越接近正股价值，受正股上涨驱动越强。
    /// 负溢价率（破面）意味着转债现价低于转股价值，通常为套利机会。
    /// </summary>
    public double? PremiumRate { get; set; }

    /// <summary>债券信用评级（如 "AAA"、"AA+"、"AA" 等，来自东方财富详情）</summary>
    public string? CreditRating { get; set; }

    /// <summary>回售触发价格（元）：正股价连续低于此价格一定天数后投资者可要求发行人回购</summary>
    public double? PutTriggerPrice { get; set; }

    /// <summary>
    /// 回售触发天数（来自集思录）：正股价已连续满足回售触发条件的天数。
    /// 一般满 30 个交易日后投资者可要求发行人按面值回购。
    /// 若集思录未提供此字段，则为 null。
    /// </summary>
    public int? PutTriggerDays { get; set; }

    /// <summary>强赎触发价格（元）：正股价连续高于此价格 15 个交易日后发行人有权强制赎回</summary>
    public double? RedeemTriggerPrice { get; set; }

    /// <summary>
    /// 强赎天计数（来自集思录）：正股价已连续达到强赎触发价的交易天数。
    /// 达到 15 天时发行人有权公告强制赎回，投资者须在公告后转股或接受赎回。
    /// </summary>
    public int? RedeemTriggerDays { get; set; }

    /// <summary>
    /// 强赎状态（来自集思录，如"Y"=触发、"N"=未触发、"已公告不赎回"等）。
    /// "已公告不赎回"表示发行人本次主动放弃强赎权，倒计时归零重新计算。
    /// </summary>
    public string? RedeemStatus { get; set; }

    /// <summary>
    /// 转债占比（%）：转债流通市值占正股流通市值的比例。
    /// 占比越高，发行规模相对正股越大，对正股可能存在稀释压力。
    /// </summary>
    public double? BondRatio { get; set; }

    /// <summary>转债到期日（格式 "yyyy-MM-dd"，到期时若尚未转股则按面值 + 应计利息兑付）</summary>
    public string? MaturityDate { get; set; }

    /// <summary>从今天到到期日的剩余年限（精确到小数，如 2.75 表示约 2 年 9 个月）</summary>
    public double? RemainingYears { get; set; }

    /// <summary>
    /// 剩余未转股规模（亿元，来自东方财富 RPT_BOND_CB_LIST CURR_ISS_AMT 字段）。
    /// 规模越小，强赎后对市场冲击越小；规模过大则二级市场流动性一般更好。
    /// </summary>
    public double? RemainingScale { get; set; }
}
