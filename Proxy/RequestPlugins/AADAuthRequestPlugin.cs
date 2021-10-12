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
    public class AADAuthRequestPlugin : RequestPlugin<AADAuthRequestPlugin.Token>
    {
        private readonly string[] _hostsSuffixes;
        private readonly string[] _scopes;

        public AADAuthRequestPlugin(string[] hostsSuffixes, string[] scopes)
        {
            _hostsSuffixes = hostsSuffixes;
            _scopes = scopes;
        }

        public override bool IsHostRelevant(string host)
        {
            return _hostsSuffixes.Any(h => host.EndsWith(h));
        }

        protected virtual async Task<Token> GetAuthorizationHeaderTokenAsync(PluginRequest r)
        {
            var result = await GetAADTokenAsync(r, CancellationToken.None);
            return new Token("AAD", result, result.AccessToken, _scopes);
        }

        public override async Task<RequestPluginResult> BeforeRequestAsync(PluginRequest r)
        {
            var url = new Uri(r.Request.Url);
            if (IsHostRelevant(url.Host))
            {
                var authzHeader = r.Request.Headers.GetFirstHeader("Authorization");
                if (authzHeader == null)
                {
                    r.Data = await GetAuthorizationHeaderTokenAsync(r);
                    if (r.Data != null)
                    {
                        r.Request.Headers.AddHeader("Authorization", $"Bearer {r.Data.Value}");
                    }
                }
                else
                {
                    r.Data = new Token("FromOriginalClient", null, authzHeader.Value, null);
                }
            }

            if (r.Request.HasBody)
            {
                var _body = await r.Args.GetRequestBody();
            }

            return RequestPluginResult.Continue;
        }

        public override Task<RequestPluginResult> BeforeResponseAsync(PluginRequest r)
        {
            if (r.Data != null)
            {
                r.Response.Headers.AddHeader(new HttpHeader(
                    $"X-DevProxy-{nameof(AzureDevOpsAuthRequestPlugin)}-TokenNotes",
                    r.Data.Notes.ToString()));
                r.Response.Headers.AddHeader(new HttpHeader(
                    $"X-DevProxy-{nameof(AzureDevOpsAuthRequestPlugin)}-TokenSHA512",
                    r.Data.ValueSHA512));
            }
            return Task.FromResult(RequestPluginResult.Continue);
        }


        public class Token
        {
            public readonly AuthenticationResult AuthResult;
            public readonly string Notes;
            public readonly string Value;
            public readonly string ValueSHA512;
            public readonly string[] Scopes;

            public Token(string notes, AuthenticationResult authResult, string tokenValue, string[] scopes)
            {
                Notes = notes;
                AuthResult = authResult;
                Value = tokenValue;
                Scopes = scopes;
                ValueSHA512 = HasherHelper.HashSecret(tokenValue);
            }
        }

        private const string NativeClientRedirect = "https://login.microsoftonline.com/common/oauth2/nativeclient";
        private const string ClientId = "872cd9fa-d31f-45e0-9eab-6e460a02d1f1";
        private const string Authority = "https://login.microsoftonline.com/organizations";

        protected virtual async Task<AuthenticationResult> GetAADTokenAsync(PluginRequest r, CancellationToken cancellationToken)
        {
            AuthenticationResult msalToken = null;
            if (msalToken == null)
            {
                msalToken = await AcquireTokenSilentlyAsync(cancellationToken);
            }

            if (msalToken == null)
            {
                msalToken = await AcquireTokenWithWindowsIntegratedAuth(cancellationToken);
            }

            return msalToken;
        }

        async Task<AuthenticationResult> AcquireTokenWithWindowsIntegratedAuth(CancellationToken cancellationToken)
        {
            var publicClient = await GetPCAAsync().ConfigureAwait(false);

            try
            {
                string upn = WindowsIntegratedAuthUtils.GetUserPrincipalName();
                if (upn == null)
                {
                    return null;
                }

                var builder = publicClient.AcquireTokenByIntegratedWindowsAuth(_scopes);
                builder.WithUsername(upn);
                var result = await builder.ExecuteAsync(cancellationToken);

                return result;
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

        async Task<AuthenticationResult> AcquireTokenSilentlyAsync(CancellationToken cancellationToken)
        {
            var publicClient = await GetPCAAsync().ConfigureAwait(false);
            var accounts = await publicClient.GetAccountsAsync();

            try
            {
                foreach (var account in accounts)
                {
                    try
                    {
                        var silentBuilder = publicClient.AcquireTokenSilent(_scopes, account);
                        return await silentBuilder.ExecuteAsync(cancellationToken);
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
