using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using SA;
using SA.Animation;
 using SA.Motion.Mechanics;
using SA.Motion.Numerics;
using SA.Motion.Unity;

namespace SA.Shape.Unity
{
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateAfter(typeof(SharedFamilyPaletteSystem))]
    [UpdateBefore(typeof(global::Unity.Rendering.EntitiesGraphicsSystem))]
    public partial class ShapeSkinPaletteSystem : SystemBase
    {
        const int MaxBones = 512;

        EntityQuery skinQuery;
        NativeArray<RigidPose> model;
        NativeArray<Affine> affineModel;
        NativeArray<Affine> deformations;
        NativeArray<Affine> bindMatrices;
        NativeArray<Affine> palette;
        NativeArray<Float3x3> normals;
        NativeArray<float4x4> upload;
        NativeArray<Pose> localPose;
        NativeArray<int> parents;

        protected override void OnCreate()
        {
            skinQuery = GetEntityQuery(ComponentType.ReadOnly<Skin>(), ComponentType.ReadOnly<SkinBinding>());
            model = new NativeArray<RigidPose>(MaxBones, Allocator.Persistent);
            affineModel = new NativeArray<Affine>(MaxBones, Allocator.Persistent);
            deformations = new NativeArray<Affine>(MaxBones, Allocator.Persistent);
            bindMatrices = new NativeArray<Affine>(MaxBones, Allocator.Persistent);
            palette = new NativeArray<Affine>(MaxBones, Allocator.Persistent);
            normals = new NativeArray<Float3x3>(MaxBones, Allocator.Persistent);
            upload = new NativeArray<float4x4>(MaxBones, Allocator.Persistent);
            localPose = new NativeArray<Pose>(MaxBones, Allocator.Persistent);
            parents = new NativeArray<int>(MaxBones, Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            model.Dispose();
            affineModel.Dispose();
            deformations.Dispose();
            bindMatrices.Dispose();
            palette.Dispose();
            normals.Dispose();
            upload.Dispose();
            localPose.Dispose();
            parents.Dispose();
        }

        protected override void OnUpdate()
        {
            var skinning = World.GetExistingSystemManaged<SkinningSystem>();
            if (skinning == null)
                return;
            var matrices = skinning.SkinMatrixBuffer;
            if (matrices == null)
                return;
            var skins = skinQuery.ToEntityArray(Allocator.Temp);
            foreach (var skinEntity in skins)
                RouteShapeSkin(skinEntity, matrices);
            skins.Dispose();
        }

        unsafe void RouteShapeSkin(Entity skinEntity, UnityEngine.GraphicsBuffer matrices)
        {
            var skin = EntityManager.GetComponentData<Skin>(skinEntity);
            if (!skin.Value.IsCreated || !EntityManager.HasComponent<ShapeDriven>(skin.Rig))
                return;
            if (!EntityManager.HasBuffer<BoneEntity>(skin.Rig) || !EntityManager.HasBuffer<ShapeSkinDeformation>(skin.Rig))
                return;
            ref var blob = ref skin.Value.Value;
            var boneCount = blob.Bones.Length;
            if (boneCount <= 0 || boneCount > MaxBones)
                return;
            var rigBones = EntityManager.GetBuffer<BoneEntity>(skin.Rig);
            var shapeDeforms = EntityManager.GetBuffer<ShapeSkinDeformation>(skin.Rig);
            if (rigBones.Length <= 0 || rigBones.Length > MaxBones || shapeDeforms.Length != rigBones.Length)
                return;
            var rigReference = EntityManager.GetComponentData<Rig>(skin.Rig);
            if (!rigReference.Value.IsCreated || rigReference.Value.Value.Bones.Length != rigBones.Length)
                return;
            for (var b = 0; b < rigBones.Length; b++)
            {
                if (!EntityManager.HasComponent<LocalTransform>(rigBones[b].Value))
                    return;
                var transform = EntityManager.GetComponentData<LocalTransform>(rigBones[b].Value);
                localPose[b] = new Pose
                {
                    Position = Conversions.ToMotion(transform.Position),
                    Rotation = Conversions.ToMotion(transform.Rotation),
                    Scale = Float3.One
                };
                parents[b] = rigReference.Value.Value.Bones[b].Parent;
                deformations[b] = shapeDeforms[b].Value;
            }
            if (RigidMath.Model(parents.GetSubArray(0, rigBones.Length).AsSpan(), localPose.GetSubArray(0, rigBones.Length).AsSpan(), model.GetSubArray(0, rigBones.Length).AsSpan()) != AnimationError.None)
                return;
            for (var b = 0; b < boneCount; b++)
                bindMatrices[b] = ToAffine(blob.Bindposes[b]);
            var paletteToRig = new ReadOnlySpan<int>(blob.Bones.GetUnsafePtr(), boneCount);
            var build = ShapeSkinUpload.BuildPalette(ToAffine(blob.MeshFromRig),
                model.GetSubArray(0, rigBones.Length).AsSpan(), deformations.GetSubArray(0, rigBones.Length).AsSpan(),
                paletteToRig, bindMatrices.GetSubArray(0, boneCount).AsSpan(), affineModel.GetSubArray(0, rigBones.Length).AsSpan(),
                palette.GetSubArray(0, boneCount).AsSpan(), normals.GetSubArray(0, boneCount).AsSpan());
            if (build != AnimationError.None)
                return;
            for (var b = 0; b < boneCount; b++)
            {
                var joint = palette[b];
                upload[b] = new float4x4(new float4(Conversions.ToUnity(joint.C0), 0f), new float4(Conversions.ToUnity(joint.C1), 0f), new float4(Conversions.ToUnity(joint.C2), 0f), new float4(Conversions.ToUnity(joint.C3), 1f));
            }
            var binding = EntityManager.GetComponentData<SkinBinding>(skinEntity);
            matrices.SetData(upload, 0, (int)binding.MatrixBase, boneCount);
        }

        static Affine ToAffine(in float3x4 matrix)
        {
            return new Affine(
                Conversions.ToMotion(matrix.c0), Conversions.ToMotion(matrix.c1),
                Conversions.ToMotion(matrix.c2), Conversions.ToMotion(matrix.c3));
        }
    }
}
