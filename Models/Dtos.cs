// Models/Dtos.cs
namespace GHSparApi.Models;

// ── Auth ──────────────────────────────────────────────────────────────────────
public record RegisterRequest(string Username, string Msisdn, string Alias, string Momo);
public record OtpVerifyRequest(string Msisdn, string Otp);
public record LoginActionRequest(string Action);

// ── Purchases ─────────────────────────────────────────────────────────────────
public record PurchaseInitiateRequest(int Amount, string PaymentMethod, string Phone);

// ── Withdrawals ───────────────────────────────────────────────────────────────
public record WithdrawalRequest(int Amount, string RecipientPhone, string Network);

// ── Rooms ─────────────────────────────────────────────────────────────────────
public record CreateRoomRequest(
    string PlayerId,
    string Alias,
    int    StakeAmount,
    int    GamesPerMatch,
    bool   OgbaEnabled,
    int    MaxPlayers);

public record JoinRoomRequest(string PlayerId, string Alias);
