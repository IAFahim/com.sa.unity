using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;

namespace SA.Editor
{
    internal static class RigBuilder
    {
        public static uint Hash(string name)
        {
            var text = new FixedString64Bytes();
            text.CopyFromTruncated(name);
            return Names.Hash(text);
        }

        public static int FrameCount(AnimationClip clip, float sampleRate) =>
            math.max(2, (int)math.ceil(clip.length * sampleRate) + 1);

        public static BonePose Local(Transform t) => new BonePose { Position = t.localPosition, Rotation = t.localRotation, Scale = t.localScale };

        public static float3x4 Affine(Matrix4x4 m) =>
            new float3x4(new float3(m.m00, m.m10, m.m20), new float3(m.m01, m.m11, m.m21), new float3(m.m02, m.m12, m.m22), new float3(m.m03, m.m13, m.m23));

        public static List<Transform> Skeleton(Transform root, IReadOnlyList<AnimationClip> clips, IReadOnlyList<SkinAuthoring> skins, List<string> errors, IReadOnlyList<Transform> extraBones = null)
        {
            var required = new HashSet<Transform>();
            if (skins.Count == 0)
                foreach (var bone in root.GetComponentsInChildren<Transform>(true))
                    if (bone != root)
                        required.Add(bone);
            foreach (var skin in skins)
                foreach (var bone in skin.Bones)
                    if (bone != null)
                        required.Add(bone);
            if (extraBones != null)
                foreach (var bone in extraBones)
                    if (bone != null)
                        required.Add(bone);
                    else
                        errors.Add("ExtraBones contains a missing transform.");
            var animator = root.GetComponent<Animator>();
            if (animator != null && animator.avatar != null && animator.avatar.isHuman)
                foreach (var human in HumanBones(root, errors))
                    required.Add(human);
            else
                foreach (var clip in clips)
                    if (clip.isHumanMotion)
                    {
                        errors.Add("a humanoid clip needs an Animator with a humanoid Avatar on the rig root.");
                        break;
                    }
            var closure = new HashSet<Transform>();
            foreach (var bone in required)
            {
                if (bone == root || !bone.IsChildOf(root))
                {
                    errors.Add($"bone '{bone.name}' is not below the rig root. Put RigAuthoring on a common parent of every bone.");
                    continue;
                }
                for (var t = bone; t != root; t = t.parent)
                    closure.Add(t);
            }
            foreach (var clip in clips)
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    if (binding.type == typeof(Transform) && binding.path.Length > 0)
                    {
                        var animated = root.Find(binding.path);
                        if (animated != null && !closure.Contains(animated))
                            errors.Add($"clip '{clip.name}' animates '{binding.path}' outside the skin skeleton. Add that transform to RigAuthoring.ExtraBones.");
                    }
            var ordered = new List<Transform>(closure.Count);
            Collect(root, closure, ordered);
            return ordered;
        }

        static void Collect(Transform parent, HashSet<Transform> keep, List<Transform> into)
        {
            var children = new List<Transform>();
            for (var i = 0; i < parent.childCount; i++)
                if (keep.Contains(parent.GetChild(i)))
                    children.Add(parent.GetChild(i));
            children.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            foreach (var child in children)
            {
                into.Add(child);
                Collect(child, keep, into);
            }
        }

        static IEnumerable<Transform> HumanBones(Transform root, List<string> errors)
        {
            var animator = root.GetComponent<Animator>();
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
            {
                errors.Add("a humanoid clip needs an Animator with a humanoid Avatar on the rig root.");
                yield break;
            }
            foreach (var human in animator.avatar.humanDescription.human)
            {
                var bone = Find(root, human.boneName);
                if (bone != null)
                    yield return bone;
            }
        }

        static Transform Find(Transform parent, string name)
        {
            if (parent.name == name)
                return parent;
            for (var i = 0; i < parent.childCount; i++)
            {
                var found = Find(parent.GetChild(i), name);
                if (found != null)
                    return found;
            }
            return null;
        }

        public static unsafe Hash128 RigId(Transform root, List<Transform> skeleton)
        {
            var hasher = new xxHash3.StreamingState(false);
            Feed(ref hasher, "SA.Skeleton.3");
            foreach (var bone in skeleton)
            {
                Feed(ref hasher, AnimationUtility.CalculateTransformPath(bone, root));
                var pose = Local(bone);
                hasher.Update(&pose, sizeof(BonePose));
            }
            return new Hash128(hasher.DigestHash128());
        }

        public static unsafe Hash128 ClipKey(Transform root, AnimationClip clip, float sampleRate, Hash128 rigId)
        {
            var hasher = new xxHash3.StreamingState(false);
            Feed(ref hasher, "SA.Clip.3");
            hasher.Update(&rigId, sizeof(Hash128));
            hasher.Update(&sampleRate, sizeof(float));
            FeedClip(ref hasher, clip);
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            var referenceTime = settings.additiveReferencePoseTime;
            var hasReference = settings.hasAdditiveReferencePose;
            hasher.Update(&hasReference, sizeof(bool));
            hasher.Update(&referenceTime, sizeof(float));
            FeedClip(ref hasher, settings.additiveReferencePoseClip);
            var animator = root.GetComponent<Animator>();
            Feed(ref hasher, AssetKey(animator != null ? animator.avatar : null));
            if (clip.isHumanMotion || (settings.additiveReferencePoseClip != null && settings.additiveReferencePoseClip.isHumanMotion))
            {
                var scale = root.lossyScale;
                hasher.Update(&scale, sizeof(Vector3));
                var applyRootMotion = animator != null && animator.applyRootMotion;
                hasher.Update(&applyRootMotion, sizeof(bool));
            }
            return new Hash128(hasher.DigestHash128());
        }

        public static unsafe Hash128 MaskKey(AvatarMask mask, Hash128 rigId)
        {
            var hasher = new xxHash3.StreamingState(false);
            Feed(ref hasher, "SA.Mask.3");
            hasher.Update(&rigId, sizeof(Hash128));
            Feed(ref hasher, AssetKey(mask));
            if (mask != null)
                for (var i = 0; i < mask.transformCount; i++)
                {
                    Feed(ref hasher, mask.GetTransformPath(i));
                    var active = mask.GetTransformActive(i);
                    hasher.Update(&active, sizeof(bool));
                }
            return new Hash128(hasher.DigestHash128());
        }

        public static unsafe Hash128 SkinId(Mesh mesh, Hash128 rigId, float3x4 meshFromRig, Transform[] bones, Transform root)
        {
            var hasher = new xxHash3.StreamingState(false);
            Feed(ref hasher, "SA.Skin.2");
            Feed(ref hasher, AssetKey(mesh));
            hasher.Update(&rigId, sizeof(Hash128));
            hasher.Update(&meshFromRig, sizeof(float3x4));
            foreach (var bone in bones)
                Feed(ref hasher, bone != null ? AnimationUtility.CalculateTransformPath(bone, root) : "null");
            return new Hash128(hasher.DigestHash128());
        }

        static string AssetKey(UnityEngine.Object asset)
        {
            if (asset == null)
                return "null";
            var path = AssetDatabase.GetAssetPath(asset);
            if (!string.IsNullOrEmpty(path) && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localId))
                return $"{guid}:{localId}:{asset.name}:{AssetDatabase.GetAssetDependencyHash(path)}";
            return $"{asset.GetHashCode()}:{asset.name}:{EditorJsonUtility.ToJson(asset)}";
        }

        static unsafe void FeedClip(ref xxHash3.StreamingState hasher, AnimationClip clip)
        {
            Feed(ref hasher, AssetKey(clip));
            if (clip == null)
                return;
            var metadata = new float4(clip.length, clip.frameRate, clip.legacy ? 1f : 0f, clip.isLooping ? 1f : 0f);
            hasher.Update(&metadata, sizeof(float4));
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                Feed(ref hasher, binding.path);
                Feed(ref hasher, binding.propertyName);
                Feed(ref hasher, binding.type.FullName);
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                var wrap = new int2((int)curve.preWrapMode, (int)curve.postWrapMode);
                hasher.Update(&wrap, sizeof(int2));
                var keys = curve.keys;
                fixed (Keyframe* data = keys)
                    hasher.Update(data, keys.Length * sizeof(Keyframe));
            }
        }

        static unsafe void Feed(ref xxHash3.StreamingState hasher, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text + "\n");
            fixed (byte* data = bytes)
                hasher.Update(data, bytes.Length);
        }

        public static BlobAssetReference<RigBlob> BuildRig(List<Transform> skeleton, Hash128 id)
        {
            using var builder = new BlobBuilder(Allocator.Temp);
            ref var rig = ref builder.ConstructRoot<RigBlob>();
            rig.Id = id;
            var index = new Dictionary<Transform, int>(skeleton.Count);
            for (var i = 0; i < skeleton.Count; i++)
                index[skeleton[i]] = i;
            var bones = builder.Allocate(ref rig.Bones, skeleton.Count);
            for (var i = 0; i < skeleton.Count; i++)
                bones[i] = new Bone { Parent = index.TryGetValue(skeleton[i].parent, out var parent) ? parent : -1, Rest = Local(skeleton[i]), NameHash = Hash(skeleton[i].name) };
            return builder.CreateBlobAssetReference<RigBlob>(Allocator.Persistent);
        }

        public static BlobAssetReference<Clip> BuildClip(Transform root, List<Transform> skeleton, AnimationClip clip, float sampleRate, Hash128 rigId, Hash128 id)
        {
            using var builder = new BlobBuilder(Allocator.Temp);
            ref var target = ref builder.ConstructRoot<Clip>();
            target.Id = id;
            target.RigId = rigId;
            target.NameHash = Hash(clip.name);
            target.Duration = clip.length;
            target.FrameCount = FrameCount(clip, sampleRate);
            var frames = target.FrameCount;
            var boneCount = skeleton.Count;
            var samples = builder.Allocate(ref target.Samples, frames * boneCount);
            var reference = builder.Allocate(ref target.Reference, boneCount);
            var copy = UnityEngine.Object.Instantiate(root.gameObject);
            copy.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                var all = copy.GetComponentsInChildren<Transform>(true);
                var rest = new BonePose[all.Length];
                for (var i = 0; i < all.Length; i++)
                    rest[i] = Local(all[i]);
                var mapped = new Transform[boneCount];
                for (var b = 0; b < boneCount; b++)
                    mapped[b] = copy.transform.Find(AnimationUtility.CalculateTransformPath(skeleton[b], root));
                for (var f = 0; f < frames; f++)
                {
                    Restore(all, rest);
                    clip.SampleAnimation(copy, clip.length * f / (frames - 1));
                    for (var b = 0; b < boneCount; b++)
                    {
                        var pose = mapped[b] != null ? Local(mapped[b]) : Local(skeleton[b]);
                        if (f > 0 && math.dot(pose.Rotation.value, samples[(f - 1) * boneCount + b].Rotation.value) < 0f)
                            pose.Rotation = new quaternion(-pose.Rotation.value);
                        samples[f * boneCount + b] = pose;
                    }
                }
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                if (settings.hasAdditiveReferencePose && settings.additiveReferencePoseClip != null)
                {
                    Restore(all, rest);
                    settings.additiveReferencePoseClip.SampleAnimation(copy, settings.additiveReferencePoseTime);
                    for (var b = 0; b < boneCount; b++)
                        reference[b] = mapped[b] != null ? Local(mapped[b]) : Local(skeleton[b]);
                }
                else
                    for (var b = 0; b < boneCount; b++)
                        reference[b] = samples[b];
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copy);
            }
            return builder.CreateBlobAssetReference<Clip>(Allocator.Persistent);
        }

        public static BlobAssetReference<BoneMask> BuildMask(Transform root, List<Transform> skeleton, AvatarMask mask, Hash128 rigId, Hash128 id)
        {
            using var builder = new BlobBuilder(Allocator.Temp);
            ref var target = ref builder.ConstructRoot<BoneMask>();
            target.Id = id;
            target.RigId = rigId;
            target.NameHash = mask != null ? Hash(mask.name) : 0;
            var weights = builder.Allocate(ref target.Weights, skeleton.Count);
            var active = new Dictionary<string, bool>();
            if (mask != null)
                for (var i = 0; i < mask.transformCount; i++)
                    active[mask.GetTransformPath(i)] = mask.GetTransformActive(i);
            for (var b = 0; b < skeleton.Count; b++)
                weights[b] = MaskWeight(root, skeleton[b], active);
            return builder.CreateBlobAssetReference<BoneMask>(Allocator.Persistent);
        }

        public static float MaskWeight(Transform root, Transform bone, Dictionary<string, bool> active)
        {
            for (var t = bone; t != null; t = t.parent)
            {
                var path = t == root ? "" : AnimationUtility.CalculateTransformPath(t, root);
                if (active.TryGetValue(path, out var included))
                    return included ? 1f : 0f;
                if (t == root)
                    break;
            }
            return 1f;
        }

        static void Restore(Transform[] all, BonePose[] rest)
        {
            for (var i = 0; i < all.Length; i++)
            {
                all[i].localPosition = rest[i].Position;
                all[i].localRotation = rest[i].Rotation;
                all[i].localScale = rest[i].Scale;
            }
        }

        public static float3x4 MeshFromRig(Transform root, Transform mesh) =>
            Affine((root.worldToLocalMatrix * mesh.localToWorldMatrix).inverse);

        public static void ValidateSkin(SkinAuthoring skin, Mesh mesh, HashSet<Transform> skeleton, List<string> errors)
        {
            var name = skin.name;
            if (mesh == null)
            {
                errors.Add($"skin '{name}' has no mesh in its MeshFilter.");
                return;
            }
            if (mesh.GetBonesPerVertex().Length == 0)
                errors.Add($"mesh '{mesh.name}' on '{name}' has no bone weights.");
            if (skin.Bones.Length != mesh.bindposes.Length)
                errors.Add($"skin '{name}' has {skin.Bones.Length} bones but mesh '{mesh.name}' has {mesh.bindposes.Length} bindposes. Re-run Tools/SA/Convert Skinned Mesh Renderers.");
            if (skin.Bones.Length > ushort.MaxValue)
                errors.Add($"skin '{name}' has more than {ushort.MaxValue} bones.");
            foreach (var bone in skin.Bones)
                if (bone == null || !skeleton.Contains(bone))
                    errors.Add($"skin '{name}' references a bone that is missing or outside the rig.");
            for (var i = 0; i < mesh.subMeshCount; i++)
                if (mesh.GetSubMesh(i).baseVertex != 0)
                    errors.Add($"mesh '{mesh.name}' submesh {i} has baseVertex {mesh.GetSubMesh(i).baseVertex}; SA skins by vertex index and needs 0.");
        }

        public static uint4 Pack(ReadOnlySpan<BoneWeight1> weights)
        {
            var count = math.min(4, weights.Length);
            var indices = uint4.zero;
            var amounts = float4.zero;
            for (var i = 0; i < count; i++)
            {
                indices[i] = (uint)weights[i].boneIndex;
                amounts[i] = math.max(0f, weights[i].weight);
            }
            var sum = math.csum(amounts);
            var quantized = sum > 0f ? (uint4)math.round(amounts / sum * 65535f) : new uint4(65535, 0, 0, 0);
            quantized.x += 65535u - math.csum(quantized);
            return new uint4(indices.x | indices.y << 16, indices.z | indices.w << 16, quantized.x | quantized.y << 16, quantized.z | quantized.w << 16);
        }

        public static BlobAssetReference<SkinBlob> BuildSkin(Transform root, List<Transform> skeleton, SkinAuthoring skin, Mesh mesh, Hash128 id)
        {
            var index = new Dictionary<Transform, int>(skeleton.Count);
            for (var i = 0; i < skeleton.Count; i++)
                index[skeleton[i]] = i;
            var builder = new BlobBuilder(Allocator.Temp);
            ref var blob = ref builder.ConstructRoot<SkinBlob>();
            blob.Id = id;
            blob.MeshFromRig = MeshFromRig(root, skin.transform);
            var bones = builder.Allocate(ref blob.Bones, skin.Bones.Length);
            var bindposes = builder.Allocate(ref blob.Bindposes, skin.Bones.Length);
            var meshBindposes = mesh.bindposes;
            for (var i = 0; i < skin.Bones.Length; i++)
            {
                bones[i] = index[skin.Bones[i]];
                bindposes[i] = Affine(meshBindposes[i]);
            }
            var perVertex = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            var influences = builder.Allocate(ref blob.Influences, perVertex.Length);
            var offset = 0;
            for (var v = 0; v < perVertex.Length; v++)
            {
                influences[v] = Pack(weights.AsReadOnlySpan().Slice(offset, perVertex[v]));
                offset += perVertex[v];
            }
            var result = builder.CreateBlobAssetReference<SkinBlob>(Allocator.Persistent);
            builder.Dispose();
            return result;
        }
    }
}
