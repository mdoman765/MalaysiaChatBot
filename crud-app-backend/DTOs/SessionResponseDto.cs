namespace crud_app_backend.DTOs
{
    public class SessionResponseDto
    {
        public string Phone { get; set; } = default!;
        public string CurrentStep { get; set; } = "INIT";
        public string TempData { get; set; } = "{}";
        public string PreviousStep { get; set; } = "INIT";
        public DateTime UpdatedAt { get; set; }
        public bool IsNew { get; set; }
    }
}
