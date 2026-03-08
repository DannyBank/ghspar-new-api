// Hubs/GameHub.cs
using GHSparApi.Services;
using Microsoft.AspNetCore.SignalR;

namespace GHSparApi.Hubs;

public class GameHub(GameService game) : Hub
{
    public async Task JoinRoom(string roomCode, string playerId)
    {
        var room = game.GetRoom(roomCode);
        if (room == null || !room.PlayerIds.Contains(playerId))
        {
            await Clients.Caller.SendAsync("Error", "Room not found or you are not in this room");
            return;
        }

        game.RegisterConnection(Context.ConnectionId, roomCode, playerId);
        await Groups.AddToGroupAsync(Context.ConnectionId, roomCode.ToUpper());

        await Clients.OthersInGroup(roomCode.ToUpper()).SendAsync("PlayerJoined", new
        {
            playerId,
            alias       = room.Players[playerId].Alias,
            playerCount = room.PlayerIds.Count,
            maxPlayers  = room.MaxPlayers
        });

        await Clients.Caller.SendAsync("GameState", game.BuildState(roomCode, playerId));
    }

    public async Task StartGame(string roomCode, string playerId)
    {
        var room = game.GetRoom(roomCode);
        if (room == null) return;

        if (room.HostId != playerId)
        {
            await Clients.Caller.SendAsync("Error", "Only the host can start the game");
            return;
        }
        if (room.PlayerIds.Count < 2)
        {
            await Clients.Caller.SendAsync("Error", "Need at least 2 players to start");
            return;
        }

        _ = Task.Run(() => game.StartGame(roomCode));
    }

    public async Task PlayCard(string roomCode, string playerId, string suit, string rank)
    {
        var ok = await game.PlayCard(roomCode, playerId, new GameCard(suit, rank));
        if (!ok)
            await Clients.Caller.SendAsync("Error", "Invalid move");
    }

    public override async Task OnConnectedAsync()
    {
        // Wire up send delegates so GameService can push messages without
        // holding a compile-time reference to GameHub.
        game.SetHubDelegates(
            sendToConn:  (connId, method, payload) => Clients.Client(connId).SendAsync(method, payload),
            sendToGroup: (group,  method, payload) => Clients.Group(group).SendAsync(method, payload)
        );
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? ex)
    {
        game.UnregisterConnection(Context.ConnectionId);
        await base.OnDisconnectedAsync(ex);
    }
}
