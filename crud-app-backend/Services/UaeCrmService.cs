using System.Net.Http.Headers;
using System.Text.Json;

namespace crud_app_backend.Bot.Services
{
    /// <summary>
    /// Submits UAE chatbot tickets to CRM via multipart/form-data.
    /// Sends actual file BYTES (not URLs) — same approach as old WhatsAppComplaintService.
    ///
    /// POST crm.prangroup.com/api/whats-app/sales-support/v1/create-uae-ticket
    /// Fields:
    ///   shopCode, whatsappNumber, description, location, cartItems
    ///   ticket_category = "UAE_Chatbot"
    ///   ticket_type     = "COMPLAIN" | "PRODUCT_REPLACEMENT" | "CONNECT_TO_AGENT"
    ///   voice_file[]    = binary audio files
    ///   images[]        = binary image files
    /// </summary>
    public class UaeCrmService : IUaeCrmService
    {
        private readonly IHttpClientFactory _factory;
        private readonly IConfiguration _config;
        private readonly ILogger<UaeCrmService> _logger;

        public UaeCrmService(
            IHttpClientFactory factory,
            IConfiguration config,
            ILogger<UaeCrmService> logger)
        {
            _factory = factory;
            _config = config;
            _logger = logger;
        }

        public async Task<UaeCrmResult> SubmitAsync(
            UaeCrmRequest req, CancellationToken ct = default)
        {
            var url = _config["Crm:UaeSubmitUrl"]
                   ?? "https://crm.prangroup.com/api/whats-app/sales-support/v1/create-uae-ticket";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(55));

            try
            {
                using var form = new MultipartFormDataContent();

                // ── Text fields ───────────────────────────────────────────────
                void Text(string key, string? val)
                    => form.Add(new StringContent(val ?? string.Empty), key);

                Text("shopCode", req.ShopCode);
                Text("whatsappNumber", req.WhatsappNumber);
                Text("description", req.Description ?? "");
                Text("location", req.Location);
                Text("cartItems", req.CartItems);
                Text("ticket_category", "UAE_Chatbot");
                Text("ticket_type", req.TicketType);

                // ── Voice files → voice_file[] (actual binary bytes) ──────────
                var voiceCount = 0;
                foreach (var filePath in req.VoiceFiles)
                {
                    if (string.IsNullOrWhiteSpace(filePath)) continue;
                    var fileBytes = ReadFile(filePath);
                    if (fileBytes == null) continue;

                    var mime = PathToMime(filePath, "audio/ogg");
                    var content = new ByteArrayContent(fileBytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue(mime);
                    form.Add(content, "voice_file[]", Path.GetFileName(filePath));
                    voiceCount++;
                    _logger.LogInformation("[UaeCRM] Voice attached: {F} ({B} bytes)", filePath, fileBytes.Length);
                }

                // ── Image files → images[] (actual binary bytes) ─────────────
                var imageCount = 0;
                foreach (var filePath in req.Images)
                {
                    if (string.IsNullOrWhiteSpace(filePath)) continue;
                    var fileBytes = ReadFile(filePath);
                    if (fileBytes == null) continue;

                    var mime = PathToMime(filePath, "image/jpeg");
                    var content = new ByteArrayContent(fileBytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue(mime);
                    form.Add(content, "images[]", Path.GetFileName(filePath));
                    imageCount++;
                    _logger.LogInformation("[UaeCRM] Image attached: {F} ({B} bytes)", filePath, fileBytes.Length);
                }

                _logger.LogInformation(
                    "[UaeCRM] POST {Url} ticketType={T} shop={S} voices={V} images={I}",
                    url, req.TicketType, req.ShopCode, voiceCount, imageCount);

                var client = _factory.CreateClient("CrmClient");
                var resp = await client.PostAsync(url, form, cts.Token);
                var json = await resp.Content.ReadAsStringAsync(cts.Token);

                _logger.LogInformation("[UaeCRM] {Code} response: {Body}",
                    (int)resp.StatusCode,
                    json.Length > 500 ? json[..500] : json);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // status == "success"
                var statusVal = root.TryGetProperty("status", out var sv) ? sv.GetString() : null;
                var isSuccess = statusVal?.ToLower() is "success" or "true" or "1"
                             || resp.IsSuccessStatusCode;

                if (!isSuccess)
                {
                    var errMsg = root.TryGetProperty("message", out var mv)
                        ? mv.GetString() : "Submission failed";
                    _logger.LogWarning("[UaeCRM] Failed: {Msg}", errMsg);
                    return new UaeCrmResult(false, null, errMsg);
                }

                // Extract ticket ID from data.id
                string? ticketId = null;
                if (root.TryGetProperty("data", out var da) &&
                    da.ValueKind == JsonValueKind.Object &&
                    da.TryGetProperty("id", out var idEl) &&
                    idEl.ValueKind != JsonValueKind.Null)
                {
                    ticketId = idEl.ToString();
                }

                _logger.LogInformation("[UaeCRM] Success ticketId={T}", ticketId ?? "null");
                return new UaeCrmResult(true, ticketId, null);
            }
            catch (OperationCanceledException)
            {
                _logger.LogError("[UaeCRM] Timed out — type={T}", req.TicketType);
                return new UaeCrmResult(false, null,
                    "Support system is taking too long. Please try again.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UaeCRM] Crashed — type={T}", req.TicketType);
                return new UaeCrmResult(false, null, "Could not reach support system.");
            }
        }

        // ── Read file bytes from disk ─────────────────────────────────────────
        private byte[]? ReadFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    _logger.LogWarning("[UaeCRM] File not found: {P}", filePath);
                    return null;
                }
                return File.ReadAllBytes(filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UaeCRM] ReadFile failed: {P}", filePath);
                return null;
            }
        }

        // ── Mime type from file extension ─────────────────────────────────────
        private static string PathToMime(string path, string fallback)
            => Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".ogg" => "audio/ogg",
                ".mp3" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                ".wav" => "audio/wav",
                ".opus" => "audio/opus",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                _ => fallback,
            };
    }
}
