using System;
using System.Collections.Generic;
using ServiceStack;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using SimpleServerEvents;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SimpleServerSentEvents4ServiceStack.Test
{
    public class AppHost : AppSelfHostBase
    {
        public AppHost()
            : base("Sever-Sent Events Test", typeof(AppHost).Assembly)
        {

        }

        public override void Configure(Funq.Container container)
        {
            Plugins.Add(new SimpleServerEventsFeature()
            {

            });
        }
    }

    [TestClass]
    public class TestAppHost
    {
        private static AppHost appHost;
        public static string ServiceAddress { get; private set; }

        [AssemblyInitialize]
        public static void AssemblyInitialize(TestContext context)
        {
            ServiceAddress = "http://localhost:1337";
            appHost = new AppHost();
            appHost.Init();

            appHost.Start(ServiceAddress);
        }

        [AssemblyCleanup]
        public static void AssemblyCleanup()
        {
            appHost.Stop();
        }
    }
}
