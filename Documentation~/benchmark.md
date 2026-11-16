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

0.3.7 on a discrete GPU (2026-11-16)
====================================

The same explicit tests, run on the 0.3.7 skinning path (unchanged by `packet/C17-shared-family-job`), 200 frames after 60 warm-up frames.

Machine: AMD Ryzen 5 8500G, NVIDIA GeForce RTX 3060, Direct3D 12, Windows 11, Unity 7000.0.0a16, batch mode.

| Characters | SA CPU avg / p95 | SA CPU + GPU compute avg / p95 |
|-----------:|-----------------:|-------------------------------:|
| 10,000 | 1.79 / 1.95 ms | 3.27 / 3.51 ms |
| 50,000 | 15.62 / 22.41 ms | 27.27 / 33.91 ms |

At 10,000 characters the GPU part is about 1.5 ms, so the 2 ms CPU and 2 ms GPU target is met at that count on this machine. At 50,000 it is not met: about 11.6 ms on the GPU and 15.6 ms on the CPU.

On this GPU the CPU side is the larger cost at 50,000, and it grows faster than the character count (8.7 times the cost for 5 times the characters). That points at the per-frame instance fill and upload before the compute kernels, not at the one-thread-per-character kernel. The CPU here has 6 cores against the 24 of the machine above, so the CPU columns of the two tables are not comparable.

Rendering cost is still not measured.

Shared-family rigs on the CPU (2026-11-16)
==========================================

`SharedFamilyJobTests.CostOfTheSharedFamilySystemPerRig`, 1000 rigs of 56 bones on the retarget-table path, one clip each, median of 120 frames after 30 warm-up frames, same machine, 11 worker threads.

| System | ms per frame | microseconds per rig |
|---|---:|---:|
| 0.3.7, main thread | 22.50 | 22.5 |
| Burst job (`packet/C17-shared-family-job`) | 2.82 | 2.8 |

This times `SharedFamilySystem` alone. `SharedFamilyPaletteSystem` still builds each skin palette on the main thread and is not included.
