using System;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;

namespace DevProxy
{
    public abstract class GenericAuthorizationHeaderProxyAuthPlugin : IProxyAuthPlugin
    {
        protected abstract string HeaderName { get; }
        private readonly string _proxyPasswordHash;

        public GenericAuthorizationHeaderProxyAuthPlugin(string proxyPassword)
        {
            _proxyPasswordHash = HasherHelper.HashSecret(proxyPassword);
        }

        public Task<(ProxyAuthPluginResult, string)> BeforeRequestAsync(SessionEventArgsBase args)
        {
            var ctxt = args.GetRequestContext();

            var authHeader = args.HttpClient.Request.Headers.GetFirstHeader(HeaderName);
            if (authHeader == null)
            {
                return Task.FromResult((ProxyAuthPluginResult.NoOpinion, $"NoHeader={HeaderName}"));
            }

            string user;
            string password;

            Func<string,string, string> base64decode = (scheme, header) => 
                Encoding.UTF8.GetString(Convert.FromBase64String(header.Substring(scheme.Length + 1)));
            
            // It uses an extensible, **case-insensitive** token to identify the authentication scheme
            string scheme = authHeader.Value.Split(' ')[0].ToLowerInvariant();
            switch(scheme)
            {
                case "basic":
                    string userPassword = base64decode(scheme, authHeader.Value);
                    string[] tokens = userPassword.Split(':');
                    
                    if (tokens.Length > 1)
                    {
                        user = tokens[0];
                        password = tokens[1];
                    }
                    else
                    {
                        user = null;
                        password = tokens[0];
                    }
                    break;
                case "bearer":
                    user = null;
                    password = base64decode(scheme, authHeader.Value);
                    break;
                default:
                    return Task.FromResult((ProxyAuthPluginResult.NoOpinion, $"UnknownAuthScheme={scheme}"));
            }

            string providedPasswordHash = HasherHelper.HashSecret(password);

            if (providedPasswordHash == _proxyPasswordHash)
            {
                ctxt.Request.Headers.RemoveHeader(HeaderName);
                return Task.FromResult((ProxyAuthPluginResult.Authenticated, $"PasswordMatch_SHA512={_proxyPasswordHash}"));
            }

            if (user != null && HasherHelper.HashSecret(user) == _proxyPasswordHash)
            {
                ctxt.Request.Headers.RemoveHeader(HeaderName);
                return Task.FromResult((ProxyAuthPluginResult.Authenticated, $"UserMatch_SHA512={_proxyPasswordHash}"));
            }
            
            return Task.FromResult((ProxyAuthPluginResult.Rejected,
                $"SHA512(ProvidedPassword)={providedPasswordHash}_SHA512(CorrectPassword)={_proxyPasswordHash}"));
        }
    }
}
