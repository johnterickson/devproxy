using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
            public UrlFilter.Config filter {get;set;}
            public string action {get;set;}
        }

        public sealed class Rule
        {
            public readonly RuleAction Action;
            public readonly UrlFilter UrlFilter;

            public Rule(RuleAction action, UrlFilter urlFilter)
            {
                Action = action;
                UrlFilter = urlFilter;
            }
        }

        internal static Rule[] ParseOptions(Dictionary<string, object> options)
        {
            if(options.TryGetValue("rules", out var rulesObject))
            {
                var ruleConfigs = rulesObject.ParseJsonArray<RuleConfig>();
                return ruleConfigs.Select(r => 
                    new Rule(
                        Enum.Parse<RuleAction>(r.action, ignoreCase: true),
                        new UrlFilter(r.filter))).ToArray();
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

            var allowMatches = rules.Where(r => r.Action == RuleAction.Allow && r.UrlFilter.IsHostMatch(host)).ToList();
            var blockMatches = rules.Where(r => r.Action == RuleAction.Block && r.UrlFilter.IsHostMatch(host)).ToList();

            if (allowMatches.Count == 0 && blockMatches.Count == 0)
            {
                return Task.FromResult((ProxyAuthPluginResult.NoOpinion, $"{host}_matches_no_host_rules."));
            }
            
            if (allowMatches.Count == 0)
            {
                var wholeHostBlock = blockMatches.FirstOrDefault(b => b.UrlFilter.IsAnyPathMatch);
                if (wholeHostBlock != null)
                {
                    return Task.FromResult((
                        ProxyAuthPluginResult.Rejected, 
                        $"{host}_blocked_by_rule_{wholeHostBlock.UrlFilter.Host}/*."));
                }
            }

            if (blockMatches.Count == 0)
            {
                var wholeHostAllow = allowMatches.FirstOrDefault(b => b.UrlFilter.IsAnyPathMatch);
                if (wholeHostAllow != null)
                {
                    return Task.FromResult((
                        ProxyAuthPluginResult.NoOpinion, 
                        $"{host}_allowed_by_{wholeHostAllow.UrlFilter.Host}/*."));
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
                if(!rule.UrlFilter.IsUriMatch(url))
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
                        $"Blocked by {rule.UrlFilter.Host} / {rule.UrlFilter.Path}",
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
            return rules.Any(r => r.UrlFilter.IsHostMatch(host));
        }
    }
}
