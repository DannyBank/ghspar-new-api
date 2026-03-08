// Services/GameSettingsService.cs
// Singleton that holds mutable runtime settings.
// Seed values come from appsettings.json on startup; can be changed live via
// PATCH /api/admin/settings without restarting the server.

namespace GHSparApi.Services;

public class GameSettingsService
{
    /// How long (seconds) a disconnected player has to reconnect before forfeiting.
    /// Default loaded from "GameSettings:ReconnectGracePeriodSeconds" in appsettings.json.
    public int ReconnectGracePeriodSeconds { get; set; } = 60;

    /// Secret key required for admin endpoints.
    /// Set via "GameSettings:AdminKey" in appsettings.json or an env var.
    public string AdminKey { get; set; } = "changeme";
}
