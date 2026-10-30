Showcase and Crowd
==================

ShowcaseSystem drives every rig through one 24-second script: a speed-driven Idle/Walk/Run/Sprint blend and a masked additive Clap.
HeadLookSystem turns the head bone entity of CPU rigs, showing that bone edits reach the skinned mesh.
CrowdAuthoring plus CrowdSpawnSystem spawn a grid of prefab instances. CrowdWaveSystem offsets the script by grid position.
Benchmark writes frame statistics to Temp/sa when a Crowd subscene is present.
Use SA Skinned Lit.shader on the converted renderer.
