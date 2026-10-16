# StS2 Mod Devkit

Private development SDK for Slay the Spire 2. The target is fast hot module replacement (HMR) and plugins that extend commands, events, diagnostics, and development workflows through versioned contracts.

## Status

The first Windows development slice implements trusted extension loading, advertised commands, reload, unload, structured diagnostics, and an owned-game launcher. It compiles the pinned spirectl contracts/reload code and adds a game-context adapter. Eight isolated filesystem integration tests pass. A bounded live probe changed A to B in the same PID/game thread, collected the previous generation, retained B on a malformed replacement, and recovered successfully. One warm edit-build-deploy-reload-observe cycle measured 1.406 seconds; this is not a latency guarantee.

See the [research report](https://github.com/Xaaalera/sts2-workshop/blob/main/docs/research/2026-10-16-sdk-landscape.md) for evidence, failed hypotheses, and remaining limits.

## Standalone use

```powershell
git clone https://github.com/Xaaalera/sts2-mod-devkit.git
cd sts2-mod-devkit
git submodule update --init upstream/spirectl
```

Read [AGENTS.md](AGENTS.md). Requires Windows PowerShell 5.1+, .NET SDK 9, Git, and GitHub CLI for fetching runtime libraries. Node/npm are development review dependencies. The parent workspace is optional.

This slice pins BaseLib 3.4.7 and RitsuLib 0.6.7's API-0.107.1 compatibility package. Game references come from your installed v0.107.1; proprietary game assemblies are never packaged. Package lock files pin compile dependencies. RitsuLib automatic deployment is disabled; complete runtime releases are verified against recorded SHA-256 hashes before extraction.

## Use the SDK

Clone the [probe](https://github.com/Xaaalera/sts2-reload-probe) separately, then run these commands from the SDK root:

```powershell
./scripts/sdk.ps1 -Action init -GameDirectory 'D:/Games/Slay the Spire 2' -ProbeDirectory 'D:/Code/sts2-reload-probe'
./scripts/sdk.ps1 -Action build
./scripts/sdk.ps1 -Action prepare
./scripts/sdk.ps1 -Action copy-config
./scripts/sdk.ps1 -Action launch
./scripts/sdk.ps1 -Action reload
./scripts/sdk.ps1 -Action invoke -Command probe
./scripts/sdk.ps1 -Action status
./scripts/sdk.ps1 -Action unload
```

`init` saves paths in ignored `.local/config.json`. `prepare` creates a separate game copy and profile, installs the two libraries and SDK host, and stages the probe. Allow roughly one installed-game copy of free disk space. It requires the owned test game to be closed; the owner's normal Steam game may remain running. `copy-config` explicitly copies the sole Steam account's settings and profile preferences; with multiple accounts pass `-SettingsFile`. It sets a 1280x720 window, skips intro/mod-consent screens, and keeps the main menu. It does not copy run/progress saves.

`launch` opens the owned game with Steam disabled and file-backed logs. It refuses to start a second test process under its own state directory. An unrelated owner Steam game is permitted and is never attached to or controlled. The handshake reports PID, creation time, executable, session ID, user directory, window mode, and library versions. `status`, `reload`, `invoke`, and `unload` verify the owned process identity. A timeout means unknown outcome: inspect the existing session rather than automatically replaying a mutation.

After changing `src/Behavior.cs` in the probe, build its extension project and run `reload` then `invoke`. The response reports the actual generation/value. The CLI's automatic artifact recipe currently owns only `reload-probe`; the host itself accepts other explicitly installed extension IDs under `.local/plugins/<id>/<id>.dll`.

Detailed operation history is under `.local/sessions/<session>/events.jsonl`; status and correlated response files are in that same directory. CLI failures reference `.local/cli-errors.jsonl`. SDK operation errors use the Google status envelope with registered reasons from `src/Runtime/SdkErrors.cs` and English messages in `Errors.resx`.

| Error reason | Status | Recovery |
| --- | --- | --- |
| `INVALID_REQUEST` | INVALID_ARGUMENT / 400 | Check plugin ID, advertised command, and arguments. |
| `SESSION_MISMATCH` | FAILED_PRECONDITION / 409 | Read the current owned-session identity. |
| `PLUGIN_NOT_LOADED` | NOT_FOUND / 404 | Load the intended extension first. |
| `RELOAD_FAILED` | FAILED_PRECONDITION / 409 | Inspect diagnostics and repair the candidate; verify retained behavior. |
| `PLUGIN_ID_MISMATCH` | FAILED_PRECONDITION / 409 | Match the manifest ID to the installation directory. |
| `SDK_BUSY` | ABORTED / 409 | Wait for the original operation's outcome; do not replay it. |
| `INTERNAL_FAILURE` | INTERNAL / 500 | Inspect the linked local diagnostic log before retrying. |

## Extension contract v0

- Implement `HotMod.Contracts.IHotLogic` from the local `Sts2.ModDevkit.Contracts` package. `build` creates it in `.local/package-feed`; it is not published to a public package registry.
- Supply `HotLogicManifest` with matching directory/mod ID, display name, logic version, contract version, and capabilities. Advertise each command as `command:<name>`.
- `Initialize` receives a logging/clock host and generation context. `OnHook` receives command ID, generation, and string arguments. `Dispose` releases generation-owned resources.
- The game dispatcher serializes operations and preserves the game's synchronization context across reload awaits. A busy operation is rejected rather than overlapped.
- Keep serialized data in shared contract/BCL types. Default serializer metadata caches can retain plugin-defined anonymous types and prevent unload; the regression suite exercises invocation before replacement.
- Extensions are trusted executable code, not sandboxed. Do not start background work, keep global registrations, or manipulate game objects without an explicit lifetime/thread protocol. Automatic gameplay-event subscription and a general host-service registry are not implemented in this slice.

Game-facing code and JSON infrastructure remain in Godot's existing assembly context. The small shared contract is resolved through Default so collectible logic uses the same interface identity. Native mod loading and library changes remain startup-bound.

## Verify and package

```powershell
dotnet test tests/Runtime/Sts2.Devkit.Tests.csproj
./scripts/sdk.ps1 -Action package-player
```

Tests cover command execution, failed replacement retention, identity/path rejection, command capability enforcement, explicit unload, old-generation collection after invocation, and public error normalization. They use real temporary files and collectible assemblies; no game is required.

The player package contains only the probe's native adapter and manifest. Install BaseLib/RitsuLib separately as declared dependencies. Player behavior is compiled from the same `Behavior.cs` and has no SDK runtime, contracts, workspace-path, or developer-process dependency. Workshop uploading is not part of this slice.

## Limits

- Supported local baseline: Windows, StS2 v0.107.1, .NET 9. Multiplayer, alternate game versions, general resource reload, and arbitrary installed-DLL replacement are unverified.
- Host/bootstrap/contracts changes require a justified restart. Reloadable behavior changes use the existing session.
- A probe lifetime's generation counter starts again after unload; use session/request IDs and the unload receipt when interpreting history.
- RitsuLib emits a missing Russian settings-translation diagnostic with the copied Russian configuration; startup and the tested SDK operations still complete. This is an open upstream localization issue.
- Review hooks are installed and proven to reject unattested pushes. CI is configured for secret scanning; hosted execution and branch protection are not yet verified.

## Boundaries

- SDK extensions use explicit lifecycle and compatibility contracts.
- Resident game hooks, player runtime, developer tooling, and reloadable behavior have separate ownership.
- Mod behavior remains independent of SDK development internals.
- Finished mods work without this source checkout or developer processes.

## Related projects

[Workspace](https://github.com/Xaaalera/sts2-workshop) · [Reload probe](https://github.com/Xaaalera/sts2-reload-probe) · [BaseLib](https://github.com/Alchyr/BaseLib-StS2) · [RitsuLib](https://github.com/BAKAOLC/STS2-RitsuLib) · [Working standards](https://github.com/Xaaalera/claude-skills).

[Xaaalera](https://github.com/Xaaalera) · [Email](mailto:dampirsimpl@gmail.com) · [Telegram](https://t.me/Victima). Unofficial fan project, not affiliated with Mega Crit.
