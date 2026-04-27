using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using InvestPlusUI.Models;

namespace InvestPlusUI.Services;

/// <summary>
/// 可转债多源数据协调服务。
///
/// 本服务并行调用以下三个数据源，将结果合并为完整的 BondInfo 列表：
///
///   1. 东方财富 Push2 实时行情（EastmoneyPush2Service）
///      提供：现价、涨跌幅、正股价/涨跌、转股价/价值/溢价率、强赎/回售触发价
///      频率：每次刷新都重新获取（数据随交易实时变化）
///
///   2. 集思录强赎数据（JisiluService）
///      提供：强赎天计数、强赎状态、回售天计数
///      频率：每次刷新都重新获取（强赎/回售天数每个交易日都可能变化）
///
///   3. 东方财富 RPT_BOND_CB_LIST 详情（EastmoneyService）
///      提供：转债名称（修正版）、债券评级、到期时间、剩余规模、正股 P/B
///      频率：本地缓存 10 分钟（这些静态数据变化极少，无需每次刷新）
///
/// 合并逻辑：
///   以 Push2 数据为主线（含全部在市转债），
///   通过规范化的 6 位转债代码做 left join 附加集思录和东方财富详情数据。
/// </summary>
public sealed class BondDataService : IDisposable
{
    // ── 共享常量 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 东方财富用于表示"无有效数据"的特殊占位值（-9999.99 或更小）。
    /// push2 和数据中心接口在字段无数据时均可能返回此值，过滤时需统一处理。
    /// </summary>
    internal const double EastmoneyInvalidValue = -9999.99;
    private const double DefaultPutTriggerRatio = 0.7;
    private const double DefaultRedeemTriggerRatio = 1.3;

    // ── 服务实例 ──────────────────────────────────────────────────────────────

    /// <summary>AKTools 服务（bond_cov_comparison / stock_zh_a_spot* / bond_cb_redeem_jsl）</summary>
    private readonly AktoolsService _aktools = new();

    /// <summary>东方财富 push2 实时行情服务（AKTools 异常时的降级兜底）</summary>
    private readonly EastmoneyPush2Service _push2 = new();

    /// <summary>东方财富 RPT_BOND_CB_LIST 债券详情服务</summary>
    private readonly EastmoneyService _detail = new();

    /// <summary>集思录强赎数据服务（AKTools 异常时的降级兜底）</summary>
    private readonly JisiluService _jisilu = new();

    // ── 详情数据缓存 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 东方财富债券详情缓存数据（评级、到期时间等静态字段）。
    /// 评级调整、规模变化等事件发生频率极低，10 分钟缓存足够准确，
    /// 同时大幅减少重复的 HTTP 请求，提高整体刷新速度。
    /// </summary>
    private List<Dictionary<string, string?>>? _cachedDetails;

    /// <summary>缓存写入时间，用于判断是否过期</summary>
    private DateTime _detailsCachedAt = DateTime.MinValue;

    /// <summary>详情缓存有效期（10 分钟）</summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    // ── 公共入口 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 并行获取全部数据源并合并，返回可转债列表及数据更新时间字符串。
    ///
    /// 返回值含义：
    ///   bonds 为空列表时，可能是：
    ///     a) Push2 请求失败（调用方通过 lastUpdate 中的提示区分）
    ///     b) 非交易时段，Push2 返回空列表（正常现象）
    ///   bonds 非空时，为正常合并结果。
    /// </summary>
    public async Task<(List<BondInfo> bonds, string lastUpdate, bool isNetworkError)> GetBondsAsync(
        CancellationToken ct = default)
    {
        // ── 并行启动请求 ────────────────────────────────────────────────────────
        // 优先使用 AKTools：转债基础 + 正股行情 + 强赎/回售天数
        var bondBaseTask = _aktools.FetchBondComparisonAsync(ct);
        var stockTask    = _aktools.FetchStockSpotAsync(ct);
        var redeemTask   = _aktools.FetchRedeemDataAsync(ct);

        // 东方财富详情：缓存未过期时直接使用缓存，避免重复网络请求
        bool useCache = _cachedDetails != null &&
                        DateTime.Now - _detailsCachedAt < CacheDuration;
        var detailTask = useCache
            ? Task.FromResult<List<Dictionary<string, string?>>?>(_cachedDetails)
            : _detail.FetchBondDetailsAsync(ct);

        // 等待请求完成（并行执行，总耗时取决于最慢的那个）
        await Task.WhenAll(bondBaseTask, stockTask, redeemTask, detailTask);

        var bondBaseData = await bondBaseTask;
        var stockData    = await stockTask;
        var redeemData   = await redeemTask;
        var detailData = await detailTask;

        // 构造统一主数据（与原 push2 字段一致），AKTools 失败则回退到原 push2
        var push2LikeRows = BuildRowsFromAktools(bondBaseData, stockData);
        if (push2LikeRows == null)
        {
            push2LikeRows = await _push2.FetchBondListAsync(ct);
        }

        // AKTools 强赎数据缺失时，回退到集思录直连
        if (redeemData == null)
        {
            redeemData = await _jisilu.FetchRedeemDataAsync(ct);
        }

        // ── 更新详情缓存 ────────────────────────────────────────────────────────
        if (!useCache && detailData != null)
        {
            _cachedDetails   = detailData;
            _detailsCachedAt = DateTime.Now;
        }
        // 若本次请求失败，回退使用旧缓存（降级处理，保证页面不空白）
        var detailToUse = detailData ?? _cachedDetails;

        // ── 合并数据 ────────────────────────────────────────────────────────────
        // push2Data == null 表示网络/API 错误；push2Data 为空列表表示非交易时段无数据
        bool isNetworkError = push2LikeRows == null;
        var bonds = MergeData(push2LikeRows, redeemData, detailToUse);
        return (bonds, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), isNetworkError);
    }

    private static List<Dictionary<string, string?>>? BuildRowsFromAktools(
        List<Dictionary<string, string?>>? bondRows,
        Dictionary<string, (string? name, double? price, double? change)>? stockRows)
    {
        if (bondRows == null) return null;
        var result = new List<Dictionary<string, string?>>();

        foreach (var row in bondRows)
        {
            var bondCode = GetString(row, "转债代码", "债券代码", "代码", "symbol", "bond_code");
            if (string.IsNullOrWhiteSpace(bondCode)) continue;
            bondCode = NormalizeCode(bondCode);
            if (string.IsNullOrWhiteSpace(bondCode)) continue;

            var stockCode = GetString(row, "正股代码", "股票代码", "stock_code", "symbol_stk");
            if (!string.IsNullOrWhiteSpace(stockCode))
                stockCode = NormalizeCode(stockCode);

            string? stockName = null;
            double? stockPrice = null;
            double? stockChange = null;
            if (!string.IsNullOrWhiteSpace(stockCode) &&
                stockRows != null &&
                stockRows.TryGetValue(stockCode, out var stock))
            {
                stockName = stock.name;
                stockPrice = stock.price;
                stockChange = stock.change;
            }

            var convPrice = GetDouble(row, "转股价", "转股价格", "conversion_price");
            var putTrigger = GetDouble(row, "回售触发价", "put_trigger_price");
            var redeemTrigger = GetDouble(row, "强赎触发价", "redeem_trigger_price");

            // AKTools 基础数据无触发价时，按常见条款比率从转股价推导
            if (!putTrigger.HasValue && convPrice.HasValue) putTrigger = convPrice.Value * DefaultPutTriggerRatio;
            if (!redeemTrigger.HasValue && convPrice.HasValue) redeemTrigger = convPrice.Value * DefaultRedeemTriggerRatio;

            var mapped = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["转债代码"] = bondCode,
                ["转债名称"] = GetString(row, "转债名称", "债券简称", "名称", "bond_name", "name"),
                ["现价"] = GetDouble(row, "现价", "最新价", "价格", "price", "close")
                    ?.ToString(CultureInfo.InvariantCulture),
                ["涨跌幅"] = GetDouble(row, "涨跌幅", "changepercent", "pct_chg", "change")
                    ?.ToString(CultureInfo.InvariantCulture),
                ["正股代码"] = stockCode,
                ["正股名称"] = stockName ?? GetString(row, "正股名称", "股票名称", "stock_name"),
                ["正股价"] = stockPrice?.ToString(CultureInfo.InvariantCulture),
                ["正股涨跌"] = stockChange?.ToString(CultureInfo.InvariantCulture),
                ["转股价"] = convPrice?.ToString(CultureInfo.InvariantCulture),
                ["转股价值"] = GetDouble(row, "转股价值", "conversion_value")
                    ?.ToString(CultureInfo.InvariantCulture),
                ["转股溢价率"] = GetDouble(row, "转股溢价率", "premium_rt", "premium_rate")
                    ?.ToString(CultureInfo.InvariantCulture),
                ["回售触发价"] = putTrigger?.ToString(CultureInfo.InvariantCulture),
                ["强赎触发价"] = redeemTrigger?.ToString(CultureInfo.InvariantCulture),
                ["转债占比"] = GetDouble(row, "转债占比", "bond_ratio", "convertible_bond_ratio")
                    ?.ToString(CultureInfo.InvariantCulture),
            };

            result.Add(mapped);
        }

        return result;
    }

    // ── 字段提取辅助方法 ──────────────────────────────────────────────────────

    /// <summary>
    /// 按候选字段名列表，从字典中依次查找并返回第一个有效的 double 值。
    /// 自动过滤：null、空串、"-"、"--"、东方财富特殊占位值（-9999.99 等）。
    /// </summary>
    private static double? GetDouble(Dictionary<string, string?> row, params string[] names)
    {
        foreach (var name in names)
        {
            if (!row.TryGetValue(name, out var s) || string.IsNullOrWhiteSpace(s)) continue;
            if (s == "-" || s == "--") continue;
            if (!double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) continue;
            // 过滤东方财富的"无数据"特殊值（-9999.99 或 -9999）
            if (Math.Abs(d - EastmoneyInvalidValue) < 0.01 || d <= -9999) continue;
            return d;
        }
        return null;
    }

    /// <summary>
    /// 按候选字段名列表，从字典中依次查找并返回第一个有效的字符串值。
    /// 自动过滤：null、空串、"-"、"--"。
    /// </summary>
    private static string? GetString(Dictionary<string, string?> row, params string[] names)
    {
        foreach (var name in names)
        {
            if (!row.TryGetValue(name, out var s)) continue;
            if (string.IsNullOrWhiteSpace(s) || s == "-" || s == "--") continue;
            return s;
        }
        return null;
    }

    // ── 代码规范化 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 将各来源的转债代码统一规范化为 6 位纯数字字符串，
    /// 方便在不同数据源之间做关联匹配（left join）。
    ///
    /// 处理场景：
    ///   "SH127018" / "SZ127018" → "127018"（去除交易所前缀）
    ///   "127018.0"              → "127018"（去除浮点 .0 后缀）
    ///   "127018"                → "127018"（无需变化）
    ///   "12701"（5 位）         → "012701"（补零至 6 位）
    /// </summary>
    private static string NormalizeCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var s = Regex.Replace(raw.Trim(), @"^(SH|SZ|BJ)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\.0+$", ""); // 去掉 .0 后缀
        if (long.TryParse(s, out var num))
            return num.ToString().PadLeft(6, '0'); // 纯数字时补零至 6 位
        return s;
    }

    // ── 衍生字段计算 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 计算转股价值：正股价 / 转股价 × 100（转债面值为 100 元）。
    /// 含义：若将转债立即转换为正股，价值相当于多少元。
    /// </summary>
    private static double? CalcConversionValue(double? stockPrice, double? convPrice)
    {
        if (stockPrice.HasValue && convPrice.HasValue && convPrice.Value > 0)
            return (stockPrice.Value / convPrice.Value) * 100.0;
        return null;
    }

    /// <summary>
    /// 计算转股溢价率：(转债现价 / 转股价值 - 1) × 100%。
    /// 含义：转债相对于其转股价值的溢价百分比；负值表示折价（破面）。
    /// </summary>
    private static double? CalcPremiumRate(double? bondPrice, double? convValue)
    {
        if (bondPrice.HasValue && convValue.HasValue && convValue.Value > 0)
            return (bondPrice.Value / convValue.Value - 1.0) * 100.0;
        return null;
    }

    /// <summary>
    /// 计算剩余年限（从今天到到期日的年数，精确到小数点后 4 位）。
    /// 已到期的转债返回 0（不返回负数）。
    /// </summary>
    private static double? CalcRemainingYears(DateTime? maturity)
    {
        if (!maturity.HasValue) return null;
        var days = (maturity.Value.Date - DateTime.Today).TotalDays;
        return days > 0 ? Math.Round(days / 365.25, 4) : 0.0;
    }

    // ── 核心合并逻辑 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 将三路数据合并成 BondInfo 列表。
    ///
    /// 合并策略：
    ///   以 Push2 数据（push2Data）为"左表"主线，
    ///   集思录强赎数据（redeemData）和东方财富详情（detailData）作为"右表"，
    ///   通过 6 位规范化转债代码做 left join（右表无对应数据时字段保持 null）。
    ///
    /// 参数说明：
    ///   push2Data  - 东方财富 push2 实时行情，key 为中文字段名
    ///   redeemData - 集思录强赎及回售数据，key 为规范化转债代码
    ///   detailData - 东方财富 RPT_BOND_CB_LIST 详情，key 为东方财富原始英文字段名
    /// </summary>
    private List<BondInfo> MergeData(
        List<Dictionary<string, string?>>?                                push2Data,
        Dictionary<string, (int? redeemDays, string? status, int? putDays)>? redeemData,
        List<Dictionary<string, string?>>?                                detailData)
    {
        // Push2 数据是主数据源：
        //   null  = 请求失败（网络错误），无法继续
        //   空列表 = 非交易时段无数据，同样无法展示行情，直接返回
        if (push2Data == null)
        {
            System.Diagnostics.Debug.WriteLine("[BondDataService] Push2 请求失败，无法合并数据");
            return new List<BondInfo>();
        }
        if (push2Data.Count == 0)
        {
            System.Diagnostics.Debug.WriteLine("[BondDataService] Push2 返回空列表（可能为非交易时段）");
            return new List<BondInfo>();
        }

        // ── 1. 构建东方财富详情查找表（规范化转债代码 → 详情行） ────────────────
        var detailByCode = new Dictionary<string, Dictionary<string, string?>>(
            StringComparer.OrdinalIgnoreCase);
        if (detailData != null)
        {
            foreach (var row in detailData)
            {
                // RPT_BOND_CB_LIST 的转债代码字段为 SECURITY_CODE
                var code = row.GetValueOrDefault("SECURITY_CODE");
                if (string.IsNullOrEmpty(code)) continue;
                var norm = NormalizeCode(code);
                if (!string.IsNullOrEmpty(norm))
                    detailByCode[norm] = row;
            }
        }

        // ── 2. 遍历 Push2 数据，构建 BondInfo 列表 ────────────────────────────
        var result = new List<BondInfo>();

        foreach (var row in push2Data)
        {
            // 转债代码：Push2 的 "转债代码" 字段（已通过 FieldMap 映射）
            var bondCode = GetString(row, "转债代码");
            if (string.IsNullOrEmpty(bondCode)) continue;
            var normCode = NormalizeCode(bondCode);
            if (string.IsNullOrEmpty(normCode)) continue;

            // ── 行情基础字段（均来自 Push2） ──────────────────────────────────
            var price         = GetDouble(row, "现价");
            var changePercent = GetDouble(row, "涨跌幅");
            var bondName      = GetString(row, "转债名称");
            var stockName     = GetString(row, "正股名称");
            var stockPrice    = GetDouble(row, "正股价");
            var stockChange   = GetDouble(row, "正股涨跌");
            var convPrice     = GetDouble(row, "转股价");
            var convValue     = GetDouble(row, "转股价值");
            var premiumRate   = GetDouble(row, "转股溢价率");
            var putTrigger    = GetDouble(row, "回售触发价");
            var redeemTrigger = GetDouble(row, "强赎触发价");

            // Push2 未提供 convValue / premiumRate 时，自行计算衍生值
            convValue   ??= CalcConversionValue(stockPrice, convPrice);
            premiumRate ??= CalcPremiumRate(price, convValue);

            // ── 3. 合并东方财富详情（评级、到期时间、规模、PB、名称修正） ────────
            string?   creditRating   = null;
            DateTime? maturityDate   = null;
            double?   remainingScale = null;
            double?   stockPB        = null;

            if (detailByCode.TryGetValue(normCode, out var det))
            {
                // 以东方财富 RPT_BOND_CB_LIST 中的 SECURITY_SHORT_NAME 为准覆盖转债名称，
                // 避免 push2 偶尔将正股名称填入该字段的问题
                var detName = det.GetValueOrDefault("SECURITY_SHORT_NAME")
                           ?? det.GetValueOrDefault("BOND_SHORT_NAME");
                if (!string.IsNullOrWhiteSpace(detName))
                    bondName = detName;

                creditRating = det.GetValueOrDefault("CREDIT_RATING");

                // 到期时间（尝试两个可能的字段名）
                var matStr = det.GetValueOrDefault("MATURITY_DATE")
                          ?? det.GetValueOrDefault("EXPIRE_DATE");
                if (!string.IsNullOrEmpty(matStr) &&
                    DateTime.TryParse(matStr, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var mat))
                    maturityDate = mat;

                // 剩余规模（CURR_ISS_AMT，单位：亿元）
                var scaleStr = det.GetValueOrDefault("CURR_ISS_AMT");
                if (!string.IsNullOrEmpty(scaleStr) &&
                    double.TryParse(scaleStr, NumberStyles.Any,
                        CultureInfo.InvariantCulture, out var scale))
                    remainingScale = scale;

                // 正股市净率 P/B（PBV_RATIO）
                var pbStr = det.GetValueOrDefault("PBV_RATIO");
                if (!string.IsNullOrEmpty(pbStr) &&
                    double.TryParse(pbStr, NumberStyles.Any,
                        CultureInfo.InvariantCulture, out var pb))
                    stockPB = pb;
            }

            // 已退市或规模为零的转债直接跳过；规模为 null 时保留（避免因数据缺失误删）
            if (remainingScale.HasValue && remainingScale.Value <= 0) continue;

            // ── 4. 合并集思录强赎及回售数据 ────────────────────────────────────
            int?    redeemTriggerDays = null;
            string? redeemStatus      = null;
            int?    putTriggerDays    = null;

            if (redeemData != null && redeemData.TryGetValue(normCode, out var rd))
            {
                redeemTriggerDays = rd.redeemDays;
                redeemStatus      = rd.status;
                putTriggerDays    = rd.putDays;

                // 若发行人已公告放弃强赎，将天数归零
                // （监管要求放弃后不得再次行使，此次强赎倒计时重置）
                if (!string.IsNullOrEmpty(redeemStatus) &&
                    Regex.IsMatch(redeemStatus, @"不赎|放弃|waiv", RegexOptions.IgnoreCase))
                    redeemTriggerDays = 0;
            }

            var remainingYears = CalcRemainingYears(maturityDate);

            result.Add(new BondInfo
            {
                BondCode           = normCode,
                BondName           = bondName,
                Price              = price,
                ChangePercent      = changePercent,
                StockName          = stockName,
                StockPrice         = stockPrice,
                StockChange        = stockChange,
                StockPB            = stockPB,
                ConversionPrice    = convPrice,
                ConversionValue    = convValue,
                PremiumRate        = premiumRate,
                CreditRating       = creditRating,
                PutTriggerPrice    = putTrigger,
                PutTriggerDays     = putTriggerDays, // 来自集思录；集思录未提供时为 null
                RedeemTriggerPrice = redeemTrigger,
                RedeemTriggerDays  = redeemTriggerDays,
                RedeemStatus       = redeemStatus,
                BondRatio          = GetDouble(row, "转债占比"),
                MaturityDate       = maturityDate?.ToString("yyyy-MM-dd"),
                RemainingYears     = remainingYears,
                RemainingScale     = remainingScale,
            });
        }

        // 按转债代码升序排列后，重新生成序号
        result = result.OrderBy(b => b.BondCode).ToList();
        for (int i = 0; i < result.Count; i++)
            result[i].Index = i + 1;

        Debug.WriteLine($"[BondDataService] 合并完成：{result.Count} 只转债");
        return result;
    }

    public void Dispose()
    {
        _aktools.Dispose();
        _push2.Dispose();
        _detail.Dispose();
        _jisilu.Dispose();
    }
}
