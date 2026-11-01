Gotchas
=======

How to drive the Editor from a shell, and the Unity Physics findings, are in `/home/i/GitHub/sa-motion/AGENTS.md` (sections "Controlling Unity from a shell" and "Findings so far").

1. A MonoBehaviour must live in a file with its own name. Otherwise it saves as a missing script and silently vanishes from prefabs.
2. Bakers write only their own entity. Use `[TemporaryBakingType]` buffers and a baking system for other entities.
3. Blobs are accessed by `ref`.
4. Enter Play from the main scene, never with the subscene open directly. Wait about 30 s for streaming.
5. CLI form is `unity command <group> <name>`. Run tests with `unity command test run --mode EditMode --filter SA.Tests --filter_type assembly --include_explicit true`.
6. `Debug.Log` from eval rarely shows. Write files under `Temp/sa/` and read them.
7. HLSL does not allow structs in `?:`. Use `if`.
8. An unfocused Editor throttles Play mode to about 1 frame per second. Measure with the explicit `Benchmarks` tests, not Play mode.
9. Build it for a person: see the section of that name in the same file. Do the person's walk before you say done, leave no probe in a scene, and add no hidden switch.
