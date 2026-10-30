The measurements below are historical v0.2 results. The 0.3 shared-asset refactor has not been performance-benchmarked; no new 50,000-character timing is claimed.

Benchmark
=========

Measured with the explicit tests in `Tests/Editor/Benchmarks.cs`: a Kyle-sized rig (49 bones, 5 clips, four synced override states plus one masked additive state) per character, SA's real systems, 200 frames after 60 warm-up frames, the GPU synchronized by reading back one skin matrix.

Machine: Intel Core i9-14900K, Mesa Intel Graphics (RPL-S), OpenGLCore, Linux.

| Characters | SA CPU avg / p95 | SA CPU + GPU compute avg / p95 |
|-----------:|-----------------:|-------------------------------:|
| 10,000 | 1.89 / 2.58 ms | 16.8 / 19.1 ms |
| 50,000 | 9.02 / 16.7 ms | 84.3 / 105.0 ms |

Target: 50,000 characters at 2 ms CPU and 2 ms GPU. **Not met.**

Cause of the GPU cost: `EvaluatePose` runs one thread per character and walks 49 bones sequentially, so the iGPU is poorly occupied. Next step: one thread per bone, with the hierarchy product done in a second pass. The CPU cost is dominated by the layout and fill passes over every rig and skin each frame; skipping unchanged rigs would reduce it.

Rendering cost is not measured. Play mode in the Editor ran at about 1 frame per second regardless of SA's cost because the unfocused Editor loop was throttled (profiler: all time inside `EditorLoop`). The Crowd sample's `Benchmark` component records frame statistics for a focused Editor or a player build.
