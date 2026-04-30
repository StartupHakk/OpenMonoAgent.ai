using System.Text.RegularExpressions;

namespace OpenMono.Utils;

public static class SecretScanner
{

    private static readonly string AntKeyPfx = string.Join("-", "sk", "ant", "api");

    private const string B = @"(?:[\x60'""\s;]|\\[nr]|$)";

    private sealed record Rule(string Id, Regex Pattern);

    private static Rule[]? _rules;
    private static Rule[] GetRules() => _rules ??= BuildRules();

    private static Rule[] BuildRules() =>
    [

        new("aws-access-token",
            Re(@"\b((?:A3T[A-Z0-9]|AKIA|ASIA|ABIA|ACCA)[A-Z2-7]{16})\b")),

        new("gcp-api-key",
            Re($@"\b(AIza[\w-]{{35}}){B}")),

        new("azure-ad-client-secret",
            Re(@"(?:^|[\\'""\x60\s>=:(,)])([a-zA-Z0-9_~.]{3}\dQ~[a-zA-Z0-9_~.-]{31,34})(?:$|[\\'""\x60\s<),])")),

        new("digitalocean-pat",
            Re($@"\b(dop_v1_[a-f0-9]{{64}}){B}")),

        new("digitalocean-access-token",
            Re($@"\b(doo_v1_[a-f0-9]{{64}}){B}")),

        new("anthropic-api-key",
            Re($@"\b({AntKeyPfx}03-[a-zA-Z0-9_\-]{{93}}AA){B}")),

        new("anthropic-admin-api-key",
            Re($@"\b(sk-ant-admin01-[a-zA-Z0-9_\-]{{93}}AA){B}")),

        new("openai-api-key",
            Re($@"\b(sk-(?:proj|svcacct|admin)-(?:[A-Za-z0-9_-]{{74}}|[A-Za-z0-9_-]{{58}})T3BlbkFJ(?:[A-Za-z0-9_-]{{74}}|[A-Za-z0-9_-]{{58}})\b|sk-[a-zA-Z0-9]{{20}}T3BlbkFJ[a-zA-Z0-9]{{20}}){B}")),

        new("huggingface-access-token",
            Re($@"\b(hf_[a-zA-Z]{{34}}){B}")),

        new("github-pat",
            Re(@"ghp_[0-9a-zA-Z]{36}")),

        new("github-fine-grained-pat",
            Re(@"github_pat_\w{82}")),

        new("github-app-token",
            Re(@"(?:ghu|ghs)_[0-9a-zA-Z]{36}")),

        new("github-oauth",
            Re(@"gho_[0-9a-zA-Z]{36}")),

        new("github-refresh-token",
            Re(@"ghr_[0-9a-zA-Z]{36}")),

        new("gitlab-pat",
            Re(@"glpat-[\w-]{20}")),

        new("gitlab-deploy-token",
            Re(@"gldt-[0-9a-zA-Z_\-]{20}")),

        new("slack-bot-token",
            Re(@"xoxb-[0-9]{10,13}-[0-9]{10,13}[a-zA-Z0-9-]*")),

        new("slack-user-token",
            Re(@"xox[pe](?:-[0-9]{10,13}){3}-[a-zA-Z0-9-]{28,34}")),

        new("slack-app-token",
            Re(@"xapp-\d-[A-Z0-9]+-\d+-[a-z0-9]+", ignoreCase: true)),

        new("twilio-api-key",
            Re(@"SK[0-9a-fA-F]{32}")),

        new("sendgrid-api-token",
            Re($@"\b(SG\.[a-zA-Z0-9=_\-.]{{66}}){B}")),

        new("npm-access-token",
            Re($@"\b(npm_[a-zA-Z0-9]{{36}}){B}")),

        new("pypi-upload-token",
            Re(@"pypi-AgEIcHlwaS5vcmc[\w-]{50,1000}")),

        new("databricks-api-token",
            Re($@"\b(dapi[a-f0-9]{{32}}(?:-\d)?){B}")),

        new("hashicorp-tf-api-token",
            Re(@"[a-zA-Z0-9]{14}\.atlasv1\.[a-zA-Z0-9\-_=]{60,70}")),

        new("pulumi-api-token",
            Re($@"\b(pul-[a-f0-9]{{40}}){B}")),

        new("postman-api-token",
            Re($@"\b(PMAK-[a-fA-F0-9]{{24}}-[a-fA-F0-9]{{34}}){B}")),

        new("grafana-api-key",
            Re($@"\b(eyJrIjoi[A-Za-z0-9+/]{{70,400}}={{0,3}}){B}")),

        new("grafana-cloud-api-token",
            Re($@"\b(glc_[A-Za-z0-9+/]{{32,400}}={{0,3}}){B}")),

        new("grafana-service-account-token",
            Re($@"\b(glsa_[A-Za-z0-9]{{32}}_[A-Fa-f0-9]{{8}}){B}")),

        new("sentry-user-token",
            Re($@"\b(sntryu_[a-f0-9]{{64}}){B}")),

        new("sentry-org-token",
            Re(@"\bsntrys_eyJpYXQiO[a-zA-Z0-9+/]{10,200}(?:LCJyZWdpb25fdXJs|InJlZ2lvbl91cmwi|cmVnaW9uX3VybCI6)[a-zA-Z0-9+/]{10,200}={0,2}_[a-zA-Z0-9+/]{43}")),

        new("stripe-access-token",
            Re($@"\b((?:sk|rk)_(?:test|live|prod)_[a-zA-Z0-9]{{10,99}}){B}")),

        new("shopify-access-token",
            Re(@"shpat_[a-fA-F0-9]{32}")),

        new("shopify-shared-secret",
            Re(@"shpss_[a-fA-F0-9]{32}")),

        new("private-key",
            Re(@"-----BEGIN[ A-Z0-9_-]{0,100}PRIVATE KEY(?: BLOCK)?-----[\s\S-]{64,}?-----END[ A-Z0-9_-]{0,100}PRIVATE KEY(?: BLOCK)?-----",
                ignoreCase: true)),
    ];

    private static Regex Re(string pattern, bool ignoreCase = false) =>
        new(pattern,
            (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None)
            | RegexOptions.Compiled);

    public static IReadOnlyList<string> Scan(string content)
    {
        var matches = new List<string>();
        var seen = new HashSet<string>();
        foreach (var rule in GetRules())
        {
            if (!seen.Contains(rule.Id) && rule.Pattern.IsMatch(content))
            {
                seen.Add(rule.Id);
                matches.Add(rule.Id);
            }
        }
        return matches;
    }

    public static string RuleIdToLabel(string ruleId)
    {
        var special = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["aws"] = "AWS", ["gcp"] = "GCP", ["api"] = "API", ["pat"] = "PAT",
            ["ad"] = "AD", ["tf"] = "TF", ["oauth"] = "OAuth", ["npm"] = "NPM",
            ["pypi"] = "PyPI", ["jwt"] = "JWT", ["github"] = "GitHub",
            ["gitlab"] = "GitLab", ["openai"] = "OpenAI",
            ["digitalocean"] = "DigitalOcean", ["huggingface"] = "HuggingFace",
            ["hashicorp"] = "HashiCorp", ["sendgrid"] = "SendGrid",
            ["anthropic"] = "Anthropic",
        };
        return string.Join(" ", ruleId.Split('-')
            .Select(p => special.TryGetValue(p, out var s)
                ? s
                : char.ToUpperInvariant(p[0]) + p[1..]));
    }

    public static string Redact(string content)
    {
        foreach (var rule in GetRules())
        {
            content = rule.Pattern.Replace(content, match =>
            {

                if (match.Groups.Count > 1 && match.Groups[1].Success)
                    return match.Value.Replace(match.Groups[1].Value, "[REDACTED]");
                return "[REDACTED]";
            });
        }
        return content;
    }
}
