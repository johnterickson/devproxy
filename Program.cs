using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

namespace DevProxy
{
    public enum PluginResult
    {
        Continue,
        Stop
    }

    public interface Plugin
    {
        Task<PluginResult> BeforeRequestAsync(SessionEventArgs e);
        Task<PluginResult> BeforeResponseAsync(SessionEventArgs e);
    }

    class Program
    {
        static void Main(string[] args)
        {
            int proxyPort = int.Parse(args[0]);
            
            var proxy = new ProxyServer();

            var seen = new ConcurrentDictionary<string,Uri>();
            
            var plugins = new List<Plugin>();
            plugins.Add(new BlobStoreCachePlugin());
            plugins.Add(new AzureDevOpsAuthPlugin());

            proxy.BeforeRequest += async (sender, e) => {

                var url = new Uri(e.HttpClient.Request.Url);
                Console.WriteLine(url);
                // foreach (var header in e.HttpClient.Request.Headers)
                // {
                //     Console.WriteLine($" {header.Name}: {header.Value}");
                // }

                foreach(var plugin in plugins)
                {
                    var result = await plugin.BeforeRequestAsync(e);
                    if(result == PluginResult.Stop)
                    {
                        return;
                    }
                }
            };

            proxy.BeforeResponse += async (sender, e) => {

                foreach(var plugin in plugins)
                {
                    var result = await plugin.BeforeResponseAsync(e);
                    if(result == PluginResult.Stop)
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

            var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, proxyPort, decryptSsl: true);
            proxy.AddEndPoint(explicitEndPoint);
            proxy.Start();

            Console.WriteLine("Started!");

            Console.ReadKey();
        }

        private class UserProfileCertificateStorage : ICertificateCache
        {
            private readonly string folderPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".devproxy",
                "certs"
            );

            public void Clear()
            {
                if (Directory.Exists(folderPath))
                {
                    foreach(var f in Directory.GetFiles(folderPath))
                    {
                        File.Delete(f);
                    }
                }
            }

            public X509Certificate2 LoadCertificate(string subjectName, X509KeyStorageFlags storageFlags)
            {
                var path = Path.Combine(folderPath, $"notroot.{subjectName}.pfx");
                return loadCertificate(path, string.Empty, storageFlags);
            }

            public X509Certificate2 LoadRootCertificate(string pathOrName, string password, X509KeyStorageFlags storageFlags)
            {
                var path = Path.Combine(folderPath, $"root.{pathOrName}");
                return loadCertificate(path, password, storageFlags);
            }

            public void SaveCertificate(string subjectName, X509Certificate2 certificate)
            {
                Directory.CreateDirectory(folderPath);
                var path = Path.Combine(folderPath, $"notroot.{subjectName}.pfx");
                byte[] exported = certificate.Export(X509ContentType.Pkcs12);
                File.WriteAllBytes(path, exported);
            }

            public void SaveRootCertificate(string pathOrName, string password, X509Certificate2 certificate)
            {
                Directory.CreateDirectory(folderPath);
                var path = Path.Combine(folderPath, $"root.{pathOrName}");
                byte[] exported = certificate.Export(X509ContentType.Pkcs12, password);
                File.WriteAllBytes(path, exported);
            }


            private X509Certificate2 loadCertificate(string path, string password, X509KeyStorageFlags storageFlags)
            {
                byte[] exported;

                if (!File.Exists(path))
                {
                    return null;
                }

                try
                {
                    exported = File.ReadAllBytes(path);
                }
                catch (IOException)
                {
                    // file or directory not found
                    return null;
                }

                return new X509Certificate2(exported, password, storageFlags);
            }

        }
    }

}
