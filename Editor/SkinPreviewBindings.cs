using UnityEditor;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace SA.Editor
{
    internal sealed class SkinPreviewBindings : ScriptableSingleton<SkinPreviewBindings>
    {
        GraphicsBuffer matrices;
        GraphicsBuffer influences;

        [InitializeOnLoadMethod]
        static void Initialize()
        {
            instance.BindPreview();
        }

        void OnEnable()
        {
            EditorApplication.update += BindPreview;
            AssemblyReloadEvents.beforeAssemblyReload += Release;
            EditorApplication.quitting += Release;
        }

        void OnDisable()
        {
            EditorApplication.update -= BindPreview;
            AssemblyReloadEvents.beforeAssemblyReload -= Release;
            EditorApplication.quitting -= Release;
            Release();
        }

        void BindPreview()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                return;
            if (matrices == null)
            {
                matrices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 64);
                influences = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
                matrices.SetData(new[] { GpuFrame.Identity });
                influences.SetData(new[] { uint4.zero });
            }
            Shader.SetGlobalBuffer("_SaSkinMatrices", matrices);
            Shader.SetGlobalBuffer("_SaInfluences", influences);
        }

        void Release()
        {
            matrices?.Dispose();
            influences?.Dispose();
            matrices = null;
            influences = null;
        }
    }
}
