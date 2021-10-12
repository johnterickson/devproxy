using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace DevProxy
{
    internal static class TestHelpers
    {
        public static HttpClientHandler CreateHandler(int proxyPort, X509Certificate2 proxyRootCert, ICredentials creds)
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
                return errors == System.Net.Security.SslPolicyErrors.None || cert.Equals(proxyRootCert);
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

        public static async Task<(int,T)> CreateWithRandomPort<T>(Func<int, Task<T>> factory)
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
    }
}