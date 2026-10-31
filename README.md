SA - Simple Animation
=====================

Skeletal animation for unmanaged ECS (Entities 6.7, Unity 7000.0).
One list of `ClipState` per character is the whole animation state.
No state machines, no parameters, no graphs.

Setup
-----

0. Add the portable motion bridge to the project's `Packages/manifest.json`, next to this package: `"com.sa.motion.unity": "<path or git url of sa-motion>/packages/com.sa.motion.unity"`. `SA.Runtime` references it.

1. Select a model, prefab or scene character and run `Tools/SA/Set Up Character`. For physics use `Tools/SA/Set Up Ragdoll` from the Reactions package. Selecting an asset creates an SA prefab; the imported model is preserved.
2. Drop additional models, Animator Controllers or folders into **Animation packs**. Clips, the Avatar, skin bones and materials are discovered. Runtime playback uses ECS without Animator Controller evaluation.
3. Choose **Start animation**, **In place by default**, **Smooth transitions**, and optionally **Plant feet on ground**. Smooth transitions and foot IK enable CPU bones automatically. Foot IK needs the Reactions package and Unity Physics ground colliders.
4. Put the resulting prefab in a subscene and press Play.

Open **Resolved animations** to rename a clip or toggle **In place** for it. Packs may contain clips with the same name: later entries get a source-qualified name; changing that name does not duplicate their sampled data. Imported source clips are preserved. Humanoid animation packs use the character's Humanoid Avatar; generic clips require a compatible hierarchy.

Bone overrides, masks, sample rate and engine selection live under **Advanced**. The setup action preserves textures/colors and all material slots using the bundled URP skinning shader. Custom shader graphs can still use `Skin_float` from `Runtime/Shaders/Skinning.hlsl` and the `_SaSkin` per-instance property.

Why the conversion: a `SkinnedMeshRenderer` is always baked by someone else's deformation pipeline. A `MeshRenderer` goes through the standard Entities Graphics baker and SA owns the skinning.

Use
---

```csharp
void Execute(in DynamicBuffer<ClipAsset> assets, ref DynamicBuffer<ClipState> states, in Speed speed)
{
    var clips = new FixedList64Bytes<ClipId> { Names.Clip(assets, "Idle"), Names.Clip(assets, "Walk"), Names.Clip(assets, "Run") };
    Span<float> weights = stackalloc float[3];
    BlendSpace.Linear(stackalloc float[] { 0f, 2f, 5f }, speed.Value, weights);
    Playback.Blend(ref states, clips.AsReadOnlySpan(), weights, 0.2f, PlayOptions.Default);
}
```

Commands state the desired end state of a layer, so calling them every frame is correct.

| Call | Meaning |
|------|---------|
| `Play(clip, options)` | the layer plays this clip now |
| `CrossFade(clip, seconds, options)` | the layer fades to this clip |
| `Blend(clips, weights, seconds, options)` | phase-synced weighted blend |
| `Stop(layer, seconds)` | the layer fades out |
| `IsPlaying`, `NormalizedTime`, `HasFinished` | queries |
| `BlendSpace.Linear / Cartesian / Directional` | weights for `Blend` |
| `Names.Clip / Mask / Bone` | lookups, `None` on a miss |

`PlayOptions` carries layer, mode (`Override` or `Additive`), mask, wrap (`Loop`, `Clamp`, `PingPong`) and speed. Speed 0 pauses. `TimeScale` scales one character.

Shared data
-----------

Skeletons, sampled clips and masks are separate immutable blobs. Characters with compatible skeletons share each clip independently: Idle/Walk/Run and Idle/Walk/Run/Attack require four clip datasets. Reordering clips or changing masks keeps existing clip data. The GPU registry uploads each unique asset once, apart from buffer growth, and retains it until its world is disposed.

`Rig.Value` contains only skeleton data. `ClipAsset` and `MaskAsset` buffers contain small references; each character owns its playback states and output pose. Look up clips with `Names.Clip(assets, name)` and masks with `Names.Mask(masks, name)`. Clip and mask IDs are local to those tables.

With skins, the skeleton includes the skin/Avatar skeleton branches and their descendants, including unskinned sockets and helpers. ExtraBones is an optional override. Without skins, all descendants are included. Clip lists never select the skeleton.

Sharing requires matching hierarchy/rest pose, clip source/sub-asset/content, sampling rate and sampler context, including Avatar and additive reference. Different compatible character placements do not duplicate generic clips. See `Documentation~/sharing.md` for migration and cache limits.

The algebra
-----------

Layers compose in ascending order. Per bone, override states of a layer form a weighted mean that is lerped over the pose below by `min(1, total weight)`. Additive states add `Delta(sample, reference)`, where the reference is the clip's first frame. `Evaluation.Bone` is the whole rule; the compute kernel mirrors it line for line.

Engines
-------

| | `Engine.Gpu` | `Engine.Cpu` |
|-|--------------|--------------|
| bone entities | none | one per bone, `PoseDriven` enabled |
| pose computed by | compute kernel | `CpuPoseSystem` |
| skinning input | kernel pose | bone `LocalToWorld` |
| take over a bone | no | disable `PoseDriven` or write after `CpuPoseSystem` in `PoseConstraintSystemGroup` |

Both engines skin through the same vertex function.

Transitions without transition clips
------------------------------------

Tick `Inertialize` on a CPU `RigAuthoring`. The rig then carries a `TransitionIntent` and one `BoneTransition` per bone (about 208 bytes each).

```csharp
TransitionPlayback.Play(ref states, ref intent, key: 7, Names.Clip(assets, "Run"), seconds: 0.3f, PlayOptions.Default);
```

A new key cuts to the destination and the pose then decays from where it was. The same key again only updates speed and weights, so calling it every frame is correct. Reusing a key for a different clip set returns `RequestDecision.Misuse` and changes nothing. `TransitionPlayback.Blend` takes the same arguments plus weights, so locomotion blends keep working inside one request. GPU rigs do not support this yet.

Limits
------

`TransitionPlayback.PlaySynced` carries the gait phase into an incoming looping clip, useful for Walk/Run/Sprint. Ordinary `Play` keeps its restart semantics for one-shot actions.

The optional `com.sa.unity.ik` package supplies head look-at and two-bone target constraints. Reactions adds ground-aware foot planting and hit-flinch playback on a separate upper-body layer.

No extracted gameplay root motion, animation events, blend shapes, generic cross-skeleton retargeting, attachments to GPU rigs or animation culling. In-place baking removes horizontal travel while preserving vertical movement; it does not move the gameplay entity.
Meshes need `baseVertex` 0 and at most 4 influences per vertex are kept.
GPU rigs are one compute thread per character; see `Documentation~/benchmark.md` for measured cost.
