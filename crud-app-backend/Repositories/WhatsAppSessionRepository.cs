using crud_app_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace crud_app_backend.Repositories
{
    public class WhatsAppSessionRepository : IWhatsAppSessionRepository
    {
        private readonly AppDbContext _db;
        private readonly ILogger<WhatsAppSessionRepository> _logger;

        public WhatsAppSessionRepository(
            AppDbContext db,
            ILogger<WhatsAppSessionRepository> logger)
        {
            _db = db;
            _logger = logger;
        }

        // ── GET ──────────────────────────────────────────────────────────

        //public async Task<WhatsAppSession?> GetByPhoneAsync(
        //    string phone, CancellationToken ct = default)
        //{
        //    return await _db.WhatsAppSessions
        //        .AsNoTracking()
        //        .FirstOrDefaultAsync(s => s.Phone == phone, ct);
        //}
        public async Task<WhatsAppSession?> GetByPhoneAsync(
    string phone, CancellationToken ct = default)
        {
            try
            {
                return await _db.WhatsAppSessions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Phone == phone, ct);
            }
            catch (OperationCanceledException)
            {
                // Request was cancelled — rethrow to respect cancellation
                throw;
            }
            catch (Exception ex)
            {
                // Log the exception (replace with your logger)
                Console.WriteLine($"Error in GetByPhoneAsync: {ex.Message}");

                // Optionally: return null or rethrow based on your design
                return null;
                // throw; // <- use this instead if you want upstream handling
            }
        }

        // ── UPSERT ───────────────────────────────────────────────────────
        // PERF: `fromStep` is now supplied by the caller (it's the in-memory
        // PreviousStep from BotSession, passed via UpsertSessionRequestDto).
        // We no longer read `existing.CurrentStep` just to get the from-step
        // for the history row — the caller already knows it.

        public async Task UpsertAsync(
            WhatsAppSession session,
            string? rawMessage,
            CancellationToken ct = default)
        {
            var strategy = _db.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database
                    .BeginTransactionAsync(ct);

                try
                {
                    // ── 1. Fetch existing row (tracked) — needed to decide INSERT vs UPDATE
                    var existing = await _db.WhatsAppSessions
                        .FirstOrDefaultAsync(s => s.Phone == session.Phone, ct);

                    // CHANGED: use the PreviousStep supplied by the caller instead of
                    // reading existing.CurrentStep — both represent the same value but
                    // PreviousStep comes from the authoritative in-memory bot state,
                    // avoiding any reliance on the DB read for history accuracy.
                    string fromStep;

                    if (existing == null)
                    {
                        // ── INSERT ────────────────────────────────────────
                        fromStep = "NEW";
                        session.CreatedAt = DateTime.UtcNow;
                        session.UpdatedAt = DateTime.UtcNow;
                        await _db.WhatsAppSessions.AddAsync(session, ct);

                        _logger.LogInformation(
                            "[WA-Session] INSERT phone={Phone} step={Step}",
                            session.Phone, session.CurrentStep);
                    }
                    else
                    {
                        // ── UPDATE ────────────────────────────────────────
                        // CHANGED: fromStep = session.PreviousStep  (was: existing.CurrentStep)
                        fromStep = string.IsNullOrWhiteSpace(session.PreviousStep)
                            ? existing.CurrentStep   // safe fallback if caller omits it
                            : session.PreviousStep;

                        existing.CurrentStep = session.CurrentStep;
                        existing.PreviousStep = session.PreviousStep;
                        existing.TempData = session.TempData;
                        existing.UpdatedAt = DateTime.UtcNow;
                        // CreatedAt never changes

                        _logger.LogInformation(
                            "[WA-Session] UPDATE phone={Phone} {From}→{To}",
                            session.Phone, fromStep, session.CurrentStep);
                    }

                    // ── 2. Write history row ──────────────────────────────
                    var historyRow = new WhatsAppSessionHistory
                    {
                        Phone = session.Phone,
                        FromStep = fromStep,
                        ToStep = session.CurrentStep,
                        RawMessage = rawMessage,
                        TempDataSnapshot = session.TempData,
                        CreatedAt = DateTime.UtcNow
                    };
                    await _db.WhatsAppSessionHistories.AddAsync(historyRow, ct);

                    // ── 3. Save both rows in one round-trip ───────────────
                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync(ct);
                    _logger.LogError(ex,
                        "[WA-Session] Upsert FAILED for phone={Phone}", session.Phone);
                    throw;
                }
            });
        }

        // ── DELETE ───────────────────────────────────────────────────────

        public async Task<bool> DeleteAsync(
            string phone, CancellationToken ct = default)
        {
            var session = await _db.WhatsAppSessions
                .FirstOrDefaultAsync(s => s.Phone == phone, ct);

            if (session == null) return false;

            _db.WhatsAppSessions.Remove(session);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[WA-Session] DELETED phone={Phone}", phone);
            return true;
        }

        // ── HISTORY ──────────────────────────────────────────────────────

        public async Task<List<WhatsAppSessionHistory>> GetHistoryAsync(
            string phone, int limit = 20, CancellationToken ct = default)
        {
            return await _db.WhatsAppSessionHistories
                .AsNoTracking()
                .Where(h => h.Phone == phone)
                .OrderByDescending(h => h.CreatedAt)
                .Take(limit)
                .ToListAsync(ct);
        }
    }
}
