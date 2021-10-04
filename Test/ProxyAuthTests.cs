using System;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;
using DevProxy;
using Titanium.Web.Proxy.EventArguments;
using System.Threading;

namespace Test
{
    [TestClass]
    public class ProxyAuthTests
    {
        private static TestEnvironment _env;

        [ClassInitialize]
        public static async Task ClassInit(TestContext context)
        {
            _env = await TestEnvironment.CreateAsync();
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _env.Dispose();
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
            Assert.AreEqual(HttpStatusCode.ProxyAuthenticationRequired, response.StatusCode);
        }

        private class HttpsInterceptAllPlugin : IRequestPlugin
        {
            public Task<RequestPluginResult> BeforeRequestAsync(SessionEventArgs args)
            {
                args.GenericResponse("Intercepted.", HttpStatusCode.OK);
                return Task.FromResult(RequestPluginResult.Stop);
            }

            public Task<RequestPluginResult> BeforeResponseAsync(SessionEventArgs args)
            {
                return Task.FromResult(RequestPluginResult.Continue);
            }

            public bool IsHostRelevant(string host)
            {
                return host.Contains("intercept.me");
            }
        }

        [TestMethod]
        public async Task HttpsInterceptAuthOK()
        {
            IRequestPlugin plugin = new HttpsInterceptAllPlugin();
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

        private class NeverCalledPlugin : IRequestPlugin
        {
            public volatile int TimesCalled = 0;
            public Task<RequestPluginResult> BeforeRequestAsync(SessionEventArgs args)
            {
                Interlocked.Increment(ref TimesCalled);
                throw new NotImplementedException();
            }

            public Task<RequestPluginResult> BeforeResponseAsync(SessionEventArgs args)
            {
                Interlocked.Increment(ref TimesCalled);
                throw new NotImplementedException();
            }

            public bool IsHostRelevant(string host)
            {
                Interlocked.Increment(ref TimesCalled);
                throw new NotImplementedException();
            }
        }

        [TestMethod]
        public async Task HttpsInterceptNoAuth407()
        {
            var plugin = new NeverCalledPlugin();
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