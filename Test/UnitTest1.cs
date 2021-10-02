using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using DevProxy;
using System.Threading.Tasks;

namespace Test
{
    [TestClass]
    public class UnitTest1
    {
        private static HttpClientHandler CreateHandler(int proxyPort, X509Certificate2 proxyRootCert, ICredentials creds)
        {
            var proxy = new WebProxy("localhost", proxyPort);
            proxy.UseDefaultCredentials = false;
            if (creds != null)
            {
                proxy.Credentials = creds;
            }

            var handler = new HttpClientHandler();
            handler.Proxy = proxy;
            handler.UseProxy = true;
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
            {
                return cert.Equals(proxyRootCert);
            };

            return handler;
        }

        private static int GetRandomUnusedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static async Task<(int,T)> CreateWithRandomPort<T>(Func<int, Task<T>> factory)
        {
            int attempts = 10;
            while(true)
            {
                var port = GetRandomUnusedPort();
                try
                {
                    return (port, await factory(port));
                }
                catch(Exception) when (attempts > 0)
                {
                    attempts--;
                }
            }
        }

        [TestMethod]
        public async Task TestMethod1()
        {
            var (_, proxy) = await CreateWithRandomPort(async p => {
                var proxy = new DevProxy.DevProxy();
                proxy.pipeName = Guid.NewGuid().ToString("N");
                proxy.proxyPort = p;
                proxy.authPlugins.Add(new ProxyPasswordAuthPlugin(proxy.proxyPassword));
                await proxy.StartAsync();
                return proxy;
            });

            var creds = new NetworkCredential("user", proxy.proxyPassword);

            var (testPort, testServer) = await CreateWithRandomPort(async p => {
                var server = new HttpListener();
                server.Prefixes.Add($"http://localhost:{p}/");
                server.Start();
                return server;
            });

            Task serverTask = Task.Run(async () => {
                while(true)
                {
                    var ctxt = await testServer.GetContextAsync();
                    ctxt.Response.StatusCode = 200;
                    ctxt.Response.OutputStream.Close();
                }
            });

            var testClient = new HttpClient(CreateHandler(proxy.proxyPort, proxy.rootCert, creds));
            try
            {
                var response = await testClient.GetAsync($"http://localhost:{testPort}/");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }
            catch(Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }
}