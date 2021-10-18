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
                bool ignoreOtherArgs = false;
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
                    case "--run":
                        ignoreOtherArgs = true;
                        break;
                    case "--get_token":
                    case "--get_proxy":
                    case "--get_wsl_proxy":
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument: `{key}={value}`");
                }

                if (ignoreOtherArgs)
                {
                    break;
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

            Func<string, Task<string>> sendIpcCommandAsync = async (string message) =>
            {
                try
                {
                    return await Ipc.SendAsync(proxy.pipeName, message, CancellationToken.None);
                }
                catch (TimeoutException)
                {
                    await Console.Error.WriteLineAsync("Timeout calling DevProxy over IPC.");
                    await Console.Error.FlushAsync();
                    Environment.Exit(1);
                    throw new NotImplementedException("unreachable");
                }
            };

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
                            string response = await sendIpcCommandAsync(message);
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
                            string response = await sendIpcCommandAsync(message);
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

            string winHelp = @"
For Windows
-----------
For most apps:
$env:http_proxy = $env:https_proxy = ""$({currentExePath} --get_proxy)""

For git (via config):
git config --add http.sslcainfo {pemForwardSlash}
For git (via env var):
$env:GIT_PROXY_SSL_CAINFO={pemForwardSlash}
";
            winHelp = winHelp.Trim()
                .Replace("{currentExePath}", currentExePath)
                .Replace("{pemForwardSlash}", pemForwardSlash);
            Console.WriteLine(winHelp);
            Console.WriteLine();

            if (proxy.wsl2hostIpInfo != null)
            {
                string linuxPemPath = "/etc/ssl/certs/devproxy.pem";
                //https://stackoverflow.com/questions/51176209/http-proxy-not-working
                // curl requires lowercase http_proxy env var name
                string wslHelp= @"
For WSL2 (Ubuntu tested)
------------------------
1. Once, install root cert:
sudo apt install ca-certificates
sudo cp {pemFromWsl2} {linuxPemPath}
sudo update-ca-certificates --verbose --fresh | grep -i devproxy

2. For each shell either set envvars to enable:
export http_proxy=$({currentExeWsl2Path} --get_wsl_proxy)
export https_proxy=$http_proxy
export NODE_EXTRA_CA_CERTS={linuxPemPath}

3. Or configure .bashrc to do it for you:
devproxy_http_proxy=$({currentExeWsl2Path} --get_wsl_proxy)
if [ $? -eq 0 ]; then
  export http_proxy=$devproxy_http_proxy
  export https_proxy=$devproxy_http_proxy
  export NODE_EXTRA_CA_CERTS=/etc/ssl/certs/devproxy.pem
  echo 'DevProxy configured.'
else
  echo 'See above for error connecting to DevProxy.'
fi
";
                wslHelp = wslHelp.Trim()
                    .Replace("{pemFromWsl2}", pemFromWsl2)
                    .Replace("{linuxPemPath}", linuxPemPath)
                    .Replace("{currentExeWsl2Path}", currentExeWsl2Path);
                Console.WriteLine(wslHelp);
                Console.WriteLine();
            }

            Console.WriteLine(@"Started!");

            while (true)
            {
                await Task.Delay(1000);
            }
        }
    }
}
