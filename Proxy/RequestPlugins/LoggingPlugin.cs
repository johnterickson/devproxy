using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static DevProxy.LoggingPlugin;

namespace DevProxy
{
    public class LoggingPlugin : IRequestPluginFactory
    {
        public IRequestPlugin Create(Dictionary<string, object> options)
        {
            return new LoggingPluginInstance(ParseOptions(options));
        }

        public enum Target
        {
            Console
        }

        public sealed class RuleConfig
        {
            public UrlFilter.Config filter { get; set; }
            public string target { get; set; }
        }

        public sealed class Rule
        {
            public readonly Target Target;
            public readonly UrlFilter UrlFilter;

            public Rule(Target target, UrlFilter urlFilter)
            {
                Target = target;
                UrlFilter = urlFilter;
            }
        }

        internal static Rule[] ParseOptions(Dictionary<string, object> options)
        {
            if (options.TryGetValue("rules", out var rulesObject))
            {
                var ruleConfigs = rulesObject.ParseJsonArray<RuleConfig>();
                return ruleConfigs.Select(r =>
                    new Rule(
                        Enum.Parse<Target>(r.target, ignoreCase: true),
                        new UrlFilter(r.filter))).ToArray();
            }
            else
            {
                throw new ArgumentException("Could not find 'rules'");
            }
        }
    }

    public class LoggingPluginInstance : RequestPlugin<List<Target>>
    {
        private readonly Rule[] rules;

        public LoggingPluginInstance(Rule[] rules)
        {
            this.rules = rules;
        }

        public override Task<RequestPluginResult> BeforeRequestAsync(PluginRequest request)
        {
            var url = new Uri(request.Request.Url);
            request.Data = this.rules.Where(r => r.UrlFilter.IsUriMatch(url)).Select(r => r.Target).Distinct().ToList();
            if (request.Data.Count > 0)
            {
                var now = DateTimeOffset.UtcNow.ToString("s");
                string message = $"{now} START {request.Request.Method} {url.GetLeftPart(UriPartial.Path)}";
                foreach (Target t in request.Data)
                {
                    switch (t)
                    {
                        case Target.Console:
                            Console.WriteLine(message);
                            break;
                    }
                }
            }
            return Task.FromResult(RequestPluginResult.Continue);
        }

        public override Task<RequestPluginResult> BeforeResponseAsync(PluginRequest request)
        {
            if (request.Data.Count > 0)
            {
                var url = new Uri(request.Request.Url);

                var now = DateTimeOffset.UtcNow.ToString("s");
                var sb = new StringBuilder();
                sb.Append($"{now} END   {request.Request.Method} {url.GetLeftPart(UriPartial.Path)} {request?.Response?.StatusCode}");
                var internalHeaders = request?.Response?.Headers?.Headers?.Where(h => h.Key.Contains("DevProxy"));
                if (internalHeaders != null)
                {
                    foreach (var h in internalHeaders)
                    {
                        sb.Append($"\n {now} {h.Key}: {h.Value.Value}");
                    }
                }

                string message = sb.ToString();
                foreach (Target t in request.Data)
                {
                    switch (t)
                    {
                        case Target.Console:
                            Console.WriteLine(message);
                            break;
                    }
                }
            }

            return Task.FromResult(RequestPluginResult.Continue);
        }

        public override bool IsHostRelevant(string host)
        {
            return rules.Any(r => r.UrlFilter.IsHostMatch(host));
        }
    }
}
