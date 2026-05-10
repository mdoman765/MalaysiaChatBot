using System.Collections.Concurrent;
using System.Text.Json;
using crud_app_backend.Bot.Models;
using crud_app_backend.DTOs;
using crud_app_backend.Models;
using crud_app_backend.Repositories;
using crud_app_backend.Services;
using Microsoft.Extensions.Caching.Memory;

namespace crud_app_backend.Bot.Services
{

    public class UaeBotService : IUaeBotService
    {
        private readonly IWhatsAppSessionService _sessionSvc;
        private readonly IWhatsAppMessageRepository _msgRepo;
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;
        private readonly IDialogClient _dialog;
        private readonly IUaeCrmService _crm;
        private readonly IMemoryCache _cache;
        private readonly BotStateService _state;
        private readonly IHttpClientFactory _httpFactory;
        private readonly ILogger<UaeBotService> _logger;

        public UaeBotService(
            IWhatsAppSessionService sessionSvc,
            IWhatsAppMessageRepository msgRepo,
            IWebHostEnvironment env,
            IConfiguration config,
            IDialogClient dialog,
            IUaeCrmService crm,
            IMemoryCache cache,
            BotStateService state,
            IHttpClientFactory httpFactory,
            ILogger<UaeBotService> logger)
        {
            _sessionSvc = sessionSvc;
            _msgRepo = msgRepo;
            _env = env;
            _config = config;
            _dialog = dialog;
            _crm = crm;
            _cache = cache;
            _state = state;
            _httpFactory = httpFactory;
            _logger = logger;
        }



        public async Task ProcessAsync(JsonElement body)
        {
            try
            {
                var msg = UaeMessageParser.Parse(body);
                if (msg is null) return;

                _logger.LogInformation("[UAE] {Type} from {Phone} id={Id}",
                    msg.MsgType, msg.From, msg.MessageId);

                var userLock = _state.UserLocks.GetOrAdd(msg.From, _ => new SemaphoreSlim(1, 1));
                await userLock.WaitAsync();
                try
                {
                    var session = await LoadSessionAsync(msg.From);

                    var ack = GetAckMessage(session, msg);
                    if (ack != null)
                        await _dialog.SendTextAsync(msg.From, ack);

                    var reply = await RouteAsync(session, msg);

                    if (string.IsNullOrWhiteSpace(reply))
                    {
                        await PersistSessionAsync(session, msg.RawText);
                        return;
                    }

                    await Task.WhenAll(
                        PersistSessionAsync(session, msg.RawText),
                        _dialog.SendTextAsync(msg.From, reply)
                    );
                }
                finally { userLock.Release(); }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UAE] ProcessAsync unhandled crash");
            }
        }


        private string? GetAckMessage(UaeSession s, UaeIncomingMessage msg)
        {
            if (s.State == "AWAITING_SHOP_CODE" && msg.MsgType == "text")
                return s.T("🔍 Verifying shop...", "🔍 শপ যাচাই করা হচ্ছে...", "🔍 दुकान की जाँच हो रही है...");

            if (s.State == "AWAITING_CATEGORY" && msg.MsgType == "text"
                && msg.RawText != "0" && !string.IsNullOrEmpty(msg.RawText))
                return s.T("⏳ Loading categories...", "⏳ ক্যাটাগরি লোড হচ্ছে...", "⏳ श्रेणियाँ लोड हो रही हैं...");

            if (s.State == "AWAITING_SUBCATEGORY" && msg.MsgType == "text"
                && msg.RawText != "0" && !string.IsNullOrEmpty(msg.RawText))
                return s.T("⏳ Loading products...", "⏳ পণ্য লোড হচ্ছে...", "⏳ उत्पाद लोड हो रहे हैं...");

            // ── Gallery burst suppression for ACK ──────────────────────────────
            // WhatsApp fires one webhook per image when user sends from gallery.
            // SemaphoreSlim ensures sequential processing per user.
            // "ack:{phone}" key — only ONE "⏳ Uploading media..." per batch (5s window).
            if ((s.State == "AWAITING_RETURN_DETAILS" || s.State == "AWAITING_COMPLAINT_DETAILS"
                 || s.State == "AWAITING_RETURN_CONFIRM" || s.State == "AWAITING_COMPLAINT_CONFIRM")
                && (msg.MsgType == "image" || msg.MsgType == "audio"))
            {
                // Use WA timestamp — not DateTime.UtcNow.
                // By the time image 3 is processed, UtcNow may have drifted past the window.
                // WA timestamps all gallery images within 1-2 seconds of each other.
                var ackNow = msg.Timestamp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(msg.Timestamp).UtcDateTime
                    : DateTime.UtcNow;
                var ackKey = $"ack:{s.Phone}";
                if (_state.LastImageTime.TryGetValue(ackKey, out var lastAck)
                    && Math.Abs((ackNow - lastAck).TotalSeconds) <= 5)
                    return null; // suppress — ACK already sent for this batch
                _state.LastImageTime[ackKey] = ackNow;
                return s.T("⏳ Uploading media...", "⏳ মিডিয়া আপলোড হচ্ছে...", "⏳ मीडिया अपलोड हो रहा है...");
            }

            if (s.State == "AWAITING_ORDER_CONFIRM" && msg.RawText == "y")
                return s.T("⏳ Placing order...", "⏳ অর্ডার দেওয়া হচ্ছে...", "⏳ ऑर्डर दिया जा रहा है...");

            if (s.State == "AWAITING_COMPLAINT_CONFIRM" && msg.RawText == "y")
                return s.T("⏳ Submitting complaint...", "⏳ অভিযোগ জমা হচ্ছে...", "⏳ शिकायत जमा हो रही है...");

            if (s.State == "AWAITING_RETURN_CONFIRM" && msg.RawText == "y" )
                return s.T("⏳ Submitting return request...", "⏳ রিটার্ন জমা হচ্ছে...", "⏳ वापसी जमा हो रही है...");

            if ((s.State == "AWAITING_AGENT_CONFIRM_1" || s.State == "AWAITING_AGENT_CONFIRM_2")
                && (msg.RawText == "y" || msg.RawText == "1" ))
                return s.T("⏳ Connecting to agent...", "⏳ এজেন্টের সাথে সংযোগ...", "⏳ एजेंट से जोड़ा जा रहा है...");

            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ROUTER
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> RouteAsync(UaeSession s, UaeIncomingMessage msg)
        {
            var raw = msg.RawText;

            // Global resets
            if (msg.MsgType == "text" &&
                new[] { "hi", "hello", "start", "hey", "new" }.Contains(raw))
            {
                ResetSession(s);
                Transition(s, "AWAITING_LANG"); // MUST transition — ResetSession sets INIT
                                                // without this, next message sees INIT again
                await SendWelcomeAsync(msg.From);
                return string.Empty;
            }

            if (s.State == "INIT")
            {
                Transition(s, "AWAITING_LANG");
                await SendWelcomeAsync(msg.From);
                return string.Empty;
            }

            // Global shortcuts (shop-verified users only)
            if (s.ShopVerified)
            {
                if (msg.MsgType == "text" && raw == "menu")
                    return BuildMainMenu(s);

                if (msg.MsgType == "text" && raw == "s")
                {
                    Transition(s, "AWAITING_AGENT_CONFIRM_1");
                    return BuildAgentConfirm1(s);
                }
            }

            return s.State switch
            {
                "AWAITING_LANG" => await HandleLangAsync(s, msg),
                "AWAITING_SHOP_CODE" => await HandleShopCodeAsync(s, msg),
                "MAIN_MENU" => await HandleMainMenu(s, msg),
                "AWAITING_ORDER_CONFIRM" => await HandleOrderConfirmAsync(s, msg),
                "AWAITING_RETURN_DETAILS" => await HandleMediaDetailsAsync(s, msg, "return"),
                "AWAITING_RETURN_CONFIRM" => await HandleReturnConfirmAsync(s, msg),
                "AWAITING_COMPLAINT_DETAILS" => await HandleMediaDetailsAsync(s, msg, "complaint"),
                "AWAITING_COMPLAINT_CONFIRM" => await HandleComplaintConfirmAsync(s, msg),
                "AWAITING_AGENT_CONFIRM_1" => await HandleAgentConfirm1Async(s, msg),
                "AWAITING_AGENT_CONFIRM_2" => await HandleAgentConfirm1Async(s, msg),
                _ => BuildMainMenu(s),
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // LANGUAGE SELECTION
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> HandleLangAsync(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.MsgType != "text") return LangPrompt();

            switch (msg.RawText.Trim())
            {
                case "1": s.Lang = "en"; break;
                case "2": s.Lang = "bn"; break;
                case "3": s.Lang = "hi"; break;
                default:
                    return "❌ Invalid. Reply *1*, *2* or *3*.\n\n" + LangPrompt();
            }

            // ── Already verified — skip shop code, go straight to main menu ──────
            // ShopVerified is preserved across language changes; no re-verification needed.
            if (s.ShopVerified)
            {
                Transition(s, "MAIN_MENU");
                return s.T(
                    $"✅ Language updated.\n\n{BuildMainMenuBody("en")}",
                    $"✅ ভাষা পরিবর্তন হয়েছে।\n\n{BuildMainMenuBody("bn")}",
                    $"✅ भाषा बदल गई।\n\n{BuildMainMenuBody("hi")}");
            }

            // ── First-time user — ask for shop code ──────────────────────────────
            Transition(s, "AWAITING_SHOP_CODE");

            // Send shopcode image with instructions.
            // Safe to be async here — the language loop was caused by the missing
            // Transition(s, "AWAITING_LANG") in the global reset block, not by this.
            // Even if SendImageAsync throws, the session object in IMemoryCache already
            // has state=AWAITING_SHOP_CODE (Transition mutated it above), so the next
            // message will load the correct state from cache.
            var baseUrl = _config["App:BaseUrl"]?.TrimEnd('/') ?? "https://webhook.prangroup.com";
            var shopCodeImageUrl = $"{baseUrl}/images/shopcode.jpeg";

            var caption = s.T(
                "✅ Language set to *English*.\n\n" +
                "👉 Please send your *Shop Code*.\n" +
                "Your Shop Code is on your PRAN-RFL Shop Card.\n\n" +
                "Example: *12345678*",

                "✅ ভাষা বাংলায় সেট হয়েছে।\n\n" +
                "👉 আপনার *শপ কোড* পাঠান।\n" +
                "শপ কোড আপনার PRAN-RFL শপ কার্ডে আছে।\n\n" +
                "উদাহরণ: *12345678*",

                "✅ भाषा हिंदी में सेट है।\n\n" +
                "👉 अपना *शॉप कोड* भेजें।\n" +
                "शॉप कोड आपके PRAN-RFL शॉप कार्ड पर है।\n\n" +
                "उदाहरण: *12345678*");

            await _dialog.SendImageAsync(msg.From, shopCodeImageUrl, caption);
            return string.Empty;
        }

        // ─────────────────────────────────────────────────────────────────────
        // SHOP AUTHENTICATION
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> HandleShopCodeAsync(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.MsgType != "text" || string.IsNullOrWhiteSpace(msg.RawText))
                return s.T(
                    "👉 Enter your *Shop Code*.\nExample: *12345678*",
                    "👉 আপনার *শপ কোড* দিন।\nউদাহরণ: *12345678*",
                    "👉 अपना *शॉप कोड* दर्ज करें।\nउदाहरण: *12345678*");

            var code = msg.RawText.Trim();
            var shop = await ValidateShopAsync(code);

            if (shop == null)
                return s.T(
                    $"❌ *Shop Code not found.*\n\n*{code}* is not recognised.\n\n👉 Check and try again.\nExample: *12345678*",
                    $"❌ *শপ কোড পাওয়া যায়নি।*\n\n*{code}* সঠিক নয়।\n\n👉 আবার চেষ্টা করুন।\nউদাহরণ: *12345678*",
                    $"❌ *शॉप कोड नहीं मिला।*\n\n*{code}* सही नहीं।\n\n👉 पुनः प्रयास करें।\nउदाहरण: *12345678*");

            s.ShopVerified = true;
            s.ShopCode = code;
            s.ShopUserId = shop.Value.Id;

            // ── Store as "OwnerName | SiteName" so a single field carries both ──
            // e.g.  "Mr Anas | GARMASHA GOURMENT CAFETERIA"
            // Downstream services (CRM description, etc.) already use ShopName as a
            // display string, so the pipe-delimited format is purely additive.
            var ownerTitleCase = System.Globalization.CultureInfo.InvariantCulture
                .TextInfo.ToTitleCase((shop.Value.OwnerName ?? "").ToLowerInvariant()).Trim();
            s.ShopName = string.IsNullOrWhiteSpace(ownerTitleCase)
                ? shop.Value.SiteName
                : $"{ownerTitleCase} | {shop.Value.SiteName}";

            Transition(s, "MAIN_MENU");

            // ── Parse owner name back out of ShopName for the greeting ───────────
            var displayOwner = ExtractOwnerFromShopName(s.ShopName);

            var greeting = string.IsNullOrWhiteSpace(displayOwner)
                ? s.T("✅ *Shop Verified! Welcome to*",
                      "✅ *শপ যাচাই হয়েছে! স্বাগতম*",
                      "✅ *दुकान सत्यापित! स्वागत है*")
                : s.T($"✅ *Hi, {displayOwner}!* Welcome to",
                      $"✅ *হ্যালো, {displayOwner}!* স্বাগতম",
                      $"✅ *नमस्ते, {displayOwner}!* स्वागत है");

            return s.T(
                $"{greeting}\n*PRAN-RFL UAE Sales Support*\n\n{BuildMainMenuBody("en")}",
                $"{greeting}\n*PRAN-RFL UAE Sales Support*\n\n{BuildMainMenuBody("bn")}",
                $"{greeting}\n*PRAN-RFL UAE Sales Support*\n\n{BuildMainMenuBody("hi")}");
        }

        // ── Returns the owner portion of "OwnerName | SiteName", or empty string ─
        private static string ExtractOwnerFromShopName(string? shopName)
        {
            if (string.IsNullOrWhiteSpace(shopName)) return string.Empty;
            var pipeIdx = shopName.IndexOf(" | ", StringComparison.Ordinal);
            return pipeIdx > 0 ? shopName[..pipeIdx].Trim() : string.Empty;
        }

        private async Task<(string SiteName, string Id, string OwnerName)?> ValidateShopAsync(string shopCode)
        {
            try
            {
                var token = _config["Spror:BearerToken"] ?? "224|IEcNubBv4Z9LoXpngVuHthRrSDdIlD0B4RGxNFqT";
                var contName = _config["Spror:ContName"] ?? "United Arab Emirates";
                var baseUrl = _config["Spror:BaseUrl"] ?? "https://spror.prgfms.com/api/v1";

                var client = _httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");

                var resp = await client.PostAsJsonAsync(
                    $"{baseUrl}/retail/shopDetails",
                    new { shop_code = shopCode, cont_name = contName });

                if (!resp.IsSuccessStatusCode) return null;

                var json = await resp.Content.ReadAsStringAsync();
                _logger.LogDebug("[UAE] ValidateShop response: {J}", json.Length > 200 ? json[..200] : json);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("status", out var st) || !st.GetBoolean())
                    return null;

                if (!root.TryGetProperty("data", out var dataEl) ||
                    dataEl.ValueKind != JsonValueKind.Array ||
                    dataEl.GetArrayLength() == 0) return null;

                var shop = dataEl[0];
                var id = shop.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
                var siteName = shop.TryGetProperty("site_name", out var snEl) ? snEl.GetString() ?? "" : "";
                var ownerName = shop.TryGetProperty("site_ownm", out var ownEl) ? ownEl.GetString() ?? "" : "";

                return string.IsNullOrEmpty(id) ? null : (siteName, id, ownerName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UAE] ValidateShop failed for {Code}", shopCode);
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // MAIN MENU
        // ─────────────────────────────────────────────────────────────────────

        private string BuildMainMenu(UaeSession s)
        {
            Transition(s, "MAIN_MENU");
            return BuildMainMenuBody(s.Lang ?? "en");
        }

        private static string BuildMainMenuBody(string lang) => lang switch
        {
            "bn" =>
               
                "1️⃣  অর্ডার দিন\n" +
                "2️⃣  রিটার্ন / রিপ্লেসমেন্ট\n" +
                "3️⃣  অভিযোগ / ফিডব্যাক\n" +
                "4️⃣  সাপোর্ট এজেন্ট\n" +
                "0️⃣  ভাষা পরিবর্তন\n\n" +
                "👉 *1*, *2*, *3*, *4* বা *0* পাঠান।",
            "hi" =>
               
                "1️⃣  ऑर्डर करें\n" +
                "2️⃣  वापसी / प्रतिस्थापन\n" +
                "3️⃣  शिकायत / फ़ीडबैक\n" +
                "4️⃣  सपोर्ट एजेंट\n" +
                "0️⃣  भाषा बदलें\n\n" +
                "👉 *1*, *2*, *3*, *4* या *0* भेजें।",
            _ =>
                
                "1️⃣  Place Order\n" +
                "2️⃣  Return / Replacement\n" +
                "3️⃣  Complaint / Feedback\n" +
                "4️⃣  Connect with Support Agent\n" +
                "0️⃣  Change Language\n\n" +
                "👉 Reply *1*, *2*, *3*, *4* or *0*.",
        };

        private async Task<string> HandleMainMenu(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.MsgType != "text") return BuildUnknown(s);
            if (msg.RawText == "1") return StartPlaceOrder(s);
            if (msg.RawText == "2") return StartReturn(s);
            if (msg.RawText == "3") return StartComplaint(s);
            if (msg.RawText == "4") return StartAgent(s);
            if (msg.RawText == "0") return ResetToLang(s);
            return BuildUnknown(s);
        }

        // ─────────────────────────────────────────────────────────────────────
        // FLOW 1 — PLACE ORDER
        // ─────────────────────────────────────────────────────────────────────

        private string StartPlaceOrder(UaeSession s)
        {
            Transition(s, "AWAITING_ORDER_CONFIRM");
            return s.T(
                "🛒 *Place Order*\n\n" +
                "Our sales team will contact you to take your order.\n\n" +
                "Send *Y* to Confirm\n" +
                "Send *N* to Cancel\n\n" +
                "👉 Send *0* to go back to main menu",

                "🛒 *অর্ডার দিন*\n\n" +
                "আমাদের সেলস টিম আপনার অর্ডার নিতে যোগাযোগ করবে।\n\n" +
                "নিশ্চিত করতে *Y* পাঠান\n" +
                "বাতিল করতে *N* পাঠান\n\n" +
                "👉 মূল মেনুতে যেতে *0* পাঠান",

                "🛒 *ऑर्डर करें*\n\n" +
                "हमारी सेल्स टीम आपका ऑर्डर लेने के लिए संपर्क करेगी।\n\n" +
                "*Y* भेजें पुष्टि के लिए\n" +
                "*N* भेजें रद्द करने के लिए\n\n" +
                "👉 मुख्य मेनू पर जाने के लिए *0* भेजें");
        }

        private async Task<string> HandleOrderConfirmAsync(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.RawText == "n" || msg.RawText == "0") return BuildMainMenu(s);
            if (msg.RawText != "y") return StartPlaceOrder(s);

            var req = new UaeCrmRequest
            {
                ShopCode = s.ShopCode ?? "",
                WhatsappNumber = s.Phone,
                TicketType = "PLACE_ORDER",
                Description = $"Place order request from shop: {s.ShopName ?? s.ShopCode}",
            };

            var result = await _crm.SubmitAsync(req);
            Transition(s, "MAIN_MENU");

            return result.Success
                ? s.T(
                    "✅ *Order Request Submitted*\n\n" +
                    (result.TicketId != null ? $"Ticket ID : *{result.TicketId}*\n\n" : "") +
                    "Our sales team will contact you shortly to take your order.\n\n" +
                    "👉 Send *menu* for Main Menu\n",

                    "✅ *অর্ডার রিকোয়েস্ট জমা হয়েছে*\n\n" +
                    (result.TicketId != null ? $"টিকেট আইডি : *{result.TicketId}*\n\n" : "") +
                    "আমাদের সেলস টিম শীঘ্রই অর্ডার নিতে যোগাযোগ করবে।\n\n" +
                    "👉 *menu* — মূল মেনু\n",

                    "✅ *ऑर्डर अनुरोध जमा हुआ*\n\n" +
                    (result.TicketId != null ? $"टिकट ID : *{result.TicketId}*\n\n" : "") +
                    "हमारी सेल्स टीम जल्द आपसे संपर्क कर ऑर्डर लेगी।\n\n" +
                    "👉 *menu* — मुख्य मेनू\n")
                : s.T(
                    $"❌ Request failed.\n{result.Error}\n\nSend *Y* to retry or *menu* for main menu.",
                    $"❌ ব্যর্থ।\n{result.Error}\n\n*Y* পাঠিয়ে আবার চেষ্টা করুন।",
                    $"❌ विफल।\n{result.Error}\n\n*Y* भेजें पुनः प्रयास के लिए।");
        }

        // ─────────────────────────────────────────────────────────────────────
        // FLOW 2 — RETURN / REPLACEMENT
        // ─────────────────────────────────────────────────────────────────────

        private string StartReturn(UaeSession s)
        {
            ClearMedia(s);
            Transition(s, "AWAITING_RETURN_DETAILS");
            return s.T(
                "🔄 *Return / Replacement*\n\n" +
                "Tell us the product you want to return.\n\n" +
                "Send *Text*, *Image*, or *Voice*\n\n" +
                "👉 Send *0* to go back to main menu",

                "🔄 *রিটার্ন / রিপ্লেসমেন্ট*\n\n" +
                "যে পণ্যটি ফেরত দিতে চান তা জানান।\n\n" +
                "*টেক্সট*, *ছবি* বা *ভয়েস* পাঠান\n\n" +
                "👉 মূল মেনুতে ফিরতে *0* পাঠান",

                "🔄 *वापसी / प्रतिस्थापन*\n\n" +
                "जो उत्पाद वापस करना है उसके बारे में बताएं।\n\n" +
                "*टेक्स्ट*, *फ़ोटो* या *आवाज़* भेजें\n\n" +
                "👉 मुख्य मेनू पर जाने के लिए *0* भेजें");
        }

        private async Task<string> HandleReturnConfirmAsync(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.RawText == "y") return await SubmitMediaAsync(s, "PRODUCT_REPLACEMENT");
            if (msg.RawText == "n") { ClearMedia(s); return StartReturn(s); }
            Transition(s, "AWAITING_RETURN_DETAILS");
            return await HandleMediaDetailsAsync(s, msg, "return");
        }

        // ─────────────────────────────────────────────────────────────────────
        // FLOW 3 — COMPLAINT / FEEDBACK
        // ─────────────────────────────────────────────────────────────────────

        private string StartComplaint(UaeSession s)
        {
            ClearMedia(s);
            Transition(s, "AWAITING_COMPLAINT_DETAILS");
            return s.T(
                "📝 *Complaint / Feedback*\n\n" +
                "Tell us your problem.\n\n" +
                "Send *Text*, *Image*, or *Voice*\n\n" +
                "👉 Send *0* to go back to main menu",

                "📝 *অভিযোগ / ফিডব্যাক*\n\n" +
                "আপনার সমস্যা জানান।\n\n" +
                "*টেক্সট*, *ছবি* বা *ভয়েস* পাঠান\n\n" +
                "👉 মূল মেনুতে ফিরতে *0* পাঠান",

                "📝 *शिकायत / फ़ीडबैक*\n\n" +
                "अपनी समस्या बताएं।\n\n" +
                "*टेक्स्ट*, *फ़ोटो* या *आवाज़* भेजें\n\n" +
                "👉 मुख्य मेनू पर जाने के लिए *0* भेजें");
        }

        private async Task<string> HandleComplaintConfirmAsync(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.RawText == "y") return await SubmitMediaAsync(s, "COMPLAIN");
            if (msg.RawText == "n") { ClearMedia(s); return StartComplaint(s); }
            Transition(s, "AWAITING_COMPLAINT_DETAILS");
            return await HandleMediaDetailsAsync(s, msg, "complaint");
        }

        // ─────────────────────────────────────────────────────────────────────
        // SHARED MEDIA HANDLER (Return + Complaint)
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> HandleMediaDetailsAsync(
            UaeSession s, UaeIncomingMessage msg, string flowType)
        {
            var confirmState = flowType == "return"
                ? "AWAITING_RETURN_CONFIRM"
                : "AWAITING_COMPLAINT_CONFIRM";

            if (msg.MsgType == "text")
            {
                if (msg.RawText == "0") return BuildMainMenu(s);
                s.MediaDescription = string.IsNullOrWhiteSpace(s.MediaDescription)
                    ? msg.RawText
                    : s.MediaDescription + "\n" + msg.RawText;
            }
            else if (msg.MsgType == "image")
            {
                var imageId = await SaveMediaToDiskAsync(
                    msg.MessageId, msg.ImageId, msg.ImageMime,
                    msg.From, msg.SenderName, msg.Timestamp, "images",
                    caption: msg.ImageCaption);
                if (imageId != null)
                    s.MediaImages.Add(imageId);
                else
                    return s.T(
                        "⚠️ Image could not be uploaded. Please try again.",
                        "⚠️ ছবি আপলোড হয়নি। আবার পাঠান।",
                        "⚠️ फ़ोटो अपलोड नहीं हुई। पुनः भेजें।");

                // ── Confirm message burst suppression ──────────────────────────
                // "confirm:{phone}" key — separate from "ack:{phone}" in GetAckMessage.
                // Ensures only ONE "✅ Received" is sent per gallery batch (5s window).
                {
                    var now = msg.Timestamp > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(msg.Timestamp).UtcDateTime
                        : DateTime.UtcNow;
                    var confirmKey = $"confirm:{s.Phone}";
                    var isBurst = _state.LastImageTime.TryGetValue(confirmKey, out var last)
                        && Math.Abs((now - last).TotalSeconds) <= 5;
                    _state.LastImageTime[confirmKey] = now;
                    if (isBurst) return string.Empty;
                }
            }
            else if (msg.MsgType == "audio")
            {
                var voiceId = await SaveMediaToDiskAsync(
                    msg.MessageId, msg.AudioId, msg.AudioMime,
                    msg.From, msg.SenderName, msg.Timestamp, "audio");
                if (voiceId != null)
                    s.MediaVoices.Add(voiceId);
                else
                    return s.T(
                        "⚠️ Voice note could not be uploaded. Please try again.",
                        "⚠️ ভয়েস আপলোড হয়নি। আবার পাঠান।",
                        "⚠️ आवाज़ अपलोड नहीं हुई। पुनः भेजें।");
            }
            else
            {
                return string.Empty;
            }

            Transition(s, confirmState);

            return s.T(
                "✅ *Received.*\n\n" +
                "Send *Y* to submit\n" +
                "Send *N* to cancel\n\n" +
                "To add more details, send another *Image*, *Voice* or *Text*",

                "✅ *পাওয়া গেছে।*\n\n" +
                "*Y* পাঠান জমা দিতে\n" +
                "*N* পাঠান বাতিল করতে\n\n" +
                "আরও যোগ করতে *ছবি*, *ভয়েস* বা *টেক্সট* পাঠান",

                "✅ *प्राप्त हुआ।*\n\n" +
                "जमा करने के लिए *Y* भेजें\n" +
                "रद्द करने के लिए *N* भेजें\n\n" +
                "अधिक जोड़ने के लिए *फ़ोटो*, *आवाज़* या *टेक्स्ट* भेजें");
        }

        private async Task<string> SubmitMediaAsync(UaeSession s, string ticketType)
        {
            var req = new UaeCrmRequest
            {
                ShopCode = s.ShopCode ?? "",
                WhatsappNumber = s.Phone,
                Description = s.MediaDescription,
                Images = new(s.MediaImages),
                VoiceFiles = new(s.MediaVoices),
                TicketType = ticketType,
            };

            var result = await _crm.SubmitAsync(req);
            ClearMedia(s);
            Transition(s, "MAIN_MENU");

            if (!result.Success)
                return s.T(
                    $"❌ Submission failed.\n{result.Error}\n\nSend *Y* to retry.",
                    $"❌ জমা ব্যর্থ।\n{result.Error}",
                    $"❌ जमा विफल।\n{result.Error}");

            var ticketLabel = ticketType == "PRODUCT_REPLACEMENT"
                ? s.T("Return Request", "রিটার্ন রিকোয়েস্ট", "वापसी अनुरोध")
                : s.T("Complaint", "অভিযোগ", "शिकायत");

            return s.T(
                $"✅ *{ticketLabel} Submitted*\n\n" +
                (result.TicketId != null ? $"Ticket ID : *{result.TicketId}*\n\n" : "") +
                "Our team will contact you shortly.\n\n" +
                "👉 Send *menu* for Main Menu\n",

                $"✅ *{ticketLabel} জমা হয়েছে*\n\n" +
                (result.TicketId != null ? $"টিকেট আইডি : *{result.TicketId}*\n\n" : "") +
                "আমাদের টিম শীঘ্রই যোগাযোগ করবে।\n\n" +
                "👉 *menu* — মূল মেনু\n" ,

                $"✅ *{ticketLabel} जमा हुआ*\n\n" +
                (result.TicketId != null ? $"टिकट ID : *{result.TicketId}*\n\n" : "") +
                "हमारी टीम जल्द संपर्क करेगी।\n\n" +
                "👉 *menu* — मुख्य मेनू\n" );
        }

        // ─────────────────────────────────────────────────────────────────────
        // FLOW 4 — CONNECT WITH SUPPORT AGENT
        // ─────────────────────────────────────────────────────────────────────

        private string StartAgent(UaeSession s)
        {
            Transition(s, "AWAITING_AGENT_CONFIRM_1");
            return BuildAgentConfirm1(s);
        }

        private string BuildAgentConfirm1(UaeSession s) =>
            s.T(
                "📞 *Connect with Support Agent*\n\n" +
                "Our support agent will contact you after confirmation.\n\n" +
                "Send *Y* to Confirm\n" +
                "Send *N* to Cancel\n\n" +
                "👉 Send *0* to go back to main menu",

                "📞 *সাপোর্ট এজেন্ট*\n\n" +
                "নিশ্চিত করলে এজেন্ট আপনার সাথে যোগাযোগ করবে।\n\n" +
                "নিশ্চিত করতে *Y* পাঠান\n" +
                "বাতিল করতে *N* পাঠান\n\n" +
                "👉 মূল মেনুতে যেতে *0* পাঠান",

                "📞 *सपोर्ट एजेंट*\n\n" +
                "पुष्टि के बाद हमारा एजेंट आपसे संपर्क करेगा।\n\n" +
                "*Y* भेजें पुष्टि करने के लिए\n" +
                "*N* भेजें रद्द करने के लिए\n\n" +
                "👉 मुख्य मेनू पर जाने के लिए *0* भेजें");

        private async Task<string> HandleAgentConfirm1Async(
            UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.RawText == "y") return await ConnectAgentAsync(s);
            if (msg.RawText == "n" || msg.RawText == "0") return BuildMainMenu(s);
            return BuildAgentConfirm1(s);
        }

        private async Task<string> ConnectAgentAsync(UaeSession s)
        {
            var req = new UaeCrmRequest
            {
                ShopCode = s.ShopCode ?? "",
                WhatsappNumber = s.Phone,
                TicketType = "CONNECT_TO_AGENT",
                Description = $"User requested live agent support. Shop: {s.ShopName ?? s.ShopCode}",
            };

            var result = await _crm.SubmitAsync(req);
            Transition(s, "MAIN_MENU");

            return result.Success
                ? s.T(
                    "✅ *Agent Request Submitted*\n\n" +
                    (result.TicketId != null ? $"Ticket ID : *{result.TicketId}*\n\n" : "") +
                    "A support agent will contact you shortly.\n\n" +
                    "👉 Send *menu* for Main Menu",

                    "✅ *অনুরোধ পাঠানো হয়েছে*\n\n" +
                    (result.TicketId != null ? $"টিকেট আইডি : *{result.TicketId}*\n\n" : "") +
                    "একজন এজেন্ট শীঘ্রই যোগাযোগ করবে।\n\n" +
                    "👉 *menu* — মূল মেনু",

                    "✅ *अनुरोध भेजा गया*\n\n" +
                    (result.TicketId != null ? $"टिकट ID : *{result.TicketId}*\n\n" : "") +
                    "एक एजेंट जल्द आपसे संपर्क करेगा।\n\n" +
                    "👉 *menu* — मुख्य मेनू")
                : s.T(
                    $"❌ Request failed.\n{result.Error}\n\nSend *S* to retry.",
                    $"❌ ব্যর্থ।\n{result.Error}",
                    $"❌ विफल।\n{result.Error}");
        }

        // ─────────────────────────────────────────────────────────────────────
        // WELCOME WITH LOGO
        // ─────────────────────────────────────────────────────────────────────

        private async Task SendWelcomeAsync(string phone, CancellationToken ct = default)
        {
            var baseUrl = _config["App:BaseUrl"]?.TrimEnd('/') ?? "https://webhook.prangroup.com";
            var logoUrl = $"{baseUrl}/images/pran-rfl-logo.jpg";
            await _dialog.SendImageAsync(phone, logoUrl, LangPrompt(), ct);
        }

        private static string LangPrompt() =>
            "👋 Hi! I'm *PRAN-RFL UAE Sales Support*\n\n" +
            "Please choose your language:\n\n" +
            "1️⃣  English\n" +
            "2️⃣  বাংলা\n" +
            "3️⃣  हिंदी\n\n" +
            "👉 Reply *1*, *2* or *3*.";

        // ─────────────────────────────────────────────────────────────────────
        // MEDIA SAVE
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string?> SaveMediaToDiskAsync(
            string messageId, string mediaId, string mimeType,
            string from, string senderName, long timestamp,
            string subFolder, string? caption = null)
        {
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                _logger.LogWarning("[UAE] SaveMedia skipped — empty mediaId msgId={Id}", messageId);
                return null;
            }
            if (string.IsNullOrWhiteSpace(_env.WebRootPath))
            {
                _logger.LogError("[UAE] SaveMedia failed — WebRootPath is null or empty");
                return null;
            }
            try
            {
                _logger.LogInformation("[UAE] Downloading media mediaId={Id} type={T}", mediaId, subFolder);
                var (bytes, mime) = await _dialog.DownloadMediaAsync(mediaId, mimeType);
                _logger.LogInformation("[UAE] Downloaded {B} bytes mime={M}", bytes.Length, mime);

                var ext = MimeToExt(mime, subFolder == "audio" ? ".ogg" : ".jpg");
                var fileName = $"{messageId}{ext}";
                var folder = Path.Combine(_env.WebRootPath, "wa-media", subFolder);
                Directory.CreateDirectory(folder);
                var filePath = Path.Combine(folder, fileName);
                await File.WriteAllBytesAsync(filePath, bytes);
                _logger.LogInformation("[UAE] Saved to {Path}", filePath);

                var baseUrl = _config["App:BaseUrl"] ?? "https://webhook.prangroup.com";
                var fileUrl = $"{baseUrl}/wa-media/{subFolder}/{fileName}";
                try
                {
                    await _msgRepo.InsertAsync(new WhatsAppMessage
                    {
                        MessageId = messageId,
                        FromNumber = from,
                        SenderName = senderName,
                        MessageType = subFolder == "audio" ? "audio" : "image",
                        MimeType = mime,
                        Caption = caption,
                        FileUrl = fileUrl,
                        FileSizeBytes = bytes.Length,
                        WaTimestamp = timestamp,
                        Status = "processed",
                        ProcessedAt = DateTime.UtcNow,
                    });
                }
                catch (Exception dbEx) when (
                    dbEx.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                    dbEx.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("[UAE] Media duplicate skipped: {Id}", messageId);
                }
                catch (Exception dbEx)
                {
                    _logger.LogWarning(dbEx, "[UAE] Media DB insert failed (file saved OK): {Id}", messageId);
                }

                // Return full disk path — UaeCrmService reads bytes from here
                return filePath;
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx,
                    "[UAE] 360dialog download failed mediaId={Id}: {Msg}", mediaId, httpEx.Message);
                return null;
            }
            catch (IOException ioEx)
            {
                _logger.LogError(ioEx,
                    "[UAE] Disk write failed wa-media/{Sub}: {Msg}", subFolder, ioEx.Message);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[UAE] SaveMedia failed msgId={Id} mediaId={MId}", messageId, mediaId);
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // SESSION CACHE
        // ─────────────────────────────────────────────────────────────────────

        private async Task<UaeSession> LoadSessionAsync(string phone)
        {
            if (_cache.TryGetValue($"uae:{phone}", out UaeSession? cached) && cached != null)
                return cached;

            var row = await _sessionSvc.GetSessionAsync(phone);
            var session = UaeSession.Load(phone, row.TempData);
            if (session.State == "INIT" && row.CurrentStep != "INIT")
                session.State = row.CurrentStep;

            _cache.Set($"uae:{phone}", session,
                new MemoryCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(60)));
            return session;
        }

        private async Task PersistSessionAsync(UaeSession s, string rawText)
        {
            _cache.Set($"uae:{s.Phone}", s,
                new MemoryCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromMinutes(60)));
            try
            {
                await _sessionSvc.UpsertSessionAsync(new UpsertSessionRequestDto
                {
                    Phone = s.Phone,
                    CurrentStep = s.State,
                    PreviousStep = s.PreviousState,
                    TempData = s.Save(),
                    RawMessage = rawText,
                });
                _logger.LogInformation("[UAE] PersistSession OK phone={Phone} step={Step}", s.Phone, s.State);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UAE] PersistSession FAILED phone={Phone} step={Step} error={Msg} inner={Inner}",
                    s.Phone, s.State, ex.Message, ex.InnerException?.Message ?? "none");
            }
        }

     

        private static void Transition(UaeSession s, string newState)
        {
            s.PreviousState = s.State;
            s.State = newState;
        }

        private static void ClearMedia(UaeSession s)
        {
            s.MediaDescription = string.Empty;
            s.MediaImages = new();
            s.MediaVoices = new();
        }

        private static void ResetSession(UaeSession s)
        {
            s.State = "INIT";
            s.PreviousState = "INIT";
            s.Lang = null;
            ClearMedia(s);
        }

        private string ResetToLang(UaeSession s)
        {
            s.Lang = null;
            Transition(s, "AWAITING_LANG");
            return LangPrompt();
        }

        private string BuildUnknown(UaeSession s) =>
            s.T(
                "❌ *Invalid input.*\n\n👉 Send *menu* to go to Main Menu.",
                "❌ *অবৈধ ইনপুট।*\n\n👉 *menu* পাঠান।",
                "❌ *अमान्य इनपुट।*\n\n👉 *menu* भेजें।");

        private static string MimeToExt(string mime, string fallback) => mime switch
        {
            "audio/ogg" => ".ogg",
            "audio/mpeg" => ".mp3",
            "audio/wav" => ".wav",
            "audio/opus" => ".opus",
            "audio/mp4" => ".m4a",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => fallback
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MESSAGE PARSER
    // ─────────────────────────────────────────────────────────────────────────

    public class UaeIncomingMessage
    {
        public string From { get; set; } = "";
        public string SenderName { get; set; } = "";
        public string MessageId { get; set; } = "";
        public string MsgType { get; set; } = "text";
        public long Timestamp { get; set; }
        public string RawText { get; set; } = "";
        public string AudioId { get; set; } = "";
        public string AudioMime { get; set; } = "audio/ogg";
        public string ImageId { get; set; } = "";
        public string ImageMime { get; set; } = "image/jpeg";
        public string ImageCaption { get; set; } = "";
    }

    public static class UaeMessageParser
    {
        public static UaeIncomingMessage? Parse(JsonElement body)
        {
            try
            {
                JsonElement? msgEl = null;
                string sender = string.Empty;

                if (body.TryGetProperty("entry", out var entries) &&
                    entries.GetArrayLength() > 0)
                {
                    var value = entries[0].GetProperty("changes")[0].GetProperty("value");
                    if (value.TryGetProperty("statuses", out _) &&
                        !value.TryGetProperty("messages", out _))
                        return null;
                    if (value.TryGetProperty("messages", out var msgs) &&
                        msgs.GetArrayLength() > 0)
                        msgEl = msgs[0];
                    if (value.TryGetProperty("contacts", out var contacts) &&
                        contacts.GetArrayLength() > 0 &&
                        contacts[0].TryGetProperty("profile", out var profile) &&
                        profile.TryGetProperty("name", out var nameEl))
                        sender = nameEl.GetString() ?? "";
                }
                else if (body.TryGetProperty("messages", out var directMsgs) &&
                         directMsgs.GetArrayLength() > 0)
                {
                    msgEl = directMsgs[0];
                    if (body.TryGetProperty("contacts", out var c) &&
                        c.GetArrayLength() > 0 &&
                        c[0].TryGetProperty("profile", out var p) &&
                        p.TryGetProperty("name", out var n))
                        sender = n.GetString() ?? "";
                }

                if (msgEl is null) return null;
                var msg = msgEl.Value;

                var from = S(msg, "from");
                var msgType = S(msg, "type");
                var msgId = S(msg, "id");
                var ts = long.TryParse(S(msg, "timestamp"), out var t) ? t : 0L;

                if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(msgType)) return null;

                string rawText = string.Empty;
                if (msgType == "text" &&
                    msg.TryGetProperty("text", out var textEl) &&
                    textEl.TryGetProperty("body", out var bodyEl))
                {
                    rawText = System.Text.RegularExpressions.Regex.Replace(
                        (bodyEl.GetString() ?? "").Trim().ToLowerInvariant(),
                        @"[\u200B-\u200D\uFEFF]", "");
                }

                string audioId = "", audioMime = "audio/ogg";
                if (msgType == "audio" && msg.TryGetProperty("audio", out var audio))
                {
                    audioId = S(audio, "id");
                    audioMime = S(audio, "mime_type") is { Length: > 0 } m ? m : "audio/ogg";
                }

                string imageId = "", imageMime = "image/jpeg", imageCap = "";
                if (msgType == "image" && msg.TryGetProperty("image", out var image))
                {
                    imageId = S(image, "id");
                    imageMime = S(image, "mime_type") is { Length: > 0 } m ? m : "image/jpeg";
                    imageCap = S(image, "caption");
                }

                return new UaeIncomingMessage
                {
                    From = from,
                    SenderName = sender,
                    MessageId = msgId,
                    MsgType = msgType,
                    Timestamp = ts,
                    RawText = rawText,
                    AudioId = audioId,
                    AudioMime = audioMime,
                    ImageId = imageId,
                    ImageMime = imageMime,
                    ImageCaption = imageCap,
                };
            }
            catch { return null; }
        }

        private static string S(JsonElement el, string key) =>
            el.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
    }
}
