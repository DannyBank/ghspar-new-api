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

        var wasDisconnected = room.Players[pid].IsDisconnected;

        game.RegisterConnection(Context.ConnectionId, code, pid);
        await Groups.AddToGroupAsync(Context.ConnectionId, code);

        Console.WriteLine($"[Hub] JoinRoom code={code} pid={pid} reconnect={wasDisconnected}");

        if (wasDisconnected)
        {
            // Reconnection — notify others and broadcast full state (timer cancel handled in RegisterConnection)
            await Clients.OthersInGroup(code).SendAsync("PlayerReconnected", new
            {
                playerId = pid,
                alias    = room.Players[pid].Alias,
            });
        }
        else
        {
            // Fresh join — notify others
            await Clients.OthersInGroup(code).SendAsync("PlayerJoined", new
            {
                playerId    = pid,
                alias       = room.Players[pid].Alias,
                playerCount = room.PlayerIds.Count,
                maxPlayers  = room.MaxPlayers,
            });
        }

        // Always send full personalised game state to the joining/rejoining player
        await Clients.Caller.SendAsync("GameState", game.BuildState(code, pid));
    }

    public async Task StartGame(string roomCode, string playerId)
    {
        var code = roomCode.Trim().ToUpper();
        var pid  = playerId.Trim().ToLower();
        Console.WriteLine($"[Hub] StartGame code={code} pid={pid}");
        var room = game.GetRoom(code);
        if (room == null) { await Clients.Caller.SendAsync("Error", $"Room '{code}' not found"); return; }
        if (room.HostId != pid) { await Clients.Caller.SendAsync("Error", $"Only host can start"); return; }
        if (room.PlayerIds.Count < 2) { await Clients.Caller.SendAsync("Error", "Need ≥2 players"); return; }
        _ = Task.Run(() => game.StartGame(code));
    }

    public async Task PlayCard(string roomCode, string playerId, string suit, string rank)
    {
        var code = roomCode.Trim().ToUpper();
        var pid  = playerId.Trim().ToLower();
        Console.WriteLine($"[Hub] PlayCard code={code} pid={pid} {suit}/{rank}");
        var ok = await game.PlayCard(code, pid, new GameCard(suit, rank));
        if (!ok) await Clients.Caller.SendAsync("Error", "Invalid move");
    }

    public override Task OnDisconnectedAsync(Exception? ex)
    {
        // Use HandleDisconnect which starts the grace period instead of immediately cleaning up
        game.HandleDisconnect(Context.ConnectionId);
        return base.OnDisconnectedAsync(ex);
    }
}
