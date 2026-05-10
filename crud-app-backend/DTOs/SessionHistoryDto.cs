namespace crud_app_backend.DTOs
{
    public class SessionResponse
    {
        public string Phone { get; set; } = default!;
        public string CurrentStep { get; set; } = "INIT";
        public string PreviousStep { get; set; } = "INIT";
        public string TempData { get; set; } = "{}";
        public DateTime UpdatedAt { get; set; }
        public bool IsNew { get; set; }
    }

}
