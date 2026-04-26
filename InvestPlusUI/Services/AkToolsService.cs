using System.Text.Json;

namespace InvestPlusUI.Services;

/// <summary>
/// AKTools HTTP API 客户端。
///
/// AKTools 是 AKShare 的 HTTP 版本，通过本地 HTTP 服务暴露 AKShare 所有数据接口。
/// 默认运行于 http://localhost:8080，可通过 <c>python -m aktools</c> 启动。
///
/// 端点格式：GET http://{host}:{port}/api/public/{function_name}?param=value
/// 响应格式：JSON 数组，每个元素为一条记录（pandas DataFrame 的 records 序列化）。
/// </summary>
public sealed class AkToolsService : IDisposable
{
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public AkToolsService(string baseUrl)
    {
        _client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(120),
        };
        _client.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    /// <summary>
    /// 调用指定的 AKShare 函数，返回 JSON 记录列表。
    /// 每条记录是一个列名到值的字典，值类型通过 <see cref="JsonElement"/> 灵活处理。
    /// </summary>
    /// <param name="functionName">AKShare 函数名，如 <c>bond_cov_comparison</c>。</param>
    /// <param name="parameters">传入函数的参数（可选）。</param>
    public async Task<List<Dictionary<string, JsonElement>>?> FetchAsync(
        string functionName,
        Dictionary<string, string>? parameters = null,
        CancellationToken ct = default)
    {
        var url = $"api/public/{functionName}";
        if (parameters?.Count > 0)
        {
            var qs = string.Join("&", parameters.Select(p =>
                $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
            url += "?" + qs;
        }

        var response = await _client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);

        // AKTools 直接返回 JSON 数组（pandas DataFrame.to_json(orient="records")）
        return JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json, _jsonOpts);
    }

    /// <summary>
    /// 检查 AKTools 服务是否可用（访问根路径，5 秒超时）。
    /// </summary>
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var resp = await _client.GetAsync("version", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _client.Dispose();
}
