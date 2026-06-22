using System;

namespace KimodoBridge.Editor
{
    public enum KimodoEditorCommandKind
    {
        Unknown = 0,
        GeneratePlayableClip = 1,
        CancelPlayableClipGeneration = 2,
        BridgeStartServer = 3,
        BridgeStopServer = 4,
        BridgeTryFix = 5,
        BridgeDeleteAllData = 6,
        BridgeRefreshStatus = 7,
        BridgeEnsureRuntimeRoot = 8
    }

    public interface IKimodoEditorCommand
    {
        Guid RequestId { get; }
        string TargetKey { get; }
        KimodoEditorCommandKind Kind { get; }
    }

    public abstract class KimodoEditorCommandBase : IKimodoEditorCommand
    {
        protected KimodoEditorCommandBase(string targetKey, KimodoEditorCommandKind kind)
        {
            RequestId = Guid.NewGuid();
            TargetKey = string.IsNullOrWhiteSpace(targetKey) ? "global" : targetKey;
            Kind = kind;
        }

        public Guid RequestId { get; }

        public string TargetKey { get; }

        public KimodoEditorCommandKind Kind { get; }
    }
}
