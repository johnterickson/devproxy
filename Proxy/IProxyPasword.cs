using System;

namespace DevProxy
{
    public interface IProxyPasword : IDisposable
    {
        string GetCurrent();
        bool Check(string givenPassword);
    }
}
