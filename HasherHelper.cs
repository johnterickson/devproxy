using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.Services.Content.Common;

namespace DevProxy
{
    public static class HasherHelper
    {
        private static readonly SHA512 _hasher = new SHA512Managed();
        private static Encoding _encoding = new UTF8Encoding(false);

        public static string HashSecret(string secret)
        {
            lock(_hasher)
            {
                return _hasher.ComputeHash(_encoding.GetBytes(secret)).ToHexString().Substring(0, 8);
            }
        }
    }
}
