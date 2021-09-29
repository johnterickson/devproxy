using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

namespace DevProxy
{

    public class Program
    {
        static async Task<int> Main(string[] args)
        {
            var proxy = new ProxyServer();

            string pipeName = "devproxy";

            var processTracker = new ProcessTracker();

            int proxyPort = 8888;
            string proxyPassword = "Password123";
            var plugins = new List<IPlugin>();

            foreach (int i in Enumerable.Range(0, args.Length))
            {
                string arg = args[i];
                string[] tokens = arg.Split('=');
                string key = tokens[0].ToLowerInvariant();
                string value = tokens.Length > 1 ? tokens[1] : null;
                switch (key)
                {
                    case "--run":
                        {
                            string message = JsonSerializer.Serialize(new IpcMessage
                            {
                                Command = "add_auth_root",
                                Args = new Dictionary<string, string>() {
                                    { "process_id", Process.GetCurrentProcess().Id.ToString()}
                                }
                            });
                            string response = await Ipc.SendAsync(pipeName, message, CancellationToken.None);
                            if (response != "OK")
                            {
                                throw new Exception(response);
                            }

                            var p = Process.Start(new ProcessStartInfo(
                                args[i + 1],
                                ProcessHelpers.EscapeCommandLineArguments(args.Skip(i + 2))
                            ));
                            await p.WaitForExitAsync();
                            return p.ExitCode;
                        }
                    case "--port":
                        proxyPort = int.Parse(value);
                        break;
                    case "--winauth":
                        proxy.EnableWinAuth = bool.Parse(value);
                        break;
                    case "--password":
                        proxyPassword = value;
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument: `{arg}`");
                }
            }

            var ipcServer = new Ipc(
                pipeName,
                async (ctxt, request_json, ct) =>
                {
                    try
                    {
                        Console.WriteLine($"Request: {request_json}");
                        var request = JsonSerializer.Deserialize<IpcMessage>(request_json);
                        switch (request.Command)
                        {
                            case "add_auth_root":
                                uint processId = uint.Parse(request.Args["process_id"]);

                                var start = Stopwatch.StartNew();
                                while (!processTracker.TrySetAuthRoot(processId))
                                {
                                    if (start.Elapsed.TotalSeconds > 10)
                                    {
                                        throw new Exception("Process not found.");
                                    }
                                    await Task.Delay(100);
                                }
                                return "OK";
                            default:
                                throw new ArgumentException($"Unknown command: `{request.Command}`");
                        }
                    }
                    catch (Exception ex)
                    {
                        return ex.Message;
                    }
                });

            Task ipcServerTask = ipcServer.RunServerAsync(CancellationToken.None);


            plugins.Add(new ProxyPasswordAuthPlugin(proxyPassword));
            plugins.Add(new ProcessTreeAuthPlugin(processTracker));

            plugins.Add(new AuthRequiredPlugin());

            plugins.Add(new BlobStoreCachePlugin());
            plugins.Add(new AzureDevOpsAuthPlugin());

            proxy.BeforeRequest += async (sender, args) =>
            {
                args.UserData = new RequestContext(args);

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
                    foreach (var plugin in plugins.AsEnumerable().Reverse())
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
                string gitPath = await ProcessHelpers.RunAsync("where.exe", "git.exe");
                gitPath = Path.GetDirectoryName(gitPath);
                gitPath = Path.GetDirectoryName(gitPath);
                string opensslPath = Path.Combine(gitPath, "mingw64", "bin", "openssl.exe");
                await ProcessHelpers.RunAsync(opensslPath, $"pkcs12 -in \"{rootPfx}\" -out \"{rootPem}\" -password pass:{proxy.CertificateManager.PfxPassword} -nokeys");
            }

            var netstatRegex = new Regex(
                @"^\s+TCP\s+[0-9\.]+:([0-9]+)\s+[0-9\.]+:" + proxyPort + @"\s+ESTABLISHED\s+([0-9]+)$",
                RegexOptions.Compiled | RegexOptions.Multiline
            );

            proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, proxyPort, decryptSsl: true));

            string wsl2hostIp = "172.28.160.1";
            if (wsl2hostIp != null)
            {
                proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Parse(wsl2hostIp), proxyPort, decryptSsl: true));
                string existingRule = await ProcessHelpers.RunAsync(
                    "powershell",
                    "-Command \"Get-NetFirewallRule -DisplayName 'DevProxy from WSL2' -ErrorAction SilentlyContinue\""
                );
                if (!existingRule.Contains("DevProxy from WSL2"))
                {
                    await ProcessHelpers.RunAsync(
                        "powershell",
                        "-Command \"New-NetFireWallRule -DisplayName 'DevProxy from WSL2' -Direction Inbound -LocalPort 8888 -Action Allow -Protocol TCP -LocalAddress '172.28.160.1' -RemoteAddress '172.28.160.1/20'\"",
                        admin: true
                    );
                }
            }
            proxy.Start();

            var pemForwardSlash = rootPem.Replace('\\', '/');
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
                await Task.Delay(1000);
            }
        }
    }
}
