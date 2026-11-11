namespace PriceTracker;

// Tự động thu thập giá theo chu kỳ (mặc định 6 giờ, đổi trong appsettings.json: "Collector": { "IntervalMinutes": 120 })
public class PriceCollectorWorker(
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<PriceCollectorWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(config.GetValue("Collector:IntervalMinutes", 360));

        try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }   // đợi ứng dụng khởi động xong
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // PriceCollectionService là scoped, nên mỗi lượt chạy tạo một scope riêng
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<PriceCollectionService>();
                var saved = await service.RunAsync(ct);
                log.LogInformation("Thu thập tự động xong: lưu {Saved} giá", saved);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                log.LogError(ex, "Lượt thu thập tự động bị lỗi");
            }

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}