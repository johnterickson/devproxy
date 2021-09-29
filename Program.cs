using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
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
            string proxyPassword = Guid.NewGuid().ToString("N");

            string wsl2hostIp = "172.28.160.1";

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
                    case "--get_token":
                        {
                            string message = JsonSerializer.Serialize(new IpcMessage
                            {
                                Command = "get_token",
                            });
                            string response = await Ipc.SendAsync(pipeName, message, CancellationToken.None);
                            Console.Write(response);
                            return 0;
                        }
                    case "--port":
                        proxyPort = int.Parse(value);
                        break;
                    case "--win_auth":
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
                            case "get_token":
                                return proxyPassword;
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

            var authPlugins = new List<IAuthPlugin>();
            authPlugins.Add(new ProxyPasswordAuthPlugin(proxyPassword));
            authPlugins.Add(new ProcessTreeAuthPlugin(processTracker));

            var plugins = new List<IPlugin>();
            plugins.Add(new BlobStoreCachePlugin());
            plugins.Add(new AzureDevOpsAuthPlugin());

            Func<SessionEventArgsBase, Task> initAsync = async (args) =>
            {
                RequestContext ctxt = args.GetRequestContext();
                if (ctxt == null)
                {
                    ctxt = new RequestContext(args);
                    args.UserData = ctxt;

                    var url = new Uri(args.HttpClient.Request.Url);
                    Console.WriteLine($"START {url}");
                }

                if (!ctxt.IsAuthenticated)
                {
                    foreach (var plugin in authPlugins)
                    {
                        var (result, notes) = await plugin.BeforeRequestAsync(args);
                        ctxt.AuthToProxyNotes.Add((plugin, $"{result.ToString()}_{notes}"));

                        if (result == AuthPluginResult.Authenticated)
                        {
                            ctxt.IsAuthenticated = true;
                            break;
                        }
                        else if (result == AuthPluginResult.Rejected)
                        {
                            break;
                        }
                    }
                }
            };

            proxy.BeforeRequest += async (sender, args) =>
            {
                await initAsync(args);
                var ctxt = args.GetRequestContext();

                if (!ctxt.IsAuthenticated)
                {
                    ctxt.ReturnProxy407();
                    ctxt.AddAuthNotesToResponse();
                    return;
                }

                foreach (var plugin in plugins)
                {
                    var result = await plugin.BeforeRequestAsync(args);
                    if (result == PluginResult.Stop)
                    {
                        return;
                    }
                }
            };


            var localhostEndpoint = new ExplicitProxyEndPoint(IPAddress.Loopback, proxyPort, decryptSsl: true);
            localhostEndpoint.BeforeTunnelConnectRequest += (sender, args) => initAsync(args);
            localhostEndpoint.BeforeTunnelConnectResponse += (sender, args) =>
            {
                var ctxt = args.GetRequestContext();
                if (!ctxt.IsAuthenticated)
                {
                    ctxt.ReturnProxy407();
                }
                ctxt.AddAuthNotesToResponse();
                return Task.CompletedTask;
            };
            proxy.AddEndPoint(localhostEndpoint);

            if (!string.IsNullOrEmpty(wsl2hostIp))
            {
                var wsl2Endpoint = new ExplicitProxyEndPoint(IPAddress.Parse(wsl2hostIp), proxyPort, decryptSsl: true);
                wsl2Endpoint.BeforeTunnelConnectRequest += (sender, args) => initAsync(args);
                wsl2Endpoint.BeforeTunnelConnectResponse += (sender, args) =>
                {
                    var ctxt = args.GetRequestContext();
                    if (!ctxt.IsAuthenticated)
                    {
                        ctxt.ReturnProxy407();
                    }
                    ctxt.AddAuthNotesToResponse();
                    return Task.CompletedTask;
                };
                proxy.AddEndPoint(wsl2Endpoint);
            }

            proxy.BeforeResponse += async (sender, args) =>
            {
                var ctxt = args.GetRequestContext();
                ctxt.AddAuthNotesToResponse();

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

            if (wsl2hostIp != null)
            {
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
            var pemFromWsl2 = ProcessHelpers.ConvertToWSL2Path(rootPem);
            var currentExePath = Assembly.GetEntryAssembly().Location.Replace(".dll",".exe");
            var currentExeWsl2Path = ProcessHelpers.ConvertToWSL2Path(currentExePath);

            Console.WriteLine(@"For most apps:");
            Console.WriteLine($"  $env:HTTP_PROXY = \"http://$({currentExePath} --get_token)@localhost:{proxyPort}\"");
            Console.WriteLine(@"For windows git (via config):");
            // Console.WriteLine($"  git config http.proxy http://user:{proxyPassword}@localhost:{proxyPort}");
            Console.WriteLine($"  git config --add http.sslcainfo {pemForwardSlash}");
            Console.WriteLine(@"For windows git (via env var):");
            // Console.WriteLine($"  HTTP_PROXY=http://user:{proxyPassword}@localhost:{proxyPort}");
            Console.WriteLine($"  GIT_PROXY_SSL_CAINFO={pemForwardSlash}");

            if (wsl2hostIp != null)
            {
                Console.WriteLine(@"For WSL2 (Ubuntu tested):");
                Console.WriteLine($"  1. Once, install root cert");
                Console.WriteLine($"       cp {pemFromWsl2} /etc/ssl/certs/");
                Console.WriteLine($"       sudo update-ca-certificates --verbose --fresh | grep -i devproxy");
                Console.WriteLine($"  2. Set envvars to enable");
                Console.WriteLine($"       export HTTP_PROXY=http://$({currentExeWsl2Path} --get_token)@{wsl2hostIp}:{proxyPort}");
                Console.WriteLine($"       export HTTPS_PROXY=$HTTP_PROXY");
                //Console.WriteLine($"  GIT_PROXY_SSL_CAINFO={pemFromWsl2}");
            }

            Console.WriteLine(@"Started!");

            while (true)
            {
                await Task.Delay(1000);
            }
        }
    }
}
