using crud_app_backend.DTOs;
using crud_app_backend.Models;
using crud_app_backend.Repositories;
namespace crud_app_backend.Services
{
    public class WhatsAppSessionService : IWhatsAppSessionService
    {
        private readonly IWhatsAppSessionRepository _repo;
        private readonly ILogger<WhatsAppSessionService> _logger;

        public WhatsAppSessionService(
            IWhatsAppSessionRepository repo,
            ILogger<WhatsAppSessionService> logger)
        {
            _repo = repo;
            _logger = logger;
        }

        // ─────────────────────────────────────────────────────────────────
        // GET SESSION
        // Called by: GET /api/whatsapp/session?phone=...
        // Always returns 200 — never throws for a missing session.
        // ─────────────────────────────────────────────────────────────────
        public async Task<SessionResponse> GetSessionAsync(
            string phone,
            CancellationToken ct = default)
        {
            var session = await _repo.GetByPhoneAsync(phone, ct);

            if (session == null)
            {
                // First contact from this phone number.
                // Return a clean INIT state so n8n can start the conversation.
                _logger.LogInformation(
                    "[WA-Service] New user — phone={Phone}", phone);

                return new SessionResponse
                {
                    Phone = phone,
                    CurrentStep = "INIT",
                    PreviousStep = "INIT",
                    TempData = "{}",
                    UpdatedAt = DateTime.UtcNow,
                    IsNew = true
                };
            }

            _logger.LogDebug(
                "[WA-Service] Session loaded — phone={Phone} step={Step}",
                session.Phone, session.CurrentStep);

            return MapEntityToResponse(session, isNew: false);
        }


        // ─────────────────────────────────────────────────────────────────
        // UPSERT SESSION
        // Called by: POST /api/whatsapp/session
        // Converts the inbound DTO → entity, then delegates to repository.
        // Returns ApiResponse<object> so the controller can return Ok(result).
        // ─────────────────────────────────────────────────────────────────
        public async Task<ApiResponseDto<object>> UpsertSessionAsync(
            UpsertSessionRequestDto req,
            CancellationToken ct = default)
        {
            // Map request DTO → domain entity
            var entity = new WhatsAppSession
            {
                Phone = req.Phone.Trim(),
                CurrentStep = req.CurrentStep.Trim(),
                PreviousStep = req.PreviousStep.Trim(),
                TempData = string.IsNullOrWhiteSpace(req.TempData)
                                    ? "{}"
                                    : req.TempData,
                // CreatedAt / UpdatedAt are set inside the repository
            };

            // Repository handles INSERT vs UPDATE + history row in one transaction
            await _repo.UpsertAsync(entity, req.RawMessage, ct);

            _logger.LogInformation(
                "[WA-Service] Session upserted — phone={Phone} step={Step}",
                entity.Phone, entity.CurrentStep);

            return ApiResponseDto<object>.Ok(new
            {
                phone = entity.Phone,
                step = entity.CurrentStep
            });
        }


        // ─────────────────────────────────────────────────────────────────
        // DELETE SESSION
        // Called by: DELETE /api/whatsapp/session?phone=...
        // Returns Success=false (→ 404) when phone not found.
        // ─────────────────────────────────────────────────────────────────
        public async Task<ApiResponseDto<object>> DeleteSessionAsync(
            string phone,
            CancellationToken ct = default)
        {
            var deleted = await _repo.DeleteAsync(phone.Trim(), ct);

            if (!deleted)
            {
                _logger.LogWarning(
                    "[WA-Service] Delete requested but no session found — phone={Phone}",
                    phone);

                return ApiResponseDto<object>.Fail(
                    $"No session found for phone: {phone}");
            }

            _logger.LogInformation(
                "[WA-Service] Session deleted — phone={Phone}", phone);

            return ApiResponseDto<object>.Ok(
                new { phone },
                "Session deleted successfully");
        }


        // ─────────────────────────────────────────────────────────────────
        // GET HISTORY
        // Called by: GET /api/whatsapp/session/history?phone=...&limit=20
        // Maps domain history entities → SessionHistoryDto list.
        // ─────────────────────────────────────────────────────────────────
        public async Task<List<WhatsAppSessionHistory>> GetHistoryAsync(
            string phone,
            int limit = 20,
            CancellationToken ct = default)
        {
            // Guard — repository accepts any int, but let's be safe
            if (limit < 1 || limit > 200)
                limit = 20;

            var rows = await _repo.GetHistoryAsync(phone.Trim(), limit, ct);

            _logger.LogDebug(
                "[WA-Service] History loaded — phone={Phone} rows={Count}",
                phone, rows.Count);

            // Map entity list → DTO list
            return rows.Select(h => new WhatsAppSessionHistory
            {
                Id = h.Id,
                Phone = h.Phone,
                FromStep = h.FromStep,
                ToStep = h.ToStep,
                RawMessage = h.RawMessage,
                CreatedAt = h.CreatedAt
            }).ToList();
        }


        // ─────────────────────────────────────────────────────────────────
        // Private helper — entity → response DTO
        // ─────────────────────────────────────────────────────────────────
        private static SessionResponse MapEntityToResponse(
            WhatsAppSession s, bool isNew) => new()
            {
                Phone = s.Phone,
                CurrentStep = s.CurrentStep,
                PreviousStep = s.PreviousStep,
                TempData = s.TempData,
                UpdatedAt = s.UpdatedAt,
                IsNew = isNew
            };
    }
}
