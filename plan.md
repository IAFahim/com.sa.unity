# SA active humanoid reactions and interruptible animation transitions

Implementation plan, revised on 2026-10-31 for the user's instruction to push the plan to main, create an issue and begin implementation. The reviewed architecture/plan must be committed and the issue created **before code edits begin**. The coordinator releases implementation after that publication acknowledgement. Independent mathematical/compatibility/physics foundations can then proceed; translating Delirium-derived code remains subject to Gate L/D. No NuGet registry publication, new public repository, force-push or license change is authorized.

## 1. Objective and starting point

Build an automatic humanoid physical-character setup and a substantial active-reaction controller: animated tracking, balance, corrective steps, impacts, bracing, controlled loss of balance, falling and recovery. Translate and adapt the selected **actual Delirium controller implementation** to unmanaged Unity ECS/Burst code, with **Unity Physics as the only dynamics/contact/joint solver**. This is a source-level controller port, not a native wrapper or a dependency on Delirium's solver.

Put new reusable math, inertialization and portable controller state in engine-independent .NET libraries with local NuGet output. Keep Unity baking, ECS scheduling, solver access and rendering adapters in UPM packages. Do not extract or rewrite the existing proven SA algebra merely to make this boundary.

Add finite-duration inertialization so transitions can begin from the current output and be interrupted without pair-specific transition clips. The destination can be one clip, a 1D blend, eight-direction locomotion, or a layered/masked blend. This removes the need for transition-pair assets; it does not synthesize missing locomotion or get-up skills, guarantee balance, remove the destination's sampling cost, or reproduce proprietary GTA IV behavior exactly.

Baseline, not work still to redo:

- Workspace: `/home/i/ruk2`. Unity `7000.0.0a12`; Entities, Entities Graphics and Unity Physics `6.7.0`.
- Existing package: `/home/i/ruk2/Packages/com.sa.unity`, version `0.3.0`, namespace `SA`; assemblies `SA.Runtime`, `SA.Editor`, `SA.Tests`.
- Repository: [IAFahim/com.sa.unity](https://github.com/IAFahim/com.sa.unity), private; recorded baseline commit `d6a447056b89425a3a2a34e31d3fe5c3efca7ba3`. Recheck HEAD when execution eventually starts; do not reset later work to this commit.
- The previous implementation run passed **35 regular tests**, including actual GPU parity, ECS serialization and shared-asset/system integration. This planning turn has not rerun them.
- Skeletons, clips and masks already share independently. Keep this design and its cache/provenance rules. See [Rig.cs](/home/i/ruk2/Packages/com.sa.unity/Runtime/Data/Rig.cs:12) and [sharing.md](/home/i/ruk2/Packages/com.sa.unity/Documentation~/sharing.md).
- Existing two-way/multi-point 1D, Cartesian and eight-way directional weighting stays supported. `BlendSpace.Linear`, `BlendSpace.Cartesian`, `BlendSpace.Directional` calculate weights; `Playback.Blend` plays the corresponding phase-synchronized clips. These are code-configured inputs, not an Animator graph. See [BlendSpace.cs](/home/i/ruk2/Packages/com.sa.unity/Runtime/Core/BlendSpace.cs:8) and [Playback.cs](/home/i/ruk2/Packages/com.sa.unity/Runtime/Core/Playback.cs:23).
- Existing CPU characters have bone entities; their current GPU skinning path reads those bones. Pure GPU characters have no bone entities and do not provide CPU pose readback.
- Historical v0.2 crowd timings missed the old 50k performance aspiration. They are not measurements of this proposed work. Do not repeat the old 2 ms CPU/2 ms GPU target as an achieved result.

The old v0.2 rebuild plan is preserved at `/tmp/sa-plan-before-reactions-20261030.md`. This document replaces it. Its v0.1 audit, monolithic clip-in-rig proposal, approximate 1,800-line total budget and obsolete out-of-scope list are superseded. Retain its useful constraints: small standalone animation core, immutable shared data, pure math modules, explicit ownership/order, idempotent intent, Burst-safe runtime, and measured claims.

## 2. Binding decisions and gates

### 2.1 Concrete .NET/Unity boundary, repositories and distribution

Use **two source repositories**, not one repository/package per helper:

1. Existing `/home/i/ruk2/Packages/com.sa.unity` remains the focused animation package and existing private repository. It owns current playback, clip sharing, blend algebra, rendering and the small new transition/clock integration. Do not relocate working 0.3 features into the portable library.
2. New canonical local repository **`/home/i/GitHub/sa-motion`** owns the new portable library solution and its thin Unity/physics adapters. The publisher/coordinator initializes this local repository before implementation writes there. A remote, if created under the user's repository workflow, is private by default; no new public repository or registry release. Keep `/home/i/ruk2` itself a non-git Unity workspace.

The second repository contains these meaningful boundaries:

| Project/package/assembly | Owns | Must not own |
|---|---|---|
| `src/SA.Motion` / `SA.Motion.dll`, local NuGet ID `SA.Motion` | Independently authored numeric POD types, scalar/vector/quaternion quintic kernels, reusable pose/history/kinematics data and elementary mechanics helpers | Unity/Entities/Burst references, solver access, source-translated Delirium algorithms or gated tuning |
| Future `src/SA.ReactionControl` / `SA.ReactionControl.dll` | Portable controller snapshots/state/intent algorithms, including approved selected Delirium source ports after Gate L/D | Unity physics objects, a second solver, implicit rights clearance; it is not a dependency of `SA.Motion` |
| `packages/com.sa.motion.unity` / `SA.Motion.Unity` | Exact portable DLL payload, Unity.Mathematics/POD conversions, Burst wrapper jobs/probes, artifact/version/hash verification | A hand-maintained second C# implementation, Entities/Physics behavior or an SA-specific graph |
| Existing `com.sa.unity` / `SA.Runtime`, `SA.Editor` | Existing animation plus small opt-in transition state/scheduling, GPU history/correction integration and calls through the bridge | New portable algorithm ownership, Unity Physics or Delirium controller dependency |
| `packages/com.sa.unity.reactions` / `SA.Reactions.Runtime`, `.Editor` | ECS target/proxy/render state, physics scheduling/query/impulse adapters, role mapping/baking, lifecycle and SA integration | A second rigid-body solver, native Delirium plugin, general-purpose numeric library |
| .NET tests and Unity bridge/reactions test assemblies | Cross-runtime/kernel tests and actual Burst/physics/render proof | Production references to private Unity test assemblies |

`SA.ReactionControl` is a separately gated source/license boundary within the same neutral solution, not an excuse to relicense translated code or a separate repository for every algorithm. Independent input/state types may be introduced without copying source; actual translated functions wait for the rights/data decision. If its intended distribution/combination cannot be cleared, do not reference it from the adapters or silently include it in the motion DLL. No additional NuGet/package is created merely for one small helper.

Authoritative layout:

```text
/home/i/GitHub/sa-motion/                 canonical repository; initially local/private workflow
  SA.Motion.sln
  src/SA.Motion/SA.Motion.csproj
  src/SA.Motion/Numerics/                own Float3, Quaternion, Pose, Matrix3x3 PODs
  src/SA.Motion/Inertialization/         new reusable kernels
  src/SA.ReactionControl/                later, gated controller project
  tests/SA.Motion.Tests/                .NET 10 tests
  tests/PackageSmoke/                    consumes the packed package, not a ProjectReference
  packages/com.sa.motion.unity/
    Runtime/Plugins/SA.Motion.dll        generated from the packed lib/netstandard2.1 artifact
    Runtime/                            thin Unity conversions/wrappers only
    Tests/                              actual Burst/managed/player probes
  packages/com.sa.unity.reactions/       Unity ECS/Physics/SA adapter
  eng/                                  reproducible build/pack/import verification scripts
  artifacts/                            ignored local packages/reports/staging
```

The Unity workspace consumes the canonical adapter directories through local UPM `file:` dependencies in `Packages/manifest.json`, for example `file:/home/i/GitHub/sa-motion/packages/com.sa.motion.unity`. Do not maintain an embedded copied source tree under `ruk2/Packages` alongside the canonical one. Release/private Git UPM locations can later use pinned package subpaths, and ordinary .NET users can consume NuGet independently. Repository operations remain publisher-owned; implementation owns the source roots above and minimal necessary edits inside the existing SA package. Workspace manifest/lock edits are integration configuration, not a reason to initialize git at the Unity root.

Select **`netstandard2.1`** for the portable DLL and C# 9-compatible source, with .NET 10 as the local standalone test/tool host. Do not ship a net10-only DLL into Unity or treat the generated IDE project's framework placeholder as deployment proof. [Unity 7000 compatibility documentation](https://docs.unity3d.com/7000.0/Documentation/Manual/dotnet-profile-support.html) supports .NET Standard managed plug-ins; [.NET's compatibility table](https://learn.microsoft.com/en-us/dotnet/standard/net-standard) includes .NET 10 consumers of .NET Standard 2.1. Neither fact proves Burst compatibility.

Default distribution is one release DLL: build/pack once, extract `lib/netstandard2.1/SA.Motion.dll`, record SHA256/version, and import those exact bytes into the UPM bridge. Local NuGet tests, managed Unity, Burst Unity and the player must test that same artifact. Keep build outputs out of source except the intentionally packaged, pinned DLL/meta payload. The bridge contains the license/provenance/artifact manifest and does not require users to install NuGet tooling inside Unity.

Use own small sequential unmanaged numeric types and static scalar/ref/in/out functions. Put conversions at adapter boundaries, not managed interface/virtual/generic-math dispatch inside bone/joint loops. Caller-owned spans/workspaces or an internal pointer+count batch path are acceptable; spans are not job fields or managed function-pointer boundaries. No managed storage/delegates, normal-flow exceptions, reflection, runtime-generated code, hidden allocations or assumed .NET SIMD/intrinsic behavior. Start with conservative scalar `System.Math` paths and explicit float casts; `MathF`/`System.Numerics` are not assumed to become Burst SIMD. Record actual support/cost in Gate R.

If the DLL route fails the actual Burst/player gate, the documented fallback is a generated source bridge from the **same pinned canonical sources**, with only one loaded definition of each type. Do not hand-copy algorithms into SA or compile source and load the same DLL simultaneously. A failed route must be diagnosed and measured before selecting the fallback.

This boundary is about reuse, runtime compatibility and ownership. Repository/project/assembly/package names and framework targets do **not** establish legal clearance.

### 2.2 Gate L: source rights, notices and data provenance — unresolved

The chosen route is a source-level port. Before translating Delirium-derived controller code, record a concrete licensing/provenance decision for the intended Unity distribution, approved by the project owner with qualified advice where needed. Do not translate that code first and decide how to license it later. This gate does not block independently authored math from the published inertialization specification, neutral unmanaged data, or a minimal independently derived physics/PD viability fixture; provenance of that foundational work must be recorded separately and it must not be presented as a completed Delirium port.

Evidence:

- Delirium declares `GPL-3.0-only` in [Cargo.toml](/home/i/GitHub/delirium-physics/Cargo.toml:8) and ships [LICENSE](/home/i/GitHub/delirium-physics/LICENSE).
- Its own [README](/home/i/GitHub/delirium-physics/README.md:22) describes a reverse-engineered Rust rewrite from GTA IV program code. This is a provenance claim by that project, not a finding that all rights are cleared.
- The [GNU FAQ on language translation](https://www.gnu.org/licenses/gpl-faq.en.html#TranslateCode) treats translation as modification; [its combination/aggregation discussion](https://www.gnu.org/licenses/gpl-faq.en.html#MereAggregation) does not establish isolation merely from separate modules.

Do not label copied/translated Delirium code MIT because its names, language, storage layout or math library changed. Keep the independently maintained animation package's current license intact. Any derived companion's license/notices and compatibility with its Unity/runtime combination need an explicit resolved record; this plan does not settle that legal question. Do not strip required notices to satisfy a no-comments style rule. If legal notices require source headers, define a narrow source-test exception after this gate; ordinary comments remain disallowed.

Required future decision record: `Documentation~/provenance.md` in the companion, with source repository/revision, original file/function, owner/license claim, port/adaptation description, required notices, intended distribution scope, and approval reference. Record uncertainties rather than fabricating permission. If the direct-port route is not approved, stop that route and report the blocked capability while continuing already authorized independent foundations; do not silently replace it with a purported clean-room controller or a native wrapper.

### 2.3 Gate D: standalone tuning and assets — unresolved inputs, explicit replacement strategy

Delirium is not a complete open numeric preset to copy. Its [tuning bank](/home/i/GitHub/delirium-physics/crates/delirium-data/src/kv.rs:1) contains 1,001 gated slots; pre-unlock values are zero. [Unlocking](/home/i/GitHub/delirium-physics/crates/delirium-data/src/kv.rs:34), [asset loading](/home/i/GitHub/delirium-physics/crates/delirium/src/nmrs.rs:23), and [agent creation](/home/i/GitHub/delirium-physics/crates/delirium-sdk/src/lib.rs:362) require game-derived inputs. Many `Params::default` methods still depend on those slots.

The standalone SA design uses independently authored, documented data instead of that bank:

| Input | Required standalone replacement | Acceptance evidence |
|---|---|---|
| `kv!` gains, thresholds, flags, epsilons, filter rates | Named typed profile fields and independently selected numerical tolerances; map every referenced slot's role before removal | No unresolved numeric-slot reads/default-zero bank; finite/unit/range tests; tuning log |
| GTA joint axes, flips, limits and part transforms | Calibrated frames from the mapped rig; independently authored humanoid limit/profile data with overrides | Nonidentity-rest and mirrored/oriented-rig tests |
| GTA masses, inertias and shapes | Mesh/rest-geometry fitting plus declared total mass/density policy; Unity collider-derived mass properties | Positive finite masses/inertias and total-mass checks |
| GTA self-collision table | Authored adjacency/nonadjacency rules on semantic anatomy | Same-character and two-character collision tests |
| Extracted character templates/build dumps | SA-owned generator/profile assets and the input character's authorized skeleton/mesh | Reproducible bake report, source/license per asset |
| Get-up/locomotion/bracing animation content | Project-owned or suitably licensed clips; procedural steps/IK where specified below | Clip origin, start pose/contact metadata and capability validation |
| Experimental learned assets | None in initial implementation | No hidden model/training/network or LAFAN-derived asset dependency |

Do not decode/copy `kv.bin`, package extracted game tuning, or bypass the SDK unlock gate. A player's copy of a game is not, by itself, a distribution authorization. The runtime must not need GTA files. The code port preserves selected controller structures and algorithms while adapting their solver interface and tuning; literal game parity is not an acceptance criterion.

### 2.4 Other gates

| Gate | Must establish before proceeding | Failure behavior |
|---|---|---|
| R | Packed netstandard2.1 DLL works on .NET 10, actual native Burst and the declared player backend, with no allocation/type duplication | Diagnose and use the reviewed same-source bridge fallback if necessary; a NuGet pack alone is not success |
| L/D | Approved source/data route and complete minimum independent profile | Stop the affected source-port/profile work, not independent math/runtime/physics foundation work; enumerate missing decisions/data |
| M | Two-body quaternion drive works with real Unity Physics, nonidentity frames, contacts and bounded impulses | Fix frames/scheduling first; do not hide failure with transform teleports or huge gains |
| B | Generator and animation/physics/render ownership work on one humanoid and one explicitly mapped generic humanoid | Reject unsupported rigs with diagnostics; no silent bad ragdolls |
| I | Original quintic CPU math, interruptions and GPU state lifetime satisfy their actual contracts | Keep existing CrossFade usable; do not call a lerp or spring implementation the specified algorithm |
| C | Active support/steps/bracing/recovery pass declared fixtures | Mark capability incomplete; a passive ragdoll plus tracking is not completion |
| P | Measured cost/memory supports a declared deployment tier | Reduce optional detail/rate within validated rules or revise targets; no invented scale claim |

## 3. Source-level port inventory

Pin the exact Delirium source revision at execution start. The inspected checkout is `/home/i/GitHub/delirium-physics`. Applicable AGENTS files must be checked then. The source map below is an implementation inventory, not permission to copy before Gate L.

| Source and relevant entry | Port into SA | Replace or exclude | Required equivalence/integration check |
|---|---|---|---|
| [nm.rs active input](/home/i/GitHub/delirium-physics/crates/delirium/src/nm.rs:490) | Convert incoming animation poses into calibrated active targets | Incoming-pose branch that directly overwrites native bodies | Targets change without teleporting dynamic proxies |
| [nmrs.rs effectors](/home/i/GitHub/delirium-physics/crates/delirium/src/nmrs.rs:406), [drive conversion](/home/i/GitHub/delirium-physics/crates/delirium/src/nmrs.rs:681) | Gain/strength/soft-limit intent and role-based target representation | Native `Character`, asset-dependent joint indices/rows and engine-owned fields | Synthetic target/gain inputs produce finite bounded drive intents |
| [ragdoll.rs muscles](/home/i/GitHub/delirium-physics/crates/delirium/src/ragdoll.rs:1589) | Shortest-arc orientation tracking, damping/strength logic and limiting intent | Native articulated effective-response solver, constraint rows and engine inertia mutation | Gate M; document pair-inertia approximation versus source's full articulated response |
| [ragdoll.rs soft limits](/home/i/GitHub/delirium-physics/crates/delirium/src/ragdoll.rs:1649) | Only identified controller soft-limit/gain policy | Duplicate hard-limit/contact integration | Unity limit solver remains authoritative; no double constraint explosion |
| [nmdbm.rs state](/home/i/GitHub/delirium-physics/crates/delirium/src/nmdbm.rs:100), [Config/Dbm](/home/i/GitHub/delirium-physics/crates/delirium/src/nmdbm.rs:1018) | Support, COM, feet, pelvis and balance state as small unmanaged data | Unknown opaque fields until their meaning and units are established | Field/function map and deterministic synthetic-input tests |
| [foot_placement](/home/i/GitHub/delirium-physics/crates/delirium/src/nmdbm.rs:400), [pelvis_control](/home/i/GitHub/delirium-physics/crates/delirium/src/nmdbm.rs:509), [balance_solve](/home/i/GitHub/delirium-physics/crates/delirium/src/nmdbm.rs:715) | Prediction, support transition, corrective step and leg/spine target algorithm | Fixed source-role indices and unexamined `kv!` values | Push-direction fixtures, stepping hysteresis and reach/landing constraints |
| [Body adapter](/home/i/GitHub/delirium-physics/crates/delirium/src/nmdbm.rs:843) | An explicit body snapshot interface | Embedded native `Ragdoll`, native joint/link/rows and solver access | Snapshot from current Unity world matches analytic COM/velocity fixtures |
| [holding/probe/tick](/home/i/GitHub/delirium-physics/crates/delirium/src/nmdbm.rs:1200), [apply](/home/i/GitHub/delirium-physics/crates/delirium/src/nmdbm.rs:1288) | Controller sequence, moving-ground support, recovery targets and feedback | Native `Collision`/`Obstacle`, environment reads, debug file paths | Current-world probes and bounded effectors; failed support triggers fall policy |
| [nmbb.rs](/home/i/GitHub/delirium-physics/crates/delirium/src/nmbb.rs:137) | Loss-of-balance/catch-fall decisions and upper-body/head response | Native ownership/tuning dependencies | Small recoverable push versus unrecoverable fall are different outcomes |
| [nmcf.rs](/home/i/GitHub/delirium-physics/crates/delirium/src/nmcf.rs:283), [nmbr.rs](/home/i/GitHub/delirium-physics/crates/delirium/src/nmbr.rs:200) | Contact-aware bracing probes, arm IK, state/hysteresis and target shaping | World collision wrapper and unbounded body-placement shortcuts | Wall/ground approach: reachable brace, safe miss, contact release |
| [behavior coordination](/home/i/GitHub/delirium-physics/crates/delirium/src/nm.rs:1295), [locomotion drive](/home/i/GitHub/delirium-physics/crates/delirium/src/nm/locomotion_drive.rs:45) | Priority/arbitration and stance/gain intent needed by selected behaviors | Native gravity/stabilization side effects, full string message framework and unrelated behavior catalog | One bounded drive result per joint and deterministic behavior ownership |
| [room getup.rs](/home/i/GitHub/delirium-physics/crates/delirium-room/src/getup.rs:422), [ragdoll selection](/home/i/GitHub/delirium-physics/crates/delirium/src/ragdoll.rs:3076) | Supine/prone selection and root-alignment concepts | Existing plain pose lerp/slerp; game animation assets | Owned get-up clips and actual physical-output inertialization |
| [Build::fit](/home/i/GitHub/delirium-physics/crates/delirium/src/ragdoll.rs:200), [SDK fit](/home/i/GitHub/delirium-physics/crates/delirium-sdk/src/lib.rs:315) | Identified geometric fitting ideas after provenance clearance | Fixed 21-part input contract as a universal rig inference mechanism | SA's own mapped-rig generation and validation |
| Native `artic`, `rigid`, collision/contact/integration/solver modules | Read only to understand controller assumptions | Do not port them as a second dynamics engine | Unity bodies/joints/contact solver is the single physical authority |

Before translating a selected function, write its port record: input/output fields, coordinate basis, units, timestep behavior, required source dependencies, all referenced tuning slots, native-solver calls, tests, and planned SA owner. Trace opaque fields such as unnamed `p...` values; do not assign guessed semantics from names alone. Convert source `Vec`/heap/string/optional object graphs to blobs, fixed small fields and native buffers. Preserve the identified control flow, then separately document adaptations.

Source fitting is not arbitrary anatomy recognition: [character parsing](/home/i/GitHub/delirium-physics/crates/delirium-sdk/src/character.rs:116) requires 21 parts/20 joints, and [the source role list](/home/i/GitHub/delirium-physics/crates/delirium-data/src/nm_asset.rs:367) includes four spine segments, neck and clavicles. The SA controller uses semantic role/chain maps instead of those numeric indices. A conventional humanoid with fewer spine bones requires an explicit tested chain adapter; do not pretend Avatar mapping alone makes source assumptions disappear.

## 4. Concrete architecture, files and data contracts

### 4.1 Planned file map

Names below are intended owners, not a file/package for every tiny helper. Paths under `sa-motion` are in the independent canonical repository from §2.1.

| Location | Responsibility |
|---|---|
| `sa-motion/src/SA.Motion/Numerics/*` | Engine-free sequential PODs and minimal new vector/quaternion/matrix math needed by the kernels; no migration of existing SA math |
| `sa-motion/src/SA.Motion/Inertialization/*` | Original scalar quintic construction/evaluation, radial/rotation correction, numerical safeguards and caller-owned state |
| `sa-motion/src/SA.ReactionControl/*` | Portable observation/role/state/intent and selected approved controller ports; translated source remains gated |
| `sa-motion/packages/com.sa.motion.unity/Runtime/*` | Pinned DLL payload and thin Unity.Mathematics/POD conversion/static wrapper layer |
| `com.sa.unity/Runtime/Data/Transitions.cs` | SA-specific `TransitionIntent`, stable history ownership and generic `FixedPoseClock`; references portable state where appropriate |
| `com.sa.unity/Runtime/Core/Playback.cs` | Small opt-in transition overloads and idempotent revision; existing APIs retained |
| `com.sa.unity/Runtime/Systems/AnimationSystems.cs` | Normal/fixed-clock exclusion and CPU correction/write ordering; no new reusable math duplicated here |
| `com.sa.unity/Runtime/Gpu/*`, `Resources/SA/Animation.compute`, `Shaders/*` | GPU state/storage and HLSL implementation of the same specified math, with cross-runtime parity; no claim that a managed DLL executes on GPU |
| `sa-motion/packages/com.sa.unity.reactions/Runtime/Data/*` | ECS/blob/entity maps, engine-facing requests, contacts and pose ownership |
| `.../Runtime/Adapters/*` | Body/COM/joint frame conversion and packed snapshot/intent mapping to portable kernels; solver mutations remain Unity-specific |
| `.../Runtime/Systems/*` | Before-build lifecycle/targets, after-build control/refit, after-export observation, render mapping |
| `.../Editor/HumanoidMapping.cs`, `RagdollBuilder.cs`, `RagdollBaker.cs` | Semantic mapping, shapes/masses/joints, owned baking and validation |
| `.../Runtime/Authoring/RagdollAuthoring.cs` | User mapping/profile/override references; own filename |
| `.../Samples~/Reactions` | Owned humanoids/clips, independently constructed two-body and behavior fixtures |
| `sa-motion/tests`, adapter tests, existing SA tests/docs | Portable math/package consumption, real Burst/player probes, regressions, provenance and measured costs |

Portable controller functions read caller-provided snapshots and emit caller-owned intents; they do not retain Unity entities or call solver/query interfaces. Typed snapshots and batch arrays permit an engine-independent test harness without interface/virtual dispatch in the hot path. The Unity adapter packs/unpacks at a deliberate boundary; measure conversion/data movement rather than claiming it is automatically free.

Keep the runtime domain readable and field-oriented. No generic visual graph, string behavior bus, reflection into Unity internals, managed per-character controllers, or animation state-machine framework. A finite reaction-mode enum and a few behavior-stage fields are needed control state. Keep ordinary code comment-free as current source tests require; explain math/provenance in documents and precise names. Required legal notices are governed by Gate L.

### 4.2 Immutable shared data

| Data | Fields/invariants | Sharing key |
|---|---|---|
| Existing `RigBlob`, `Clip`, `BoneMask` | Keep 0.3 layouts and independent assets unless an explicit migration is required | Existing skeleton/clip/mask keys; adding physical capability must not duplicate clips |
| `RagdollDefinition` blob | Skeleton ID; semantic role→bone/body map; body parent/order; `BoneFromBody` transforms; shape descriptions; mass fractions; calibrated joint frames/limits; bind/display mappings; collision exclusions; recovery anchors | Skeleton/rest ID + mapping/geometry/profile versions + mesh dependencies/fit settings + uniform scale + overrides |
| `ReactionProfile` blob | Named drive gains/caps, contact/support/step/brace/recovery thresholds, tolerances, gravity-relative policies, fixed role-chain adapter and provenance/version | Independently authored profile content/version; no opaque game tuning indices |
| Collider blobs | Immutable Unity Physics colliders/materials from generated geometry | Geometry/material/filter content and generator version; registered with correct baking ownership |
| Recovery metadata blob | Definition-compatible prone/supine clip IDs or name hashes resolved to local tables; start anchors/facing, support/contact markers, safe handover interval | Owned source clips/dependencies + skeleton + authored metadata; no transition-pair asset |

External `BlobAssetReference` values live in ECS components/buffers, not as unmanaged external pointers hidden inside another blob. Definition blobs can contain scalar/hash/array data; link profile/collider/recovery references through tracked components or buffers. Read blob contents by `ref`. Do not mutate shared colliders/masses/profiles per character.

### 4.3 Per-character and per-body state

| Owner | Proposed data | Purpose |
|---|---|---|
| Rig root | Existing `Rig`, `ClipAsset`, `MaskAsset`, `ClipState`, `TimeScale`, `BoneEntity`; CPU-capable authoring | Shared source data and the single animation clock/state |
| Rig root | `RagdollRig` (definition/profile references), `RagdollBodies`/`RagdollJoints` entity buffers, `FixedPoseClock` | Stable topology/capability; no packed physics-body indices persisted across builds |
| Rig root | `ReactionIntent`, `ReactionState`, small `BalanceState`, `StepState`, `BraceState`, `RecoveryState` | Desired mode, current stage, timers/hysteresis, deterministic RNG state when needed |
| Rig root | CPU `TargetPose`/kinematics and constrained-target workspace | Animation output, never the dynamic body transforms |
| Rig root | Previous/current solved snapshot, root/frame timestamps and visual mapping workspace | Physical motion interpolation and transfer to render bones |
| Rig root | Bounded `HitRequest`/`ReactionEvent` buffers | One-shot inputs/events with explicit sequence IDs and overflow policy |
| Physics proxy entity | `PhysicsCollider`, `PhysicsMass`, `PhysicsVelocity`, world-space `LocalTransform`, `PhysicsWorldIndex`, damping/gravity/material settings, owner/body index, lifecycle exclusion | Unparented dynamic body owned only by physics while active |
| Joint entity/entities | `PhysicsJoint`, `PhysicsConstrainedBodyPair`, `PhysicsWorldIndex`, owner/joint map and required companion links | Limits/contact exclusions; two limit joints where the factory requires them |
| Render bone | Existing skeletal `Parent`, `LocalTransform`, optional `PostTransformMatrix`, `PoseDriven`; explicit physical/external ownership marker | Rendering hierarchy; never also the dynamic proxy entity |
| Inertialization-capable character | Stable history slot+generation, request revision, elapsed time; per-bone corrected local history and active offsets | CPU/GPU transition continuity without outgoing-clip evaluation |

Keep mutable pose/state separate from shared source data. Preallocate buffers/archetypes at bake/pool creation. Contacts and temporary per-body impulse accumulators use bounded native workspaces. Events from parallel jobs need streams/reductions, not concurrent writes into a character's ordinary buffer. Define overflow counters and deterministic top-priority retention; never silently drop a fatal impact or let buffer growth allocate indefinitely.

## 5. Automatic humanoid construction

### 5.1 Supported anatomy and mapping

Initial scope is a human-like biped. Use a valid humanoid `Avatar` mapping when available; otherwise require an explicit generic humanoid map. No bone-name guessing as the only correctness mechanism and no quadruped/arbitrary-anatomy claim.

Choose a **15-body base profile**: pelvis, torso, head; and bilateral upper arm, forearm, hand, thigh, shin, foot. The mapped torso can correspond to a declared spine chain. All source controller references to several spine/clavicle segments must be adapted explicitly to the selected role chains and tested. Optional extra physical spine/clavicle bodies are a later detail tier using the same semantic map, not a prerequisite for the first supported humanoid. Missing required legs, head, torso, hands or valid parent chains is a bake error for the full behavior profile. A reduced capability must be explicit and may not report bracing/get-up support it lacks.

The mapping report names every role, source skeleton path, physical body, chain, axis and unmapped optional bone. Require an author-confirmable rest/T/A pose orientation and a forward/up definition. `RigAuthoring.ExtraBones` must include required control/attachment transforms outside SA's stable skin/Avatar skeleton; never let the reaction clip list silently change the skeleton. See [RigAuthoring.cs](/home/i/ruk2/Packages/com.sa.unity/Runtime/Authoring/RigAuthoring.cs:11).

### 5.2 Geometry, mass, limits and frames

1. Validate finite transforms, parent acyclicity, nonzero segment lengths, consistent scale, positive declared total mass, mapped chain order, and unique ownership. Reject unsupported negative/nonuniform root scale for dynamic physics initially. Bake geometry at its supported uniform scale; runtime rescaling requires rebake/reinitialization.
2. Fit limb capsules along mapped joint segments. Prefer weighted skinned vertices in editor space for transverse extent when reliable; otherwise use explicitly authored role/segment-length ratios. Use spheres/capsules for head/hands and boxes/capsules for torso/feet as declared by the independent profile. Endpoint padding, minimum radius/length, fitting quantiles and shape choices are named versioned inputs, not copied source constants.
3. Align shape/body frames and joint anchors from the rest pose. Store exact `BoneFromBody`; do not assume the bone forward axis equals the capsule axis or that the pivot equals COM.
4. Initial mass distribution is declared total mass times normalized shape-volume×role-density weights, with overrides. This is an independently authored starting heuristic, not a claim of measured human biomechanics. Compute each inertia using `PhysicsMass.CreateDynamic(collider.MassProperties, mass)`; do not fill inverse-inertia vectors by hand.
5. Use independently authored anatomical limits expressed in calibrated joint coordinates. Infer axes from joint directions plus a reliable bend/roll reference, not collinear limb vectors alone. Knees/elbows need a validated preferred bend direction. Provide per-joint overrides and visual diagnostics.
6. Generate the complementary limit joints from Unity's supported ragdoll factory or a documented composed constraint layout. Store the complete pair/companion relation. Limits must include the rest pose and the supported target envelope.
7. Adjacent-body contact is disabled by the constrained pair; selected nonadjacent self-contact stays enabled. Inter-character and environment contacts remain enabled. Do not assign every ragdoll the same negative collision group.
8. Preview colliders, anchors, axes, limits, COM, mass and role chains before runtime. Invalid authoring fails with object/bone names and actionable diagnostics; no silent fallback to a distorted humanoid.

Relevant installed sources: [mass construction](/home/i/ruk2/Library/PackageCache/com.unity.physics@5333869c01db/Unity.Physics/ECS/Base/Components/PhysicsComponents.cs:146), [ragdoll factory](/home/i/ruk2/Library/PackageCache/com.unity.physics@5333869c01db/Unity.Physics/ECS/Base/Components/PhysicsJointComponents.cs:834), [constrained pair](/home/i/ruk2/Library/PackageCache/com.unity.physics@5333869c01db/Unity.Physics/ECS/Base/Components/PhysicsJointComponents.cs:16).

### 5.3 Baking and ownership

Use an SA-owned `Baker<RagdollAuthoring>` and owned additional proxy/joint entities with public physics components and collider blobs. Do not call private `RigidbodyBaker`, private `BakingUtility`, or assume `JointBakingSystem.CreateJointEntities` is a public utility. Compile the minimal baker early. Use temporary baking records plus a baking system when writing components owned by other bakers; preserve the current SA ownership pattern.

Register dependencies before cache lookup, including skeleton/rest transforms, Avatar, source meshes, physical profile, overrides, recovery clips and metadata. Reference the existing animation blobs; do not resample or bundle them into the ragdoll definition. Pool/prefab instantiation must remap proxy/joint/bone entity references through the linked prefab entities correctly, while shared blobs/colliders remain shared. Verify actual subscene serialization/reload and two instances, not merely equal hashes.

## 6. Spaces, units and mathematical conventions

Use meters, kilograms, seconds, radians, rad/s, N, N·m and impulses N·s/N·m·s. Unity `float3` coordinates use +Y up for default fixtures and +Z forward. Control uses `up = -normalize(gravity)` when gravity is nonzero; zero gravity needs an explicit fixed-up/no-ground policy. Convert source axes once at the port boundary; its Z-up conventions and row/order-specific expressions must not leak into arbitrary SA code. Before translating formulas, record the actual source up/forward/handedness and length/mass/time units, with analytic fixtures proving the conversion. For an orthogonal basis conversion `C`, polar vectors use `C*v`, while axial angular velocity/torque use `det(C)*C*v`, plus their declared unit factors. A reflection is not an ordinary component shuffle for every quantity. Convert rotations with `RUnity = C*RSource*inverse(C)` and then construct a normalized quaternion; transform inertia tensors consistently. Do not guess quaternion component permutations or inherit numeric gains before this basis/unit gate passes.

Notation: `AFromB` maps coordinates in B to A. Composition is left-to-right matrix/quaternion multiplication as implemented by `math.mul(AFromB, BFromC)`. Quaternions are normalized; error log/axis-angle vectors contain the **full angle**, not a quaternion half-angle. Calibrate and test multiplication side explicitly.

For body B bound to bone K, with `BoneFromBody = KFromB`:

```text
WorldFromBodyTarget = WorldFromBoneTarget * BoneFromBody
WorldFromBoneSolved = WorldFromBodySolved * inverse(BoneFromBody)
ParentFromBoneSolved = inverse(WorldFromParentResolved) * WorldFromBoneSolved
```

Use the actual resolved parent pose, including unsimulated intermediate bones, rather than a baked parent or raw animation parent. For an unbound render bone, retain its target-local transform under its resolved parent; this preserves twist/detail bones without adding a rigid body for each.

Dynamic proxies have world-space `LocalTransform` and **no skeletal Parent**. Render bones keep their hierarchy. SA's current CPU system writes hierarchy-local bone transforms, which is why those entities cannot also serve as dynamic proxies. See [existing writer](/home/i/ruk2/Packages/com.sa.unity/Runtime/Systems/AnimationSystems.cs:55), [dynamic-body documentation](https://docs.unity3d.com/Packages/com.unity.physics@6.7/manual/concepts-data.html#dynamic-bodies), and [exporter](/home/i/ruk2/Library/PackageCache/com.unity.physics@5333869c01db/Unity.Physics/ECS/Base/Systems/PhysicsWorldExporter.cs:155).

`PhysicsVelocity.Angular` is in the inertia frame. Use the installed world-space angular conversion/impulse APIs, not direct assignment of world angular velocity into that field. A body's mass/inertia frame includes its `PhysicsMass` transform and COM offset. Point velocity is COM linear velocity plus angular velocity crossed with the COM-to-point vector. Source gain units and torque/impulse units must be re-established; do not copy dimensionless-looking constants without their dimensional context. See [angular field](/home/i/ruk2/Library/PackageCache/com.unity.physics@5333869c01db/Unity.Physics/ECS/Base/Components/PhysicsComponents.cs:194), [component angular conversion](/home/i/ruk2/Library/PackageCache/com.unity.physics@5333869c01db/Unity.Physics/Extensions/PhysicsComponentExtensions.cs:225), and [world impulse conversion](/home/i/ruk2/Library/PackageCache/com.unity.physics@5333869c01db/Unity.Physics/Extensions/PhysicsWorldExtensions.cs:263).

Animation can retain its existing scale support. Dynamic physical characters initially require rigid/uniform-scale body transforms; do not try to make a rigid body follow animated bone scale. Preserve/rest-scale render details through the target/render mapping, with validated limits on deformation.

## 7. One clock, one physical authority, explicit scheduling

### 7.1 Clock and transform ownership

Physics-capable characters use a fixed animation clock throughout their animated/active/passive/recovery lifecycle. A generic `FixedPoseClock` tag excludes them from the normal `PlaybackSystem` **and** the normal CPU evaluate/write path. The companion advances `Playback.Step` exactly once per physics tick and evaluates targets through the same pure core. Merely adding a fixed target sampler while leaving normal playback running would double-advance time.

`TimeScale` and clip `Speed` affect animation playback/phase, not the physical timestep, gravity, contact solve or controller integration. Keep existing animation fade semantics covered by compatibility tests. Target history timestamps, target velocity derivatives and inertialization elapsed time use the actual owning clock's seconds; physical target histories therefore use unscaled fixed timestamps even when playback is slow, paused or reversed. With `TimeScale = 0`, the clip target stops advancing while active bodies remain physical and can still be hit or fall. Paused/slow/reverse targets must be tested without substituting scaled playback delta for physical `h`.

Normal CPU/GPU animation-only characters retain their existing simulation-time clock. Explicitly order the ordinary animation group after the fixed-step group and before `TransformSystemGroup` where needed; order attributes must refer to systems in compatible groups. Do not rely on current incidental creation order.

| State/stage | Animation target | Dynamic proxy transforms | Render bone transforms |
|---|---|---|---|
| Normal animation-only | Core evaluation | Absent | Existing CPU/GPU output |
| Physics-capable, animated mode | Fixed CPU target evaluation | Excluded from simulation, seeded only at lifecycle boundary | Companion writes/interpolates target pose; no competing normal CPU writer |
| Active reaction/passive fall/recovery drive | Fixed CPU target/behavior intent | Unity Physics only | Companion maps solved/interpolated bodies; unbound bones retain target details |
| Final visual constraints | No feedback into physical state by default | Never overwritten | Optional downstream contact/IK owns declared bones only |

Enable `CpuEngine` and bake bone entities for reaction-capable characters. They may still use SA's current GPU skinning upload/render path. A pure GPU character cannot instantly gain a CPU physical skeleton just by enabling a component. Runtime conversion of arbitrary GPU-only crowd entities into physical heroes is outside the first release; use prebuilt CPU-capable pooled characters for the active cohort. No per-frame GPU readback.

`PoseDriven` remains the animation-write permission. On physical takeover, disable it for directly physical bones and mark explicit physical ownership. External overrides have their own declared ownership. On recovery/handover, transfer ownership once; never leave two writers active. The companion render mapper respects external ownership and does not reinterpret every disabled `PoseDriven` bone as a physics bone.

### 7.2 Fixed tick pipeline — selected correctness-first implementation

| Order and Unity location | Work | Authoritative data and dependencies |
|---|---|---|
| 1. `BeforePhysicsSystemGroup` | Consume deduplicated intent/hits; execute bounded lifecycle changes; advance fixed animation once; evaluate/inertialize target and target kinematics; prepare target constraints | ECS inputs and previous output histories; no dynamic-body animation writes |
| 2. Still before build | Initialize newly activated proxies from current output pose/velocities; apply queued external hit impulses; set physical mode/mass/limits/exclusion before snapshot | ECS physics components; new velocity is visible to broadphase prediction |
| 3. `PhysicsInitializeGroup` | Unity builds bodies/joints/world from ECS | Unity owns snapshot construction |
| 4. Companion control group inside `PhysicsSystemGroup`, after initialize and before simulation | Resolve body entity→world indices; gather current world body snapshot; cast fresh ground/brace probes; run controller ports; accumulate bounded drives; apply to **PhysicsWorld** motion velocities | `GetSingletonRW<PhysicsWorldSingleton>` and correctly chained jobs; do not write component-only velocities/joints after snapshot |
| 5. Same post-build interval | Refit dynamic broadphase using public `CollisionWorld.ScheduleUpdateDynamicTree` after post-build drive impulses | Include new motion velocities in predicted AABBs; synchronize dependencies before body-pair generation |
| 6. `PhysicsSimulationGroup`, then `ExportPhysicsWorld` | Unity handles body pairs, contacts, limits, solve and integration; exports solved state | Single dynamics solver |
| 7. `AfterPhysicsSystemGroup` | Read solved ECS body transforms/velocities; consume current collision events; record contact/support/impact state and solved snapshots | After export; no assumption that unsynchronized `CollisionWorld.Bodies` already contains the final transforms |
| 8. Once per rendered update in `SA.AnimationSystemGroup`, after `CpuPoseSystem` and explicitly before `PoseConstraintSystemGroup` | Interpolate solved snapshots in a consistent root frame and write mapped root/bone locals; existing declared final visual constraints run afterward | Fixed-clock rigs are excluded from the normal CPU writer; no playback/physics advance; transform propagation then supplies bone worlds |
| 9. After `TransformSystemGroup`, before presentation skinning | Capture timestamped resolved output/world kinematics when needed for later physical activation | Separate post-constraint capture; never automatically feed final visual constraints back into the core inertializer |

Initial physics settings: `SubstepCount = 1`, `IncrementalDynamicBroadphase = false`; fixed tick 1/60 s. Record solver iterations and gravity explicitly. The post-build refit is part of the initial correctness contract, not a free optimization. Large post-build velocity changes otherwise invalidate old predicted bounds. It does not supply full continuous collision detection. Profile its cost; a later pre-build controller redesign may remove it only with equivalent collision/latency tests.

Do not change entity topology between world build and export. If adding custom joint-motor constraints, update ECS joints **before** build, or deliberately modify the built joint data at the documented stage; changing a `PhysicsJoint` component after the snapshot does not update this step's solver.

References: [group order and after-export group](/home/i/ruk2/Library/PackageCache/com.unity.physics@5333869c01db/Unity.Physics/ECS/Base/Systems/PhysicsSystemGroups.cs:152), [world velocity/joint snapshot](/home/i/ruk2/Library/PackageCache/com.unity.physics@5333869c01db/Unity.Physics/ECS/Base/Systems/PhysicsWorldBuilder.cs:838), [dynamic-tree update](/home/i/ruk2/Library/PackageCache/com.unity.physics@5333869c01db/Unity.Physics/Collision/World/CollisionWorld.cs:749), [simulation modification](https://docs.unity3d.com/Packages/com.unity.physics@6.7/manual/simulation-modification.html).

### 7.3 Contacts, moving supports and rendering

- Contact state consumed by control is from the last completed solve, supplemented by fresh pre-simulation probes. Label timestamps. Predict support motion from its point velocity and retain support entity/local anchor, not just an old world foot point.
- Opt physical materials into collision events when needed. Consume events only in their documented lifetime; do not retain raw event-stream pointers across a subsequent simulation. Filter by owner/role; ground probes must reject all of the querying character's proxies while still detecting other characters when appropriate.
- After export, get solved transforms from ECS. End-step collision-world queries require explicit synchronization if used; the default does not guarantee refreshed world body transforms. Recovery-clearance queries can run in the next fresh pre-simulation controller stage instead of enabling global end-step sync indiscriminately.
- Interpolate previous/current physical snapshots for graphics using their real fixed timestamps. On multiple fixed catch-up ticks, preserve the correct pair; on zero ticks, do not step animation again. Teleport/pool reset invalidates interpolation and histories.
- Root follows the solved pelvis/support frame as a presentation anchor during physical ownership. Changing this root must not move proxies that are already in world space. Re-express bone and history data consistently when the root frame changes.
- Concretely, `ReactionRenderPoseSystem` is a child of `SA.AnimationSystemGroup` with ordering after `CpuPoseSystem` and before its existing child `PoseConstraintSystemGroup`. Those ordering targets are peers inside the same group. Existing head-look/declared visual writes must run after mapping so they are not overwritten. A separate post-`TransformSystemGroup` capture records final resolved output if needed for activation; core transition history remains the pre-constraint corrected animation output, and solved physical snapshots remain fixed-tick observations.
- Standard transform propagation runs after mapped bone locals, then [SA skinning](/home/i/ruk2/Packages/com.sa.unity/Runtime/Gpu/SkinningSystem.cs:57) reads the resulting bone worlds. Bounds need the physical envelope; do not leave a falling body culled at its old animated-root bounds.

## 8. Drive solver adapter and the two-body gate

### 8.1 Selected drive path

Use Unity's ragdoll joints for anchor/limit constraints and a **bounded quaternion-PD angular-impulse controller** as the initial active drive. This matches the port's torque/effector boundary without requiring a new native solver. Do not stack three factory hinge motors to imitate a 3DOF shoulder. The factory motor helpers include extra hinge/anchor constraints; they are not independent unconstrained axes. Unity's legacy `ConfigurableJoint` Slerp drive is not the ECS implementation.

See [rotational factory](/home/i/ruk2/Library/PackageCache/com.unity.physics@5333869c01db/Unity.Physics/ECS/Base/Components/PhysicsJointComponents.cs:564), [angular velocity factory](/home/i/ruk2/Library/PackageCache/com.unity.physics@5333869c01db/Unity.Physics/ECS/Base/Components/PhysicsJointComponents.cs:662), and [official motor authoring](https://docs.unity3d.com/Packages/com.unity.physics@6.7/manual/custom-motors.html). If the initial impulse path fails Gate M after correct frames/timestep/clamps, the explicit fallback is a composed low-level angular constraint/motor layout with one verified 3DOF design. It is not three stacked hinge factories. Review/get-set constraint APIs and their build timing before using them.

### 8.2 Defined frames and bounded control

For parent A and child B, compute world joint frames from the calibrated body-local joint transforms. Let `qRel = inverse(qWorldJointA) * qWorldJointB`; compute the desired relative orientation from the corrected animation/behavior target in those same frames. Use shortest-arc `qError = qDesiredRel * inverse(qRel)`. Rotate its full-angle logarithm into world space before combining it with world relative angular velocity.

The source's static-target damped fallback can be expressed as the following controller specification, with actual fixed step `h`, natural angular frequency `w` in rad/s, damping ratio `z`, `s = w*w`, `d = 2*z*w` (an authoring frequency in Hz is converted with `w = 2*pi*f`):

```text
angularAcceleration = (s*error - (d + h*s)*relativeAngularVelocity)
                      / (1 + h*d + h*h*s)
```

The source's articulated/rigid path is more elaborate. For SA's local parent/child approximation, compute `deltaOmega = h * angularAccelerationWorld`, construct `K = InverseInertiaWorldA + InverseInertiaWorldB`, and solve `K * J = deltaOmega`. Clamp the angular impulse `J` by `MaxTorque * h` in the declared joint/world cap convention, then apply `-J` to the parent and `+J` to the child. A static/kinematic body contributes zero inverse inertia and is not impulsed; singular/locked axes project to the supported subspace or produce zero with a diagnostic counter, rather than dividing by zero. This is an explicitly documented approximation of source articulated coupling. A moving-target feed-forward term, if enabled, uses target relative angular velocity in a proven common frame and is recorded as an adaptation; test static tracking first. Zero-strength disables drive but leaves contacts/limits.

Build the effective inverse-inertia operator from both dynamic bodies' mass frames; handle static/kinematic parents and singular axes without division by zero. Apply equal/opposite internal angular impulses. Do not turn target poses into unlimited external world torques on the torso, or directly set solved rotations every tick. Any optional external assistance must be a separately named, bounded, off-by-default mode and is not counted as physically self-balancing behavior.

Parallel safety: one Burst job iteration owns one disjoint character. Accumulate per-body joint contributions locally, then apply once per body, or execute its small joint chain sequentially. Do not schedule independent per-joint writes to the same parent's velocity. If a support-body reaction impulse is ever applied to a shared platform/body, reduce those contributions across characters separately. Entity ownership and physics-world index must be validated.

For pre-build external hits use component impulse APIs with correct frames; for selected post-build drives use `PhysicsWorld` world-space APIs. See [component world impulse](/home/i/ruk2/Library/PackageCache/com.unity.physics@5333869c01db/Unity.Physics/Extensions/PhysicsComponentExtensions.cs:524) and [world angular impulse](/home/i/ruk2/Library/PackageCache/com.unity.physics@5333869c01db/Unity.Physics/Extensions/PhysicsWorldExtensions.cs:263).

### 8.3 Gate M acceptance before a full humanoid

Build two bodies with rotated body/joint frames, nonzero COM offsets, nonidentity rest rotation, finite limits and a ground collider. Measure and assert:

1. World↔inertia angular conversion round-trips; zero target error/velocity produces no drive; quaternion sign reversal produces equivalent torque.
2. A modest displacement decays toward its target, while an external impulse visibly displaces it. Strength zero gives a passive physical result.
3. Internal impulses are equal/opposite within numerical tolerance and never exceed declared caps. Torque-to-impulse conversion uses `h`; 30/60/120 Hz test variants remain bounded, not assumed bit-identical.
4. Limit/anchor behavior survives gravity/contact and nonzero target tilt. No extra hinge locking is introduced at a ball joint.
5. After-build drive mutations affect this step's solved state and survive export. Component-only post-build writes must be shown to be the wrong path in a focused regression or documented instrumentation.
6. High impulses near a wall test the dynamic-tree refit path. No tunneling/CCD guarantee is inferred from a single pass; record supported impulse/speed bounds.
7. No skeletal parenting, normal+fixed double stepping, per-tick managed allocation or competing transform writer.

Record timestep, solver settings, gains, energy/error traces and exact failures. Passing this gate is necessary, not proof that a humanoid can balance.

## 9. Active controller stages and behavior contract

PD tracking alone does not provide balance. [SIMBICON](https://www.cs.ubc.ca/~van/papers/2007-siggraph-simbicon.pdf) motivates support/COM feedback beyond pose targets. [DReCon](https://static-wordpress.ubisoft.com/montreal.ubisoft.com/wp-content/uploads/2019/11/13214229/DReCon.pdf) uses a learned feedback policy; this plan does not inherit that capability or add an ML training dependency. The chosen solution is the selected Delirium controller port with independent profiles and tested physics adaptations.

### 9.1 Per-tick controller sequence

1. **Observe:** gather world body poses, mass/COM, linear/angular velocities and previous contact state. Compute character COM and COM velocity by mass weighting. Resolve fresh world indices after each build. Convert gravity/heading into a stable support frame.
2. **Sense:** ground/support probes at feet and anticipated landing points; wall/fall-brace probes from head/torso/hand reach. Use bounded cast counts, slope/reach filters and owner exclusion. Keep moving-support local anchors and velocities.
3. **Classify:** support polygon/margins, predicted COM/support error, foot plant/swing status, pelvis height/orientation, head danger, hit strength and current recovery state. Use named finite profile thresholds and hysteresis, not flickering booleans.
4. **Balance:** port identified `holding`, `pelvis_control`, `balance_solve` and target/gain shaping. Correct torso/pelvis/leg targets based on support and COM feedback. Maintain physically feasible internal actuation; contacts generate external support reactions.
5. **Step:** port foot placement/step selection and support handoff. Choose a reachable landing target with ground clearance and swing timing; solve leg targets with calibrated chain IK. Clamp leg extension/bend, step distance/height, duration and rate; abort/replan when support disappears.
6. **React/brace:** arbitrate hit reactions, upper-body/head protection and contact-aware arm bracing. Hand targets must be reachable and collision-aware. A failed reach or missing surface leads to a fall response, not a permanently pinned hand.
7. **Fall/recover arbitration:** port balance-failure/catch-fall logic and selected state ownership. Recovery/get-up suppresses conflicting locomotion/step intent. Airborne behavior relaxes or reorients within caps without invisible ground support.
8. **Drive synthesis:** combine animation target, selected balance/step/brace target changes, masks and gains once. Clamp anatomical targets and torque intent. Produce one bounded drive per physical joint, then the solver adapter applies it.
9. **Feedback after solve:** update support/contact/body state, detect impacts/failure/sleep, advance success/failure hysteresis, and issue small typed events for gameplay. Do not overwrite physical state to make a behavior report success.

Ground-aware IK here changes desired targets before drive. Optional final render-only foot/hand corrections come later and do not conceal failed physical support. Any gravity compensation/virtual-force term ported from source must be identified as internal joint action or explicit external assistance, with force balance and airborne tests. Do not copy native gravity/inertia manipulation into ECS as an unexplained stabilization trick.

### 9.2 Required behavior coverage

| Scenario | Required behavior | Failure must remain honest |
|---|---|---|
| Small torso push on level ground | Lean/limb adjustment and/or one corrective step, then stable support | Not merely snap back to animation |
| Directional/limb impact | One-shot impulse at actual point; local/full-body response according to profile | No frame-rate-multiplied impulse |
| Larger push/support loss | Recovery-step attempt, then catch-fall/relaxation if outside capability | No unbounded balance torque or hidden root pin |
| Wall/ground approach | Contact-aware brace when reachable, then release/continue safely | No bracing through surfaces or permanent attachment |
| Slope/step/moving platform | Updated support frame/anchors and reachable foot placement within declared terrain envelope | Unsupported terrain produces an event/fall, not fabricated stability |
| Supine/prone rest | Clearance check, correct owned get-up skill and root alignment | Missing/blocked skill keeps the body physical and reports why |
| Hit during get-up | Interrupt from actual current state, re-enter physical reaction without a pose reset | No forced completion of a stale get-up clip |
| Recovery completion | Stable support and a bounded handover to ordinary desired animation | No transform/velocity snap hidden by disabling physics |

Start with authored fixtures and conservative independent tuning, then expand the supported envelope. Record the measured push/terrain/mass range. “Works for any humanoid under any hit” and exact Euphoria/GTA IV fidelity are not acceptable claims.

## 10. Original finite-duration inertialization

### 10.1 Selected algorithm and source boundary

Implement the **original radial/axis-angle quintic offset decay** independently in `SA.Motion`, not a half-life spring. Unity wrappers use the same portable DLL; HLSL mirrors the mathematical contract and is verified against it. Existing SA blend algebra is not migrated. The user's [video](https://www.youtube.com/watch?v=BYyv4KTegJI) corresponds to David Bollo's GDC 2018 presentation. The source used here is its [original slides](https://media.gdcvault.com/gdc2018/presentations/bollo_david_inertialization_high_performance.pdf#page=24); do not claim the video was watched. References below use **one-based PDF page numbers**.

Restated scalar rule (pp.24–36), for nonnegative offset `x0`, duration `T>0` and incoming radial velocity estimate `vRaw`:

```text
v0 = min(vRaw, 0)
if v0 < 0: T = min(T, -5*x0/v0)
a0 = max(0, (-8*v0*T - 20*x0) / T^2)
A = -(a0*T^2 + 6*v0*T + 12*x0) / (2*T^5)
B =  (3*a0*T^2 + 16*v0*T + 30*x0) / (2*T^4)
C = -(3*a0*T^2 + 12*v0*T + 20*x0) / (2*T^3)
x(t) = A*t^5 + B*t^4 + C*t^3 + (a0/2)*t^2 + v0*t + x0
x(t) = 0 when t >= T
```

Position construction (pp.38–43), with corrected output history `Y0,Yprev`, history interval `h`, and the **same** current destination `B0` in both offsets:

```text
d0 = Y0 - B0; dPrev = Yprev - B0
x0 = length(d0); n = d0/x0
vRaw = (x0 - dot(dPrev,n))/h
output(t) = destination(t) + n*x(t)
```

Rotation construction (pp.44–49), using left correction and a consistent shortest/sign branch:

```text
D0 = Yrot0 * inverse(Brot0); Dprev = YrotPrev * inverse(Brot0)
n = Axis(D0); x0 = Angle(D0)
thetaPrev = unwrap_near(x0, 2*atan2(dot(Dprev.xyz,n), Dprev.w))
vRaw = (x0 - thetaPrev)/h
outputRotation(t) = AxisAngle(n,x(t)) * destinationRotation(t)
```

`n` is in the parent frame for local rotations. This is a radial source-history estimate, not explicit source-minus-destination velocity. For alternatives, [spring inertialization](https://theorangeduck.com/page/spring-roll-call#inertialization) is a different algorithm; [Epic's integration guidance](https://dev.epicgames.com/documentation/en-us/unreal-engine/animation-blueprint-blend-nodes-in-unreal-engine) is useful for node ordering but is not the math specification.

### 10.2 Exact contract and SA numerical policy

The original construction projects away perpendicular velocity and clamps away-moving radial velocity. A moving destination contributes its own derivative. Therefore **do not promise unconditional full-vector/angular C1 continuity at transition start**, or match outgoing acceleration. The scalar correction reaches zero value/first/second derivative at its endpoint; that endpoint does not add a discontinuity to an otherwise smooth destination. Terminal zero jerk is not guaranteed after clamping `a0`. No-overshoot refers to the scalar offset, not world motion, contacts or arbitrary destination trajectories.

SA implementation decisions, distinct from claims about the slides:

- Reinitialize from the latest **corrected output** and its actual prior timestamp on every new transition/interrupt. Never reconstruct the source from an old raw clip or reset to an old transition origin. Full output kinematics remain available through timestamped histories for physics transfer and diagnostics.
- Missing history or invalid/zero historical interval uses zero initial velocity; zero current delta time does not advance history or elapsed time. Sanitize invalid requested durations at the API boundary. Check the **final** effective duration again after `T = min(T,-5*x0/v0)` and before computing any powers/reciprocals; a nonfinite, nonpositive or epsilon-small final duration chooses the destination directly for that channel and clears its correction. This policy can intentionally snap in degenerate cases; never claim initial continuity through that direct-target fallback.
- Near-zero current offset magnitude clears that correction. Normalize finite quaternions; hemisphere-align history and handle near-pi branches explicitly. If the **current** rotational offset has no valid nonzero axis/angle, clear that rotational correction. If the current offset is valid but only the **previous** twist projection is degenerate (both atan2 arguments near zero), keep the current offset/axis and treat velocity history as missing (`vRaw = 0`); do not clear a valid correction and create an avoidable pose pop. Unwrap a valid historical twist near the current angle. Use identical thresholds/branch policy in C# and HLSL.
- Evaluate the polynomial with Horner form and normalized time/coefficient scaling if needed for numerical conditioning; prove equivalence to the stated endpoints and test small durations. Do not evaluate beyond the endpoint and hope cancellation produces zero.
- Initial scale policy: apply the same scalar radial rule to the vector of **log positive scale** as an SA extension, then exponentiate. Nonpositive/signed scale immediately uses destination scale and resets only scale history/correction state; position and rotation correction remain intact. Physical bodies still disallow animated nonuniform scale. Do not attribute scale policy to the original slides.
- Generic transition history is the corrected animation output **before downstream contact/IK**; keep that stage explicit to avoid feeding constraints back into the animation filter. Physical takeover/recovery captures actual resolved physical/render output separately in a consistent frame.
- Teleports, skeleton/layout changes, pool reuse and explicit discontinuous-reset commands clear/seed histories. Root/frame changes must rebase both histories and offsets before differencing; a uniform coordinate-frame change must not look like character velocity.

For valid current position/rotation offsets with usable effective duration, explicitly test `output(0) == captured corrected Y0` (rotation up to quaternion sign), including a valid current rotation plus a degenerate historical twist. Separate tests document the intentional exceptions for direct-target/near-zero/invalid-scale policies. Acceptance must include expected velocity changes for perpendicular, away-going and moving-destination cases. Only a stationary-destination, parallel approaching case should demand the original projected starting derivative. If full-vector start-C1 is later required, design/review a distinct relative-velocity/componentwise extension; do not quietly change this algorithm while claiming original behavior.

### 10.3 Integration and idempotent requests

Add an opt-in `TransitionIntent`/revision and pure transition operations. Keep current `Play`, `CrossFade`, `Blend`, `Stop` behavior intact. Proposed minimal additional surface:

```text
Playback.Transition(states, transitionIntent, desiredKey, clip, seconds, playOptions)
Playback.TransitionBlend(states, transitionIntent, desiredKey, clips, weights, seconds, playOptions)
```

These are specification signatures, not paste-ready C#. Their ECS wrappers use `ref` buffers/components and unmanaged values. `desiredKey` identifies a destination recipe; clip/mask IDs remain local to the character's asset tables. Repeating the same desired key/recipe updates ordinary continuous weights/speed without resetting phase, history, revision or transition age. A new recipe/key or explicit edge-triggered restart increments revision once. Reusing a key with inconsistent clip membership/mode/mask is a checked misuse, not silent stale playback. Ordinary eight-way direction-weight changes within a locomotion recipe are not new transition requests.

An inertialized request installs the destination recipe with the required immediate state semantics, captures its current destination pose and initializes correction once. It does **not** also keep a normal crossfade alive for the outgoing recipe. Existing intentional multi-clip destination blending and independent additive layers still evaluate. Initially use one full-rig visual inertialization filter after final layer evaluation; do not create multiple order-dependent inertializers for overlapping masks. Layer changes can request that same filter.

Destination pipeline:

```text
sample destination clips -> existing layers/masks/additive algebra
-> inertialization correction -> target/contact constraints as applicable
-> CPU bone write OR physical target controller OR GPU FK/skinning
```

Stop/restart/phase changes that are declared discontinuities can use the same request mechanism. Keep the existing `BlendSpace` helpers and phase synchronization; inertialization smooths changes between outputs, not the underlying locomotion phase design.

### 10.4 CPU/GPU state and lifecycle

- CPU opt-in characters retain two timestamped corrected **local** output poses per bone plus active direction/axis/scalar-decay records. Derive output velocity from those histories with the stored historical interval; do not substitute the new frame's delta time.
- Physics-capable characters evaluate corrected CPU targets at their fixed tick. Do not also run the visual GPU animation evaluator on the same character. Current GPU skinning can consume their mapped bone worlds.
- Pure GPU opt-in characters retain local histories/offsets on GPU. Apply correction before FK. Keep C#/HLSL equations, sign conventions and epsilon policy aligned; readback is only for tests/diagnostics, never a production per-frame dependency.
- Existing `_Poses`/`PoseSlot` layout is compacted frame output, not a persistent identity. Add an independent stable slot+generation allocator for histories. Spawn/despawn, compaction and engine lifecycle must not cross-wire character histories. Only request/identity metadata crosses each frame; no CPU upload of whole GPU poses.
- Allocate history for transition-capable cohorts explicitly and active-offset storage through reusable pools. Histories cannot be discarded every idle frame if a later arbitrary transition must use actual prior output. When disabling/reenabling the capability, seed it and declare the first-frame velocity fallback.
- Report memory honestly: two 40-byte `BonePose` histories are 80 bytes/bone before offsets, alignment and kinematics/workspace. At 50,000×49 bones that alone is about 196 MB decimal. Active correction state adds more. Make opt-in/LOD choice visible; do not claim transitions are free or globally allocate rich physical state to the entire crowd.

## 11. Root control, physical takeover and get-up

### 11.1 Entering physical control

Activation occurs at the pre-build lifecycle boundary. Sample the current resolved output in a known world/root frame, map each body through its calibrated offset, and seed velocities from timestamped output/root kinematics. Convert bone pivot velocities to COM velocities correctly. Respect declared speed/energy caps only through explicit policy/events; do not silently zero motion just to stabilize entry.

Enable the prebuilt proxy/joint set, disable competing pose ownership and run the next physics step. `Active` tracks targets with controller feedback; `Passive` removes drives while retaining physical limits/contacts. A hit is a one-shot impulse with application point and sequence ID, not a force replayed each rendered frame. Deliberate continuous forces use a separate API and multiply by the actual fixed `h` once.

### 11.2 Root and locomotion intent

The gameplay API supplies desired planar velocity/facing and selected animation recipe. While physical, Unity owns world body motion; the rig root follows the resolved physical/pelvis frame for rendering and bounds. No root teleport is used to achieve a balance target. Initial locomotion clips for physical tracking are in-place or have an explicitly declared root-channel treatment; generic root-motion extraction is not silently added to the tiny animation core.

Recovery metadata may specify a root/anchor trajectory for a get-up skill. That is owned by the recovery controller and validated against contacts/clearance while physics remains active. Host character/navigation motion is suspended or receives resolved motion during physical ownership, so it cannot also move the root against the body solver.

### 11.3 Recovery contract

1. Detect recoverable prone/supine state from actual torso/pelvis/ground normals, support, speeds and settling duration. Do not start merely because a timer expired.
2. Check body/limb clearance and stable/moving support. Select an owned compatible get-up skill with authored start anchors/contact phases. Missing or blocked skill produces a typed reason and leaves physical ownership intact.
3. Align destination heading/root anchors to the actual solved body without changing the body's current world pose. Rebase local pose histories accordingly. Initialize recovery transition from physical pose/velocities, not the last animation target. Construct historical poses consistently from snapshots; no fake zero velocity.
4. Inertialize the new desired target **before** drive/contact constraints. Ramp stiffness/caps and execute contact-aware support/hand/foot phases. Do not render a get-up animation while teleporting a passive body underneath it.
5. Only hand over to animation when contact/support, residual pose error, speeds and collision clearance are within the independently declared safe envelope for a sustained interval. The original radial filter cannot preserve arbitrary high tangential body velocity: remain physical until the handover is safe rather than hiding that limitation.
6. A new impact or failed support interrupts recovery from current physical state. Re-enter active/passive response with histories aligned; do not resume an old capture after the interruption.
7. On successful handover, seed animation corrected-output history from the last actual output and transfer pose/root ownership exactly once. Repeated `Recover` intent during the same valid recovery does not restart the clip.

The source get-up path's lerp/slerp is not the inertialization algorithm and is not copied as the final transition solution.

## 12. Minimal public interface and compatibility

Retain animation APIs and lookup semantics. Add an opt-in physical authoring component and a small typed runtime API, approximately:

| Intent/operation | Data/meaning |
|---|---|
| `React.Activate` / desired mode | Animated, Active, Passive; idempotent mode intent |
| `React.SetMovement` | Desired planar velocity and heading in a declared world/support frame; no Animator parameters |
| `React.Hit` | Sequence ID, body/bone/role, world impulse, world application point, optional reaction profile/mask |
| `React.Recover` | Desired recovery enabled/disabled and skill set; idempotent while already recovering |
| `React.SetStrength` | Bounded per-role/layer strength intent; does not edit shared profile data |
| `React.TeleportReset` | Explicit lifecycle relocation/reset at the safe boundary; not an implicit balancing tool |
| `ReactionEvent` | Activated, stepped, braced, lost balance, fallen, blocked/missing recovery, recovered, invalid/overflow input |

Use structs/enums/IDs and buffers, with pure functions plus scheduling systems. Requests that represent events use sequence/consumption semantics; continuous desired-state calls use equality/idempotence. No strings, boxed payloads, reflection or dynamic parameter registry in the hot path. Expose capability/status queries so callers know whether a rig has bracing/recovery support.

Keep two-way 1D, multiple 1D thresholds, Cartesian, eight directions and idle-at-origin examples. For an eight-direction ring, neighboring directional clips can blend; more general layouts can involve more contributors. Recovery can blend into a locomotion recipe, not only a single clip. Do not introduce transition-pair assets or an asset for every desired-state edge.

## 13. Pooling, collision, sleeping, culling and detail tiers

- Bake a complete physical-capable prefab archetype. Pool activation/deactivation and any structural operations occur before build through the declared lifecycle barrier. Use the installed physics-exclusion mechanism only after validating that it excludes the **whole body/joint set**; otherwise a bounded pre-build add/remove/Disabled path is acceptable. Do not leave orphan joints solving against inactive bodies.
- Store prefab-remapped entity refs; resolve world packed body indices anew after build. Pool reuse increments identity generations and resets hits, contact anchors, behavior timers, render interpolation, inertialization and consumed-request revisions. Shared blobs/colliders stay valid through reference ownership.
- Disabling adjacent collisions via constrained pairs must not disable separate characters. Additional self-pair exclusions use owner-aware pair filtering if needed; a unique owner identity is not a shared negative global group. Test limb-to-body self contacts and two touching characters.
- Active balancing/controller drives keep a character awake. Do not assume Unity Physics 6.7 provides the desired character sleeping policy automatically. Passive-rest detection is an explicit, tested companion policy: low linear/angular speed, stable support and time thresholds; contact/hit/support-motion changes wake it. A fully excluded character cannot receive collision-driven wake events, so that tier requires explicit gameplay/proximity activation or retained collision representation.
- Visibility only culls rendering. For inertialization-capable animation, continue the required evaluation/history updates while render-culled; this plan does not silently add evaluation culling. A later evaluation-cull tier must explicitly invalidate/reseed history or advance it under a separately tested policy, with a declared resume-velocity fallback. Do not stop physical simulation for an invisible body still interacting with the world. Controllers may run at reduced rates only with fixed-clock/body-simulation continuity, bounded held targets and stability tests.
- Detail tiers are explicit cohorts: full active controller near gameplay; reduced controller/probe frequency within measured envelope; passive simulated ragdolls; settled/pooled bodies; animation-only GPU crowds. Do not silently replace an active hero with a passive body when a probe budget is exhausted.
- Change physical topology/collider detail only at a safe lifecycle boundary with pose/momentum transfer. The initial release keeps one topology while active; runtime remeshing/automatic GPU-only→physical conversion is deferred.
- Do not mutate a shared collider's filter/material to implement one character's state. Choose component/pair policy or an explicitly owned collider variant with correct lifetime.

## 14. Performance design and measurement contract

Fast is an engineering target, not a current result. Separate animation-only crowd scale from active articulated-body scale.

### 14.1 Intended runtime properties

- No managed allocations per ordinary tick after warmup. No string parsing, editor APIs, asset loading, native-plugin calls or per-joint structural changes.
- Share definition/profile/collider/animation assets. Use jobs over disjoint characters and packed per-character body/target workspaces. Reduce events/impulses explicitly and schedule dependencies instead of indiscriminate main-thread completion.
- One target evaluation per fixed physical tick; no normal+fixed duplicate. One controller result per joint/body. Preserve existing GPU shared-asset upload behavior; only new transition metadata/state changes are additional traffic.
- Bounded query counts and cached semantic/frame data; fast casts with owner filtering. Ground/brace queries, body snapshot, control math, impulses, dynamic-tree refit, physics solve, output mapping, animation and rendering receive separate profiler markers.
- Histories/contacts/offset workspaces grow through pools with high-water reporting. Reuse frame scratch; do not proliferate buffers without measuring chunk/allocation cost.
- Use the existing GPU kernel initially. One-thread-per-bone or unrelated animation throughput rewrites are a later measured optimization, not hidden scope in this controller plan.

### 14.2 Workload matrix

| Workload | Counts/settings | Measure separately |
|---|---|---|
| Drive viability | One/two bodies, grounded/free, 30/60/120 physics Hz | Error, energy, clamps, broadphase correctness, per-step cost |
| Active humanoids | 1, 10, 50, 100, then 250 only if previous tiers are stable | CPU target/control/probes, refit, Physics solve, mapping; bodies/joints/contact pairs |
| Passive/settled | Same physical cohorts with drives disabled and rest policy variants | Solve/contact cost, rest/wake behavior, memory |
| Mixed gameplay | Small active cohort plus passive bodies and moving platforms/contacts | Spikes, inter-character contention, query/event bounds |
| Animation-only | 10,000 and 50,000 GPU rigs, baseline and opt-in transitions | Animation CPU/GPU, persistent history memory, uploads, render cost separately |
| Transition stress | Synchronized request burst, random interruptions, eight-way destinations | Target sample count, correction cost, history pool churn, spikes |

Set and record the actual target hardware, resolution, rendered mesh complexity, fixed/render rates, solver iterations, contacts, driver/API, build configuration and warmup/measurement windows. Use a focused standalone player for rendered timings. Editor out-of-focus throttling is not a performance result. A GPU fence/readback wall-clock test is CPU+GPU synchronization cost, not isolated GPU kernel time; use GPU timings where supported and label limitations.

Before tuning, agree a **provisional budget** for the named hardware/workload: for example, at 60 Hz reserve at most 25% of the 16.67 ms frame (about 4.17 ms) for the complete 100-active-character physical pipeline, with a separately reported controller subtotal, and a 10% p95 overhead target for 50k animation-only playback when inertialization is disabled. These are planning goals to validate or revise, not promises. Record average, p50/p95/p99, maxima, memory and zero-allocation counts; show the maximum stable active cohort within budget. The 50k cohort is not 50k active humanoid ragdolls.

Optimize only the measured dominant stage. If refit dominates, redesign control timing/query snapshots with proof of correctness before removing it. If physics dominates, adjust the explicitly declared body/detail/cohort envelope. If GPU history dominates memory, restrict opt-in cohorts or implement a separately reviewed compression policy; do not silently lose interruption history.

## 15. Incremental execution phases

Every phase produces concrete evidence and updates a small decisions/provenance report. After P0A acknowledgement, begin the independent P0B/P1/P3 work rather than stopping at another paper-only response. Source-port phases remain conditional on their genuine gates. Do not proceed through failed gates by marking a passive body as complete active behavior.

### P0 — Publication, baseline and cross-runtime viability

**P0A: publish the reviewed plan and tracking issue before implementation.** The designated publisher commits the plan to the existing SA repository's main branch and creates the issue. The coordinator acknowledges that result and initializes/assigns the canonical `sa-motion` repository/source roots. Until then, plan edits/read-only preparation only. Do not perform implementation/git work in parallel with that documentation commit.

**P0B: independent work permitted after acknowledgement.**

- Read actual AGENTS/Unity CLI guidance and inspect current user changes. Preserve the current 0.3 package; do not rebuild it from the old plan or refactor proven math for reuse.
- Run the existing regular SA suite as baseline and record actual results; 35 is the prior recorded result.
- Create the neutral `netstandard2.1`/C#9 library, .NET10 test host, local NuGet/package-consumer smoke fixture and thin canonical UPM bridge. No registry publication.
- Implement the smallest representative independent scalar/quaternion/math kernels needed to exercise real operations, not just an addition-only plugin smoke test.
- Pack once; verify no `Unity.*` assembly references or net10 BCL leakage; compare the NuGet payload SHA with the Unity DLL.
- Compare .NET10, managed Unity and **synchronously compiled native Burst job** results using shared edge/golden fixtures. A BurstDiscard-only managed sentinel must distinguish native execution from managed fallback. Test actual transcendental paths, layout sizes/alignment, allocations and caller-owned-buffer behavior; spans remain inside the job, not its boundary.
- Run the declared player/backend probe as well as Editor tests. Editor import/compilation is insufficient. If local player build capacity is genuinely unavailable, record it as unverified and do not call Gate R passed; continue bounded independent work that does not depend on that deployment claim.
- Record supported units/numeric ABI, actual Unity/Burst2/runtime profiles and measured conversion/kernel costs. Choose the same-source bridge fallback only if the default route fails and is diagnosed.

Exit P0B: Gate R evidence for the tested backend or an explicitly incomplete deployment claim, current SA baseline intact, canonical sources under their assigned repository, reproducible local package/bridge artifacts. Proceed with independent inertialization and minimum physics foundations; do not stop all work because the source-port decision remains open.

**P0C: controller route, which can remain pending while independent phases advance.** Resolve Gate L and minimum Gate D data before translating functions. Pin the source revision/port inventory and independent profile/owned recovery-clip provenance. Establish source-to-Unity basis, handedness and unit conversion with polar/axial, quaternion, inertia and impulse fixtures before transferring controller formulas or gains. Unresolved source rights, opaque fields or extracted data block the affected port capability, not unrelated foundational math/tests.

### P1 — Spaces, topology and two-body physics proof

- Create the companion assembly skeleton and a minimal public-component baker/test fixture.
- Implement pure frame conversions and a minimal bounded quaternion drive adapter independently from standard mechanics and the mathematical specification. This is a physics viability fixture, not a copied Delirium controller or evidence that the port is complete.
- Create real two-body limit-joint setup, world mutation/refit stage, export observation and a debug visualization.
- Validate world/inertia conversions, impulses/limits/timestep and ownership from Gate M; do not add full balance yet.

Exit: Gate M passes with numerical traces and an actual rendered two-body response. Correctness of body/joint factory/API assumptions is proven in this installed Unity version.

### P2 — Automatic humanoid and target/proxy/render separation

- Implement 15-body semantic mapping/generation, independent shape/mass/limit profile and overrides.
- Bake unparented proxies/joints, shared blobs/colliders, target buffers and explicit pose ownership.
- Implement the one-clock fixed target path, physical activation/exit, solved snapshot/render mapping and bounds.
- Demonstrate passive ragdoll plus modest tracking on a humanoid Avatar and a differently oriented explicit generic humanoid. This is a pipeline milestone, not completion of active reactions.

Exit: Gate B passes; real subscene reload/prefab instantiation/sharing and mesh motion work; no duplicate clock/writer.

### P3 — Pure transition math and CPU integration

- Complete the original scalar/vector/quaternion rules and numerical/scale policy from §10 in `SA.Motion`; verify its packed artifact in the .NET and Unity/Burst hosts. Add only SA-specific conversion/state/clock integration in the animation package.
- Add idempotent desired-state requests and CPU history/correction ordering without breaking existing APIs.
- Test repeated and interrupted transitions, eight-way/layered destination inputs, elapsed/history dt differences, quaternion branches, teleports and downstream-constraint stage separation.

Exit: CPU scalar laws and honest continuity cases pass. CrossFade regression stays green; no outgoing recipe is evaluated after an inertialized switch merely to keep a transition alive.

### P4 — GPU inertialization and stable state lifecycle

- Add opt-in persistent GPU local histories/offsets and stable identity/generation slots, separate from compacted frame poses.
- Mirror math/branches/order; initialize requests on GPU and retain corrected outputs without readback.
- Test CPU/GPU parity, interruptions, reordered/compacted characters, spawn/despawn/pool reuse and eight-way destinations. Measure memory and per-request/per-frame traffic.

Exit: Gate I passes, including real GPU readback parity tests and rendered animation. No unmeasured throughput claim.

### P5 — Port observation, support, balance and corrective steps

- Translate identified source POD/control functions into the separately gated portable `SA.ReactionControl` project only after each port record is complete and rights/data gate cleared. Feed engine-neutral snapshots/intents through the Unity adapter; never add the translated dependency to the independently authored `SA.Motion` DLL.
- Replace body/collision adapters, fixed role indices and tuning slots; integrate fresh probes and after-solve contact feedback.
- Implement support/pelvis/foot placement/step/leg/spine control and explicit failure state.
- Validate synthetic state→target behavior first, then physical push/step/slope/platform fixtures on the supported humanoids.

Exit: recoverable pushes produce measured active recovery; unsupported pushes lead to fall policy. No hidden external root pin or passive-only substitution.

### P6 — Impacts, bracing and falling

- Add one-shot hit routing, typed behavior arbitration, contact-aware arm/head response and fall transitions.
- Verify deterministic priority, bounded torque/query counts, safe misses/release, support loss and inter-character contacts.
- Test interruption during an animation transition and during an ongoing physical response.

Exit: bracing/fall/impact coverage table is demonstrated with actual solver contacts and rendered bodies; source/adaptation/tuning record remains complete.

### P7 — Recovery and return to animation

- Add owned get-up metadata/selection, current physical-history capture, root alignment, clearance and support checks.
- Execute recovery targets under active physics with gain scheduling and safe ownership handover; handle interrupted/blocked/missing skills.
- Use inertialization, not source get-up lerp/slerp, for the specified transition correction.

Exit: prone/supine and hit-during-get-up fixtures pass; root, pose and measured residual velocities satisfy declared handover bounds. No fake full-C1 claim.

### P8 — Lifecycle, detail and performance validation

- Validate pool/reset/sharing, rest/wake/exclusion, event/query capacity and rendering-cull separation.
- Run the workload matrix on recorded hardware. Profile each stage; tune only measured bottlenecks within validated physical behavior.
- Publish measured capability/performance/memory envelopes in docs, clearly distinguishing active cohorts from 10k/50k animation-only rigs.

Exit: Gate P passes for a declared target tier or a documented revised envelope is accepted; all prior correctness gates remain green.

### P9 — Documentation and release readiness

- Rewrite READMEs around the final small API and setup, with source provenance/license record, humanoid limitations, scale/frame rules, get-up asset requirements, clock/ownership diagrams and measured results.
- Keep existing 1D/eight-way examples and independent animation sharing. Separate native-source provenance from runtime dependencies: no native Delirium runtime is required.
- Run the final relevant regression/integration suites and inspect actual rendered fixtures. Report incomplete capability honestly.
- Commit/push/publish only when separately authorized for that future implementation session. This planning request authorizes none of those actions.

Exit: the definition of done below is checked with linked artifacts, not a completion narrative unsupported by tests.

## 16. Verification matrix and future Unity CLI workflow

### 16.1 Required automated/numerical checks

| Area | Required tests/evidence |
|---|---|
| Portable/runtime boundary | Exact netstandard2.1 artifact consumed through local NuGet and Unity; no Unity/net10 reference leakage; managed/Burst/.NET parity, native-execution sentinel, ABI/allocations and player/backend proof |
| Current animation regression | Preserve all existing 35 regular behaviors: blend algebra, idempotence, shared clips/masks, source rules, serialization, GPU parity and live SkinningSystem updates |
| Inertial scalar | Initial value, stated initial derivative, nonnegative monotone canonical cases, exact endpoint x/v/a within tolerances, duration clamp, a0 clamp and no extrapolation |
| Inertial vector/quaternion | Radial/twist projection, explicit valid `output(0)` equality, sign equivalence, near-zero/pi, valid current offset with degenerate historical twist, shortened-duration epsilon, actual historical dt, interrupted corrected-output history, known perpendicular/away/moving-target limitations |
| GPU state | C#/HLSL parity; stable slot identity across frame compaction, destruction and generation reuse; no readback dependency; request burst/resource growth |
| Frame math | Rotated body/joint/COM frames, world↔inertia angular velocity, full-angle convention, root rebase, velocity at points, anatomical left/right axes |
| Generator | Avatar and explicit generic mapping, invalid/missing bones, scale/length/mass errors, limit/rest inclusion, deterministic IDs, actual bake/store/serialization and prefab remapping |
| Physics scheduling | One playback tick per fixed step at 30/60/120 render rates, zero/multiple fixed ticks, paused/slow/reverse target playback with real unscaled physical h, pre/post-build data authority, export/refit, render mapper before final constraints, no competing bone writer |
| Drives/contact | Equal/opposite bounded internal impulses, strength0 passive, joint constraints, adjacent/nonadjacent/self/inter-character collisions, ground-probe owner rejection |
| Controller | Synthetic deterministic state→intent tests; real small/large push, stance/swing/landing, support loss, wall brace, moving support, airborne behavior and safe failover |
| Recovery | Actual physical pose/velocity capture, prone/supine alignment, clearance/missing-skill handling, hit interruption, bounded residual handover, ownership/root consistency |
| Lifecycle/performance | Pool/reset generations, excluded body/joint sets, wake policy, contact/event overflow diagnostics, no steady-state managed allocation, measured cost/memory cohorts |
| Rendering | Visible CPU-target/physics output through SA skinning, multiple meshes/rotated mesh transforms, interpolated fall/get-up, bounds/shadows/material path and captures at distinct timestamps |

Physics outcomes need tolerances, invariant checks and repeated statistical fixtures where appropriate. Do not assert bit-identical cross-platform contact dynamics. Pure deterministic modules should reproduce the same inputs/dt sequence; GPU checks use declared numerical tolerances. Screenshots that merely differ prove motion, not stability, correct ownership or balanced recovery.

### 16.2 Commands after the plan/issue publication barrier

After P0A publication acknowledgement, the independent repository uses commands such as the following (project names/paths become real during P0B):

```bash
dotnet test /home/i/GitHub/sa-motion/tests/SA.Motion.Tests/SA.Motion.Tests.csproj --configuration Release
dotnet pack /home/i/GitHub/sa-motion/src/SA.Motion/SA.Motion.csproj --configuration Release --output /home/i/GitHub/sa-motion/artifacts/packages
```

The packaging script must then restore/run `tests/PackageSmoke` from that local feed, record SHA256/assembly references, and import the same DLL into the canonical UPM bridge. Exact test-framework package versions are pinned/locked when the host is created. `dotnet pack` is not `dotnet nuget push`; registry publication remains out of scope.

Use the running Editor. Do not close/restart the user's Editor just to run tests. The installed CLI form and the commands below were used successfully in the earlier 0.3 session; rediscover schemas if the installed package changes.

```bash
unity status
unity command --query compile --detail compact
unity command --query 'Execute Unity tests with filtering options' --detail full
unity command script recompile
unity command script recompile_status
unity command test run --mode EditMode --filter SA.Tests --filter_type assembly --include_explicit false --async_tests true
unity command test status
```

For the proposed new assemblies once they exist (run the bridge Burst/player probes in addition to the reactions suites):

```bash
unity command test run --mode EditMode --filter SA.Motion.Unity.Tests --filter_type assembly --include_explicit false --async_tests true
unity command test status
unity command test run --mode EditMode --filter SA.Reactions.Tests.EditMode --filter_type assembly --include_explicit false --async_tests true
unity command test status
unity command test run --mode PlayMode --filter SA.Reactions.Tests.PlayMode --filter_type assembly --include_explicit false --async_tests true
unity command test status
```

Poll status at sensible intervals; test submission reporting `running` with zero results is not a pass. Check real summary/counts and messages. Only run explicit benchmarks deliberately after discovering their precise test filters; do not enable every explicit test during ordinary verification.

Use `unity command --query ... --detail full` to obtain the installed playmode, scene, capture, profiler and script-evaluation schemas before giving their commands arguments. Enter Play through the main scene and allow subscene streaming. Save captures inside the project and numerical reports under `Temp/sa/`. A prepared one-off inspection can use the existing `unity command script eval_file --file <absolute-path> --timeout <seconds>` after implementation authorization, but package runtime/tests must not rely on reflection into Unity internals. Prefer public baking/serialization APIs and real scene integration; internal baking APIs in this Unity build are not public package utilities.

Record profiler reports from a focused standalone player for rendered performance. If using a compute/readback microbenchmark, label its scope and synchronization overhead. Never convert editor throttling, averaged screenshot sizes or a headless result into an isolated GPU/frame-time claim.

## 17. Completion criteria and unresolved items

Completion requires all of the following:

- [ ] Portable math has one canonical neutral source repository, reproducible netstandard2.1 NuGet/DLL artifacts and verified .NET/managed Unity/native Burst/player compatibility for the declared backend; no unnecessary migration of current SA features.
- [ ] Gate L has a resolved, recorded source/license/notices/distribution decision; the animation package is not silently relicensed and package separation is not described as a legal exemption.
- [ ] All selected controller dependencies/tuning fields have known meanings/units and independent data provenance; no GTA unlock, extracted tuning or native runtime dependency remains in standalone mode.
- [ ] Public-component automatic humanoid generation works for the declared Avatar/generic scope, with honest role-chain adaptation from the source's 21-part assumptions.
- [ ] Unparented physical proxies, separate CPU animation targets and rendered hierarchy have one writer and one clock. World/inertia/joint frames and impulses are correct.
- [ ] Two-body viability and complete physical-system integration pass before high-level controller success is claimed.
- [ ] The specified controller port supplies observed support/balance/steps/bracing/fall/get-up behavior within a documented envelope; passive bodies/PD alone do not satisfy the goal.
- [ ] Original quintic behavior and its limitations are documented and tested. Corrected-output histories survive interrupts; GPU state has stable identity; no pair-specific transition asset is required.
- [ ] Eight-way/1D/layered destinations and the current shared asset architecture remain supported; the ordinary animation API stays small.
- [ ] Actual physical-output recovery/root alignment/interruption/handover is verified, with owned required clips and meaningful failure states.
- [ ] Pool/collision/rest/culling/detail behavior is correct; no per-frame managed allocation or unbounded event/query growth is hidden.
- [ ] Current regressions, new math/bake/physics/GPU integration and real rendering checks pass with reports.
- [ ] Performance/memory results distinguish active ragdolls from animation-only crowds and are measured on named hardware; no exact GTA fidelity or unmeasured scale claim is made.

Unresolved before execution: licensing/distribution clearance for the direct source port; complete independent named tuning profiles and semantic mapping of opaque source fields; authorized recovery clips and their metadata; target hardware and accepted active-character budget. These are explicit gates/inputs, not permission fabricated by this plan. The two-body and behavior phases resolve remaining engineering uncertainties through concrete tests rather than assumptions.

Execution begins only after the reviewed plan/issue documentation commit is acknowledged. The user has authorized implementation after that barrier: advance the independent compatibility/inertialization/minimum-physics foundations and report real checkpoints, while keeping any unresolved Delirium port/data work honestly gated. Repository operations stay coordinated/publisher-owned; no NuGet registry release, new public repository or force-push is authorized.
