using System;

namespace KimodoBridge.Editor
{
    public readonly struct KimodoEditorCommandProgressEvent
    {
        public KimodoEditorCommandProgressEvent(IKimodoEditorCommand command, string message, KimodoGeneratePipelineStage stage = KimodoGeneratePipelineStage.None)
        {
            Command = command;
            Message = message ?? string.Empty;
            Stage = stage;
        }

        public IKimodoEditorCommand Command { get; }

        public string Message { get; }

        public KimodoGeneratePipelineStage Stage { get; }
    }

    public readonly struct KimodoEditorCommandCompletedEvent
    {
        public KimodoEditorCommandCompletedEvent(IKimodoEditorCommand command, IKimodoEditorCommandResult payload)
        {
            Command = command;
            Payload = payload;
        }

        public IKimodoEditorCommand Command { get; }

        public IKimodoEditorCommandResult Payload { get; }
    }

    public readonly struct KimodoEditorCommandFailedEvent
    {
        public KimodoEditorCommandFailedEvent(IKimodoEditorCommand command, string message, Exception exception)
        {
            Command = command;
            Message = message ?? string.Empty;
            Exception = exception;
        }

        public IKimodoEditorCommand Command { get; }

        public string Message { get; }

        public Exception Exception { get; }
    }

    public readonly struct KimodoEditorCommandCanceledEvent
    {
        public KimodoEditorCommandCanceledEvent(IKimodoEditorCommand command, string reason)
        {
            Command = command;
            Reason = reason ?? string.Empty;
        }

        public IKimodoEditorCommand Command { get; }

        public string Reason { get; }
    }
}
