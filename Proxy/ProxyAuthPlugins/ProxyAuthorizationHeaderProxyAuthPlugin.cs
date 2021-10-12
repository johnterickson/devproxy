using System.Collections.Generic;

namespace DevProxy
{
    public class ProxyAuthorizationHeaderProxyAuthPlugin : IProxyAuthPluginFactory
    {
        public IProxyAuthPlugin Create(IProxyPassword password, Dictionary<string, object> options)
        {
            return new ProxyAuthorizationHeaderProxyAuthPluginInstance(password);
        }
    }

    public class ProxyAuthorizationHeaderProxyAuthPluginInstance : GenericAuthorizationHeaderProxyAuthPlugin
    {
        public ProxyAuthorizationHeaderProxyAuthPluginInstance(IProxyPassword password) : base(password)
        {
        }

        protected override string HeaderName => "Proxy-Authorization";
    }
}
