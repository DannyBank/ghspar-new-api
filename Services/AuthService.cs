// Services/AuthService.cs
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GHSparApi.Data;
using GHSparApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace GHSparApi.Services;

public class AuthService(AppDbContext db, IConfiguration cfg)
{
    private static readonly Random Rng = new();

    public string GenerateOtp() => Rng.Next(100000, 999999).ToString();

    public string CreateJwt(Guid playerId)
    {
        var key    = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Secret"]!));
        var creds  = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token  = new JwtSecurityToken(
            issuer:            cfg["Jwt:Issuer"],
            audience:          cfg["Jwt:Audience"],
            claims:            [new Claim("sub", playerId.ToString())],
            expires:           DateTime.UtcNow.AddDays(7),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task StoreOtp(string msisdn, string otp)
    {
        db.OtpLogs.Add(new OtpLog
        {
            Msisdn    = msisdn,
            Otp       = otp,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        });
        await db.SaveChangesAsync();
    }

    public async Task<bool> VerifyOtp(string msisdn, string otp)
    {
        var log = await db.OtpLogs
            .Where(o => o.Msisdn == msisdn && o.Otp == otp && !o.Used && o.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync();

        if (log == null) return false;
        log.Used = true;
        await db.SaveChangesAsync();
        return true;
    }
}
