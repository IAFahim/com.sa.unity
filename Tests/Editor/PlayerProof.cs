using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Unity.Entities;
using Unity.Entities.Serialization;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SA.Tests
{
    public static class PlayerProof
    {
        const string StreamingAssets = "Assets/StreamingAssets";
        const string DataName = "SA.PlayerProof";
        const string DataFolder = StreamingAssets + "/" + DataName;
        const string Probe = "SA.PlayerProof.SaPlayerProof, SA.PlayerProof";
        const int PlayerSeconds = 300;

        [MenuItem("Tools/SA/Tests/Run Player Proof")]
        public static void Menu()
        {
            var report = Run(out var passed);
            if (passed)
                Debug.Log(report);
            else
                Debug.LogWarning(report);
        }

        public static void RunFromCommandLine()
        {
            var report = Run(out var passed);
            Debug.Log(report);
            EditorApplication.Exit(passed ? 0 : 1);
        }

        public static string Run(out bool passed)
        {
            passed = false;
            if (Type.GetType(Probe) == null)
                return "SA player proof: the probe is not in this project. Open Window > Package Manager, select SA - Simple Animation, import the Player Proof sample, then choose Tools > SA > Tests > Run Player Proof again.";
            if (AssetDatabase.IsValidFolder(DataFolder))
                return "SA player proof: " + DataFolder + " is already there, left by a run that did not finish. Delete that folder, then run again.";
            var scene = FirstScene();
            if (scene.Length == 0)
                return "SA player proof: the project has no scene to build. Save any scene under Assets, then run again.";
            var project = Directory.GetParent(Application.dataPath).FullName;
            var target = EditorUserBuildSettings.activeBuildTarget;
            var backend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
            var header = "SA player proof (" + target + ", " + backend + "): ";
            var folder = Path.Combine(project, "Builds", DataName, target + "-" + backend);
            var windows = target == BuildTarget.StandaloneWindows64 || target == BuildTarget.StandaloneWindows;
            var location = Path.Combine(folder, target == BuildTarget.StandaloneOSX ? "SaPlayerProof.app" : windows ? "SaPlayerProof.exe" : "SaPlayerProof");
            var executable = target == BuildTarget.StandaloneOSX ? Path.Combine(location, "Contents", "MacOS", PlayerSettings.productName) : location;
            var reportFile = Path.Combine(folder, "report.json");
            var log = Path.Combine(folder, "player.log");
            var madeStreamingAssets = !AssetDatabase.IsValidFolder(StreamingAssets);
            try
            {
                if (madeStreamingAssets)
                    AssetDatabase.CreateFolder("Assets", "StreamingAssets");
                AssetDatabase.CreateFolder(StreamingAssets, DataName);
                WriteData(Path.Combine(project, DataFolder));
                AssetDatabase.Refresh();
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
                Directory.CreateDirectory(folder);
                var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { scene },
                    locationPathName = location,
                    target = target,
                    options = BuildOptions.None
                });
                if (build == null || build.summary.result != BuildResult.Succeeded)
                    return header + "the player did not build" + (build == null ? "" : " (" + build.summary.totalErrors + " errors)") + ". The Console says why.";
                EditorUtility.DisplayProgressBar("SA player proof", "Running the built player", 0.9f);
                var info = new ProcessStartInfo(executable, "-batchmode -saPlayerProof \"" + reportFile + "\" -logFile \"" + log + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(info))
                {
                    if (!process.WaitForExit(PlayerSeconds * 1000))
                    {
                        process.Kill();
                        return header + "the player did not finish in " + PlayerSeconds + " seconds and was stopped. Its log is " + log;
                    }
                    if (!File.Exists(reportFile))
                        return header + "the player exited with code " + process.ExitCode + " and wrote no report. Its log is " + log;
                    var text = File.ReadAllText(reportFile);
                    passed = process.ExitCode == 0 && text.Contains("\"failures\": 0");
                    return header + (passed ? "the player reproduced every recorded value.\n" : "the player did not reproduce the recordings. Its log is " + log + "\n") + text;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.DeleteAsset(DataFolder);
                if (madeStreamingAssets && AssetDatabase.IsValidFolder(StreamingAssets) && Directory.GetFileSystemEntries(Path.Combine(project, StreamingAssets)).Length == 0)
                    AssetDatabase.DeleteAsset(StreamingAssets);
            }
        }

        static string FirstScene()
        {
            foreach (var scene in EditorBuildSettings.scenes)
                if (scene.enabled && File.Exists(scene.path))
                    return scene.path;
            foreach (var guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }))
                return AssetDatabase.GUIDToAssetPath(guid);
            return string.Empty;
        }

        static void WriteData(string folder)
        {
            using (var stage = SharedFamilyJobTests.Populate())
                Save(stage.World, stage.Rigs, Path.Combine(folder, "Shared.entities"));
            using (var scene = SharedFamilyPaletteTests.Populate())
                Save(scene.Stage.World, scene.Stage.Rigs, Path.Combine(folder, "SharedSkins.entities"));
            using (var scene = ShapeJobTests.Populate(false))
                Save(scene.Stage.World, scene.Stage.Rigs, Path.Combine(folder, "Shape.entities"));
            using (var scene = ShapeJobTests.Populate(true))
                Save(scene.Stage.World, scene.Stage.Rigs, Path.Combine(folder, "ShapeSkins.entities"));
            foreach (var name in new[] { "SharedFamilyTrace.bytes", "SharedFamilyPalette.bytes", "ShapeTrace.bytes", "ShapePalette.bytes" })
                File.Copy(TraceFixtures.FixturePath(name), Path.Combine(folder, name), true);
        }

        static unsafe void Save(World world, List<Entity> rigs, string file)
        {
            var manager = world.EntityManager;
            for (var i = 0; i < rigs.Count; i++)
                manager.SetComponentData(rigs[i], new SharedRig { NameHash = (uint)i });
            using (var writer = new MemoryBinaryWriter())
            {
                SerializeUtility.SerializeWorld(manager, writer);
                var bytes = new byte[writer.Length];
                fixed (byte* target = bytes)
                    Buffer.MemoryCopy(writer.Data, target, bytes.Length, bytes.Length);
                File.WriteAllBytes(file, bytes);
            }
        }
    }
}
