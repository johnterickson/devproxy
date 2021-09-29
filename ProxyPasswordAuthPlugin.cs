using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Models;

namespace DevProxy
{
    public class ProxyPasswordAuthPlugin : Plugin
    {
        private readonly string _proxyPassword;

        public ProxyPasswordAuthPlugin(string proxyPassword)
        {
            _proxyPassword = proxyPassword;
        }

        public override Task<PluginResult> BeforeRequestAsync(PluginRequest request)
        {
            if (request.RequestContext.IsAuthenticated)
            {
                return Task.FromResult(PluginResult.Continue);
            }

            var proxyAuth = request.Request.Headers.GetFirstHeader("Proxy-Authorization");
            if (proxyAuth != null && proxyAuth.Value.StartsWith("Basic "))
            {
                string userPassword = Encoding.UTF8.GetString(Convert.FromBase64String(proxyAuth.Value.Substring(6)));
                string[] tokens = userPassword.Split(':');
                string user = tokens[0];
                string password = tokens[1];
                if (user == _proxyPassword || password == _proxyPassword)
                {
                    request.RequestContext.IsAuthenticated = true;
                    request.RequestContext.AuthToProxyNotes = "Proxy-Authorization header";
                }
                else
                {
                    request.Args.GenericResponse(
                        "Incorrect password in Proxy-Authorization header.",
                        System.Net.HttpStatusCode.ProxyAuthenticationRequired,
                        new Dictionary<string, HttpHeader>()
                    );
                    return Task.FromResult(PluginResult.Stop);
                }
            }

            return Task.FromResult(PluginResult.Continue);
        }

        public override Task<PluginResult> BeforeResponseAsync(PluginRequest request)
        {
            return Task.FromResult(PluginResult.Continue);
        }
    }
}
