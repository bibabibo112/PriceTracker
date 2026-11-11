using Microsoft.EntityFrameworkCore;

namespace PriceTracker;

public class Source
{
    public int Id { get; set; }
    public string Name { get; set; } = "";          // "Tiki", "Lazada"
}

// Gom cùng một sản phẩm ở nhiều sàn (ghép thủ công)
public class ProductGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<Product> Products { get; set; } = new();
}

public class Product
{
    public int Id { get; set; }
    public int SourceId { get; set; }
    public Source Source { get; set; } = null!;
    public int? ProductGroupId { get; set; }
    public ProductGroup? ProductGroup { get; set; }
    public string ExternalId { get; set; } = "";    // id sản phẩm trên sàn
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public List<PriceSnapshot> Snapshots { get; set; } = new();
}

// Mỗi lần thu thập = 1 dòng mới, KHÔNG ghi đè giá cũ
public class PriceSnapshot
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public decimal Price { get; set; }
    public decimal? OriginalPrice { get; set; }
    public DateTime CollectedAt { get; set; }
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<ProductGroup> ProductGroups => Set<ProductGroup>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<PriceSnapshot> PriceSnapshots => Set<PriceSnapshot>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Source>().HasData(
            new Source { Id = 1, Name = "Tiki" },
            new Source { Id = 2, Name = "Lazada" });

        // Một sản phẩm trên một sàn chỉ xuất hiện một lần
        b.Entity<Product>().HasIndex(p => new { p.SourceId, p.ExternalId }).IsUnique();

        b.Entity<PriceSnapshot>().HasIndex(s => new { s.ProductId, s.CollectedAt });
    }
}