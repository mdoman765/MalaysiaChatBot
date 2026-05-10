using System.Text.Json;
using System.Text.Json.Serialization;

namespace crud_app_backend.Bot.Models
{
    /// <summary>
    /// Full session state for one UAE bot user.
    /// Serialised to JSON and stored in dbo.WhatsAppSessions.TempData.
    /// </summary>
    public class UaeSession
    {
        public string Phone { get; set; } = string.Empty;
        public string State { get; set; } = "INIT";
        public string PreviousState { get; set; } = "INIT";
        public string? Lang { get; set; }   // "en" | "bn" | "hi"

        // Shop authentication
        public bool ShopVerified { get; set; }
        public string? ShopCode { get; set; }
        public string? ShopUserId { get; set; }   // id from shopDetails API
        public string? ShopName { get; set; }

        // Complaint / Return media
        public string MediaDescription { get; set; } = string.Empty;
        public List<string> MediaImages { get; set; } = new();
        public List<string> MediaVoices { get; set; } = new();

        // ── Language helper ───────────────────────────────────────────────────
        public string T(string en, string bn, string hi)
            => Lang == "bn" ? bn : Lang == "hi" ? hi : en;

        public string T(string en, string bn)
            => Lang == "bn" ? bn : en;

        // ── Serialisation ─────────────────────────────────────────────────────
        private static readonly JsonSerializerOptions WriteOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public string Save() => JsonSerializer.Serialize(this, WriteOpts);

        public static UaeSession Load(string phone, string? json)
        {
            var s = new UaeSession { Phone = phone };
            if (string.IsNullOrWhiteSpace(json) || json == "{}") return s;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                s.State = Str(root, "state") ?? "INIT";
                s.PreviousState = Str(root, "previousState") ?? "INIT";
                s.Lang = Str(root, "lang");

                s.ShopVerified = Bool(root, "shopVerified") ?? false;
                s.ShopCode = Str(root, "shopCode");
                s.ShopUserId = Str(root, "shopUserId");
                s.ShopName = Str(root, "shopName");

                s.MediaDescription = Str(root, "mediaDescription") ?? string.Empty;
                s.MediaImages = StrList(root, "mediaImages") ?? new();
                s.MediaVoices = StrList(root, "mediaVoices") ?? new();
            }
            catch { s = new UaeSession { Phone = phone }; }

            return s;
        }

        // ── JSON helpers ──────────────────────────────────────────────────────
        private static string? Str(JsonElement el, string key)
            => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
               ? v.GetString() : null;

        private static bool? Bool(JsonElement el, string key)
            => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True
                ? true
                : el.TryGetProperty(key, out var v2) && v2.ValueKind == JsonValueKind.False
                    ? false : null;

        private static List<string>? StrList(JsonElement el, string key)
        {
            if (!el.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<string>();
            foreach (var item in v.EnumerateArray())
                if (item.GetString() is { } str) list.Add(str);
            return list;
        }
    }
}
