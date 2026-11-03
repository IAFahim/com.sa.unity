using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using SA.Shape.Unity;
using SA.Editor;
using SA.Motion.Numerics;
using Unity.Mathematics;
using SA.Motion.Unity;

namespace SA.Shape.Unity.Editor
{
    public class ShapeBaker : Baker<ShapeAuthoring>
    {
        public override void Bake(ShapeAuthoring authoring)
        {
            var family = authoring.Family != null ? authoring.Family : GetComponent<SharedFamilyAuthoring>();
            if (family == null)
            {
                Debug.LogError("SA: shape '" + authoring.name + "': no SharedFamilyAuthoring found. Fix: add it to the same GameObject or assign Family, then rebake.", authoring);
                return;
            }
            if (authoring.transform != family.transform)
                Debug.LogWarning("SA: shape '" + authoring.name + "' sits apart from its family. Fix: move it to the family GameObject, or the baked shape stays inert.", authoring);
            var rig = family.Rig != null ? family.Rig : GetComponent<RigAuthoring>();
            if (rig == null)
            {
                Debug.LogError("SA: shape '" + authoring.name + "': no RigAuthoring found. Fix: add RigAuthoring to the same GameObject, then rebake.", authoring);
                return;
            }
            if (authoring.Definition == null || authoring.Definition.Source == null)
            {
                Debug.LogError("SA: shape '" + authoring.name + "': no shape definition assigned. Fix: assign a .sashape TextAsset on a ShapeDefinitionAsset, then rebake.", authoring);
                return;
            }
            DependsOn(authoring.Definition.Source);
            if (!ShapeRigContext.Describe(rig, family, out var boneNames, out var calibration, out var target, out var contextError))
            {
                Debug.LogError("SA: shape '" + authoring.name + "': " + contextError + " Fix the rig, then rebake.", authoring);
                return;
            }
            var baseOffsets = new Float3[calibration.Length];
            for (var b = 0; b < calibration.Length; b++)
                baseOffsets[b] = calibration[b].Position;
            if (!ShapeInspectorModel.Describe(authoring.Definition.Source, boneNames, MeshMorphNames(rig), target, baseOffsets,
                authoring.Requests != null ? authoring.Requests.ToArray() : System.Array.Empty<ShapeRequest>(),
                out var asset, out _, out var warning, out var error))
            {
                Debug.LogError("SA: shape '" + authoring.name + "': " + error, authoring);
                return;
            }
            if (asset == null)
            {
                Debug.LogError("SA: shape '" + authoring.name + "': shape definition failed to load. Fix the .sashape source, then rebake.", authoring);
                return;
            }
            if (!string.IsNullOrEmpty(warning))
                Debug.LogWarning("SA: shape '" + authoring.name + "': " + warning, authoring);
            var builder = new BlobBuilder(Allocator.Temp);
            ref var root = ref builder.ConstructRoot<ShapeDefinitionData>();
            root.Definition = asset.Definition;
            root.Target = asset.Target;
            root.Grounding = asset.Grounding;
            root.SupportA = asset.SupportA;
            root.SupportB = asset.SupportB;
            root.MinimumLength = asset.MinimumLength;
            root.MaximumLength = asset.MaximumLength;
            root.MinimumSkinScale = asset.MinimumSkinScale;
            root.MaximumSkinScale = asset.MaximumSkinScale;
            var channels = builder.Allocate(ref root.Channels, asset.Channels.Length);
            for (var i = 0; i < channels.Length; i++)
                channels[i] = asset.Channels[i];
            var offsets = builder.Allocate(ref root.Offsets, asset.Offsets.Length);
            for (var i = 0; i < offsets.Length; i++)
                offsets[i] = asset.Offsets[i];
            var skin = builder.Allocate(ref root.Skin, asset.Skin.Length);
            for (var i = 0; i < skin.Length; i++)
                skin[i] = asset.Skin[i];
            var frames = builder.Allocate(ref root.Frames, asset.Frames.Length);
            for (var i = 0; i < frames.Length; i++)
                frames[i] = asset.Frames[i];
            var morphs = builder.Allocate(ref root.Morphs, asset.Morphs.Length);
            for (var i = 0; i < morphs.Length; i++)
                morphs[i] = asset.Morphs[i];
            var morphEffects = builder.Allocate(ref root.MorphEffects, asset.MorphEffects.Length);
            for (var i = 0; i < morphEffects.Length; i++)
                morphEffects[i] = asset.MorphEffects[i];
            var blob = builder.CreateBlobAssetReference<ShapeDefinitionData>(Allocator.Persistent);
            builder.Dispose();
            AddBlobAsset(ref blob, out _);
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new ShapeDriven());
            AddComponent(entity, new ShapeDefinitionRef { Value = blob });
            AddComponent(entity, new ShapeState { Revision = 0 });
            AddComponent(entity, new ShapeContactState());
            var states = AddBuffer<ShapeChannelState>(entity);
            foreach (var channel in asset.Channels)
            {
                var requested = channel.Default;
                if (authoring.Requests != null)
                    foreach (var saved in authoring.Requests)
                        if (saved.Id == channel.Id)
                        {
                            requested = saved.Value;
                            break;
                        }
                if (requested < channel.Minimum)
                    requested = channel.Minimum;
                if (requested > channel.Maximum)
                    requested = channel.Maximum;
                states.Add(new ShapeChannelState { Id = channel.Id, Requested = requested, Value = channel.Default, Velocity = 0f });
            }
            AddBuffer<ShapeSkinDeformation>(entity);
            var weights = AddBuffer<ShapeMorphWeight>(entity);
            for (var m = 0; m < asset.Morphs.Length; m++)
                weights.Add(new ShapeMorphWeight { Value = asset.Morphs[m].Base });
            var bindings = AddBuffer<ShapeMorphBinding>(entity);
            foreach (var name in asset.MorphNames)
                bindings.Add(new ShapeMorphBinding { Name = name });
            AddComponent(entity, new ShapePhysical { LivePhysical = authoring.LivePhysical ? (byte)1 : (byte)0, MassPolicy = (byte)authoring.MassPolicy });
            var rest = AddBuffer<ShapePhysicalRest>(entity);
            for (var b = 0; b < calibration.Length; b++)
            {
                var offset = Conversions.ToUnity(calibration[b].Position);
                var length = math.length(offset);
                var direction = length > 1e-9f ? math.normalize(math.rotate(math.inverse(Conversions.ToUnity(calibration[b].Rotation)), -offset)) : float3.zero;
                rest.Add(new ShapePhysicalRest { RestOffset = offset, RestDirection = direction, RestLength = length, RestRadius = ShapePhysicalSync.RestRadius(length) });
            }
            AddBuffer<ShapePhysicalBody>(entity);
            AddBuffer<ShapePhysicalJoint>(entity);
            if (!ShapePhysicalSync.ValidateWardrobe(authoring.Wardrobe != null ? authoring.Wardrobe.ToArray() : System.Array.Empty<string>(), asset.MorphNames, MeshMorphNames(rig), out var wardrobeError))
                Debug.LogWarning("SA: shape '" + authoring.name + "': " + wardrobeError, authoring);
        }

        public static string[] MeshMorphNames(RigAuthoring rig)
        {
            var names = new List<string>();
            if (rig == null)
                return names.ToArray();
            foreach (var skin in rig.GetComponentsInChildren<SkinAuthoring>(true))
            {
                if (skin == null || (skin.Rig != rig && skin.Rig != null))
                    continue;
                var filter = skin.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                    continue;
                for (var m = 0; m < mesh.blendShapeCount; m++)
                {
                    var morph = mesh.GetBlendShapeName(m);
                    if (!string.IsNullOrEmpty(morph) && !names.Contains(morph))
                        names.Add(morph);
                }
            }
            return names.ToArray();
        }
    }
}
