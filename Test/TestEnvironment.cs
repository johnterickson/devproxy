using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using static DevProxy.LimitNetworkAccessPlugin;

namespace DevProxy
{
    internal sealed class TestEnvironment : IDisposable
    {
        public readonly DevProxy proxy;
        public readonly HttpListener server;
        public readonly HttpClient authClient;
        public readonly HttpClient noAuthClient;
        public readonly Uri serverUri;

        private TestEnvironment(DevProxy proxy, HttpListener testServer)
        {
            this.proxy = proxy;
            this.server = testServer;

            var creds = new NetworkCredential("user", proxy.Passwords.GetCurrent());
            this.authClient = new HttpClient(TestHelpers.CreateHandler(proxy.proxyPort, proxy.rootCert, creds));;
            this.noAuthClient = new HttpClient(TestHelpers.CreateHandler(proxy.proxyPort, proxy.rootCert, null));;
            this.serverUri = new Uri(this.server.Prefixes.Single());
        }

        public static async Task<TestEnvironment> CreateAsync(Configuration config)
        {
            var (_, proxy) = await TestHelpers.CreateWithRandomPort(async p => {
                config.proxy.port = p;
                config.proxy.ipcPipeName = $"TestPipe{p}_{Guid.NewGuid().ToString("N")}";
                config.proxy.listen_to_wsl2 = false;
                var proxy = new DevProxy(config);
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