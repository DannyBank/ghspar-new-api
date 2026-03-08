// Hubs/GameHub.cs
using GHSparApi.Services;
using Microsoft.AspNetCore.SignalR;

namespace GHSparApi.Hubs;

public class GameHub(GameService game) : Hub
{
    public override async Task OnConnectedAsync()
    {
        // Wire send delegates once on every new connection so GameService
        // can push messages without a compile-time ref to GameHub.
        game.SetHubDelegates(
            sendToConn:  (connId, method, payload) => Clients.Client(connId).SendAsync(method, payload),
            sendToGroup: (group,  method, payload) => Clients.Group(group).SendAsync(method, payload)
        );
        await base.OnConnectedAsync();
    }

    /// Called by Flutter immediately after the WS connection is established.
    /// roomCode and playerId must exactly match what the HTTP create/join returned.
    public async Task JoinRoom(string roomCode, string playerId)
    {
        var code = roomCode.Trim().ToUpper();
        var pid  = playerId.Trim().ToLower(); // Guids from C# are lowercase

        var room = game.GetRoom(code);
        if (room == null)
        {
            await Clients.Caller.SendAsync("Error", $"Room '{code}' not found");
            return;
        }

        // Normalise stored IDs to lowercase for comparison
        var found = room.PlayerIds.Any(p => p.ToLower() == pid);
        if (!found)
        {
            await Clients.Caller.SendAsync("Error",
                $"Player '{pid}' is not registered in room '{code}'. " +
                $"Known players: {string.Join(", ", room.PlayerIds)}");
            return;
        }

        // Use the canonical casing stored in the room
        var canonicalPid = room.PlayerIds.First(p => p.ToLower() == pid);

        game.RegisterConnection(Context.ConnectionId, code, canonicalPid);
        await Groups.AddToGroupAsync(Context.ConnectionId, code);

        await Clients.OthersInGroup(code).SendAsync("PlayerJoined", new
        {
            playerId    = canonicalPid,
            alias       = room.Players[canonicalPid].Alias,
            playerCount = room.PlayerIds.Count,
            maxPlayers  = room.MaxPlayers
        });

        await Clients.Caller.SendAsync("GameState", game.BuildState(code, canonicalPid));
    }

    public async Task StartGame(string roomCode, string playerId)
    {
        var code = roomCode.Trim().ToUpper();
        var pid  = playerId.Trim().ToLower();

        var room = game.GetRoom(code);
        if (room == null) return;

        var canonicalPid = room.PlayerIds.FirstOrDefault(p => p.ToLower() == pid) ?? pid;

        if (room.HostId.ToLower() != pid)
        {
            await Clients.Caller.SendAsync("Error", "Only the host can start the game");
            return;
        }
        if (room.PlayerIds.Count < 2)
        {
            await Clients.Caller.SendAsync("Error", "Need at least 2 players to start");
            return;
        }

        _ = Task.Run(() => game.StartGame(code));
    }

    public async Task PlayCard(string roomCode, string playerId, string suit, string rank)
    {
        var code = roomCode.Trim().ToUpper();
        var pid  = playerId.Trim().ToLower();

        var room = game.GetRoom(code);
        var canonicalPid = room?.PlayerIds.FirstOrDefault(p => p.ToLower() == pid) ?? pid;

        var ok = await game.PlayCard(code, canonicalPid, new GameCard(suit, rank));
        if (!ok)
            await Clients.Caller.SendAsync("Error", "Invalid move");
    }

    public override async Task OnDisconnectedAsync(Exception? ex)
    {
        game.UnregisterConnection(Context.ConnectionId);
        await base.OnDisconnectedAsync(ex);
    }
}
