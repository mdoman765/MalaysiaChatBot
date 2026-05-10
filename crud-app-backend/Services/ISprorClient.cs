namespace crud_app_backend.Bot.Services
{
    // ── Records ───────────────────────────────────────────────────────────────

    public record SprorShopData(
        string Id,        // data[0].id
        string ContId,    // data[0].cont_id
        string ShopName,  // data[0].site_name
        string SiteCode); // data[0].site_code

    public record SprorCategory(string Id, string Name);

    public record SprorProduct(
        string Id,
        string Name,
        double Price,
        double OldPrice,
        string Code,
        string ImageUrl,
        int Factor,
        string GroupId,   // groupId from product — used in order payload
        string PriceId);  // priceId from product — used in order payload

    public record SprorOrderResult(bool Success, string? OrderId, string? Error);

    // SR Agent (salesman) records
    public record SprorAgent(string StaffId, string StaffName);

    public record SprorAgentResult(
        bool Success,
        List<SprorAgent> Agents,
        string? SiteName,
        string? Error);

    // ── Interface ─────────────────────────────────────────────────────────────

    public interface ISprorClient
    {
        Task<SprorShopData?> ValidateShopAsync(string shopCode, CancellationToken ct = default);
        Task<List<SprorCategory>> GetCategoriesAsync(CancellationToken ct = default);
        Task<List<SprorCategory>> GetSubcategoriesAsync(string catId, CancellationToken ct = default);
        Task<List<SprorProduct>> GetProductsAsync(string subcatId, CancellationToken ct = default);
        Task<SprorOrderResult> PlaceOrderAsync(PlaceOrderRequest req, CancellationToken ct = default);
        Task<string> GetOrdersAsync(string shopUserId, CancellationToken ct = default);

        /// <summary>
        /// GET /api/v3/get-sr-agent?country_id={countryId}&amp;site_code={siteCode}
        /// Returns the salesman list assigned to a shop.
        /// </summary>
        Task<SprorAgentResult> GetSrAgentsAsync(string siteCode, string countryId = "15", CancellationToken ct = default);
    }

    // ── DTOs ──────────────────────────────────────────────────────────────────

    public class PlaceOrderRequest
    {
        public string UserId { get; set; } = "";
        public string CountryId { get; set; } = "15";
        public string GroupId { get; set; } = "";
        public string PriceId { get; set; } = "";
        public List<UaeOrderItem> Items { get; set; } = new();
        public double TotalAmount { get; set; }
    }

    public class UaeOrderItem
    {
        public string Pid { get; set; } = "";
        public string Name { get; set; } = "";
        public string Code { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public double Price { get; set; }
        public double OldPrice { get; set; }
        public int Qty { get; set; }
        public int Factor { get; set; } = 1;
        public double Amount { get; set; }
        public string GroupId { get; set; } = "";
        public string PriceId { get; set; } = "";
    }
}
