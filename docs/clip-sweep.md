# Clip sweep: SA against Unity's Humanoid playback

Date: 2026-11-10. Unity 7000.0.0a12, batchmode Editor on a separate copy project (`~/sweep-project`), com.sa.unity branch `feat/clip-sweep`, com.sa.motion.unity bridge from sa-motion `e56b12f` (unchanged). Every number below comes from a run on that project. The per-clip CSVs stay in `~/sweep-project/Temp/sa/sweep/` (copied to `~/sweep-project/SweepResults/`, since the Editor clears Temp on start) because it names corpus assets; `docs/clip-sweep-summary.csv` holds the numbers only.

## Method

For every `AnimationClip` in the corpora (FBX takes without `__preview__`, and `.anim` files) and every reference character:

- **Ground truth**: `clip.SampleAnimation` on a hidden copy of the character with its Humanoid avatar, at the clip's frame rate (30 Hz when it has none), every bone reset to rest before each sample. This is the pose Unity and Rukhanka (which plays through Unity's Humanoid retarget) produce at that time.
- **SA**: `FamilyClips.TrySample` then `TryEncode` (packed, the shipping setting) then `SharedClipReader.Load`, `SharedClip.Sample`, the decoded planar root, and `FamilyClips.TryApply` on the same character.
- **Compared per frame**: world rotation of every bone the plan maps to a Humanoid role; world position of hips, head, hands and feet; the planar root path and heading against the same root derived from the ground truth; duration; loop flag; NaN/Inf; `.saclip` size.
- **Gate (unchanged)**: rotation at most 1.0 deg, position at most 1.0 cm, root path at most 1 cm, heading at most 1 deg, exact duration and loop flag.
- **Not a gate, reported**: the same comparison halfway between frames ("in-between", see below).

Tool: `FamilyClipSweep.Run` (Editor API, also `Tools/SA/Animation Library/Check Clips Against Unity (Selected Character and Folders)`), which writes the CSV and a `.summary.txt` beside it. Tests: `FamilyClipsTests` (synthetic regressions run in every suite; `CorpusSweepPassesEveryHumanoidClip` is `[Explicit]` and runs on any project that has `Assets/zz_corpus_*` folders).

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

## Result

**12438 of 12438 Humanoid clip x character rows pass (100%) on all 9 character variants.**

| | worst |
|---|---|
| world rotation, any mapped bone, any frame | 0.0123 deg (LeftMiddleDistal) |
| hips/head/hand/foot position | 0.016 cm |
| planar root path | 0.0005 cm |
| heading | 0.0001 deg |
| duration, loop flag, NaN/Inf | exact, 0 mismatches |
| `.saclip` size | 4.0 KB to 1.17 MB, mean 27.9 KB (Synty) |

Per-character and per-bone worst numbers are in `docs/clip-sweep-summary.csv` and the `.summary.txt`; every bone's worst is below 0.0123 deg (fingers worst, hips 0.003 deg).

### Runtime (ECS) proof

58 clips chosen across corpora and flags (root motion, in place, loop, 60 fps, mirror, sub-range, additive, very short, 35-67 s) on PolygonSyntyCharacter. Each `.saclip` is attached with the real `SA.Inbetween.Clips.SaClipRuntime.TryAttach` to an entity carrying `Rig`, `SharedRetarget`, `SharedClock` and bone entities, and `SharedFamilySystem` evaluates it in a hand-stepped `World` (seek, update, read `LocalTransform` of every bone entity, compose; root motion applied as the entity transform, scaled by the character's height ratio). 10 frame times and 10 off-frame times per clip.

- Entity vs Unity ground truth at frame times: **58/58 pass**, worst 0.0101 deg, 0.009 cm.
- Entity vs the editor decoder path (`FamilyClips.TryApply`) at any time: worst 0.000055 deg.

Not measured: a SubScene bake of `SharedFamilyAuthoring` followed by Play mode. Instead, the baker's plan was compared bone by bone with `FamilyClips.TryPlan` on all 7 characters: identical Humanoid mapping and calibration (worst 3e-15 deg) for every mapped bone.

## Failures found and fixed

| # | Root cause (class) | Symptom before the fix | Fix |
|---|---|---|---|
| 1 | calibration math: model-space rows in `SharedFamilyBake.TargetToCanonical` used `inverse(target parent) * local` instead of `inverse(canonical parent) * target model` | Rukhanka pilot: 41 of 44 rows failed. Any clip whose root object moves (hips under an unmapped parent): 22 to 180 deg; bones under an unmapped intermediate (Terraformer has no neck in its avatar): 110 deg | corrected the formula; the shared-family baker uses the same function |
| 2 | encoder: verify sampled the last frame of a looping clip at `t = duration`, which wraps to frame 0 | every looping clip whose last pose differs from its first refused to encode | verify that frame just before the end |
| 3 | calibration: rest positions instead of the avatar skeleton | Grruzam Fighter: 1217 of 1217 rows failed, 6 to 9 cm (Unity writes Humanoid bone positions from the avatar, not the FBX rest) | Humanoid bones take positions from `humanDescription.skeleton` (`FamilyClips.ApplyAvatarCalibration`) |
| 4 | avatar translation DoF / arm-leg stretch: the canonical pose carried no per-bone translation | Grruzam Fighter, 1 clip: 5.2 cm (spine moves 4.5 cm, hands stretch 2 cm) | `CanonicalTranslation`: canonical bones carry their translation offset, `FamilyClips.TryApply` and `SharedFamilySystem` apply it back. Offsets are relative to the canonical reference, so existing clips (offset 0) play unchanged |
| 5 | sampling: frames stopped at `floor(length * hz)` and `TryEncode` always wrote `loop = false` | duration short by up to one frame on non-integer lengths; loop flag lost on 548 clips | `TrySample` spaces frames evenly over the exact length; `TryEncode(..., duration, loop, ...)` and `TryConvert` |
| 6 | baker consistency: the shared-family baker mapped by name and calibrated from rest; it also rejected FBX scale noise | a `.saclip` from `FamilyClips` and a baked character disagreed on A-pose rigs; Neophyte (scale 1.000019) and Ellen (1.000021) could not bake at all | baker maps Humanoid avatars through `HumanBodyBones`, calibrates like `FamilyClips`, and accepts unit scale within 1e-4 like `FamilyClips` |

## Not Humanoid (190 clips, listed with proof in the CSV)

| Count | Proof |
|---|---|
| 148 | FBX `animationType=Generic`, `avatar.isHuman=False` (Rukhanka Ellen/Terraformer/Chomper/SkinnedFigure/Armature generic variants, baseplayer props and creatures) |
| 36 | `.anim` with `humanMotion=False` and no muscle curves: transform-curve rigs (Kyle, Armature, Ellen generic, IK targets), props, camera, blend shapes |
| 6 | takes inside Humanoid FBX with 0 curves of any kind (Blender per-object empty actions) |

## In-between frames: the remaining gap, for the owner to decide

The gate compares at sample times. Between them SA interpolates linearly; Unity evaluates its muscle curves. Halfway between frames, excluding intervals where Unity's own pose jumps more than 90 deg in one frame (twist-split flips and cuts, present in 21% of rows at the clip rate):

| Sample rate | rows at most 1 deg | at most 2 deg | median | p90 | p99 | mean size |
|---|---|---|---|---|---|---|
| clip rate (1x) | 15.7% | 44% | 2.15 deg | 7.2 deg | 17 deg | 31.6 KB |
| 2x | 44% | 70% | 1.14 deg | 4.5 deg | 10.3 deg | 58.3 KB |
| 4x | 77% | 92% | 0.44 deg | 1.8 deg | 4.2 deg | 111.8 KB |

All three rates were measured on the full corpus (12438 rows); every rate still passes the per-frame gate at 100%.

Proposed gate (owner decides): keep the per-frame gate (met at 100%), and add an in-between gate over the corpus of **p90 at most 2 deg and p99 at most 5 deg outside Unity's own jumps**, met by encoding at 4x the clip rate (pass `hz * 4` to `TrySample`, about 3.5x the bytes). A per-clip worst-case in-between gate is not reached by sampling alone: at 4x the worst row is still 90 deg (cause not analysed). The alternative that keeps sizes is to encode Unity's keyframes and tangents instead of samples, which needs a codec change in sa-motion.

## Reproduce

1. `~/sweep-project`: ProjectSettings and manifest from TestingAssets, com.sa.unity and com.sa.motion.unity as `file:` packages from the worktrees, SA.Inbetween.Clips Runtime embedded; corpora copied with their `.meta` (deduplicated by FBX+meta hash) into `Assets/zz_corpus_*`.
2. Batchmode Editor: `SA_PROJECT=~/sweep-project eng/unity_start.sh`.
3. `FamilyClipSweep.Run(characters, scales, folders, "Temp/sa/sweep/sweep.csv", 0, int.MaxValue, false)` through `eng/unity.sh eval`; about 2.5 minutes for 12438 rows; `rateScale` overload for 2x/4x.
4. Runtime: `FamilyClipRuntimeCheck.TryMeasure(plan, clip, hz, 10, SaClipRuntime.TryAttach, ...)`.
