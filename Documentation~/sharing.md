Shared animation assets
=======================

A rig entity holds a shared `RigBlob` skeleton plus small `ClipAsset` and `MaskAsset` buffers. Those buffers contain external blob references, not samples. Sampled clips and masks are immutable independent blobs. Entities serializes their references through the standard component/buffer reference tracking.

A character with Idle, Walk and Run and another with Run, Attack, Idle and Walk share three exact clip references. Only Attack adds sampled data. Their clip IDs differ because IDs index each character's table. Masks are separate assets, so adding or reordering masks cannot invalidate clips. CPU and GPU engines use the same assets and keep independent playback and pose output for every character.

Compatibility and invalidation
------------------------------

The skeleton key covers canonical bone paths, order and local rest poses. It does not include available animations, masks or sample rate. Skinned rigs select skin bones, their ancestors, Avatar bones and explicit ExtraBones. Rigs without skins select all descendants. Every layout orders parents before children and sibling names ordinally. A curve targeting a transform outside a skinned layout is rejected with an ExtraBones instruction.

Each clip key includes its skeleton key, sampler version/rate, source identity, dependency content and current curve keys/wrap modes, Avatar identity/content, and additive-reference source/time. Persistent source identity includes both GUID and local file ID, distinguishing identically named sub-assets in one imported file. Unsaved editor objects use session identity and serialized contents. Humanoid sampling additionally distinguishes root scale and root-motion context. Changing a rest pose or bake setting creates a separate compatible dataset instead of reusing unsuitable samples. Additive references and Avatar dependencies are recorded before cache lookup.

Each mask key includes only its source identity/content, current transform paths/weights and compatible skeleton key. Current curves and mask entries also invalidate edits made before an asset is saved or reimported. The implicit full mask is shared per skeleton. Skin meshes are keyed independently of animation availability as well.

Runtime storage
---------------

The GPU registry uses separate hash-to-offset maps for skeletons, clips and masks. It copies and uploads an unseen asset once. Growing a graphics buffer reallocates it and reuploads its existing contents. Registry storage is append-only until the SkinningSystem/world is disposed; there is no per-asset eviction or streaming reclamation in this version.

Character instantiation copies small reference tables, playback state and pose allocations; it does not copy animation samples. CPU blob data, CPU staging arrays and GPU buffers are different storage needed by their respective consumers. The change eliminates duplication between character configurations, not those backend copies. It does not alter the compute kernel or establish a new 50,000-character performance result.

Migrating from 0.2
------------------

- Authoring clip/mask lists and normal Playback.Play/CrossFade/Blend/Stop calls stay the same.
- `Rig.Value.Value` now contains only `Id` and `Bones`.
- Query `DynamicBuffer<ClipAsset>` and use `Names.Clip(clips, name)`; query `DynamicBuffer<MaskAsset>` and use `Names.Mask(masks, name)`.
- `Names.Bone(ref skeleton, name)` stays the same.
- Low-level sampling receives `ref clips[index].Value.Value`.
- Manual `Playback.Step` receives the clip-table span. Manual `Evaluation.Bone` receives skeleton, clip-table span, mask-table span, states and bone index.
- Rebuild baked subscenes after the package update. Existing source prefabs need ExtraBones only when animations target non-skinned transforms outside the selected skeleton.

Verification
------------

The sharing tests check exact BlobAssetStore reference reuse, partial-overlap/reordered tables, independent masks, stable layouts and validation, sample-affecting key changes, same-file sub-asset IDs, Entities world save/load, and independently advancing characters evaluated by CPU and GPU. Existing blend/playback/bake/GPU-parity tests remain part of the regular SA suite. Explicit performance benchmarks compile with the new asset tables; old timings remain historical until measured again.
