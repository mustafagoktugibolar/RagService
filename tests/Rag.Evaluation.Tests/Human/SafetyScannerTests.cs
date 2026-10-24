using Rag.Evaluation.Human;

namespace Rag.Evaluation.Tests.Human;

public sealed class SafetyScannerTests
{
    private static IReadOnlyList<SafetyFinding> Scan(string text) => SafetyScanner.Scan("doc.md", text);

    [Theory]
    [InlineData("openai-key", "key is sk-proj-abcdefghijklmnopqrstuvwxyz0123456789ABCD")]
    [InlineData("bearer-token", "Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("email", "contact jane.doe@example.com for access")]
    [InlineData("local-user-path", "see /Users/someone/dev/project/file.cs")]
    [InlineData("windows-user-path", @"C:\Users\someone\Documents\x.txt")]
    [InlineData("ipv4-address", "server at 10.20.30.40 is internal")]
    [InlineData("private-key", "-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData("jwt", "token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1rwW1gFWFOEjXk")]
    [InlineData("aws-access-key", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("connection-secret", "Host=db;Username=app;Password=hunter2secret")]
    [InlineData("secret-assignment", "S3__SECRETKEY=abcd1234efgh5678")]
    [InlineData("api-key-assignment", "api_key = \"abcd1234efgh5678ijkl\"")]
    public void RealLookingSecretsAndIdentifiers_AreFound(string rule, string text)
    {
        var finding = Assert.Single(Scan(text), f => f.Rule == rule);

        Assert.Equal("doc.md", finding.File);
        Assert.Equal(1, finding.Line);
    }

    [Fact]
    public void AFinding_NeverContainsTheMatchedSecret()
    {
        var secret = "sk-proj-abcdefghijklmnopqrstuvwxyz0123456789ABCD";

        var finding = Assert.Single(Scan($"x {secret} y"));

        Assert.DoesNotContain(secret, finding.ToString());
        Assert.DoesNotContain("abcdefghij", finding.ToString());
        Assert.True(finding.Preview.Length <= 8, "a preview may show at most a few leading characters");
    }

    [Theory]
    [InlineData("OPENAI__APIKEY=")]                                  // empty value
    [InlineData("OPENAI__APIKEY=sk-...")]                            // documented placeholder
    [InlineData("key: sk-REDACTED")]
    [InlineData("Authorization: Bearer <token>")]
    [InlineData("version 10.0.9 and jev-1.13.0 and 1.2.3")]          // version numbers are not IPs
    [InlineData("listens on 127.0.0.1 and 0.0.0.0")]                 // loopback / unspecified addresses
    [InlineData("redis:6379, localhost:6333, http://localhost:5099")]
    [InlineData("the MinIO root user is documented")]
    [InlineData("password and token are discussed as concepts")]
    [InlineData("we use Qdrant:Host and Jev:ApiKey settings")]
    [InlineData("api key rotation policy")]
    public void OrdinaryDocumentationText_IsNotFlagged(string text) => Assert.Empty(Scan(text));

    [Fact]
    public void FindingsCarryTheirLineNumber()
    {
        var findings = Scan("line one\nline two\ncontact a.b@example.org\nline four");

        Assert.Equal(3, Assert.Single(findings).Line);
    }

    [Fact]
    public void SeveralKindsInOneFile_AreAllReported()
    {
        var findings = Scan("mail a@b.com\nIP 8.8.4.4\n/Users/x/y");

        Assert.Equal(["email", "ipv4-address", "local-user-path"], findings.Select(f => f.Rule).Order());
    }

    [Fact]
    public void TheShippedFrozenCorpus_HasNoFindings_WhenItExists()
    {
        var root = FindPilotCorpus();
        if (root is null)
        {
            return;   // the corpus is created by `pilot-corpus`; until then there is nothing to check
        }

        var findings = Directory.EnumerateFiles(root, "*.md").SelectMany(f => SafetyScanner.Scan(Path.GetFileName(f), File.ReadAllText(f))).ToList();

        Assert.Empty(findings);
    }

    private static string? FindPilotCorpus()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "datasets", "human-pilot-v1", "corpus");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
