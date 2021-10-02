using System;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;

namespace Test
{

    [TestClass]
    public class UnitTest1
    {
        private static TestEnvironment _env;

        [ClassInitialize]
        public static async Task ClassInit(TestContext context)
        {
            _env = await TestEnvironment.CreateAsync();
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _env.Dispose();
        }

        
        [TestMethod]
        public async Task AuthOK()
        {
            try
            {
                var response = await _env.authClient.GetAsync(_env.serverUri);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }
            catch(Exception e)
            {
                Console.WriteLine(e);
            }
        }

        [TestMethod]
        public async Task NoAuth407()
        {
            try
            {
                var response = await _env.noAuthClient.GetAsync(_env.serverUri);
                Assert.AreEqual(HttpStatusCode.ProxyAuthenticationRequired, response.StatusCode);
            }
            catch(Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }
}