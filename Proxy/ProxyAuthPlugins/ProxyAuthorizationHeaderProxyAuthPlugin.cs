namespace DevProxy
{
    public class ProxyAuthorizationHeaderProxyAuthPlugin : GenericAuthorizationHeaderProxyAuthPlugin
    {
        public ProxyAuthorizationHeaderProxyAuthPlugin(string proxyPassword) : base(proxyPassword)
        {
        }

        protected override string HeaderName => "Proxy-Authorization";
    }
}
