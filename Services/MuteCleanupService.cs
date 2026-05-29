using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using ChatRoom2.Data;

namespace ChatRoom2.Services;

public class MuteCleanupService : BackgroundService
{
    private readonly IServiceProvider _services;

    public MuteCleanupService(IServiceProvider services)
    {
        _services = services;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
                var now = DateTime.UtcNow;
                var expired = await db.MuteRecords
                    .Where(m => m.ExpiresAt <= now)
                    .ToListAsync(stoppingToken);
                if (expired.Any())
                {
                    db.MuteRecords.RemoveRange(expired);
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch { /* 记录日志 */ }
        }
    }
}