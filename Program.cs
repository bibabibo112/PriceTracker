using Microsoft.EntityFrameworkCore;
using PriceTracker;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite("Data Source=pricetracker.db"));

builder.Services.AddHttpClient<TikiCollector>(c => c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0"));
builder.Services.AddHttpClient<LazadaCollector>(c =>
{
    // Dán đúng User-Agent đầy đủ bạn đang dùng trong Postman (Lazada hay từ chối UA quá ngắn)
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
    c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("vi-VN,vi;q=0.9");
});
builder.Services.AddTransient<IPriceCollector>(sp => sp.GetRequiredService<TikiCollector>());
builder.Services.AddTransient<IPriceCollector>(sp => sp.GetRequiredService<LazadaCollector>());
builder.Services.AddScoped<PriceCollectionService>();
builder.Services.AddHostedService<PriceCollectorWorker>();

builder.Services.AddOpenApi();

var app = builder.Build();
app.MapOpenApi();   // mô tả API tại /openapi/v1.json (test bằng Postman)
app.UseDefaultFiles();   // phục vụ wwwroot/index.html tại "/"
app.UseStaticFiles();

// Danh sách sản phẩm (kèm giá mới nhất)
app.MapGet("/products", async (AppDbContext db) =>
    await db.Products
        .Select(p => new
        {
            p.Id,
            p.Name,
            Source = p.Source.Name,
            p.Url,
            LatestPrice = p.Snapshots.OrderByDescending(s => s.CollectedAt)
                .Select(s => (decimal?)s.Price).FirstOrDefault()
        })
        .ToListAsync());

// Thêm sản phẩm cần theo dõi
app.MapPost("/products", async (AddProductRequest req, AppDbContext db) =>
{
    var source = await db.Sources.FirstOrDefaultAsync(s => s.Name == req.SourceName);
    if (source is null) return Results.BadRequest("Nguồn không tồn tại (Tiki/Lazada)");

    var p = new Product { SourceId = source.Id, ExternalId = req.ExternalId, Name = req.Name, Url = req.Url };
    db.Products.Add(p);
    await db.SaveChangesAsync();
    return Results.Created($"/products/{p.Id}", new { p.Id });
});

// Lịch sử giá để vẽ biểu đồ
app.MapGet("/products/{id:int}/price-history", async (int id, AppDbContext db) =>
    await db.PriceSnapshots
        .Where(s => s.ProductId == id)
        .OrderBy(s => s.CollectedAt)
        .Select(s => new { s.CollectedAt, s.Price, s.OriginalPrice })
        .ToListAsync());

// Chạy thu thập thủ công (sau này gọi từ BackgroundService)
app.MapPost("/collect", async (PriceCollectionService svc) =>
    Results.Ok(new { saved = await svc.RunAsync() }));

// Danh sách nhóm sản phẩm kèm giá mới nhất ở từng sàn
app.MapGet("/groups", async (AppDbContext db) =>
    await db.ProductGroups
        .Select(g => new
        {
            g.Id,
            g.Name,
            Prices = g.Products.Select(p => new
            {
                Source = p.Source.Name,
                p.Url,
                Price = p.Snapshots.OrderByDescending(s => s.CollectedAt)
                    .Select(s => (decimal?)s.Price).FirstOrDefault()
            })
        })
        .ToListAsync());

// So sánh lịch sử giá của một nhóm sản phẩm giữa các sàn (dùng cho biểu đồ)
app.MapGet("/groups/{id:int}/compare", async (int id, AppDbContext db) =>
{
    var group = await db.ProductGroups.Where(g => g.Id == id)
        .Select(g => new { g.Id, g.Name }).FirstOrDefaultAsync();
    if (group is null) return Results.NotFound();

    var rows = await db.PriceSnapshots
        .Where(s => s.Product.ProductGroupId == id)
        .OrderBy(s => s.CollectedAt)
        .Select(s => new { Source = s.Product.Source.Name, s.CollectedAt, s.Price, s.OriginalPrice })
        .ToListAsync();

    var series = rows.GroupBy(r => r.Source).Select(g => new
    {
        Source = g.Key,
        Latest = g.Last().Price,
        // Thời gian lưu theo UTC, đánh dấu rõ để trình duyệt đổi sang giờ địa phương
        Points = g.Select(r => new
        {
            CollectedAt = DateTime.SpecifyKind(r.CollectedAt, DateTimeKind.Utc),
            r.Price,
            r.OriginalPrice
        }).ToList()
    }).ToList();

    var cheapest = series.OrderBy(s => s.Latest).FirstOrDefault()?.Source;
    return Results.Ok(new { group.Id, group.Name, Cheapest = cheapest, Series = series });
});

// Nạp danh sách sản phẩm từ Products.json (đặt cùng thư mục với Program.cs)
app.MapPost("/seed", async (AppDbContext db, IWebHostEnvironment env) =>
    Results.Ok(new { added = await SeedData.LoadAsync(db, Path.Combine(env.ContentRootPath, "Products.json")) }));

app.Run();

record AddProductRequest(string SourceName, string ExternalId, string Name, string Url);