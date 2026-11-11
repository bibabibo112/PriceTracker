using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace PriceTracker;

public record CollectedPrice(string ExternalId, string Name, string Url, decimal Price, decimal? OriginalPrice);

public interface IPriceCollector
{
    string SourceName { get; }   // phải trùng Source.Name trong DB
    Task<CollectedPrice?> CollectAsync(string externalId, string url, CancellationToken ct = default);
}

public class TikiCollector(HttpClient http) : IPriceCollector
{
    public string SourceName => "Tiki";

    public async Task<CollectedPrice?> CollectAsync(string externalId, string url, CancellationToken ct = default)
    {
        // externalId = id sản phẩm Tiki; spid (người bán/biến thể) lấy từ tikiUrl (?spid=...)
        var spid = Regex.Match(url ?? "", @"spid=(\d+)").Groups[1].Value;
        var api = $"https://tiki.vn/api/v2/products/{externalId}" + (spid.Length > 0 ? $"?spid={spid}" : "");

        // TODO: đối chiếu URL và tên trường với JSON bạn đã test bằng Postman
        using var res = await http.GetAsync(api, ct);
        res.EnsureSuccessStatusCode();   // lỗi 403/404... sẽ hiện trong log kèm mã trạng thái

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var r = doc.RootElement;

        return new CollectedPrice(
            externalId,
            r.GetProperty("name").GetString() ?? "",
            r.TryGetProperty("short_url", out var u) ? u.GetString() ?? "" : "",
            r.GetProperty("price").GetDecimal(),
            r.TryGetProperty("list_price", out var lp) ? lp.GetDecimal() : null);
    }
}

public partial class LazadaCollector(HttpClient http) : IPriceCollector
{
    public string SourceName => "Lazada";

    public async Task<CollectedPrice?> CollectAsync(string externalId, string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        using var res = await http.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();   // lỗi 403/404... sẽ hiện trong log kèm mã trạng thái
        var html = await res.Content.ReadAsStringAsync(ct);

        // Trang sản phẩm có sẵn giá trong dữ liệu theo dõi, dạng: "pdt_price":"6.799.000 ₫"
        // (khung giá hiển thị trên trang thì rỗng, được nạp sau bằng JavaScript)
        var m = PriceRegex().Match(html);
        if (!m.Success)   // có thể bị chặn (captcha) hoặc Lazada đổi cấu trúc trang
            throw new InvalidOperationException($"Không tìm thấy pdt_price trong trang (html dài {html.Length} ký tự)");

        var digits = new string(m.Groups[1].Value.Where(char.IsDigit).ToArray());
        if (!decimal.TryParse(digits, out var price)) return null;

        var t = TitleRegex().Match(html);
        var name = t.Success ? WebUtility.HtmlDecode(t.Groups[1].Value) : "";

        // Giá gốc không có trong HTML tĩnh nên để null
        return new CollectedPrice(externalId, name, url, price, null);
    }

    [GeneratedRegex("\"pdt_price\":\"([^\"]+)\"")]
    private static partial Regex PriceRegex();

    [GeneratedRegex("<meta property=\"og:title\" content=\"([^\"]*)\"")]
    private static partial Regex TitleRegex();
}

public class PriceCollectionService(
    AppDbContext db,
    IEnumerable<IPriceCollector> collectors,
    ILogger<PriceCollectionService> log)
{
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var products = await db.Products.Include(p => p.Source).ToListAsync(ct);
        var saved = 0;

        foreach (var p in products)
        {
            var collector = collectors.FirstOrDefault(c => c.SourceName == p.Source.Name);
            if (collector is null) continue;

            try
            {
                var r = await collector.CollectAsync(p.ExternalId, p.Url, ct);
                if (r is null)
                {
                    log.LogWarning("Không lấy được giá {Source}/{Id}", p.Source.Name, p.ExternalId);
                    continue;
                }

                db.PriceSnapshots.Add(new PriceSnapshot
                {
                    ProductId = p.Id,
                    Price = r.Price,
                    OriginalPrice = r.OriginalPrice,
                    CollectedAt = DateTime.UtcNow
                });
                saved++;
            }
            catch (Exception ex)
            {
                // Một sản phẩm lỗi không được làm hỏng cả lượt chạy
                log.LogError(ex, "Lỗi khi thu thập {Source}/{Id}", p.Source.Name, p.ExternalId);
            }

            await Task.Delay(1500, ct);   // giãn cách request, tránh bị chặn
        }

        await db.SaveChangesAsync(ct);
        return saved;
    }
}