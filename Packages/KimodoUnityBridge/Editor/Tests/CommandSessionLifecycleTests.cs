using System;
using System.Linq;
using System.Reflection;
using KimodoUnityBridge.Command;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command.Tests
{
    public sealed class CommandSessionLifecycleTests
    {
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
            Animator animator = source.AddComponent<Animator>();
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
            track.trackOffset = TrackOffset.ApplySceneOffsets;
            MethodInfo bindMethod = context.GetMethod("BindTimelineCharacter", BindingFlags.Static | BindingFlags.NonPublic);

            try
            {
                bindMethod.Invoke(null, new object[] { session, source, source, animator, track });

                Assert.That(director.GetGenericBinding(track), Is.SameAs(animator));
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
                UnityEngine.Object.DestroyImmediate(sessionRoot);
                UnityEngine.Object.DestroyImmediate(timeline);
                UnityEngine.Object.DestroyImmediate(metadata);
            }
        }

        [Test]
        public void DestroySceneSessionRoots_DestroysTemporaryContexts()
        {
            Scene previousScene = SceneManager.GetActiveScene();
            Scene testScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            GameObject contextRoot = new GameObject("KimodoSession_DestroyTest");
            SceneManager.MoveGameObjectToScene(contextRoot, testScene);

            try
            {
                Type context = typeof(command_dispatcher).Assembly.GetType("KimodoUnityBridge.Command.command_context");
                MethodInfo destroy = context.GetMethod("DestroySceneSessionRoots", BindingFlags.Static | BindingFlags.NonPublic);

                Assert.That((int)destroy.Invoke(null, new object[] { testScene }), Is.EqualTo(1));
                Assert.That(contextRoot == null, Is.True);
            }
            finally
            {
                if (testScene.IsValid())
                {
                    SceneManager.SetActiveScene(previousScene);
                    EditorSceneManager.CloseScene(testScene, true);
                }
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
