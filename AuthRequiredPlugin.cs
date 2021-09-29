using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Models;

namespace DevProxy
{
    public class AuthRequiredPlugin : Plugin
    {
        public override Task<PluginResult> BeforeRequestAsync(PluginRequest request)
        {
            if (!request.RequestContext.IsAuthenticated)
            {
                request.Args.GenericResponse(
                    "Must authenicate.",
                    HttpStatusCode.ProxyAuthenticationRequired,
                    headers: new Dictionary<string, HttpHeader>());
                return Task.FromResult(PluginResult.Stop);
            }

            return Task.FromResult(PluginResult.Continue);
        }

        public override Task<PluginResult> BeforeResponseAsync(PluginRequest request)
        {
            if (!string.IsNullOrEmpty(request.RequestContext.AuthToProxyNotes))
            {
                request.Response.Headers.AddHeader("X-DevProxy-AuthToProxy", request.RequestContext.AuthToProxyNotes);
            }
            return Task.FromResult(PluginResult.Continue);

        }
    }
}
