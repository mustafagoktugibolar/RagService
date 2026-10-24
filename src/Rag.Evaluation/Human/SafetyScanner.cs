using System.Text.RegularExpressions;

namespace Rag.Evaluation.Human;

/// <summary>A suspected sensitive item. Deliberately carries no matched text beyond a few leading characters: the scanner must not become a leak itself.</summary>
public sealed record SafetyFinding(string File, int Line, string Rule, string Preview);

/// <summary>
/// Offline check that text about to be sent to an external model provider holds no credential or personal/internal identifier.
/// It is a safety net with known blind spots (it cannot recognise confidential <i>content</i>), not a substitute for the owner's review;
/// see docs/eval/send-safety-policy.md.
/// </summary>
public static partial class SafetyScanner
{
    private sealed record Rule(string Name, Regex Pattern);

    private static readonly Rule[] Rules =
    [
        new("openai-key", OpenAiKey()),
        new("bearer-token", Bearer()),
        new("jwt", Jwt()),
        new("aws-access-key", AwsKey()),
        new("private-key", PrivateKey()),
        new("email", Email()),
        new("local-user-path", UnixUserPath()),
        new("windows-user-path", WindowsUserPath()),
        new("ipv4-address", Ipv4()),
        new("connection-secret", ConnectionSecret()),
        new("secret-assignment", SecretAssignment()),
        new("api-key-assignment", ApiKeyAssignment())
    ];

    public static IReadOnlyList<SafetyFinding> Scan(string file, string text)
    {
        var findings = new List<SafetyFinding>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var rule in Rules)
            {
                foreach (Match match in rule.Pattern.Matches(lines[i]))
                {
                    if (IsPlaceholder(rule.Name, match.Value))
                    {
                        continue;
                    }

                    findings.Add(new SafetyFinding(file, i + 1, rule.Name, Preview(match.Value)));
                }
            }
        }

        return findings;
    }

    private static string Preview(string matched) => matched.Length <= 3 ? "***" : matched[..3] + "***";

    private static bool IsPlaceholder(string rule, string value)
    {
        if (rule == "ipv4-address")
        {
            // loopback, unspecified and documentation ranges are not real internal addresses
            return value.StartsWith("127.") || value == "0.0.0.0" || value.StartsWith("192.0.2.") || value.StartsWith("198.51.100.") || value.StartsWith("203.0.113.");
        }

        // documented placeholders: "sk-REDACTED", "sk-...", "Bearer <token>"
        return value.Contains("REDACTED", StringComparison.OrdinalIgnoreCase) || value.Contains("...") || value.Contains('<');
    }

    [GeneratedRegex(@"\b(?:sk|rk)-(?:proj-|live-|test-)?[A-Za-z0-9_-]{20,}")]
    private static partial Regex OpenAiKey();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=-]{20,}")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex AwsKey();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"/Users/[A-Za-z0-9_.-]+")]
    private static partial Regex UnixUserPath();

    [GeneratedRegex(@"[A-Za-z]:\\Users\\[A-Za-z0-9_. -]+")]
    private static partial Regex WindowsUserPath();

    [GeneratedRegex(@"(?<![\d.])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?![\d.])")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"(?i)\b(?:password|pwd)\s*=\s*[^;\s""']{4,}")]
    private static partial Regex ConnectionSecret();

    [GeneratedRegex(@"\b[A-Z][A-Z0-9_]*(?:SECRET|TOKEN|PASSWORD|PASSWD)[A-Z0-9_]*\s*=\s*[^\s""']{8,}")]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"(?i)\bapi[_-]?key\s*[:=]\s*[""']?[A-Za-z0-9._~+/=-]{16,}")]
    private static partial Regex ApiKeyAssignment();
}
