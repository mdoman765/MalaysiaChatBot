namespace crud_app_backend.Models
{
    public class BotCatalogProduct
    {
        public int Id { get; set; }
        public string Sku { get; set; } = "";
        public string ProductName { get; set; } = "";
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
