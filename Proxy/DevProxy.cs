using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace DevProxy
{
    public sealed class DevProxy : IDisposable
    {
        public readonly Configuration configuration;

        public DevProxy(Configuration configuration)
        {
            this.configuration = configuration;
            this.configuration.proxy = this.configuration.proxy ?? new ProxyConfiguration();
            
            this.pipeName = configuration.proxy.ipcPipeName ?? "devproxy";
            this.ipcServer = new Ipc(pipeName, HandleIpc);

            this.proxyPort = configuration.proxy.port ?? 8888;
            this.logRequests = configuration.proxy.log_requests ?? false;

            string password_type = configuration.proxy.password_type ?? "rotating";

            this.ListenToWSL2 = configuration.proxy.listen_to_wsl2 ?? true;

            switch(password_type)
            {
                case "fixed":
                    this.Passwords = new FixedProxyPassword(this.configuration.proxy.fixed_password.value);
                    break;
                case "rotating":
                    if (configuration.proxy.rotating_password == null)
                    {
                        configuration.proxy.rotating_password = new RotatingPasswordConfiguration()
                        {
                            base_secret = "SomethingBetterHere",
                            generate_new_every_seconds = 3600,
                            passwords_lifetime_seconds = 24*3600,
                        };
                    }
                    var config = configuration.proxy.rotating_password;

                    this.Passwords = new RotatingPassword(
                        maxDuration: TimeSpan.FromSeconds(config.passwords_lifetime_seconds ?? 24*3600),
                        baseSecret: config.base_secret,
                        rotationRate: TimeSpan.FromSeconds(config.generate_new_every_seconds ?? 3600)
                    );
                    break;
                default:
                    throw new ArgumentException("unknown password type: " + password_type);
            }

            this.proxy.MaxCachedConnections = configuration.proxy.max_cached_connections_per_host ?? 64;

            string upstreamHttpProxy =
                configuration.proxy.upstream_http_proxy
                    ?? Environment.GetEnvironmentVariable("http_proxy");
            if (!string.IsNullOrEmpty(upstreamHttpProxy))
            {
                this.proxy.UpStreamHttpProxy = ParseProxy(upstreamHttpProxy);
            }

            string upstreamHttpsProxy = 
                configuration.proxy.upstream_https_proxy 
                    ?? Environment.GetEnvironmentVariable("https_proxy");
            if (!string.IsNullOrEmpty(upstreamHttpsProxy))
            {
                var httpsProxy = ParseProxy(upstreamHttpsProxy);
                this.proxy.UpStreamHttpsProxy = httpsProxy;

                var upstream = new WebProxy(httpsProxy.HostName, httpsProxy.Port);
                upstream.UseDefaultCredentials = false;

                var handler = new HttpClientHandler();
                handler.Proxy = upstream;
                handler.UseProxy = true;

                HttpClient = new HttpClient(handler);
            }
            else
            {
                HttpClient = new HttpClient();
            }

            proxy.BeforeRequest += OnBeforeRequestAsync;
            proxy.BeforeResponse += OnBeforeResponseAsync;

            proxy.OnServerConnectionCreate += (sender, args) =>
            {
                // Console.WriteLine("Connected to remote: " + args.RemoteEndPoint.ToString());
                return Task.CompletedTask;
            };


            if (this.ListenToWSL2)
            {
                foreach(var i in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (i.Name == "vEthernet (WSL)")
                    {
                        var props = i.GetIPProperties();
                        var addresses = props.UnicastAddresses;
                        wsl2hostIp = addresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                        break;
                    }
                }
            }

            Action<IPAddress> createEndpoint = (address) => {
                var endpoint = new ExplicitProxyEndPoint(address, proxyPort, decryptSsl: true);
                endpoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnectRequestAsync;
                endpoint.BeforeTunnelConnectResponse += OnBeforeTunnelConnectResponseAsync;
                proxy.AddEndPoint(endpoint);
            };

            createEndpoint(IPAddress.Loopback);

            if (wsl2hostIp != null)
            {
                createEndpoint(wsl2hostIp.Address);
            }
        }

        private static CancellationTokenSource _shutdown = new CancellationTokenSource();

        public readonly ProxyServer proxy = new ProxyServer();

        public readonly ProcessTracker processTracker = new ProcessTracker();

        public readonly IProxyPasword Passwords;

        public readonly string pipeName;

        public readonly int proxyPort;

        public readonly bool ListenToWSL2;
        public UnicastIPAddressInformation wsl2hostIp {get; private set;}

        public readonly bool logRequests;

        public readonly string upstreamHttpProxy;
        public readonly string upstreamHttpsProxy = Environment.GetEnvironmentVariable("https_proxy");

        public readonly HttpClient HttpClient;

        private readonly Ipc ipcServer;

        public List<IProxyAuthPlugin> authPlugins = new List<IProxyAuthPlugin>();
        public List<IRequestPlugin> plugins = new List<IRequestPlugin>();

        public string rootPfx;
        public string rootPem;

        public X509Certificate2 rootCert => proxy.CertificateManager.RootCertificate;

        public void Dispose()
        {
            ipcServer.Dispose();
            processTracker.Dispose();
            proxy.Stop();
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
                        return Passwords.GetCurrent();
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
            if (wsl2hostIp != null)
            {
                await OpenFirewallToWSL2Async();
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

        private async Task OnBeforeRequestAsync(object sender, SessionEventArgs args)
        {
            await InitRequestAsync(args);
            var ctxt = args.GetRequestContext();

            if (!ctxt.IsAuthenticated)
            {
                ctxt.ReturnProxy407();
                ctxt.AddAuthNotesToResponse();
                args.Respond(args.HttpClient.Response);
                return;
            }

            foreach (var plugin in plugins)
            {
                var result = await plugin.BeforeRequestAsync(args);
                if (result == RequestPluginResult.Stop)
                {
                    return;
                }
            }
        }


        private async Task OnBeforeResponseAsync(object sender, SessionEventArgs args)
        {
            var ctxt = args.GetRequestContext();
            if (!ctxt.IsAuthenticated)
            {
                return;
            }

            ctxt.AddAuthNotesToResponse();

            try
            {
                foreach (var plugin in plugins.AsEnumerable().Reverse())
                {
                    var result = await plugin.BeforeResponseAsync(args);
                    if (result == RequestPluginResult.Stop)
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
                ctxt = new RequestContext(args, this);
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

                    if (result == ProxyAuthPluginResult.Authenticated)
                    {
                        ctxt.IsAuthenticated = true;
                        break;
                    }
                    else if (result == ProxyAuthPluginResult.Rejected)
                    {
                        break;
                    }
                }
            }
        }

        private static SemaphoreSlim _installRootLock = new SemaphoreSlim(1,1);

        private async Task EnsureRootCertIsInstalledAsync()
        {
            try
            {
                await _installRootLock.WaitAsync();
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
            }
            finally
            {
                _installRootLock.Release();
            }
        }

        private static SemaphoreSlim _firewallLock = new SemaphoreSlim(1,1);
        private async Task OpenFirewallToWSL2Async()
        {
            string ruleName = $"DevProxy listening to {proxyPort} from WSL2 {wsl2hostIp.Address}/{wsl2hostIp.PrefixLength}";
            try
            {
                await _firewallLock.WaitAsync();
                string existingRule = await ProcessHelpers.RunAsync(
                    "powershell",
                    $"-Command \"Get-NetFirewallRule -DisplayName '{ruleName}' -ErrorAction SilentlyContinue\""
                );
                
                if (!existingRule.Contains(ruleName))
                {
                    await ProcessHelpers.RunAsync(
                        "powershell",
                        $"-Command \"New-NetFireWallRule -DisplayName '{ruleName}' -Direction Inbound -LocalPort {proxyPort} -Action Allow -Protocol TCP " +
                        $"-LocalAddress '{wsl2hostIp.Address}' " + 
                        $"-RemoteAddress '{wsl2hostIp.Address}/{wsl2hostIp.PrefixLength}'\"",
                        admin: true
                    );
                }
            }
            finally
            {
                _firewallLock.Release();
            }
        }
    }
}
