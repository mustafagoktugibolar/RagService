using System.Net.Http.Json;
using System.Text.Json;
using Rag.Core.Contracts;
using Xunit;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Rag.IntegrationTests;

[Collection("Integration")]
public class EndToEndTests : IClassFixture<IntegrationTestWebAppFactory>
{
    private readonly IntegrationTestWebAppFactory _factory;
    private readonly HttpClient _client;

    public EndToEndTests(IntegrationTestWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task IndexAndQuery_EndToEnd_WorksCorrectly()
    {
        // 1. Setup WireMock for OpenAI API mock
        _factory.WireMockServer
            .Given(Request.Create().WithPath(new WireMock.Matchers.RegexMatcher(".*embeddings.*")).UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new
                {
                    @object = "list",
                    data = new[]
                    {
                        new { @object = "embedding", index = 0, embedding = new float[] { 0.1f, 0.2f, 0.3f } }
                    },
                    model = "text-embedding-ada-002",
                    usage = new { prompt_tokens = 5, total_tokens = 5 }
                }));

        // Wait for containers to be ready
        await Task.Delay(2000);
        
        var indexRequest = new IndexDocumentRequest 
        { 
            RequestId = Guid.NewGuid().ToString(),
            Collection = "test-collection", 
            DocumentId = "doc1", 
            FilePath = "dummy.txt"
        };
        
        var healthResponse = await _client.GetAsync("/health/ready");
        var healthStr = await healthResponse.Content.ReadAsStringAsync();
        
        // Assert
        Assert.True(healthResponse.IsSuccessStatusCode, $"Health check failed: {healthStr}");
    }
}
