using crud_app_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace crud_app_backend.Repositories
{
    public class BotCatalogRepository : IBotCatalogRepository
    {
        private readonly AppDbContext _context;

        public BotCatalogRepository(AppDbContext context)
        {
            _context = context;
        }

        public async Task<BotCatalogSettings?> GetSettingsAsync()
        {
            return await _context.BotCatalogSettings
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync();
        }

        public async Task<Dictionary<string, string>> GetProductNameMapAsync()
        {
            return await _context.BotCatalogProducts
                .Where(x => x.IsActive)
                .ToDictionaryAsync(
                    x => x.Sku,
                    x => x.ProductName,
                    StringComparer.OrdinalIgnoreCase);
        }

        public async Task<string?> GetProductNameAsync(string sku)
        {
            return await _context.BotCatalogProducts
                .Where(x => x.Sku == sku && x.IsActive)
                .Select(x => x.ProductName)
                .FirstOrDefaultAsync();
        }
    }
}