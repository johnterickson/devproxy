using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DevProxy
{
    public class Program
    {
        
        static async Task<int> Main(string[] args)
        {
            var proxy = new DevProxy();

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
                            string response = await Ipc.SendAsync(proxy.pipeName, message, CancellationToken.None);
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
                            string response = await Ipc.SendAsync(proxy.pipeName, message, CancellationToken.None);
                            Console.Write(response);
                            return 0;
                        }
                    case "--port":
                        proxy.proxyPort = int.Parse(value);
                        break;
                    case "--upstream_http_proxy":
                        proxy.upstreamHttpProxy = value;
                        break;
                    case "--upstream_https_proxy":
                        proxy.upstreamHttpsProxy = value;
                        break;
                    case "--max_cached_connections_per_host":
                        proxy.maxCachedConnectionsPerHost = int.Parse(value);
                        break;
                    case "--log_requests":
                        proxy.logRequests = bool.Parse(value);
                        break;
                    case "--win_auth":
                        proxy.proxy.EnableWinAuth = bool.Parse(value);
                        break;
                    case "--password":
                        proxy.proxyPassword = value;
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument: `{arg}`");
                }
            }
            
            proxy.authPlugins.Add(new ProxyPasswordAuthPlugin(proxy.proxyPassword));
            proxy.authPlugins.Add(new ProcessTreeAuthPlugin(proxy.processTracker));

            proxy.plugins.Add(new BlobStoreCachePlugin());
            proxy.plugins.Add(new AzureDevOpsAuthPlugin());
            
            await proxy.StartAsync();
            
            var pemForwardSlash = proxy.rootPem.Replace('\\', '/');
            var pemFromWsl2 = ProcessHelpers.ConvertToWSL2Path(proxy.rootPem);
            var currentExePath = Assembly.GetEntryAssembly().Location.Replace(".dll", ".exe");
            var currentExeWsl2Path = ProcessHelpers.ConvertToWSL2Path(currentExePath);

            Console.WriteLine(@"For most apps:");
            Console.WriteLine($"  $env:HTTP_PROXY = \"http://user:$({currentExePath} --get_token)@localhost:{proxy.proxyPort}\"");
            Console.WriteLine(@"For windows git (via config):");
            // Console.WriteLine($"  git config http.proxy http://user:{proxyPassword}@localhost:{proxyPort}");
            Console.WriteLine($"  git config --add http.sslcainfo {pemForwardSlash}");
            Console.WriteLine(@"For windows git (via env var):");
            // Console.WriteLine($"  HTTP_PROXY=http://user:{proxyPassword}@localhost:{proxyPort}");
            Console.WriteLine($"  GIT_PROXY_SSL_CAINFO={pemForwardSlash}");

            if (proxy.wsl2hostIp != null)
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
                Console.WriteLine($"       export http_proxy=http://user:$({currentExeWsl2Path} --get_token)@{proxy.wsl2hostIp}:{proxy.proxyPort}");
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
