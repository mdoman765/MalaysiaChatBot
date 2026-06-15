using crud_app_backend.Models;

namespace crud_app_backend.Repositories
{
    public interface IBotCatalogRepository
    {
        Task<BotCatalogSettings?> GetSettingsAsync();
        Task<Dictionary<string, string>> GetProductNameMapAsync(); // SKU → Name
        Task<string?> GetProductNameAsync(string sku);
    }
}
