using System.Globalization;
using System.Text.Json;

namespace InvestPlusUI.Services;

/// <summary>
/// 直接调用东方财富数据中心 REST API 获取可转债补充信息。
///
/// 数据来源：东方财富 RPT_BOND_CB_LIST
/// 提供字段：债券评级、到期时间、剩余规模、正股PB 等
/// （这些字段在 AKShare bond_cov_comparison 中不提供）
/// </summary>
public sealed class EastmoneyService : IDisposable
{
    private const string ApiUrl = "https://datacenter-web.eastmoney.com/api/data/v1/get";

    private readonly HttpClient _client;

    public EastmoneyService()
    {
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://data.eastmoney.com/");
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
    }

    /// <summary>
    /// 获取全部可转债的补充信息（支持翻页，自动合并）。
    /// 返回 null 表示获取失败；返回空列表表示无数据。
    /// </summary>
    public async Task<List<Dictionary<string, string?>>?> FetchBondDetailsAsync(
        CancellationToken ct = default)
    {
        var allRows = new List<Dictionary<string, string?>>();
        int page = 1;
        int totalPages = 1;

        while (page <= totalPages)
        {
            var result = await FetchPageAsync(page, ct);
            if (result == null) break;

            allRows.AddRange(result.Value.rows);
            totalPages = result.Value.totalPages;
            page++;

            if (page <= totalPages)
                await Task.Delay(300, ct);
        }

        return allRows.Count > 0 ? allRows : null;
    }

    private async Task<(List<Dictionary<string, string?>> rows, int totalPages)?> FetchPageAsync(
        int page, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var qs = string.Join("&", new Dictionary<string, string>
                {
                    ["sortColumns"] = "PUBLIC_START_DATE",
                    ["sortTypes"] = "-1",
                    ["pageSize"] = "500",
                    ["pageNumber"] = page.ToString(),
                    ["reportName"] = "RPT_BOND_CB_LIST",
                    ["columns"] = "ALL",
                    ["source"] = "WEB",
                    ["client"] = "WEB",
                }.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

                var resp = await _client.GetAsync($"{ApiUrl}?{qs}", ct);
                resp.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;

                if (!root.TryGetProperty("result", out var resultEl) ||
                    resultEl.ValueKind == JsonValueKind.Null)
                {
                    System.Diagnostics.Debug.WriteLine($"EastmoneyService page {page}: no result element");
                    break;
                }

                int pages = resultEl.TryGetProperty("pages", out var pagesEl)
                    ? pagesEl.GetInt32()
                    : 1;

                if (!resultEl.TryGetProperty("data", out var dataEl) ||
                    dataEl.ValueKind != JsonValueKind.Array)
                {
                    System.Diagnostics.Debug.WriteLine($"EastmoneyService page {page}: no data array");
                    break;
                }

                var rows = new List<Dictionary<string, string?>>();
                foreach (var item in dataEl.EnumerateArray())
                {
                    var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in item.EnumerateObject())
                    {
                        row[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.Number => prop.Value.GetRawText(),
                            JsonValueKind.Null => null,
                            _ => prop.Value.GetRawText(),
                        };
                    }
                    rows.Add(row);
                }

                return (rows, pages);
            }
            catch (Exception ex) when (attempt < 2)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"EastmoneyService page {page} attempt {attempt + 1} failed: {ex.Message}");
                await Task.Delay(1500 * (attempt + 1), ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EastmoneyService page {page} fatal: {ex.Message}");
                break;
            }
        }

        return null;
    }

    public void Dispose() => _client.Dispose();
}
