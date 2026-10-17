# SDK working instructions

- Keep repository content in English; conversation may be Russian.
- Native game facilities are the first choice: reuse actual game APIs/components/scenes/assets, palette, fonts, animations and interactions before library adapters or custom code. A custom implementation requires a demonstrated gap recorded in existing research; copying native colors/textures alone does not establish native reuse.
- Read README, the current [workspace handoff](https://github.com/Xaaalera/sts2-workshop/blob/main/docs/handoff.md), and relevant retained research before work.
- Search retained findings, then primary internet sources and existing libraries before choosing an implementation. Record revisions, applicability, verification, and limits.
- Use the installed applicable guides from [Xaaalera/claude-skills](https://github.com/Xaaalera/claude-skills): SOLID, Ockham, documentation, tests, errors, and atomic commits. Read the guide before claiming application.
- The complete shared baseline lives in the workspace [AGENTS.md](https://github.com/Xaaalera/sts2-workshop/blob/main/AGENTS.md); follow it in linked or standalone development. Local SDK-specific rules add to that baseline.
- BaseLib and RitsuLib are selected. Reuse them; pin compatible package/runtime versions and update the workspace technology inventory. Source checkout pins do not prove runtime compatibility.
- Define versioned extension contracts around real plugin needs. Own registration, event subscriptions, tasks, state, failure recovery, and disposal explicitly.
- Separate developer tools, player runtime, adapters, and mod behavior. Do not expose transport or HMR internals to gameplay algorithms or UI.
- Keep game/framework type identity stable across reloads. Marshal game calls onto the appropriate game thread. Verify old-generation cleanup and actual collection separately from activation.
- Use only owned test processes with PID/creation/executable/session guards. Authorized input may be used; stop immediately when the owner needs it and resume only on instruction.
- Reuse the owned game/session and saved scenarios. Restart only for a demonstrated need; ending a turn is not a reason to close it.
- Use programmatic diagnostics; game screenshots require an explicit owner request for an image or visual comparison.
- Verify the smallest changed boundary and retain actual results. Do not claim live HMR, multiplayer, package independence, review scores, or timings from unrelated evidence.
- Every feature needs a human-usable entry point, help, observable progress, and actionable errors. Documentation must describe implemented behavior.
- Never commit game binaries, extracted/decompiled game content, saves, profiles, secrets, or raw personal logs. Put local artifacts in ignored `.local/`.
- The pinned review harness and pre-push hook are installed; unattested pushes are verified to fail. Use the configured independent roles and never bypass the gate. CI configuration does not establish hosted execution or branch protection.
