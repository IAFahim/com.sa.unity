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

A second run of the same tests split the CPU column into its two systems (run-to-run spread is visible in the totals):

| Characters | PlaybackSystem | SkinningSystem | CPU total avg / p95 | CPU + GPU avg / p95 |
|-----------:|---------------:|---------------:|--------------------:|--------------------:|
| 10,000 | 0.31 ms | 1.58 ms | 1.89 / 2.04 ms | 3.34 / 3.54 ms |
| 50,000 | 1.42 ms | 10.70 ms | 12.12 / 19.49 ms | 23.79 / 31.13 ms |

So at 50,000 characters `SkinningSystem.OnUpdate` (register, layout, fill, upload and dispatch) is the CPU cost to attack, at about 0.2 microseconds per character per frame against 0.03 for playback, and the GPU compute is about as large again.

A third run timed the phases inside `SkinningSystem.OnUpdate` with a stopwatch added for the measurement and removed again (ms per frame, mean of 200 frames):

| Characters | Register | Layout (count jobs) | Fill (jobs and frame arrays) | Upload and dispatch | Dispose |
|-----------:|---------:|--------------------:|-----------------------------:|--------------------:|--------:|
| 10,000 | 0.02 | 0.43 | 0.79 | 0.25 | 0.01 |
| 50,000 | 0.04 | 1.91 | 3.63 | 4.52 | 0.01 |

Upload and dispatch grows 18 times for 5 times the characters, so the per-frame upload of every instance, motion and skin job is the first thing to cut. Layout and fill both walk every clip state of every rig, so they do that work twice per frame.

Rendering cost is still not measured.

Shared-family rigs on the CPU (2026-11-16)
==========================================

`SharedFamilyJobTests.CostOfTheSharedFamilySystemPerRig`, 1000 rigs of 56 bones on the retarget-table path, one clip each, median of 120 frames after 30 warm-up frames, same machine, 11 worker threads.

| System | ms per frame | microseconds per rig |
|---|---:|---:|
| 0.3.7, main thread | 22.50 | 22.5 |
| Burst job (`packet/C17-shared-family-job`) | 2.82 | 2.8 |

This times `SharedFamilySystem` alone.

`SharedFamilyPaletteTests.CostOfTheSharedFamilyPaletteSystemPerSkin`, 1000 skins of 56 bones, one per rig, median of 60 frames after 20 warm-up frames, timing `SharedFamilyPaletteSystem` alone:

| System | ms per frame | microseconds per skin |
|---|---:|---:|
| 0.3.7, main thread, one `SetData` per skin | 15.63 | 15.6 |
| Burst job, one `SetData` per run of neighbouring skins (`packet/C18-shared-palette-job`) | 2.68 | 2.7 |

Together the two systems cost 38.1 ms per frame for 1000 shared-family characters before and 5.5 ms after.
