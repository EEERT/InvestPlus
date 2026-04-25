using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using InvestPlusUI.Models;

namespace InvestPlusUI.Services;

/// <summary>
/// 协调从 AKTools 和东方财富获取可转债数据，完成多源合并与衍生字段计算。
///
/// 数据来源：
/// 1. AKTools bond_cov_comparison  — 东方财富实时行情（当前交易中的转债）
/// 2. AKTools bond_cb_redeem_jsl   — 集思录强赎倒计时
/// 3. 东方财富 RPT_BOND_CB_LIST    — 债券评级、到期时间、剩余规模、正股PB（直接 HTTP 调用）
///
/// 合并逻辑参考原 Python utils/calculations.py merge_bond_data 函数。
/// </summary>
public sealed class BondDataService : IDisposable
{
    private readonly AkToolsService _aktools;
    private readonly EastmoneyService _eastmoney = new();

    public BondDataService(AkToolsService aktools) => _aktools = aktools;

    // ── 公共入口 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 并行获取三路数据并合并，返回可转债列表与更新时间。
    /// </summary>
    public async Task<(List<BondInfo> bonds, string lastUpdate)> GetBondsAsync(
        CancellationToken ct = default)
    {
        // 并行发起三路请求
        var compTask    = _aktools.FetchAsync("bond_cov_comparison", ct: ct);
        var redeemTask  = _aktools.FetchAsync("bond_cb_redeem_jsl",  ct: ct);
        var detailTask  = _eastmoney.FetchBondDetailsAsync(ct);

        await Task.WhenAll(compTask, redeemTask, detailTask);

        var comparison = await compTask;
        var redeem     = await redeemTask;
        var detail     = await detailTask;

        var bonds = MergeData(comparison, redeem, detail);
        return (bonds, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    // ── JSON 字段提取辅助方法 ─────────────────────────────────────────────────

    /// <summary>按候选列名顺序查找并返回 double 值；找不到则返回 null。</summary>
    private static double? GetDouble(
        Dictionary<string, JsonElement> row, params string[] names)
    {
        foreach (var name in names)
        {
            if (!row.TryGetValue(name, out var el)) continue;
            if (el.ValueKind == JsonValueKind.Number)
                return el.GetDouble();
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s) && s != "-" && s != "--" &&
                    double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                    return d;
            }
        }
        return null;
    }

    /// <summary>按候选列名顺序查找并返回字符串值；空/"-"视为 null。</summary>
    private static string? GetString(
        Dictionary<string, JsonElement> row, params string[] names)
    {
        foreach (var name in names)
        {
            if (!row.TryGetValue(name, out var el)) continue;
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                return string.IsNullOrWhiteSpace(s) || s == "-" || s == "--" ? null : s;
            }
            if (el.ValueKind == JsonValueKind.Number)
                return el.GetRawText();
        }
        return null;
    }

    // ── 代码规范化 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 去除 SH/SZ/BJ 前缀、去除浮点尾缀（如 127018.0），标准化为 6 位字符串。
    /// </summary>
    private static string NormalizeCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var s = Regex.Replace(raw.Trim(), @"^(SH|SZ|BJ)", "", RegexOptions.IgnoreCase);
        // 去除 "110044.0" 这类浮点后缀
        s = Regex.Replace(s, @"\.0+$", "");
        // 若为纯数字（可能来自 akshare 的 float→str 转换），补零至 6 位
        if (long.TryParse(s, out var num))
            return num.ToString().PadLeft(6, '0');
        return s;
    }

    // ── 衍生字段计算 ──────────────────────────────────────────────────────────

    private static double? CalcConversionValue(double? stockPrice, double? convPrice)
    {
        if (stockPrice.HasValue && convPrice.HasValue && convPrice.Value > 0)
            return (stockPrice.Value / convPrice.Value) * 100.0;
        return null;
    }

    private static double? CalcPremiumRate(double? bondPrice, double? convValue)
    {
        if (bondPrice.HasValue && convValue.HasValue && convValue.Value > 0)
            return (bondPrice.Value / convValue.Value - 1.0) * 100.0;
        return null;
    }

    private static double? CalcRemainingYears(DateTime? maturity)
    {
        if (!maturity.HasValue) return null;
        var days = (maturity.Value.Date - DateTime.Today).TotalDays;
        return days > 0 ? Math.Round(days / 365.25, 4) : 0.0;
    }

    // ── 核心合并逻辑 ──────────────────────────────────────────────────────────

    private List<BondInfo> MergeData(
        List<Dictionary<string, JsonElement>>? comparison,
        List<Dictionary<string, JsonElement>>? redeem,
        List<Dictionary<string, string?>>? detail)
    {
        if (comparison == null || comparison.Count == 0)
            return new List<BondInfo>();

        // ── 1. 构建东方财富详情查找表（转债代码 → 详情行） ────────────────────
        var detailByCode = new Dictionary<string, Dictionary<string, string?>>(
            StringComparer.OrdinalIgnoreCase);
        if (detail != null)
        {
            foreach (var row in detail)
            {
                var code = row.GetValueOrDefault("SECURITY_CODE");
                if (string.IsNullOrEmpty(code)) continue;
                var norm = NormalizeCode(code);
                if (!string.IsNullOrEmpty(norm))
                    detailByCode[norm] = row;
            }
        }

        // ── 2. 构建集思录强赎查找表（转债代码 → 天数 + 状态） ────────────────
        var redeemByCode = new Dictionary<string, (int? days, string? status)>(
            StringComparer.OrdinalIgnoreCase);
        if (redeem != null)
        {
            foreach (var row in redeem)
            {
                var code = GetString(row, "代码", "转债代码", "bond_code");
                if (string.IsNullOrEmpty(code)) continue;
                var norm = NormalizeCode(code);
                if (string.IsNullOrEmpty(norm)) continue;

                // 天数字段：多种可能的列名
                var daysDouble = GetDouble(row,
                    "强赎天计数", "已满足天数", "满足天数", "强赎天数", "redeem_days", "days");
                int? days = daysDouble.HasValue ? (int)daysDouble.Value : null;

                // 若天数来自字符串（如 "15天"），提取数字部分
                if (!days.HasValue)
                {
                    var dStr = GetString(row,
                        "强赎天计数", "已满足天数", "满足天数", "强赎天数", "redeem_days", "days");
                    if (dStr != null)
                    {
                        var m = Regex.Match(dStr, @"\d+");
                        if (m.Success && int.TryParse(m.Value, out var di))
                            days = di;
                    }
                }

                var status = GetString(row, "强赎状态", "redeem_status", "状态");
                redeemByCode[norm] = (days, status);
            }
        }

        // ── 3. 遍历 comparison 数据，构建 BondInfo 列表 ───────────────────────
        var result = new List<BondInfo>();

        foreach (var row in comparison)
        {
            // 转债代码
            var bondCode = GetString(row, "转债代码", "bond_code", "cb_code", "代码", "债券代码");
            if (string.IsNullOrEmpty(bondCode)) continue;
            var normCode = NormalizeCode(bondCode);
            if (string.IsNullOrEmpty(normCode)) continue;

            // 基本行情字段
            var price         = GetDouble(row, "转债最新价", "转债现价", "债现价", "现价");
            var changePercent = GetDouble(row, "转债涨跌幅", "转债涨跌");
            var bondName      = GetString(row, "转债名称", "bond_name", "cb_name", "名称", "债券简称");
            var stockCode     = GetString(row, "正股代码", "stock_code");
            var stockName     = GetString(row, "正股名称", "stock_name", "正股简称");
            var stockPrice    = GetDouble(row, "正股最新价", "正股现价", "正股价", "stock_price");
            var stockChange   = GetDouble(row, "正股涨跌幅", "正股涨跌", "stock_change");
            var convPrice     = GetDouble(row, "转股价", "conversion_price", "转股价格");
            var convValue     = GetDouble(row, "转股价值", "conversion_value");
            var premiumRate   = GetDouble(row, "转股溢价率", "premium_rate");
            var putTrigger    = GetDouble(row, "回售触发价", "put_trigger", "回售价格");
            var redeemTrigger = GetDouble(row, "强赎触发价", "redeem_trigger", "强赎价格");

            // 若 AKTools 未返回 convValue / premiumRate，则自行计算
            convValue   ??= CalcConversionValue(stockPrice, convPrice);
            premiumRate ??= CalcPremiumRate(price, convValue);

            // ── 4. 合并东方财富详情（评级、到期时间、规模、PB） ───────────────
            string? creditRating   = null;
            DateTime? maturityDate = null;
            double? remainingScale = null;
            double? stockPB        = null;

            if (detailByCode.TryGetValue(normCode, out var det))
            {
                // 转债名称以东方财富 RPT_BOND_CB_LIST 中的 SECURITY_SHORT_NAME 为准
                var detName = det.GetValueOrDefault("SECURITY_SHORT_NAME")
                           ?? det.GetValueOrDefault("BOND_SHORT_NAME");
                if (!string.IsNullOrWhiteSpace(detName))
                    bondName = detName;

                creditRating = det.GetValueOrDefault("CREDIT_RATING");

                var matStr = det.GetValueOrDefault("MATURITY_DATE")
                          ?? det.GetValueOrDefault("EXPIRE_DATE");
                if (!string.IsNullOrEmpty(matStr) &&
                    DateTime.TryParse(matStr, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var mat))
                    maturityDate = mat;

                var scaleStr = det.GetValueOrDefault("CURR_ISS_AMT");
                if (!string.IsNullOrEmpty(scaleStr) &&
                    double.TryParse(scaleStr, NumberStyles.Any,
                        CultureInfo.InvariantCulture, out var scale))
                    remainingScale = scale;

                var pbStr = det.GetValueOrDefault("PBV_RATIO");
                if (!string.IsNullOrEmpty(pbStr) &&
                    double.TryParse(pbStr, NumberStyles.Any,
                        CultureInfo.InvariantCulture, out var pb))
                    stockPB = pb;
            }

            // 过滤已退市（剩余规模为 0）的转债；规模 null 时保留（数据缺失时不误杀）
            if (remainingScale.HasValue && remainingScale.Value <= 0) continue;

            // ── 5. 合并集思录强赎数据 ─────────────────────────────────────────
            int? redeemTriggerDays = null;
            string? redeemStatus   = null;

            if (redeemByCode.TryGetValue(normCode, out var rd))
            {
                redeemTriggerDays = rd.days;
                redeemStatus      = rd.status;

                // 发行人已公告放弃强赎时，将天数归零（监管要求其不得再行使该权利）
                if (!string.IsNullOrEmpty(redeemStatus) &&
                    Regex.IsMatch(redeemStatus, @"不赎|放弃|waiv", RegexOptions.IgnoreCase))
                    redeemTriggerDays = 0;
            }

            var remainingYears = CalcRemainingYears(maturityDate);

            result.Add(new BondInfo
            {
                BondCode          = normCode,
                BondName          = bondName,
                Price             = price,
                ChangePercent     = changePercent,
                StockName         = stockName,
                StockPrice        = stockPrice,
                StockChange       = stockChange,
                StockPB           = stockPB,
                ConversionPrice   = convPrice,
                ConversionValue   = convValue,
                PremiumRate       = premiumRate,
                CreditRating      = creditRating,
                PutTriggerPrice   = putTrigger,
                PutTriggerDays    = 0,   // 回售触发天数需大量股价历史数据，暂不计算
                RedeemTriggerPrice = redeemTrigger,
                RedeemTriggerDays  = redeemTriggerDays,
                RedeemStatus       = redeemStatus,
                BondRatio          = null,
                MaturityDate       = maturityDate?.ToString("yyyy-MM-dd"),
                RemainingYears     = remainingYears,
                RemainingScale     = remainingScale,
            });
        }

        // 按转债代码排序后重新编号
        result = result.OrderBy(b => b.BondCode).ToList();
        for (int i = 0; i < result.Count; i++)
            result[i].Index = i + 1;

        return result;
    }

    public void Dispose() => _eastmoney.Dispose();
}
