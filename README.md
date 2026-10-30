SA - Simple Animation
=====================

Skeletal animation for unmanaged ECS (Entities 6.7, Unity 7000.0).
One list of `ClipState` per character is the whole animation state.
No state machines, no parameters, no graphs.

Setup
-----

1. Add `RigAuthoring` to the character root. List the clips and masks. Pick `Engine.Gpu` (fast, no bone entities) or `Engine.Cpu` (bone entities you can drive).
2. Select the character and run `Tools/SA/Convert Skinned Mesh Renderers`. Each `SkinnedMeshRenderer` becomes a `MeshRenderer` plus `SkinAuthoring`.
3. Give the renderer a skinning material. Import the `Showcase` sample and use `SA Skinned Lit.shader`, or add a Vector2 property `_SaSkin` (Override Property Declaration: Hybrid Per Instance) to your ShaderGraph and feed `Skin_float` from `Runtime/Shaders/Skinning.hlsl` through a Custom Function node.
4. Put the character in a subscene and press Play.

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

With skins, the skeleton is the union of skin bones, their ancestors, Avatar bones and `RigAuthoring.ExtraBones`. Animated transforms outside that skeleton are a baking error; add them to ExtraBones. Without skins, all descendants are included. Clip lists never select the skeleton.

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

Limits
------

No root motion, events, IK solvers, blend shapes, humanoid retargeting, attachments to GPU rigs or animation culling.
Meshes need `baseVertex` 0 and at most 4 influences per vertex are kept.
GPU rigs are one compute thread per character; see `Documentation~/benchmark.md` for measured cost.
