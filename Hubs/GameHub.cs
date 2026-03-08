// Hubs/GameHub.cs
using GHSparApi.Services;
using Microsoft.AspNetCore.SignalR;

namespace GHSparApi.Hubs;

public class GameHub(GameService game) : Hub
{
    public async Task JoinRoom(string roomCode, string playerId)
    {
        var code = roomCode.Trim().ToUpper();
        var pid  = playerId.Trim().ToLower();

        var room = game.GetRoom(code);
        if (room == null)
        {
            await Clients.Caller.SendAsync("Error", $"Room '{code}' not found");
            return;
        }

        var found = room.PlayerIds.Any(p => p == pid);
        if (!found)
        {
            await Clients.Caller.SendAsync("Error",
                $"Player '{pid}' not in room '{code}'. Known: {string.Join(", ", room.PlayerIds)}");
            return;
        }

        game.RegisterConnection(Context.ConnectionId, code, pid);
        await Groups.AddToGroupAsync(Context.ConnectionId, code);

        Console.WriteLine($"[Hub] JoinRoom code={code} pid={pid} connId={Context.ConnectionId}");

        await Clients.OthersInGroup(code).SendAsync("PlayerJoined", new
        {
            playerId    = pid,
            alias       = room.Players[pid].Alias,
            playerCount = room.PlayerIds.Count,
            maxPlayers  = room.MaxPlayers,
        });

        await Clients.Caller.SendAsync("GameState", game.BuildState(code, pid));
    }

    public async Task StartGame(string roomCode, string playerId)
    {
        var code = roomCode.Trim().ToUpper();
        var pid  = playerId.Trim().ToLower();

        Console.WriteLine($"[Hub] StartGame code={code} pid={pid}");

        var room = game.GetRoom(code);
        if (room == null)
        {
            await Clients.Caller.SendAsync("Error", $"Room '{code}' not found");
            return;
        }
        if (room.HostId != pid)
        {
            await Clients.Caller.SendAsync("Error", $"Only host can start. host={room.HostId} you={pid}");
            return;
        }
        if (room.PlayerIds.Count < 2)
        {
            await Clients.Caller.SendAsync("Error", $"Need ≥2 players, have {room.PlayerIds.Count}");
            return;
        }

        // Fire-and-forget on thread pool so the hub call returns immediately
        _ = Task.Run(() => game.StartGame(code));
    }

    public async Task PlayCard(string roomCode, string playerId, string suit, string rank)
    {
        var code = roomCode.Trim().ToUpper();
        var pid  = playerId.Trim().ToLower();

        Console.WriteLine($"[Hub] PlayCard code={code} pid={pid} suit={suit} rank={rank}");

        var ok = await game.PlayCard(code, pid, new GameCard(suit, rank));
        if (!ok)
            await Clients.Caller.SendAsync("Error", "Invalid move");
    }

    public override Task OnDisconnectedAsync(Exception? ex)
    {
        game.UnregisterConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(ex);
    }
}
