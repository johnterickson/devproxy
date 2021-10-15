using System;
using System.IO;
using System.Runtime.InteropServices;
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
            return loadCertificate(GetNotRootCertPath(subjectName), null, storageFlags);

        }

        public X509Certificate2 LoadRootCertificate(string pathOrName, string password, X509KeyStorageFlags storageFlags)
        {
            return loadCertificate(GetRootCertPath(pathOrName), password, storageFlags);
        }

        public void SaveCertificate(string subjectName, X509Certificate2 certificate)
        {
            saveCertificate(certificate, GetNotRootCertPath(subjectName), null);
        }

        public void SaveRootCertificate(string pathOrName, string password, X509Certificate2 certificate)
        {
            saveCertificate(certificate, GetRootCertPath(pathOrName), password);
        }

        public string GetRootCertPath(string pathOrName) => Path.Combine(folderPath, $"root.{pathOrName}");
        public string GetNotRootCertPath(string pathOrName) => Path.Combine(folderPath, $"notroot.{pathOrName}");

        private void saveCertificate(X509Certificate2 certificate, string path, string password)
        {
            Directory.CreateDirectory(folderPath);
            byte[] exported = certificate.Export(X509ContentType.Pkcs12, password);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                exported = CrossProtect.Protect(exported, null, DataProtectionScope.CurrentUser);
            }
            File.WriteAllBytes(path, exported);
        }

        private X509Certificate2 loadCertificate(string path, string password, X509KeyStorageFlags storageFlags)
        {
            byte[] imported;

            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                imported = File.ReadAllBytes(path);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    imported = CrossProtect.Unprotect(imported, null, DataProtectionScope.CurrentUser);
                }
            }
            catch (IOException)
            {
                // file or directory not found
                return null;
            }

            return new X509Certificate2(imported, password, storageFlags);
        }

    }

}
