using System.Diagnostics;
using System.Text.Json;

namespace InvestPlusUI.Services;

/// <summary>
/// 东方财富数据中心债券详情服务（RPT_BOND_CB_LIST 报表）。
///
/// 数据来源：东方财富数据中心 datacenter-web.eastmoney.com
/// 报表名称：RPT_BOND_CB_LIST
///
/// 提供的补充字段（这些字段在 push2 实时接口中未包含）：
///   - SECURITY_SHORT_NAME : 转债简称（以此为准修正 push2 中可能混入的正股名）
///   - CREDIT_RATING       : 债券评级（如 AAA、AA+ 等）
///   - MATURITY_DATE       : 到期时间
///   - CURR_ISS_AMT        : 剩余发行规模（亿元）
///   - PBV_RATIO           : 正股市净率（P/B）
///   - SECURITY_CODE       : 转债代码（用于与 push2 数据关联）
///
/// 该数据变化频率较低（评级调整、到期等为低频事件），
/// 由 BondDataService 在上层做 10 分钟缓存，本类每次都直接请求网络。
/// </summary>
public sealed class EastmoneyService : IDisposable
{
    // ── 常量定义 ──────────────────────────────────────────────────────────────

    /// <summary>东方财富数据中心 API 基础地址</summary>
    private const string ApiUrl = "https://datacenter-web.eastmoney.com/api/data/v1/get";

    /// <summary>单页最大请求条数（设为 500，通常 1~2 页即可取完全量数据）</summary>
    private const int PageSize = 500;

    private readonly HttpClient _client;

    // ── 构造函数 ──────────────────────────────────────────────────────────────

    public EastmoneyService()
    {
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        // 模拟浏览器请求头，避免被数据中心接口拦截
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Referer", "https://data.eastmoney.com/");
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "application/json, text/plain, */*");
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
    }

    // ── 公共方法 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 获取全部在市可转债的补充详情信息（自动翻页合并）。
    ///
    /// 返回值说明：
    ///   - null     表示获取失败
    ///   - 非空列表 正常结果，每条为一只转债的详情字典
    ///              key = 东方财富原始字段名（英文大写），value = 字段值字符串
    /// </summary>
    public async Task<List<Dictionary<string, string?>>?> FetchBondDetailsAsync(
        CancellationToken ct = default)
    {
        var allRows   = new List<Dictionary<string, string?>>();
        int page      = 1;
        int totalPages = 1;

        while (page <= totalPages)
        {
            var result = await FetchPageAsync(page, ct);
            if (result == null) break; // 失败时停止翻页，返回已获取的部分

            allRows.AddRange(result.Value.rows);
            totalPages = result.Value.totalPages;
            page++;

            // 翻页时短暂等待，避免对东方财富服务器请求过于密集
            if (page <= totalPages)
                await Task.Delay(300, ct);
        }

        return allRows.Count > 0 ? allRows : null;
    }

    // ── 私有方法 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 获取指定页的债券详情数据，失败时最多重试 3 次。
    /// </summary>
    private async Task<(List<Dictionary<string, string?>> rows, int totalPages)?> FetchPageAsync(
        int page, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // 构建查询参数（请求 RPT_BOND_CB_LIST 报表全部字段）
                var qs = string.Join("&", new Dictionary<string, string>
                {
                    ["sortColumns"] = "PUBLIC_START_DATE", // 按发行日期倒序排列
                    ["sortTypes"]   = "-1",
                    ["pageSize"]    = PageSize.ToString(),
                    ["pageNumber"]  = page.ToString(),
                    ["reportName"]  = "RPT_BOND_CB_LIST",  // 可转债列表报表
                    ["columns"]     = "ALL",               // 获取所有字段
                    ["source"]      = "WEB",
                    ["client"]      = "WEB",
                }.Select(kv =>
                    $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

                var resp = await _client.GetAsync($"{ApiUrl}?{qs}", ct);
                resp.EnsureSuccessStatusCode();

                using var doc  = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var       root = doc.RootElement;

                // result 节点包含实际数据，缺失则表示接口异常
                if (!root.TryGetProperty("result", out var resultEl) ||
                    resultEl.ValueKind == JsonValueKind.Null)
                {
                    Debug.WriteLine($"[EastmoneyDetail] 页 {page}: 无 result 节点");
                    break;
                }

                // 总页数
                int pages = resultEl.TryGetProperty("pages", out var pagesEl)
                    ? pagesEl.GetInt32() : 1;

                // data 数组：每个元素对应一只转债的详情
                if (!resultEl.TryGetProperty("data", out var dataEl) ||
                    dataEl.ValueKind != JsonValueKind.Array)
                {
                    Debug.WriteLine($"[EastmoneyDetail] 页 {page}: 无 data 数组");
                    break;
                }

                // 将每条记录的所有字段都转换为字符串字典
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
                            JsonValueKind.Null   => null,
                            _                    => prop.Value.GetRawText(),
                        };
                    }
                    rows.Add(row);
                }

                return (rows, pages);
            }
            catch (OperationCanceledException)
            {
                throw; // 取消时直接上抛
            }
            catch (Exception ex) when (attempt < 2)
            {
                Debug.WriteLine(
                    $"[EastmoneyDetail] 页 {page} 第 {attempt + 1} 次失败: {ex.Message}，重试中…");
                await Task.Delay(1500 * (attempt + 1), ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EastmoneyDetail] 页 {page} 最终失败: {ex.Message}");
                break;
            }
        }

        return null;
    }

    public void Dispose() => _client.Dispose();
}
