# AZR-001 source recovery status

## Status

`SOURCE_MATERIAL_RESOLVED`

The recovered source package supplied for this documentation task contained every required artifact. Each source file was verified against `SOURCE-MANIFEST.txt` before repository materialization.

| Source artifact | SHA-256 | Result |
| --- | --- | --- |
| `azr-001-v0.8.md` | `d3d5df8756d28df2e370017c36152fdbfb084542b731a0b97d39e0a5ea37b012` | MATCH |
| `e-001-v0.3.md` | `c61a83cb0a1014151471e4aba05cb8793d35a274f0a0599682f7d064b662efee` | MATCH |
| `azr-001-dec-001.md` | `2d752c367d484b2820f83ce844ed34edbbe1b3dedd633a8ae8aedd3b79b03e5a` | MATCH |
| `e-002-v0.13.1.md` | `5bfc9cc673e03a48678b44e5dc90060c1b055faca2d3b33c52dcf06ea502c795` | MATCH |

## Materialization rule

The four verified artifacts are authoritative recovered sources for this documentation task. They were materialized without reconstructing acceptance criteria or replacing their content from Git history, issues, pull-request comments, attachments, or implementation evidence.

Their governance statements are historical. Later decisions and operational facts are recorded only in [`current-state.md`](current-state.md). Thus a historical `pending`, `owner_approved = false`, or `implementation_authorized = false` remains preserved even when the separate current-state record shows that the corresponding gate was later resolved.

The legacy untracked `docs/operations/azr-001-dev-synthetic-v0.8.md` was not used as source authority, was not promoted into this contract family, and remains outside the documentation commit.
