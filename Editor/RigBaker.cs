using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Hybrid.Baking;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEditor;
using UnityEngine;

namespace SA.Editor
{
    [TemporaryBakingType]
    internal struct BakedBone : IBufferElementData
    {
        public Entity Value;
    }

    [TemporaryBakingType]
    internal struct BakedSkin : IBufferElementData
    {
        public Entity Renderer;
        public BlobAssetReference<SkinBlob> Skin;
        public AABB Bounds;
    }

    internal class RigBaker : Baker<RigAuthoring>
    {
        public override void Bake(RigAuthoring authoring)
        {
            var root = authoring.transform;
            DependsOn(root);
            foreach (var transform in GetComponentsInChildren<Transform>())
                DependsOn(transform);
            foreach (var bone in authoring.ExtraBones)
                if (bone != null)
                    DependsOn(bone);
            var animator = GetComponentInChildren<Animator>();
            if (animator != null)
            {
                DependsOn(animator);
                DependsOn(animator.avatar);
                DependsOn(animator.runtimeAnimatorController);
            }
            foreach (var source in authoring.AnimationLibraries)
                if (source != null)
                    DependsOn(source);
            foreach (var motion in authoring.GetComponents<IMotionClips>())
                DependsOn((UnityEngine.Object)motion);
            var features = authoring.GetComponents<ICpuPoseFeature>();
            foreach (var feature in features)
                DependsOn((UnityEngine.Object)feature);
            var errors = new List<string>();
            var clips = new List<AnimationClip>();
            foreach (var clip in AnimationDiscovery.Clips(authoring))
                if (clip != null && !clips.Contains(clip))
                {
                    DependsOn(clip);
                    var reference = AnimationUtility.GetAnimationClipSettings(clip).additiveReferencePoseClip;
                    if (reference != null)
                        DependsOn(reference);
                    clips.Add(clip);
                }
            var masks = new List<AvatarMask>();
            foreach (var mask in authoring.Masks)
                if (mask != null && !masks.Contains(mask))
                {
                    DependsOn(mask);
                    masks.Add(mask);
                }
            var skins = new List<SkinAuthoring>();
            var meshes = new List<Mesh>();
            foreach (var skin in GetComponentsInChildren<SkinAuthoring>())
                if (skin.Rig == authoring)
                {
                    DependsOn(skin);
                    var filter = GetComponent<MeshFilter>(skin);
                    var mesh = filter != null ? filter.sharedMesh : null;
                    DependsOn(mesh);
                    skins.Add(skin);
                    meshes.Add(mesh);
                }

            if (!(authoring.SampleRate > 0f) || !math.isfinite(authoring.SampleRate))
                errors.Add("SampleRate must be greater than 0.");
            if (authoring.Autoplay != null && !clips.Contains(authoring.Autoplay))
                errors.Add($"Autoplay clip '{authoring.Autoplay.name}' is not in Clips.");
            if (clips.Count >= ushort.MaxValue || masks.Count >= ushort.MaxValue)
                errors.Add("a rig supports fewer than 65535 clips and masks.");
            var names = new HashSet<string>();
            foreach (var clip in clips)
                if (!names.Add("clip:" + AnimationDiscovery.Name(authoring, clip, clips)))
                    errors.Add($"two clips resolve to the same name '{clip.name}'. Change an animation's name in the resolved animations list.");
            foreach (var mask in masks)
                if (!names.Add("mask:" + mask.name))
                    errors.Add($"two masks are named '{mask.name}'. Mask names must be unique per rig.");
            if (GetComponentsInChildren<SkinnedMeshRenderer>().Length > 0)
                errors.Add("a SkinnedMeshRenderer is still under the rig. Run Tools/SA/Convert Skinned Mesh Renderers.");

            var skeleton = RigBuilder.Skeleton(root, clips, skins, errors, authoring.ExtraBones);
            var skeletonSet = new HashSet<Transform>(skeleton);
            if (authoring.MotionBone != null)
            {
                DependsOn(authoring.MotionBone);
                if (!skeletonSet.Contains(authoring.MotionBone))
                    errors.Add("MotionBone must be part of this rig's skeleton.");
            }
            foreach (var bone in skeleton)
                DependsOn(bone);
            for (var i = 0; i < skins.Count; i++)
                RigBuilder.ValidateSkin(skins[i], meshes[i], skeletonSet, errors);
            var cpu = authoring.Engine == Engine.Cpu || authoring.Inertialize || authoring.FootIK || features.Length > 0;
            if (!cpu)
                foreach (var renderer in GetComponentsInChildren<Renderer>())
                    for (var t = renderer.transform; t != null && t != root; t = t.parent)
                        if (skeletonSet.Contains(t))
                        {
                            errors.Add($"renderer '{renderer.name}' sits under bone '{t.name}'. GPU rigs cannot carry rigid attachments; use Engine.Cpu or skin it.");
                            break;
                        }

            if (errors.Count > 0)
            {
                foreach (var error in errors)
                    Debug.LogError($"SA: {authoring.name}: {error}", authoring);
                return;
            }

            var rigId = RigBuilder.RigId(root, skeleton);
            if (!TryGetBlobAssetReference(rigId, out BlobAssetReference<RigBlob> rig))
            {
                rig = RigBuilder.BuildRig(skeleton, rigId);
                AddBlobAssetWithCustomHash(ref rig, rigId);
            }

            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new Rig { Value = rig });
            var clipAssets = AddBuffer<ClipAsset>(entity);
            var humanoid = animator != null && animator.avatar != null && animator.avatar.isHuman;
            foreach (var clip in clips)
            {
                var inPlace = authoring.IsInPlace(clip);
                var id = RigBuilder.ClipKey(root, clip, authoring.SampleRate, rigId, inPlace, authoring.MotionBone);
                var fit = ClipCompatibility.Of(clip.isHumanMotion, humanoid);
                if (fit != ClipFit.Fits)
                    Debug.LogWarning($"SA: {authoring.name}: clip '{clip.name}' {ClipCompatibility.Advice(fit)}", authoring);
                if (!TryGetBlobAssetReference(id, out BlobAssetReference<Clip> asset))
                {
                    var notes = fit == ClipFit.Fits ? new List<string>() : null;
                    asset = RigBuilder.BuildClip(root, skeleton, clip, authoring.SampleRate, rigId, id, inPlace, authoring.MotionBone, notes);
                    if (notes != null)
                        foreach (var note in notes)
                            Debug.LogWarning($"SA: {authoring.name}: clip '{clip.name}' {note}", authoring);
                    AddBlobAssetWithCustomHash(ref asset, id);
                }
                clipAssets.Add(new ClipAsset { Value = asset, NameHash = RigBuilder.Hash(AnimationDiscovery.Name(authoring, clip, clips)), FootIK = authoring.UsesFootIK(clip) ? 1f : 0f });
            }
            var maskAssets = AddBuffer<MaskAsset>(entity);
            for (var m = -1; m < masks.Count; m++)
            {
                var mask = m < 0 ? null : masks[m];
                var id = RigBuilder.MaskKey(mask, rigId);
                if (!TryGetBlobAssetReference(id, out BlobAssetReference<BoneMask> asset))
                {
                    asset = RigBuilder.BuildMask(root, skeleton, mask, rigId, id);
                    AddBlobAssetWithCustomHash(ref asset, id);
                }
                maskAssets.Add(new MaskAsset { Value = asset });
            }
            var upper = RigBuilder.UpperBody(root, skeleton);
            if (upper >= 0 && !masks.Exists(m => m.name == "UpperBody"))
            {
                var id = new Unity.Entities.Hash128(UnityEngine.Hash128.Compute(rigId + ":SA.UpperBody.1").ToString());
                if (!TryGetBlobAssetReference(id, out BlobAssetReference<BoneMask> asset))
                {
                    asset = RigBuilder.BuildUpperMask(skeleton, upper, rigId, id);
                    AddBlobAssetWithCustomHash(ref asset, id);
                }
                maskAssets.Add(new MaskAsset { Value = asset });
            }
            AddComponent(entity, new TimeScale { Value = authoring.TimeScale });
            if (authoring.GetComponents<IMotionClips>().Length > 0)
                AddComponent<MotionDriver>(entity);
            AddComponent(entity, new PoseSlot { Base = -1 });
            var states = AddBuffer<ClipState>(entity);
            if (authoring.Autoplay != null)
                states.Add(new ClipState
                {
                    Clip = new ClipId((ushort)clips.IndexOf(authoring.Autoplay)),
                    Mask = MaskId.Full,
                    Wrap = Wrap.Loop,
                    Speed = 1f,
                    Weight = 1f,
                    Fade = 1f
                });

            var boneEntities = new NativeArray<Entity>(skeleton.Count, Allocator.Temp);
            for (var b = 0; b < skeleton.Count; b++)
                boneEntities[b] = GetEntity(skeleton[b], cpu ? TransformUsageFlags.Dynamic : TransformUsageFlags.None);
            if (cpu)
            {
                AddComponent<CpuEngine>(entity);
                var bones = AddBuffer<BoneEntity>(entity);
                foreach (var bone in boneEntities)
                    bones.Add(new BoneEntity { Value = bone });
                if (authoring.Inertialize)
                {
                    AddComponent<TransitionIntent>(entity);
                    AddBuffer<BoneTransition>(entity).Resize(boneEntities.Length, NativeArrayOptions.ClearMemory);
                }
            }
            var baked = AddBuffer<BakedBone>(entity);
            foreach (var bone in boneEntities)
                baked.Add(new BakedBone { Value = bone });

            var skinEntries = new List<BakedSkin>();
            for (var i = 0; i < skins.Count; i++)
            {
                var skinId = RigBuilder.SkinId(meshes[i], rigId, RigBuilder.MeshFromRig(root, skins[i].transform), skins[i].Bones, root);
                if (!TryGetBlobAssetReference(skinId, out BlobAssetReference<SkinBlob> skin))
                {
                    skin = RigBuilder.BuildSkin(root, skeleton, skins[i], meshes[i], skinId);
                    AddBlobAssetWithCustomHash(ref skin, skinId);
                }
                skinEntries.Add(new BakedSkin
                {
                    Renderer = GetEntity(skins[i], TransformUsageFlags.Dynamic),
                    Skin = skin,
                    Bounds = new AABB { Center = skins[i].LocalBounds.center, Extents = skins[i].LocalBounds.extents }
                });
            }
            var bakedSkins = AddBuffer<BakedSkin>(entity);
            foreach (var entry in skinEntries)
                bakedSkins.Add(entry);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    [UpdateInGroup(typeof(BakingSystemGroup), OrderFirst = true)]
    internal partial class BoneBakingSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var commands = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (bones, entity) in SystemAPI.Query<DynamicBuffer<BakedBone>>().WithEntityAccess().WithOptions(EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities))
            {
                var cpu = EntityManager.HasComponent<CpuEngine>(entity);
                foreach (var bone in bones)
                {
                    if (!EntityManager.Exists(bone.Value))
                        continue;
                    if (cpu)
                        commands.AddComponent<PoseDriven>(bone.Value);
                    else
                        commands.AddComponent<BakingOnlyEntity>(bone.Value);
                }
            }
            commands.Playback(EntityManager);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    [UpdateInGroup(typeof(PostBakingSystemGroup))]
    internal partial class SkinBindingBakingSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var commands = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (skins, entity) in SystemAPI.Query<DynamicBuffer<BakedSkin>>().WithEntityAccess().WithOptions(EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities))
                foreach (var skin in skins)
                {
                    if (!EntityManager.Exists(skin.Renderer))
                        continue;
                    Bind(ref commands, skin.Renderer, skin, entity);
                    if (EntityManager.HasBuffer<AdditionalEntitiesBakingData>(skin.Renderer))
                        foreach (var additional in EntityManager.GetBuffer<AdditionalEntitiesBakingData>(skin.Renderer))
                            Bind(ref commands, additional.Value, skin, entity);
                }
            commands.Playback(EntityManager);
        }

        void Bind(ref EntityCommandBuffer commands, Entity renderer, in BakedSkin skin, Entity rig)
        {
            if (!EntityManager.Exists(renderer) || !EntityManager.HasComponent<MaterialMeshInfo>(renderer))
                return;
            commands.AddComponent(renderer, new Skin { Value = skin.Skin, Rig = rig });
            commands.AddComponent(renderer, new SkinBinding());
            if (EntityManager.HasComponent<RenderBounds>(renderer))
                commands.SetComponent(renderer, new RenderBounds { Value = skin.Bounds });
        }
    }
}
