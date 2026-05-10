using crud_app_backend.Bot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace crud_app_backend.Services
{
    /// <summary>
    /// Runs every 3 minutes:
    ///   1. Keeps SQL Server connection pool warm (prevents IIS idle timeout)
    ///   2. Pre-loads last 100 recently active sessions into IMemoryCache
    ///      so returning users NEVER hit SQL — instant reply every time.
    /// Uses UaeSession + "uae:{phone}" cache key to match UaeBotService.
    /// </summary>
    public class KeepAliveService : BackgroundService
    {
        private readonly IServiceScopeFactory     _scopeFactory;
        private readonly ILogger<KeepAliveService> _logger;

        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(3);

        public KeepAliveService(
            IServiceScopeFactory scopeFactory,
            ILogger<KeepAliveService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger       = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[KeepAlive] Started — pinging every {Min} min",
                Interval.TotalMinutes);

            // Wait 30s after startup so EF warmup in Program.cs finishes first
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await PingAsync(stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }

            _logger.LogInformation("[KeepAlive] Stopped.");
        }

        private async Task PingAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db    = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var cache = scope.ServiceProvider.GetRequiredService<IMemoryCache>();

                // Load last 100 recently active sessions into memory cache.
                // Only set if NOT already in cache — never overwrite a live session.
                var sessions = await db.WhatsAppSessions
                    .AsNoTracking()
                    .OrderByDescending(s => s.UpdatedAt)
                    .Take(100)
                    .ToListAsync(ct);

                var opts = new MemoryCacheEntryOptions()
                    .SetSlidingExpiration(TimeSpan.FromMinutes(60));

                int warmed = 0;
                foreach (var row in sessions)
                {
                    // UAE bot uses "uae:{phone}" cache key
                    var cacheKey = $"uae:{row.Phone}";

                    if (!cache.TryGetValue(cacheKey, out _))
                    {
                        var uaeSession = UaeSession.Load(row.Phone, row.TempData);
                        if (uaeSession.State == "INIT" && row.CurrentStep != "INIT")
                            uaeSession.State = row.CurrentStep;
                        cache.Set(cacheKey, uaeSession, opts);
                        warmed++;
                    }
                }

                _logger.LogDebug("[KeepAlive] Ping OK at {Time:HH:mm:ss} — {Total} sessions, {Warmed} newly cached",
                    DateTime.UtcNow, sessions.Count, warmed);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[KeepAlive] Ping failed — will retry in {Min} min",
                    Interval.TotalMinutes);
            }
        }
    }
}
