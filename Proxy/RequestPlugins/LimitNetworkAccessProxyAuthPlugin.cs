using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;
using static DevProxy.LimitNetworkAccessPlugin;

namespace DevProxy
{
    public class LimitNetworkAccessPlugin : IRequestPluginFactory, IProxyAuthPluginFactory
    {
        public enum RuleAction
        {
            Block,
            Allow
        }

        public sealed class RuleConfig
        {
            public string host_regex {get;set;}
            public string path_regex {get;set;}
            public string action {get;set;}
        }

        public sealed class Rule
        {
            public readonly RuleAction Action;

            public Rule(RuleAction action, string hostRegex, string pathRegex)
            {
                Action = action;
                if (hostRegex != null)
                {
                    Host = new Regex(hostRegex, RegexOptions.Compiled);
                }
                if (!string.IsNullOrWhiteSpace(pathRegex) && pathRegex != ".*")
                {
                    Path = new Regex(pathRegex, RegexOptions.Compiled);
                }
            }

            public readonly Regex Host;
            public readonly Regex Path;
        }

        internal static Rule[] ParseOptions(Dictionary<string, object> options)
        {
            if(options.TryGetValue("rules", out var rulesObject))
            {
                List<RuleConfig> ruleConfigs;
                if (rulesObject is JsonElement rulesElement &&
                    rulesElement.ValueKind == JsonValueKind.Array)
                {
                    string rulesText = rulesElement.GetRawText();
                    ruleConfigs = JsonSerializer.Deserialize<List<RuleConfig>>(rulesText);
                }
                else if(rulesObject is IEnumerable<RuleConfig> rulesEnumerable)
                {
                    ruleConfigs = rulesEnumerable.ToList();
                }
                else {
                    throw new ArgumentException(
                        "'rules' is wrong type: " + rulesObject.GetType().FullName);
                }
               
                return ruleConfigs.Select(r => 
                    new Rule(
                        Enum.Parse<RuleAction>(r.action, ignoreCase: true),
                        r.host_regex,
                        r.path_regex)).ToArray();
            }
            else
            {
                throw new ArgumentException("Could not find 'rules'");
            }
        }

        public IRequestPlugin Create(Dictionary<string, object> options)
        {
            return new LimitNetworkAccessPluginInstance(ParseOptions(options));
        }

        public IProxyAuthPlugin Create(IProxyPassword password, Dictionary<string, object> options)
        {
            return new LimitNetworkAccessPluginInstance(ParseOptions(options));
        }
    }

    public class LimitNetworkAccessPluginInstance : RequestPlugin<Rule>, IProxyAuthPlugin
    {
        private readonly Rule[] rules;

        internal LimitNetworkAccessPluginInstance(Rule[] rules)
        {
            this.rules = rules;
        }

        public Task<(ProxyAuthPluginResult, string)> BeforeRequestAsync(SessionEventArgsBase args)
        {
            string host = (new Uri(args.HttpClient.Request.Url)).Host;

            var allowMatches = rules.Where(r => r.Action == RuleAction.Allow && r.Host.IsMatch(host)).ToList();
            var blockMatches = rules.Where(r => r.Action == RuleAction.Block && r.Host.IsMatch(host)).ToList();

            if (allowMatches.Count == 0 && blockMatches.Count == 0)
            {
                return Task.FromResult((ProxyAuthPluginResult.NoOpinion, $"{host}_matches_no_host_rules."));
            }
            
            if (allowMatches.Count == 0)
            {
                var wholeHostBlock = blockMatches.FirstOrDefault(b => b.Path == null);
                if (wholeHostBlock != null)
                {
                    return Task.FromResult((
                        ProxyAuthPluginResult.Rejected, 
                        $"{host}_blocked_by_rule_{wholeHostBlock.Host}/*."));
                }
            }

            if (blockMatches.Count == 0)
            {
                var wholeHostAllow = allowMatches.FirstOrDefault(b => b.Path == null);
                if (wholeHostAllow != null)
                {
                    return Task.FromResult((
                        ProxyAuthPluginResult.NoOpinion, 
                        $"{host}_allowed_by_{wholeHostAllow.Host}/*."));
                }
            }
            
            return Task.FromResult((
                        ProxyAuthPluginResult.NoOpinion, 
                        $"{host}_depends_on_path."));
        }

        public override Task<RequestPluginResult> BeforeRequestAsync(PluginRequest request)
        {
            var url = new Uri(request.Request.Url);
            foreach (var rule in rules)
            {
                if (!rule.Host.IsMatch(url.Host))
                {
                    continue;
                }

                if (rule.Path != null && !rule.Path.IsMatch(url.PathAndQuery))
                {
                    continue;
                }

                request.Data = rule;

                if (rule.Action == RuleAction.Allow)
                {
                    return Task.FromResult(RequestPluginResult.Continue);
                }
                else if (rule.Action == RuleAction.Block)
                {
                    request.Args.GenericResponse(
                        $"Blocked by {rule.Host} / {rule.Path}",
                        HttpStatusCode.UnavailableForLegalReasons,
                        new HttpHeader[] {
                            new HttpHeader(HeaderName, CreateHeaderValue(rule))
                        });
                    return Task.FromResult(RequestPluginResult.Stop);
                }
            }

            return Task.FromResult(RequestPluginResult.Continue);
        }

        private static readonly string HeaderName =  $"X-DevCache-{nameof(LimitNetworkAccessPlugin)}-Rule";
        private static string CreateHeaderValue(Rule r) => r == null ? "None" : r.ToString();

        public override Task<RequestPluginResult> BeforeResponseAsync(PluginRequest request)
        {
            if (!request.Response.Headers.Any(h => h.Name == HeaderName))
            {
                request.Response.Headers.AddHeader(HeaderName, CreateHeaderValue(request.Data));
            }

            return Task.FromResult(RequestPluginResult.Continue);
        }

        public override bool IsHostRelevant(string host)
        {
            return rules.Any(r => r.Host.IsMatch(host));
        }
    }
}
