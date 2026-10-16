# StS2 Mod Devkit

Private development SDK for Slay the Spire 2. The target is fast hot module replacement (HMR) and plugins that extend commands, events, diagnostics, and development workflows through versioned contracts.

## Status

Repository foundation only. No SDK runtime, extension API, deploy command, or player package is implemented yet. Existing upstream HMR code has passed an isolated research test run; live integration is pending. See the [research report](https://github.com/Xaaalera/sts2-workshop/blob/main/docs/research/2026-10-16-sdk-landscape.md) and [backlog](https://github.com/Xaaalera/sts2-workshop/blob/main/docs/backlog.md).

## Standalone use

```powershell
git clone https://github.com/Xaaalera/sts2-mod-devkit.git
cd sts2-mod-devkit
```

Read [AGENTS.md](AGENTS.md). There are no source/build dependencies to initialize yet. Future build instructions must resolve game assemblies from an ignored local configuration, without requiring the parent workspace or copying proprietary assemblies into Git.

BaseLib and RitsuLib are selected integration libraries. Choose package/runtime versions against the installed game API; their source submodules in the workspace are reference checkouts, not implicit build dependencies.

## Boundaries

- SDK extensions use explicit lifecycle and compatibility contracts.
- Resident game hooks, player runtime, developer tooling, and reloadable behavior have separate ownership.
- Mod behavior remains independent of SDK development internals.
- Finished mods work without this source checkout or developer processes.

## Related projects

[Workspace](https://github.com/Xaaalera/sts2-workshop) · [Reload probe](https://github.com/Xaaalera/sts2-reload-probe) · [BaseLib](https://github.com/Alchyr/BaseLib-StS2) · [RitsuLib](https://github.com/BAKAOLC/STS2-RitsuLib) · [Working standards](https://github.com/Xaaalera/claude-skills).

[Xaaalera](https://github.com/Xaaalera) · [Email](mailto:dampirsimpl@gmail.com) · [Telegram](https://t.me/Victima). Unofficial fan project, not affiliated with Mega Crit.
