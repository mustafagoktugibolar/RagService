using Xunit;

namespace Rag.IntegrationTests;

[CollectionDefinition("Integration")]
public class IntegrationCollection : ICollectionFixture<IntegrationTestWebAppFactory>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}
