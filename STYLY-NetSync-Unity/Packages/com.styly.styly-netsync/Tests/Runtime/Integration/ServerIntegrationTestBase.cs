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
            RequireOrIgnore(
                PythonServerFixture.IsServerProjectPresent(),
                "STYLY-NetSync-Server project not checked out alongside the Unity project");
            RequireOrIgnore(
                PythonServerFixture.IsUvAvailable(),
                "uv not found on PATH");

            Server = new PythonServerFixture();
            Server.Start();
        }

        /// <summary>
        /// Skip the suite when a server prerequisite is missing, or fail loudly
        /// when STYLY_NETSYNC_TESTS_REQUIRE_SERVER=1 (CI) so coverage cannot
        /// silently disappear.
        /// </summary>
        private static void RequireOrIgnore(bool condition, string reason)
        {
            if (condition) { return; }
            if (Environment.GetEnvironmentVariable("STYLY_NETSYNC_TESTS_REQUIRE_SERVER") == "1")
            {
                Assert.Fail($"{reason} but STYLY_NETSYNC_TESTS_REQUIRE_SERVER=1");
            }
            Assert.Ignore($"{reason} - skipping server integration tests");
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
            // The destroyed manager kicks off an async NetMQ context termination
            // (Task.Run + 500ms bound in ConnectionManager). A socket created while
            // that is still in flight dies with TerminatingException, failing the
            // next test. NetMQConfig.Cleanup serializes on the library lock, so a
            // synchronous call here blocks until any in-flight termination has
            // finished and the next test starts against a fresh context.
            NetMQLifecycle.Cleanup();
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
