namespace crud_app_backend.Models
{
    public class BotCatalogSettings
    {
        public int Id { get; set; }
        public string CatalogId { get; set; } = "";
        public string CatalogPhone { get; set; } = "";
        public string ThumbSku { get; set; } = "";
        public DateTime UpdatedAt { get; set; }
    }
}

