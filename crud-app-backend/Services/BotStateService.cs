using System.Collections.Concurrent;

namespace crud_app_backend.Bot.Services
{
    /// <summary>
    /// Singleton — holds shared state that must survive across HTTP requests.
    ///
    /// BotService is Scoped (new instance per request), so any fields on it
    /// are reset every request. This singleton carries the two dictionaries
    /// that MUST be shared across all requests:
    ///
    ///   UserLocks     — per-user SemaphoreSlim for gallery burst ordering
    ///   LastImageTime — per-user timestamp for burst vs one-by-one detection
    /// </summary>
    public class BotStateService
    {
        /// <summary>
        /// One SemaphoreSlim per phone number.
        /// Ensures concurrent webhooks for the same user are queued, not raced.
        /// </summary>
        public ConcurrentDictionary<string, SemaphoreSlim> UserLocks { get; } = new();

        /// <summary>
        /// Timestamp of the last image received per phone number.
        /// Used to detect gallery burst (gap &lt; 2s) vs deliberate one-by-one send.
        /// </summary>
        public ConcurrentDictionary<string, DateTime> LastImageTime { get; } = new();
    }
}
