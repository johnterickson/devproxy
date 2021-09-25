using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

namespace DevProxy
{
    class Program
    {
        static async Task Main(string[] args)
        {
            int proxyPort = int.Parse(args[0]);

            var proxy = new ProxyServer();
            // proxy.EnableWinAuth = true;

            var plugins = new List<IPlugin>();
            plugins.Add(new BlobStoreCachePlugin());
            plugins.Add(new AzureDevOpsAuthPlugin());

            proxy.BeforeRequest += async (sender, args) =>
            {
                args.UserData = new Dictionary<IPlugin, object>();

                var url = new Uri(args.HttpClient.Request.Url);
                Console.WriteLine($"START {url}");
                // foreach (var header in e.HttpClient.Request.Headers)
                // {
                //     Console.WriteLine($" {header.Name}: {header.Value}");
                // }

                foreach (var plugin in plugins)
                {
                    var result = await plugin.BeforeRequestAsync(args);
                    if (result == PluginResult.Stop)
                    {
                        return;
                    }
                }
            };

            proxy.BeforeResponse += async (sender, args) =>
            {
                try
                {
                    foreach (var plugin in plugins)
                    {
                        var result = await plugin.BeforeResponseAsync(args);
                        if (result == PluginResult.Stop)
                        {
                            return;
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"END   {args.HttpClient?.Request?.Url} {e.Message}");
                }
                finally
                {
                    Console.WriteLine($"END   {args.HttpClient?.Request?.Url} {args.HttpClient?.Response?.StatusCode}");
                }
            };

            //  openssl pkcs7 -in titanium.p7b -inform DER -print_certs -out titanium.pem\
            var certStorage = new UserProfileCertificateStorage();

            proxy.CertificateManager.CertificateStorage = certStorage;
            proxy.CertificateManager.RootCertificateName = Environment.ExpandEnvironmentVariables("DevProxy for %USERNAME%");
            proxy.CertificateManager.RootCertificateIssuerName = "DevProxy";
            proxy.CertificateManager.PfxFilePath = "devproxy.pfx";
            proxy.CertificateManager.PfxPassword = "devproxy";

            if (null == proxy.CertificateManager.LoadRootCertificate())
            {
                proxy.CertificateManager.EnsureRootCertificate();
            }

            string rootPfx = certStorage.GetRootCertPath(proxy.CertificateManager.PfxFilePath);
            string rootPem = rootPfx.Replace(".pfx", ".pem");

            if (!File.Exists(rootPem))
            {
                string gitPath = await RunAsync("where.exe", "git.exe");
                gitPath = Path.GetDirectoryName(gitPath);
                gitPath = Path.GetDirectoryName(gitPath);
                string opensslPath = Path.Combine(gitPath, "mingw64","bin","openssl.exe");
                await RunAsync(opensslPath, $"pkcs12 -in \"{rootPfx}\" -out \"{rootPem}\" -password pass:{proxy.CertificateManager.PfxPassword} -nokeys");
            }

            //proxy.CertificateManager.CreateRootCertificate(persistToFile: true);
            //proxy.CertificateManager.TrustRootCertificateAsAdmin(machineTrusted: true);

            string proxyPassword = "Password123";
            proxy.ProxyBasicAuthenticateFunc = (args, username, password) =>
            {
                return Task.FromResult(username == proxyPassword || password == proxyPassword);
            };

            proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, proxyPort, decryptSsl: true));

            string wsl2hostIp = "172.28.160.1";
            if (wsl2hostIp != null)
            {
                proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Parse(wsl2hostIp), proxyPort, decryptSsl: true));
                string existingRule = await RunAsync(
                    "powershell",
                    "-Command \"Get-NetFirewallRule -DisplayName 'DevProxy from WSL2' -ErrorAction SilentlyContinue\""
                );
                if (!existingRule.Contains("DevProxy from WSL2"))
                {
                    await RunAsync(
                        "powershell",
                        "-Command \"New-NetFireWallRule -DisplayName 'DevProxy from WSL2' -Direction Inbound -LocalPort 8888 -Action Allow -Protocol TCP -LocalAddress '172.28.160.1' -RemoteAddress '172.28.160.1/20'\"",
                        admin: true
                    );
                }
            }
            proxy.Start();

            var pemForwardSlash = rootPem.Replace('\\','/');
            var pemFromWsl2 = "/mnt/" + char.ToLower(pemForwardSlash[0]) + pemForwardSlash.Substring(2);

            Console.WriteLine(@"For most apps:");
            Console.WriteLine($"  HTTP_PROXY=http://{proxyPassword}@localhost:{proxyPort}");
            Console.WriteLine(@"For windows git (via config):");
            Console.WriteLine($"  git config http.proxy http://user:{proxyPassword}@localhost:{proxyPort}");
            Console.WriteLine($"  git config --add http.sslcainfo {pemForwardSlash}");
            Console.WriteLine(@"For windows git (via env var):");
            Console.WriteLine($"  HTTP_PROXY=http://user:{proxyPassword}@localhost:{proxyPort}");
            Console.WriteLine($"  GIT_PROXY_SSL_CAINFO={pemForwardSlash}");
            if (wsl2hostIp != null)
            {
                Console.WriteLine(@"For WSL2 git (via env var):");
                Console.WriteLine($"  HTTP_PROXY=http://user:{proxyPassword}@{wsl2hostIp}:{proxyPort}");
                Console.WriteLine($"  GIT_PROXY_SSL_CAINFO={pemFromWsl2}");
            }

            Console.WriteLine(@"Started!");

            while (true)
            {
                Thread.Sleep(TimeSpan.FromMinutes(1));
            }
        }

        private static async Task<string> RunAsync(string fileName, string arguments, bool admin = false, string stdin = null)
        {
            var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                Verb = admin ? "runas" : null,
                UseShellExecute = admin,
                RedirectStandardInput = !string.IsNullOrEmpty(stdin),
                RedirectStandardOutput = !admin,
                RedirectStandardError = !admin,
            });
            Task writeTask = stdin == null
                ? Task.CompletedTask
                : Task.Run(() => process.StandardInput.WriteLineAsync(stdin));
            Task<string> stdoutTask = admin
                ? Task.FromResult(string.Empty)
                : process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = admin
                ? Task.FromResult(string.Empty)
                : process.StandardError.ReadToEndAsync();
            await Task.WhenAll(writeTask, stdoutTask, stderrTask, process.WaitForExitAsync());
            string stdout = (await stdoutTask).Trim();
            string stderr = (await stderrTask).Trim();
            // if (process.ExitCode != 0)
            // {
            //     throw new Exception($"{fileName} {arguments} failed with exit code {process.ExitCode}: {stdout} {stderr}");
            // }
            return stdout;
        }


    }
}
