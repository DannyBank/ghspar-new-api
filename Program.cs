// Program.cs
using GHSparApi.Data;
using GHSparApi.Hubs;
using GHSparApi.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(opt =>
        opt.JsonSerializerOptions.PropertyNamingPolicy =
            System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
    c.SwaggerDoc("v1", new() { Title = "GHSpar API", Version = "v3" }));

// PostgreSQL
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// SignalR
builder.Services.AddSignalR(opt =>
{
    opt.EnableDetailedErrors  = builder.Environment.IsDevelopment();
    opt.KeepAliveInterval     = TimeSpan.FromSeconds(15);
    opt.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
});

builder.Services.AddSingleton<GameSettingsService>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    return new GameSettingsService
    {
        ReconnectGracePeriodSeconds = cfg.GetValue<int?>("GameSettings:ReconnectGracePeriodSeconds") ?? 60,
        RoundResultDelaySeconds     = cfg.GetValue<int?>("GameSettings:RoundResultDelaySeconds") ?? 4,
        AdminKey                    = cfg.GetValue<string>("GameSettings:AdminKey") ?? "changeme",
    };
});
builder.Services.AddSingleton<GameService>();
builder.Services.AddScoped<AuthService>();

// CORS — SignalR requires AllowCredentials() which cannot be combined with
// AllowAnyOrigin(). List every origin Flutter web may run from.
builder.Services.AddCors(opt => opt.AddDefaultPolicy(p =>
    p.WithOrigins(
        "http://localhost:5000",
        "http://localhost:5001",
        "http://localhost:8080",   // flutter run -d chrome
        "http://localhost:7070",
        "http://localhost:3000",
        "http://127.0.0.1:8080",
        "http://127.0.0.1:5000"
    )
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.MapControllers();
app.MapHub<GameHub>("/gamehub");
app.MapGet("/health", () => new
{
    status    = "ok",
    service   = "GHSpar API v3 (C# / ASP.NET Core 8)",
    timestamp = DateTime.UtcNow
});

// Create all PostgreSQL tables from the EF model if they don't exist yet.
// To use proper migrations instead: dotnet ef migrations add Init && dotnet ef database update
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.Run();
