// Services/GameService.cs
// Room state is kept in-memory for real-time speed.
// Match outcomes are persisted to PostgreSQL via a scoped DbContext.
// The hub context is injected via the constructor from DI — no circular dependency.

using System.Collections.Concurrent;
using GHSparApi.Data;
using GHSparApi.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GHSparApi.Services;

// ── Value types ───────────────────────────────────────────────────────────────
public record GameCard(string Suit, string Rank)
{
    private static readonly string[] RankOrder =
        ["six","seven","eight","nine","ten","jack","queen","king","ace"];
    private static readonly Dictionary<string, int> RankValues =
        RankOrder.Select((r, i) => (r, v: i + 6)).ToDictionary(x => x.r, x => x.v);

    public int Value => RankValues.GetValueOrDefault(Rank, 0);
}

public record RoundResult(int RoundNumber, string? WinnerId);

// ── Room state ─────────────────────────────────────────────────────────────────
public class RoomPlayer
{
    public required string Alias        { get; init; }
    public string?         ConnectionId { get; set; }
}

public class GameState
{
    public string    Phase        { get; set; } = "lobby";
    public int       CurrentGame  { get; set; } = 1;
    public int       CurrentRound { get; set; } = 1;
    public string?   LeadPlayerId { get; set; }
    public GameCard? LeadingCard  { get; set; }
    public Dictionary<string, GameCard>       RoundPlays   { get; set; } = [];
    public Dictionary<string, List<GameCard>> Hands        { get; set; } = [];
    public Dictionary<string, List<GameCard>> PriorHands   { get; set; } = [];
    public List<RoundResult>                  Rounds       { get; set; } = [];
    public List<string>                       Disqualified { get; set; } = [];
    public Dictionary<string, int>            GameScores   { get; set; } = [];
    public string?   Message       { get; set; }
    public string?   MatchWinnerId { get; set; }
}

public class Room
{
    public required Guid   MatchId       { get; init; }
    public required string HostId        { get; init; }
    public List<string>                     PlayerIds { get; } = [];
    public Dictionary<string, RoomPlayer>   Players   { get; } = [];
    public int  MaxPlayers    { get; init; }
    public int  StakeAmount   { get; init; }
    public int  GamesPerMatch { get; init; }
    public bool OgbaEnabled   { get; init; }
    public GameState State    { get; } = new();
}

// ── GameService ────────────────────────────────────────────────────────────────
// HubContext is injected via a Func<IHubClients> factory to avoid the circular
// dependency that would arise from referencing GameHub directly.
// In practice we inject IHubContext<dynamic> workaround via IHubContext abstraction
// stored as a field after first use (set by GameHub on first call).

public class GameService(IServiceScopeFactory scopeFactory)
{
    private readonly ConcurrentDictionary<string, Room> _rooms   = new();
    private readonly ConcurrentDictionary<string, (string Room, string Player)> _connMap = new();

    // Set by GameHub the first time it calls into us, giving us a way to send
    // messages without a compile-time reference to GameHub.
    private IClientProxy?        _allClients;   // not used — we use per-connection
    private Func<string, IClientProxy>? _clientById;   // connId → proxy
    private Func<string, IGroupManager>? _groups;

    // Simpler: store the hub's SendAsync capability as a delegate
    private Func<string, string, object, Task>? _sendToConn;
    private Func<string, string, object, Task>? _sendToGroup;

    /// Called by GameHub to wire up send delegates — avoids circular type reference.
    public void SetHubDelegates(
        Func<string, string, object, Task> sendToConn,
        Func<string, string, object, Task> sendToGroup)
    {
        _sendToConn  = sendToConn;
        _sendToGroup = sendToGroup;
    }

    private static readonly string[] Suits = ["hearts","diamonds","clubs","spades"];
    private static readonly string[] Ranks = ["six","seven","eight","nine","ten","jack","queen","king","ace"];

    // ── Room management ───────────────────────────────────────────────────────
    public string NewRoomCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        while (true)
        {
            var code = new string(Enumerable.Range(0, 6)
                .Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
            if (!_rooms.ContainsKey(code)) return code;
        }
    }

    public Room? GetRoom(string code) => _rooms.GetValueOrDefault(code.ToUpper());

    public Room CreateRoom(string code, Guid matchId, string hostId, string hostAlias,
        int maxPlayers, int stakeAmount, int gamesPerMatch, bool ogbaEnabled)
    {
        var room = new Room
        {
            MatchId       = matchId,
            HostId        = hostId,
            MaxPlayers    = maxPlayers,
            StakeAmount   = stakeAmount,
            GamesPerMatch = gamesPerMatch,
            OgbaEnabled   = ogbaEnabled,
        };
        room.PlayerIds.Add(hostId);
        room.Players[hostId]          = new RoomPlayer { Alias = hostAlias };
        room.State.GameScores[hostId] = 0;
        room.State.Message            = "Waiting for players…";
        _rooms[code.ToUpper()]        = room;
        return room;
    }

    public (bool Ok, string? Error) AddPlayer(string code, string playerId, string alias)
    {
        var room = GetRoom(code);
        if (room == null)                            return (false, "Room not found");
        if (room.PlayerIds.Count >= room.MaxPlayers) return (false, "Room is full");
        if (room.PlayerIds.Contains(playerId))       return (false, "Already in room");
        room.PlayerIds.Add(playerId);
        room.Players[playerId]          = new RoomPlayer { Alias = alias };
        room.State.GameScores[playerId] = 0;
        return (true, null);
    }

    public void RegisterConnection(string connectionId, string roomCode, string playerId)
    {
        _connMap[connectionId] = (roomCode.ToUpper(), playerId);
        var room = GetRoom(roomCode);
        if (room != null && room.Players.TryGetValue(playerId, out var rp))
            rp.ConnectionId = connectionId;
    }

    public void UnregisterConnection(string connectionId)
    {
        if (_connMap.TryRemove(connectionId, out var info))
        {
            var room = GetRoom(info.Room);
            if (room != null && room.Players.TryGetValue(info.Player, out var rp)
                && rp.ConnectionId == connectionId)
                rp.ConnectionId = null;
        }
    }

    // ── Internal send helpers ─────────────────────────────────────────────────
    private Task SendToConn(string connId, string method, object payload)
        => _sendToConn?.Invoke(connId, method, payload) ?? Task.CompletedTask;

    private Task SendToGroup(string group, string method, object payload)
        => _sendToGroup?.Invoke(group, method, payload) ?? Task.CompletedTask;

    // ── Deck ──────────────────────────────────────────────────────────────────
    private static List<GameCard> BuildDeck()
        => Suits.SelectMany(s => Ranks.Select(r => new GameCard(s, r)))
                .OrderBy(_ => Random.Shared.Next()).ToList();

    // ── Game flow ─────────────────────────────────────────────────────────────
    public async Task StartGame(string code)
    {
        var room = GetRoom(code);
        if (room == null) return;

        var gs   = room.State;
        var deck = BuildDeck();
        gs.Hands.Clear(); gs.PriorHands.Clear();
        for (int i = 0; i < room.PlayerIds.Count; i++)
        {
            var pid  = room.PlayerIds[i];
            var hand = deck.Skip(i * 5).Take(5).ToList();
            gs.Hands[pid]      = hand;
            gs.PriorHands[pid] = [.. hand];
        }
        gs.Rounds.Clear();
        gs.CurrentRound  = 1;
        gs.LeadPlayerId  = null;
        gs.Disqualified.Clear();
        gs.RoundPlays.Clear();
        gs.LeadingCard   = null;
        gs.Phase         = "dealing";
        gs.Message       = $"Game {gs.CurrentGame} of {room.GamesPerMatch} — cards dealt!";

        await BroadcastPersonalisedState(code);
        await Task.Delay(1000);
        await StartRound(code);
    }

    private async Task StartRound(string code)
    {
        var room = GetRoom(code);
        if (room == null) return;
        var gs = room.State;
        gs.RoundPlays.Clear();
        gs.LeadingCard  = null;
        gs.LeadPlayerId ??= room.PlayerIds[gs.CurrentRound % room.PlayerIds.Count];

        gs.Phase   = "playerTurn";
        gs.Message = $"Round {gs.CurrentRound}: {room.Players[gs.LeadPlayerId].Alias} leads";
        await BroadcastPersonalisedState(code);
    }

    public async Task<bool> PlayCard(string code, string playerId, GameCard card)
    {
        var room = GetRoom(code);
        if (room == null) return false;
        var gs = room.State;
        if (gs.Phase != "playerTurn")            return false;
        if (gs.RoundPlays.ContainsKey(playerId)) return false;
        if (gs.Disqualified.Contains(playerId))  return false;

        var hand   = gs.Hands.GetValueOrDefault(playerId);
        var inHand = hand?.FirstOrDefault(c => c.Suit == card.Suit && c.Rank == card.Rank);
        if (inHand == null) return false;

        hand!.Remove(inHand);
        gs.RoundPlays[playerId] = inHand;
        gs.LeadingCard ??= inHand;

        await SendToGroup(code, "CardPlayed", new
        {
            playerId,
            alias = room.Players[playerId].Alias,
            card  = new { inHand.Suit, inHand.Rank }
        });

        await AttemptResolve(code);
        return true;
    }

    private async Task AttemptResolve(string code)
    {
        var room = GetRoom(code);
        if (room == null) return;
        var gs       = room.State;
        var expected = room.PlayerIds.Where(p => !gs.Disqualified.Contains(p)).ToList();
        if (!expected.All(p => gs.RoundPlays.ContainsKey(p))) return;

        var leading = gs.LeadingCard!;

        if (room.OgbaEnabled)
        {
            foreach (var (pid, played) in gs.RoundPlays)
            {
                if (played.Suit != leading.Suit)
                {
                    var prior = gs.PriorHands.GetValueOrDefault(pid, []);
                    if (prior.Any(c => c.Suit == leading.Suit))
                        gs.Disqualified.Add(pid);
                }
            }
        }

        string? winnerId = null; int bestVal = -1;
        foreach (var (pid, played) in gs.RoundPlays)
        {
            if (gs.Disqualified.Contains(pid)) continue;
            if (played.Suit == leading.Suit && played.Value > bestVal)
                { bestVal = played.Value; winnerId = pid; }
        }

        gs.Rounds.Add(new RoundResult(gs.CurrentRound, winnerId));
        gs.LeadPlayerId = winnerId ?? room.PlayerIds[0];
        gs.Message = winnerId != null
            ? $"{room.Players[winnerId].Alias} won round {gs.CurrentRound}!"
            : $"No winner for round {gs.CurrentRound}.";
        gs.Phase = "roundResult";
        await BroadcastPersonalisedState(code);
        await Task.Delay(2000);

        bool handsEmpty = room.PlayerIds.All(p => !gs.Hands.GetValueOrDefault(p, []).Any());
        if (gs.CurrentRound >= 5 || handsEmpty || gs.Disqualified.Count > 0)
            await EndGame(code);
        else { gs.CurrentRound++; await StartRound(code); }
    }

    private async Task EndGame(string code)
    {
        var room = GetRoom(code);
        if (room == null) return;
        var gs = room.State;

        string? gameWinner = null;
        if (gs.Disqualified.Count > 0)
        {
            var rem = room.PlayerIds.Where(p => !gs.Disqualified.Contains(p)).ToList();
            if (rem.Count == 1) gameWinner = rem[0];
        }
        else if (gs.Rounds.Count > 0)
            gameWinner = gs.Rounds[^1].WinnerId;

        if (gameWinner != null)
            gs.GameScores[gameWinner] = gs.GameScores.GetValueOrDefault(gameWinner, 0) + 1;

        int gp = gs.CurrentGame;
        var scoreStr = string.Join("  |  ", room.PlayerIds
            .Select(p => $"{room.Players[p].Alias}: {gs.GameScores.GetValueOrDefault(p, 0)}"));
        gs.Message = gameWinner != null
            ? $"{room.Players[gameWinner].Alias} won Game {gp}!  [{scoreStr}]"
            : $"Game {gp} was a draw.  [{scoreStr}]";
        gs.Phase = "gameResult";
        await BroadcastPersonalisedState(code);
        await Task.Delay(3000);

        if (gp >= room.GamesPerMatch) await EndMatch(code);
        else { gs.CurrentGame++; await StartGame(code); }
    }

    private async Task EndMatch(string code)
    {
        var room = GetRoom(code);
        if (room == null) return;
        var gs = room.State;

        int topScore     = gs.GameScores.Values.DefaultIfEmpty(0).Max();
        var winners      = room.PlayerIds.Where(p => gs.GameScores.GetValueOrDefault(p, 0) == topScore).ToList();
        string? matchWin = winners.Count == 1 ? winners[0] : null;
        gs.MatchWinnerId = matchWin;
        int totalPot     = room.StakeAmount * room.GamesPerMatch * room.PlayerIds.Count;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var match = await db.Matches.FindAsync(room.MatchId);
        if (match != null)
        {
            match.Status      = "completed";
            match.WinnerId    = matchWin != null ? Guid.Parse(matchWin) : null;
            match.CompletedAt = DateTime.UtcNow;
        }

        if (matchWin != null)
        {
            var wp = await db.Players.FindAsync(Guid.Parse(matchWin));
            if (wp != null)
            {
                wp.SparCoins += totalPot;
                db.TransactionHistories.Add(new TransactionHistory
                {
                    UserId = wp.Id, TransactionType = "credit", Source = "gameReward",
                    Reference = $"MATCH-{room.MatchId}", Amount = totalPot,
                    BalanceAfter = wp.SparCoins, Description = $"Won match — pot of {totalPot} SC"
                });
            }
        }
        else
        {
            int share = totalPot / room.PlayerIds.Count;
            foreach (var pid in room.PlayerIds)
            {
                var p = await db.Players.FindAsync(Guid.Parse(pid));
                if (p == null) continue;
                p.SparCoins += share;
                db.TransactionHistories.Add(new TransactionHistory
                {
                    UserId = p.Id, TransactionType = "credit", Source = "draw",
                    Reference = $"MATCH-{room.MatchId}", Amount = share,
                    BalanceAfter = p.SparCoins, Description = $"Draw — {share} SC refunded"
                });
            }
        }

        await db.SaveChangesAsync();

        gs.Message = matchWin != null
            ? $"{room.Players[matchWin].Alias} wins the match! 🏆  +{totalPot} SC"
            : "It's a draw! Pot split.";
        gs.Phase = "gameOver";
        await BroadcastPersonalisedState(code);
    }

    // ── Personalised broadcast ────────────────────────────────────────────────
    public async Task BroadcastPersonalisedState(string code)
    {
        var room = GetRoom(code);
        if (room == null) return;
        foreach (var pid in room.PlayerIds)
        {
            var connId = room.Players[pid].ConnectionId;
            if (connId == null) continue;
            await SendToConn(connId, "GameState", BuildState(code, pid));
        }
    }

    public object BuildState(string code, string viewerId)
    {
        var room = GetRoom(code)!;
        var gs   = room.State;
        var players = room.PlayerIds.Select(pid =>
        {
            var hand = gs.Hands.GetValueOrDefault(pid, []);
            return new
            {
                id        = pid,
                alias     = room.Players[pid].Alias,
                handCount = hand.Count,
                hand      = pid == viewerId
                    ? (object)hand.Select(c => new { c.Suit, c.Rank }).ToList()
                    : Array.Empty<object>(),
                gameScore = gs.GameScores.GetValueOrDefault(pid, 0),
            };
        }).ToList();

        return new
        {
            type          = "game_state",
            roomCode      = code,
            phase         = gs.Phase,
            currentGame   = gs.CurrentGame,
            gamesPerMatch = room.GamesPerMatch,
            currentRound  = gs.CurrentRound,
            leadPlayerId  = gs.LeadPlayerId,
            leadingCard   = gs.LeadingCard != null
                ? (object)new { gs.LeadingCard.Suit, gs.LeadingCard.Rank } : null,
            roundPlays    = gs.RoundPlays.ToDictionary(
                kv => kv.Key, kv => (object)new { kv.Value.Suit, kv.Value.Rank }),
            message       = gs.Message,
            players,
            disqualified  = gs.Disqualified,
            matchWinnerId = gs.MatchWinnerId,
            gameScores    = gs.GameScores,
        };
    }
}
