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

            string baseSecret = "StoreSomethingRandomInDPAPI";
            string proxyPassword = HasherHelper.HashSecret(baseSecret + DateTime.Now.ToLongDateString());

            int proxyPort = 8888;
            string wsl2hostIp = "172.28.160.1";

            bool logRequests = false;

            int maxCachedConnectionsPerHost = 64;
            string upstreamHttpProxy = Environment.GetEnvironmentVariable("http_proxy");
            string upstreamHttpsProxy = Environment.GetEnvironmentVariable("https_proxy");

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
                    case "--upstream_http_proxy":
                        upstreamHttpProxy = value;
                        break;
                    case "--upstream_https_proxy":
                        upstreamHttpsProxy = value;
                        break;
                    case "--max_cached_connections_per_host":
                        maxCachedConnectionsPerHost = int.Parse(value);
                        break;
                    case "--log_requests":
                        logRequests = bool.Parse(value);
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

            proxy.MaxCachedConnections = maxCachedConnectionsPerHost;

            Func<string, ExternalProxy> parseProxy = (url) =>
            {
                var proxyUrl = new Uri(url);
                string user = null;
                string password = null;
                if (proxyUrl.UserInfo != null)
                {
                    string[] userInfoTokens = proxyUrl.UserInfo.Split(':');
                    user = userInfoTokens[0];
                    if (userInfoTokens.Length > 1)
                    {
                        password = userInfoTokens[1];
                    }
                }

                return new ExternalProxy(proxyUrl.Host, proxyUrl.Port, user, password);
            };


            if (!string.IsNullOrEmpty(upstreamHttpProxy))
            {
                proxy.UpStreamHttpProxy = parseProxy(upstreamHttpProxy);
            }

            if (!string.IsNullOrEmpty(upstreamHttpsProxy))
            {
                proxy.UpStreamHttpsProxy = parseProxy(upstreamHttpsProxy);
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
                var url = new Uri(args.HttpClient.Request.Url);

                RequestContext ctxt = args.GetRequestContext();
                if (ctxt == null)
                {
                    ctxt = new RequestContext(args);
                    args.UserData = ctxt;
                }

                string method = ctxt.Args.HttpClient.Request.Method.ToUpperInvariant();
                if (method == "CONNECT")
                {
                    if (!ctxt.ConnectSeen)
                    {
                        if (logRequests)
                        {
                            Console.WriteLine($"CONNECT {url}");
                        }
                        ctxt.ConnectSeen = true;
                    }
                }
                else
                {
                    if (!ctxt.RequestSeen)
                    {
                        if (logRequests)
                        {
                            Console.WriteLine($"{method} {url}");
                        }
                        ctxt.RequestSeen = true;
                    }
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

            proxy.OnServerConnectionCreate += async (sender, args) => {
                // Console.WriteLine("Connected to remote: " + args.RemoteEndPoint.ToString());
                return Task.CompletedTask;
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
                if (logRequests)
                {
                    Console.WriteLine($"END  {args.HttpClient?.Request?.Method} {args.HttpClient?.Request?.Url} {args.HttpClient?.Response?.StatusCode}");
                }
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
                    if (logRequests)
                    {
                        Console.WriteLine($"END  {args.HttpClient?.Request?.Method} {args.HttpClient?.Request?.Url} {args.HttpClient?.Response?.StatusCode}");
                    }
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
                    Console.WriteLine($"END  {args.HttpClient?.Request?.Method} {args.HttpClient?.Request?.Url} {e.Message}");
                }
                finally
                {
                    if (logRequests)
                    {
                        Console.WriteLine($"END  {args.HttpClient?.Request?.Method} {args.HttpClient?.Request?.Url} {args.HttpClient?.Response?.StatusCode}");
                    }
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
            var currentExePath = Assembly.GetEntryAssembly().Location.Replace(".dll", ".exe");
            var currentExeWsl2Path = ProcessHelpers.ConvertToWSL2Path(currentExePath);

            Console.WriteLine(@"For most apps:");
            Console.WriteLine($"  $env:HTTP_PROXY = \"http://user:$({currentExePath} --get_token)@localhost:{proxyPort}\"");
            Console.WriteLine(@"For windows git (via config):");
            // Console.WriteLine($"  git config http.proxy http://user:{proxyPassword}@localhost:{proxyPort}");
            Console.WriteLine($"  git config --add http.sslcainfo {pemForwardSlash}");
            Console.WriteLine(@"For windows git (via env var):");
            // Console.WriteLine($"  HTTP_PROXY=http://user:{proxyPassword}@localhost:{proxyPort}");
            Console.WriteLine($"  GIT_PROXY_SSL_CAINFO={pemForwardSlash}");

            if (wsl2hostIp != null)
            {
                string linuxPemPath = "/etc/ssl/certs/devproxy.pem";
                //https://stackoverflow.com/questions/51176209/http-proxy-not-working
                // curl requires lowercase http_proxy env var name
                Console.WriteLine(@"For WSL2 (Ubuntu tested):");
                Console.WriteLine($"  1. Once, install root cert");
                Console.WriteLine($"       sudo apt install ca-certificates");
                Console.WriteLine($"       sudo cp {pemFromWsl2} {linuxPemPath}");
                Console.WriteLine($"       sudo update-ca-certificates --verbose --fresh | grep -i devproxy");
                Console.WriteLine($"  2. Set envvars to enable");
                Console.WriteLine($"       export http_proxy=http://user:$({currentExeWsl2Path} --get_token)@{wsl2hostIp}:{proxyPort}");
                Console.WriteLine($"       export https_proxy=$http_proxy");
                Console.WriteLine($"       export NODE_EXTRA_CA_CERTS={linuxPemPath}");
            }

            Console.WriteLine(@"Started!");

            while (true)
            {
                await Task.Delay(1000);
            }
        }
    }
}
