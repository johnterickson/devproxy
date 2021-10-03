using System;
using System.Net;
using System.Net.Http;
using DevProxy;
using System.Threading.Tasks;
using System.Linq;

namespace Test
{
    internal sealed class TestEnvironment : IDisposable
    {
        public readonly DevProxy.DevProxy proxy;
        public readonly HttpListener server;
        public readonly HttpClient authClient;
        public readonly HttpClient noAuthClient;
        public readonly Uri serverUri;

        private TestEnvironment(DevProxy.DevProxy proxy, HttpListener testServer)
        {
            this.proxy = proxy;
            this.server = testServer;

            var creds = new NetworkCredential("user", proxy.proxyPassword);
            this.authClient = new HttpClient(TestHelpers.CreateHandler(proxy.proxyPort, proxy.rootCert, creds));;
            this.noAuthClient = new HttpClient(TestHelpers.CreateHandler(proxy.proxyPort, proxy.rootCert, null));;
            this.serverUri = new Uri(this.server.Prefixes.Single());
        }

        public static async Task<TestEnvironment> CreateAsync()
        {
            var (_, proxy) = await TestHelpers.CreateWithRandomPort(async p => {
                var proxy = new DevProxy.DevProxy();
                proxy.pipeName = Guid.NewGuid().ToString("N");
                proxy.proxyPort = p;
                proxy.authPlugins.Add(new ProxyAuthorizationHeaderProxyAuthPlugin(proxy.proxyPassword));
                await proxy.StartAsync();
                return proxy;
            });

            var (testPort, testServer) = await TestHelpers.CreateWithRandomPort(p => {
                var server = new HttpListener();
                server.Prefixes.Add($"http://localhost:{p}/");
                server.Start();
                return Task.FromResult(server);
            });

            var _serverTask = Task.Run(async () => {
                while(true)
                {
                    var ctxt = await testServer.GetContextAsync();
                    ctxt.Response.StatusCode = int.Parse(ctxt.Request.QueryString.Get("status") ?? "200");
                    ctxt.Response.OutputStream.Close();
                }
            });

            return new TestEnvironment(proxy, testServer);
        }

        public void Dispose()
        {
            authClient.Dispose();
            proxy.Dispose();
            server.Stop();
            server.Close();
        }
    }
}