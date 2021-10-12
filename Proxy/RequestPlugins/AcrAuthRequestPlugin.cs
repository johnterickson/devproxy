using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;

namespace DevProxy
{
    // https://github.com/Azure/acr/blob/main/docs/AAD-OAuth.md
    public class ACRAuthRequestPlugin : AADAuthRequestPlugin
    {
        // https://github.com/Azure/azure-sdk-for-java/blob/6c61e2bd69491085af49658b1b136d42499b4b35/sdk/containerregistry/azure-containers-containerregistry/src/main/java/com/azure/containers/containerregistry/models/ContainerRegistryAudience.java#L23
        private const string Resource = "https://management.azure.com/.default";

        private class Registry
        {
            public readonly string HostName;

            public Registry(string hostName)
            {
                HostName = hostName;
            }

            public readonly Dictionary<string, string> ScopedATs = new Dictionary<string, string>();
            public string RefreshToken;
            public AuthenticationResult AadAuthResult;
            public string MostRecentSuccessfulScope;
        }

        private readonly Dictionary<string, Registry> _registries = new Dictionary<string, Registry>();

        public ACRAuthRequestPlugin() : base(new[] { "azurecr.io" }, new[] { Resource })
        {

        }

        protected override async Task<Token> GetAuthorizationHeaderTokenAsync(PluginRequest r)
        {
            if (r.Data != null)
            {
                return r.Data;
            }

            if (_registries.TryGetValue(r.Request.Host, out var registry) && registry.MostRecentSuccessfulScope != null)
            {
                if (registry.ScopedATs.TryGetValue(registry.MostRecentSuccessfulScope, out string at))
                {
                    return new Token("ACR_AT_MostRecent", registry.AadAuthResult, at, new[] { registry.MostRecentSuccessfulScope });
                }
            }

            return null;
        }

        private async Task<string> GetRefreshTokenAsync(Registry registry, PluginRequest req)
        {
            if (registry.AadAuthResult == null)
            {
                registry.AadAuthResult = await base.GetAADTokenAsync(req, CancellationToken.None);
            }

            var content = new FormUrlEncodedContent(new[] {
                new KeyValuePair<string,string>("grant_type", "access_token"),
                new KeyValuePair<string,string>("service",registry.HostName),
                new KeyValuePair<string,string>("tenant",registry.AadAuthResult.TenantId),
                new KeyValuePair<string,string>("access_token",registry.AadAuthResult.AccessToken),
            });

            var client = req.RequestContext.Proxy.HttpClient;
            var response = await client.PostAsync($"https://{registry.HostName}/oauth2/exchange", content);
            var body = await response.Content.ReadAsStringAsync();
            var rt = JsonSerializer.Deserialize<RefreshTokenResponse>(body);
            return rt.refresh_token;
        }

        private class RefreshTokenResponse
        {
            public string refresh_token { get; set; }
        }

        private async Task<Token> GetAccessTokenAsync(string challenge, PluginRequest req)
        {
            string[] challenge_pairs = challenge.Split("\",");
            var kvps = challenge_pairs
                .Select(p => p.Split('='))
                .ToDictionary(
                    p => p[0],
                    p => p[1].Trim('"'));

            string registry = kvps["service"];
            kvps.TryGetValue("error", out string error);
            kvps.TryGetValue("scope", out string missingScope);
            missingScope = missingScope ?? "registry:catalog:*";

            if (!_registries.TryGetValue(registry, out Registry reg))
            {
                reg = new Registry(registry);
                _registries.Add(registry, reg);
            }

            var allScopesToRequest = new HashSet<string>();
            foreach (var scopesList in reg.ScopedATs.Keys)
            {
                foreach (string s in scopesList.Split("|||"))
                {
                    allScopesToRequest.Add(s);
                }
            }
            allScopesToRequest.Add(missingScope);

            {
                string providedAT = req.Data?.Value;
                (string existingScopeList, string existingAT) = reg.ScopedATs.FirstOrDefault(s => s.Value == providedAT);
                if (existingAT != null && error != "insufficient_scope")
                {
                    reg.ScopedATs.Remove(existingScopeList);
                }
            }

            {
                (string existingScopes, string existingAT) = reg.ScopedATs.FirstOrDefault(s => s.Key.Split("|||").Contains(missingScope));
                if (existingAT != null)
                {
                    return new Token("ACR_AT_EXISTING", reg.AadAuthResult, existingAT, new[] { missingScope });
                }
            }

            if (reg.RefreshToken == null)
            {
                reg.RefreshToken = await GetRefreshTokenAsync(reg, req);
            }

            var tokenRequestPairs = new List<KeyValuePair<string, string>>() {
                new KeyValuePair<string,string>("grant_type","refresh_token"),
                new KeyValuePair<string,string>("service", reg.HostName),
                new KeyValuePair<string,string>("refresh_token", reg.RefreshToken),
            };

            NormalizeScopeSet(allScopesToRequest);
            foreach (var s in allScopesToRequest)
            {
                tokenRequestPairs.Add(new KeyValuePair<string, string>("scope", s));
            }

            var content = new FormUrlEncodedContent(tokenRequestPairs);

            var client = req.RequestContext.Proxy.HttpClient;
            var response = await client.PostAsync($"https://{reg.HostName}/oauth2/token", content);
            var body = await response.Content.ReadAsStringAsync();
            var rt = JsonSerializer.Deserialize<AccessTokenResponse>(body);

            string scopes = string.Join("|||", allScopesToRequest);
            reg.ScopedATs[scopes] = rt.access_token;
            return new Token("ACR_AT_NEW", reg.AadAuthResult, rt.access_token, new[] { missingScope });
        }

        /*
            ACR gets confused when you ask for multiple scopes of the same registry.
            scope=repository:hello-world:pull <--- only takes the first one it sees. le sigh
            scope=repository:hello-world:pull,push
        */
        private void NormalizeScopeSet(HashSet<string> scopes)
        {
            bool fixedSomething;
            do
            {
                fixedSomething = false;

                foreach (var s1 in scopes)
                {
                    foreach (var s2 in scopes)
                    {
                        if (s1.Length > s2.Length && s1.Contains(s2))
                        {
                            scopes.Remove(s2);
                            fixedSomething = true;
                            continue;
                        }
                    }
                }
            }
            while (fixedSomething);
        }

        private class AccessTokenResponse
        {
            public string access_token { get; set; }
        }

        public override async Task<RequestPluginResult> BeforeResponseAsync(PluginRequest r)
        {
            if (r.Response.StatusCode == 401)
            {
                var challengeHeader = r.Response.Headers.GetFirstHeader("Www-Authenticate");
                if (challengeHeader != null && challengeHeader.Value.StartsWith("Bearer "))
                {
                    string challenge = challengeHeader.Value.Substring(7);
                    r.Data = await GetAccessTokenAsync(challenge, r);
                    r.Request.Headers.RemoveHeader("Authorization");
                    r.Request.Headers.AddHeader("Authorization", $"Bearer {r.Data.Value}");
                    r.Args.ReRequest = true;
                    return RequestPluginResult.Stop;
                }
            }
            else if (r.Data?.Value != null)
            {
                if (_registries.TryGetValue(r.Request.Host, out Registry reg))
                {
                    string scope = reg.ScopedATs.FirstOrDefault(kvp => kvp.Value == r.Data.Value).Key;
                    reg.MostRecentSuccessfulScope = scope;
                }
            }

            return await base.BeforeResponseAsync(r);
        }


    }

}
