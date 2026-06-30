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
        private readonly IBotCatalogService _catalog;

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
            IBotCatalogService catalog,
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
            _catalog = catalog;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ENTRY POINT
        // ─────────────────────────────────────────────────────────────────────

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

        // ─────────────────────────────────────────────────────────────────────
        // ACK MESSAGES
        // ─────────────────────────────────────────────────────────────────────

        private string? GetAckMessage(UaeSession s, UaeIncomingMessage msg)
        {
            if (s.State == "AWAITING_SHOP_CODE" && msg.MsgType == "text"
                && !ResetKeywords.Contains(msg.RawText))
                return s.T(
                    "🔍 Verifying shop...",
                    "🔍 শপ যাচাই করা হচ্ছে...",
                    "🔍 கடையை சரிபார்க்கிறோம்...",
                    "🔍 正在验证商店...",
                    "🔍 Mengesahkan kedai...");

            // ── Gallery burst suppression for ACK ──────────────────────────────
            if ((s.State == "AWAITING_RETURN_DETAILS" || s.State == "AWAITING_COMPLAINT_DETAILS"
                 || s.State == "AWAITING_RETURN_CONFIRM" || s.State == "AWAITING_COMPLAINT_CONFIRM")
                && (msg.MsgType == "image" || msg.MsgType == "audio"))
            {
                var ackNow = msg.Timestamp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(msg.Timestamp).UtcDateTime
                    : DateTime.UtcNow;
                var ackKey = $"ack:{s.Phone}";
                if (_state.LastImageTime.TryGetValue(ackKey, out var lastAck)
                    && Math.Abs((ackNow - lastAck).TotalSeconds) <= 5)
                    return null;
                _state.LastImageTime[ackKey] = ackNow;
                return s.T(
                    "⏳ Uploading media...",
                    "⏳ মিডিয়া আপলোড হচ্ছে...",
                    "⏳ மீடியா பதிவேற்றுகிறோம்...",
                    "⏳ 正在上传媒体...",
                    "⏳ Memuat naik media...");
            }

            if (s.State == "AWAITING_ORDER_CONFIRM" && msg.RawText == "y")
                return s.T(
                    "⏳ Placing order...",
                    "⏳ অর্ডার দেওয়া হচ্ছে...",
                    "⏳ ஆர்டர் செய்கிறோம்...",
                    "⏳ 正在下单...",
                    "⏳ Membuat pesanan...");

            if (s.State == "AWAITING_COMPLAINT_CONFIRM" && msg.RawText == "y")
                return s.T(
                    "⏳ Submitting complaint...",
                    "⏳ অভিযোগ জমা হচ্ছে...",
                    "⏳ புகாரை சமர்ப்பிக்கிறோம்...",
                    "⏳ 正在提交投诉...",
                    "⏳ Menghantar aduan...");

            if (s.State == "AWAITING_RETURN_CONFIRM" && msg.RawText == "y")
                return s.T(
                    "⏳ Submitting return request...",
                    "⏳ রিটার্ন জমা হচ্ছে...",
                    "⏳ திரும்பப்பெறும் கோரிக்கையை சமர்ப்பிக்கிறோம்...",
                    "⏳ 正在提交退货请求...",
                    "⏳ Menghantar permintaan pemulangan...");

            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // ROUTER
        // ─────────────────────────────────────────────────────────────────────

        private static readonly HashSet<string> MenuKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "menu",
            "মেনু",
            "மெனு",
            "菜单",
        };

        private static readonly HashSet<string> ResetKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "hi", "hello", "start", "hey", "new",
            "হ্যালো", "শুরু",
            "வணக்கம்", "தொடங்கு",
            "你好", "开始",
            "helo", "mula", "selamat",
        };

        private async Task<string> RouteAsync(UaeSession s, UaeIncomingMessage msg)
        {
            var raw = msg.RawText;

            // ── Cart order webhook — handled regardless of session state ──────
            if (msg.MsgType == "order" && msg.CartItems.Count > 0)
                return await HandleCartOrderAsync(s, msg);

            // ── INIT or reset keywords: funnel ALL first messages through shop code handler ──
            if (s.State == "INIT" || (msg.MsgType == "text" && ResetKeywords.Contains(raw)))
            {
                ResetSession(s);
                Transition(s, "AWAITING_SHOP_CODE");
                return await HandleShopCodeAsync(s, msg);
            }

            // ── menu keyword: available to ALL users once past shop/lang steps ──
            if (msg.MsgType == "text" && MenuKeywords.Contains(raw)
                && s.State != "AWAITING_SHOP_CODE" && s.State != "AWAITING_LANG")
                return BuildMainMenu(s);

            // ── "s" shortcut: verified users only ────────────────────────────
            if (s.ShopVerified && msg.MsgType == "text" && raw == "s")
                return await ConnectAgentAsync(s);

            return s.State switch
            {
                "AWAITING_LANG" => await HandleLangAsync(s, msg),
                "AWAITING_SHOP_CODE" => await HandleShopCodeAsync(s, msg),
                "MAIN_MENU" => await HandleMainMenu(s, msg),
                "AWAITING_RETURN_DETAILS" => await HandleMediaDetailsAsync(s, msg, "return"),
                "AWAITING_RETURN_CONFIRM" => await HandleReturnConfirmAsync(s, msg),
                "AWAITING_COMPLAINT_DETAILS" => await HandleMediaDetailsAsync(s, msg, "complaint"),
                "AWAITING_COMPLAINT_CONFIRM" => await HandleComplaintConfirmAsync(s, msg),
                _ => BuildMainMenu(s),
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // LANGUAGE SELECTION
        // ─────────────────────────────────────────────────────────────────────

        private Task<string> HandleLangAsync(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.MsgType != "text") return Task.FromResult(LangPrompt());

            switch (msg.RawText.Trim())
            {
                case "1": s.Lang = "en"; break;
                case "2": s.Lang = "bn"; break;
                case "3": s.Lang = "ta"; break;
                case "4": s.Lang = "zh"; break;
                case "5": s.Lang = "ms"; break;
                default:
                    return Task.FromResult("❌ Invalid. Reply *1*, *2*, *3*, *4* or *5*.\n\n" + LangPrompt());
            }

            Transition(s, "MAIN_MENU");
            var v = s.ShopVerified;
            return Task.FromResult(s.T(
                "✅ Language set to *English*.\n\n" + BuildMainMenuBody("en", v),
                "✅ ভাষা বাংলায় সেট হয়েছে।\n\n" + BuildMainMenuBody("bn", v),
                "✅ மொழி தமிழில் அமைக்கப்பட்டது.\n\n" + BuildMainMenuBody("ta", v),
                "✅ 语言已设置为中文。\n\n" + BuildMainMenuBody("zh", v),
                "✅ Bahasa ditetapkan kepada *Bahasa Melayu*.\n\n" + BuildMainMenuBody("ms", v)));
        }

        // ─────────────────────────────────────────────────────────────────────
        // SHOP AUTHENTICATION
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> HandleShopCodeAsync(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.MsgType != "text" || string.IsNullOrWhiteSpace(msg.RawText))
                return string.Empty;

            //var code = msg.RawText.Trim();
            var code = ExtractShopCode(msg.RawText);
            var baseUrl = _config["App:BaseUrl"]?.TrimEnd('/') ?? "https://webhook.prangroup.com";
            var logoUrl = $"{baseUrl}/images/pran-rfl-logo.jpg";

            // ── If the text is a greeting/reset keyword, skip validation entirely ──
            if (ResetKeywords.Contains(code))
            {
                s.ShopVerified = false;
                s.ShopCode = string.Empty;
                Transition(s, "AWAITING_LANG");
                var greetMsg = "👋 Hi! Welcome to *PRAN-RFL Malaysia Sales Support*\n\n" +
                               LangOptions();
                await _dialog.SendImageAsync(msg.From, logoUrl, greetMsg);
                return string.Empty;
            }

            // ── Normal path: treat input as a shop code and validate ──────────
            var shop = await ValidateShopAsync(code);

            if (shop == null)
            {
                s.ShopVerified = false;
                s.ShopCode = code;
                Transition(s, "AWAITING_LANG");
                var invalidMsg = $"❌ *Shop Code not found.* *{code}* is not recognised.\n\n" + LangPrompt();
                await _dialog.SendImageAsync(msg.From, logoUrl, invalidMsg);
                return string.Empty;
            }

            s.ShopVerified = true;
            s.ShopCode = code;
            s.ShopUserId = shop.Value.Id;

            var ownerTitleCase = System.Globalization.CultureInfo.InvariantCulture
                .TextInfo.ToTitleCase((shop.Value.OwnerName ?? "").ToLowerInvariant()).Trim();
            s.ShopName = string.IsNullOrWhiteSpace(ownerTitleCase)
                ? shop.Value.SiteName
                : $"{ownerTitleCase} | {shop.Value.SiteName}";

            Transition(s, "AWAITING_LANG");
            var displayOwner = ExtractOwnerFromShopName(s.ShopName);
            var welcomePrefix = string.IsNullOrWhiteSpace(displayOwner)
                ? "✅ *Shop Verified!*"
                : $"✅ *Hi, {displayOwner}!* Shop Verified.";
            var validMsg = $"{welcomePrefix}\n\n" + LangPrompt();
            await _dialog.SendImageAsync(msg.From, logoUrl, validMsg);
            return string.Empty;
        }

        private static string ExtractOwnerFromShopName(string? shopName)
        {
            if (string.IsNullOrWhiteSpace(shopName)) return string.Empty;
            var pipeIdx = shopName.IndexOf(" | ", StringComparison.Ordinal);
            return pipeIdx > 0 ? shopName[..pipeIdx].Trim() : string.Empty;
        }

        private static string ExtractShopCode(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText)) return rawText?.Trim() ?? "";

            // Matches "code: 123456", "code : 123456", "(code 123456)", "code-123456" etc.
            var match = System.Text.RegularExpressions.Regex.Match(
                rawText,
                @"code\s*[:\-]?\s*(\d{3,})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success)
                return match.Groups[1].Value.Trim();

            // Fallback: if the text has no "code" keyword but contains a long
            // numeric run (e.g. user just pastes the number alone), grab it.
            var numMatch = System.Text.RegularExpressions.Regex.Match(rawText, @"\d{4,}");
            if (numMatch.Success)
                return numMatch.Value.Trim();

            // No pattern matched — preserve original behaviour (whole text as code)
            return rawText.Trim();
        }

        private async Task<(string SiteName, string Id, string OwnerName)?> ValidateShopAsync(string shopCode)
        {
            try
            {
                var token = _config["Spror:BearerToken"] ?? "224|IEcNubBv4Z9LoXpngVuHthRrSDdIlD0B4RGxNFqT";
                var contName = _config["Spror:ContName"] ?? "Malaysia";
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
            return BuildMainMenuBody(s.Lang ?? "en", s.ShopVerified);
        }

        /// <summary>
        /// Verified shops see:   1=Place Order  2=Connect with Agent  0=Change Language
        /// Unverified shops see: 1=Connect with Agent  0=Change Language
        /// </summary>
        private static string BuildMainMenuBody(string lang, bool shopVerified = true)
        {
            // ── Unverified: agent + change language ───────────────────────────
            if (!shopVerified) return lang switch
            {
                "bn" =>
                    "1️⃣  সাপোর্ট এজেন্ট\n" +
                    "0️⃣  ভাষা পরিবর্তন\n\n" +
                    "👉 *1* বা *0* পাঠান।",
                "ta" =>
                    "1️⃣  ஆதரவு முகவர்\n" +
                    "0️⃣  மொழியை மாற்று\n\n" +
                    "👉 *1* அல்லது *0* அனுப்பவும்.",
                "zh" =>
                    "1️⃣  联系客服\n" +
                    "0️⃣  更改语言\n\n" +
                    "👉 请发送 *1* 或 *0*。",
                "ms" =>
                    "1️⃣  Hubungi Ejen Sokongan\n" +
                    "0️⃣  Tukar Bahasa\n\n" +
                    "👉 Balas *1* atau *0*.",
                _ =>
                    "1️⃣  Connect with Agent\n" +
                    "0️⃣  Change Language\n\n" +
                    "👉 Reply *1* or *0*.",
            };

            // ── Verified: full menu ───────────────────────────────────────────
            return lang switch
            {
                "bn" =>
                    "1️⃣  অর্ডার দিন\n" +
                    "2️⃣  সাপোর্ট এজেন্ট\n" +
                    "0️⃣  ভাষা পরিবর্তন\n\n" +
                    "👉 *1*, *2* বা *0* পাঠান।",
                "ta" =>
                    "1️⃣  ஆர்டர் செய்யுங்கள்\n" +
                    "2️⃣  ஆதரவு முகவர்\n" +
                    "0️⃣  மொழியை மாற்று\n\n" +
                    "👉 *1*, *2* அல்லது *0* அனுப்பவும்.",
                "zh" =>
                    "1️⃣  下单\n" +
                    "2️⃣  联系客服\n" +
                    "0️⃣  更改语言\n\n" +
                    "👉 请发送 *1*、*2* 或 *0*。",
                "ms" =>
                    "1️⃣  Buat Pesanan\n" +
                    "2️⃣  Hubungi Ejen Sokongan\n" +
                    "0️⃣  Tukar Bahasa\n\n" +
                    "👉 Balas *1*, *2* atau *0*.",
                _ =>
                    "1️⃣  Place Order\n" +
                    "2️⃣  Connect with Agent\n" +
                    "0️⃣  Change Language\n\n" +
                    "👉 Reply *1*, *2* or *0*.",
            };
        }

        /// <summary>
        /// Routes MAIN_MENU input.
        /// Verified:   1 = Place Order,  2 = Connect Agent,  0 = Change Language
        /// Unverified: 1 = Connect Agent,                    0 = Change Language
        /// </summary>
        private Task<string> HandleMainMenu(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.MsgType != "text") return Task.FromResult(BuildUnknown(s));

            // Re-display menu for any menu keyword (safety net)
            if (MenuKeywords.Contains(msg.RawText)) return Task.FromResult(BuildMainMenu(s));

            // ── 0: change language (available to all users) ───────────────────
            if (msg.RawText == "0")
            {
                s.Lang = null;
                Transition(s, "AWAITING_LANG");
                return Task.FromResult(LangPrompt());
            }

            if (!s.ShopVerified)
            {
                if (msg.RawText == "1") return ConnectAgentAsync(s);
                return Task.FromResult(BuildUnknown(s));
            }

            if (msg.RawText == "1") return Task.FromResult(StartPlaceOrder(s));
            if (msg.RawText == "2") return ConnectAgentAsync(s);
            return Task.FromResult(BuildUnknown(s));
        }

        // ─────────────────────────────────────────────────────────────────────
        // FLOW 1 — PLACE ORDER  (direct URL, no channel sub-menu)
        // ─────────────────────────────────────────────────────────────────────

        private string StartPlaceOrder(UaeSession s)
        {
            Transition(s, "MAIN_MENU");
            return s.T(
                $"🌐 *Place your order on our website:*\nhttps://myorder.prangroup.com/?cont_id=14&order=1&shopCode={s.ShopCode}\n\n" +
                "👉 Send *menu* for Main Menu",

                $"🌐 *আমাদের ওয়েবসাইটে অর্ডার করুন:*\nhttps://myorder.prangroup.com/?cont_id=14&order=1&shopCode={s.ShopCode}\n\n" +
                "👉 *মেনু* — মূল মেনু",

                $"🌐 *எங்கள் இணையதளத்தில் ஆர்டர் செய்யுங்கள்:*\nhttps://myorder.prangroup.com/?cont_id=14&order=1&shopCode={s.ShopCode}\n\n" +
                "👉 *மெனு* — முகப்பு மெனு",

                $"🌐 *请在我们的网站上下单：*\nhttps://myorder.prangroup.com/?cont_id=14&order=1&shopCode={s.ShopCode}\n\n" +
                "👉 *menu* — 主菜单",

                $"🌐 *Buat pesanan anda di laman web kami:*\nhttps://myorder.prangroup.com/?cont_id=14&order=1&shopCode={s.ShopCode}\n\n" +
                "👉 *menu* — Menu Utama");
        }

        // ─────────────────────────────────────────────────────────────────────
        // FLOW 1B — CART ORDER (inbound webhook type="order")
        // ─────────────────────────────────────────────────────────────────────

        private async Task<string> HandleCartOrderAsync(UaeSession s, UaeIncomingMessage msg)
        {
            _logger.LogInformation(
                "[UAE] Cart order from {Phone} — {Count} items, catalogId={Cat}",
                msg.From, msg.CartItems.Count, msg.OrderCatalogId);

            var nameMap = await _catalog.GetAllNamesAsync();

            var itemLines = msg.CartItems
                .Select(i =>
                {
                    var name = nameMap.TryGetValue(i.Sku, out var n) ? n : i.Sku;
                    return $"• {name} ({i.Sku}) × {i.Qty}" +
                           (i.Price > 0 ? $" @ {i.Price:F2} {i.Currency}" : "");
                })
                .ToList();

            var total = msg.CartItems.Sum(i => i.Price * i.Qty);
            var currency = msg.CartItems.FirstOrDefault()?.Currency ?? "MYR";

            var totalLine = total > 0
                ? $"\n\n*Total: {total:F2} {currency}*"
                : string.Empty;

            var description =
                $"WhatsApp Catalog Order — Shop: {s.ShopName ?? s.ShopCode}\n\n" +
                string.Join("\n", itemLines) +
                (total > 0 ? $"\n\nEstimated Total: {total:F2} {currency}" : "") +
                (string.IsNullOrWhiteSpace(msg.OrderText) ? "" : $"\n\nCustomer note: {msg.OrderText}") +
                $"\n\nCatalog ID: {msg.OrderCatalogId}";

            var req = new UaeCrmRequest
            {
                ShopCode = s.ShopCode ?? "",
                WhatsappNumber = s.Phone,
                TicketType = "PLACE_ORDER",
                Description = description,
                CartItems = string.Join("|", msg.CartItems.Select(i => $"{i.Sku}:{i.Qty}:{i.Price}")),
            };

            var result = await _crm.SubmitAsync(req);
            Transition(s, "MAIN_MENU");

            var itemSummary = string.Join("\n", itemLines);

            return result.Success
                ? s.T(
                    $"✅ *Order Received!*\n\n" +
                    $"{itemSummary}{totalLine}\n\n" +
                    (result.TicketId != null ? $"Ticket ID: *{result.TicketId}*\n\n" : "") +
                    "Our team will confirm your order shortly.\n\n" +
                    "👉 Send *menu* for Main Menu",

                    $"✅ *অর্ডার পাওয়া গেছে!*\n\n" +
                    $"{itemSummary}{totalLine}\n\n" +
                    (result.TicketId != null ? $"টিকেট আইডি: *{result.TicketId}*\n\n" : "") +
                    "আমাদের টিম শীঘ্রই আপনার অর্ডার নিশ্চিত করবে।\n\n" +
                    "👉 *মেনু* — মূল মেনু",

                    $"✅ *ஆர்டர் பெறப்பட்டது!*\n\n" +
                    $"{itemSummary}{totalLine}\n\n" +
                    (result.TicketId != null ? $"டிக்கெட் ஐடி: *{result.TicketId}*\n\n" : "") +
                    "எங்கள் குழு விரைவில் உங்கள் ஆர்டரை உறுதிப்படுத்தும்.\n\n" +
                    "👉 *மெனு* — முகப்பு மெனு",

                    $"✅ *订单已收到！*\n\n" +
                    $"{itemSummary}{totalLine}\n\n" +
                    (result.TicketId != null ? $"工单 ID：*{result.TicketId}*\n\n" : "") +
                    "我们的团队将尽快确认您的订单。\n\n" +
                    "👉 *menu* — 主菜单",

                    $"✅ *Pesanan Diterima!*\n\n" +
                    $"{itemSummary}{totalLine}\n\n" +
                    (result.TicketId != null ? $"ID Tiket: *{result.TicketId}*\n\n" : "") +
                    "Pasukan kami akan mengesahkan pesanan anda tidak lama lagi.\n\n" +
                    "👉 Hantar *menu* untuk Menu Utama")

                : s.T(
                    $"❌ *Could not save your order.*\n{result.Error}\n\n" +
                    "Please try again or send *S* to reach a support agent.",
                    $"❌ *অর্ডার সেভ করা যায়নি।*\n{result.Error}\n\n" +
                    "আবার চেষ্টা করুন বা *S* পাঠিয়ে এজেন্টের সাথে যোগাযোগ করুন।",
                    $"❌ *ஆர்டரை சேமிக்க முடியவில்லை.*\n{result.Error}\n\n" +
                    "மீண்டும் முயற்சிக்கவும் அல்லது முகவருக்கு *S* அனுப்பவும்.",
                    $"❌ *无法保存您的订单。*\n{result.Error}\n\n" +
                    "请重试或发送 *S* 联系客服。",
                    $"❌ *Pesanan tidak dapat disimpan.*\n{result.Error}\n\n" +
                    "Sila cuba lagi atau hantar *S* untuk hubungi ejen.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // FLOW 2 — RETURN / REPLACEMENT
        // ─────────────────────────────────────────────────────────────────────

        private string StartReturnDirect(UaeSession s)
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

                "🔄 *திரும்பப்பெறுதல் / மாற்றீடு*\n\n" +
                "திரும்பப்பெற விரும்பும் தயாரிப்பை எங்களிடம் தெரிவிக்கவும்.\n\n" +
                "*உரை*, *படம்* அல்லது *குரல்* அனுப்பவும்\n\n" +
                "👉 முகப்பு மெனுவிற்கு *0* அனுப்பவும்",

                "🔄 *退货 / 换货*\n\n" +
                "请告诉我们您想退回的产品。\n\n" +
                "发送*文字*、*图片*或*语音*\n\n" +
                "👉 发送 *0* 返回主菜单",

                "🔄 *Pemulangan / Penggantian*\n\n" +
                "Beritahu kami produk yang ingin anda pulangkan.\n\n" +
                "Hantar *Teks*, *Gambar*, atau *Suara*\n\n" +
                "👉 Hantar *0* untuk kembali ke menu utama");
        }

        private async Task<string> HandleReturnConfirmAsync(UaeSession s, UaeIncomingMessage msg)
        {
            if (msg.RawText == "y") return await SubmitMediaAsync(s, "PRODUCT_REPLACEMENT");
            if (msg.RawText == "n") { ClearMedia(s); return StartReturnDirect(s); }
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

                "📝 *புகார் / கருத்து*\n\n" +
                "உங்கள் சிக்கலை எங்களிடம் தெரிவிக்கவும்.\n\n" +
                "*உரை*, *படம்* அல்லது *குரல்* அனுப்பவும்\n\n" +
                "👉 முகப்பு மெனுவிற்கு *0* அனுப்பவும்",

                "📝 *投诉 / 反馈*\n\n" +
                "请告诉我们您的问题。\n\n" +
                "发送*文字*、*图片*或*语音*\n\n" +
                "👉 发送 *0* 返回主菜单",

                "📝 *Aduan / Maklum Balas*\n\n" +
                "Beritahu kami masalah anda.\n\n" +
                "Hantar *Teks*, *Gambar*, atau *Suara*\n\n" +
                "👉 Hantar *0* untuk kembali ke menu utama");
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
                        "⚠️ படம் பதிவேற்ற முடியவில்லை. மீண்டும் முயற்சிக்கவும்.",
                        "⚠️ 图片上传失败，请重试。",
                        "⚠️ Gambar tidak dapat dimuat naik. Sila cuba lagi.");

                // Confirm-message burst suppression
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
                        "⚠️ குரல் குறிப்பு பதிவேற்ற முடியவில்லை. மீண்டும் முயற்சிக்கவும்.",
                        "⚠️ 语音消息上传失败，请重试。",
                        "⚠️ Nota suara tidak dapat dimuat naik. Sila cuba lagi.");
            }
            else
            {
                return string.Empty;
            }

            Transition(s, confirmState);

            return s.T(
                "✅ *Received.*\n\n" +
                "Send *Y* to Complete the request or To add more details, send another *Image*, *Voice* or *Text*",

                "✅ *পাওয়া গেছে।*\n\n" +
                "অনুরোধ সম্পন্ন করতে *Y* পাঠান অথবা আরও তথ্য যোগ করতে *ছবি*, *ভয়েস* বা *টেক্সট* পাঠান",

                "✅ *பெறப்பட்டது.*\n\n" +
                "கோரிக்கையை முடிக்க *Y* அனுப்பவும் அல்லது மேலும் விவரங்களைச் சேர்க்க மற்றொரு *படம்*, *குரல்* அல்லது *உரை* அனுப்பவும்",

                "✅ *已收到。*\n\n" +
                "发送 *Y* 完成请求，或发送更多 *图片*、*语音* 或 *文字* 以补充详细信息",

                "✅ *Diterima.*\n\n" +
                "Hantar *Y* untuk melengkapkan permintaan atau hantar *Gambar*, *Suara* atau *Teks* lain untuk menambah maklumat");
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
                    $"❌ சமர்ப்பிப்பு தோல்வி.\n{result.Error}",
                    $"❌ 提交失败。\n{result.Error}",
                    $"❌ Penghantaran gagal.\n{result.Error}\n\nHantar *Y* untuk cuba semula.");

            var ticketLabel = ticketType == "PRODUCT_REPLACEMENT"
                ? s.T("Return Request", "রিটার্ন রিকোয়েস্ট", "திரும்பப்பெறும் கோரிக்கை", "退货请求", "Permintaan Pemulangan")
                : s.T("Complaint", "অভিযোগ", "புகார்", "投诉", "Aduan");

            return s.T(
                $"✅ *{ticketLabel} Submitted*\n\n" +
                (result.TicketId != null ? $"Ticket ID : *{result.TicketId}*\n\n" : "") +
                "Our team will contact you shortly.\n\n" +
                "👉 Send *menu* for Main Menu\n",

                $"✅ *{ticketLabel} জমা হয়েছে*\n\n" +
                (result.TicketId != null ? $"টিকেট আইডি : *{result.TicketId}*\n\n" : "") +
                "আমাদের টিম শীঘ্রই যোগাযোগ করবে।\n\n" +
                "👉 *মেনু* — মূল মেনু\n",

                $"✅ *{ticketLabel} சமர்ப்பிக்கப்பட்டது*\n\n" +
                (result.TicketId != null ? $"டிக்கெட் ஐடி : *{result.TicketId}*\n\n" : "") +
                "எங்கள் குழு விரைவில் தொடர்பு கொள்ளும்.\n\n" +
                "👉 *மெனு* — முகப்பு மெனு\n",

                $"✅ *{ticketLabel} 已提交*\n\n" +
                (result.TicketId != null ? $"工单 ID：*{result.TicketId}*\n\n" : "") +
                "我们的团队将尽快与您联系。\n\n" +
                "👉 *菜单* — 主菜单\n",

                $"✅ *{ticketLabel} Dihantar*\n\n" +
                (result.TicketId != null ? $"ID Tiket : *{result.TicketId}*\n\n" : "") +
                "Pasukan kami akan menghubungi anda tidak lama lagi.\n\n" +
                "👉 Hantar *menu* untuk Menu Utama\n");
        }

        // ─────────────────────────────────────────────────────────────────────
        // FLOW 4 — CONNECT WITH SUPPORT AGENT  (immediate — no Y/N confirm)
        // ─────────────────────────────────────────────────────────────────────

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
                    "👉 *মেনু* — মূল মেনু",

                    "✅ *கோரிக்கை அனுப்பப்பட்டது*\n\n" +
                    (result.TicketId != null ? $"டிக்கெட் ஐடி : *{result.TicketId}*\n\n" : "") +
                    "ஒரு முகவர் விரைவில் தொடர்பு கொள்வார்.\n\n" +
                    "👉 *மெனு* — முகப்பு மெனு",

                    "✅ *客服请求已提交*\n\n" +
                    (result.TicketId != null ? $"工单 ID：*{result.TicketId}*\n\n" : "") +
                    "客服人员将尽快联系您。\n\n" +
                    "👉 *menu* — 主菜单",

                    "✅ *Permintaan Ejen Dihantar*\n\n" +
                    (result.TicketId != null ? $"ID Tiket : *{result.TicketId}*\n\n" : "") +
                    "Seorang ejen sokongan akan menghubungi anda tidak lama lagi.\n\n" +
                    "👉 Hantar *menu* untuk Menu Utama")

                : s.T(
                    $"❌ Request failed.\n{result.Error}\n\nSend *S* to retry.",
                    $"❌ ব্যর্থ।\n{result.Error}",
                    $"❌ தோல்வி.\n{result.Error}",
                    $"❌ 请求失败。\n{result.Error}",
                    $"❌ Permintaan gagal.\n{result.Error}\n\nHantar *S* untuk cuba semula.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // WELCOME / LANGUAGE PROMPT
        // ─────────────────────────────────────────────────────────────────────

        private async Task SendWelcomeAsync(string phone, CancellationToken ct = default)
        {
            var baseUrl = _config["App:BaseUrl"]?.TrimEnd('/') ?? "https://webhook.prangroup.com";
            var logoUrl = $"{baseUrl}/images/pran-rfl-logo.jpg";
            await _dialog.SendImageAsync(phone, logoUrl, LangPrompt(), ct);
        }

        /// <summary>Just the language options list — no intro line.</summary>
        private static string LangOptions() =>
            "Please choose your language:\n\n" +
            "1️⃣  English\n" +
            "2️⃣  বাংলা\n" +
            "3️⃣  தமிழ்\n" +
            "4️⃣  中文\n" +
            "5️⃣  Bahasa Melayu\n\n" +
            "👉 Reply *1*, *2*, *3*, *4* or *5*.";

        /// <summary>Full language prompt shown after shop verification (valid or invalid code).</summary>
        private static string LangPrompt() =>
            "👋 Hi! I'm *PRAN-RFL Malaysia Sales Support*\n\n" +
            LangOptions();

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

                return filePath;
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, "[UAE] 360dialog download failed mediaId={Id}: {Msg}", mediaId, httpEx.Message);
                return null;
            }
            catch (IOException ioEx)
            {
                _logger.LogError(ioEx, "[UAE] Disk write failed wa-media/{Sub}: {Msg}", subFolder, ioEx.Message);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UAE] SaveMedia failed msgId={Id} mediaId={MId}", messageId, mediaId);
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // SESSION HELPERS
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
                _logger.LogError(ex,
                    "[UAE] PersistSession FAILED phone={Phone} step={Step} error={Msg} inner={Inner}",
                    s.Phone, s.State, ex.Message, ex.InnerException?.Message ?? "none");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // STATE / MEDIA UTILITIES
        // ─────────────────────────────────────────────────────────────────────

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

        private string BuildUnknown(UaeSession s) =>
            s.T(
                "❌ *Invalid input.*\n\n👉 Send *menu* to go to Main Menu.",
                "❌ *অবৈধ ইনপুট।*\n\n👉 *menu* পাঠান।",
                "❌ *தவறான உள்ளீடு.*\n\n👉 *menu* அனுப்பவும்.",
                "❌ *无效输入。*\n\n👉 发送 *menu* 返回主菜单。",
                "❌ *Input tidak sah.*\n\n👉 Hantar *menu* untuk pergi ke Menu Utama.");

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
    // NUMERAL NORMALISER
    // ─────────────────────────────────────────────────────────────────────────

    public static class NumeralNormaliser
    {
        private static readonly Dictionary<char, char> Map = new()
        {
            // Tamil
            { '\u0BE6', '0' }, { '\u0BE7', '1' }, { '\u0BE8', '2' }, { '\u0BE9', '3' },
            { '\u0BEA', '4' }, { '\u0BEB', '5' }, { '\u0BEC', '6' }, { '\u0BED', '7' },
            { '\u0BEE', '8' }, { '\u0BEF', '9' },
            // Bengali
            { '\u09E6', '0' }, { '\u09E7', '1' }, { '\u09E8', '2' }, { '\u09E9', '3' },
            { '\u09EA', '4' }, { '\u09EB', '5' }, { '\u09EC', '6' }, { '\u09ED', '7' },
            { '\u09EE', '8' }, { '\u09EF', '9' },
            // Fullwidth (Mandarin IME)
            { '\uFF10', '0' }, { '\uFF11', '1' }, { '\uFF12', '2' }, { '\uFF13', '3' },
            { '\uFF14', '4' }, { '\uFF15', '5' }, { '\uFF16', '6' }, { '\uFF17', '7' },
            { '\uFF18', '8' }, { '\uFF19', '9' },
            // Arabic-Indic
            { '\u0660', '0' }, { '\u0661', '1' }, { '\u0662', '2' }, { '\u0663', '3' },
            { '\u0664', '4' }, { '\u0665', '5' }, { '\u0666', '6' }, { '\u0667', '7' },
            { '\u0668', '8' }, { '\u0669', '9' },
            // Extended Arabic-Indic
            { '\u06F0', '0' }, { '\u06F1', '1' }, { '\u06F2', '2' }, { '\u06F3', '3' },
            { '\u06F4', '4' }, { '\u06F5', '5' }, { '\u06F6', '6' }, { '\u06F7', '7' },
            { '\u06F8', '8' }, { '\u06F9', '9' },
        };

        public static string Normalise(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            bool hasScript = false;
            foreach (var ch in input)
                if (Map.ContainsKey(ch)) { hasScript = true; break; }
            if (!hasScript) return input;

            var sb = new System.Text.StringBuilder(input.Length);
            foreach (var ch in input)
                sb.Append(Map.TryGetValue(ch, out var ascii) ? ascii : ch);
            return sb.ToString();
        }
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

        // Audio
        public string AudioId { get; set; } = "";
        public string AudioMime { get; set; } = "audio/ogg";

        // Image
        public string ImageId { get; set; } = "";
        public string ImageMime { get; set; } = "image/jpeg";
        public string ImageCaption { get; set; } = "";

        // ── Catalog cart order (type = "order") ───────────────────────────────
        public string OrderCatalogId { get; set; } = "";
        public string OrderText { get; set; } = "";
        public List<CartItem> CartItems { get; set; } = new();
    }

    /// <summary>One line-item from a WhatsApp catalog cart webhook.</summary>
    public record CartItem(string Sku, int Qty, decimal Price, string Currency);

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
                    if (value.TryGetProperty("smb_message_echoes", out _))
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

                // ── Text ─────────────────────────────────────────────────────
                string rawText = string.Empty;
                if (msgType == "text" &&
                    msg.TryGetProperty("text", out var textEl) &&
                    textEl.TryGetProperty("body", out var bodyEl))
                {
                    rawText = NumeralNormaliser.Normalise(
                        System.Text.RegularExpressions.Regex.Replace(
                            (bodyEl.GetString() ?? "").Trim().ToLowerInvariant(),
                            @"[\u200B-\u200D\uFEFF]", ""));
                }

                // ── Audio ────────────────────────────────────────────────────
                string audioId = "", audioMime = "audio/ogg";
                if (msgType == "audio" && msg.TryGetProperty("audio", out var audio))
                {
                    audioId = S(audio, "id");
                    audioMime = S(audio, "mime_type") is { Length: > 0 } m ? m : "audio/ogg";
                }

                // ── Image ────────────────────────────────────────────────────
                string imageId = "", imageMime = "image/jpeg", imageCap = "";
                if (msgType == "image" && msg.TryGetProperty("image", out var image))
                {
                    imageId = S(image, "id");
                    imageMime = S(image, "mime_type") is { Length: > 0 } m ? m : "image/jpeg";
                    imageCap = S(image, "caption");
                }

                // ── Catalog cart order ────────────────────────────────────────
                string orderCatalogId = "", orderText = "";
                var cartItems = new List<CartItem>();

                if (msgType == "order" && msg.TryGetProperty("order", out var order))
                {
                    orderCatalogId = S(order, "catalog_id");
                    orderText = S(order, "text");

                    if (order.TryGetProperty("product_items", out var items) &&
                        items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            var sku = S(item, "product_retailer_id");
                            var qty = item.TryGetProperty("quantity", out var qEl) ? qEl.GetInt32() : 1;
                            var price = item.TryGetProperty("item_price", out var pEl) ? pEl.GetDecimal() : 0m;
                            var currency = S(item, "currency");

                            if (!string.IsNullOrEmpty(sku))
                                cartItems.Add(new CartItem(sku, qty, price, currency));
                        }
                    }
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
                    OrderCatalogId = orderCatalogId,
                    OrderText = orderText,
                    CartItems = cartItems,
                };
            }
            catch { return null; }
        }

        private static string S(JsonElement el, string key) =>
            el.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
    }
}
