// Controllers/RoomsController.cs
using GHSparApi.Data;
using GHSparApi.Models;
using GHSparApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace GHSparApi.Controllers;

[ApiController]
[Route("api/rooms")]
public class RoomsController(AppDbContext db, GameService game) : ControllerBase
{
    // POST /api/rooms/create
    // Stake is NOT deducted here — it is deducted for all players when the host
    // calls StartGame, so no one loses coins just for creating or joining a room.
    [HttpPost("create")]
    public async Task<IActionResult> Create([FromBody] CreateRoomRequest req)
    {
        if (!Guid.TryParse(req.PlayerId, out var playerId))
            return BadRequest(new { error = "Invalid player ID" });

        int totalStake = req.StakeAmount * req.GamesPerMatch;
        var player = await db.Players.FindAsync(playerId);
        if (player == null) return NotFound(new { error = "Player not found" });

        // Balance check only — no deduction yet
        if (player.SparCoins < totalStake)
            return BadRequest(new { error = $"Insufficient SparCoins (need {totalStake})" });

        var match = new Match
        {
            PlayerIds     = System.Text.Json.JsonSerializer.Serialize(new[] { req.PlayerId.Trim().ToLower() }),
            Mode          = "multiplayer",
            StakeAmount   = req.StakeAmount,
            GamesPerMatch = req.GamesPerMatch,
            OgbaEnabled   = req.OgbaEnabled,
        };
        db.Matches.Add(match);
        await db.SaveChangesAsync();

        var code = game.NewRoomCode();
        var pid  = req.PlayerId.Trim().ToLower();
        game.CreateRoom(code, match.Id, pid, req.Alias,
            req.MaxPlayers, req.StakeAmount, req.GamesPerMatch, req.OgbaEnabled);

        return Ok(new { roomCode = code, matchId = match.Id });
    }

    // POST /api/rooms/{code}/join
    // Stake is NOT deducted here — deducted at game start for all players together.
    [HttpPost("{roomCode}/join")]
    public async Task<IActionResult> Join(string roomCode, [FromBody] JoinRoomRequest req)
    {
        if (!Guid.TryParse(req.PlayerId, out var playerId))
            return BadRequest(new { error = "Invalid player ID" });

        var room = game.GetRoom(roomCode);
        if (room == null) return NotFound(new { error = "Room not found" });

        int totalStake = room.StakeAmount * room.GamesPerMatch;
        var player = await db.Players.FindAsync(playerId);
        if (player == null) return NotFound(new { error = "Player not found" });

        // Balance check only — no deduction yet
        if (player.SparCoins < totalStake)
            return BadRequest(new { error = $"Insufficient SparCoins (need {totalStake})" });

        var (ok, error) = game.AddPlayer(roomCode, req.PlayerId, req.Alias);
        if (!ok) return BadRequest(new { error });

        await db.SaveChangesAsync();

        return Ok(new
        {
            status        = "joined",
            roomCode      = roomCode.ToUpper(),
            players       = room.PlayerIds.Select(p => new { id = p, alias = room.Players[p].Alias }),
            maxPlayers    = room.MaxPlayers,
            stakeAmount   = room.StakeAmount,
            gamesPerMatch = room.GamesPerMatch,
            ogbaEnabled   = room.OgbaEnabled,
        });
    }

    // GET /api/rooms/{code}
    [HttpGet("{roomCode}")]
    public IActionResult Get(string roomCode)
    {
        var room = game.GetRoom(roomCode);
        if (room == null) return NotFound(new { error = "Room not found" });

        return Ok(new
        {
            roomCode      = roomCode.ToUpper(),
            players       = room.PlayerIds.Select(p => new { id = p, alias = room.Players[p].Alias }),
            maxPlayers    = room.MaxPlayers,
            stakeAmount   = room.StakeAmount,
            gamesPerMatch = room.GamesPerMatch,
            ogbaEnabled   = room.OgbaEnabled,
            phase         = room.State.Phase,
        });
    }
}
