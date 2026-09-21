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
                        type = "start",
                        room = current.Id,
                        epoch = current.Epoch,
                        startAt = result.StartAt,
                        serverTime = RaceRoom.NowMs()
                    }, json, context.RequestAborted);
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

                    if (!current!.TryUpdateState(peer, state.Value, out var slot))
                        break;

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
                        speed = state.Value.Speed,
                        lap = state.Value.Lap,
                        checkpoint = state.Value.Checkpoint,
                        routeProgress = state.Value.RouteProgress,
                        drifting = state.Value.Drifting,
                        boost = state.Value.Boost
                    }, json, context.RequestAborted);
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
    public RaceState? LastState { get; set; }
    public long LastStateAt { get; set; }

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

sealed class RaceRoom
{
    public const int MaxPlayers = 8;
    private readonly object _gate = new();
    private readonly WebPeer?[] _slots = new WebPeer?[MaxPlayers];

    public RaceRoom(string id) => Id = id;

    public string Id { get; }
    public int Epoch { get; private set; }
    public long? StartAt { get; private set; }

    public int Count
    {
        get { lock (_gate) return _slots.Count(x => x is not null); }
    }

    public bool TryJoin(WebPeer peer, string nickname, int kart, int character, out string? error)
    {
        lock (_gate)
        {
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
            _slots[slot] = peer;
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
            peer.LastState = null;

            if (_slots.All(x => x is null))
            {
                StartAt = null;
                Epoch = 0;
            }
        }
    }

    public void SetReady(WebPeer peer, bool ready)
    {
        lock (_gate)
        {
            if (peer.Slot >= 0 && peer.Slot < MaxPlayers && ReferenceEquals(_slots[peer.Slot], peer))
                peer.Ready = ready;
        }
    }

    public StartResult TryStart(WebPeer peer)
    {
        lock (_gate)
        {
            var host = Array.FindIndex(_slots, x => x is not null);
            if (host < 0 || peer.Slot != host)
                return StartResult.Fail("not-host", "Only the room host can start.");

            var players = _slots.Where(x => x is not null).Cast<WebPeer>().ToArray();
            if (players.Length < 1)
                return StartResult.Fail("empty-room", "Room has no players.");

            if (players.Any(x => x.Slot != host && !x.Ready))
                return StartResult.Fail("not-ready", "All non-host players must be ready.");

            Epoch++;
            StartAt = NowMs() + 3000;
            foreach (var player in players)
            {
                player.LastState = null;
                player.LastStateAt = 0;
            }
            return StartResult.Success(StartAt.Value);
        }
    }

    public bool TryUpdateState(WebPeer peer, RaceState state, out int slot)
    {
        lock (_gate)
        {
            slot = peer.Slot;
            if (slot < 0 || slot >= MaxPlayers || !ReferenceEquals(_slots[slot], peer))
                return false;

            var now = NowMs();
            if (peer.LastStateAt != 0 && now - peer.LastStateAt < 8)
                return false; // hard cap at roughly 125 Hz
            peer.LastState = state;
            peer.LastStateAt = now;
            return true;
        }
    }

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
                startAt = StartAt,
                players = _slots.Select((peer, slot) => peer is null ? null : new
                {
                    slot,
                    host = slot == host,
                    nickname = peer.Nickname,
                    kart = peer.Kart,
                    character = peer.Character,
                    ready = peer.Ready,
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

readonly record struct RaceState(
    long Seq,
    long Time,
    double[] Position,
    double[] Rotation,
    double[] Velocity,
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
            if (p is null || q is null || v is null)
                return null;

            var seq = message["seq"]?.GetValue<long?>() ?? 0;
            var time = message["t"]?.GetValue<long?>() ?? 0;
            var speed = message["speed"]?.GetValue<double?>() ?? 0;
            var lap = message["lap"]?.GetValue<int?>() ?? 0;
            var checkpoint = message["checkpoint"]?.GetValue<int?>() ?? 0;
            var routeProgress = message["routeProgress"]?.GetValue<double?>() ?? 0;
            var drifting = message["drifting"]?.GetValue<bool?>() ?? false;
            var boost = message["boost"]?.GetValue<bool?>() ?? false;

            if (!Finite(p) || !Finite(q) || !Finite(v) || !double.IsFinite(speed) || !double.IsFinite(routeProgress))
                return null;

            return new RaceState(seq, time, p, q, v, speed, lap, checkpoint, routeProgress, drifting, boost);
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
