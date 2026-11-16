Player Proof
============

Checks that the shared-family and shape jobs give the same poses and palettes in a built player as in the Editor.

How to run it
-------------

1. Import this sample (Window > Package Manager > SA - Simple Animation > Samples > Player Proof).
2. List `com.sa.unity` under `testables` in `Packages/manifest.json`, so the SA tests compile.
3. Choose **Tools > SA > Tests > Run Player Proof**. The Editor builds a player for the current standalone platform with the scripting backend chosen in Player Settings, runs it, and writes the result to the Console. The build takes a few minutes and the Editor waits for it.

From a shell: `Unity -batchmode -quit -projectPath <project> -executeMethod SA.Tests.PlayerProof.RunFromCommandLine`. The exit code is 0 when every value matched.

What it does
------------

The menu item writes four small worlds (the scenes `SharedFamilyJobTests`, `SharedFamilyPaletteTests` and `ShapeJobTests` use) and the four recorded traces into `Assets/StreamingAssets/SA.PlayerProof`, builds the player, and deletes that folder again. The player and its report go to `Builds/SA.PlayerProof/<platform>-<backend>/` in the project folder.

In the player, `SaPlayerProof` does nothing unless the player was started with `-saPlayerProof <report file>`. With it, it loads each world, steps the systems through the same scene the tests play, compares every value with the recording, checks that every rig ran on Burst, writes the report and quits.
