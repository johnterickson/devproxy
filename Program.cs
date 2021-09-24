using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace DevProxy
{
    public enum PluginResult
    {
        Continue,
        Stop
    }

    public abstract class Plugin
    {
        public abstract Task<PluginResult> BeforeRequestAsync(SessionEventArgs e);
        public abstract Task<PluginResult> BeforeResponseAsync(SessionEventArgs e);

        protected T GetRequestData<T>(SessionEventArgs e) where T : class
        {
            return (T)GetAllData(e)[this];
        }

        protected void SetRequestData<T>(SessionEventArgs e, T data) where T : class
        {
            GetAllData(e)[this] = data;
        }
        
        private Dictionary<Plugin,object> GetAllData(SessionEventArgs e) => (Dictionary<Plugin,object>)e.UserData;
    }

    class Program
    {
        static void Main(string[] args)
        {
            int proxyPort = int.Parse(args[0]);

            var proxy = new ProxyServer();

            var seen = new ConcurrentDictionary<string, Uri>();

            var plugins = new List<Plugin>();
            plugins.Add(new BlobStoreCachePlugin());
            plugins.Add(new AzureDevOpsAuthPlugin());

            proxy.BeforeRequest += async (sender, e) =>
            {
                e.UserData = new Dictionary<Plugin, object>();

                var url = new Uri(e.HttpClient.Request.Url);
                Console.WriteLine(url);
                // foreach (var header in e.HttpClient.Request.Headers)
                // {
                //     Console.WriteLine($" {header.Name}: {header.Value}");
                // }

                foreach (var plugin in plugins)
                {
                    var result = await plugin.BeforeRequestAsync(e);
                    if (result == PluginResult.Stop)
                    {
                        return;
                    }
                }
            };

            proxy.BeforeResponse += async (sender, e) =>
            {

                foreach (var plugin in plugins)
                {
                    var result = await plugin.BeforeResponseAsync(e);
                    if (result == PluginResult.Stop)
                    {
                        return;
                    }
                }

                if (e.HttpClient.Response.StatusCode == 303)
                {
                    Console.WriteLine($"{e.HttpClient.Request.Url} {e.HttpClient.Response.StatusCode} {e.HttpClient.Response.Headers.GetFirstHeader("Location").Value}");
                }
                else
                {
                    Console.WriteLine($"{e.HttpClient.Request.Url} {e.HttpClient.Response.StatusCode}");
                }
            };

            //  openssl pkcs7 -in titanium.p7b -inform DER -print_certs -out titanium.pem
            proxy.CertificateManager.CertificateStorage = new UserProfileCertificateStorage();
            proxy.CertificateManager.RootCertificateName = Environment.ExpandEnvironmentVariables("DevProxy for %USERNAME%");
            proxy.CertificateManager.RootCertificateIssuerName = "DevProxy";
            proxy.CertificateManager.PfxFilePath = "devproxy.pfx";
            proxy.CertificateManager.PfxPassword = "devproxy";
            if (null == proxy.CertificateManager.LoadRootCertificate())
            {
                proxy.CertificateManager.EnsureRootCertificate();
            }
            //proxy.CertificateManager.CreateRootCertificate(persistToFile: true);
            //proxy.CertificateManager.TrustRootCertificateAsAdmin(machineTrusted: true);

            string proxyPassword = "Password123";
            proxy.ProxyBasicAuthenticateFunc = (args, username, password) => {
                return Task.FromResult(username == proxyPassword || password == proxyPassword);
            };

            var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, proxyPort, decryptSsl: true);
            proxy.AddEndPoint(explicitEndPoint);
            proxy.Start();

            Console.WriteLine($"HTTP_PROXY=http://{proxyPassword}@localhost:{proxyPort}");
            Console.WriteLine( "For git:");
            Console.WriteLine( "  openssl pkcs12 -in root.devproxy.pfx -out root.devproxy.pem -nokeys");
            Console.WriteLine($"  git config http.proxy=http://{proxyPassword}@localhost:{proxyPort}");
            Console.WriteLine( "  git config --add http.sslcainfoC:/Users/jerick/.devproxy/certs/root.devproxy.pem");
            Console.WriteLine("Started!");

            while (true)
            {
                Thread.Sleep(TimeSpan.FromMinutes(1));
            }
        }


    }
}
