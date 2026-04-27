using System.Diagnostics;
using System.Text.Json;

namespace InvestPlusUI.Services;

/// <summary>
/// 集思录可转债强赎数据服务。
///
/// 集思录（jisilu.cn）提供业界公认的可转债强赎倒计时数据：
/// 精确统计"正股价连续达到强赎触发价的天数"（即强赎天计数）。
/// 该指标是判断是否即将触发强制赎回条款的核心依据。
///
/// 本服务直接调用集思录的数据接口，无需通过 AKTools 中转，
/// 减少链路延迟并消除对 AKTools 服务进程的依赖。
///
/// 注意：若集思录 API 返回非 JSON 内容（如登录重定向页面）或网络错误，
/// 本服务返回 null，调用方应做好无强赎数据时的降级展示处理
///（保留行情数据，强赎天数显示为"--"）。
/// </summary>
public sealed class JisiluService : IDisposable
{
    // ── 常量定义 ──────────────────────────────────────────────────────────────

    /// <summary>集思录可转债列表接口地址</summary>
    private const string ApiUrl = "https://www.jisilu.cn/data/cbnew/cb_list/";

    /// <summary>
    /// 每次请求返回的条数。
    /// 集思录支持通过 rp 参数控制每页条数，设为较大值以减少翻页次数。
    /// </summary>
    private const int PageSize = 50;

    private readonly HttpClient _client;

    // ── 构造函数 ──────────────────────────────────────────────────────────────

    public JisiluService()
    {
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // 模拟真实浏览器行为，附带 Referer 和 XHR 标志
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Referer", "https://www.jisilu.cn/data/cbnew/");
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "application/json, text/javascript, */*; q=0.01");
        // X-Requested-With: XMLHttpRequest 表明这是 Ajax 请求
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-Requested-With", "XMLHttpRequest");
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept-Language", "zh-CN,zh;q=0.9");
    }

    // ── 公共方法 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 获取全部可转债的强赎相关数据，以转债代码（6 位，已规范化）为键。
    ///
    /// 返回字典：key = 转债代码，value = (强赎天计数, 强赎状态)
    ///   - 强赎天计数：正股价连续满足强赎条件的天数（满 15 天可触发强赎）
    ///   - 强赎状态：如"Y"（已触发）、"N"（未触发）、"已公告不赎回"等
    ///
    /// 返回值说明：
    ///   - null       表示获取失败（网络错误或需要鉴权）
    ///   - 空字典     表示无数据
    ///   - 非空字典   正常结果
    /// </summary>
    public async Task<Dictionary<string, (int? days, string? status)>?> FetchRedeemDataAsync(
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, (int? days, string? status)>(
            StringComparer.OrdinalIgnoreCase);

        int page       = 1;
        int totalPages = 1; // 初始假设 1 页，取到第一页数据后用实际值覆盖

        while (page <= totalPages)
        {
            var pageResult = await FetchPageAsync(page, ct);
            if (pageResult == null)
            {
                // 第一页即失败 → 整体失败；否则返回已获取的部分
                return page == 1 ? null : result;
            }

            foreach (var (code, days, status) in pageResult.Value.rows)
            {
                if (!string.IsNullOrEmpty(code))
                    result[code] = (days, status);
            }

            totalPages = pageResult.Value.totalPages;
            page++;

            // 还有后续页时，稍作等待
            if (page <= totalPages)
                await Task.Delay(200, ct);
        }

        // 返回结果字典（可能为空字典，空字典与 null 含义不同：
        // 空字典 = 成功请求但无数据；null = 请求失败）
        return result;
    }

    // ── 私有方法 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 获取集思录指定页的可转债数据。
    /// 集思录鉴权失败时通常直接返回 HTML 登录页，本方法检测到后立即返回 null，
    /// 不再重试（重试同样会鉴权失败，没有意义）。
    /// </summary>
    private async Task<(List<(string code, int? days, string? status)> rows, int totalPages)?> FetchPageAsync(
        int page, CancellationToken ct)
    {
        try
        {
            // 附带毫秒级时间戳，避免集思录服务端缓存旧数据
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var url = $"{ApiUrl}?___jsl=LST___t={timestamp}&rp={PageSize}&page={page}&returnType=json";

            var resp = await _client.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);

            // 如果响应内容不以 '{' 或 '[' 开头，则是 HTML（如登录重定向页面）
            var trimmed = json.TrimStart();
            if (!trimmed.StartsWith("{") && !trimmed.StartsWith("["))
            {
                Debug.WriteLine("[JiSiLu] 返回非 JSON 内容（可能需要登录），跳过集思录数据");
                return null;
            }

            using var doc  = JsonDocument.Parse(json);
            var       root = doc.RootElement;

            // 集思录响应格式：{ "rows": [ { "id":"xxx", "cell":{ ... } }, ... ], "total":"xxx" }
            if (!root.TryGetProperty("rows", out var rowsEl) ||
                rowsEl.ValueKind != JsonValueKind.Array)
            {
                Debug.WriteLine("[JiSiLu] 响应中无有效 rows 数组");
                return null;
            }

            // 计算总页数（total 字段为字符串或数字，需兼容两种格式）
            int totalRows = 0;
            if (root.TryGetProperty("total", out var totalEl))
            {
                var totalStr = totalEl.ValueKind == JsonValueKind.String
                    ? totalEl.GetString()
                    : totalEl.GetRawText();
                int.TryParse(totalStr, out totalRows);
            }
            int totalPages = totalRows > 0
                ? (int)Math.Ceiling((double)totalRows / PageSize)
                : 1;

            // 逐行解析数据
            var rows = new List<(string code, int? days, string? status)>();
            foreach (var rowEl in rowsEl.EnumerateArray())
            {
                // 实际数据在 cell 子对象中
                if (!rowEl.TryGetProperty("cell", out var cell)) continue;

                // 转债代码（bond_id 字段，可能是字符串或数字）
                string code = "";
                if (cell.TryGetProperty("bond_id", out var bondIdEl))
                {
                    code = bondIdEl.ValueKind == JsonValueKind.String
                        ? (bondIdEl.GetString() ?? "")
                        : bondIdEl.GetRawText().Trim('"');
                }
                if (string.IsNullOrEmpty(code)) continue;

                // 强赎天计数（redeem_tc：正股价已连续满足强赎触发价的天数）
                int? days = null;
                if (cell.TryGetProperty("redeem_tc", out var redeemTcEl))
                {
                    if (redeemTcEl.ValueKind == JsonValueKind.Number)
                        days = redeemTcEl.GetInt32();
                    else if (redeemTcEl.ValueKind == JsonValueKind.String &&
                             int.TryParse(redeemTcEl.GetString(), out var d))
                        days = d;
                }

                // 强赎状态（优先 redeem_status，fallback 到 force_redeem）
                string? status = null;
                if (cell.TryGetProperty("redeem_status", out var rsEl))
                {
                    var s = rsEl.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) status = s;
                }
                if (status == null && cell.TryGetProperty("force_redeem", out var frEl))
                    status = frEl.GetString();

                rows.Add((code, days, status));
            }

            return (rows, totalPages);
        }
        catch (OperationCanceledException)
        {
            throw; // 用户取消时直接上抛，不重试
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[JiSiLu] 页 {page} 请求失败: {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _client.Dispose();
}
