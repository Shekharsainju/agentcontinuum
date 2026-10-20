using AgentContinuum.Core.Rules;

namespace AgentContinuum.Core.Billing;

/// <summary>P3: the stored login is a subscription, judged by key names only so no secret is read out.</summary>
public static class CredentialCheck
{
    public static ProofResult Check(string userProfile, CredentialRule rule)
    {
        var problems = new List<string>();

        var credentials = Path.Combine(userProfile, rule.File);
        if (!File.Exists(credentials))
        {
            problems.Add("credentials: missing");
        }
        else if (JsonPaths.ReadObject(credentials) is not { } doc)
        {
            problems.Add("credentials: unreadable");
        }
        else
        {
            using (doc)
            {
                problems.AddRange(rule.SubscriptionKeys
                    .Where(key => !JsonPaths.Has(doc.RootElement, key))
                    .Select(key => $"credentials: no {key}"));
            }
        }

        var apiKeyFile = Path.Combine(userProfile, rule.ApiKeyFile);
        if (File.Exists(apiKeyFile) || Directory.Exists(apiKeyFile))
        {
            if (JsonPaths.ReadObject(apiKeyFile) is not { } apiDoc)
            {
                problems.Add("api key file: unreadable");
            }
            else
            {
                using (apiDoc)
                {
                    problems.AddRange(rule.ApiKeyKeys
                        .Where(key => JsonPaths.Has(apiDoc.RootElement, key))
                        .Select(key => $"api key file: {key}"));
                }
            }
        }

        return ProofResult.FromProblems(problems);
    }
}
