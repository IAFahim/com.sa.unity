using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace SA.Shape.Unity
{
    public struct ShapeMorphRenderer : IComponentData
    {
        public UnityObjectRef<SkinnedMeshRenderer> Renderer;
    }

    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateBefore(typeof(global::Unity.Rendering.EntitiesGraphicsSystem))]
    public partial class ShapeMorphUploadSystem : SystemBase
    {
        public static string LastDiagnostic { get; private set; } = string.Empty;
        public static int LastUploaded { get; private set; }
        public static int LastMissing { get; private set; }
        public static int LastUnmapped { get; private set; }
        public static int LastUnbound { get; private set; }
        public static int TrackedRigCount { get; private set; }

        sealed class ResolvedBody
        {
            public SkinnedMeshRenderer Renderer;
            public int[] Indices = Array.Empty<int>();
            public FixedString64Bytes[] Names = Array.Empty<FixedString64Bytes>();
        }

        EntityQuery uploads;
        readonly Dictionary<Entity, ResolvedBody> bodies = new Dictionary<Entity, ResolvedBody>();
        readonly HashSet<string> warned = new HashSet<string>();

        protected override void OnCreate()
        {
            uploads = GetEntityQuery(
                ComponentType.ReadOnly<ShapeDriven>(),
                ComponentType.ReadOnly<ShapeMorphWeight>(),
                ComponentType.ReadOnly<ShapeMorphBinding>(),
                ComponentType.ReadOnly<ShapeMorphRenderer>());
        }

        protected override void OnDestroy()
        {
            bodies.Clear();
        }

        void WarnOnce(string key, string message)
        {
            if (warned.Add(key))
            {
                LastDiagnostic = message;
                Debug.LogWarning(message);
            }
        }

        protected override void OnUpdate()
        {
            LastUploaded = 0;
            LastMissing = 0;
            LastUnmapped = 0;
            using var entities = uploads.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
                UploadRig(entity);
            var dead = Entity.Null;
            foreach (var pair in bodies)
                if (!EntityManager.Exists(pair.Key) || !EntityManager.HasComponent<ShapeDriven>(pair.Key) || !EntityManager.HasComponent<ShapeMorphRenderer>(pair.Key))
                {
                    dead = pair.Key;
                    break;
                }
            var unbound = 0;
            if (dead != Entity.Null)
            {
                bodies.Remove(dead);
                unbound = 1;
            }
            LastUnbound = unbound;
            TrackedRigCount = bodies.Count;
        }

        void UploadRig(Entity entity)
        {
            if (!EntityManager.HasComponent<ShapeMorphRenderer>(entity))
                return;
            var link = EntityManager.GetComponentData<ShapeMorphRenderer>(entity);
            var renderer = link.Renderer.Value;
            if (renderer == null)
            {
                bodies.Remove(entity);
                LastMissing++;
                WarnOnce("norenderer" + entity.Index + "v" + entity.Version, "SA: shape morph upload has no body renderer. Fix: assign Body on ShapeMorphRendererAuthoring, then rebake.");
                return;
            }
            var weights = EntityManager.GetBuffer<ShapeMorphWeight>(entity);
            var bindings = EntityManager.GetBuffer<ShapeMorphBinding>(entity);
            if (!bodies.TryGetValue(entity, out var body) || !ReferenceEquals(body.Renderer, renderer) || body.Names.Length != bindings.Length || NamesChanged(body.Names, bindings))
            {
                body = Resolve(renderer, bindings);
                bodies[entity] = body;
            }
            var unmapped = 0;
            for (var m = 0; m < body.Indices.Length; m++)
                if (body.Indices[m] < 0)
                    unmapped++;
            if (unmapped > 0)
            {
                LastUnmapped += unmapped;
                WarnOnce("unmapped" + entity.Index + "v" + entity.Version, DescribeUnmapped(renderer, body));
            }
            var count = weights.Length < body.Indices.Length ? weights.Length : body.Indices.Length;
            for (var m = 0; m < count; m++)
            {
                var index = body.Indices[m];
                if (index < 0)
                    continue;
                renderer.SetBlendShapeWeight(index, weights[m].Value);
            }
            LastUploaded++;
        }

        static ResolvedBody Resolve(SkinnedMeshRenderer renderer, DynamicBuffer<ShapeMorphBinding> bindings)
        {
            var body = new ResolvedBody
            {
                Renderer = renderer,
                Indices = new int[bindings.Length],
                Names = new FixedString64Bytes[bindings.Length]
            };
            var mesh = renderer.sharedMesh;
            for (var m = 0; m < bindings.Length; m++)
            {
                body.Names[m] = bindings[m].Name;
                body.Indices[m] = mesh == null ? -1 : mesh.GetBlendShapeIndex(bindings[m].Name.ToString());
            }
            return body;
        }

        static bool NamesChanged(FixedString64Bytes[] cached, DynamicBuffer<ShapeMorphBinding> bindings)
        {
            for (var m = 0; m < cached.Length; m++)
                if (!cached[m].Equals(bindings[m].Name))
                    return true;
            return false;
        }

        static string DescribeUnmapped(SkinnedMeshRenderer renderer, ResolvedBody body)
        {
            var mesh = renderer.sharedMesh;
            var meshName = mesh != null ? mesh.name : "<missing>";
            var missing = string.Empty;
            for (var m = 0; m < body.Indices.Length; m++)
                if (body.Indices[m] < 0)
                    missing += (missing.Length == 0 ? string.Empty : ", ") + body.Names[m].ToString();
            return "SA: shape body mesh '" + meshName + "' has no blendshape for " + missing + ". Fix: add the blendshape to the mesh or drop the name from the wardrobe, then rebake.";
        }
    }
}
