using System.Diagnostics;
using System.Text.Json;

namespace InvestPlusUI.Services;

/// <summary>
/// 东方财富实时行情服务（Push2 接口版本）。
///
/// 直接调用东方财富证券 push2.eastmoney.com 的 clist 接口，
/// 获取可转债市场（MK0354）的实时行情数据，无需依赖 AKTools。
///
/// 返回数据包含：转债代码/名称、现价、涨跌幅、正股行情、
/// 转股价/转股价值/转股溢价率、强赎/回售触发价等字段。
///
/// 接口特点：
///   - 响应速度快（毫秒级），适合高频定时刷新
///   - fltt=2 模式下数值直接为浮点小数，无需额外换算
///   - 数据在交易时段内实时更新
/// </summary>
public sealed class EastmoneyPush2Service : IDisposable
{
    // ── 常量定义 ──────────────────────────────────────────────────────────────

    /// <summary>东方财富 push2 行情接口地址</summary>
    private const string ApiUrl = "https://push2.eastmoney.com/api/qt/clist/get";

    /// <summary>单页最大返回条数（接口支持上限 500 条）</summary>
    private const int PageSize = 500;

    /// <summary>
    /// 东方财富请求令牌，用于接口鉴权（固定值，从网页抓包获得）。
    /// 若该值失效，可从浏览器开发者工具中的请求 URL 里更新。
    /// </summary>
    private const string UtToken = "bd1d9ddb04089700cf9c27f6f7426281";

    /// <summary>
    /// 东方财富返回"无有效数据"时使用的特殊占位值（与 BondDataService.EastmoneyInvalidValue 保持一致）
    /// </summary>
    private const double InvalidValue = BondDataService.EastmoneyInvalidValue;

    private readonly HttpClient _client;

    /// <summary>
    /// 东方财富内部字段编码（f-code）到业务字段名的映射表。
    /// 东方财富行情接口用 f12、f14 等简短编码表示各列，
    /// 此处将其翻译为对应的中文业务名称，便于后续数据处理和阅读。
    /// </summary>
    private static readonly Dictionary<string, string> FieldMap = new()
    {
        ["f12"]  = "转债代码",   // 6 位转债代码（纯数字，如 127018）
        ["f13"]  = "市场代码",   // 市场标识：0 = 深圳，1 = 上海
        ["f14"]  = "转债名称",   // 转债简称（如"海澜转债"）
        ["f2"]   = "现价",       // 转债最新成交价格（元）
        ["f3"]   = "涨跌幅",     // 转债当日涨跌幅（%，正数为上涨）
        ["f18"]  = "昨收",       // 昨日收盘价（元）
        ["f111"] = "正股代码",   // 对应正股的 6 位代码
        ["f164"] = "正股名称",   // 正股简称（如"海澜之家"）
        ["f238"] = "正股价",     // 正股最新成交价格（元）
        ["f239"] = "正股涨跌",   // 正股当日涨跌幅（%）
        ["f161"] = "转股价",     // 当前有效转股价格（元）
        ["f240"] = "转股价值",   // 以正股价计算的理论转股价值（= 正股价 / 转股价 × 100）
        ["f162"] = "转股溢价率", // 转债溢价率（% = (现价 / 转股价值 - 1) × 100）
        ["f163"] = "转债占比",   // 转债市值占正股流通市值比例（%）
        ["f237"] = "强赎触发价", // 触发强制赎回条款所需的正股价格阈值（元）
        ["f243"] = "回售触发价", // 触发回售条款的正股价格阈值（元）
    };

    // ── 构造函数 ──────────────────────────────────────────────────────────────

    public EastmoneyPush2Service()
    {
        // 初始化 HttpClient，设置超时与浏览器模拟请求头，避免被拦截
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Referer", "https://data.eastmoney.com/cjzx/");
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "application/json, text/plain, */*");
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept-Language", "zh-CN,zh;q=0.9");
    }

    // ── 公共方法 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 获取当前全部在市可转债的实时行情数据。
    /// 自动处理翻页（可转债约 500 只左右，通常 1 次请求即可取完）。
    ///
    /// 返回值说明：
    ///   - null       表示请求完全失败（网络错误或 API 异常）
    ///   - 空列表     表示无数据（非交易时段可能出现）
    ///   - 非空列表   正常结果，每条为一只转债的行情字典
    ///                字典 key = 中文字段名，value = 字段值（字符串形式）
    /// </summary>
    public async Task<List<Dictionary<string, string?>>?> FetchBondListAsync(
        CancellationToken ct = default)
    {
        var allRows = new List<Dictionary<string, string?>>();
        int page  = 1;
        int total = int.MaxValue; // 先设大数，取到第一页后用实际值覆盖

        while ((page - 1) * PageSize < total)
        {
            var pageResult = await FetchPageAsync(page, ct);
            if (pageResult == null)
            {
                // 第一页就失败 → 整体失败；否则返回已取到的部分数据
                return page == 1 ? null : allRows;
            }

            allRows.AddRange(pageResult.Value.rows);
            total = pageResult.Value.total;
            page++;

            // 还有后续页时，短暂等待，避免对服务器请求过于密集
            if ((page - 1) * PageSize < total)
                await Task.Delay(150, ct);
        }

        // 返回已收集的行列表（可能为空列表，空列表与 null 含义不同：
        // 空列表 = 成功请求但无数据（非交易时段）；null = 请求完全失败）
        return allRows;
    }

    // ── 私有方法 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 获取指定页码的行情数据，失败时自动重试最多 3 次。
    /// </summary>
    private async Task<(List<Dictionary<string, string?>> rows, int total)?> FetchPageAsync(
        int page, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var url  = BuildUrl(page);
                var resp = await _client.GetAsync(url, ct);
                resp.EnsureSuccessStatusCode();

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc  = JsonDocument.Parse(json);
                var       root = doc.RootElement;

                // 检查 API 返回码（rc = 0 表示成功）
                if (root.TryGetProperty("rc", out var rcEl) && rcEl.GetInt32() != 0)
                {
                    Debug.WriteLine($"[Push2] 页 {page}: API 错误码 rc={rcEl.GetInt32()}");
                    return null;
                }

                // data 节点缺失通常表示非交易时段或该时段无数据
                if (!root.TryGetProperty("data", out var dataEl) ||
                    dataEl.ValueKind == JsonValueKind.Null)
                {
                    Debug.WriteLine($"[Push2] 页 {page}: 无 data 节点（可能为非交易时段）");
                    return null;
                }

                // total 字段：本次查询的总条数，用于翻页计算
                int total = dataEl.TryGetProperty("total", out var totalEl)
                    ? totalEl.GetInt32() : 0;

                // diff 数组：每个元素对应一只转债的行情
                if (!dataEl.TryGetProperty("diff", out var diffEl) ||
                    diffEl.ValueKind != JsonValueKind.Array)
                {
                    Debug.WriteLine($"[Push2] 页 {page}: diff 数组缺失或为空");
                    return (new List<Dictionary<string, string?>>(), total);
                }

                // 逐条解析，将 JSON 元素转换为字符串字典
                var rows = new List<Dictionary<string, string?>>();
                foreach (var item in diffEl.EnumerateArray())
                    rows.Add(ParseItem(item));

                return (rows, total);
            }
            catch (OperationCanceledException)
            {
                throw; // 用户取消时直接上抛，不重试
            }
            catch (Exception ex) when (attempt < 2)
            {
                Debug.WriteLine(
                    $"[Push2] 页 {page} 第 {attempt + 1} 次失败: {ex.Message}，正在重试…");
                await Task.Delay(TimeSpan.FromSeconds(1.5 * (attempt + 1)), ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Push2] 页 {page} 最终失败: {ex.Message}");
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// 将单条 JSON 对象解析为业务字段字典。
    /// 通过 FieldMap 将东方财富内部编码映射为中文字段名；
    /// 遇到无效占位值（-9999.99 等）时将对应字段置为 null。
    /// </summary>
    private static Dictionary<string, string?> ParseItem(JsonElement item)
    {
        var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in item.EnumerateObject())
        {
            // 优先使用映射后的中文名，否则保留原始 f-code 作为 key
            var key = FieldMap.TryGetValue(prop.Name, out var mapped) ? mapped : prop.Name;

            string? val = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.GetRawText(),
                JsonValueKind.Null   => null,
                _                    => prop.Value.GetRawText(),
            };

            // 过滤东方财富用于表示"无数据"的特殊占位值
            if (val != null &&
                double.TryParse(val,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var dbl) &&
                Math.Abs(dbl - InvalidValue) < 0.01)
            {
                val = null;
            }

            row[key] = val;
        }

        return row;
    }

    /// <summary>
    /// 构建带查询参数的完整请求 URL。
    /// 使用 fltt=2 以浮点格式返回数值。
    ///
    /// fs 参数说明：
    ///   使用"市场代码+证券类型"组合筛选，确保覆盖沪深两市的全部可转债和可交换债：
    ///     m:0+t:115  — 深交所可转债
    ///     m:0+t:116  — 深交所可交换债
    ///     m:1+t:185  — 上交所可转债
    ///     m:1+t:186  — 上交所可交换债
    ///   原先使用的 b:MK0354 实际上只匹配约 10~20 只可交换债，
    ///   并不包含全部 400+ 只可转债，因此改为类型码组合方式以保证数据完整性。
    /// </summary>
    private static string BuildUrl(int page)
    {
        // 请求的字段编码列表，与 FieldMap 的 key 对应
        const string fields =
            "f12,f13,f14,f2,f3,f18,f111,f161,f162,f163,f164,f237,f238,f239,f240,f243";

        var ps = new Dictionary<string, string>
        {
            ["pn"]     = page.ToString(),      // 页码（从 1 开始）
            ["pz"]     = PageSize.ToString(),  // 每页条数
            ["po"]     = "1",                  // 排序方向（1 = 降序）
            ["np"]     = "1",
            ["ut"]     = UtToken,              // 请求令牌
            ["fltt"]   = "2",                  // 数值格式（2 = 带小数点浮点数，无需手动除以100）
            ["invt"]   = "2",
            ["fid"]    = "f243",               // 默认排序字段（回售触发价）
            // 沪深两市可转债 + 可交换债（与 akshare bond_zh_hs_cov_spot 保持一致）
            ["fs"]     = "m:0+t:115,m:0+t:116,m:1+t:185,m:1+t:186",
            ["fields"] = fields,
        };

        var qs = string.Join("&", ps.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{ApiUrl}?{qs}";
    }

    public void Dispose() => _client.Dispose();
}
