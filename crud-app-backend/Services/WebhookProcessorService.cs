namespace crud_app_backend.Bot.Services
{
    public class WebhookProcessorService : BackgroundService
    {
        private readonly WebhookQueue            _queue;
        private readonly IServiceScopeFactory    _scopeFactory;
        private readonly ILogger<WebhookProcessorService> _logger;

        private const int MaxConcurrency = 10;

        public WebhookProcessorService(
            WebhookQueue queue,
            IServiceScopeFactory scopeFactory,
            ILogger<WebhookProcessorService> logger)
        {
            _queue        = queue;
            _scopeFactory = scopeFactory;
            _logger       = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[WebhookProcessor] Started — max concurrency={N}", MaxConcurrency);

            var semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);

            try
            {
                await foreach (var body in _queue.Reader.ReadAllAsync(stoppingToken))
                {
                    await semaphore.WaitAsync(stoppingToken);

                    var captured = body;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await using var scope = _scopeFactory.CreateAsyncScope();
                            var bot = scope.ServiceProvider.GetRequiredService<IUaeBotService>();
                            await bot.ProcessAsync(captured);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[WebhookProcessor] ProcessAsync crashed");
                        }
                        finally
                        {
                            try { semaphore.Release(); }
                            catch (ObjectDisposedException) { }
                        }
                    });
                }
            }
            catch (OperationCanceledException) { }
            finally { semaphore.Dispose(); }

            _logger.LogInformation("[WebhookProcessor] Stopped.");
        }
    }
}
