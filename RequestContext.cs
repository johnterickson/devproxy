using System.Collections.Generic;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace DevProxy
{
    public class RequestContext
    {
        public readonly Dictionary<IPlugin, object> PluginData = new Dictionary<IPlugin, object>();
        public bool IsAuthenticated = false;
        public List<(IAuthPlugin,string)> AuthToProxyNotes = new List<(IAuthPlugin,string)>();
        public SessionEventArgsBase Args;

        public RequestContext(SessionEventArgsBase args)
        {
            this.Args = args;
        }

        public void AddAuthNotesToResponse()
        {
            foreach (var (plugin,authNote) in AuthToProxyNotes)
            {
                Args.HttpClient.Response.Headers.AddHeader(
                    new HttpHeader(
                        $"X-DevProxy-AuthToProxy-{plugin.GetType().Name}",
                        authNote));
            }
        }
    }

    public static class SessionEventArgsExtensions
    {
        public static RequestContext GetRequestContext(this SessionEventArgsBase args)
        {
            var ctxt = (RequestContext)args.UserData;
            if (ctxt != null)
            {
                ctxt.Args = args;
            }
            return ctxt;
        }
    }
}
