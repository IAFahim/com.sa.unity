using System;
using System.IO;
using NUnit.Framework;

namespace SA.Tests
{
    internal static class TraceFixtures
    {
        public const string RecordReason = "Writes what the systems produce now to Library/SA.Recorded and says whether it equals the fixture. It never changes the fixture: copy the file over it by hand only when the behaviour was meant to change.";

        public static string FixturePath(string name)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TraceFixtures).Assembly);
            Assert.IsNotNull(package, "the SA package could not be located");
            return Path.Combine(package.resolvedPath, "Tests", "Editor", "Fixtures", name);
        }

        public static string RecordedPath(string name)
        {
            return Path.Combine(Directory.GetParent(UnityEngine.Application.dataPath).FullName, "Library", "SA.Recorded", name);
        }

        public static float[] Load(string name)
        {
            var path = FixturePath(name);
            Assert.IsTrue(File.Exists(path), "missing fixture " + path);
            var stored = File.ReadAllBytes(path);
            var values = new float[stored.Length / 4];
            Buffer.BlockCopy(stored, 0, values, 0, values.Length * 4);
            return values;
        }

        public static bool Record(string name, float[] trace)
        {
            Assert.Greater(trace.Length, 0);
            var bytes = new byte[trace.Length * 4];
            Buffer.BlockCopy(trace, 0, bytes, 0, bytes.Length);
            var path = RecordedPath(name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
            var fixture = FixturePath(name);
            var same = File.Exists(fixture) && bytes.AsSpan().SequenceEqual(File.ReadAllBytes(fixture));
            TestContext.WriteLine("wrote " + trace.Length + " values to " + path + (same ? ", identical to the fixture" : ", which differs from the fixture " + fixture));
            return same;
        }
    }
}
