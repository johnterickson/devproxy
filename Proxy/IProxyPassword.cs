using System;

namespace DevProxy
{
    public interface IProxyPassword : IDisposable
    {
        string GetCurrent();
        bool Check(string givenPassword);
    }
}
