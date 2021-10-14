using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;
using System.Collections.Generic;
using static DevProxy.LimitNetworkAccessPlugin;
using System.Text.Json;
using System.Net;

namespace DevProxy
{
    [TestClass]
    public class LimitNetworkAccessTests
    {
        private static TestEnvironment _env;

        [ClassInitialize]
        public static async Task ClassInit(TestContext context)
        {
            var pluginConfig = new PluginConfiguration()
            {
                class_name = nameof(LimitNetworkAccessPlugin),
                options = new Dictionary<string, object>()
                {
                    {
                        "rules",
                        new RuleConfig[] {
                            new RuleConfig()
                            {
                                action = RuleAction.Allow.ToString(),
                                host_regex = ".*dev\\.azure\\.com",
                                path_regex = "/mseng/.*"
                            },
                            new RuleConfig()
                            {
                                action = RuleAction.Block.ToString(),
                                host_regex = ".*dev\\.azure\\.com",
                                path_regex = ".*"
                            },
                            new RuleConfig()
                            {
                                action = RuleAction.Block.ToString(),
                                host_regex = "blockeddomain1\\.com",
                                path_regex = ".*"
                            },
                            new RuleConfig()
                            {
                                action = RuleAction.Block.ToString(),
                                host_regex = "blockeddomain2\\.com",
                            },
                        }
                    }
                }
            };
            var config = new Configuration();
            config.plugins.Add(pluginConfig);
            config.plugins.Add(new PluginConfiguration()
            {
                class_name = nameof(ProxyAuthorizationHeaderProxyAuthPlugin),
            });

            _env = await TestEnvironment.CreateAsync(config);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _env.Dispose();
        }

        [TestMethod]
        public async Task MsengOK()
        {
            var response = await _env.authClient.GetAsync("https://dev.azure.com/mseng/hello");
            Assert.AreEqual(HttpStatusCode.NonAuthoritativeInformation, response.StatusCode);
        }

        [TestMethod]
        public async Task NotMsengBlocked()
        {
            var response = await _env.authClient.GetAsync("https://dev.azure.com/other/hello");
            Assert.AreEqual(HttpStatusCode.UnavailableForLegalReasons, response.StatusCode);
        }

        [TestMethod]
        public async Task Evil1Blocked()
        {
            var response = await _env.authClient.GetAsync("https://blockeddomain1.com");
            Assert.AreEqual(HttpStatusCode.ProxyAuthenticationRequired, response.StatusCode);
        }

        [TestMethod]
        public async Task Evil2Blocked()
        {
            var response = await _env.authClient.GetAsync("https://blockeddomain2.com");
            Assert.AreEqual(HttpStatusCode.ProxyAuthenticationRequired, response.StatusCode);
        }

        [TestMethod]
        public void ParseConfig()
        {
            string configText =
                @"{" +
                @" ""plugins"": [{" +
                @"   ""class_name"": """ + nameof(LimitNetworkAccessPlugin) + @"""," +
                @"   ""options"": {" + 
                @"    ""rules"": [" + 
                @"      { ""action"": ""allow"", ""host_regex"": "".*dev.azure.com"", ""path_regex"": ""/mseng/.*""}," +
                @"      { ""action"": ""block"", ""host_regex"": "".*"", ""path_regex"": "".*""}" +
                @"    ]" +
                @"   }" +
                @" }]" + 
                @"}";
            var config = JsonSerializer.Deserialize<Configuration>(configText);
            using(var proxy = new DevProxy(config)) 
            {

            }
        }
    }
}