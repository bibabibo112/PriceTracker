using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace PriceTracker;

// Một phần tử trong Products.json
public class SeedItem
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("tiki_url")] public string? TikiUrl { get; set; }
    [JsonPropertyName("tiki_spid")] public string? TikiSpid { get; set; }
    [JsonPropertyName("lazada_url")] public string? LazadaUrl { get; set; }
    [JsonPropertyName("lazada_itemid")] public string? LazadaItemId { get; set; }
}

public static partial class SeedData
{
    // Đọc Products.json: mỗi phần tử = 1 ProductGroup + 1 Product cho mỗi sàn
    // Id sản phẩm Tiki được tách từ tiki_url (đoạn "-p278692861.html")
    // Chạy nhiều lần không bị trùng (bỏ qua sản phẩm đã có)
    public static async Task<int> LoadAsync(AppDbContext db, string path)
    {
        var items = JsonSerializer.Deserialize<List<SeedItem>>(await File.ReadAllTextAsync(path)) ?? new();
        var sources = await db.Sources.ToDictionaryAsync(s => s.Name);
        var added = 0;

        foreach (var it in items)
        {
            var group = await db.ProductGroups.FirstOrDefaultAsync(g => g.Name == it.Name)
                        ?? db.ProductGroups.Add(new ProductGroup { Name = it.Name }).Entity;

            var tikiId = TikiIdRegex().Match(it.TikiUrl ?? "").Groups[1].Value;

            added += await AddProduct(db, group, sources["Tiki"], tikiId, it.Name, it.TikiUrl);
            added += await AddProduct(db, group, sources["Lazada"], it.LazadaItemId, it.Name, it.LazadaUrl);
        }

        await db.SaveChangesAsync();
        return added;
    }

    [GeneratedRegex(@"-p(\d+)\.html")]
    private static partial Regex TikiIdRegex();

    private static async Task<int> AddProduct(
        AppDbContext db, ProductGroup group, Source source, string? externalId, string name, string? url)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return 0;

        // Đã có thì cập nhật tên, link, nhóm (để sửa dữ liệu sai từ lần nạp trước), không tính là "added"
        var existing = await db.Products.FirstOrDefaultAsync(p => p.SourceId == source.Id && p.ExternalId == externalId);
        if (existing is not null)
        {
            existing.Name = name;
            existing.Url = url ?? "";
            existing.ProductGroup = group;
            return 0;
        }

        db.Products.Add(new Product
        {
            SourceId = source.Id,
            ExternalId = externalId,
            Name = name,
            Url = url ?? "",
            ProductGroup = group
        });
        return 1;
    }
}