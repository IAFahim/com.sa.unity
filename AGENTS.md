Gotchas
=======

How to drive the Editor from a shell, and the measured Unity findings, are in the current `sa-motion` checkout's `AGENTS.md` (sections "Controlling Unity from a shell" and "Findings so far"). The maintained copy is [sa-motion main](https://github.com/IAFahim/sa-motion/blob/main/AGENTS.md); do not assume an old local packet branch contains the latest instructions.

Before Unity work, load the official `unity-cli` skill (`unity skill show` if unavailable in your client) and the SA companion in `sa-motion/.claude/skills/sa-unity-cli/SKILL.md`. Its `references/rig-coverage.md` maps existing SA and read-only Rukhanka rig/clip/skin paths. Its `references/shape-rendering.md` records the matrix, Vulkan, normal/tangent and package failures fixed in SH5. These are also linked from [the maintained companion](https://github.com/IAFahim/sa-motion/blob/main/.claude/skills/sa-unity-cli/SKILL.md).

Fetch this repository and sa-motion, read the current issue comments, and identify the actual package source/commit before editing. Pass `SA_PROJECT` to the shared `eng/unity.sh` wrapper for compile/test/eval, and `--project-path` for other direct Editor commands. Do not launch a second Editor for a package worktree.

1. A MonoBehaviour must live in a file with its own name. Otherwise it saves as a missing script and silently vanishes from prefabs.
2. Bakers write only their own entity. Use `[TemporaryBakingType]` buffers and a baking system for other entities.
3. Blobs are accessed by `ref`.
4. Enter Play from the main scene, never with the subscene open directly. Wait about 30 s for streaming.
5. CLI form is `unity command <group> <name>`. Run tests with `unity command test run --mode EditMode --filter SA.Tests --filter_type assembly --include_explicit true`.
6. `Debug.Log` from eval rarely shows. Write files under `Temp/sa/` and read them.
7. HLSL does not allow structs in `?:`. Use `if`.
8. An unfocused Editor throttles Play mode to about 1 frame per second. Measure with the explicit `Benchmarks` tests, not Play mode.
9. Build it for a person: see the section of that name in the same file. Do the person's walk before you say done, leave no probe in a scene, and add no hidden switch.
10. CPU/blob affines are 48-byte 3 x 4 matrices; GPU palettes and readbacks are 64-byte `float4x4`. Keep the startup and Edit-mode fallback buffer bindings valid on Vulkan. Shape palettes upload after SkinningSystem and before EntitiesGraphicsSystem; preserve the shaped rig's NoSkin guard.
11. Resolve filesystem paths for Git UPM packages through `PackageInfo.resolvedPath`, not an assumed physical Packages directory. Never patch Library/PackageCache. Update the source repository and pinned dependency.
12. Compact means ECS timeline scheduling without Animator state machines/parameters. Preserve ordinary Humanoid/Generic playback, extra bones, masks, root motion and multiple skins/materials. Shared-family retargeting and runtime proportions have additional validation gates; one working rig does not establish every rig or a player build.
13. On Linux a `-batchmode` Editor picks OpenGLCore unless the project lists Vulkan for Linux (Player Settings, Linux, Graphics APIs, Auto off); the windowed Editor runs Vulkan. On OpenGLCore, compute results read back in tests came back stale in measured runs (SA.Tests failed 3 of 5 runs on `SkinningShaderTests`, `ReorderedSharedClips...`, `SkinningSystemRegistersShared...`; 0 of 5 on Vulkan), so the GPU readback tests skip there with the reason. Give a test project Vulkan for Linux before trusting GPU results from a batchmode Editor. The `Unity Data Store service ... is no longer running` error fails whichever test is running when the Editor's Data Store child process dies; stop Editors by pid, never with `pkill -f` on a path, which also matches that child.
