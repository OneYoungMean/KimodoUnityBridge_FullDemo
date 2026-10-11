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
        public void CloneCharacterToSession_ReusesGhostAndDestroysLegacyDuplicates()
        {
            Type context = typeof(command_dispatcher).Assembly.GetType("KimodoUnityBridge.Command.command_context");
            Type sessionType = context.GetNestedType("TimelineSessionRecord", BindingFlags.NonPublic);
            Type metadataType = typeof(command_dispatcher).Assembly.GetType(
                "KimodoUnityBridge.Command.KimodoCommandSessionMetadata");
            GameObject sessionRoot = new GameObject("KimodoSession_Test");
            GameObject source = new GameObject("TestCharacter");
            GameObject oldGhost = new GameObject("TestCharacter");
            GameObject duplicateOldGhost = new GameObject("TestCharacter");
            GameObject duplicateNamedGhost = new GameObject("Ghost_TestCharacter");
            oldGhost.transform.SetParent(sessionRoot.transform, false);
            duplicateOldGhost.transform.SetParent(sessionRoot.transform, false);
            duplicateNamedGhost.transform.SetParent(sessionRoot.transform, false);
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            ScriptableObject metadata = ScriptableObject.CreateInstance(metadataType);
            var directorObject = new GameObject("Kimodo_CommandSession_Test");
            directorObject.transform.SetParent(sessionRoot.transform, false);
            PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
            object session = CreateSessionRecord(sessionType, timeline, metadata, director, sessionRoot);
            MethodInfo cloneMethod = context.GetMethod("CloneCharacterToSession", BindingFlags.Static | BindingFlags.NonPublic);

            try
            {
                GameObject first = (GameObject)cloneMethod.Invoke(null, new object[] { session, source, "TestCharacter" });
                GameObject second = (GameObject)cloneMethod.Invoke(null, new object[] { session, source, "TestCharacter" });

                Assert.That(first, Is.SameAs(duplicateNamedGhost));
                Assert.That(second, Is.SameAs(first), "Repeated session hydration must reuse its Ghost.");
                Assert.That(first.name, Is.EqualTo("Ghost_TestCharacter"));
                Assert.That(first.transform.parent, Is.EqualTo(sessionRoot.transform));
                Assert.That(new[] { oldGhost, duplicateOldGhost }.All(item => item == null), Is.True);
                Assert.That(sessionRoot.transform.Cast<Transform>().Count(item => item.name == "Ghost_TestCharacter"), Is.EqualTo(1));
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
