using System;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using System.Threading;
using System.Text.Json;
using System.Collections.Generic;

namespace DevProxy
{

    [TestClass]
    public class ProxyAuthTests
    {
        private static TestEnvironment _env;

        [ClassInitialize]
        public static async Task ClassInit(TestContext context)
        {
            var pluginConfig = new PluginConfiguration()
            {
                class_name = nameof(ProxyAuthorizationHeaderProxyAuthPlugin),
            };

            var config = new Configuration();
            config.plugins.Add(pluginConfig);
                
            _env = await TestEnvironment.CreateAsync(config);
        }

        [TestMethod]
        public async Task HttpAuthOK()
        {
            var response = await _env.authClient.GetAsync(_env.serverUri);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        [TestMethod]
        public async Task HttpNoAuth407()
        {
            var response = await _env.noAuthClient.GetAsync(_env.serverUri);
            Assert.AreEqual(HttpStatusCode.ProxyAuthenticationRequired, response.StatusCode);
        }

        [TestMethod]
        public async Task HttpsBypassAuthOK()
        {
            var response = await _env.authClient.GetAsync("https://www.bing.com");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        [TestMethod]
        public async Task HttpsBypassNoAuth407()
        {
            var response = await _env.noAuthClient.GetAsync("https://www.bing.com");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        private class HttpsInterceptPlugin : IRequestPlugin
        {
            private readonly string hostSubstring;
            public volatile int TimesCalled = 0;

            public HttpsInterceptPlugin(string hostSubstring)
            {
                this.hostSubstring = hostSubstring;
            }

            public Task<RequestPluginResult> BeforeRequestAsync(SessionEventArgs args)
            {
                Interlocked.Increment(ref TimesCalled);
                args.GenericResponse("Intercepted.", HttpStatusCode.OK);
                return Task.FromResult(RequestPluginResult.Stop);
            }

            public Task<RequestPluginResult> BeforeResponseAsync(SessionEventArgs args)
            {
                Interlocked.Increment(ref TimesCalled);
                return Task.FromResult(RequestPluginResult.Continue);
            }

            public bool IsHostRelevant(string host)
            {
                return host.Contains(hostSubstring);
            }
        }

        [TestMethod]
        public async Task HttpsInterceptAuthOK()
        {
            var plugin = new HttpsInterceptPlugin("intercept.me");
            try
            {
                _env.proxy.plugins.Add(plugin);
                var response = await _env.authClient.GetAsync("https://intercept.me");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }
            finally
            {
                _env.proxy.plugins.Remove(plugin);
            }
        }

        [TestMethod]
        public async Task HttpsInterceptNoAuth407()
        {
            var plugin = new HttpsInterceptPlugin("intercept.me");
            try
            {
                _env.proxy.plugins.Add(plugin);
                var response = await _env.noAuthClient.GetAsync("https://intercept.me");
                Assert.AreEqual(HttpStatusCode.ProxyAuthenticationRequired, response.StatusCode);
                Assert.AreEqual(0, plugin.TimesCalled);
            }
            finally
            {
                _env.proxy.plugins.Remove(plugin);
            }
        }
    }
}