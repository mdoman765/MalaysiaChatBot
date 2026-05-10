namespace crud_app_backend.Bot.Services
{
    /// <summary>
    /// Hardcoded area → salesman lookup.
    /// Edit this file and redeploy to update the list.
    /// Keys are case-insensitive.
    /// </summary>
    public static class SalesmanData
    {
        private static readonly Dictionary<string, string> _map =
            new(StringComparer.OrdinalIgnoreCase)
        {
            // ── Replace with your real salesman data ──────────────────────────
            ["dubai deira"]     = "👤 *Ahmed Khan*\n📞 +971-50-XXX-XXXX\n🕐 Sat–Thu 8AM–6PM",
            ["dubai bur dubai"] = "👤 *Mohammed Ali*\n📞 +971-55-XXX-XXXX\n🕐 Sat–Thu 8AM–6PM",
            ["sharjah"]         = "👤 *Khalid Hassan*\n📞 +971-52-XXX-XXXX\n🕐 Sat–Thu 8AM–6PM",
            ["abu dhabi"]       = "👤 *Omar Farooq*\n📞 +971-56-XXX-XXXX\n🕐 Sat–Thu 8AM–6PM",
            ["ajman"]           = "👤 *Tariq Mahmood*\n📞 +971-58-XXX-XXXX\n🕐 Sat–Thu 8AM–6PM",
        };

        public static string? Find(string area)
        {
            area = area.Trim();
            if (_map.TryGetValue(area, out var exact)) return exact;
            foreach (var kv in _map)
                if (area.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.Contains(area, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }
    }
}
