# Clip sweep: SA against Unity's Humanoid playback

Date: 2026-11-13. Unity 7000.0.0a12, batchmode Editor on a separate copy project (`~/sweep-project`, Linux graphics API set to Vulkan, see Tests), com.sa.unity branch `feat/closeout2` (from main 0.3.4), com.sa.motion.unity bridge from sa-motion `e56b12f` (unchanged), Rukhanka Animation System 2.10.0 (copied from TestingAssets into the sweep project). Every number below comes from a run on that project and that code. The per-clip CSVs stay in `~/sweep-project/SweepResults/` (`sweep_final.csv` default sampling, `conformance_full_b00..b06.csv` Play mode with Rukhanka; `sweep_r1/r2/r4.csv` are the 0.3.4 fixed-rate runs) because they name corpus assets; `docs/clip-sweep-summary.csv` holds the numbers only.

## Method

For every `AnimationClip` in the corpora (FBX takes without `__preview__`, and `.anim` files) and every reference character:

- **Ground truth**: `clip.SampleAnimation` on a hidden copy of the character with its Humanoid avatar, every bone reset to rest before each sample.
- **SA**: `FamilyClips.TrySample` (the default sampling, below) then `TryEncode` (packed, the shipping setting) then `SharedClipReader.Load`, `SharedClip.Sample`, the decoded planar root, and `FamilyClips.TryApply` on the same character.
- **Compared at every encoded sample** (the clip's frames are always among them): world rotation of every bone the plan maps to a Humanoid role; world position of hips, head, hands and feet; the planar root path and heading; duration; loop flag; NaN/Inf; `.saclip` size.
- **Gate (unchanged)**: rotation at most 1.0 deg, position at most 1.0 cm, root path at most 1 cm, heading at most 1 deg, exact duration and loop flag.
- **In-between**: the same rotation comparison halfway between every pair of encoded samples, the worst place for SA's linear interpolation, with **Unity jumps** reported separately (rule below).

### Default sampling: adaptive rate

`FamilyClips.TrySample(plan, clip, ...)`, `TrySample(plan, clip, hz, ...)` (with `hz` as the base), `TryConvert` and the shared-family baker sample each clip at its own frame rate and double the rate (2x, 4x, at most 8x) until Unity's pose halfway between every pair of samples is within 1.5 deg (`FamilyClips.InBetweenDegrees`, any canonical bone in model space, decoded from the packed `.saclip`, Unity jumps left out). Sample counts are `multiplier * (clip frames - 1) + 1`, so every clip frame stays a sample and the per-frame gate is unchanged. `TrySampleFixed` keeps a fixed rate; `TryEncode(..., hz, ...)` writes the clip's exact length and loop flag for samples that came from `TrySample`. No codec change.

### Unity jump rule (stated, tested)

**A clip frame is left out of the in-between measure only when Unity itself turns some mapped bone more than 90 deg (`FamilyClips.JumpDegrees`) within one sample step of the top rate, 1/(8 x clip fps) s.** `FamilyClips.TryUnityJumps` finds it: every clip frame whose sampled path (samples and midpoints) exceeds 90 deg is a candidate, and Unity is sampled 16 times inside that frame (`JumpProbeSteps`, half the 8x step); the frame is a jump when two consecutive half-steps carry more than 90 deg. No allowed rate can follow such a turn. The adaptive sampler and `FamilyClipSweep` use the same function. Tests: `JumpRuleExcludesOnlyTurnsNoSampleRateFollows` (a fast in-range twist and spine motion has 0 jumps and is followed within 1.5 deg; a twist muscle ramped to 3 flips Unity's upper arm 180 deg within 1/240 s at muscle 2.0, is the only left-out frame, is reported as `beyond-limit` naming `Left Arm Twist In-Out`, and has 0 jumps under per-bone twist).

This replaces the 0.3.4 rule (90 deg along a whole clip frame), which left out 2646 rows (21%) including fast but smooth motion: the hips of `Jump_Attack_Spiral_4_Loop` turn 105 deg per frame (root turns 62 deg per frame) and SA's in-between there is 32, 9.4, 3.1, 0.87, 0.32 deg at 1x/2x/4x/8x/16x, so a rate does follow it. Under the new rule those frames count and raise the rate.

**Evidence that every left-out frame is a source-data jump.** For each jump frame the sweep reads the clip's own curves (`FamilyClipSweep.SourceOf`): the muscles of the jumping bone and of its Humanoid parents, their range in that frame, the largest value, and the RootQ turn; the CSV columns `jump_kinds` and `jump_evidence` hold it. Muscles are normalised to the avatar's limits (plus or minus 1 is the limit). Result on the full corpus: on the partial final sweep (11187 rows, below) 609 jump frames in 238 rows (2.1%, 34 clips), **all 609 `beyond-limit`** (a chain muscle outside plus or minus 1), none `fast-source` or `in-range`; every row whose in-between would be 90 deg or more is one of them (238 of 238). On the 2-character run of the whole corpus: 188 jump frames, 78 rows, 39 clips, all `beyond-limit`.

| Source animation (all takes and `_Root`/`_ZeroHeight` variants) | rows left out | worst SA in-between inside the jump | jumping bone, Unity's turn within 1/240 s | muscle beyond the limit in that frame |
|---|---|---|---|---|
| Dual_Blade Skill_A, Skill_E, Attack_3Combo_A, Jump_Attack_Spiral_3/5/ALL, Equip/Unequip, Attack_4Combo_B | 2 to 8 frames each | 90 to 180 deg | RightUpperArm/LeftUpperArm 180 to 183 deg | Right/Left Arm Twist In-Out 2.00 to 2.27 |
| Combo_Attack_03_All (Part 3/4), Combo_Attack_05_04B/C, combo04_1, EllenCombo4 | 1 to 5 | 180 deg | Left/RightLowerArm 180 deg | Forearm Twist In-Out 2.00 to 2.03; Shoulder Front-Back -2.3 to -2.6 |
| Dual_Blade KnockDown_Front(_Root) | 1 | 164 deg | Head 226 deg | Head Tilt Left-Right 8.5, Head Nod Down-Up -4.4 |

Every one is a twist, shoulder or head muscle driven past its limit: a twist muscle at 2.0 or more is a twist of 180 deg or more (default limit 90 deg), where Unity's split of the twist between the bone and its neighbour (avatar twist 0.5) flips to the other side of the half-turn; `KnockDown_Front` drives the head to 8.5 times its limit. Under per-bone twist (below) the twist flips disappear (`Unequip_DualBlade_To_Fighter`: 2 jump frames with the avatar's twist, 0 with per-bone twist).

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

## Result (default adaptive sampling, final code)

**Partial (the run was stopped by the usage limit at 1243 of 1382 clips): 11187 of 11187 rows pass the per-frame gate on all 9 character variants.** Worst rotation 0.0128 deg, position 0.017 cm, root 0.0007 cm / 0.0002 deg, duration and loop flag exact. In-between outside jumps: p50 0.93, p90 1.375, p99 1.815 deg (gate p90 2, p99 5: met); 41 rows above 2 deg, 21 above 5 deg, worst 28.4 deg (`EllenCombo3`, RightEye, on Ellen/Terraformer/Sidekick; not analysed). Size 4.0 KB to 2.95 MB, mean 82.9 KB; rate 1x/2x/4x/8x on 2631/4012/2923/1621 rows, mean 3.16x. The 2-character run over all 1382 clips (same code): 2764/2764 pass, p90 1.37, p99 1.76, worst 5.75 deg (`KnockDown_Front` frame 2, Head).

## In-between accuracy by rate

| Sampling | rows at most 1 deg | at most 2 deg | median | p90 | p99 | worst | mean size |
|---|---|---|---|---|---|---|---|
| clip rate (1x), 0.3.4 rule | 16.0% | 44.5% | 2.14 deg | 7.05 deg | 16.5 deg | 37.2 deg | 31.6 KB |
| 2x, 0.3.4 rule | 46.1% | 76.3% | 1.08 deg | 3.22 deg | 7.03 deg | 15.6 deg | 58.3 KB |
| 4x, 0.3.4 rule | 83.3% | 98.0% | 0.42 deg | 1.33 deg | 2.59 deg | 6.87 deg | 111.9 KB |
| adaptive, 0.3.4 rule | 60.1% | 100% | 0.87 deg | 1.35 deg | 1.48 deg | 1.69 deg | 80.6 KB |
| **adaptive, new rule (partial, 11187 rows)** | 56.8% | 99.6% | 0.93 deg | **1.375 deg** | **1.815 deg** | 28.4 deg | 82.9 KB |

The fixed-rate rows were measured on 0.3.4 and leave out the frames of the old, wider jump rule, so their tails are, if anything, lower than they would be now. The gate (p90 at most 2 deg, p99 at most 5 deg outside Unity's own jumps) is met by the default.

## Real runtime path: SubScene bake and Play mode, whole corpus, with Rukhanka

`SA.Conformance.PlayModeConformance` builds `Assets/zz_conformance/Conformance.unity` with a SubScene holding, per character, `RigAuthoring` (Cpu) + `SharedFamilyAuthoring` (avatar twist), a second SA rig with `SharedFamilyAuthoring.Twist = PerBone`, and a controller-free Rukhanka rig (driver below). It enters Play mode in the batchmode Editor (main scene, SubScene closed, baked by the import worker) and in one eval disables `UpdateWorldTimeSystem`, then for every clip and **every clip frame** seeks each rig, runs `World.Update()` twice and reads each bone entity's `LocalToWorld` relative to its rig entity. Two truths are sampled per frame: `SampleAnimation` with the avatar as imported, and with a copy of the avatar whose four twist settings are 1 (`FamilyClips.PerBoneTwistAvatar`). Rotation of every Humanoid bone and position of hips, hands and feet; gate 1 deg / 1 cm.

All 1382 Humanoid clips ran in 7 batches of 200 (`PlayModeConformance.HumanoidClips` order, `BuildScene(..., perBoneTwist: true)`), 51,620 frames per character, on PolygonSyntyCharacter and Neophyte_RIG (Mixamo); about 13 minutes in all.

| Comparison, 1382 clips, every frame | PolygonSyntyCharacter | Neophyte_RIG |
|---|---|---|
| SA vs Unity (avatar as imported) | **1382/1382**, 0.018 deg, 0.029 cm | **1382/1382**, 0.019 deg, 0.033 cm |
| SA.PerBoneTwist vs Unity with twist 1 | 1382/1382, 0.017 deg | 1382/1382, 0.017 deg |
| Rukhanka vs Unity (avatar as imported) | 1/1382, 89.9 deg (twist bones), 22.0 deg outside them | 1/1382, 89.9 deg, 20.4 deg outside |
| Rukhanka vs Unity with twist 1 | 1379/1382, 22.0 deg, 7.3 cm | 1379/1382, 20.4 deg, 9.9 cm |
| SA vs Rukhanka | 1/1382, 89.9 deg | 1/1382, 89.9 deg |
| **SA.PerBoneTwist vs Rukhanka** | **1379/1382** | **1379/1382** |
| cannot play (Rukhanka / SA) | 0 / 0 | 0 / 0 |

- **SA matches Unity's Humanoid on every clip and frame** (1382/1382 per character, worst 0.019 deg, 0.033 cm), through the real baker and runtime.
- **Rukhanka is Unity's Humanoid with every avatar twist setting at 1.** Against Unity as imported (twist 0.5) it fails 1381 of 1382 clips on both characters, every time on the upper arm, forearm, upper leg or lower leg about the bone's own axis (median 25.6 deg, worst 89.9 deg; joint positions match: 2758 of 2764 rows are within 1 deg and 1 cm outside those 8 bones, the 6 others are the 3 clips below); the one clip that passes is a T-pose. Against Unity with the twist settings at 1 it passes 1379 of 1382 on both characters. This is a Rukhanka limitation: its baker (`RigDefinitionBaker.CreateHumanoidBoneRotationData`) reads each bone's pre/post rotation, limits and sign but never `HumanDescription.upperArmTwist`, `lowerArmTwist`, `upperLegTwist` or `lowerLegTwist`, and `MuscleValuesToQuaternion` writes each twist muscle to its own bone; its manual does not mention twist.
- **Opt-in `HumanoidTwist.PerBone` reproduces Rukhanka's twist handling**: SA with per-bone twist against Rukhanka passes **1379 of 1382 clips on both characters within 1 deg / 1 cm**, and against Unity with twist 1 it passes 1382/1382 (worst 0.017 deg).
- **The 3 clips are Rukhanka's own differences from Unity, not twist**, the same 3 on both characters, and SA (both modes) matches Unity on them:
  - `Billion End` (worst 3.9 deg, 6.4 cm) and `Billion Loop` (0.73 deg, 1.77 cm), 25 fps clips whose muscles step by 2 to 4 units within one frame (`Right Hand In-Out` -0.49 to 1.62, `Right Shoulder Down-Up` -2.45 to 1.48). Rukhanka resamples the root and hips curves from Unity at 1/60 s (`AnimationClipBaker.SampleUnityAnimation`, "Sample root and hips curves ... from unity animations") and interpolates between those samples: at 1/60 s-aligned times it matches Unity exactly (0.00 deg at 0.40 s and 0.20 s), between them it is 3 to 5 deg and 2 to 7 cm off.
  - `A_Run_BckStrafeFL_Masc` (22 deg on a finger, 7.3 to 9.9 cm), a 0.667 s loop with Loop Pose on whose last frame differs from its first. Rukhanka's `CalculateLoopPose` turns every bone toward its first-frame rotation in proportion to normalised time, so near the end it shows the first frame (0.83 deg from Unity at t = 0), where Unity's `SampleAnimation` shows the last.
  - Outside those 3 clips Rukhanka is within 0.40 deg / 0.64 cm of Unity with twist 1 (the same 1/60 s resampling), SA per-bone within 0.40 deg / 0.64 cm of Rukhanka.
- **Clips Rukhanka plays that SA does not: 0. Clips Rukhanka cannot play: 0** (no clip blob missing, no refusal; additive, mirrored, sub-range, 60 fps and 67 s clips included). SA cannot play: 0.

The Rukhanka driver is not package source (the package carries no Rukhanka code, `PackageSourceHasNoCommentsReflectionLegacyInputOrRukhanka`); it is kept as `docs/conformance-rukhanka.cs.txt` with its assembly definition `docs/conformance-rukhanka.asmdef.txt` (versionDefine on `com.rukhanka.animation`) and runs from `Assets/zz_conformance_tools/Editor` of the sweep project, unchanged from 0.3.4. It registers with `PlayModeConformance.ExtraRigs`/`ExtraDrivers` and puts a Rukhanka rig next to the SA rigs: `RigDefinitionAuthoring` (User Defined, the character's avatar, CPU engine, no root motion, all bone entities kept) and `AnimationAssetSetAuthoring` with the batch's clips; each frame time is submitted with `ScriptedAnimator.ResetAnimationState` + `PlayAnimation(clip blob from BlobDatabaseSingleton, normalised time)`. Rukhanka applies no root motion, so its bones are compared with Unity's pose relative to the root transform `SampleAnimation` moves, and SA's pose is brought into the same frame for SA against Rukhanka.

### Per-bone twist option

`FamilyClips.TryPlan(character, HumanoidTwist.PerBone, out plan, out error)` (or `SharedFamilyAuthoring.Twist = PerBone` for baked characters, `FamilyClipSweep.Run(..., twist)` for the sweep) samples the clip on a copy of the character whose Animator uses `FamilyClips.PerBoneTwistAvatar`: the avatar rebuilt by `AvatarBuilder.BuildHumanAvatar` from its own `HumanDescription` with `upperArmTwist`, `lowerArmTwist`, `upperLegTwist` and `lowerLegTwist` at 1. Rebuilding PolygonSyntyCharacter's avatar unchanged reproduces the imported avatar exactly (0 deg on `Unequip_DualBlade_To_Fighter`), and rebuilding it with the twists at 1 reproduces the `PolygonSyntyTwist1.fbx` import exactly (0 deg; the imported avatar differs by 88.8 deg on the upper arm). The plan owns that hidden avatar: `FamilyRigPlan` is `IDisposable`. The default stays `HumanoidTwist.Avatar` (Unity's Animator). Test: `PerBoneTwistPlanSamplesLikeAnAvatarWhoseTwistSettingsAreOne` (per-bone on a 0.5 avatar vs the avatar at 1: 0.0000 deg on every canonical bone; vs the 0.5 avatar: 36/58/18/30 deg on upper arm, lower arm, upper leg, lower leg; the sweep passes it against the per-bone truth).

## Tests

Cause of the GPU flakes: on Linux a `-batchmode` Editor picks OpenGLCore (log: "Creating OpenGL 4.5 graphics device", Direct threading); the windowed Editor runs Vulkan. On OpenGLCore, from a fresh Editor, SA.Tests passed the first run and failed 3 of the next 5 (`ReorderedSharedClipsHaveIndependentCpuAndGpuPlayback` 0.6 off, `SkinningSystemInitializesTheSentinelBeforeAnySkinIsLoaded` all zeros, `SkinningSystemRegistersSharedAndChangedAssetTables`). A probe of the exact ReorderedSharedClips GPU path read back a rest pose in 2 of 480 dispatches on OpenGLCore (the Motions buffer written through the CommandBuffer arrived empty; `AsyncGPUReadback` and a second `GetData` fail the same) and 0 of 480 on Vulkan. Not the Editor's age: it failed minutes after a fresh start. Fix: the sweep project lists Vulkan for Linux, and the GPU readback tests skip with the reason on OpenGLCore (`TestRigs.RequireGpu`, `RequireReliableReadback`). On Vulkan: 5 of 5 SA.Tests runs passed (108/108 before the new tests, then 110/110 with them, including the 2 new tests). The `Unity Data Store service ... is no longer running` failure (earlier session log, during `SkinningShaderTests`) is the Editor's Data Store child process dying mid-run; the Test Framework fails whichever test logs then. It did not occur in this session; stop Editors by pid, never `pkill -f` on a project path (it matches that child).

## Failures found and fixed

| # | Root cause (class) | Symptom before the fix | Fix |
|---|---|---|---|
| 1 | calibration math: model-space rows in `SharedFamilyBake.TargetToCanonical` used `inverse(target parent) * local` instead of `inverse(canonical parent) * target model` | Rukhanka pilot: 41 of 44 rows failed. Any clip whose root object moves (hips under an unmapped parent): 22 to 180 deg; bones under an unmapped intermediate (Terraformer has no neck in its avatar): 110 deg | corrected the formula; the shared-family baker uses the same function |
| 2 | encoder: verify sampled the last frame of a looping clip at `t = duration`, which wraps to frame 0 | every looping clip whose last pose differs from its first refused to encode | verify that frame just before the end |
| 3 | calibration: rest positions instead of the avatar skeleton | Grruzam Fighter: 1217 of 1217 rows failed, 6 to 9 cm (Unity writes Humanoid bone positions from the avatar, not the FBX rest) | Humanoid bones take positions from `humanDescription.skeleton` (`FamilyClips.ApplyAvatarCalibration`) |
| 4 | avatar translation DoF / arm-leg stretch: the canonical pose carried no per-bone translation | Grruzam Fighter, 1 clip: 5.2 cm (spine moves 4.5 cm, hands stretch 2 cm) | `CanonicalTranslation`: canonical bones carry their translation offset, `FamilyClips.TryApply` and `SharedFamilySystem` apply it back |
| 5 | sampling: frames stopped at `floor(length * hz)` and `TryEncode` always wrote `loop = false` | duration short by up to one frame on non-integer lengths; loop flag lost on 548 clips | `TrySample` spaces frames evenly over the exact length; `TryEncode(..., duration, loop, ...)`, and `TryEncode(..., hz, ...)` keeps the length and loop flag of `TrySample` samples |
| 6 | baker consistency: the shared-family baker mapped by name and calibrated from rest; it also rejected FBX scale noise | a `.saclip` from `FamilyClips` and a baked character disagreed on A-pose rigs; Neophyte (scale 1.000019) and Ellen (1.000021) could not bake at all | baker maps Humanoid avatars through `HumanBodyBones`, calibrates like `FamilyClips`, and accepts unit scale within 1e-4 like `FamilyClips` |
| 7 | baker sampling: its own sampler used `ceil(length * min(rate, 60)) + 1` frames | 120 fps clips lost every other frame; non-integer lengths shifted every sample off Unity's frame times | the baker samples through `FamilyClips.TrySample` |
| 8 | in-between: fixed clip-rate sampling | p90 7.0 deg, p99 16.5 deg, worst 37 deg halfway between frames | adaptive rate (above) |
| 9 | jump rule too wide: 90 deg along a whole clip frame | 21% of rows left fast but smooth frames out of the measure (spiral spins followed at 8x within 0.9 deg) | 90 deg within one top-rate step, probed in Unity (`TryUnityJumps`); every remaining left-out frame has a muscle beyond its limit |
| 10 | test environment: batchmode Editor on OpenGLCore | `GpuMatchesCpuEvaluationAndSkinning`, `SkinningSystemInitializesTheSentinel...`, `ReorderedSharedClips...`, `SkinningSystemRegistersShared...` failed at random (stale compute results read back) | sweep project runs Vulkan; GPU readback tests skip with the reason on OpenGLCore |

## Not Humanoid (190 clips, listed with proof in the CSV)

| Count | Proof |
|---|---|
| 148 | FBX `animationType=Generic`, `avatar.isHuman=False` (Rukhanka Ellen/Terraformer/Chomper/SkinnedFigure/Armature generic variants, baseplayer props and creatures) |
| 36 | `.anim` with `humanMotion=False` and no muscle curves: transform-curve rigs (Kyle, Armature, Ellen generic, IK targets), props, camera, blend shapes |
| 6 | takes inside Humanoid FBX with 0 curves of any kind (Blender per-object empty actions) |

## Reproduce

1. `~/sweep-project`: ProjectSettings and manifest from TestingAssets plus a Linux graphics API entry (`LinuxStandaloneSupport`, Vulkan, Auto off) so the batchmode Editor runs Vulkan; com.sa.unity (this worktree) and com.sa.motion.unity as `file:` packages, SA.Inbetween.Clips and `com.rukhanka.animation` embedded; corpora copied with their `.meta` (deduplicated by FBX+meta hash) into `Assets/zz_corpus_*`.
2. Batchmode Editor: `SA_PROJECT=~/sweep-project eng/unity_start.sh` (start it with `flock -o` so the Editor does not inherit the wrapper lock).
3. `FamilyClipSweep.Run(characters, scales, folders, "SweepResults/sweep_final.csv", 0, int.MaxValue, false, 0f)` through an eval; `1f`/`2f`/`4f` for fixed rates.
4. Play mode, per batch b of 200: `PlayModeConformance.BuildScene("Assets/zz_conformance", characters, PlayModeConformance.HumanoidClips(folders).GetRange(200 * b, n), true)` in Edit mode (it leaves `Conformance.unity` open), `unity command editor.playmode start`, then `PlayModeConformance.Measure("Assets/zz_conformance", "SweepResults/conformance_full_bNN.csv", int.MaxValue)` until it stops returning "not ready" (the SubScene bakes in about 30 s), `editor.playmode stop`.

## Remaining

Done on `feat/closeout2` (not pushed): per-bone twist option and its test; tightened jump rule with source evidence and its test; GPU test isolation; whole-corpus Play-mode SA/Rukhanka run on Synty and Neophyte (`~/sweep-project/SweepResults/conformance_full_b00..b06.csv`, 22112 rows).

Not done (usage limit):
1. The final 9-character sweep stopped at 1243 of 1382 clips (11187 rows, all pass; `~/sweep-project/SweepResults/sweep_final.csv`, copy `/tmp/claude-1000/sp/sweep_final_partial.csv`). Rerun `FamilyClipSweep.Run(..., 0f)` to completion and update the Result numbers and `docs/clip-sweep-summary.csv` (still the 0.3.4 numbers).
2. Not analysed: `EllenCombo3` in-between 28.4 deg on RightEye (Ellen, Terraformer, Sidekick), outside jumps.
3. SA.Tests 3 runs in a row from a fresh Editor start on the final committed code: not run after the last edits (5 of 5 clean runs on Vulkan were on earlier states of this branch, the last one with all code changes except docs).
4. `SA.Inbetween.Clips` tests: not run (the sweep project embeds only its Runtime; the full package is `~/GitHub/sa/sa-inbetween/unity/com.sa.inbetween.sa`).
5. Fixed-rate 1x/2x/4x rows were not rerun under the new jump rule.
6. 3 clips Rukhanka plays differently from Unity outside twist (60 Hz root/hips resampling, Loop Pose), so SA.PerBoneTwist vs Rukhanka is 1379/1382, not 1382/1382; SA does not copy those Rukhanka artifacts.
7. The sweep project's batchmode Editor was stopped by pid (SIGKILL after a SIGTERM did not end it within 23 s).
