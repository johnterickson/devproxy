using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

            var argKvps = Enumerable.Range(0, args.Length).Select(i =>
            {
                string arg = args[i];
                var tokens = arg.Split('=');
                string key = tokens[0].ToLowerInvariant();
                string value = tokens.Length > 1 ? tokens[1] : null;
                return (i, key, value);
            }).ToArray();

            var config = new Configuration();

            var configKvp = argKvps.FirstOrDefault(arg => arg.key == "--config");
            if (configKvp.key != null)
            {
                string configText = await File.ReadAllTextAsync(configKvp.value);
                config = JsonSerializer.Deserialize<Configuration>(configText);
            }

            foreach ((int i, string key, string value) in argKvps)
            {
                switch (key)
                {
                    case "--port":
                        config.proxy.port = int.Parse(value);
                        break;
                    case "--upstream_http_proxy":
                        config.proxy.upstream_http_proxy = value;
                        break;
                    case "--upstream_https_proxy":
                        config.proxy.upstream_https_proxy = value;
                        break;
                    case "--max_cached_connections_per_host":
                        config.proxy.max_cached_connections_per_host = int.Parse(value);
                        break;
                    case "--log_requests":
                        config.proxy.log_requests = bool.Parse(value);
                        break;
                    case "--password":
                        config.proxy.password_type = "fixed";
                        config.proxy.fixed_password = new FixedPasswordConfiguration()
                        {
                            value = value
                        };
                        break;
                    case "--get_token":
                    case "--run":
                    case "--get_proxy":
                    case "--get_wsl_proxy":
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument: `{key}={value}`");
                }
            }

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING")))
            {
                config.plugins.Add(new PluginConfiguration()
                {
                    class_name = nameof(AzureBlobSasRequestPlugin),
                    options = new Dictionary<string, object>()
                    {
                        {"connection_string_env_var", "STORAGE_CONNECTION_STRING"}
                    }
                });
            }

            // config.plugins.Add(new PluginConfiguration()
            // {
            //     class_name = nameof(LimitNetworkAccessProxyAuthPlugin),
            //     options = new Dictionary<string, object>()
            //     {
            //         {"rules", new}
            //     }
            // });

            var proxy = new DevProxy(config);

            foreach ((int i, string key, string value) in argKvps)
            {
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
                    case "--get_proxy":
                    case "--get_wsl_proxy":
                        {
                            string message = JsonSerializer.Serialize(new IpcMessage
                            {
                                Command = key.Substring(2),
                            });
                            string response = await Ipc.SendAsync(proxy.pipeName, message, CancellationToken.None);
                            Console.Write(response);
                            return 0;
                        }
                }
            }

            proxy.authPlugins.Add(new AuthorizationHeaderProxyAuthPlugin(proxy.Passwords));
            proxy.authPlugins.Add(new ProxyAuthorizationHeaderProxyAuthPluginInstance(proxy.Passwords));
            proxy.authPlugins.Add(new ProcessTreeProxyAuthPlugin(proxy.processTracker));

            proxy.plugins.Add(new BlobStoreCacheRequestPlugin());
            proxy.plugins.Add(new AzureDevOpsAuthRequestPlugin());
            proxy.plugins.Add(new ACRAuthRequestPluginInstance());

            await proxy.StartAsync();

            var pemForwardSlash = proxy.rootPem.Replace('\\', '/');
            var pemFromWsl2 = ProcessHelpers.ConvertToWSL2Path(proxy.rootPem);
            var currentExePath = Assembly.GetEntryAssembly().Location.Replace(".dll", ".exe");
            var currentExeWsl2Path = ProcessHelpers.ConvertToWSL2Path(currentExePath);

            Console.WriteLine(@"For most apps:");
            Console.WriteLine($"  $env:http_proxy = \"$({currentExePath} --get_proxy)\"");
            Console.WriteLine(@"For windows git (via config):");
            // Console.WriteLine($"  git config http.proxy http://user:{proxyPassword}@localhost:{proxyPort}");
            Console.WriteLine($"  git config --add http.sslcainfo {pemForwardSlash}");
            Console.WriteLine(@"For windows git (via env var):");
            // Console.WriteLine($"  http_proxy=http://user:{proxyPassword}@localhost:{proxyPort}");
            Console.WriteLine($"  GIT_PROXY_SSL_CAINFO={pemForwardSlash}");

            if (proxy.wsl2hostIpInfo != null)
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
                Console.WriteLine($"       export http_proxy=$({currentExeWsl2Path} --get_wsl_proxy)");
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
