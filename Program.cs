using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

namespace DevProxy
{
    class Program
    {
        static void Main(string[] args)
        {
            int proxyPort = int.Parse(args[0]);
            
            var proxy = new ProxyServer();

            var seen = new ConcurrentDictionary<string,Uri>();

            proxy.BeforeRequest += (sender, e) => {

                var url = new Uri(e.HttpClient.Request.Url);
                Console.WriteLine(url);
                // foreach (var header in e.HttpClient.Request.Headers)
                // {
                //     Console.WriteLine($" {header.Name}: {header.Value}");
                // }

                if (url.Host.EndsWith("core.windows.net"))
                {
                    if (seen.TryAdd(url.AbsolutePath, url))
                    {
                        e.GenericResponse("", HttpStatusCode.ServiceUnavailable, new Dictionary<string, HttpHeader>());
                    }
                    else 
                    {
                        Console.WriteLine($"Retry of {url.AbsolutePath}");
                    }
                }

                // bool authAdded = false;
                // if (url.Host.EndsWith("dev.azure.com") || url.Host.EndsWith("visualstudio.com"))
                // {
                //     if (e.HttpClient.Request.Headers.All(h => h.Name != "Authorization"))
                //     {
                //         e.HttpClient.Request.Headers.AddHeader("Authorization", $"Bearer {accessToken}");
                //         authAdded = true;
                //     }
                // }


                return Task.CompletedTask;
            };

            proxy.BeforeResponse += (sender, e) => {
                if (e.HttpClient.Response.StatusCode == 303)
                {
                    Console.WriteLine($"{e.HttpClient.Request.Url} {e.HttpClient.Response.StatusCode} {e.HttpClient.Response.Headers.GetFirstHeader("Location").Value}");
                }
                else
                {
                    Console.WriteLine($"{e.HttpClient.Request.Url} {e.HttpClient.Response.StatusCode}");
                }
                return Task.CompletedTask;
            };

            //  openssl pkcs7 -in titanium.p7b -inform DER -print_certs -out titanium.pem
            proxy.CertificateManager.CreateRootCertificate(persistToFile: true);
            proxy.CertificateManager.TrustRootCertificateAsAdmin(machineTrusted: true);

            var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, proxyPort, decryptSsl: true);
            proxy.AddEndPoint(explicitEndPoint);
            proxy.Start();

            Console.WriteLine("Started!");

            Console.ReadKey();
        }
    }
}
