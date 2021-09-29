using System.Collections.Generic;
using Titanium.Web.Proxy.EventArguments;

namespace DevProxy
{
    public class RequestContext
    {
        public readonly Dictionary<IPlugin, object> PluginData = new Dictionary<IPlugin, object>();
        public bool IsAuthenticated = false;
        public string AuthToProxyNotes = null;
        public readonly SessionEventArgs Args;

        public RequestContext(SessionEventArgs args)
        {
            this.Args = args;
        }
    }

    public static class SessionEventArgsExtensions
    {
        public static RequestContext GetRequestContext(this SessionEventArgs args)
        {
            return (RequestContext)args.UserData;
        }
    }
}
