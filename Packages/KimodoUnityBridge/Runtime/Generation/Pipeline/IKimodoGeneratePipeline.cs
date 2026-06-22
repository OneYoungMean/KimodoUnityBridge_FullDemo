using System;
using System.Threading;
using System.Threading.Tasks;

namespace KimodoBridge
{
    public interface IKimodoGeneratePipeline
    {
        Task<KimodoGeneratePipelineResult> ExecuteAsync(
            KimodoGeneratePipelineRequest request,
            Action<KimodoGeneratePipelineStage, string> progress,
            CancellationToken token);
    }
}
