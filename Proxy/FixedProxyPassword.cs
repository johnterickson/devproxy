using System;

namespace DevProxy
{
    public sealed class FixedProxyPassword : IProxyPasword
    {
        private readonly string password;

        public FixedProxyPassword(string password)
        {
            this.password = password;
        }

        public bool Check(string givenPassword)
        {
            return this.password.Equals(givenPassword, StringComparison.Ordinal);
        }

        public void Dispose()
        {
            //nothing
        }

        public string GetCurrent()
        {
            return this.password;
        }
    }
}
