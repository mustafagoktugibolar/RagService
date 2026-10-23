using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rag.Core.Reranking;

namespace Rag.Reranking.Onnx;

public static class OnnxRerankerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the local ONNX reranker as a singleton (the model is loaded once and disposed by the container) — but only
    /// when <c>Retrieval:Reranking</c> is enabled with provider <c>Onnx</c>, so a host that references this project still pays nothing while reranking is off.
    /// Options: <c>Retrieval:Reranking</c> (enable, provider, budget, timeout) and <c>OnnxReranker</c> (model paths, batching).
    /// </summary>
    public static IServiceCollection AddOnnxReranker(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Retrieval:Reranking:Enabled")
            || !string.Equals(configuration["Retrieval:Reranking:Provider"], "Onnx", StringComparison.OrdinalIgnoreCase))
        {
            return services;
        }

        services.Configure<OnnxRerankerOptions>(configuration.GetSection("OnnxReranker"));
        services.AddSingleton<OnnxCrossEncoderReranker>();
        services.AddSingleton<IReranker>(sp => sp.GetRequiredService<OnnxCrossEncoderReranker>());
        return services;
    }
}
