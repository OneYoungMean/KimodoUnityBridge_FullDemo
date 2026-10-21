using System;
using System.IO;
using System.Linq;
using System.Reflection;
using KimodoBridge;
using KimodoBridge.Editor;
using KimodoUnityBridge.Command;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command.Tests
{
    public sealed class CommandSessionLifecycleTests
    {
        [TestCase("mesh")]
        [TestCase("missing_avatar")]
        [TestCase("generic_avatar")]
        [TestCase("mismatched_skeleton")]
        public void SceneCharacterIndex_SkipsObjectsWhoseAvatarCannotBeResolvedOrGenerated(string kind)
        {
            var root = new GameObject("CharacterIndex_" + Guid.NewGuid().ToString("N"));
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            Avatar genericAvatar = null;
            UnityEngine.Object previousSelection = Selection.activeObject;
            try
            {
                root.AddComponent<MeshRenderer>();
                Animator animator = kind == "mesh" ? null : root.AddComponent<Animator>();
                if (kind == "generic_avatar")
                    animator.avatar = genericAvatar = AvatarBuilder.BuildGenericAvatar(root, string.Empty);
                if (kind == "mismatched_skeleton")
                {
                    Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                        "smplx", out Avatar humanoid, out string error), Is.True, error);
                    animator.avatar = humanoid;
                }

                var resolved = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(root);
                Assert.That(resolved.IsHumanoid, Is.False);
                Assert.That(resolved.Avatar, Is.Null);
                Assert.That(resolved.Error, Does.Contain("Cannot resolve ModelImporter"));

                Type context = typeof(command_context);
                MethodInfo find = context.GetMethod("ResolveRequestedSceneCharacters", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That((GameObject[])find.Invoke(null, new object[] { root.name }), Is.Empty);
                Selection.activeGameObject = root;
                Assert.That((GameObject[])find.Invoke(null, new object[] { "@active_animator" }), Is.Empty);

                Type sessionType = context.GetNestedType("TimelineSessionRecord", BindingFlags.NonPublic);
                object session = CreateSessionRecord(sessionType, timeline, null, null, null);
                MethodInfo add = context.GetMethod("AddCharacterTrack", BindingFlags.Static | BindingFlags.NonPublic);
                object[] arguments = { session, root, animator, true, null, true };
                Assert.That((bool)add.Invoke(null, arguments), Is.False);
                Assert.That(arguments[4] as string, Does.StartWith("avatar_required:"));
                Assert.That(timeline.GetRootTracks(), Is.Empty, "Rejected objects must not create Timeline tracks.");

                var legacy = new command_context.TimelineCharacterRecord("legacy", root, animator,
                    animator != null ? animator.avatar : null, null, null, string.Empty);
                MethodInfo indexed = context.GetMethod("IsIndexableSessionCharacter", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That((bool)indexed.Invoke(null, new object[] { legacy }), Is.False);
            }
            finally
            {
                Selection.activeObject = previousSelection;
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(timeline);
                if (genericAvatar != null) UnityEngine.Object.DestroyImmediate(genericAvatar);
            }
        }

        [Test]
        public void SceneCharacterIndex_UsesExistingHumanoidAvatarAndSkipsSelectedProp()
        {
            Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                "smplx", out Avatar avatar, out string error), Is.True, error);
            var root = new GameObject("CharacterIndex_" + Guid.NewGuid().ToString("N"));
            var prop = new GameObject("CharacterIndexProp");
            UnityEngine.Object previousSelection = Selection.activeObject;
            try
            {
                prop.AddComponent<Animator>();
                Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryBuildHierarchyFromAvatarSkeleton(
                    avatar, root.transform, out error), Is.True, error);
                Animator animator = root.AddComponent<Animator>();
                animator.avatar = avatar;
                var resolved = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(root);
                Assert.That(resolved.Avatar, Is.SameAs(avatar));
                Assert.That(resolved.Source, Is.EqualTo("Animator"));
                Assert.That(resolved.Error, Is.Empty);
                Type context = typeof(command_context);
                MethodInfo find = context.GetMethod("ResolveRequestedSceneCharacters", BindingFlags.Static | BindingFlags.NonPublic);
                CollectionAssert.AreEqual(new[] { root },
                    (GameObject[])find.Invoke(null, new object[] { root.name }));

                Selection.activeGameObject = root;
                MethodInfo automatic = context.GetMethod("ResolveAutomaticSceneCharacter", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(automatic.Invoke(null, null), Is.SameAs(animator));
                Selection.activeGameObject = prop;
                var fallback = (Animator)automatic.Invoke(null, null);
                Assert.That(fallback, Is.Not.Null);
                Assert.That(fallback, Is.Not.SameAs(prop.GetComponent<Animator>()));
                Assert.That(KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(
                    fallback.transform.root.gameObject).IsHumanoid, Is.True);

                var character = new command_context.TimelineCharacterRecord("humanoid", root,
                    animator, avatar, null, null, string.Empty);
                MethodInfo indexed = context.GetMethod("IsIndexableSessionCharacter", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That((bool)indexed.Invoke(null, new object[] { character }), Is.True);
            }
            finally
            {
                Selection.activeObject = previousSelection;
                UnityEngine.Object.DestroyImmediate(prop);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SceneCharacterIndex_GeneratesMissingAvatarBeforeIndexing(bool useSelection)
        {
            string folderName = "SceneCharacterIndex_" + Guid.NewGuid().ToString("N");
            string folder = "Assets/" + folderName;
            string modelPath = folder + "/Humanoid.fbx";
            GameObject root = null;
            TimelineAsset timeline = null;
            UnityEngine.Object[] previousSelection = Selection.objects;
            try
            {
                AssetDatabase.CreateFolder("Assets", folderName);
                Assert.That(AssetDatabase.CopyAsset(
                    "Packages/com.unity.kimodo_unity_motion_tools/Editor/Model/SOMA30.fbx", modelPath), Is.True);
                var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
                importer.animationType = ModelImporterAnimationType.Generic;
                importer.SaveAndReimport();
                root = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modelPath));
                root.name = folderName;
                Animator animator = root.GetComponent<Animator>() ?? root.AddComponent<Animator>();
                animator.avatar = null;
                Assert.That(AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>()
                    .Any(avatar => avatar.isValid && avatar.isHuman), Is.False,
                    "The fixture must start without a valid Humanoid Avatar.");

                Type context = typeof(command_context);
                Selection.activeGameObject = root;
                MethodInfo automatic = context.GetMethod("ResolveAutomaticSceneCharacter", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(automatic.Invoke(null, null), Is.Null,
                    "Automatic scene discovery must not generate an Avatar or reimport the character.");
                Assert.That(((ModelImporter)AssetImporter.GetAtPath(modelPath)).animationType,
                    Is.EqualTo(ModelImporterAnimationType.Generic),
                    "Read-only character discovery must preserve the ModelImporter rig setting.");

                if (useSelection)
                {
                    var explicitlyResolved = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(root);
                    Assert.That(explicitlyResolved.IsHumanoid, Is.True, explicitlyResolved.Error);
                    Assert.That(automatic.Invoke(null, null), Is.SameAs(animator));
                }
                else
                {
                    MethodInfo find = context.GetMethod("ResolveRequestedSceneCharacters", BindingFlags.Static | BindingFlags.NonPublic);
                    CollectionAssert.AreEqual(new[] { root },
                        (GameObject[])find.Invoke(null, new object[] { root.name }));
                }

                var resolved = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(root);
                Assert.That(resolved.IsHumanoid, Is.True, resolved.Error);
                Assert.That(KimodoLocalAvatarUtility.CheckAvatarValid(resolved.Avatar, root), Is.True);
                Assert.That(((ModelImporter)AssetImporter.GetAtPath(modelPath)).animationType,
                    Is.EqualTo(ModelImporterAnimationType.Generic),
                    "Avatar generation must restore the original ModelImporter rig setting.");
                var queried = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(root, allowGeneration: false);
                Assert.That(queried.IsHumanoid, Is.True, queried.Error);
                Assert.That(queried.Source, Is.EqualTo("Cache"),
                    "A query must reuse the cached Avatar without changing the importer.");
                Assert.That(((ModelImporter)AssetImporter.GetAtPath(modelPath)).animationType,
                    Is.EqualTo(ModelImporterAnimationType.Generic));
                timeline = ScriptableObject.CreateInstance<TimelineAsset>();
                object session = CreateSessionRecord(
                    context.GetNestedType("TimelineSessionRecord", BindingFlags.NonPublic), timeline, null, null, null);
                MethodInfo add = context.GetMethod("AddCharacterTrack", BindingFlags.Static | BindingFlags.NonPublic);
                object[] arguments = { session, root, animator, true, null, true };
                Assert.That((bool)add.Invoke(null, arguments), Is.True, arguments[4] as string);
                Assert.That(timeline.GetRootTracks().Count(), Is.EqualTo(1));
                Assert.That(timeline.GetRootTracks().OfType<AnimationTrack>().Single().trackOffset,
                    Is.EqualTo(TrackOffset.ApplyTransformOffsets),
                    "Command-created character tracks must not apply scene offsets.");
            }
            finally
            {
                Selection.objects = previousSelection;
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                if (timeline != null) UnityEngine.Object.DestroyImmediate(timeline);
                // Preserve generated fixture assets outside Assets so discovery cannot index them later.
                string project = Path.GetFullPath(Directory.GetCurrentDirectory()) + Path.DirectorySeparatorChar;
                string source = Path.GetFullPath(folder);
                string archive = Path.GetFullPath(Path.Combine("archive", folderName));
                if (!source.StartsWith(project, StringComparison.OrdinalIgnoreCase) ||
                    !archive.StartsWith(project, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Fixture archive paths must remain inside the project.");
                Directory.CreateDirectory(Path.GetDirectoryName(archive));
                if (Directory.Exists(source)) Directory.Move(source, archive);
                if (File.Exists(source + ".meta")) File.Move(source + ".meta", archive + ".meta");
                AssetDatabase.Refresh();
            }
        }

        [TestCase(false, false)]
        [TestCase(true, true)]
        public void AutomaticSessionRestore_RequiresLiveWorkspaceInCurrentScene(
            bool hasExistingSceneContext,
            bool expected)
        {
            Type context = typeof(command_dispatcher).Assembly.GetType("KimodoUnityBridge.Command.command_context");
            Type metadataType = typeof(command_dispatcher).Assembly.GetType(
                "KimodoUnityBridge.Command.KimodoCommandSessionMetadata");
            ScriptableObject metadata = ScriptableObject.CreateInstance(metadataType);
            try
            {
                metadataType.GetField("isAutomatic").SetValue(metadata, true);
                metadataType.GetField("scenePath").SetValue(metadata, "Assets/PreviouslyOpened.unity");
                MethodInfo matches = context.GetMethod("ShouldRestoreAutomaticSession", BindingFlags.Static | BindingFlags.NonPublic);

                Assert.That((bool)matches.Invoke(null, new object[] { metadata, hasExistingSceneContext }), Is.EqualTo(expected));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(metadata);
            }
        }

        [Test]
        public void BindTimelineCharacter_UsesOriginalAnimatorAndArchivesLegacyGhost()
        {
            Type context = typeof(command_dispatcher).Assembly.GetType("KimodoUnityBridge.Command.command_context");
            Type sessionType = context.GetNestedType("TimelineSessionRecord", BindingFlags.NonPublic);
            Type metadataType = typeof(command_dispatcher).Assembly.GetType(
                "KimodoUnityBridge.Command.KimodoCommandSessionMetadata");
            GameObject sessionRoot = new GameObject("KimodoSession_Test");
            GameObject source = new GameObject("TestCharacter");
            GameObject otherSource = new GameObject("OtherCharacter");
            Animator animator = source.AddComponent<Animator>();
            Animator otherAnimator = otherSource.AddComponent<Animator>();
            animator.applyRootMotion = true;
            source.transform.position = new Vector3(3f, 1f, 7f);
            source.transform.rotation = Quaternion.Euler(0f, 31f, 0f);
            Vector3 sourcePosition = source.transform.position;
            Quaternion sourceRotation = source.transform.rotation;
            GameObject oldGhost = new GameObject("Ghost_TestCharacter");
            oldGhost.transform.SetParent(sessionRoot.transform, false);
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            ScriptableObject metadata = ScriptableObject.CreateInstance(metadataType);
            var directorObject = new GameObject("Kimodo_CommandSession_Test");
            directorObject.transform.SetParent(sessionRoot.transform, false);
            PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
            director.playableAsset = timeline;
            object session = CreateSessionRecord(sessionType, timeline, metadata, director, sessionRoot);
            AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "TestCharacter");
            AnimationTrack otherTrack = timeline.CreateTrack<AnimationTrack>(null, "OtherCharacter");
            director.SetGenericBinding(otherTrack, otherAnimator);
            track.trackOffset = TrackOffset.ApplyTransformOffsets;
            MethodInfo bindMethod = context.GetMethod("BindTimelineCharacter", BindingFlags.Static | BindingFlags.NonPublic);

            try
            {
                bindMethod.Invoke(null, new object[] { session, source, source, animator, track });

                Assert.That(director.GetGenericBinding(track), Is.SameAs(animator));
                Assert.That(director.GetGenericBinding(otherTrack), Is.SameAs(otherAnimator),
                    "Binding one character must preserve other Animator bindings.");
                Assert.That(animator.applyRootMotion, Is.True);
                Assert.That(source.transform.position, Is.EqualTo(sourcePosition));
                Assert.That(source.transform.rotation, Is.EqualTo(sourceRotation));

                director.RebuildGraph();
                director.time = 0.5d;
                director.Evaluate();
                Assert.That(source.transform.position, Is.EqualTo(sourcePosition),
                    "Evaluating a newly bound Timeline must preserve the character's scene position.");
                Assert.That(Quaternion.Angle(source.transform.rotation, sourceRotation), Is.LessThan(0.01f),
                    "Evaluating a newly bound Timeline must preserve the character's scene rotation.");
                Assert.That(oldGhost.activeSelf, Is.False);
                Assert.That(oldGhost.transform.parent.name, Is.EqualTo("Archive_GhostPreviews"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(otherSource);
                UnityEngine.Object.DestroyImmediate(sessionRoot);
                UnityEngine.Object.DestroyImmediate(timeline);
                UnityEngine.Object.DestroyImmediate(metadata);
            }
        }

        [Test]
        public void DestroySceneSessionRoots_DestroysTemporaryContexts()
        {
            Scene testScene = SceneManager.GetActiveScene();
            GameObject contextRoot = new GameObject("KimodoSession_DestroyTest");

            try
            {
                Type context = typeof(command_dispatcher).Assembly.GetType("KimodoUnityBridge.Command.command_context");
                MethodInfo destroy = context.GetMethod("DestroySceneSessionRoots", BindingFlags.Static | BindingFlags.NonPublic);

                Assert.That((int)destroy.Invoke(null, new object[] { testScene }), Is.EqualTo(1));
                Assert.That(contextRoot == null, Is.True);
            }
            finally
            {
                if (contextRoot != null) UnityEngine.Object.DestroyImmediate(contextRoot);
            }
        }

        private static object CreateSessionRecord(
            Type sessionType,
            TimelineAsset timeline,
            ScriptableObject metadata,
            PlayableDirector director,
            GameObject sessionRoot)
        {
            ConstructorInfo constructor = sessionType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(item => item.GetParameters().Length == 8);
            return constructor.Invoke(new object[]
            {
                Guid.NewGuid(), "Test", director, timeline, string.Empty, true, metadata, sessionRoot
            });
        }
    }
}
