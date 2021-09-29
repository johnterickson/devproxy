using System;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;

namespace DevProxy
{
    public class ProxyPasswordAuthPlugin : IAuthPlugin
    {
        private readonly string _proxyPassword;
        private readonly string _proxyPasswordHash;

        public ProxyPasswordAuthPlugin(string proxyPassword)
        {
            _proxyPassword = proxyPassword;
            _proxyPasswordHash = HasherHelper.HashSecret(proxyPassword);
        }

        public Task<(AuthPluginResult, string)> BeforeRequestAsync(SessionEventArgsBase args)
        {
            var ctxt = args.GetRequestContext();

            var proxyAuth = args.HttpClient.Request.Headers.GetFirstHeader("Proxy-Authorization");
            if (proxyAuth == null)
            {
                return Task.FromResult((AuthPluginResult.NoOpinion, "NoProxyAuthorizationHeader"));
            }
            
            if (!proxyAuth.Value.StartsWith("Basic "))
            {
                return Task.FromResult((AuthPluginResult.NoOpinion,"NotBasicAuth"));
            }

            string userPassword = Encoding.UTF8.GetString(Convert.FromBase64String(proxyAuth.Value.Substring(6)));
            string[] tokens = userPassword.Split(':');
            
            string password = tokens[1];
            if (password == _proxyPassword)
            {
                return Task.FromResult((AuthPluginResult.Authenticated, $"PasswordMatch_SHA512={_proxyPasswordHash}"));
            }

            string user = tokens[0];
            if (user == _proxyPassword)
            {
                return Task.FromResult((AuthPluginResult.Authenticated, $"UserMatch_SHA512={_proxyPasswordHash}"));
                
            }
            
            return Task.FromResult((AuthPluginResult.Rejected,
                $"SHA512(ProvidedPassword)={HasherHelper.HashSecret(password)}_SHA512(CorrectPassword)={_proxyPasswordHash}"));
        }
    }
}
