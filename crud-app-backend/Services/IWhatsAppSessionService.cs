using crud_app_backend.DTOs;
using crud_app_backend.Models;

namespace crud_app_backend.Services
{

    public interface IWhatsAppSessionService
    {
        /// <summary>
        /// Returns the session for <paramref name="phone"/>.
        /// Never returns null — new phones receive a default INIT response
        /// with <see cref="SessionResponse.IsNew"/> = true.
        /// </summary>
        /// <param name="phone">WhatsApp phone number (e.g. 966XXXXXXXXX)</param>
        Task<SessionResponse> GetSessionAsync(
            string phone,
            CancellationToken ct = default);

        /// <summary>
        /// Insert or update the session row and write one history entry.
        /// Returns a success/fail envelope consumed by the controller.
        /// </summary>
        /// <param name="req">Validated request body from n8n POST node</param>
        Task<ApiResponseDto<object>> UpsertSessionAsync(
            UpsertSessionRequestDto req,
            CancellationToken ct = default);

        /// <summary>
        /// Hard-delete a session and all its history rows (CASCADE).
        /// Returns <see cref="ApiResponse{T}.Success"/> = false when the
        /// phone number is not found, so the controller can return 404.
        /// </summary>
        /// <param name="phone">WhatsApp phone number</param>
        Task<ApiResponseDto<object>> DeleteSessionAsync(
            string phone,
            CancellationToken ct = default);

        /// <summary>
        /// Returns the last <paramref name="limit"/> state-transition rows
        /// for <paramref name="phone"/>, ordered newest-first.
        /// </summary>
        /// <param name="phone">WhatsApp phone number</param>
        /// <param name="limit">Max rows to return (1–200, default 20)</param>
        Task<List<WhatsAppSessionHistory>> GetHistoryAsync(
            string phone,
            int limit = 20,
            CancellationToken ct = default);
    }

}
