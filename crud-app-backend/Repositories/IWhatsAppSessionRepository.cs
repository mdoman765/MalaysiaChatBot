using crud_app_backend.DTOs;
using crud_app_backend.Models;

namespace crud_app_backend.Repositories
{
    public interface IWhatsAppSessionRepository
    {
        /// <summary>
        /// Returns the session for a phone number, or null if none exists.
        /// </summary>
        Task<WhatsAppSession?> GetByPhoneAsync(string phone, CancellationToken ct = default);

        /// <summary>
        /// Atomically insert-or-update the session and write one history row.
        /// </summary>
        Task UpsertAsync(WhatsAppSession session, string? rawMessage, CancellationToken ct = default);

        /// <summary>
        /// Hard-delete a session (and its history via CASCADE).
        /// </summary>
        Task<bool> DeleteAsync(string phone, CancellationToken ct = default);

        /// <summary>
        /// Returns the last <paramref name="limit"/> history rows for a phone,
        /// newest first.
        /// </summary>
        Task<List<WhatsAppSessionHistory>> GetHistoryAsync(
            string phone, int limit = 20, CancellationToken ct = default);
    }
}
