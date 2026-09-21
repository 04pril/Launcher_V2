# KartRider.WebBridge

Cross-platform WebSocket bridge for the browser version of `middle_race`.

This project intentionally does **not** replace the Windows Launcher executable. It mirrors the
native Launcher's eight-player room boundary so the browser client can be developed and deployed
on Linux while the native `Launcher_V2` remains untouched.

## Protocol

Connect to `/ws`.

Client messages:

- `hello`: `{ "type":"hello", "room":"main", "nickname":"Rider", "kart":0, "character":0 }`
- `ready`: `{ "type":"ready", "ready":true }`
- `start`: host-only; schedules a race three seconds from server time.
- `state`: pose/race snapshot. Fields: `seq,t,p[3],q[4],v[3],speed,lap,checkpoint,routeProgress,drifting,boost`.
- `ping`: time-sync probe.
- `leave`: disconnect cleanly.

Server messages:

- `welcome`
- `room`
- `start`
- `state`
- `pong`
- `error`

Rooms are capped at **8 players**, matching the original Launcher_V2 player slots. The first
occupied slot is the room host. All non-host players must be ready before the host can start.

## Run

```bash
dotnet run --project KartRider.WebBridge/KartRider.WebBridge.csproj
```

Default listen address is `http://127.0.0.1:8093`. Override with:

```bash
WEBBRIDGE_URLS=http://127.0.0.1:8093 dotnet run --project KartRider.WebBridge/KartRider.WebBridge.csproj
```

Health endpoint: `/healthz`.
