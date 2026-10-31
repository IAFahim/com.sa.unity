Unreleased
----------

- Opt-in inertialized transitions on CPU rigs: `RigAuthoring.Inertialize`, `TransitionPlayback.Play` and `Blend`, `TransitionIntent`, `BoneTransition`. A new request cuts to its destination and decays the offset from the last output; no outgoing clip is kept alive.
- `SA.Runtime` now references the portable `SA.Motion` library through the `com.sa.motion.unity` package. Add that package to the project manifest.

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
