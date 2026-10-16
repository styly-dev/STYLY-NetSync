// BinarySerializerGoldenBytesTests.cs - Cross-language protocol v8 parity guard.
// Serializes fixed inputs with the C# BinarySerializer and asserts byte-for-byte
// equality against golden .bin files produced by the Python serializer
// (STYLY-NetSync-Server/tests/fixtures/protocol_v8). Inputs here MUST match
// generate_golden.py. A protocol change means regenerating fixtures in the same PR.
//
// When the server project is not checked out alongside the Unity project the
// tests self-ignore, so the Unity package stays testable standalone.
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Styly.NetSync.Tests
{
    public class BinarySerializerGoldenBytesTests
    {
        private static string FixtureDir =>
            Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "..",
                "STYLY-NetSync-Server", "tests", "fixtures", "protocol_v8"));

        private static byte[] LoadGolden(string caseName)
        {
            var path = Path.Combine(FixtureDir, caseName + ".bin");
            if (!File.Exists(path))
            {
                Assert.Ignore($"Golden fixture not found: {path} (server project not checked out?)");
            }
            return File.ReadAllBytes(path);
        }

        private static void AssertMatches(string caseName, byte[] actual)
        {
            var expected = LoadGolden(caseName);
            Assert.AreEqual(expected, actual,
                $"Byte mismatch for '{caseName}': C# and Python serializers disagree. " +
                $"If the protocol changed intentionally, regenerate the fixtures.");
        }

        [Test]
        public void ClientHelloVisible_MatchesGolden()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            BinarySerializer.SerializeClientHelloInto(writer, "device-123", isStealth: false);
            AssertMatches("client_hello_visible", ms.ToArray());
        }

        [Test]
        public void ClientHelloStealth_MatchesGolden()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            BinarySerializer.SerializeClientHelloInto(writer, "device-123", isStealth: true);
            AssertMatches("client_hello_stealth", ms.ToArray());
        }

        [Test]
        public void RpcBroadcast_MatchesGolden()
        {
            var bytes = BinarySerializer.SerializeRPCMessage(new RPCMessage
            {
                senderClientNo = 7,
                deviceId = "sender-dev",
                targetClientNos = System.Array.Empty<int>(),
                functionName = "DoThing",
                argumentsJson = "[\"a\",1,true]",
            });
            AssertMatches("rpc_broadcast", bytes);
        }

        [Test]
        public void RpcTargeted_MatchesGolden()
        {
            var bytes = BinarySerializer.SerializeRPCMessage(new RPCMessage
            {
                senderClientNo = 7,
                deviceId = "sender-dev",
                targetClientNos = new[] { 3, 4, 65535 },
                functionName = "Targeted",
                argumentsJson = "[]",
            });
            AssertMatches("rpc_targeted", bytes);
        }

        [Test]
        public void GlobalVarSet_MatchesGolden()
        {
            var bytes = BinarySerializer.SerializeGlobalVarSet(new Dictionary<string, object>
            {
                ["senderClientNo"] = 5,
                ["deviceId"] = "dev",
                ["variableName"] = "score",
                ["variableValue"] = "100",
            });
            AssertMatches("global_var_set", bytes);
        }

        [Test]
        public void ClientVarSet_MatchesGolden()
        {
            var bytes = BinarySerializer.SerializeClientVarSet(new Dictionary<string, object>
            {
                ["senderClientNo"] = 5,
                ["deviceId"] = "dev",
                ["targetClientNo"] = 8,
                ["variableName"] = "hp",
                ["variableValue"] = "50",
            });
            AssertMatches("client_var_set", bytes);
        }

        [Test]
        public void ClientVarClear_MatchesGolden()
        {
            var bytes = BinarySerializer.SerializeClientVarClear(new Dictionary<string, object>
            {
                ["senderClientNo"] = 3,
                ["deviceId"] = "dev",
            });
            AssertMatches("client_var_clear", bytes);
        }

        [Test]
        public void ObjectPoseIdentity_MatchesGolden()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            BinarySerializer.SerializeObjectPoseInto(
                writer, "owner", objectId: 0xDEADBEEF, poseSeq: 77,
                position: new Vector3(1.23f, -4.56f, 7.89f), rotation: Quaternion.identity);
            AssertMatches("object_pose_identity", ms.ToArray());
        }

        [Test]
        public void DeviceIdMapping_DeserializesGoldenCorrectly()
        {
            // C# has no serializer for this server->client message, so verify the
            // reverse direction: C# must decode the Python-produced golden bytes.
            var golden = LoadGolden("device_id_mapping");
            var (messageType, data) = BinarySerializer.Deserialize(golden);

            Assert.AreEqual(BinarySerializer.MSG_DEVICE_ID_MAPPING, messageType);
            var mapping = (DeviceIdMappingData)data;
            Assert.AreEqual(0, mapping.serverVersionMajor);
            Assert.AreEqual(17, mapping.serverVersionMinor);
            Assert.AreEqual(1, mapping.serverVersionPatch);
            Assert.AreEqual(2, mapping.mappings.Count);
            Assert.AreEqual(1, mapping.mappings[0].clientNo);
            Assert.AreEqual("device-a", mapping.mappings[0].deviceId);
            Assert.IsFalse(mapping.mappings[0].isStealthMode);
            Assert.AreEqual("device-b", mapping.mappings[1].deviceId);
            Assert.IsTrue(mapping.mappings[1].isStealthMode);
        }
    }
}
