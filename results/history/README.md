# Acceptance history

Earlier acceptance runs, kept as they were recorded. Each one is evidence of what was true
**on its date**. None of them is a current result; the current state is in
[verification.md](../verification.md).

| Date | Tests | Commit | Record |
|---|---|---|---|
| 2026-10-04 | 189 passed, 0 failed (fresh clone); CI green, 189/189 | `686d6cc` (now `fa79883`, same tree) | [2026-10-04-race-fixes.md](2026-10-04-race-fixes.md): the inbox concurrent-delivery race fixes and their deterministic tests |
| 2026-10-04 | 182 passed, 0 failed (fresh clone); CI green, 182/182 | `c94ac50` | [2026-10-04-initial.md](2026-10-04-initial.md): the first run pinned to a commit; the README setup path from a fresh clone, and the launch-profile defects it exposed |
| 2026-09-13 | 182 passed, 0 failed | none (development tree) | [2026-09-13.md](2026-09-13.md): eight boundary gaps closed (G1–G8), each with the test that fails without its fix; the five scenarios on isolated resources |
| 2026-09-11 | 98 passed, 0 failed | none (development tree) | [2026-09-11.md](2026-09-11.md): the first full run, and the defects the first tests found in the application |

## A note on commit ids

The commits `686d6cc`, `b37263e` and `3c966ce` were re-recorded as `fa79883`, `c436c05` and
`aa0062e` when trailer lines were removed from their messages. Nothing else changed: the
trees are byte-identical (`94676b3`, `c604a49`, `6a606af`), as are the author, the dates and
the message bodies. The CI runs recorded against the old ids
([37207068481](https://github.com/hidayetcolkusu/dotnet-reliable-integration-lab/actions/runs/37207068481)
on `b37263e`) therefore tested exactly the content now at `c436c05`. Every earlier commit
(`e5a2025` to `c94ac50`) keeps its original id.
