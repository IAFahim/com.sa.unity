using NUnit.Framework;
using SA.Animation;
using SA.Motion.Numerics;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace SA.Tests
{
    public class SkinningShaderTests
    {
        [Test]
        public void SkinningSystemInitializesTheSentinelBeforeAnySkinIsLoaded()
        {
            TestRigs.RequireGpu();
            using var world = new global::Unity.Entities.World("SA.ShaderStartup");
            var system = world.GetOrCreateSystemManaged<SkinningSystem>();
            var matrices = new float4x4[1];
            system.SkinMatrixBuffer.GetData(matrices);
            Assert.AreEqual(float4x4.identity, matrices[0]);
            var influences = new uint4[1];
            system.Registry.InfluenceBuffer.Buffer.GetData(influences);
            Assert.AreEqual(uint4.zero, influences[0]);
        }

        static float4[] Dispatch(float4x4[] palette, uint4 packed, float3 position, float3 normal, float3 tangent, uint matrixBase = 1)
        {
            TestRigs.RequireGpu();
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.sa.unity/Tests/Editor/SkinningProof.compute");
            Assert.IsNotNull(shader);
            var kernel = shader.FindKernel("CheckVertex");
            using var matrices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, palette.Length, 64);
            using var influences = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
            using var output = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 3, 16);
            matrices.SetData(palette);
            influences.SetData(new[] { packed });
            shader.SetBuffer(kernel, "_SaSkinMatrices", matrices);
            shader.SetBuffer(kernel, "_SaInfluences", influences);
            shader.SetBuffer(kernel, "_TestOutput", output);
            shader.SetVector("_TestPosition", new Vector4(position.x, position.y, position.z, 1));
            shader.SetVector("_TestNormal", new Vector4(normal.x, normal.y, normal.z, 0));
            shader.SetVector("_TestTangent", new Vector4(tangent.x, tangent.y, tangent.z, 0));
            shader.SetVector("_TestSkin", new Vector4(math.asfloat(matrixBase), 0, 0, 0));
            shader.Dispatch(kernel, 1, 1, 1);
            var values = new float4[3];
            output.GetData(values);
            return values;
        }

        [Test]
        public void NonuniformBlendedShaderMatchesPositionNormalAndTangentReference()
        {
            var a = float4x4.TRS(new float3(.1f, -.3f, .2f), quaternion.EulerXYZ(.2f, -.3f, .4f), new float3(2f, .5f, 1.2f));
            var b = float4x4.TRS(new float3(-.4f, .1f, .3f), quaternion.EulerXYZ(-.1f, .4f, -.2f), new float3(.7f, 1.8f, .4f));
            var palette = new[] { float4x4.identity, a, b };
            var weightA = 16384f / 65535f;
            var weightB = 49151f / 65535f;
            var packed = new uint4(1u << 16, 0, 16384u | 49151u << 16, 0);
            var position = new float3(.3f, -.2f, .5f);
            var normal = math.normalize(new float3(1, 2, 3));
            var tangent = math.normalize(math.cross(normal, new float3(0, 1, 0)));
            var actual = Dispatch(palette, packed, position, normal, tangent);
            Assert.AreEqual(AnimationError.None, SA.Shape.ShapeSkinPalette.Blend(new[] { AffineOf(a), AffineOf(b) }, new[] { 0, 1 }, new[] { weightA, weightB }, out var matrix, out var normals));
            var expectedPosition = Point(matrix, position);
            var expectedNormal = math.normalize(Unity(normals.Multiply(Motion(normal))));
            var expectedTangent = Vector(matrix, tangent);
            expectedTangent = math.normalize(expectedTangent - expectedNormal * math.dot(expectedNormal, expectedTangent));
            Near(actual[0].xyz, expectedPosition);
            Near(actual[1].xyz, expectedNormal);
            Near(actual[2].xyz, expectedTangent);
            Assert.Less(math.abs(math.dot(actual[1].xyz, actual[2].xyz)), 1e-5f);
            Assert.IsTrue(SA.Shape.Unity.ShapeSkinUpload.FinishTangent(matrix, Motion(tangent), -1, Motion(expectedNormal), out var cpuTangent, out var handedness));
            Near(Unity(cpuTangent), expectedTangent);
            Assert.AreEqual(-1, handedness);
        }

        [Test]
        public void SingularPaletteKeepsFiniteUnitNormalAndTangent()
        {
            var singular = float4x4.Scale(new float3(0, 0, 1));
            var actual = Dispatch(new[] { float4x4.identity, singular }, new uint4(0, 0, 65535, 0),
                new float3(1, 2, 3), new float3(0, 1, 0), new float3(1, 0, 0));
            foreach (var value in actual) Assert.IsTrue(math.all(math.isfinite(value)));
            Assert.AreEqual(1f, math.length(actual[1].xyz), 1e-5f);
            Assert.AreEqual(1f, math.length(actual[2].xyz), 1e-5f);
        }

        [Test]
        public void ZeroBindingLeavesAnUnskinnedVertexUnchanged()
        {
            var position = new float3(.3f, -.2f, .5f);
            var actual = Dispatch(new[] { float4x4.identity }, default, position, new float3(0, 1, 0), new float3(1, 0, 0), 0);
            Near(actual[0].xyz, position);
            Near(actual[1].xyz, new float3(0, 1, 0));
            Near(actual[2].xyz, new float3(1, 0, 0));
        }

        static Affine AffineOf(float4x4 matrix) => new Affine(Motion(matrix.c0.xyz), Motion(matrix.c1.xyz), Motion(matrix.c2.xyz), Motion(matrix.c3.xyz));
        static Float3 Motion(float3 value) => new Float3(value.x, value.y, value.z);
        static float3 Unity(Float3 value) => new float3(value.X, value.Y, value.Z);
        static float3 Vector(Affine matrix, float3 value) => Unity(matrix.C0) * value.x + Unity(matrix.C1) * value.y + Unity(matrix.C2) * value.z;
        static float3 Point(Affine matrix, float3 value) => Vector(matrix, value) + Unity(matrix.C3);
        static void Near(float3 actual, float3 expected) => Assert.Less(math.distance(actual, expected), 3e-5f);
    }
}
