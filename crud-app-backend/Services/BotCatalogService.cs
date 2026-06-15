// Services/BotCatalogService.cs
using crud_app_backend.Models;
using crud_app_backend.Repositories;
using Microsoft.Extensions.Caching.Memory;

namespace crud_app_backend.Services
{
    public interface IBotCatalogService
    {
        Task<BotCatalogSettings> GetSettingsAsync();
        Task<string> GetProductNameAsync(string sku); // never throws, falls back to SKU
        Task<Dictionary<string, string>> GetAllNamesAsync();
    }

    public class BotCatalogService : IBotCatalogService
    {
        private readonly IBotCatalogRepository _repo;
        private readonly IMemoryCache _cache;
        private readonly ILogger<BotCatalogService> _logger;

        private const string SettingsKey = "catalog:settings";
        private const string ProductMapKey = "catalog:products";

        public BotCatalogService(
            IBotCatalogRepository repo,
            IMemoryCache cache,
            ILogger<BotCatalogService> logger)
        {
            _repo = repo;
            _cache = cache;
            _logger = logger;
        }

        public async Task<BotCatalogSettings> GetSettingsAsync()
        {
            if (_cache.TryGetValue(SettingsKey, out BotCatalogSettings? cached) && cached != null)
                return cached;

            var settings = await _repo.GetSettingsAsync() ?? new BotCatalogSettings
            {
                CatalogId = "2069443963978268",  // safe fallback
                CatalogPhone = "60162272364",
                ThumbSku = "PRANF-RFL-005"
            };

            // Cache for 30 minutes — admin can update DB and wait for expiry
            _cache.Set(SettingsKey, settings,
                new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(30)));

            return settings;
        }

        public async Task<string> GetProductNameAsync(string sku)
        {
            var map = await GetAllNamesAsync();
            return map.TryGetValue(sku, out var name) ? name : sku; // fallback to SKU
        }

        public async Task<Dictionary<string, string>> GetAllNamesAsync()
        {
            if (_cache.TryGetValue(ProductMapKey, out Dictionary<string, string>? cached) && cached != null)
                return cached;

            try
            {
                var map = await _repo.GetProductNameMapAsync();
                _cache.Set(ProductMapKey, map,
                    new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(30)));
                return map;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Catalog] Failed to load product names from DB");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}