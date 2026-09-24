// RpcIntegrationTests.cs - Round-trip an RPC through the live server. A broadcast RPC
// is relayed to every client in the room including the sender, so a single client
// receives its own call back.
using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Styly.NetSync.Tests
{
    public class RpcIntegrationTests : ServerIntegrationTestBase
    {
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator BroadcastRpc_LoopsBackThroughServer()
        {
            var room = NewRoom();
            yield return CreateManagerAndWaitReady(room);

            int received = 0;
            string gotFn = null;
            string[] gotArgs = null;
            Manager.OnRPCReceived.AddListener((sender, fn, args) =>
            {
                if (fn == "IntegrationPing")
                {
                    received++;
                    gotFn = fn;
                    gotArgs = args;
                }
            });

            Manager.Rpc("IntegrationPing", new[] { "hello", "42" });

            yield return PollWait.Until(() => received > 0, 10f,
                "broadcast RPC relayed back to sender");

            Assert.AreEqual("IntegrationPing", gotFn);
            Assert.AreEqual(new[] { "hello", "42" }, gotArgs);
        }
    }
}
