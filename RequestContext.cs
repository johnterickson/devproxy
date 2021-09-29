using System;
using System.Collections.Generic;
using System.Net;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace DevProxy
{
    public class RequestContext
    {
        public readonly Dictionary<IPlugin, object> PluginData = new Dictionary<IPlugin, object>();
        public bool IsAuthenticated = false;
        public List<(IAuthPlugin, string)> AuthToProxyNotes = new List<(IAuthPlugin, string)>();
        public SessionEventArgsBase Args;

        public RequestContext(SessionEventArgsBase args)
        {
            this.Args = args;
        }

        public void ReturnProxy407()
        {
            var r = this.Args.HttpClient.Response;
            r.HttpVersion = new Version(1, 1);
            r.StatusCode = (int)HttpStatusCode.ProxyAuthenticationRequired;
            r.StatusDescription = "Proxy Authentication Required";
            r.Headers.Clear();
            r.Headers.AddHeader(
                new HttpHeader("Proxy-Authenticate", "Basic realm=\"DevProxy\""));
            r.Headers.AddHeader("Connection", "close");
        }

        public void AddAuthNotesToResponse()
        {
            foreach (var (plugin, authNote) in AuthToProxyNotes)
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
