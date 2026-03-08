// Controllers/AdminController.cs
// Admin endpoints for runtime configuration. Protect with a secret key header
// before deploying to production (see X-Admin-Key check below).

using GHSparApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace GHSparApi.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminController(GameSettingsService settings) : ControllerBase
{
    private bool IsAuthorised =>
        Request.Headers.TryGetValue("X-Admin-Key", out var key) &&
        key == settings.AdminKey;

    /// GET /api/admin/settings
    /// Returns current runtime settings.
    [HttpGet("settings")]
    public IActionResult GetSettings()
    {
        if (!IsAuthorised) return Unauthorized(new { error = "Invalid admin key" });
        return Ok(new
        {
            reconnectGracePeriodSeconds = settings.ReconnectGracePeriodSeconds,
        });
    }

    /// PATCH /api/admin/settings
    /// Body: { "reconnectGracePeriodSeconds": 30 }
    /// Changes take effect for all new disconnections immediately — ongoing
    /// grace-period timers already running keep their original duration.
    [HttpPatch("settings")]
    public IActionResult UpdateSettings([FromBody] UpdateSettingsRequest req)
    {
        if (!IsAuthorised) return Unauthorized(new { error = "Invalid admin key" });

        if (req.ReconnectGracePeriodSeconds.HasValue)
        {
            var v = req.ReconnectGracePeriodSeconds.Value;
            if (v < 10 || v > 300)
                return BadRequest(new { error = "Grace period must be 10–300 seconds" });
            settings.ReconnectGracePeriodSeconds = v;
        }

        return Ok(new
        {
            message = "Settings updated",
            reconnectGracePeriodSeconds = settings.ReconnectGracePeriodSeconds,
        });
    }
}

public record UpdateSettingsRequest(int? ReconnectGracePeriodSeconds);
