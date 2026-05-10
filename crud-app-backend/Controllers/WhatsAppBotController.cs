using System.Text.Json;
using crud_app_backend.Bot.Services;
using Microsoft.AspNetCore.Mvc;

namespace crud_app_backend.Bot.Controllers
{
    /// <summary>
    /// Receives 360dialog webhook POST requests.
    ///
    /// IMPORTANT: 360dialog requires a response within 80ms.
    /// This controller does ONE thing — write body to WebhookQueue — and
    /// returns 200 OK immediately. No DB calls, no HTTP calls, no processing.
    /// WebhookProcessorService handles all actual processing in the background.
    /// </summary>
    [ApiController]
    [Route("webhook")]
    public class WhatsAppBotController : ControllerBase
    {
        private readonly WebhookQueue _queue;
        private readonly ILogger<WhatsAppBotController> _logger;

        public WhatsAppBotController(
            WebhookQueue queue,
            ILogger<WhatsAppBotController> logger)
        {
            _queue = queue;
            _logger = logger;
        }

        /// <summary>
        /// 360dialog calls this on every incoming WhatsApp message.
        /// Returns 200 in &lt; 1ms. All processing is done by WebhookProcessorService.
        /// </summary>
        [HttpPost("whatsapp-webhook")]
        public IActionResult Webhook([FromBody] JsonElement body)
        {
            // TryEnqueue clones the JsonElement (safe after request scope ends)
            // and writes to the Channel. O(1), never blocks.
            if (!_queue.TryEnqueue(body))
            {
                // Queue full (extremely rare — only if 1000+ messages backlogged)
                _logger.LogWarning("[Webhook] Queue full — message dropped");
            }

            // 200 OK back to 360dialog in < 1ms
            return Ok(new { status = "received" });
        }

        /// <summary>Health check — 360dialog or monitoring can ping this.</summary>
        [HttpGet("health")]
        public IActionResult Health() => Ok(new { status = "ok", time = DateTime.UtcNow });
    }
}
