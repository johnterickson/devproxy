using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Integrative.Encryption;
using Titanium.Web.Proxy.Network;

namespace DevProxy
{
    public class UserProfileCertificateStorage : ICertificateCache
    {
        private readonly string folderPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".devproxy",
            "certs"
        );

        public void Clear()
        {
            // This is being called too often, so commenting out for now.

            // if (Directory.Exists(folderPath))
            // {
            //     foreach(var f in Directory.GetFiles(folderPath))
            //     {
            //         File.Delete(f);
            //     }
            // }
        }

        public X509Certificate2 LoadCertificate(string subjectName, X509KeyStorageFlags storageFlags)
        {
            var path = Path.Combine(folderPath, $"notroot.{subjectName}.pfx");
            return loadCertificate(path, string.Empty, storageFlags);
        }

        public X509Certificate2 LoadRootCertificate(string pathOrName, string password, X509KeyStorageFlags storageFlags)
        {
            return loadCertificate(GetRootCertPath(pathOrName), password, storageFlags);
        }

        public void SaveCertificate(string subjectName, X509Certificate2 certificate)
        {
            Directory.CreateDirectory(folderPath);
            var path = Path.Combine(folderPath, $"notroot.{subjectName}.pfx");
            byte[] exported = certificate.Export(X509ContentType.Pkcs12);
            var encrypted = CrossProtect.Protect(exported, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, encrypted);
        }

        public void SaveRootCertificate(string pathOrName, string password, X509Certificate2 certificate)
        {
            Directory.CreateDirectory(folderPath);
            var path = GetRootCertPath(pathOrName);
            byte[] exported = certificate.Export(X509ContentType.Pkcs12, password);
            var encrypted = CrossProtect.Protect(exported, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, encrypted);
        }

        public string GetRootCertPath(string pathOrName) => Path.Combine(folderPath, $"root.{pathOrName}");


        private X509Certificate2 loadCertificate(string path, string password, X509KeyStorageFlags storageFlags)
        {
            byte[] exported;

            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                byte[] encrypted = File.ReadAllBytes(path);
                exported = CrossProtect.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            }
            catch (IOException)
            {
                // file or directory not found
                return null;
            }

            return new X509Certificate2(exported, password, storageFlags);
        }

    }

}
