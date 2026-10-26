using System.Collections.Generic;
using System.Threading;
using Flinch.Api.Models;

namespace Flinch.Api.Services
{
    public interface IAiProvider
    {
        string ProviderName { get; }

        IAsyncEnumerable<AnswerStreamChunk> GenerateAnswerStreamAsync(
            AnswerRequest request,
            CancellationToken cancellationToken);
    }
}
