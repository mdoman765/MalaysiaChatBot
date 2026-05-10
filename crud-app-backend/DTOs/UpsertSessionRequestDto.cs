using System.ComponentModel.DataAnnotations;

namespace crud_app_backend.DTOs
{
    public class UpsertSessionRequestDto
    {
        [Required]
        public string Phone { get; set; } = default!;

        [Required]
        public string CurrentStep { get; set; } = default!;

        /// <summary>
        /// Full JSON string — whatever the workflow needs to persist.
        /// e.g. {"menu_map":{"1":"category"},"selected_category":3}
        /// </summary>
        public string TempData { get; set; } = "{}";

        public string PreviousStep { get; set; } = "INIT";

        /// <summary>Optional: raw message text, stored in audit log.</summary>
        public string? RawMessage { get; set; }
    }
}
