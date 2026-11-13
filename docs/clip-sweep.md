# Clip sweep: SA against Unity's Humanoid playback

Date: 2026-11-10. Unity 7000.0.0a12, batchmode Editor on a separate copy project (`~/sweep-project`), com.sa.unity branch `feat/closeout`, com.sa.motion.unity bridge from sa-motion `e56b12f` (unchanged), Rukhanka Animation System 2.10.0 (copied from TestingAssets into the sweep project). Every number below comes from a run on that project. The per-clip CSVs stay in `~/sweep-project/SweepResults/` (`sweep_r0.csv` default sampling, `sweep_r1/r2/r4.csv` fixed 1x/2x/4x, `conformance.csv` Play mode and Rukhanka) because they name corpus assets; `docs/clip-sweep-summary.csv` holds the numbers only.

## Method

For every `AnimationClip` in the corpora (FBX takes without `__preview__`, and `.anim` files) and every reference character:

- **Ground truth**: `clip.SampleAnimation` on a hidden copy of the character with its Humanoid avatar, every bone reset to rest before each sample.
- **SA**: `FamilyClips.TrySample` (the default sampling, below) then `TryEncode` (packed, the shipping setting) then `SharedClipReader.Load`, `SharedClip.Sample`, the decoded planar root, and `FamilyClips.TryApply` on the same character.
- **Compared at every encoded sample** (the clip's frames are always among them): world rotation of every bone the plan maps to a Humanoid role; world position of hips, head, hands and feet; the planar root path and heading; duration; loop flag; NaN/Inf; `.saclip` size.
- **Gate (unchanged)**: rotation at most 1.0 deg, position at most 1.0 cm, root path at most 1 cm, heading at most 1 deg, exact duration and loop flag.
- **In-between**: the same rotation comparison halfway between every pair of encoded samples, the worst place for SA's linear interpolation. Intervals inside a **Unity jump** are reported separately: a clip frame in which some mapped bone turns more than 90 deg along Unity's own path (measured through the samples and midpoints in that frame). These are cuts, twist flips and spins Unity makes inside one frame; no sample rate follows them.

### Default sampling (new): adaptive rate

`FamilyClips.TrySample(plan, clip, ...)`, `TrySample(plan, clip, hz, ...)` (with `hz` as the base), `TryConvert` and the shared-family baker now sample each clip at its own frame rate and double the rate (2x, 4x, at most 8x) until Unity's pose halfway between every pair of samples is within 1.5 deg (`FamilyClips.InBetweenDegrees`, any canonical bone in model space, decoded from the packed `.saclip`, Unity jumps left out). Sample counts are `multiplier * (clip frames - 1) + 1`, so every clip frame stays a sample and the per-frame gate is unchanged. `TrySampleFixed` keeps the old fixed rate; `TryEncode(..., hz, ...)` writes the clip's exact length and loop flag for samples that came from `TrySample`, so existing tool code (sa-inbetween's `SaClipConvert` passes 30 Hz to both) gets the adaptive rate without a change. No codec change: the `.saclip` still holds evenly spaced samples.

Tool: `FamilyClipSweep.Run(..., rateScale)` (0 = default adaptive, 1/2/4 = fixed samples per clip frame), also `Tools/SA/Animation Library/Check Clips Against Unity (Selected Character and Folders)`. Tests: `FamilyClipsTests` (synthetic regressions incl. `AdaptiveSamplingRaisesTheRateOnlyWhereInBetweensMissUnity`; `CorpusSweepPassesEveryHumanoidClip` is `[Explicit]`).

## Corpus

| Source | FBX + .anim files | Assets with clips | Notes |
|---|---|---|---|
| TestingAssets/Assets (Synty, Rukhanka samples, InbetweenDemo) | 833 | 789 | |
| ee2-corpus (DualBlade, Neophyte) | 392 | 383 | |
| sa-demo-sh5/Assets | 721 | 0 | byte-identical (FBX and .meta) to TestingAssets Synty; skipped as duplicates |
| vex-corpus/com.vex.ee.baseplayer | 185 | 156 | |
| vex-corpus/piloto-studio-2-unity-package | 26 | 0 | models only, no clips |
| vex-corpus/com.vex.animation, com.vex.motion, com.vex.unity.ee2.assets | 0 | 0 | no FBX or .anim |
| synthetic (sweep project only) | 8 | 8 | mirror copies of 4 FBX, 35 s / 67 s / 31 s at 60 fps / 60 fps `.anim` built from Humanoid curves |

Clips: **1382 Humanoid** (1373 corpus + 9 synthetic), **190 not Humanoid**.

Coverage among the Humanoid clips: 607 root motion, 775 in place, 548 looping, 544/619/774 baked XZ/rotation/Y, 117 sub-range takes, 19 additive, 6 mirrored, 40 at 60 fps, 25 shorter than 0.2 s (shortest 0.033 s), 3 longer than 30 s (longest 66.9 s).

Reference characters (all Humanoid avatars): PolygonSyntyCharacter (no ring/little fingers or eyes), Neophyte_RIG (Mixamo, no jaw/eyes), EllenHumanoid (full set incl. jaw/eyes), TerraformerHumanoid (distinct proportions, avatar maps no neck, A-pose: upper arm 48 deg from the T-pose, twist bones), SidekickSyntyCharacter, Grruzam_Modeling_Fighter (UE mannequin, translation DoF on), Armature_Humanoid (A-pose, 46 deg); plus PolygonSyntyCharacter scaled 1.7 and Neophyte_RIG scaled 0.6.

## Result (default adaptive sampling)

**12438 of 12438 Humanoid clip x character rows pass the per-frame gate (100%) on all 9 character variants**, and the in-between meets the proposed gate on the whole corpus.

| | worst |
|---|---|
| world rotation, any mapped bone, any sample | 0.0126 deg (LeftThumbDistal) |
| hips/head/hand/foot position | 0.017 cm |
| planar root path | 0.0007 cm |
| heading | 0.0003 deg |
| duration, loop flag, NaN/Inf | exact, 0 mismatches |
| in-between, outside Unity jumps | p50 0.87 deg, p90 1.35 deg, p99 1.48 deg, worst 1.69 deg (Dash_Attack_B_Spiral_Root, Neophyte, 8x) |
| `.saclip` size | 4.0 KB to 2.95 MB, mean 80.6 KB (Synty 68.5 KB); rate 1x/2x/4x/8x on 3303/4889/3326/920 rows, mean 2.71x |

Per-character and per-bone worst numbers are in `docs/clip-sweep-summary.csv` and `sweep_r0.summary.txt`. A pass takes 855 s (adaptive) against 215 s (1x).

## In-between accuracy by rate (full corpus, 12438 rows each, all pass the per-frame gate)

| Sampling | rows at most 1 deg | at most 2 deg | median | p90 | p99 | worst | mean size |
|---|---|---|---|---|---|---|---|
| clip rate (1x) | 16.0% | 44.5% | 2.14 deg | 7.05 deg | 16.5 deg | 37.2 deg | 31.6 KB |
| 2x | 46.1% | 76.3% | 1.08 deg | 3.22 deg | 7.03 deg | 15.6 deg | 58.3 KB |
| 4x | 83.3% | 98.0% | 0.42 deg | 1.33 deg | 2.59 deg | 6.87 deg | 111.9 KB |
| **adaptive (default)** | 60.1% | **100%** | 0.87 deg | **1.35 deg** | **1.48 deg** | **1.69 deg** | **80.6 KB** |

The proposed gate (p90 at most 2 deg, p99 at most 5 deg outside Unity's own jumps) is met by the default, at 72% of the 4x size and with a per-clip worst of 1.69 deg that no fixed rate reaches. 21% of rows contain at least one Unity jump frame.

### Root cause of the 90 deg rows at 4x (previous report: "not analysed")

All 16 rows were `Dual_Blade@KnockDown_Front` and `KnockDown_Front_Root` (one source animation), worst bone Head at 4x sample 7.5 (t = 0.0625 s), every character. The source data drives the head far outside the human range: the clip's `Head Nod Down-Up` muscle is -9.2, -7.8, -4.4 at frames 0..2 and `Head Tilt Left-Right` 8.8, 8.5, 4.1 (muscles are normalised to plus or minus 1 of the avatar limit, so this is a head turned through more than 300 deg). Between frames 1 and 2 Unity's swing conversion of those values passes through the half-turn and the head whips through 176 deg inside 1/240 s (measured by `SampleAnimation` every 1/960 s: 48 deg at 0.0625 s, 177 deg at 0.0646 s, 88 deg at 0.0667 s from the pose at 0.0583 s). The old jump rule only compared neighbouring samples; at 4x the two samples around the spin differed by 87.7 deg, just under 90, so the spin counted as an SA in-between error. It is a Unity jump: the head turns more than 90 deg within one clip frame. The new rule measures the path through the samples, so it is excluded at every rate (and SA still matches Unity at every frame, 0.01 deg). The same rule caught a second class the endpoint rule missed: Unity's upper-arm twist flip in `Unequip_DualBlade_To_Fighter` (179.7 deg between two 1/120 s samples while the fingers move 2.5 deg), which read as a 180 deg in-between error.

## Real runtime path: SubScene bake and Play mode

`SA.Conformance.PlayModeConformance` builds `Assets/zz_conformance/Conformance.unity` with a SubScene holding `RigAuthoring` (Cpu) + `SharedFamilyAuthoring` for each character, enters Play mode in the batchmode Editor (main scene, SubScene closed, baked by the import worker), and in one eval disables `UpdateWorldTimeSystem`, then for every clip and frame time seeks `SharedClock`, runs `World.Update()` twice and reads each bone entity's `LocalToWorld` relative to the rig entity. Truth is `SampleAnimation` on a hidden copy. The baker now samples through `FamilyClips.TrySample` (same calibration, frame times and adaptive rate as the tools) and puts the planar root back into the hips, because the runtime applies no root motion.

59 clips (the 58 of the previous runtime proof, chosen across corpora and flags: root motion, in place, loop, 60 fps, mirror, sub-range, additive, 0.033 s, 31 to 67 s; plus `KnockDown_Front`), every clip frame up to 120 per clip, on PolygonSyntyCharacter, Neophyte_RIG, EllenHumanoid and two Synty copies with avatar twist 0 and 1: **295 of 295 rows pass, 12965 frames, worst 0.043 deg and 0.033 cm** (hips, hands, feet).

## Rukhanka

The Rukhanka driver is not package source (the package carries no Rukhanka code, `PackageSourceHasNoCommentsReflectionLegacyInputOrRukhanka`); it is kept as `docs/conformance-rukhanka.cs.txt` with its assembly definition `docs/conformance-rukhanka.asmdef.txt` (versionDefine on `com.rukhanka.animation`), and ran from `Assets/zz_conformance_tools/Editor` of the sweep project. It registers with `PlayModeConformance.ExtraRigs`/`ExtraDrivers` and puts a controller-free Rukhanka rig next to each SA rig in the same SubScene: `RigDefinitionAuthoring` (User Defined, the character's avatar, CPU engine, no root motion, all bone entities kept) and `AnimationAssetSetAuthoring` with the 59 clips. In Play mode each frame time is submitted with `ScriptedAnimator.ResetAnimationState` + `PlayAnimation(clip blob from BlobDatabaseSingleton, normalised time)` before the same `World.Update()` calls. Rukhanka applies no root motion, so its bones are compared with Unity's pose relative to the root transform `SampleAnimation` moves, and SA's pose is brought into the same frame for SA against Rukhanka.

| Character (avatar twist) | Rukhanka vs Unity | SA vs Rukhanka | worst outside the 8 twist bones | positions |
|---|---|---|---|---|
| PolygonSyntyCharacter (0.5, default) | 1/59, 89.8 deg | 1/59, 89.8 deg | 0.040 deg | 0.054 cm |
| Neophyte_RIG (0.5) | 1/59, 89.8 deg | 1/59, 89.8 deg | 0.039 deg | 0.073 cm |
| EllenHumanoid (0.5) | 1/59, 89.8 deg | 1/59, 89.8 deg | 0.067 deg | 0.065 cm |
| PolygonSyntyTwist0 (0) | 1/59, 179.6 deg | 1/59, 179.6 deg | 0.040 deg | 0.055 cm |
| **PolygonSyntyTwist1 (1)** | **59/59, 0.038 deg** | **59/59, 0.040 deg** | 0.040 deg | 0.053 cm |

Rukhanka played all 59 clips (none refused; additive, mirrored, sub-range, 60 fps and 67 s clips included). Every failure is on the upper arm, forearm, upper leg or lower leg, about the bone's own axis, with joint positions unchanged: Rukhanka writes each limb's twist muscle to that bone alone and ignores the avatar's `upperArmTwist`/`lowerArmTwist`/`upperLegTwist`/`lowerLegTwist` (Unity's default 0.5 moves half the roll to the neighbouring bone). With all four set to 1 on the avatar, Rukhanka matches Unity and SA on every clip. SA matches Unity on all five avatars. No clip played by Rukhanka is refused by SA (SA plays all 1382 Humanoid clips).

## Tests

`SA.Tests` with explicit tests on the sweep project: 110 of 111 pass. The two Synty-path tests (`SyntyJumpRoundTrip`, `NeophyteMixamoMapAndRoundTrip`) now find their assets by file name anywhere in the project and run (skipped only when the asset is absent, with the reason). The one failure is the `[Explicit]` GPU benchmark `FullSaFramePerCharacterCount(50000)`, which exceeds its 180 s timeout in the batchmode Editor; it exercises `PlaybackSystem` and `SkinningSystem`, and no `Runtime/` file changed in this work.

## Failures found and fixed

| # | Root cause (class) | Symptom before the fix | Fix |
|---|---|---|---|
| 1 | calibration math: model-space rows in `SharedFamilyBake.TargetToCanonical` used `inverse(target parent) * local` instead of `inverse(canonical parent) * target model` | Rukhanka pilot: 41 of 44 rows failed. Any clip whose root object moves (hips under an unmapped parent): 22 to 180 deg; bones under an unmapped intermediate (Terraformer has no neck in its avatar): 110 deg | corrected the formula; the shared-family baker uses the same function |
| 2 | encoder: verify sampled the last frame of a looping clip at `t = duration`, which wraps to frame 0 | every looping clip whose last pose differs from its first refused to encode | verify that frame just before the end |
| 3 | calibration: rest positions instead of the avatar skeleton | Grruzam Fighter: 1217 of 1217 rows failed, 6 to 9 cm (Unity writes Humanoid bone positions from the avatar, not the FBX rest) | Humanoid bones take positions from `humanDescription.skeleton` (`FamilyClips.ApplyAvatarCalibration`) |
| 4 | avatar translation DoF / arm-leg stretch: the canonical pose carried no per-bone translation | Grruzam Fighter, 1 clip: 5.2 cm (spine moves 4.5 cm, hands stretch 2 cm) | `CanonicalTranslation`: canonical bones carry their translation offset, `FamilyClips.TryApply` and `SharedFamilySystem` apply it back |
| 5 | sampling: frames stopped at `floor(length * hz)` and `TryEncode` always wrote `loop = false` | duration short by up to one frame on non-integer lengths; loop flag lost on 548 clips | `TrySample` spaces frames evenly over the exact length; `TryEncode(..., duration, loop, ...)`, and `TryEncode(..., hz, ...)` now keeps the length and loop flag of `TrySample` samples |
| 6 | baker consistency: the shared-family baker mapped by name and calibrated from rest; it also rejected FBX scale noise | a `.saclip` from `FamilyClips` and a baked character disagreed on A-pose rigs; Neophyte (scale 1.000019) and Ellen (1.000021) could not bake at all | baker maps Humanoid avatars through `HumanBodyBones`, calibrates like `FamilyClips`, and accepts unit scale within 1e-4 like `FamilyClips` |
| 7 | baker sampling: its own sampler used `ceil(length * min(rate, 60)) + 1` frames | 120 fps clips lost every other frame; non-integer lengths shifted every sample off Unity's frame times | the baker samples through `FamilyClips.TrySample` |
| 8 | in-between: fixed clip-rate sampling | p90 7.0 deg, p99 16.5 deg, worst 37 deg halfway between frames | adaptive rate (above) |

## Not Humanoid (190 clips, listed with proof in the CSV)

| Count | Proof |
|---|---|
| 148 | FBX `animationType=Generic`, `avatar.isHuman=False` (Rukhanka Ellen/Terraformer/Chomper/SkinnedFigure/Armature generic variants, baseplayer props and creatures) |
| 36 | `.anim` with `humanMotion=False` and no muscle curves: transform-curve rigs (Kyle, Armature, Ellen generic, IK targets), props, camera, blend shapes |
| 6 | takes inside Humanoid FBX with 0 curves of any kind (Blender per-object empty actions) |

## Reproduce

1. `~/sweep-project`: ProjectSettings and manifest from TestingAssets, com.sa.unity and com.sa.motion.unity as `file:` packages from the worktrees, SA.Inbetween.Clips Runtime and `com.rukhanka.animation` embedded; corpora copied with their `.meta` (deduplicated by FBX+meta hash) into `Assets/zz_corpus_*`.
2. Batchmode Editor: `SA_PROJECT=~/sweep-project eng/unity_start.sh` (start it with `flock -o` so the Editor does not inherit the wrapper lock).
3. `FamilyClipSweep.Run(characters, scales, folders, "Temp/sa/sweep/sweep_r0.csv", 0, int.MaxValue, false, 0f)` through an eval; `1f`/`2f`/`4f` for fixed rates.
4. Play mode and Rukhanka: `PlayModeConformance.BuildScene("Assets/zz_conformance", characters, clips)` in Edit mode (it leaves `Conformance.unity` open; after an Editor restart open it again), `unity command editor.playmode start`, then `PlayModeConformance.Measure("Assets/zz_conformance", "Temp/sa/conformance.csv", 120)` (returns "not ready" until the SubScene has streamed in). The twist variants are copies of `PolygonSyntyCharacter.fbx` with the four avatar twist values set to 0 or 1.
