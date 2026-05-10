using crud_app_backend.DTOs;
using crud_app_backend.Repositories;
using crud_app_backend.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace crud_app_backend.Controllers
{
    [ApiController]
    [Route("api/whatsapp")]
    [Produces("application/json")]
    public class WhatsAppSessionController : ControllerBase
    {
        private readonly IWhatsAppSessionService _service;
        private readonly ILogger<WhatsAppSessionController> _logger;

        public WhatsAppSessionController(
            IWhatsAppSessionService service,
            ILogger<WhatsAppSessionController> logger)
        {
            _service = service;
            _logger = logger;
        }

       
        [HttpGet("session")]
        [ProducesResponseType(typeof(SessionResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ApiResponseDto<object>), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetSession(
            [FromQuery] string phone,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(phone))
                return BadRequest(new { error = "phone query parameter is required" });

            try
            {
                var result = await _service.GetSessionAsync(phone.Trim(), ct);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[WA-Controller] GetSession failed — phone={Phone}", phone);

                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponseDto<object>.Fail("Failed to load session: " + ex.Message));
            }
        }


        [HttpPost("session")]
        [ProducesResponseType(typeof(ApiResponseDto<object>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ApiResponseDto<object>), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> UpsertSession(
            [FromBody] UpsertSessionRequestDto req,
            CancellationToken ct)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            try
            {
                var result = await _service.UpsertSessionAsync(req, ct);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[WA-Controller] UpsertSession failed — phone={Phone} step={Step}",
                    req.Phone, req.CurrentStep);

                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponseDto<object>.Fail("Failed to save session: " + ex.Message));
            }
        }


     
        [HttpDelete("session")]
        [ProducesResponseType(typeof(ApiResponseDto<object>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ApiResponseDto<object>), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ApiResponseDto<object>), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> DeleteSession(
            [FromQuery] string phone,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(phone))
                return BadRequest(new { error = "phone query parameter is required" });

            try
            {
                var result = await _service.DeleteSessionAsync(phone.Trim(), ct);

                if (!result.Success)
                    return NotFound(result);

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[WA-Controller] DeleteSession failed — phone={Phone}", phone);

                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponseDto<object>.Fail("Failed to delete session: " + ex.Message));
            }
        }


        
        [HttpGet("session/history")]
        [ProducesResponseType(typeof(List<Models.WhatsAppSessionHistory>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ApiResponseDto<object>), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetHistory(
            [FromQuery] string phone,
            [FromQuery] int limit = 20,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(phone))
                return BadRequest(new { error = "phone query parameter is required" });

            // Clamp limit — service also guards, but be explicit here
            if (limit is < 1 or > 200)
                limit = 20;

            try
            {
                var rows = await _service.GetHistoryAsync(phone.Trim(), limit, ct);
                return Ok(rows);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[WA-Controller] GetHistory failed — phone={Phone}", phone);

                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponseDto<object>.Fail("Failed to load history: " + ex.Message));
            }
        }
    }
}
