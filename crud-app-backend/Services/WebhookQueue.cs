using System.Text.Json;
using System.Threading.Channels;

namespace crud_app_backend.Bot.Services
{
    /// <summary>
    /// Singleton in-memory queue for incoming WhatsApp webhook payloads.
    ///
    /// The controller writes to this queue in &lt; 1ms and returns 200 OK immediately.
    /// WebhookProcessorService reads from this queue and does the actual processing
    /// in the background — completely decoupled from the HTTP request lifecycle.
    ///
    /// Why Channel instead of Task.Run:
    ///   Task.Run queues work on the thread pool. Under load, thread pool can be
    ///   exhausted, causing Task.Run itself to block before the work even starts.
    ///   Channel write is O(1) and never blocks — it just adds to a queue.
    /// </summary>
    public sealed class WebhookQueue
    {
        // Bounded at 1000 — drops oldest if queue overflows (prevents OOM).
        // In practice a WhatsApp bot rarely exceeds a few concurrent messages.
        private readonly Channel<JsonElement> _channel =
            Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(1000)
            {
                FullMode     = BoundedChannelFullMode.DropOldest,
                SingleReader = false, // WebhookProcessorService uses multiple readers
                SingleWriter = false  // multiple HTTP requests can write
            });

        public ChannelReader<JsonElement> Reader => _channel.Reader;

        /// <summary>
        /// Enqueue a webhook payload. Always O(1). Never blocks.
        /// Clones the JsonElement so it is safe after the HTTP request scope ends.
        /// Returns false if the channel is full (extremely unlikely).
        /// </summary>
        public bool TryEnqueue(JsonElement body)
        {
            var copy = body.Clone(); // safe copy — request scope can be disposed after this
            return _channel.Writer.TryWrite(copy);
        }
    }
}
