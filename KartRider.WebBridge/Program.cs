using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var rooms = new ConcurrentDictionary<string, RaceRoom>(StringComparer.OrdinalIgnoreCase);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false
};

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20)
});

app.MapGet("/healthz", () => Results.Json(new
{
    ok = true,
    service = "Launcher_V2 WebBridge",
    now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
}));

app.MapGet("/api/rooms", () =>
{
    var snapshot = rooms.Values
        .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
        .Select(x => x.Snapshot())
        .ToArray();
    return Results.Json(snapshot, json);
});

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var peer = new WebPeer(socket);
    RaceRoom? room = null;

    try
    {
        await peer.SendAsync(new
        {
            type = "welcome",
            protocol = 1,
            maxPlayers = RaceRoom.MaxPlayers,
            serverTime = RaceRoom.NowMs()
        }, json, context.RequestAborted);

        while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
        {
            var text = await peer.ReceiveTextAsync(context.RequestAborted);
            if (text is null)
                break;

            JsonObject? message;
            try
            {
                message = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                await peer.SendErrorAsync("bad-json", "Invalid JSON.", json, context.RequestAborted);
                continue;
            }

            if (message is null)
            {
                await peer.SendErrorAsync("bad-message", "Message must be a JSON object.", json, context.RequestAborted);
                continue;
            }

            var type = message["type"]?.GetValue<string>()?.Trim().ToLowerInvariant();
            switch (type)
            {
                case "hello":
                {
                    if (room is not null)
                    {
                        await peer.SendErrorAsync("already-joined", "Leave before joining another room.", json, context.RequestAborted);
                        break;
                    }

                    var roomId = SanitizeRoom(message["room"]?.GetValue<string>());
                    var nickname = SanitizeNickname(message["nickname"]?.GetValue<string>());
                    var kart = message["kart"]?.GetValue<int?>() ?? 0;
                    var character = message["character"]?.GetValue<int?>() ?? 0;

                    room = rooms.GetOrAdd(roomId, id => new RaceRoom(id));
                    if (!room.TryJoin(peer, nickname, kart, character, out var error))
                    {
                        room = null;
                        await peer.SendErrorAsync("join-failed", error ?? "Unable to join.", json, context.RequestAborted);
                        break;
                    }

                    await room.BroadcastRoomAsync(json, context.RequestAborted);
                    break;
                }

                case "ready":
                {
                    if (!RequireRoom(peer, room, out var current))
                        break;
                    current!.SetReady(peer, message["ready"]?.GetValue<bool?>() ?? true);
                    await current.BroadcastRoomAsync(json, context.RequestAborted);
                    break;
                }

                case "loadout":
                {
                    if (!RequireRoom(peer, room, out var current))
                        break;

                    var kart = message["kart"]?.GetValue<int?>() ?? 0;
                    var character = message["character"]?.GetValue<int?>() ?? 0;
                    if (kart < 0 || character < 0)
                    {
                        await peer.SendErrorAsync("bad-loadout", "Loadout item IDs must be non-negative.", json, context.RequestAborted);
                        break;
                    }

                    var result = current!.TryUpdateLoadout(peer, kart, character);
                    if (!result.Ok)
                    {
                        await peer.SendErrorAsync(result.Code, result.Message, json, context.RequestAborted);
                        break;
                    }

                    await current.BroadcastRoomAsync(json, context.RequestAborted);
                    break;
                }

                case "config":
                {
                    if (!RequireRoom(peer, room, out var current))
                        break;

                    var config = RaceConfig.TryParse(message);
                    if (config is null)
                    {
                        await peer.SendErrorAsync("bad-config", "Room configuration is invalid.", json, context.RequestAborted);
                        break;
                    }

                    var result = current!.TryConfigure(peer, config.Value);
                    if (!result.Ok)
                    {
                        await peer.SendErrorAsync(result.Code, result.Message, json, context.RequestAborted);
                        break;
                    }

                    await current.BroadcastRoomAsync(json, context.RequestAborted);
                    break;
                }

                case "start":
                {
                    if (!RequireRoom(peer, room, out var current))
                        break;
                    var result = current!.TryStart(peer);
                    if (!result.Ok)
                    {
                        await peer.SendErrorAsync(result.Code, result.Message, json, context.RequestAborted);
                        break;
                    }

                    await current.BroadcastAsync(new
                    {
                        type = "prepare",
                        room = current.Id,
                        epoch = current.Epoch,
                        serverTime = RaceRoom.NowMs()
                    }, json, context.RequestAborted);
                    await current.BroadcastRoomAsync(json, context.RequestAborted);
                    break;
                }

                case "loaded":
                {
                    if (!RequireRoom(peer, room, out var current))
                        break;

                    var epoch = message["epoch"]?.GetValue<int?>() ?? -1;
                    var result = current!.TryMarkLoaded(peer, epoch);
                    if (!result.Ok)
                    {
                        await peer.SendErrorAsync(result.Code, result.Message, json, context.RequestAborted);
                        break;
                    }

                    if (result.GoAt > 0)
                    {
                        await current.BroadcastAsync(new
                        {
                            type = "go",
                            room = current.Id,
                            epoch = current.Epoch,
                            startAt = result.GoAt,
                            serverTime = RaceRoom.NowMs()
                        }, json, context.RequestAborted);
                    }

                    await current.BroadcastRoomAsync(json, context.RequestAborted);
                    break;
                }

                case "return":
                {
                    if (!RequireRoom(peer, room, out var current))
                        break;

                    var epoch = message["epoch"]?.GetValue<int?>() ?? -1;
                    var result = current!.TryReturn(peer, epoch);
                    if (!result.Ok)
                    {
                        await peer.SendErrorAsync(result.Code, result.Message, json, context.RequestAborted);
                        break;
                    }

                    await current.BroadcastRoomAsync(json, context.RequestAborted);
                    break;
                }

                case "state":
                {
                    if (!RequireRoom(peer, room, out var current))
                        break;
                    var state = RaceState.TryParse(message);
                    if (state is null)
                    {
                        await peer.SendErrorAsync("bad-state", "State packet is invalid.", json, context.RequestAborted);
                        break;
                    }

                    if (!current!.TryUpdateState(peer, state.Value, out var slot, out var phaseChanged))
                        break;

                    if (phaseChanged)
                        await current.BroadcastRoomAsync(json, context.RequestAborted);

                    await current.BroadcastExceptAsync(peer, new
                    {
                        type = "state",
                        room = current.Id,
                        epoch = current.Epoch,
                        slot,
                        serverTime = RaceRoom.NowMs(),
                        seq = state.Value.Seq,
                        t = state.Value.Time,
                        p = state.Value.Position,
                        q = state.Value.Rotation,
                        v = state.Value.Velocity,
                        yawRate = state.Value.YawRate,
                        hitbox = state.Value.Hitbox,
                        mass = state.Value.Mass,
                        pairBalance = state.Value.PairBalance,
                        motorcyclePresentation = state.Value.MotorcyclePresentation,
                        collisionAck = state.Value.CollisionAck,
                        boosterState = state.Value.BoosterState,
                        dualBoosterMode = state.Value.DualBoosterMode,
                        speed = state.Value.Speed,
                        lap = state.Value.Lap,
                        checkpoint = state.Value.Checkpoint,
                        routeProgress = state.Value.RouteProgress,
                        drifting = state.Value.Drifting,
                        boost = state.Value.Boost
                    }, json, context.RequestAborted);
                    break;
                }

                case "collision":
                {
                    if (!RequireRoom(peer, room, out var current))
                        break;

                    var collision = PairCollisionCorrection.TryParse(message);
                    if (collision is null)
                    {
                        await peer.SendErrorAsync("bad-collision", "Pair collision correction is invalid.", json, context.RequestAborted);
                        break;
                    }

                    if (!current!.TryGetCollisionTarget(peer, collision.Value.TargetSlot, out var target))
                        break;

                    var collisionTime = RaceRoom.NowMs();
                    var collisionId = current.NextCollisionSerial();
                    await Task.WhenAll(
                        peer.SendAsync(new
                        {
                            type = "collision",
                            room = current.Id,
                            epoch = current.Epoch,
                            sourceSlot = peer.Slot,
                            otherSlot = collision.Value.TargetSlot,
                            collisionId,
                            impulse = collision.Value.SourceDelta,
                            serverTime = collisionTime
                        }, json, context.RequestAborted),
                        target!.SendAsync(new
                        {
                            type = "collision",
                            room = current.Id,
                            epoch = current.Epoch,
                            sourceSlot = peer.Slot,
                            otherSlot = peer.Slot,
                            collisionId,
                            impulse = collision.Value.TargetDelta,
                            serverTime = collisionTime
                        }, json, context.RequestAborted)
                    );
                    break;
                }

                case "ping":
                    await peer.SendAsync(new
                    {
                        type = "pong",
                        clientTime = message["clientTime"]?.GetValue<long?>(),
                        serverTime = RaceRoom.NowMs()
                    }, json, context.RequestAborted);
                    break;

                case "leave":
                    return;

                default:
                    await peer.SendErrorAsync("unknown-type", $"Unknown message type: {type ?? "<null>"}", json, context.RequestAborted);
                    break;
            }
        }
    }
    catch (OperationCanceledException)
    {
    }
    catch (WebSocketException)
    {
    }
    finally
    {
        if (room is not null)
        {
            room.Leave(peer);
            if (room.Count == 0)
                rooms.TryRemove(new KeyValuePair<string, RaceRoom>(room.Id, room));
            else
                await room.BroadcastRoomAsync(json, CancellationToken.None);
        }
    }
});

var url = Environment.GetEnvironmentVariable("WEBBRIDGE_URLS") ?? "http://127.0.0.1:8093";
app.Run(url);

static bool RequireRoom(WebPeer peer, RaceRoom? room, out RaceRoom? current)
{
    current = room;
    return room is not null && peer.Slot >= 0;
}

static string SanitizeRoom(string? value)
{
    var room = string.IsNullOrWhiteSpace(value) ? "main" : value.Trim();
    room = new string(room.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(32).ToArray());
    return string.IsNullOrWhiteSpace(room) ? "main" : room;
}

static string SanitizeNickname(string? value)
{
    var nickname = string.IsNullOrWhiteSpace(value) ? "WebRider" : value.Trim();
    nickname = new string(nickname.Where(c => !char.IsControl(c)).Take(20).ToArray());
    return string.IsNullOrWhiteSpace(nickname) ? "WebRider" : nickname;
}

sealed class WebPeer
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public WebPeer(WebSocket socket) => Socket = socket;

    public WebSocket Socket { get; }
    public int Slot { get; set; } = -1;
    public string Nickname { get; set; } = "";
    public int Kart { get; set; }
    public int Character { get; set; }
    public bool Ready { get; set; }
    public int LoadedEpoch { get; set; } = -1;
    public int ReturnedEpoch { get; set; } = -1;
    public RaceState? LastState { get; set; }
    public long LastStateAt { get; set; }
    public long LastCollisionAt { get; set; }

    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await Socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new WebSocketException("Binary messages are not supported.");
            if (stream.Length + result.Count > 64 * 1024)
                throw new WebSocketException("Message too large.");

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    public async Task SendAsync(object payload, JsonSerializerOptions json, CancellationToken cancellationToken)
    {
        if (Socket.State != WebSocketState.Open)
            return;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, json);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (Socket.State == WebSocketState.Open)
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task SendErrorAsync(string code, string message, JsonSerializerOptions json, CancellationToken cancellationToken) =>
        SendAsync(new { type = "error", code, message }, json, cancellationToken);
}

readonly record struct StartResult(bool Ok, string Code, string Message, long StartAt)
{
    public static StartResult Success(long startAt) => new(true, "", "", startAt);
    public static StartResult Fail(string code, string message) => new(false, code, message, 0);
}

readonly record struct BarrierResult(bool Ok, string Code, string Message, long GoAt)
{
    public static BarrierResult Success(long goAt = 0) => new(true, "", "", goAt);
    public static BarrierResult Fail(string code, string message) => new(false, code, message, 0);
}

sealed class RaceRoom
{
    private long _collisionSerial;
    public const int MaxPlayers = 8;
    private readonly object _gate = new();
    private readonly WebPeer?[] _slots = new WebPeer?[MaxPlayers];

    public RaceRoom(string id) => Id = id;

    public string Id { get; }
    public int Epoch { get; private set; }
    public long Revision { get; private set; }
    public long? StartAt { get; private set; }
    public RaceConfig? Config { get; private set; }
    public string Phase { get; private set; } = "waiting";

    public int Count
    {
        get { lock (_gate) return _slots.Count(x => x is not null); }
    }

    public bool TryJoin(WebPeer peer, string nickname, int kart, int character, out string? error)
    {
        lock (_gate)
        {
            if (Phase != "waiting" && _slots.Any(x => x is not null))
            {
                error = "Race is already preparing or running.";
                return false;
            }

            if (_slots.Any(x => x is not null && x.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase)))
            {
                error = "Nickname is already in the room.";
                return false;
            }

            var slot = Array.FindIndex(_slots, x => x is null);
            if (slot < 0)
            {
                error = "Room is full.";
                return false;
            }

            peer.Slot = slot;
            peer.Nickname = nickname;
            peer.Kart = kart;
            peer.Character = character;
            peer.Ready = false;
            peer.LoadedEpoch = -1;
            peer.ReturnedEpoch = -1;
            _slots[slot] = peer;
            Revision++;
            error = null;
            return true;
        }
    }

    public void Leave(WebPeer peer)
    {
        lock (_gate)
        {
            if (peer.Slot >= 0 && peer.Slot < MaxPlayers && ReferenceEquals(_slots[peer.Slot], peer))
                _slots[peer.Slot] = null;
            peer.Slot = -1;
            peer.Ready = false;
            peer.LoadedEpoch = -1;
            peer.ReturnedEpoch = -1;
            peer.LastState = null;
            Revision++;

            var remaining = _slots.Where(x => x is not null).Cast<WebPeer>().ToArray();
            if (remaining.Length == 0)
            {
                StartAt = null;
                Epoch = 0;
                Config = null;
                Phase = "waiting";
            }
            else if (remaining.Length < 2 && Phase != "waiting")
            {
                StartAt = null;
                Phase = "waiting";
                foreach (var player in remaining)
                {
                    player.Ready = false;
                    player.LoadedEpoch = -1;
                    player.ReturnedEpoch = -1;
                    player.LastState = null;
                    player.LastStateAt = 0;
                player.LastCollisionAt = 0;
                }
            }
        }
    }

    public void SetReady(WebPeer peer, bool ready)
    {
        lock (_gate)
        {
            if (Phase != "waiting")
                return;

            if (peer.Slot >= 0 && peer.Slot < MaxPlayers && ReferenceEquals(_slots[peer.Slot], peer) && peer.Ready != ready)
            {
                peer.Ready = ready;
                Revision++;
            }
        }
    }

    public StartResult TryUpdateLoadout(WebPeer peer, int kart, int character)
    {
        lock (_gate)
        {
            if (Phase != "waiting")
                return StartResult.Fail("room-busy", "Loadout cannot change while preparing or racing.");

            if (peer.Slot < 0 || peer.Slot >= MaxPlayers || !ReferenceEquals(_slots[peer.Slot], peer))
                return StartResult.Fail("not-in-room", "Player is not in this room.");

            if (peer.Kart == kart && peer.Character == character)
                return StartResult.Success(0);

            peer.Kart = kart;
            peer.Character = character;
            peer.Ready = false;
            Revision++;
            return StartResult.Success(0);
        }
    }

    public StartResult TryConfigure(WebPeer peer, RaceConfig config)
    {
        lock (_gate)
        {
            var host = Array.FindIndex(_slots, x => x is not null);
            if (host < 0 || peer.Slot != host)
                return StartResult.Fail("not-host", "Only the room host can change room settings.");

            if (Phase != "waiting")
                return StartResult.Fail("room-busy", "Room settings cannot change while preparing or racing.");

            Config = config;
            Revision++;
            return StartResult.Success(0);
        }
    }

    public StartResult TryStart(WebPeer peer)
    {
        lock (_gate)
        {
            var host = Array.FindIndex(_slots, x => x is not null);
            if (host < 0 || peer.Slot != host)
                return StartResult.Fail("not-host", "Only the room host can start.");

            if (Phase != "waiting")
                return StartResult.Fail("room-busy", "Race is already preparing or running.");

            if (Config is null)
                return StartResult.Fail("missing-config", "Room configuration must be synchronized before start.");

            var players = _slots.Where(x => x is not null).Cast<WebPeer>().ToArray();
            if (players.Length < 2)
                return StartResult.Fail("not-enough-players", "Multiplayer requires at least two players.");

            if (players.Any(x => x.Slot != host && !x.Ready))
                return StartResult.Fail("not-ready", "All non-host players must be ready.");

            Epoch++;
            Revision++;
            Phase = "preparing";
            StartAt = null;
            foreach (var player in players)
            {
                player.LoadedEpoch = -1;
                player.ReturnedEpoch = -1;
                player.LastState = null;
                player.LastStateAt = 0;
                player.LastCollisionAt = 0;
            }

            return StartResult.Success(0);
        }
    }

    public BarrierResult TryMarkLoaded(WebPeer peer, int epoch)
    {
        lock (_gate)
        {
            if (epoch != Epoch)
                return BarrierResult.Fail("stale-epoch", "Loaded acknowledgement belongs to a stale race epoch.");

            if (Phase != "preparing")
                return BarrierResult.Fail("not-preparing", "Room is not waiting for race loading.");

            if (peer.Slot < 0 || peer.Slot >= MaxPlayers || !ReferenceEquals(_slots[peer.Slot], peer))
                return BarrierResult.Fail("not-in-room", "Player is not in this room.");

            if (peer.LoadedEpoch != epoch)
            {
                peer.LoadedEpoch = epoch;
                Revision++;
            }

            var players = _slots.Where(x => x is not null).Cast<WebPeer>().ToArray();
            if (players.Any(x => x.LoadedEpoch != epoch))
                return BarrierResult.Success();

            Phase = "countdown";
            Revision++;
            StartAt = NowMs() + 7000;
            return BarrierResult.Success(StartAt.Value);
        }
    }

    public BarrierResult TryReturn(WebPeer peer, int epoch)
    {
        lock (_gate)
        {
            if (epoch != Epoch)
                return BarrierResult.Fail("stale-epoch", "Return acknowledgement belongs to a stale race epoch.");

            if (peer.Slot < 0 || peer.Slot >= MaxPlayers || !ReferenceEquals(_slots[peer.Slot], peer))
                return BarrierResult.Fail("not-in-room", "Player is not in this room.");

            if (peer.ReturnedEpoch != epoch)
            {
                peer.ReturnedEpoch = epoch;
                Revision++;
            }

            var players = _slots.Where(x => x is not null).Cast<WebPeer>().ToArray();
            if (players.Any(x => x.ReturnedEpoch != epoch))
                return BarrierResult.Success();

            Phase = "waiting";
            Revision++;
            StartAt = null;
            foreach (var player in players)
            {
                player.Ready = false;
                player.LoadedEpoch = -1;
                player.ReturnedEpoch = -1;
                player.LastState = null;
                player.LastStateAt = 0;
                player.LastCollisionAt = 0;
            }

            return BarrierResult.Success();
        }
    }

    public bool TryUpdateState(WebPeer peer, RaceState state, out int slot, out bool phaseChanged)
    {
        lock (_gate)
        {
            phaseChanged = false;
            slot = peer.Slot;
            if (slot < 0 || slot >= MaxPlayers || !ReferenceEquals(_slots[slot], peer))
                return false;

            var now = NowMs();
            if (Phase == "countdown" && StartAt is not null && now >= StartAt.Value)
            {
                Phase = "racing";
                Revision++;
                phaseChanged = true;
            }

            if (Phase is not ("countdown" or "racing"))
                return false;

            if (peer.LastStateAt != 0 && now - peer.LastStateAt < 8)
                return false; // hard cap at roughly 125 Hz
            peer.LastState = state;
            peer.LastStateAt = now;
            return true;
        }
    }

    public bool TryGetCollisionTarget(WebPeer source, int targetSlot, out WebPeer? target)
    {
        lock (_gate)
        {
            target = null;
            if (Phase != "racing")
                return false;
            if (source.Slot < 0 || source.Slot >= MaxPlayers || !ReferenceEquals(_slots[source.Slot], source))
                return false;
            if (targetSlot < 0 || targetSlot >= MaxPlayers || targetSlot == source.Slot)
                return false;
            if (source.Slot >= targetSlot)
                return false;

            var now = NowMs();
            if (source.LastCollisionAt != 0 && now - source.LastCollisionAt < 25)
                return false;

            target = _slots[targetSlot];
            if (target is null)
                return false;

            source.LastCollisionAt = now;
            return true;
        }
    }

    public long NextCollisionSerial() => Interlocked.Increment(ref _collisionSerial);

    public object Snapshot()
    {
        lock (_gate)
        {
            var host = Array.FindIndex(_slots, x => x is not null);
            return new
            {
                id = Id,
                maxPlayers = MaxPlayers,
                epoch = Epoch,
                revision = Revision,
                phase = Phase,
                startAt = StartAt,
                config = Config,
                players = _slots.Select((peer, slot) => peer is null ? null : new
                {
                    slot,
                    host = slot == host,
                    nickname = peer.Nickname,
                    kart = peer.Kart,
                    character = peer.Character,
                    ready = peer.Ready,
                    loaded = peer.LoadedEpoch == Epoch && Phase != "waiting",
                    returned = peer.ReturnedEpoch == Epoch && Phase != "waiting",
                    state = peer.LastState
                }).Where(x => x is not null).ToArray()
            };
        }
    }

    public async Task BroadcastRoomAsync(JsonSerializerOptions json, CancellationToken cancellationToken)
    {
        var snapshot = Snapshot();
        await BroadcastAsync(new { type = "room", serverTime = NowMs(), room = snapshot }, json, cancellationToken);
    }

    public Task BroadcastAsync(object payload, JsonSerializerOptions json, CancellationToken cancellationToken)
    {
        WebPeer[] peers;
        lock (_gate) peers = _slots.Where(x => x is not null).Cast<WebPeer>().ToArray();
        return Task.WhenAll(peers.Select(x => SafeSendAsync(x, payload, json, cancellationToken)));
    }

    public Task BroadcastExceptAsync(WebPeer except, object payload, JsonSerializerOptions json, CancellationToken cancellationToken)
    {
        WebPeer[] peers;
        lock (_gate) peers = _slots.Where(x => x is not null && !ReferenceEquals(x, except)).Cast<WebPeer>().ToArray();
        return Task.WhenAll(peers.Select(x => SafeSendAsync(x, payload, json, cancellationToken)));
    }

    private static async Task SafeSendAsync(WebPeer peer, object payload, JsonSerializerOptions json, CancellationToken cancellationToken)
    {
        try { await peer.SendAsync(payload, json, cancellationToken); }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

readonly record struct RaceConfig(
    string TrackId,
    string MapPath,
    int Speed,
    int Booster)
{
    public static RaceConfig? TryParse(JsonObject message)
    {
        try
        {
            var trackId = message["trackId"]?.GetValue<string>()?.Trim() ?? "";
            var mapPath = message["mapPath"]?.GetValue<string>()?.Trim() ?? "";
            var speed = message["speed"]?.GetValue<int?>() ?? 7;
            var booster = message["booster"]?.GetValue<int?>() ?? 0;

            if (trackId.Length is < 1 or > 64 ||
                mapPath.Length is < 1 or > 192 ||
                !mapPath.StartsWith("track_/", StringComparison.OrdinalIgnoreCase) ||
                mapPath.Any(char.IsControl) ||
                trackId.Any(char.IsControl) ||
                speed is < 0 or > 16 ||
                booster is < 0 or > 16)
                return null;

            return new RaceConfig(trackId, mapPath, speed, booster);
        }
        catch
        {
            return null;
        }
    }
}

readonly record struct PairCollisionCorrection(int TargetSlot, double[] SourceDelta, double[] TargetDelta)
{
    public static PairCollisionCorrection? TryParse(JsonObject message)
    {
        try
        {
            var targetSlot = message["targetSlot"]?.GetValue<int?>() ?? -1;
            if (targetSlot is < 0 or >= RaceRoom.MaxPlayers)
                return null;

            var sourceDelta = ReadDelta(message["sourceDelta"]);
            var targetDelta = ReadDelta(message["targetDelta"]);
            if (sourceDelta is null || targetDelta is null)
                return null;

            var sourceMagnitude = Math.Sqrt(sourceDelta[0] * sourceDelta[0] + sourceDelta[1] * sourceDelta[1]);
            var targetMagnitude = Math.Sqrt(targetDelta[0] * targetDelta[0] + targetDelta[1] * targetDelta[1]);
            if (sourceMagnitude > 20 || targetMagnitude > 20 || (sourceMagnitude <= 0 && targetMagnitude <= 0))
                return null;

            return new PairCollisionCorrection(targetSlot, sourceDelta, targetDelta);
        }
        catch
        {
            return null;
        }
    }

    private static double[]? ReadDelta(JsonNode? node)
    {
        if (node is not JsonArray array || array.Count != 2)
            return null;

        var x = array[0]?.GetValue<double?>() ?? double.NaN;
        var z = array[1]?.GetValue<double?>() ?? double.NaN;
        return double.IsFinite(x) && double.IsFinite(z) ? new[] { x, z } : null;
    }
}

readonly record struct RaceState(
    long Seq,
    long Time,
    double[] Position,
    double[] Rotation,
    double[] Velocity,
    double YawRate,
    double[]? Hitbox,
    double Mass,
    double PairBalance,
    double MotorcyclePresentation,
    long CollisionAck,
    int BoosterState,
    int DualBoosterMode,
    double Speed,
    int Lap,
    int Checkpoint,
    double RouteProgress,
    bool Drifting,
    bool Boost)
{
    public static RaceState? TryParse(JsonObject message)
    {
        try
        {
            var p = ReadVector(message["p"], 3);
            var q = ReadVector(message["q"], 4);
            var v = ReadVector(message["v"], 3);
            var yawRate = message["yawRate"]?.GetValue<double?>() ?? 0;
            var hitbox = message["hitbox"] is null ? null : ReadVector(message["hitbox"], 3);
            if (p is null || q is null || v is null || (message["hitbox"] is not null && hitbox is null))
                return null;

            var seq = message["seq"]?.GetValue<long?>() ?? 0;
            var time = message["t"]?.GetValue<long?>() ?? 0;
            var mass = message["mass"]?.GetValue<double?>() ?? 100;
            var pairBalance = message["pairBalance"]?.GetValue<double?>() ?? 1;
            var motorcyclePresentation = message["motorcyclePresentation"]?.GetValue<double?>() ?? 0;
            var collisionAck = message["collisionAck"]?.GetValue<long?>() ?? 0;
            var boosterState = message["boosterState"]?.GetValue<int?>() ?? 0;
            var dualBoosterMode = message["dualBoosterMode"]?.GetValue<int?>() ?? 0;
            var speed = message["speed"]?.GetValue<double?>() ?? 0;
            var lap = message["lap"]?.GetValue<int?>() ?? 0;
            var checkpoint = message["checkpoint"]?.GetValue<int?>() ?? 0;
            var routeProgress = message["routeProgress"]?.GetValue<double?>() ?? 0;
            var drifting = message["drifting"]?.GetValue<bool?>() ?? false;
            var boost = message["boost"]?.GetValue<bool?>() ?? false;

            if (!Finite(p) || !Finite(q) || !Finite(v) ||
                !double.IsFinite(yawRate) || Math.Abs(yawRate) > 100 ||
                (hitbox is not null && (!Finite(hitbox) || hitbox.Any(x => x <= 0 || x > 10))) ||
                !double.IsFinite(mass) || mass is < 1 or > 10000 ||
                !double.IsFinite(pairBalance) || pairBalance is < 0 or > 4 ||
                !double.IsFinite(motorcyclePresentation) || Math.Abs(motorcyclePresentation) > 100 ||
                collisionAck < 0 ||
                boosterState is < 0 or > 64 || dualBoosterMode is < 0 or > 16 ||
                !double.IsFinite(speed) || !double.IsFinite(routeProgress))
                return null;

            return new RaceState(seq, time, p, q, v, yawRate, hitbox, mass, pairBalance, motorcyclePresentation, collisionAck, boosterState, dualBoosterMode, speed, lap, checkpoint, routeProgress, drifting, boost);
        }
        catch
        {
            return null;
        }
    }

    private static double[]? ReadVector(JsonNode? node, int count)
    {
        if (node is not JsonArray array || array.Count != count)
            return null;
        var result = new double[count];
        for (var i = 0; i < count; i++)
            result[i] = array[i]?.GetValue<double?>() ?? double.NaN;
        return result;
    }

    private static bool Finite(IEnumerable<double> values) => values.All(double.IsFinite);
}
