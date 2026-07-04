// ServerIntegrationTestBase.cs - Shared lifecycle for PlayMode integration tests.
// Spins up one local Python server per test class and gives each test a fresh room.
// Self-ignores when `uv` is unavailable so EditMode/offline suites still run.
using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Styly.NetSync.Tests
{
    [Category("Integration")]
    public abstract class ServerIntegrationTestBase
    {
        protected PythonServerFixture Server;
        protected NetSyncManager Manager;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (!PythonServerFixture.IsUvAvailable())
            {
                if (Environment.GetEnvironmentVariable("STYLY_NETSYNC_TESTS_REQUIRE_SERVER") == "1")
                {
                    Assert.Fail("uv not found on PATH but STYLY_NETSYNC_TESTS_REQUIRE_SERVER=1");
                }
                Assert.Ignore("uv not found on PATH - skipping server integration tests");
            }

            Server = new PythonServerFixture();
            Server.Start();
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            Server?.Stop();
            Server = null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (Manager != null)
            {
                // Destroy the manager (OnDisable stops the receive thread) before the
                // fixture and sockets go away, then yield a frame so it fully unwinds.
                UnityEngine.Object.Destroy(Manager.gameObject);
                Manager = null;
            }
            yield return null;
        }

        protected static string NewRoom() => "it_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>Create a manager pointed at the fixture and wait until it is ready.</summary>
        protected IEnumerator CreateManagerAndWaitReady(string room)
        {
            Manager = NetSyncManagerTestFactory.CreateConnected(
                Server.ServerAddress, Server.ControlPort, Server.TransformPort, Server.PubPort, room);
            yield return PollWait.Until(() => Manager.IsReady, 20f, "NetSyncManager IsReady against live server");
        }
    }
}
