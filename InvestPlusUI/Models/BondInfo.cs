using System.Text.Json.Serialization;

namespace InvestPlusUI.Models;

/// <summary>
/// 单只可转债的行情与指标数据，与 FastAPI /api/bonds 返回的 JSON 字段一一对应。
/// </summary>
public class BondInfo
{
    [JsonPropertyName("序号")]
    public int? Index { get; set; }

    [JsonPropertyName("转债代码")]
    public string? BondCode { get; set; }

    [JsonPropertyName("转债名称")]
    public string? BondName { get; set; }

    [JsonPropertyName("现价")]
    public double? Price { get; set; }

    [JsonPropertyName("涨跌幅")]
    public double? ChangePercent { get; set; }

    [JsonPropertyName("正股名称")]
    public string? StockName { get; set; }

    [JsonPropertyName("正股价")]
    public double? StockPrice { get; set; }

    [JsonPropertyName("正股涨跌")]
    public double? StockChange { get; set; }

    [JsonPropertyName("正股PB")]
    public double? StockPB { get; set; }

    [JsonPropertyName("转股价")]
    public double? ConversionPrice { get; set; }

    [JsonPropertyName("转股价值")]
    public double? ConversionValue { get; set; }

    [JsonPropertyName("转股溢价率")]
    public double? PremiumRate { get; set; }

    [JsonPropertyName("债券评级")]
    public string? CreditRating { get; set; }

    [JsonPropertyName("回售触发价")]
    public double? PutTriggerPrice { get; set; }

    [JsonPropertyName("回售触发天数")]
    public int? PutTriggerDays { get; set; }

    [JsonPropertyName("强赎触发价")]
    public double? RedeemTriggerPrice { get; set; }

    [JsonPropertyName("强赎触发天数")]
    public int? RedeemTriggerDays { get; set; }

    [JsonPropertyName("强赎状态")]
    public string? RedeemStatus { get; set; }

    [JsonPropertyName("转债占比")]
    public double? BondRatio { get; set; }

    [JsonPropertyName("到期时间")]
    public string? MaturityDate { get; set; }

    [JsonPropertyName("剩余年限")]
    public double? RemainingYears { get; set; }

    [JsonPropertyName("剩余规模")]
    public double? RemainingScale { get; set; }
}

/// <summary>
/// FastAPI /api/bonds 端点的完整响应结构。
/// </summary>
public class BondsResponse
{
    [JsonPropertyName("bonds")]
    public List<BondInfo>? Bonds { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("last_update")]
    public string? LastUpdate { get; set; }
}
