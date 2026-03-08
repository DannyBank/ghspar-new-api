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
    [HttpPost("create")]
    public async Task<IActionResult> Create([FromBody] CreateRoomRequest req)
    {
        if (!Guid.TryParse(req.PlayerId, out var playerId))
            return BadRequest(new { error = "Invalid player ID" });

        int totalStake = req.StakeAmount * req.GamesPerMatch;
        var player = await db.Players.FindAsync(playerId);
        if (player == null)      return NotFound(new { error = "Player not found" });
        if (player.SparCoins < totalStake)
            return BadRequest(new { error = $"Insufficient SparCoins (need {totalStake})" });

        player.SparCoins -= totalStake;

        var match = new Match
        {
            PlayerIds    = System.Text.Json.JsonSerializer.Serialize(new[] { req.PlayerId }),
            Mode         = "multiplayer",
            StakeAmount  = req.StakeAmount,
            GamesPerMatch = req.GamesPerMatch,
            OgbaEnabled  = req.OgbaEnabled,
        };
        db.Matches.Add(match);
        await db.SaveChangesAsync();

        var code = game.NewRoomCode();
        game.CreateRoom(code, match.Id, req.PlayerId, req.Alias,
            req.MaxPlayers, req.StakeAmount, req.GamesPerMatch, req.OgbaEnabled);

        return Ok(new { roomCode = code, matchId = match.Id });
    }

    // POST /api/rooms/{code}/join
    [HttpPost("{roomCode}/join")]
    public async Task<IActionResult> Join(string roomCode, [FromBody] JoinRoomRequest req)
    {
        if (!Guid.TryParse(req.PlayerId, out var playerId))
            return BadRequest(new { error = "Invalid player ID" });

        var room = game.GetRoom(roomCode);
        if (room == null) return NotFound(new { error = "Room not found" });

        int totalStake = room.StakeAmount * room.GamesPerMatch;
        var player = await db.Players.FindAsync(playerId);
        if (player == null)      return NotFound(new { error = "Player not found" });
        if (player.SparCoins < totalStake)
            return BadRequest(new { error = $"Insufficient SparCoins (need {totalStake})" });

        var (ok, error) = game.AddPlayer(roomCode, req.PlayerId, req.Alias);
        if (!ok) return BadRequest(new { error });

        player.SparCoins -= totalStake;
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
