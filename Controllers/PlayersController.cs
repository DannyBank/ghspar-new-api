// Controllers/PlayersController.cs
using GHSparApi.Data;
using GHSparApi.Models;
using GHSparApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GHSparApi.Controllers;

[ApiController]
[Route("api/players")]
public class PlayersController(AppDbContext db, AuthService auth) : ControllerBase
{
    // POST /api/players/register
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        bool taken = await db.Players.AnyAsync(p => p.Username == req.Username || p.Msisdn == req.Msisdn);
        if (taken) return BadRequest(new { error = "Username or phone already registered" });

        var player = new Player
        {
            Username  = req.Username,
            Alias     = req.Alias,
            Msisdn    = req.Msisdn,
            MoMo      = req.Momo,
            SparCoins = 20
        };
        db.Players.Add(player);
        db.LoginLogs.Add(new LoginLog { PlayerId = player.Id, Action = "register" });
        await db.SaveChangesAsync();

        var otp = auth.GenerateOtp();
        await auth.StoreOtp(req.Msisdn, otp);

        // TODO: send OTP via SMS / MoMo API
        return Ok(new
        {
            OTP        = otp,   // remove from production response once SMS is live
            PlayerData = new { id = player.Id, player.Username, player.Alias, player.Msisdn, player.MoMo, player.SparCoins, player.IsActive }
        });
    }

    // GET /api/players?username=x&msisdn=y   (look up existing player + issue OTP for login)
    [HttpGet]
    public async Task<IActionResult> GetPlayer([FromQuery] string username = "", [FromQuery] string msisdn = "")
    {
        var player = await db.Players
            .Where(p => p.Username == username || p.Msisdn == msisdn)
            .FirstOrDefaultAsync();

        if (player == null) return Ok(new { found = false });

        var otp = auth.GenerateOtp();
        await auth.StoreOtp(player.Msisdn, otp);

        return Ok(new
        {
            found      = true,
            OTP        = otp,
            PlayerData = new { id = player.Id, player.Username, player.Alias, player.Msisdn, player.MoMo, player.SparCoins, player.IsActive }
        });
    }

    // POST /api/players/verify-otp
    [HttpPost("verify-otp")]
    public async Task<IActionResult> VerifyOtp([FromBody] OtpVerifyRequest req)
    {
        var ok = await auth.VerifyOtp(req.Msisdn, req.Otp);
        if (!ok) return BadRequest(new { error = "Invalid or expired OTP" });
        return Ok(new { verified = true });
    }

    // POST /api/players/{id}/login
    [HttpPost("{playerId:guid}/login")]
    public async Task<IActionResult> RecordLogin(Guid playerId, [FromBody] LoginActionRequest req)
    {
        db.LoginLogs.Add(new LoginLog { PlayerId = playerId, Action = req.Action });
        await db.SaveChangesAsync();
        return Ok(new { status = "ok" });
    }

    // GET /api/players/{id}/balance
    [HttpGet("{playerId:guid}/balance")]
    public async Task<IActionResult> GetBalance(Guid playerId)
    {
        var player = await db.Players.FindAsync(playerId);
        return Ok(new { balance = player?.SparCoins ?? 0 });
    }
}
