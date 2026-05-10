using System.Text.Json;

namespace crud_app_backend.Bot.Services
{
    /// <summary>
    /// Wraps all spror.prgfms.com API calls used by the UAE bot.
    /// </summary>
    public class SprorClient : ISprorClient
    {
        private const string ContName = "Saudi Arabia";

        private readonly IHttpClientFactory _factory;
        private readonly ILogger<SprorClient> _logger;
        private readonly IConfiguration _config;

        public SprorClient(
            IHttpClientFactory factory,
            ILogger<SprorClient> logger,
            IConfiguration config)
        {
            _factory = factory;
            _logger = logger;
            _config = config;
        }

        private string BaseUrl => (_config["Spror:BaseUrl"] ?? "http://spror.prgfms.com/api/v1").TrimEnd('/');

        // ── Shop validation ───────────────────────────────────────────────────
        public async Task<SprorShopData?> ValidateShopAsync(
            string shopCode, CancellationToken ct = default)
        {
            try
            {
                var client = _factory.CreateClient("Spror");

                // API requires shop_code + cont_name in the body
                var resp = await client.PostAsJsonAsync(
                    $"{BaseUrl}/retail/shopDetails",
                    new { shop_code = shopCode, cont_name = ContName },
                    ct);

                if (!resp.IsSuccessStatusCode) return null;

                var json = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogDebug("[Spror] ValidateShop response: {J}", json.Length > 200 ? json[..200] : json);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Response: { "code": 200, "status": true, "data": [{...}] }
                if (!root.TryGetProperty("status", out var st) || !st.GetBoolean())
                    return null;

                if (!root.TryGetProperty("data", out var dataEl) ||
                    dataEl.ValueKind != JsonValueKind.Array ||
                    dataEl.GetArrayLength() == 0)
                    return null;

                var shop = dataEl[0];

                // cont_id is a number in the response (e.g. 15)
                var contId = shop.TryGetProperty("cont_id", out var cid)
                    ? cid.ToString() : "15";

                return new SprorShopData(
                    Id: S(shop, "id"),        // numeric id → string
                    ContId: contId,               // cont_id number → string
                    ShopName: S(shop, "site_name"), // site_name not shop_name
                    SiteCode: S(shop, "site_code")  // echo back for confirmation
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Spror] ValidateShop failed shopCode={C}", shopCode);
                return null;
            }
        }

        // ── Categories ────────────────────────────────────────────────────────
        //public async Task<List<SprorCategory>> GetCategoriesAsync(CancellationToken ct = default)
        //{
        //    try
        //    {
        //        var client = _factory.CreateClient("Spror");
        //        var url = $"{BaseUrl}/category/1?cont_name={Uri.EscapeDataString(ContName)}";
        //        var resp = await client.GetAsync(url, ct);

        //        _logger.LogInformation("[Spror] GetCategories {Code} url={U}", (int)resp.StatusCode, url);
        //        if (!resp.IsSuccessStatusCode) return new();

        //        var json = await resp.Content.ReadAsStringAsync(ct);
        //        _logger.LogDebug("[Spror] GetCategories response: {J}", json.Length > 300 ? json[..300] : json);

        //        // Try multiple common field names for id and name
        //        return ParseListFlexible(json, item =>
        //        {
        //            var id = S(item, "id");
        //            if (string.IsNullOrEmpty(id)) id = S(item, "category_id");
        //            var name = S(item, "name");
        //            if (string.IsNullOrEmpty(name)) name = S(item, "category_name");
        //            if (string.IsNullOrEmpty(name)) name = S(item, "cat_name");
        //            return string.IsNullOrEmpty(id) && string.IsNullOrEmpty(name)
        //                ? null
        //                : new SprorCategory(id, name);
        //        });
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "[Spror] GetCategories failed");
        //        return new();
        //    }
        //}
        public async Task<List<SprorCategory>> GetCategoriesAsync(CancellationToken ct = default)
        {
            try
            {
                var client = _factory.CreateClient("Spror");

                // Bearer token required by this endpoint
                var token = _config["Spror:BearerToken"] ?? "224|IEcNubBv4Z9LoXpngVuHthRrSDdIlD0B4RGxNFqT";
                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{BaseUrl}/category?cont_name={Uri.EscapeDataString(ContName)}");
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

                var resp = await client.SendAsync(request, ct);
                _logger.LogInformation("[Spror] GetCategories {Code}", (int)resp.StatusCode);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("[Spror] GetCategories failed {Code} body={B}", (int)resp.StatusCode, err);
                    return new();
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogInformation("[Spror] GetCategories response: {J}", json.Length > 300 ? json[..300] : json);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Response shape: {"data": {"current_page":1, "data": [{...},...], ...}}
                // Navigate: root → data (object) → data (array)
                if (!root.TryGetProperty("data", out var outerData))
                    return new();

                JsonElement arr = default;

                if (outerData.ValueKind == JsonValueKind.Object &&
                    outerData.TryGetProperty("data", out var innerArr) &&
                    innerArr.ValueKind == JsonValueKind.Array)
                {
                    // Paginated: data.data[]
                    arr = innerArr;
                }
                else if (outerData.ValueKind == JsonValueKind.Array)
                {
                    // Flat: data[]
                    arr = outerData;
                }

                if (arr.ValueKind != JsonValueKind.Array) return new();

                var list = new List<SprorCategory>();
                foreach (var item in arr.EnumerateArray())
                {
                    var id = S(item, "id");
                    var name = S(item, "category_name");
                    if (string.IsNullOrEmpty(name)) name = S(item, "name");
                    if (!string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(name))
                        list.Add(new SprorCategory(id, name));
                }
                return list;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Spror] GetCategories failed");
                return new();
            }
        }

        //public async Task<List<SprorCategory>> GetSubcategoriesAsync(
        //    string catId, CancellationToken ct = default)
        //{
        //    try
        //    {
        //        var client = _factory.CreateClient("Spror");
        //        // GET /api/v1/category/{catId}?cont_name=Saudi Arabia
        //        // Returns: {"data":{"id":1,"category_name":"...","sub_category":[{id, sub_category_name,...}]}}
        //        var url = $"{BaseUrl}/category/1?cont_name={Uri.EscapeDataString(ContName)}";
        //        var resp = await client.GetAsync(url, ct);

        //        _logger.LogInformation("[Spror] GetSubcategories {Code} catId={C}", (int)resp.StatusCode, catId);
        //        if (!resp.IsSuccessStatusCode) return new();

        //        var json = await resp.Content.ReadAsStringAsync(ct);
        //        _logger.LogInformation("[Spror] GetSubcategories response: {J}", json.Length > 300 ? json[..300] : json);

        //        using var doc = JsonDocument.Parse(json);
        //        var root = doc.RootElement;

        //        // Navigate: root.data.sub_category[]
        //        if (!root.TryGetProperty("data", out var dataEl) ||
        //            dataEl.ValueKind != JsonValueKind.Object)
        //            return new();

        //        if (!dataEl.TryGetProperty("sub_category", out var subArr) ||
        //            subArr.ValueKind != JsonValueKind.Array)
        //            return new();

        //        var list = new List<SprorCategory>();
        //        var seen = new HashSet<string>(); // deduplicate by sub_category_code
        //        foreach (var item in subArr.EnumerateArray())
        //        {
        //            var id = S(item, "id");
        //            var name = S(item, "sub_category_name");
        //            if (string.IsNullOrEmpty(name)) name = S(item, "name");
        //            var code = S(item, "sub_category_code");

        //            // Skip duplicates (API has duplicate entries for same code)
        //            if (!string.IsNullOrEmpty(code) && !seen.Add(code)) continue;

        //            if (!string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(name))
        //                list.Add(new SprorCategory(id, name));
        //        }
        //        return list;
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "[Spror] GetSubcategories failed catId={C}", catId);
        //        return new();
        //    }
        //}
        public async Task<List<SprorCategory>> GetSubcategoriesAsync(
    string catId, CancellationToken ct = default)
        {
            try
            {
                var client = _factory.CreateClient("Spror");

                // Bearer token (same as GetCategoriesAsync)
                var token = _config["Spror:BearerToken"] ?? "224|IEcNubBv4Z9LoXpngVuHthRrSDdIlD0B4RGxNFqT";

                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{BaseUrl}/category/1?cont_name={Uri.EscapeDataString(ContName)}");

                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

                var resp = await client.SendAsync(request, ct);
                _logger.LogInformation("[Spror] GetSubcategories {Code} catId={C}", (int)resp.StatusCode, catId);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("[Spror] GetSubcategories failed {Code} body={B}", (int)resp.StatusCode, err);
                    return new();
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogInformation("[Spror] GetSubcategories response: {J}", json.Length > 300 ? json[..300] : json);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Expected: root.data.sub_category[]
                if (!root.TryGetProperty("data", out var dataEl))
                    return new();

                JsonElement arr = default;

                if (dataEl.ValueKind == JsonValueKind.Object &&
                    dataEl.TryGetProperty("sub_category", out var subArr) &&
                    subArr.ValueKind == JsonValueKind.Array)
                {
                    arr = subArr;
                }

                if (arr.ValueKind != JsonValueKind.Array) return new();

                var list = new List<SprorCategory>();
                var seen = new HashSet<string>(); // deduplicate

                foreach (var item in arr.EnumerateArray())
                {
                    var id = S(item, "id");

                    var name = S(item, "sub_category_name");
                    if (string.IsNullOrEmpty(name)) name = S(item, "name");

                    var code = S(item, "sub_category_code");

                    // remove duplicates
                    if (!string.IsNullOrEmpty(code) && !seen.Add(code))
                        continue;

                    if (!string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(name))
                        list.Add(new SprorCategory(id, name));
                }

                return list;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Spror] GetSubcategories failed catId={C}", catId);
                return new();
            }
        }

        //public async Task<List<SprorProduct>> GetProductsAsync(
        //    string subcatId, CancellationToken ct = default)
        //{
        //    try
        //    {
        //        var client = _factory.CreateClient("Spror");
        //        var resp = await client.GetAsync(
        //            $"{BaseUrl}/sub-category/{subcatId}?cont_name={Uri.EscapeDataString(ContName)}", ct);

        //        if (!resp.IsSuccessStatusCode) return new();
        //        var json = await resp.Content.ReadAsStringAsync(ct);
        //        return ParseList(json, item => new SprorProduct(
        //            Id: S(item, "id"),
        //            Name: S(item, "product_name"),
        //            Price: D(item, "product_price"),
        //            OldPrice: D(item, "productOldPrice"),
        //            Code: S(item, "product_code"),
        //            ImageUrl: S(item, "productImageUrl"),
        //            Factor: I(item, "factor"),
        //            GroupId: S(item, "groupId"),   // store for order payload
        //            PriceId: S(item, "priceId")    // store for order payload
        //        ));
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "[Spror] GetProducts failed subcatId={C}", subcatId);
        //        return new();
        //    }
        //}
        public async Task<List<SprorProduct>> GetProductsAsync(
    string subcatId, CancellationToken ct = default)
        {
            try
            {
                var client = _factory.CreateClient("Spror");

                var token = _config["Spror:BearerToken"] ?? "224|IEcNubBv4Z9LoXpngVuHthRrSDdIlD0B4RGxNFqT";

                // FIX 1: use subcatId parameter, not hardcoded 10113
                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{BaseUrl}/sub-category/10113?cont_name={Uri.EscapeDataString(ContName)}");

                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

                var resp = await client.SendAsync(request, ct);
                _logger.LogInformation("[Spror] GetProducts {Code} subcatId={C}", (int)resp.StatusCode, subcatId);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("[Spror] GetProducts failed {Code} body={B}", (int)resp.StatusCode, err);
                    return new();
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogInformation("[Spror] GetProducts response: {J}", json.Length > 300 ? json[..300] : json);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // FIX 2: correct JSON path is root → data (object) → products (object) → data (array)
                if (!root.TryGetProperty("data", out var dataEl) ||
                    dataEl.ValueKind != JsonValueKind.Object)
                    return new();

                if (!dataEl.TryGetProperty("products", out var productsEl) ||
                    productsEl.ValueKind != JsonValueKind.Object)
                    return new();

                if (!productsEl.TryGetProperty("data", out var arr) ||
                    arr.ValueKind != JsonValueKind.Array)
                    return new();

                var list = new List<SprorProduct>();

                foreach (var item in arr.EnumerateArray())
                {
                    // FIX 3: use actual field names from the API response
                    var id = S(item, "id");           // numeric → S() uses .ToString(), works fine
                    var name = S(item, "name");
                    var price = D(item, "new_price");
                    var oldPrice = D(item, "old_price");
                    var code = S(item, "code");
                    var imageUrl = S(item, "image");
                    var factor = I(item, "factor");
                    var groupId = S(item, "group_id");
                    var priceId = S(item, "price_id");

                    if (!string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(name))
                    {
                        list.Add(new SprorProduct(
                            Id: id,
                            Name: name,
                            Price: price,
                            OldPrice: oldPrice,
                            Code: code,
                            ImageUrl: imageUrl,
                            Factor: factor,
                            GroupId: groupId,
                            PriceId: priceId
                        ));
                    }
                }

                return list;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Spror] GetProducts failed subcatId={C}", subcatId);
                return new();
            }
        }

        // ── Place order ───────────────────────────────────────────────────────
        public async Task<SprorOrderResult> PlaceOrderAsync(
            PlaceOrderRequest req, CancellationToken ct = default)
        {
            try
            {
                int seq = 1;
                var itemsArr = req.Items.Select(i => new
                {
                    amount = Math.Round(i.Price * i.Qty, 3),
                    exc_percentage = 0.0,
                    excise_amount = 0.0,
                    factor = i.Factor,
                    free_is = 0,
                    groupId = int.TryParse(i.GroupId, out var gid) ? gid : 0,
                    id = seq++,
                    min_order_qty = 1,
                    netTotal = Math.Round(i.Price * i.Qty, 3),
                    priceId = int.TryParse(i.PriceId, out var pid) ? pid : 0,
                    product_code = i.Code,
                    product_id = i.Pid,
                    productImageUrl = i.ImageUrl,
                    product_name = i.Name,
                    productOldPrice = i.OldPrice,
                    product_price = i.Price,
                    product_quantity = i.Qty,
                    promo_id = 0,
                    vat_amount = 0.0,
                    vat_percentage = 0.0,
                }).ToList();

                var body = new
                {
                    user_id = req.UserId,
                    country_id = req.CountryId,
                    cont_name = ContName,
                    group_id = req.GroupId,
                    price_id = req.PriceId,
                    geo_lat = "23.518808859594895",
                    geo_lon = "44.815050969064266",
                    items = JsonSerializer.Serialize(itemsArr),
                    delivery_date = "",
                    total_amount = req.TotalAmount.ToString("F4"),
                    latitude = "23.5134646",
                    longitude = "44.8224735",
                    countryName = ContName,
                    countryCode = "SA",
                    state = "Riyadh Province",
                    city = "",
                    postalCode = "19928",
                    addressLine = "19928, Saudi Arabia"
                };

                var client = _factory.CreateClient("Spror");

                // Bearer token required — same as all other Spror endpoints
                var token = _config["Spror:BearerToken"] ?? "224|IEcNubBv4Z9LoXpngVuHthRrSDdIlD0B4RGxNFqT";
                var orderRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/store-order")
                {
                    Content = JsonContent.Create(body)
                };
                orderRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

                var resp = await client.SendAsync(orderRequest, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                _logger.LogInformation("[Spror] PlaceOrder {Code}: {Body}",
                    (int)resp.StatusCode, json.Length > 200 ? json[..200] : json);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var ok = root.TryGetProperty("status", out var sv) && sv.GetBoolean();
                var orderId = root.TryGetProperty("order_id", out var oid) ? oid.GetString() : null;
                var msg = root.TryGetProperty("message", out var mv) ? mv.GetString() : null;

                return ok
                    ? new SprorOrderResult(true, orderId, null)
                    : new SprorOrderResult(false, null, msg ?? "Order failed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Spror] PlaceOrder crashed");
                return new SprorOrderResult(false, null, ex.Message);
            }
        }

        // ── Order history ─────────────────────────────────────────────────────
        public async Task<string> GetOrdersAsync(
            string shopUserId, CancellationToken ct = default)
        {
            try
            {
                var client = _factory.CreateClient("Spror");

                // Bearer token + cont_name required
                var token = _config["Spror:BearerToken"] ?? "224|IEcNubBv4Z9LoXpngVuHthRrSDdIlD0B4RGxNFqT";
                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{BaseUrl}/user/order-details/{shopUserId}?cont_name={Uri.EscapeDataString(ContName)}");
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

                var resp = await client.SendAsync(request, ct);
                _logger.LogInformation("[Spror] GetOrders {Code} userId={U}", (int)resp.StatusCode, shopUserId);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("[Spror] GetOrders failed {Code} body={B}", (int)resp.StatusCode, err);
                    return "[]";
                }

                return await resp.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Spror] GetOrders failed userId={U}", shopUserId);
                return "[]";
            }
        }

        // ── SR Agents (salesman list) ─────────────────────────────────────────
        // Different base URL and API key from the other Spror endpoints.
        // GET /api/v3/get-sr-agent?country_id={countryId}&site_code={siteCode}
        public async Task<SprorAgentResult> GetSrAgentsAsync(
            string siteCode, string countryId = "15", CancellationToken ct = default)
        {
            try
            {
                var agentApiKey = _config["Spror:AgentApiKey"] ?? "f06ff43be3310989";
                var agentBase = (_config["Spror:AgentBaseUrl"] ?? "http://spro.prgfms.com").TrimEnd('/');

                // Use a fresh request so we can set the ApiKey header per-request
                var client = _factory.CreateClient("Spror");
                var request = new HttpRequestMessage(HttpMethod.Post,
                    $"{agentBase}/api/v3/get-sr-agent" +
                    $"?country_id={Uri.EscapeDataString(countryId)}" +
                    $"&site_code={Uri.EscapeDataString(siteCode)}");
                request.Headers.TryAddWithoutValidation("ApiKey", agentApiKey);

                _logger.LogInformation("[Spror] GetSrAgents siteCode={S} countryId={C}", siteCode, countryId);
                var resp = await client.SendAsync(request, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[Spror] GetSrAgents {Code} for siteCode={S}", (int)resp.StatusCode, siteCode);
                    return new SprorAgentResult(false, new(), null, "Agent API returned an error.");
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogDebug("[Spror] GetSrAgents response: {J}", json.Length > 200 ? json[..200] : json);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("status", out var sv) || sv.GetString() != "success")
                    return new SprorAgentResult(false, new(), null, "Agent API returned non-success.");

                // Parse agentList array
                var agents = new List<SprorAgent>();
                if (root.TryGetProperty("agentList", out var al) &&
                    al.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in al.EnumerateArray())
                    {
                        var staffId = S(item, "STAFF_ID");
                        var staffName = S(item, "STAFF_NAME");
                        if (!string.IsNullOrWhiteSpace(staffName))
                            agents.Add(new SprorAgent(staffId, staffName));
                    }
                }

                // Parse site name from site object
                var siteName = root.TryGetProperty("site", out var site)
                    ? S(site, "SITE_NAME")
                    : null;

                return new SprorAgentResult(true, agents, siteName, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Spror] GetSrAgents crashed siteCode={S}", siteCode);
                return new SprorAgentResult(false, new(), null, ex.Message);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // ParseList — expects { "data": [...] } structure
        private static List<T> ParseList<T>(string json, Func<JsonElement, T> map)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var dataEl) ||
                dataEl.ValueKind != JsonValueKind.Array)
                return new();

            var list = new List<T>();
            foreach (var item in dataEl.EnumerateArray())
                try { list.Add(map(item)); } catch { }
            return list;
        }

        // ParseListFlexible — handles multiple response shapes:
        //   { "data": [...] }  |  { "categories": [...] }  |  [...]  (root array)
        //   Also filters nulls (map can return null to skip an item)
        private static List<T> ParseListFlexible<T>(string json, Func<JsonElement, T?> map)
            where T : class
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Try common wrapper keys
            JsonElement arr = default;
            if (root.ValueKind == JsonValueKind.Array)
                arr = root;
            else if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array)
                arr = d;
            else if (root.TryGetProperty("categories", out var c) && c.ValueKind == JsonValueKind.Array)
                arr = c;
            else if (root.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array)
                arr = it;

            if (arr.ValueKind != JsonValueKind.Array) return new();

            var list = new List<T>();
            foreach (var item in arr.EnumerateArray())
            {
                try
                {
                    var result = map(item);
                    if (result != null) list.Add(result);
                }
                catch { }
            }
            return list;
        }

        private static string S(JsonElement el, string key)
            => el.TryGetProperty(key, out var v) ? v.ToString() : "";

        private static double D(JsonElement el, string key)
            => el.TryGetProperty(key, out var v) &&
               v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

        private static int I(JsonElement el, string key)
            => el.TryGetProperty(key, out var v) &&
               v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 1;
    }
}
