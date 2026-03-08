// Controllers/TransactionControllers.cs
using GHSparApi.Data;
using GHSparApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace GHSparApi.Controllers;

// ── Purchases ─────────────────────────────────────────────────────────────────
[ApiController]
[Route("api/purchase")]
public class PurchasesController(AppDbContext db) : ControllerBase
{
    private static readonly Dictionary<int, int> GhsDiscounts = new()
        { {10,18},{20,34},{50,80},{100,150},{200,280} };

    private Guid? RequestPlayerId =>
        Request.Headers.TryGetValue("X-Player-Id", out var v) && Guid.TryParse(v, out var g) ? g : null;

    // POST /api/purchase/initiate
    [HttpPost("initiate")]
    public async Task<IActionResult> Initiate([FromBody] PurchaseInitiateRequest req)
    {
        var pid = RequestPlayerId;
        if (pid == null) return Unauthorized();

        int ghs = (GhsDiscounts.TryGetValue(req.Amount, out var d) ? d : req.Amount * 2) * 100;
        var txRef = $"SC-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        db.PurchaseTransactions.Add(new PurchaseTransaction
        {
            UserId            = pid.Value,
            Reference         = txRef,
            PaymentMethod     = req.PaymentMethod,
            AmountInSparcoins = req.Amount,
            AmountInCurrency  = ghs,
        });
        await db.SaveChangesAsync();

        return Ok(new
        {
            message              = "Transaction initiated",
            transactionReference = txRef,
            paystackRedirectUrl  = $"https://paystack.com/pay/{txRef}"
        });
    }

    // POST /api/purchase/webhook  (Paystack calls this on charge.success)
    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook([FromBody] JsonElement payload)
    {
        var reference = payload.GetProperty("data").GetProperty("reference").GetString() ?? "";
        var eventName = payload.GetProperty("event").GetString() ?? "";

        db.PaymentWebhookLogs.Add(new PaymentWebhookLog
            { Reference = reference, Event = eventName, Payload = payload.ToString() });

        if (eventName == "charge.success")
        {
            var tx = await db.PurchaseTransactions.FirstOrDefaultAsync(t => t.Reference == reference);
            if (tx is { Status: "pending" })
            {
                tx.Status      = "success";
                tx.CompletedAt = DateTime.UtcNow;

                var player = await db.Players.FindAsync(tx.UserId);
                if (player != null)
                {
                    player.SparCoins += tx.AmountInSparcoins;
                    db.TransactionHistories.Add(new TransactionHistory
                    {
                        UserId = tx.UserId, TransactionType = "credit", Source = "purchase",
                        Reference = reference, Amount = tx.AmountInSparcoins,
                        BalanceAfter = player.SparCoins,
                        Description = $"Purchased {tx.AmountInSparcoins} SparCoins"
                    });
                }
            }
        }

        await db.SaveChangesAsync();
        return Ok(new { status = "ok" });
    }

    // GET /api/purchase/history
    [HttpGet("history")]
    public async Task<IActionResult> History()
    {
        var pid = RequestPlayerId;
        if (pid == null) return Unauthorized();
        var rows = await db.PurchaseTransactions
            .Where(t => t.UserId == pid.Value)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
        return Ok(rows);
    }
}

// ── Withdrawals ───────────────────────────────────────────────────────────────
[ApiController]
[Route("api/withdrawals")]
public class WithdrawalsController(AppDbContext db) : ControllerBase
{
    private Guid? RequestPlayerId =>
        Request.Headers.TryGetValue("X-Player-Id", out var v) && Guid.TryParse(v, out var g) ? g : null;

    // POST /api/withdrawals/initiate
    [HttpPost("initiate")]
    public async Task<IActionResult> Initiate([FromBody] WithdrawalRequest req)
    {
        var pid = RequestPlayerId;
        if (pid == null) return Unauthorized();

        var player = await db.Players.FindAsync(pid.Value);
        if (player == null)              return NotFound();
        if (player.SparCoins < 10)       return BadRequest(new { error = "Minimum balance of 10 SparCoins required" });
        if (req.Amount < 5)              return BadRequest(new { error = "Minimum withdrawal is 5 SparCoins" });
        if (player.SparCoins < req.Amount) return BadRequest(new { error = "Insufficient balance" });

        var wdRef = $"WD-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        player.SparCoins -= req.Amount;

        db.Withdrawals.Add(new Withdrawal
        {
            UserId            = pid.Value,
            Reference         = wdRef,
            AmountInSparcoins = req.Amount,
            AmountInCurrency  = req.Amount * 200,  // 1 SC = GHS 2 = 200 pesewas
            RecipientPhone    = req.RecipientPhone,
        });
        db.TransactionHistories.Add(new TransactionHistory
        {
            UserId = pid.Value, TransactionType = "debit", Source = "withdrawal",
            Reference = wdRef, Amount = req.Amount,
            BalanceAfter = player.SparCoins,
            Description  = $"Withdrawal to MoMo {req.RecipientPhone}"
        });
        await db.SaveChangesAsync();
        // TODO: trigger Paystack Transfer API

        return Ok(new { status = "success", message = "Withdrawal initiated", transactionReference = wdRef });
    }

    // POST /api/withdrawals/webhook
    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook([FromBody] JsonElement payload)
    {
        var reference = payload.GetProperty("data").GetProperty("reference").GetString() ?? "";
        var eventName = payload.GetProperty("event").GetString() ?? "";

        db.WithdrawalWebhookLogs.Add(new WithdrawalWebhookLog
            { Reference = reference, Event = eventName, Payload = payload.ToString() });

        var wd = await db.Withdrawals.FirstOrDefaultAsync(w => w.Reference == reference);
        if (wd != null)
        {
            if (eventName == "transfer.success")
            {
                wd.Status      = "success";
                wd.CompletedAt = DateTime.UtcNow;
            }
            else if (eventName == "transfer.failed")
            {
                wd.Status = "failed";
                var p = await db.Players.FindAsync(wd.UserId);
                if (p != null)
                {
                    p.SparCoins += wd.AmountInSparcoins;
                    db.TransactionHistories.Add(new TransactionHistory
                    {
                        UserId = p.Id, TransactionType = "credit", Source = "refund",
                        Reference = reference, Amount = wd.AmountInSparcoins,
                        BalanceAfter = p.SparCoins, Description = "Withdrawal failed — refunded"
                    });
                }
            }
        }

        await db.SaveChangesAsync();
        return Ok(new { status = "ok" });
    }

    // GET /api/withdrawals/history
    [HttpGet("history")]
    public async Task<IActionResult> History()
    {
        var pid = RequestPlayerId;
        if (pid == null) return Unauthorized();
        var rows = await db.Withdrawals
            .Where(w => w.UserId == pid.Value)
            .OrderByDescending(w => w.CreatedAt)
            .ToListAsync();
        return Ok(new { status = "success", withdrawals = rows });
    }
}

// ── Transactions ──────────────────────────────────────────────────────────────
[ApiController]
[Route("api/transactions")]
public class TransactionsController(AppDbContext db) : ControllerBase
{
    private Guid? RequestPlayerId =>
        Request.Headers.TryGetValue("X-Player-Id", out var v) && Guid.TryParse(v, out var g) ? g : null;

    // GET /api/transactions
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var pid = RequestPlayerId;
        if (pid == null) return Unauthorized();
        var rows = await db.TransactionHistories
            .Where(t => t.UserId == pid.Value)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
        return Ok(rows);
    }
}
