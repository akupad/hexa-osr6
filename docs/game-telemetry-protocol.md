# Hexa Game Telemetry Protocol v1

Engine bridges send normalized motion telemetry to Hexa over UDP loopback.
Bridges never write TCode to the device directly. Hexa owns interpolation,
comfort limits, foreground gating, watchdogs, and emergency stop behavior.

## Transport

- Address: `127.0.0.1`
- Default port: `26781`
- Maximum datagram: 16 KiB
- Encoding: UTF-8 JSON
- Recommended rate: 20-50 Hz
- `timestamp`: current Unix time in milliseconds

## Session Rules

Each bridge startup creates a new random `sessionId`, preferably a UUID. The
first `sequence` is 1 and every later message increments it. Hexa binds the
session to the UDP sender, process name, and process ID. Duplicate, backwards,
stale, future, mismatched, and unauthenticated messages are rejected.

The bridge token is generated in the Hexa settings as `GameTelemetryToken`.
Do not log it or include it in crash reports.

## Motion Message

```json
{
  "protocolVersion": 1,
  "messageType": "motion",
  "token": "<GameTelemetryToken>",
  "sessionId": "6d466f95-5771-4d70-b5ca-c534e9ea2a32",
  "sequence": 1,
  "processId": 1234,
  "process": "ExampleGame",
  "engine": "Unity",
  "scene": "SceneName",
  "pose": "PoseName",
  "timestamp": 1786812345678,
  "confidence": 0.9,
  "intensity": 1.0,
  "transitionMs": 20,
  "axes": [50, 50, 50, 50, 50, 50]
}
```

`axes` use percentages in this fixed order:

1. `L0` up/down
2. `L1` forward/backward
3. `L2` left/right
4. `R0` twist
5. `R1` roll
6. `R2` pitch

If `axes` is omitted, Hexa maps semantic fields instead: `depth`, `surge`,
`sway`, `twist`, `roll`, and `pitch`. Semantic signed axes use `-1..1`; depth,
phase, and confidence use `0..1`. `intensity` is applied once, after the user
intensity setting, and capped by the active comfort profile.

## Stop Message

```json
{
  "protocolVersion": 1,
  "messageType": "stop",
  "token": "<GameTelemetryToken>",
  "sessionId": "6d466f95-5771-4d70-b5ca-c534e9ea2a32",
  "sequence": 42,
  "processId": 1234,
  "process": "ExampleGame",
  "timestamp": 1786812346789
}
```

An authenticated stop message for the active session bypasses confidence and
foreground checks and sends `DSTOP` immediately. If motion telemetry is absent
for 300 ms, Hexa also sends `DSTOP` and holds position. Automatic recentering
is never a disconnect fallback.

## Adapter Contract

1. Detect the engine and game-specific profile.
2. Read game state without controlling the OSR6 directly.
3. Normalize state into semantic fields or six axis percentages.
4. Send only while the intended scene is active.
5. Send a stop message before unload, scene exit, pause, or bridge shutdown.
6. Start a new session after process restart or bridge reload.

Raw TCode UDP is disabled by default. When explicitly enabled, Hexa accepts
only installed OSR6 axes, `DSTOP`, and one shared `I` interpolation duration.
`S`, `G`, mixed per-axis durations, vibration, and valve channels are rejected.
