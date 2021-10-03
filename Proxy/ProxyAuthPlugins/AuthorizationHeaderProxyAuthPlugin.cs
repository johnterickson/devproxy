namespace DevProxy
{
    public class AuthorizationHeaderProxyAuthPlugin : GenericAuthorizationHeaderProxyAuthPlugin
    {
        public AuthorizationHeaderProxyAuthPlugin(string proxyPassword) : base(proxyPassword)
        {
        }

        protected override string HeaderName => "Authorization";
    }
}
