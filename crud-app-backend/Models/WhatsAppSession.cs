using System.ComponentModel.DataAnnotations;

namespace crud_app_backend.Models
{
    public class WhatsAppSession
    {
        [Key]
        [MaxLength(30)]
        public string Phone { get; set; } = default!;

        [MaxLength(50)]
        public string CurrentStep { get; set; } = "INIT";

        // JSON blob — menu_map, selected_category, cart items, etc.
        public string TempData { get; set; } = "{}";

        [MaxLength(50)]
        public string PreviousStep { get; set; } = "INIT";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public ICollection<WhatsAppSessionHistory> History { get; set; }
            = new List<WhatsAppSessionHistory>();
    }
}
