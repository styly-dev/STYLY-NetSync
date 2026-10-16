// NetworkVariableIntegrationTests.cs - Round-trip Network Variables through the live
// server. Unity->server is observed via the REST bridge; server->Unity via REST POST
// that the client receives as an NV sync event.
using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Styly.NetSync.Tests
{
    public class NetworkVariableIntegrationTests : ServerIntegrationTestBase
    {
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator GlobalVariable_UnityToServer_VisibleViaRest()
        {
            var room = NewRoom();
            yield return CreateManagerAndWaitReady(room);

            Manager.SetGlobalVariable("score", "123");

            string restValue = null;
            yield return PollWait.Until(
                () => Server.TryGetGlobalVariable(room, "score", out restValue) && restValue == "123",
                10f, "global variable visible on server via REST");

            NUnit.Framework.Assert.AreEqual("123", restValue);
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator GlobalVariable_ServerToUnity_FiresChangeEvent()
        {
            var room = NewRoom();
            yield return CreateManagerAndWaitReady(room);

            string gotName = null, gotValue = null;
            Manager.OnGlobalVariableChanged.AddListener((n, o, v) =>
            {
                if (n == "level") { gotName = n; gotValue = v; }
            });

            // Push a change from the server side via the REST bridge.
            Server.PostGlobalVariable(room, "level", "7");

            yield return PollWait.Until(() => gotName != null, 10f,
                "OnGlobalVariableChanged for a server-pushed change");
            NUnit.Framework.Assert.AreEqual("7", gotValue);
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator ClientVariable_UnityToServer_VisibleViaRest()
        {
            var room = NewRoom();
            yield return CreateManagerAndWaitReady(room);

            // Set a client variable on ourselves.
            Manager.SetClientVariable("hp", "88", Manager.ClientNo);

            string restValue = null;
            yield return PollWait.Until(
                () => Server.TryGetClientVariable(room, Manager.DeviceId, "hp", out restValue) && restValue == "88",
                10f, "client variable visible on server via REST");

            NUnit.Framework.Assert.AreEqual("88", restValue);
        }
    }
}
