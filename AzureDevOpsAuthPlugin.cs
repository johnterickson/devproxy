using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;

namespace DevProxy
{
    public class AzureDevOpsAuthPlugin : Plugin
    {
        private const string NativeClientRedirect = "https://login.microsoftonline.com/common/oauth2/nativeclient";
        private const string Resource = "499b84ac-1321-427f-aa17-267ca6975798/.default";
        private const string ClientId = "872cd9fa-d31f-45e0-9eab-6e460a02d1f1";
        private const string Authority = "https://login.microsoftonline.com/organizations";

        private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
        {
            string token = Environment.GetEnvironmentVariable("AZURE_DEVOPS_TOKEN");
            if (token != null)
            {
                return token;
            }

            var msalToken = await AcquireTokenWithWindowsIntegratedAuth(cancellationToken);

            //var msalToken = await AcquireTokenSilentlyAsync(token);
            return msalToken.AccessToken;
        }

        public async Task<PluginResult> BeforeRequestAsync(SessionEventArgs e)
        {
            var url = new Uri(e.HttpClient.Request.Url);
            if (url.Host.EndsWith("dev.azure.com") || url.Host.EndsWith("visualstudio.com"))
            {
                if (e.HttpClient.Request.Headers.All(h => h.Name != "Authorization"))
                {
                    var token = await GetTokenAsync(CancellationToken.None);
                    e.HttpClient.Request.Headers.AddHeader("Authorization", $"Bearer {token}");
                }
            }

            return PluginResult.Continue;
        }

        public Task<PluginResult> BeforeResponseAsync(SessionEventArgs e)
        {
            return Task.FromResult(PluginResult.Continue);
        }

        internal interface IMsalToken
        {
            string AccessTokenType { get; }

            string AccessToken { get; }
        }

        internal class MsalToken : IMsalToken
        {
            public MsalToken(string accessToken)
            {
                this.AccessToken = accessToken;
            }

            public MsalToken(AuthenticationResult authenticationResult)
                : this(authenticationResult.AccessToken)
            {
            }

            public string AccessTokenType => "Bearer";

            public string AccessToken { get; }
        }

        async Task<IMsalToken> AcquireTokenWithWindowsIntegratedAuth(CancellationToken cancellationToken)
        {
            var publicClient = await GetPCAAsync().ConfigureAwait(false);

            try
            {
                string upn = WindowsIntegratedAuthUtils.GetUserPrincipalName();
                if (upn == null)
                {
                    return null;
                }

                var builder = publicClient.AcquireTokenByIntegratedWindowsAuth(new string[] { Resource });
                builder.WithUsername(upn);
                var result = await builder.ExecuteAsync(cancellationToken);

                return new MsalToken(result);
            }
            catch (MsalServiceException e)
            {
                if (e.ErrorCode.Contains(MsalError.AuthenticationCanceledError))
                {
                    return null;
                }

                throw;
            }
            finally
            {
                var helper = await GetMsalCacheHelperAsync();
                helper?.UnregisterCache(publicClient.UserTokenCache);
            }
        }

        async Task<IMsalToken> AcquireTokenSilentlyAsync(CancellationToken cancellationToken)
        {
            var publicClient = await GetPCAAsync().ConfigureAwait(false);
            var accounts = await publicClient.GetAccountsAsync();

            try
            {
                foreach (var account in accounts)
                {
                    try
                    {
                        var silentBuilder = publicClient.AcquireTokenSilent(new string[] { Resource }, account);
                        var result = await silentBuilder.ExecuteAsync(cancellationToken);
                        return new MsalToken(result);
                    }
                    catch (MsalUiRequiredException)
                    { }
                    catch (MsalServiceException)
                    { }
                }
            }
            finally
            {
                var helper = await GetMsalCacheHelperAsync();
                helper?.UnregisterCache(publicClient.UserTokenCache);
            }

            return null;
        }

        private MsalCacheHelper _helper = null;
        private async Task<MsalCacheHelper> GetMsalCacheHelperAsync()
        {
            // There are options to set up the cache correctly using StorageCreationProperties on other OS's but that will need to be tested
            // for now only support windows
            if (_helper == null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                StorageCreationProperties creationProps = CreateTokenCacheProps(useLinuxFallback: false);
                //var fileName = Path.GetFileName(cacheLocation);
                //var directory = Path.GetDirectoryName(cacheLocation);

                //var builder = new StorageCreationPropertiesBuilder(fileName, directory, ClientId);
                //StorageCreationProperties creationProps = cacheProps.Build();
                _helper = await MsalCacheHelper.CreateAsync(creationProps);
            }

            return _helper;
        }

        private async Task<IPublicClientApplication> GetPCAAsync(bool useLocalHost = false)
        {
            var helper = await GetMsalCacheHelperAsync().ConfigureAwait(false);

            var publicClientBuilder = PublicClientApplicationBuilder.Create(ClientId)
                .WithAuthority(Authority);

            if (useLocalHost)
            {
                publicClientBuilder.WithRedirectUri("http://localhost");
            }
            else
            {
                publicClientBuilder.WithRedirectUri(NativeClientRedirect);
            }

            var publicClient = publicClientBuilder.Build();
            helper?.RegisterCache(publicClient.UserTokenCache);
            return publicClient;
        }

        internal static class WindowsIntegratedAuthUtils
        {
            // Adapted from https://github.com/AzureAD/azure-activedirectory-library-for-dotnet/blob/dev/core/src/Platforms/net45/NetDesktopPlatformProxy.cs
            [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.U1)]
            private static extern bool GetUserNameEx(int nameFormat, StringBuilder userName, ref uint userNameSize);

            public static bool SupportsWindowsIntegratedAuth()
            {
                return Environment.OSVersion.Platform == PlatformID.Win32NT;
            }

            public static string GetUserPrincipalName()
            {
                try
                {
                    const int NameUserPrincipal = 8;
                    uint userNameSize = 0;
                    GetUserNameEx(NameUserPrincipal, null, ref userNameSize);
                    if (userNameSize == 0)
                    {
                        return null;
                    }

                    var sb = new StringBuilder((int)userNameSize);
                    if (!GetUserNameEx(NameUserPrincipal, sb, ref userNameSize))
                    {
                        return null;
                    }

                    return sb.ToString();
                }
                catch
                {
                    return null;
                }
            }
        }


        private StorageCreationProperties CreateTokenCacheProps(bool useLinuxFallback)
        {
            const string cacheFileName = "msal.cache";
            string cacheDirectory;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // The shared MSAL cache is located at "%LocalAppData%\.IdentityService\msal.cache" on Windows.
                cacheDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    ".IdentityService"
                );
            }
            else
            {
                // The shared MSAL cache metadata is located at "~/.local/.IdentityService/msal.cache" on UNIX.
                cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", ".IdentityService");
            }

            // The keychain is used on macOS with the following service & account names
            var builder = new StorageCreationPropertiesBuilder(cacheFileName, cacheDirectory)
                .WithMacKeyChain("Microsoft.Developer.IdentityService", "MSALCache");

            if (useLinuxFallback)
            {
                builder.WithLinuxUnprotectedFile();
            }
            else
            {
                // The SecretService/keyring is used on Linux with the following collection name and attributes
                builder.WithLinuxKeyring(cacheFileName,
                    "default", "MSALCache",
                    new KeyValuePair<string, string>("MsalClientID", "Microsoft.Developer.IdentityService"),
                    new KeyValuePair<string, string>("Microsoft.Developer.IdentityService", "1.0.0.0"));
            }

            return builder.Build();
        }
    }

}
