// Models/Entities.cs
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GHSparApi.Models;

public class Player
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(50)] public string Username  { get; set; } = "";
    [Required, MaxLength(50)] public string Alias     { get; set; } = "";
    [Required, MaxLength(20)] public string Msisdn    { get; set; } = "";
    [Required, MaxLength(20)] public string MoMo      { get; set; } = "";
    public int  SparCoins { get; set; } = 20;
    public bool IsActive  { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class LoginLog
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlayerId { get; set; }
    [MaxLength(20)] public string Action { get; set; } = "login";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class OtpLog
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(20)] public string Msisdn    { get; set; } = "";
    [Required, MaxLength(10)] public string Otp       { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public bool     Used      { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class PurchaseTransaction
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    [Required, MaxLength(60)] public string  Reference       { get; set; } = "";
    [MaxLength(20)]           public string? PaymentMethod   { get; set; }
    public int AmountInSparcoins { get; set; }
    public int AmountInCurrency  { get; set; }
    [MaxLength(5)]  public string  Currency { get; set; } = "GHS";
    [MaxLength(20)] public string  Status   { get; set; } = "pending";
    [MaxLength(60)] public string? PaystackReference { get; set; }
    public DateTime  CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public class PaymentWebhookLog
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(60)] public string? Reference { get; set; }
    [MaxLength(40)] public string? Event     { get; set; }
    public string?  Payload    { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}

public class TransactionHistory
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    [Required, MaxLength(20)] public string TransactionType { get; set; } = "";
    [Required, MaxLength(30)] public string Source          { get; set; } = "";
    [MaxLength(60)] public string? Reference    { get; set; }
    public int     Amount       { get; set; }
    public int?    BalanceAfter { get; set; }
    public string? Description  { get; set; }
    public DateTime CreatedAt   { get; set; } = DateTime.UtcNow;
}

public class Withdrawal
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    [Required, MaxLength(60)] public string  Reference          { get; set; } = "";
    public int AmountInSparcoins  { get; set; }
    public int AmountInCurrency   { get; set; }
    [MaxLength(5)]  public string  Currency           { get; set; } = "GHS";
    [MaxLength(20)] public string? RecipientPhone     { get; set; }
    [MaxLength(20)] public string  Status             { get; set; } = "pending";
    [MaxLength(60)] public string? PaystackTransferId { get; set; }
    public DateTime  CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public class WithdrawalWebhookLog
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(60)] public string? Reference { get; set; }
    [MaxLength(40)] public string? Event     { get; set; }
    public string?  Payload    { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}

public class Match
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    [Required] public string PlayerIds    { get; set; } = "[]";  // JSON array of player IDs
    [Required, MaxLength(20)] public string Mode { get; set; } = "multiplayer";
    public int  StakeAmount   { get; set; }
    public int  GamesPerMatch { get; set; }
    public bool OgbaEnabled   { get; set; } = true;
    [MaxLength(20)] public string  Status      { get; set; } = "active";
    public Guid?    WinnerId    { get; set; }
    public DateTime CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public class Round
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    public Guid    MatchId     { get; set; }
    public int     RoundNumber { get; set; }
    [Required] public string Plays       { get; set; } = "{}"; // JSON: playerId → card
    public string?  WinnerId    { get; set; }
    public string?  LeadingCard { get; set; }
    public DateTime PlayedAt    { get; set; } = DateTime.UtcNow;
}
