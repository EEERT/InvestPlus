using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace InvestPlusUI.Services;

/// <summary>
/// AKTools API 服务：
/// - bond_cov_comparison：可转债基础数据
/// - stock_zh_a_spot_em / stock_zh_a_spot：正股实时数据
/// - bond_cb_redeem_jsl：强赎/回售天计数
/// </summary>
public sealed class AktoolsService : IDisposable
{
    private readonly HttpClient _client;
    private readonly string _baseUrl;

    public AktoolsService()
    {
        _baseUrl = (Environment.GetEnvironmentVariable("AKTOOLS_BASE_URL")
                    ?? "http://127.0.0.1:8080/api/public").TrimEnd('/');
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "InvestPlusUI/1.0 (+AKTools)");
    }

    public async Task<List<Dictionary<string, string?>>?> FetchBondComparisonAsync(
        CancellationToken ct = default)
    {
        return await FetchTableAsync("bond_cov_comparison", ct);
    }

    public async Task<Dictionary<string, (string? name, double? price, double? change)>?> FetchStockSpotAsync(
        CancellationToken ct = default)
    {
        var rows = await FetchTableAsync("stock_zh_a_spot_em", ct)
                   ?? await FetchTableAsync("stock_zh_a_spot", ct);
        if (rows == null) return null;

        var result = new Dictionary<string, (string? name, double? price, double? change)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var code = NormalizeCode(GetString(row, "代码", "股票代码", "symbol", "stock_code"));
            if (string.IsNullOrEmpty(code)) continue;

            var name = GetString(row, "名称", "股票名称", "name", "stock_name");
            var price = GetDouble(row, "最新价", "现价", "收盘价", "price", "last_price", "close");
            var change = GetDouble(row, "涨跌幅", "changepercent", "pct_chg", "change");
            result[code] = (name, price, change);
        }

        return result;
    }

    public async Task<Dictionary<string, (int? redeemDays, string? status, int? putDays)>?> FetchRedeemDataAsync(
        CancellationToken ct = default)
    {
        var rows = await FetchTableAsync("bond_cb_redeem_jsl", ct);
        if (rows == null) return null;

        var result = new Dictionary<string, (int? redeemDays, string? status, int? putDays)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var code = NormalizeCode(GetString(row, "转债代码", "代码", "bond_id", "bond_code", "symbol"));
            if (string.IsNullOrEmpty(code)) continue;

            var redeemDays = GetInt(row, "强赎天数", "redeem_tc", "redeem_days");
            var status = GetString(row, "强赎状态", "redeem_status", "force_redeem", "status");
            var putDays = GetInt(row, "回售天数", "put_tc", "put_count", "put_cnt", "putback_tc");
            result[code] = (redeemDays, status, putDays);
        }

        return result;
    }

    private async Task<List<Dictionary<string, string?>>?> FetchTableAsync(
        string endpoint, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var resp = await _client.GetAsync($"{_baseUrl}/{endpoint}", ct);
                resp.EnsureSuccessStatusCode();
                var text = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(text);
                var rows = ExtractRows(doc.RootElement);
                if (rows != null) return rows;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 2)
            {
                Debug.WriteLine($"[AKTools] {endpoint} 请求失败，第 {attempt + 1} 次重试：{ex.Message}");
                await Task.Delay(1200 * (attempt + 1), ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AKTools] {endpoint} 最终失败：{ex.Message}");
            }
        }
        return null;
    }

    private static List<Dictionary<string, string?>>? ExtractRows(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return ParseArray(root);

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (root.TryGetProperty("data", out var dataEl))
        {
            if (dataEl.ValueKind == JsonValueKind.Array) return ParseArray(dataEl);
            if (dataEl.ValueKind == JsonValueKind.Object &&
                dataEl.TryGetProperty("rows", out var rowsEl) &&
                rowsEl.ValueKind == JsonValueKind.Array) return ParseArray(rowsEl);
        }

        if (root.TryGetProperty("result", out var resultEl))
        {
            if (resultEl.ValueKind == JsonValueKind.Array) return ParseArray(resultEl);
            if (resultEl.ValueKind == JsonValueKind.Object &&
                resultEl.TryGetProperty("data", out var rowsEl) &&
                rowsEl.ValueKind == JsonValueKind.Array) return ParseArray(rowsEl);
        }

        if (root.TryGetProperty("rows", out var rows2El) &&
            rows2El.ValueKind == JsonValueKind.Array) return ParseArray(rows2El);

        return null;
    }

    private static List<Dictionary<string, string?>> ParseArray(JsonElement arr)
    {
        var rows = new List<Dictionary<string, string?>>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in item.EnumerateObject())
            {
                row[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => null,
                    _ => p.Value.GetRawText(),
                };
            }
            rows.Add(row);
        }
        return rows;
    }

    private static string? GetString(Dictionary<string, string?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!row.TryGetValue(key, out var v)) continue;
            if (string.IsNullOrWhiteSpace(v) || v is "-" or "--") continue;
            return v.Trim();
        }
        return null;
    }

    private static double? GetDouble(Dictionary<string, string?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!row.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v)) continue;
            if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                return d;
        }
        return null;
    }

    private static int? GetInt(Dictionary<string, string?> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!row.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v)) continue;
            if (int.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
                return n;
        }
        return null;
    }

    private static string NormalizeCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return string.Empty;
        return digits.Length >= 6 ? digits[^6..] : digits.PadLeft(6, '0');
    }

    public void Dispose() => _client.Dispose();
}
