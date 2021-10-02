using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace DevProxy
{
    public class DevProxy
    {
        public ProxyServer proxy = new ProxyServer();

        public ProcessTracker processTracker = new ProcessTracker();

        public string baseSecret = "StoreSomethingRandomInDPAPI";
        public string proxyPassword;

        public string pipeName;

        public int proxyPort = 8888;
        public string wsl2hostIp = "172.28.160.1";

        public bool logRequests = false;
        public int maxCachedConnectionsPerHost = 64;

        public string upstreamHttpProxy = Environment.GetEnvironmentVariable("http_proxy");
        public string upstreamHttpsProxy = Environment.GetEnvironmentVariable("https_proxy");

        private Ipc ipcServer;

        public List<IAuthPlugin> authPlugins = new List<IAuthPlugin>();
        public List<IPlugin> plugins = new List<IPlugin>();

        public string rootPfx;
        public string rootPem;

        public DevProxy()
        {
            pipeName = "devproxy";

            proxyPassword = HasherHelper.HashSecret(baseSecret + DateTime.Now.ToLongDateString());
        }

        private async Task<string> HandleIpc(object sender, string request_json, CancellationToken cancellationToken)
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
        }

        public async Task StartAsync()
        {
            proxy.MaxCachedConnections = maxCachedConnectionsPerHost;

            if (!string.IsNullOrEmpty(upstreamHttpProxy))
            {
                proxy.UpStreamHttpProxy = ParseProxy(upstreamHttpProxy);
            }

            if (!string.IsNullOrEmpty(upstreamHttpsProxy))
            {
                proxy.UpStreamHttpsProxy = ParseProxy(upstreamHttpsProxy);
            }

            ipcServer = new Ipc(pipeName, HandleIpc);

            proxy.BeforeRequest += OnBeforeRequestAsync;
            proxy.BeforeResponse += OnBeforeResponseAsync;

            proxy.OnServerConnectionCreate += (sender, args) =>
            {
                // Console.WriteLine("Connected to remote: " + args.RemoteEndPoint.ToString());
                return Task.CompletedTask;
            };

            Action<IPAddress> createEndpoint = (address) => {
                var endpoint = new ExplicitProxyEndPoint(address, proxyPort, decryptSsl: true);
                endpoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnectRequestAsync;
                endpoint.BeforeTunnelConnectResponse += OnBeforeTunnelConnectResponseAsync;
                proxy.AddEndPoint(endpoint);
            };

            createEndpoint(IPAddress.Loopback);

            if (!string.IsNullOrEmpty(wsl2hostIp))
            {
                createEndpoint(IPAddress.Parse(wsl2hostIp));
            }

            await EnsureRootCertIsInstalledAsync();

            proxy.Start();
        }

        private async Task OnBeforeTunnelConnectRequestAsync(object sender, TunnelConnectSessionEventArgs args)
        {
            string host = args.HttpClient.Request.RequestUri.Host;

            await InitRequestAsync(args);
            var ctxt = args.GetRequestContext();
            if (!ctxt.IsAuthenticated)
            {
                args.DenyConnect = true;
                ctxt.ReturnProxy407();
                ctxt.AddAuthNotesToResponse();
                return;
            }

            bool shouldDecrypt = plugins.Any(p => p.IsHostRelevant(host));
            if (!shouldDecrypt)
            {
                args.DecryptSsl = false;
            }
        }

        private Task OnBeforeTunnelConnectResponseAsync(object sender, TunnelConnectSessionEventArgs args)
        {
            if (!args.DecryptSsl)
            {
                return Task.CompletedTask;
            }

            var ctxt = args.GetRequestContext();
            if (!ctxt.IsAuthenticated)
            {
                throw new UnauthorizedAccessException();
            }
            ctxt.AddAuthNotesToResponse();
            if (logRequests)
            {
                Console.WriteLine($"END  {args.HttpClient?.Request?.Method} {args.HttpClient?.Request?.Url} {args.HttpClient?.Response?.StatusCode}");
            }
            return Task.CompletedTask;
        }


        private async Task OnBeforeResponseAsync(object sender, SessionEventArgs args)
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
        }

        private async Task OnBeforeRequestAsync(object sender, SessionEventArgs args)
        {
            await InitRequestAsync(args);
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
        }

        private static ExternalProxy ParseProxy(string proxyString)
        {
            var proxyUrl = new Uri(proxyString);
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
        }

        private async Task InitRequestAsync(SessionEventArgsBase args)
        {
            var url = new Uri(args.HttpClient.Request.Url);

            RequestContext ctxt = args.GetRequestContext();
            if (ctxt == null)
            {
                ctxt = new RequestContext(args);
                args.UserData = ctxt;
            }

            if (logRequests)
            {
                Console.WriteLine($"{ctxt.Request.Method} {ctxt.Request.Url}");
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
        }

        private async Task EnsureRootCertIsInstalledAsync()
        {
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

            rootPfx = certStorage.GetRootCertPath(proxy.CertificateManager.PfxFilePath);
            rootPem = rootPfx.Replace(".pfx", ".pem");

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
        }
    }
}
