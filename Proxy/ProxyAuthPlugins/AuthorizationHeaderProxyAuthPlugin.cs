namespace DevProxy
{
    public class AuthorizationHeaderProxyAuthPlugin : GenericAuthorizationHeaderProxyAuthPlugin
    {
        public AuthorizationHeaderProxyAuthPlugin(IProxyPassword proxyPassword) : base(proxyPassword)
        {
        }

        protected override string HeaderName => "Authorization";
    }
}
