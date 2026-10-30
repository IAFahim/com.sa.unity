Decisions
=========

- Skinned meshes are converted to MeshRenderer plus SkinAuthoring so no external deformation baker touches them.
- Gate G2 (strip GPU bone entities): adding `BakingOnlyEntity` from a baking system works. A GPU Kyle bakes to a rig entity and a renderer entity only.
- Gate G1 (shader route): the hand-written URP shader with a DOTS-instanced `_SaSkin` property works. No ShaderGraph asset ships; the README describes the Custom Function route.
- Gate G3 (sampling in a baker): clips are sampled with `SampleAnimation` on a hidden copy of the rig, restored per clip.
- Conversion keeps the original Animator on a humanoid rig. SA bakes onto the rig's own skeleton.
- Clip root motion is not extracted. A clip that moves its Root bone moves the character's bone, as the Run clip in the demo does.

- Skeletons, clips and masks are separate blobs. Only components/buffers hold external BlobAssetReference values, so Entities can serialize and relocate every reference.
- Clip lists are small per-character reference tables. Changing their order or membership does not duplicate shared sampled data; IDs remain local to the table.
- Skeleton layout comes from skin/Avatar bones and explicit ExtraBones, or all descendants for rigs without skins. Parents precede children and siblings use ordinal name order. Curves cannot silently enlarge a skinned skeleton.
- Clip keys include skeleton/rest compatibility, persistent asset GUID and local file ID, dependency content, sample rate, Avatar context and additive-reference source/time. Cache-hit bakers still register input dependencies.
- GPU skeleton, clip and mask registries are independent and append-only for the world's lifetime. Buffer growth reuploads existing data; per-asset eviction is outside this change.
