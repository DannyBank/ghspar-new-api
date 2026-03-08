// Data/AppDbContext.cs
using Microsoft.EntityFrameworkCore;
using GHSparApi.Models;

namespace GHSparApi.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Player>               Players               { get; set; }
    public DbSet<LoginLog>             LoginLogs             { get; set; }
    public DbSet<OtpLog>               OtpLogs               { get; set; }
    public DbSet<PurchaseTransaction>  PurchaseTransactions  { get; set; }
    public DbSet<PaymentWebhookLog>    PaymentWebhookLogs    { get; set; }
    public DbSet<TransactionHistory>   TransactionHistories  { get; set; }
    public DbSet<Withdrawal>           Withdrawals           { get; set; }
    public DbSet<WithdrawalWebhookLog> WithdrawalWebhookLogs { get; set; }
    public DbSet<Match>                Matches               { get; set; }
    public DbSet<Round>                Rounds                { get; set; }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Player>().HasIndex(p => p.Username).IsUnique();
        b.Entity<Player>().HasIndex(p => p.Msisdn).IsUnique();
        b.Entity<PurchaseTransaction>().HasIndex(t => t.Reference).IsUnique();
        b.Entity<Withdrawal>().HasIndex(w => w.Reference).IsUnique();
    }
}
