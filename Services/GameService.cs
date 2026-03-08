// Services/GameService.cs
using System.Collections.Concurrent;
using GHSparApi.Data;
using GHSparApi.Hubs;
using GHSparApi.Models;
using Microsoft.AspNetCore.SignalR;

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
    // Reconnection tracking
    public bool            IsDisconnected    { get; set; } = false;
    public DateTime?       DisconnectedAt    { get; set; }
    public CancellationTokenSource? ForfeitCts { get; set; }
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
    // Reconnection UI state
    public string?   ReconnectingPlayerId    { get; set; }
    public int?      ReconnectSecondsLeft    { get; set; }
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
public class GameService(IHubContext<GameHub> hub, IServiceScopeFactory scopeFactory, GameSettingsService settings)
{
    private readonly ConcurrentDictionary<string, Room> _rooms   = new();
    private readonly ConcurrentDictionary<string, (string Room, string Player)> _connMap = new();

    // Reads from GameSettingsService singleton — changes made via PATCH /api/admin/settings
    // take effect immediately for all new disconnections, no restart required.
    private int GracePeriodSeconds => settings.ReconnectGracePeriodSeconds;

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
        var hid  = hostId.Trim().ToLower();
        var room = new Room
        {
            MatchId = matchId, HostId = hid,
            MaxPlayers = maxPlayers, StakeAmount = stakeAmount,
            GamesPerMatch = gamesPerMatch, OgbaEnabled = ogbaEnabled,
        };
        room.PlayerIds.Add(hid);
        room.Players[hid]          = new RoomPlayer { Alias = hostAlias };
        room.State.GameScores[hid] = 0;
        room.State.Message         = "Waiting for players…";
        _rooms[code.ToUpper()]     = room;
        return room;
    }

    public (bool Ok, string? Error) AddPlayer(string code, string playerId, string alias)
    {
        var room = GetRoom(code);
        if (room == null)                            return (false, "Room not found");
        if (room.PlayerIds.Count >= room.MaxPlayers) return (false, "Room is full");
        var npid = playerId.Trim().ToLower();
        if (room.PlayerIds.Any(p => p == npid))      return (false, "Already in room");
        room.PlayerIds.Add(npid);
        room.Players[npid]          = new RoomPlayer { Alias = alias };
        room.State.GameScores[npid] = 0;
        return (true, null);
    }

    public void RegisterConnection(string connectionId, string roomCode, string playerId)
    {
        _connMap[connectionId] = (roomCode.ToUpper(), playerId);
        var room = GetRoom(roomCode);
        if (room == null) return;
        if (!room.Players.TryGetValue(playerId, out var rp)) return;

        rp.ConnectionId   = connectionId;
        rp.IsDisconnected = false;
        rp.DisconnectedAt = null;

        // Cancel any pending forfeit timer for this player
        rp.ForfeitCts?.Cancel();
        rp.ForfeitCts = null;
    }

    // Called by GameHub.OnDisconnectedAsync — starts grace period instead of
    // immediately ending the game.
    public void HandleDisconnect(string connectionId)
    {
        if (!_connMap.TryRemove(connectionId, out var info)) return;

        var room = GetRoom(info.Room);
        if (room == null) return;
        if (!room.Players.TryGetValue(info.Player, out var rp)) return;
        if (rp.ConnectionId != connectionId) return; // stale connection, ignore

        rp.ConnectionId   = null;
        rp.IsDisconnected = true;
        rp.DisconnectedAt = DateTime.UtcNow;

        // Only trigger grace period if a game is actually in progress
        var gs = room.State;
        if (gs.Phase == "lobby" || gs.Phase == "gameOver") return;

        Console.WriteLine($"[Reconnect] {rp.Alias} disconnected from {info.Room} — grace={GracePeriodSeconds}s");

        var cts = new CancellationTokenSource();
        rp.ForfeitCts = cts;

        // Fire-and-forget countdown — broadcasts tick every second
        _ = Task.Run(() => RunGracePeriod(info.Room, info.Player, rp.Alias, GracePeriodSeconds, cts.Token));
    }

    private async Task RunGracePeriod(string roomCode, string playerId, string alias, int totalSeconds, CancellationToken ct)
    {
        var room = GetRoom(roomCode);
        if (room == null) return;
        var gs = room.State;

        gs.ReconnectingPlayerId = playerId;

        for (int s = totalSeconds; s >= 0; s--)
        {
            if (ct.IsCancellationRequested)
            {
                // Player reconnected — clear overlay and resume
                Console.WriteLine($"[Reconnect] {alias} reconnected to {roomCode}");
                gs.ReconnectingPlayerId = null;
                gs.ReconnectSecondsLeft = null;
                gs.Message = $"{alias} reconnected!";
                await BroadcastPersonalisedState(roomCode);
                return;
            }

            gs.ReconnectSecondsLeft = s;
            if (s % 5 == 0 || s <= 10) // broadcast every 5s, then every second for last 10
            {
                gs.Message = s > 0
                    ? $"⚠️ {alias} disconnected — {s}s to reconnect…"
                    : $"⏱ {alias} failed to reconnect.";
                await BroadcastPersonalisedState(roomCode);
            }

            if (s == 0) break;
            await Task.Delay(1000, CancellationToken.None); // don't cancel the delay itself
        }

        if (ct.IsCancellationRequested) return; // reconnected during last delay

        // Grace period expired — forfeit
        Console.WriteLine($"[Reconnect] {alias} forfeited {roomCode} after {totalSeconds}s");
        gs.ReconnectingPlayerId = null;
        gs.ReconnectSecondsLeft = null;

        var room2 = GetRoom(roomCode);
        if (room2 == null) return;

        // Award match to remaining player(s)
        var remaining = room2.PlayerIds.Where(p => p != playerId).ToList();
        string? winnerId = remaining.Count == 1 ? remaining[0] : null;

        room2.State.MatchWinnerId = winnerId;
        room2.State.Phase = "gameOver";
        room2.State.Message = winnerId != null
            ? $"{alias} disconnected. {room2.Players[winnerId].Alias} wins by forfeit! 🏆"
            : $"{alias} disconnected. Match cancelled.";

        await BroadcastPersonalisedState(roomCode);

        // Persist payout
        if (winnerId != null)
        {
            int totalPot = room2.StakeAmount * room2.GamesPerMatch * room2.PlayerIds.Count;
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (Guid.TryParse(winnerId, out var wGuid))
            {
                var wp = await db.Players.FindAsync(wGuid);
                if (wp != null)
                {
                    wp.SparCoins += totalPot;
                    db.TransactionHistories.Add(new TransactionHistory
                    {
                        UserId = wp.Id, TransactionType = "credit", Source = "forfeitWin",
                        Reference = $"FORFEIT-{room2.MatchId}", Amount = totalPot,
                        BalanceAfter = wp.SparCoins,
                        Description = $"Won by forfeit vs {alias} — +{totalPot} SC"
                    });
                    await db.SaveChangesAsync();
                }
            }
        }

        _rooms.TryRemove(roomCode, out _);
    }

    // ── Send helpers ──────────────────────────────────────────────────────────
    private Task SendToConn(string connId, string method, object payload)
        => hub.Clients.Client(connId).SendAsync(method, payload);

    private Task SendToGroup(string group, string method, object payload)
        => hub.Clients.Group(group).SendAsync(method, payload);

    // ── Deck ──────────────────────────────────────────────────────────────────
    private static List<GameCard> BuildDeck()
        => Suits.SelectMany(s => Ranks.Select(r => new GameCard(s, r)))
                .OrderBy(_ => Random.Shared.Next()).ToList();

    // ── Game flow ─────────────────────────────────────────────────────────────
    public async Task StartGame(string code)
    {
        var room = GetRoom(code);
        if (room == null) return;
        Console.WriteLine($"[Game] StartGame code={code} players={string.Join(",", room.PlayerIds)}");
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
        gs.LeadPlayerId  = room.PlayerIds[Random.Shared.Next(room.PlayerIds.Count)];
        gs.Disqualified.Clear();
        gs.RoundPlays.Clear();
        gs.LeadingCard   = null;
        gs.Phase         = "playing";
        gs.Message       = $"Game {gs.CurrentGame}/{room.GamesPerMatch} — {room.Players[gs.LeadPlayerId].Alias} leads.";
        await BroadcastPersonalisedState(code);
    }

    public async Task<bool> PlayCard(string code, string playerId, GameCard card)
    {
        var room = GetRoom(code);
        if (room == null) return false;
        var gs = room.State;
        if (gs.Phase != "playing") return false;
        if (gs.Disqualified.Contains(playerId)) return false;
        if (gs.RoundPlays.ContainsKey(playerId)) return false;

        // Block non-leaders until lead card is played
        if (gs.LeadingCard == null && playerId != gs.LeadPlayerId) return false;

        var hand = gs.Hands.GetValueOrDefault(playerId, []);
        var match = hand.FirstOrDefault(c =>
            c.Suit.Equals(card.Suit, StringComparison.OrdinalIgnoreCase) &&
            c.Rank.Equals(card.Rank, StringComparison.OrdinalIgnoreCase));
        if (match == null) return false;

        hand.Remove(match);
        gs.RoundPlays[playerId] = match;
        if (gs.LeadingCard == null) gs.LeadingCard = match;

        await BroadcastPersonalisedState(code);
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
        else
        {
            gs.CurrentRound++;
            gs.RoundPlays.Clear();
            gs.LeadingCard = null;
            foreach (var pid in room.PlayerIds)
                gs.PriorHands[pid] = [.. gs.Hands.GetValueOrDefault(pid, [])];
            gs.Phase = "playing";
            gs.Message = $"Round {gs.CurrentRound} — {room.Players[gs.LeadPlayerId!].Alias} leads.";
            await BroadcastPersonalisedState(code);
        }
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
        _rooms.TryRemove(code, out _);
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
            var rp   = room.Players[pid];
            return new
            {
                id             = pid,
                alias          = rp.Alias,
                handCount      = hand.Count,
                hand           = pid == viewerId
                    ? (object)hand.Select(c => new { suit = c.Suit, rank = c.Rank }).ToList()
                    : Array.Empty<object>(),
                gameScore      = gs.GameScores.GetValueOrDefault(pid, 0),
                isDisconnected = rp.IsDisconnected,
            };
        }).ToList();

        return new
        {
            type                    = "game_state",
            roomCode                = code,
            phase                   = gs.Phase,
            currentGame             = gs.CurrentGame,
            gamesPerMatch           = room.GamesPerMatch,
            currentRound            = gs.CurrentRound,
            leadPlayerId            = gs.LeadPlayerId,
            leadingCard             = gs.LeadingCard != null
                ? (object)new { suit = gs.LeadingCard.Suit, rank = gs.LeadingCard.Rank } : null,
            roundPlays              = gs.RoundPlays.ToDictionary(
                kv => kv.Key, kv => (object)new { suit = kv.Value.Suit, rank = kv.Value.Rank }),
            message                 = gs.Message,
            players,
            disqualified            = gs.Disqualified,
            matchWinnerId           = gs.MatchWinnerId,
            gameScores              = gs.GameScores,
            reconnectingPlayerId    = gs.ReconnectingPlayerId,
            reconnectSecondsLeft    = gs.ReconnectSecondsLeft,
        };
    }
}
