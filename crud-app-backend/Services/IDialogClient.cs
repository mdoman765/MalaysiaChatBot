namespace crud_app_backend.Bot.Services
{
    public interface IDialogClient
    {
        /// <summary>Send a plain-text WhatsApp message via 360dialog v2.</summary>
        Task SendTextAsync(string phone, string message,
            CancellationToken ct = default);

        /// <summary>
        /// Send an image message with a caption via 360dialog v2.
        /// imageUrl must be a publicly accessible HTTPS URL.
        /// e.g. https://webhook.prangroup.com/images/pran-rfl-logo.jpg
        /// 360dialog fetches the image directly from this URL — no upload needed.
        /// caption supports WhatsApp markdown (*bold*, _italic_, etc).
        /// Falls back to plain text automatically if imageUrl is empty or fetch fails.
        /// </summary>
        Task SendImageAsync(string phone, string imageUrl, string caption,
            CancellationToken ct = default);

        /// <summary>
        /// Download a media file from 360dialog.
        /// Returns (bytes, mimeType). Throws on failure.
        /// </summary>
        Task<(byte[] Data, string MimeType)> DownloadMediaAsync(
            string mediaId, string fallbackMime,
            CancellationToken ct = default);
    }
}
