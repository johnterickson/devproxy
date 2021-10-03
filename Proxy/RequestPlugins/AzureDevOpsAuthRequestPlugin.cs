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
using Titanium.Web.Proxy.Models;

namespace DevProxy
{
    // Hopefully Kyle will fix this or I can shell out to his tool??
    /*
        Example tested API:
        Invoke-WebRequest -Proxy http://localhost:8888 -Uri https://vsblob.dev.azure.com/mseng/_apis/blob/blobs/1E761B9ED61FA4F3D47258FB4F4E04751FE9D01C4A5360D4F81791AA60BFFD0D00/url
    */
    public class AzureDevOpsAuthRequestPlugin : RequestPlugin<AzureDevOpsAuthRequestPlugin.Token>
    {
        private static readonly string[] HostsSuffixes = { "dev.azure.com", "visualstudio.com" };
        
        public override bool IsHostRelevant(string host)
        {
            return HostsSuffixes.Any(h => host.EndsWith(h));
        }
        public override async Task<RequestPluginResult> BeforeRequestAsync(PluginRequest r)
        {
            var url = new Uri(r.Request.Url);
            if (IsHostRelevant(url.Host))
            {
                var authzHeader = r.Request.Headers.GetFirstHeader("Authorization");
                if (authzHeader == null)
                {
                    r.Data = await GetTokenAsync(CancellationToken.None);
                    r.Request.Headers.AddHeader("Authorization", $"Bearer {r.Data.TokenValue}");
                }
                else
                {
                    r.Data = new Token(TokenType.ExistingBearer, authzHeader.Value);
                }
            }

            return RequestPluginResult.Continue;
        }

        public override Task<RequestPluginResult> BeforeResponseAsync(PluginRequest r)
        {
            if (r.Data != null)
            {
                r.Response.Headers.AddHeader(new HttpHeader(
                    $"X-DevProxy-{nameof(AzureDevOpsAuthRequestPlugin)}-TokenType",
                    r.Data.TokenType.ToString()));
                r.Response.Headers.AddHeader(new HttpHeader(
                    $"X-DevProxy-{nameof(AzureDevOpsAuthRequestPlugin)}-TokenSHA512",
                    r.Data.TokenValueSHA512));
            }
            return Task.FromResult(RequestPluginResult.Continue);
        }

        
        public class Token
        {
            public readonly TokenType TokenType;
            public readonly string TokenValue;
            public readonly string TokenValueSHA512;

            public Token(TokenType tokenType, string tokenValue)
            {
                TokenType = tokenType;
                TokenValue = tokenValue;
                TokenValueSHA512 = HasherHelper.HashSecret(tokenValue);
            }
        }

        private const string NativeClientRedirect = "https://login.microsoftonline.com/common/oauth2/nativeclient";
        private const string Resource = "499b84ac-1321-427f-aa17-267ca6975798/.default";
        private const string ClientId = "872cd9fa-d31f-45e0-9eab-6e460a02d1f1";
        private const string Authority = "https://login.microsoftonline.com/organizations";


        public enum TokenType
        {
            None,
            ExistingBearer,
            EnvVar_AZURE_DEVOPS_TOKEN,
            WindowsIntegratedAuth
        }

        private async Task<Token> GetTokenAsync(CancellationToken cancellationToken)
        {
            string token = Environment.GetEnvironmentVariable("AZURE_DEVOPS_TOKEN");
            if (token != null)
            {
                return new Token(TokenType.EnvVar_AZURE_DEVOPS_TOKEN, token);
            }

            IMsalToken msalToken = null;
            if (msalToken == null)
            {
                msalToken = await AcquireTokenSilentlyAsync(cancellationToken);
            }
            
            if (msalToken == null)
            {
                msalToken = await AcquireTokenWithWindowsIntegratedAuth(cancellationToken);
            }

            return new Token(TokenType.WindowsIntegratedAuth, msalToken.AccessToken);
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

            var publicClientBuilder = PublicClientApplicationBuilder
                .Create(ClientId)
                .WithAuthority(Authority)
                .WithLogging((LogLevel level, string message, bool containsPii) =>
                {
                    if (level < LogLevel.Info)
                    {
                        Console.WriteLine($"{level} - {message}");
                    }
                });

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