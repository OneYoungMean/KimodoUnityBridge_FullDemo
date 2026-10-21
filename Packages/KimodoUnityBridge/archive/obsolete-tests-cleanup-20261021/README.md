# Archived obsolete tests

`AnalysisPictureSpacingTests` used reflection to call helper methods that were removed when the analysis renderer was consolidated. The active `AnimationAnalyzeCommandIntegrationTests` now verifies the 0.5 m spacing rule through the public `animation.analysis` path.