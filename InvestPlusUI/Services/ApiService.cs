using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using InvestPlusUI.Models;

namespace InvestPlusUI.Services;

/// <summary>
/// 封装对 InvestPlus FastAPI 后端的 HTTP 调用。
/// </summary>
public class ApiService : IDisposable
{
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ApiService(string baseUrl)
    {
        _client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(180),
        };
        _client.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    /// <summary>
    /// 获取所有正在交易的可转债列表。
    /// </summary>
    public async Task<BondsResponse?> GetBondsAsync(CancellationToken ct = default)
    {
        var response = await _client.GetAsync("/api/bonds", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<BondsResponse>(json, _jsonOptions);
    }

    /// <summary>
    /// 健康检查，用于验证后端是否已启动。
    /// </summary>
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetAsync("/api/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _client.Dispose();
}
