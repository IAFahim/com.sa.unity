Unreleased
----------

- Per-bone twist option: `FamilyClips.TryPlan(character, HumanoidTwist.PerBone, ...)` and `SharedFamilyAuthoring.Twist` sample a Humanoid clip through a copy of the avatar whose upper/lower arm and leg twist settings are 1, so each twist muscle turns its own bone only. That is how engines that ignore the avatar twist settings play Humanoid clips; the default (`HumanoidTwist.Avatar`) stays Unity's Animator. `FamilyRigPlan` is now `IDisposable` (it owns that hidden avatar); `FamilyClips.PerBoneTwistAvatar` and `FamilyClips.SamplingCopy` are public. `FamilyClipSweep.Run` takes the twist mode.
- Unity jump rule: an in-between interval is left out only when Unity itself turns a mapped bone more than 90 deg within one sample step of the top rate (1/(8 x clip fps) s), measured by `FamilyClips.TryUnityJumps` inside every clip frame whose sampled path exceeds 90 deg. The old rule (90 deg along a whole clip frame) also left out fast spins that 4x and 8x follow; those now raise the sample rate like any other motion.
- `FamilyClipSweep` reports where each left-out jump comes from in the clip's own curves (`jump_kinds`, `jump_evidence`: the muscles of the jumping bone and its parents, their range in that frame, the largest value and the root turn; `FamilyClipSweep.SourceOf`).
- `PlayModeConformance.BuildScene(..., perBoneTwist)` adds a per-bone-twist SA rig per character, `Measure` compares every rig against Unity with the avatar as imported and with per-bone twist, and every other driver against both SA rigs; `HumanoidClips(folders)` lists the corpus in sweep order for batching.
- GPU readback tests are skipped with the reason on OpenGLCore, where compute results read back in a batchmode Editor came back stale in measured runs; run the Editor on Vulkan to test the GPU path.

- Adaptive sampling is the default for `FamilyClips.TrySample`/`TryConvert` and the shared-family baker: each clip starts at its own frame rate and doubles (up to 8x) until Unity's pose halfway between samples is within 1.5 deg (`FamilyClips.InBetweenDegrees`), leaving out Unity's own jumps (a bone turning more than 90 deg within one clip frame). `TrySampleFixed`, `TrySampleFrames` and `TrySampleAdaptive` give explicit control; `TryEncode(..., hz, ...)` writes the clip's exact length and loop flag for samples from `TrySample`. Corpus numbers in `docs/clip-sweep.md`.
- The shared-family baker samples through `FamilyClips` (same calibration, sample times and rate as the `.saclip` tools) and puts the planar root back into the hips, so a baked character moves as `SampleAnimation` moves it.
- `FamilyClipSweep` measures fixed rates (`rateScale` 1, 2, 4) or the default adaptive sampling (`rateScale` 0), reports `rate_x` and `jump_intervals`, and defines jumps by path length within a clip frame; the cubic estimate is gone.
- Conformance assembly `SA.Conformance`: bakes `SharedFamilyAuthoring` characters in a SubScene, enters Play mode and steps the world by hand against `SampleAnimation`; other animation systems plug in through `PlayModeConformance.ExtraRigs` and `ExtraDrivers`.

- Clip sweep fixes (`docs/clip-sweep.md`): model-space rows of `TargetToCanonical` used the inverse of the target parent instead of the canonical parent, which broke every clip whose root moves and every bone under an unmapped intermediate bone; looping clips whose last frame differs from the first now encode; Humanoid avatars calibrate human-bone positions from the avatar skeleton, as Unity writes them; canonical poses carry per-bone translation offsets (translation DoF, arm/leg stretch) through `CanonicalTranslation`, applied by `FamilyClips.TryApply` and `SharedFamilySystem`; the shared-family baker maps Humanoid avatars through `HumanBodyBones` and calibrates from the avatar like `FamilyClips`.
- `FamilyClips.TrySample` spaces frames evenly over the exact clip length; new `TryEncode(plan, canonical, root, duration, loop, lossless, ...)`, `TryConvert`, `SampleCount`, `SampleSeconds`, `EncodedDuration`.
- `FamilyClipSweep` and `FamilyClipRuntimeCheck` compare SA against `AnimationClip.SampleAnimation` per frame and through the timeline system; menu `Tools/SA/Animation Library/Check Clips Against Unity (Selected Character and Folders)`.
- Opt-in inertialized transitions on CPU rigs: `RigAuthoring.Inertialize`, `TransitionPlayback.Play` and `Blend`, `TransitionIntent`, `BoneTransition`. A new request cuts to its destination and decays the offset from the last output; no outgoing clip is kept alive.
- `SA.Runtime` now references the portable `SA.Motion` library through the `com.sa.motion.unity` package. Add that package to the project manifest.
- Optional shape addon assembly `SA.Shape.Unity`: per-character proportions from `.sashape` definitions with revisioned parameters, staged Prepare/Commit timeline evaluation and named channel controls. Shaped rigs are driven by the addon system; ordinary characters compile and behave as before. Needs `SA.Shape.dll` from the `com.sa.motion.unity` bridge next to the existing DLLs.
- Shape skin/contact pass: affine per-renderer palette upload through `ShapeSkinPalette.Build` with original inverse binds, inverse-transpose normal reference plus renderer-owned renormalize/orthogonalize/handedness, corrective morph weight upload through `ShapeMorphs.Evaluate`, and current-shape sole markers plus rotation-only contact wiring. The core palette route skips shape-driven rigs so each skin has one writer.
- Shape physical pass: kinematic-customize-then-one-rebuild through `ShapePhysical`, old/new snapshot collider/joint/mass refit from `ShapeInertia` with an explicit mass policy, visible refusal of shape edits while live-physical, and a wardrobe validation hook. Needs the repacked `SA.Shape.dll` with `ShapeInertia`.
- Shape bake/drive fixes: `.sashape` files import as `TextAsset` through a versioned `ScriptedImporter`; the shared-family name scorer maps shoulder/elbow joints and numbered mirror (`_1`) sides and drops topology-violating assignments; `ShapeBaker` publishes the initial `ShapeContactState`; the GPU skin job yields matrix ranges of shape-driven rigs to the shape palette upload so each skin keeps one writer.
- Public editor API `FamilyClips` (`TryPlan`, `TrySample`, `TryEncode`, `TryCapture`, `TryApply`) and `FamilyRigPlan`: an `AnimationClip` on a character becomes humanoid-v1 `.saclip` bytes with a planar root (hips ground projection and leg-line heading, the SOMA convention), and back to character locals. Humanoid avatars map through `HumanBodyBones` and calibrate from the avatar T-pose, reporting rest-pose differences above 5 degrees; Generic rigs use the name scorer plus Mixamo naming. `SharedFamilyBake.EncodeCanonicalSamples` gains a root policy overload; the shared-family baker is unchanged.

0.3.0
-----

- Share skeletons, clips and masks independently across character animation lists.
- Reuse GPU sample ranges when clip lists overlap, are reordered, or use different masks.
- Validate stable skeleton layouts and support explicit ExtraBones.
- Key persistent sub-assets by GUID/local file ID and include sampling/additive/Avatar dependencies.
- Store serializable external blob references in ClipAsset and MaskAsset buffers.
- Names.Clip/Mask and manual playback/evaluation now accept the corresponding asset tables; normal Playback commands and RigAuthoring clip/mask setup are unchanged.

0.2.0
-----

Rebuilt from scratch. Every API changed.

- Real GPU engine: compute kernel evaluates poses and skin matrices.
- Own skinning include and material property; no dependency on Unity.Deformations.
- CPU engine skins from bone entities, so bone edits show on the mesh.
- Layers stack; additive uses the correct reference pose; phase-synced blends.
- Idempotent commands; clips baked through Unity's own sampler.

0.1.0
-----

First prototype.
