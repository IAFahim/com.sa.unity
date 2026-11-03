Unreleased
----------

- Opt-in inertialized transitions on CPU rigs: `RigAuthoring.Inertialize`, `TransitionPlayback.Play` and `Blend`, `TransitionIntent`, `BoneTransition`. A new request cuts to its destination and decays the offset from the last output; no outgoing clip is kept alive.
- `SA.Runtime` now references the portable `SA.Motion` library through the `com.sa.motion.unity` package. Add that package to the project manifest.
- Optional shape addon assembly `SA.Shape.Unity`: per-character proportions from `.sashape` definitions with revisioned parameters, staged Prepare/Commit timeline evaluation and named channel controls. Shaped rigs are driven by the addon system; ordinary characters compile and behave as before. Needs `SA.Shape.dll` from the `com.sa.motion.unity` bridge next to the existing DLLs.
- Shape skin/contact pass: affine per-renderer palette upload through `ShapeSkinPalette.Build` with original inverse binds, inverse-transpose normal reference plus renderer-owned renormalize/orthogonalize/handedness, corrective morph weight upload through `ShapeMorphs.Evaluate`, and current-shape sole markers plus rotation-only contact wiring. The core palette route skips shape-driven rigs so each skin has one writer.
- Shape physical pass: kinematic-customize-then-one-rebuild through `ShapePhysical`, old/new snapshot collider/joint/mass refit from `ShapeInertia` with an explicit mass policy, visible refusal of shape edits while live-physical, and a wardrobe validation hook. Needs the repacked `SA.Shape.dll` with `ShapeInertia`.
- Shape bake/drive fixes: `.sashape` files import as `TextAsset` through a versioned `ScriptedImporter`; the shared-family name scorer maps shoulder/elbow joints and numbered mirror (`_1`) sides and drops topology-violating assignments; `ShapeBaker` publishes the initial `ShapeContactState`; the GPU skin job yields matrix ranges of shape-driven rigs to the shape palette upload so each skin keeps one writer.

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
