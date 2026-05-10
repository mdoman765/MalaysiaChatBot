namespace crud_app_backend.Bot.Services
{
    public record UaeCrmResult(bool Success, string? TicketId, string? Error);

    public class UaeCrmRequest
    {
        public string ShopCode { get; set; } = "";
        public string WhatsappNumber { get; set; } = "";
        public List<string> VoiceFiles { get; set; } = new();
        public List<string> Images { get; set; } = new();
        public string? Description { get; set; }
        public string Location { get; set; } = "";
        public string CartItems { get; set; } = "";

        /// <summary>
        /// ticketType values:
        ///   "complaint"           ? Complaint / Feedback
        ///   "product_replcatement"? Return / Replacement (API spelling preserved)
        ///   "connect_to_agent"    ? Talk to Support Agent
        /// </summary>
        public string TicketType { get; set; } = "complaint";
    }

    public interface IUaeCrmService
    {
        Task<UaeCrmResult> SubmitAsync(UaeCrmRequest req, CancellationToken ct = default);
    }
}
