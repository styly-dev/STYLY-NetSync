// BinarySerializerTests.cs - EditMode tests for the protocol v8 binary serializer.
// Round-trip tests emulate the server relay: the client-pose body bytes are embedded
// verbatim into a MSG_ROOM_POSE payload, exactly as the Python server does (opaque relay).
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Styly.NetSync.Tests
{
    public class BinarySerializerTests
    {
        private const float AbsPosScale = 0.01f;   // head / object absolute position quantization step
        private const float RelPosScale = 0.005f;  // head-relative position quantization step
        private const float LocoPosScale = 0.01f;  // xrOriginDelta / moving-floor-local position step
        private const float YawScale = 0.1f;       // physical yaw quantization step (degrees)

        // Quantization gives at most half a step of error per axis; reconstructed hand
        // positions add head (abs) and hand (rel) errors, so allow the sum plus margin.
        private const float HeadPosTolerance = AbsPosScale * 0.5f + 1e-4f;
        private const float HandPosTolerance = AbsPosScale * 0.5f + RelPosScale * 0.5f + 1e-4f;
        // Smallest-three with 10-bit components stays well under half a degree.
        private const float RotToleranceDeg = 0.5f;

        #region === Helpers ===

        private static ClientTransformData MakeFullPose()
        {
            return new ClientTransformData
            {
                deviceId = "test-device-001",
                poseSeq = 42,
                flags = PoseFlags.PhysicalValid | PoseFlags.HeadValid | PoseFlags.RightValid
                      | PoseFlags.LeftValid | PoseFlags.VirtualsValid,
                xrOriginDeltaPosition = new Vector3(1.25f, 0f, -2.5f),
                xrOriginDeltaYaw = 30f,
                physical = new TransformData(Vector3.zero, Quaternion.identity),
                head = new TransformData(new Vector3(1.23f, 1.62f, -3.21f), Quaternion.Euler(10f, 37f, 5f)),
                rightHand = new TransformData(new Vector3(1.5f, 1.3f, -3.0f), Quaternion.Euler(0f, 90f, 45f)),
                leftHand = new TransformData(new Vector3(0.9f, 1.25f, -3.4f), Quaternion.Euler(-20f, 180f, 0f)),
                virtuals = new List<TransformData>
                {
                    new TransformData(new Vector3(1.0f, 1.0f, -3.0f), Quaternion.Euler(0f, 45f, 0f)),
                    new TransformData(new Vector3(1.4f, 0.8f, -2.9f), Quaternion.Euler(15f, -30f, 60f)),
                },
            };
        }

        /// <summary>
        /// Wraps a serialized MSG_CLIENT_POSE payload into a MSG_ROOM_POSE payload the way
        /// the server does: strip the header (type, version, deviceId) and relay the body.
        /// </summary>
        private static byte[] BuildRoomPoseFromClientPose(
            byte[] clientPose, ushort clientNo, double poseTime,
            string roomId = "test_room", double broadcastTime = 12.5)
        {
            Assert.AreEqual(BinarySerializer.MSG_CLIENT_POSE, clientPose[0]);
            Assert.AreEqual(BinarySerializer.PROTOCOL_VERSION, clientPose[1]);
            int deviceIdLength = clientPose[2];
            int bodyStart = 3 + deviceIdLength;

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(BinarySerializer.MSG_ROOM_POSE);
            writer.Write(BinarySerializer.PROTOCOL_VERSION);
            var roomIdBytes = System.Text.Encoding.UTF8.GetBytes(roomId);
            writer.Write((byte)roomIdBytes.Length);
            writer.Write(roomIdBytes);
            writer.Write(broadcastTime);
            writer.Write((ushort)1);
            writer.Write(clientNo);
            writer.Write(poseTime);
            writer.Write(clientPose, bodyStart, clientPose.Length - bodyStart);
            return ms.ToArray();
        }

        private static ClientTransformData RoundTrip(ClientTransformData input, ushort clientNo = 7, double poseTime = 3.25)
        {
            var clientPose = BinarySerializer.SerializeClientTransform(input);
            var roomPose = BuildRoomPoseFromClientPose(clientPose, clientNo, poseTime);
            var (messageType, data) = BinarySerializer.Deserialize(roomPose);
            Assert.AreEqual(BinarySerializer.MSG_ROOM_POSE, messageType);
            var room = (RoomTransformData)data;
            Assert.AreEqual(1, room.clients.Count);
            return room.clients[0];
        }

        private static void AssertPositionNear(Vector3 expected, Vector3 actual, float tolerance, string label)
        {
            Assert.Less(Vector3.Distance(expected, actual), tolerance,
                $"{label}: expected {expected}, got {actual}");
        }

        private static void AssertRotationNear(Quaternion expected, Quaternion actual, string label)
        {
            Assert.Less(Quaternion.Angle(expected, actual), RotToleranceDeg,
                $"{label}: expected {expected.eulerAngles}, got {actual.eulerAngles}");
        }

        #endregion

        #region === Client pose header layout ===

        [Test]
        public void SerializeClientTransform_WritesHeaderLayout()
        {
            var data = MakeFullPose();
            var bytes = BinarySerializer.SerializeClientTransform(data);

            Assert.AreEqual(BinarySerializer.MSG_CLIENT_POSE, bytes[0]);
            Assert.AreEqual(BinarySerializer.PROTOCOL_VERSION, bytes[1]);

            var deviceIdBytes = System.Text.Encoding.UTF8.GetBytes(data.deviceId);
            Assert.AreEqual(deviceIdBytes.Length, bytes[2]);
            CollectionAssert.AreEqual(deviceIdBytes, new ArraySegment<byte>(bytes, 3, deviceIdBytes.Length));

            int offset = 3 + deviceIdBytes.Length;
            Assert.AreEqual(data.poseSeq, BitConverter.ToUInt16(bytes, offset));
            Assert.AreEqual((byte)data.flags, bytes[offset + 2]);
        }

        [Test]
        public void SerializeClientTransform_NullData_ProducesMinimalValidPayload()
        {
            var bytes = BinarySerializer.SerializeClientTransform(null);
            Assert.AreEqual(BinarySerializer.MSG_CLIENT_POSE, bytes[0]);
            Assert.AreEqual(BinarySerializer.PROTOCOL_VERSION, bytes[1]);
            Assert.AreEqual(0, bytes[2]); // empty deviceId
            // seq(2) + flags(1) + encoding(1) + virtualCount(1)
            Assert.AreEqual(3 + 2 + 1 + 1 + 1, bytes.Length);
        }

        #endregion

        #region === Round-trip via room pose relay ===

        [Test]
        public void RoundTrip_FullPose_RestoresHeadAndHands()
        {
            var input = MakeFullPose();
            var result = RoundTrip(input, clientNo: 9, poseTime: 100.5);

            Assert.AreEqual(9, result.clientNo);
            Assert.AreEqual(100.5, result.poseTime, 1e-9);
            Assert.AreEqual(input.poseSeq, result.poseSeq);
            Assert.AreEqual(input.flags, result.flags);

            AssertPositionNear(input.head.position, result.head.position, HeadPosTolerance, "head");
            AssertRotationNear(input.head.rotation, result.head.rotation, "head");
            AssertPositionNear(input.rightHand.position, result.rightHand.position, HandPosTolerance, "rightHand");
            AssertRotationNear(input.rightHand.rotation, result.rightHand.rotation, "rightHand");
            AssertPositionNear(input.leftHand.position, result.leftHand.position, HandPosTolerance, "leftHand");
            AssertRotationNear(input.leftHand.rotation, result.leftHand.rotation, "leftHand");

            Assert.AreEqual(input.virtuals.Count, result.virtuals.Count);
            for (int i = 0; i < input.virtuals.Count; i++)
            {
                AssertPositionNear(input.virtuals[i].position, result.virtuals[i].position, HandPosTolerance, $"virtual[{i}]");
                AssertRotationNear(input.virtuals[i].rotation, result.virtuals[i].rotation, $"virtual[{i}]");
            }
        }

        [Test]
        public void RoundTrip_HeadOnly_LeavesHandsAtIdentity()
        {
            var input = MakeFullPose();
            input.flags = PoseFlags.HeadValid;

            var result = RoundTrip(input);

            AssertPositionNear(input.head.position, result.head.position, HeadPosTolerance, "head");
            Assert.AreEqual(Vector3.zero, result.rightHand.position);
            Assert.AreEqual(Quaternion.identity, result.rightHand.rotation);
            Assert.AreEqual(Vector3.zero, result.leftHand.position);
            Assert.IsNull(result.virtuals);
        }

        [Test]
        public void RoundTrip_PhysicalPose_ReconstructedFromXrOriginDelta()
        {
            var input = MakeFullPose();
            input.flags = PoseFlags.PhysicalValid | PoseFlags.HeadValid;
            input.xrOriginDeltaPosition = new Vector3(1.0f, 0f, 2.0f);
            input.xrOriginDeltaYaw = 90f;
            input.head = new TransformData(new Vector3(3.0f, 1.6f, 5.0f), Quaternion.Euler(0f, 120f, 0f));

            var result = RoundTrip(input);

            // physical = invDeltaRot * (headPos - deltaPos); physicalYaw = headYaw - deltaYaw
            var deltaRot = Quaternion.Euler(0f, input.xrOriginDeltaYaw, 0f);
            var expectedPos = Quaternion.Inverse(deltaRot) * (input.head.position - input.xrOriginDeltaPosition);
            var expectedRot = Quaternion.Euler(0f, 120f - 90f, 0f);

            AssertPositionNear(input.xrOriginDeltaPosition, result.xrOriginDeltaPosition, LocoPosScale, "xrOriginDelta");
            Assert.AreEqual(input.xrOriginDeltaYaw, result.xrOriginDeltaYaw, YawScale);
            AssertPositionNear(expectedPos, result.physical.position, 0.05f, "physical");
            AssertRotationNear(expectedRot, result.physical.rotation, "physical");
        }

        [Test]
        public void RoundTrip_MovingFloorLocal_UsesPhysicalSlotDirectly()
        {
            var input = MakeFullPose();
            input.flags = PoseFlags.PhysicalValid | PoseFlags.HeadValid | PoseFlags.MovingFloorLocal;
            input.physical = new TransformData(new Vector3(0.5f, 0f, 0.25f), Quaternion.Euler(0f, 45f, 0f));

            var result = RoundTrip(input);

            Assert.AreNotEqual(PoseFlags.None, result.flags & PoseFlags.MovingFloorLocal);
            AssertPositionNear(input.physical.position, result.physical.position, LocoPosScale, "physical (floor-local)");
            AssertRotationNear(input.physical.rotation, result.physical.rotation, "physical (floor-local)");
            // xrOriginDelta must stay zero in moving-floor-local mode
            Assert.AreEqual(Vector3.zero, result.xrOriginDeltaPosition);
            Assert.AreEqual(0f, result.xrOriginDeltaYaw);
        }

        [Test]
        public void RoundTrip_HandFlagsWithoutHead_AreSanitizedAway()
        {
            var input = MakeFullPose();
            input.flags = PoseFlags.RightValid | PoseFlags.LeftValid | PoseFlags.VirtualsValid;

            var clientPose = BinarySerializer.SerializeClientTransform(input);
            int flagsOffset = 3 + System.Text.Encoding.UTF8.GetBytes(input.deviceId).Length + 2;
            Assert.AreEqual((byte)PoseFlags.None, clientPose[flagsOffset],
                "Right/Left/Virtuals flags must be cleared when HeadValid is unset");

            var result = RoundTrip(input);
            Assert.AreEqual(PoseFlags.None, result.flags);
            Assert.AreEqual(Vector3.zero, result.rightHand.position);
        }

        #endregion

        #region === Quantization boundaries ===

        [Test]
        public void RoundTrip_HeadPositionBeyondInt24Range_IsClamped()
        {
            // int24 max quantized = 2^23 - 1 => 83886.07 m at 0.01 m per step
            const float maxRepresentable = ((1 << 23) - 1) * AbsPosScale;
            var input = MakeFullPose();
            input.flags = PoseFlags.HeadValid;
            input.head = new TransformData(new Vector3(1e6f, -1e6f, 0f), Quaternion.identity);

            var result = RoundTrip(input);

            Assert.AreEqual(maxRepresentable, result.head.position.x, AbsPosScale);
            Assert.AreEqual(-(1 << 23) * AbsPosScale, result.head.position.y, AbsPosScale);
            Assert.AreEqual(0f, result.head.position.z, HeadPosTolerance);
        }

        [Test]
        public void RoundTrip_HandOffsetBeyondInt16Range_IsClamped()
        {
            // int16 max => 163.835 m at 0.005 m per step
            const float maxRelRepresentable = short.MaxValue * RelPosScale;
            var input = MakeFullPose();
            input.flags = PoseFlags.HeadValid | PoseFlags.RightValid;
            input.head = new TransformData(Vector3.zero, Quaternion.identity);
            input.rightHand = new TransformData(new Vector3(1000f, 0f, 0f), Quaternion.identity);

            var result = RoundTrip(input);

            Assert.AreEqual(maxRelRepresentable, result.rightHand.position.x, RelPosScale);
        }

        [Test]
        public void RoundTrip_SubQuantumHeadMovement_ProducesIdenticalBytes()
        {
            var a = MakeFullPose();
            a.flags = PoseFlags.HeadValid;
            a.head = new TransformData(new Vector3(1.232f, 1.6f, -3.2f), Quaternion.identity);

            var b = MakeFullPose();
            b.flags = PoseFlags.HeadValid;
            // 0.0001 m is far below the 0.01 m quantization step
            b.head = new TransformData(new Vector3(1.2321f, 1.6f, -3.2f), Quaternion.identity);

            CollectionAssert.AreEqual(
                BinarySerializer.SerializeClientTransform(a),
                BinarySerializer.SerializeClientTransform(b));
        }

        #endregion

        #region === Quaternion smallest-three compression ===

        private static IEnumerable<Quaternion> RotationCases()
        {
            yield return Quaternion.identity;
            yield return Quaternion.Euler(90f, 0f, 0f);
            yield return Quaternion.Euler(0f, 90f, 0f);
            yield return Quaternion.Euler(0f, 0f, 90f);
            yield return Quaternion.Euler(180f, 0f, 0f);
            yield return Quaternion.Euler(0f, 180f, 0f);
            yield return Quaternion.Euler(0f, 0f, 180f);
            yield return Quaternion.Euler(-90f, 45f, 135f);

            var rng = new System.Random(12345);
            for (int i = 0; i < 20; i++)
            {
                yield return Quaternion.Euler(
                    (float)(rng.NextDouble() * 360.0 - 180.0),
                    (float)(rng.NextDouble() * 360.0 - 180.0),
                    (float)(rng.NextDouble() * 360.0 - 180.0));
            }
        }

        [Test]
        public void RoundTrip_HeadRotation_AccurateWithinHalfDegree()
        {
            foreach (var rotation in RotationCases())
            {
                var input = MakeFullPose();
                input.flags = PoseFlags.HeadValid;
                input.head = new TransformData(Vector3.one, rotation);

                var result = RoundTrip(input);
                AssertRotationNear(rotation, result.head.rotation, $"rotation {rotation.eulerAngles}");
            }
        }

        [Test]
        public void Serialize_NegatedQuaternion_ProducesIdenticalBytes()
        {
            var q = Quaternion.Euler(10f, 20f, 30f);
            var negated = new Quaternion(-q.x, -q.y, -q.z, -q.w);

            var a = MakeFullPose();
            a.flags = PoseFlags.HeadValid;
            a.head = new TransformData(Vector3.one, q);

            var b = MakeFullPose();
            b.flags = PoseFlags.HeadValid;
            b.head = new TransformData(Vector3.one, negated);

            CollectionAssert.AreEqual(
                BinarySerializer.SerializeClientTransform(a),
                BinarySerializer.SerializeClientTransform(b));
        }

        [Test]
        public void RoundTrip_ZeroQuaternion_FallsBackToIdentity()
        {
            var input = MakeFullPose();
            input.flags = PoseFlags.HeadValid;
            input.head = new TransformData(Vector3.zero, new Quaternion(0f, 0f, 0f, 0f));

            var result = RoundTrip(input);
            AssertRotationNear(Quaternion.identity, result.head.rotation, "zero quaternion");
        }

        #endregion

        #region === Stealth handshake ===

        [Test]
        public void SerializeStealthHandshake_WritesExpectedLayout()
        {
            var bytes = BinarySerializer.SerializeStealthHandshake("dev");

            Assert.AreEqual(BinarySerializer.MSG_CLIENT_POSE, bytes[0]);
            Assert.AreEqual(BinarySerializer.PROTOCOL_VERSION, bytes[1]);
            Assert.AreEqual(3, bytes[2]);
            Assert.AreEqual((byte)'d', bytes[3]);
            int offset = 3 + 3;
            Assert.AreEqual(0, BitConverter.ToUInt16(bytes, offset)); // poseSeq
            Assert.AreEqual((byte)PoseFlags.IsStealth, bytes[offset + 2]);
            Assert.AreEqual(0, bytes[bytes.Length - 1]); // virtual count
        }

        #endregion

        #region === Deserialize error handling ===

        [Test]
        public void Deserialize_NullOrEmpty_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => BinarySerializer.Deserialize(null));
            Assert.Throws<ArgumentException>(() => BinarySerializer.Deserialize(Array.Empty<byte>()));
        }

        [Test]
        public void Deserialize_UnknownMessageType_ReturnsNullData()
        {
            var (typeZero, dataZero) = BinarySerializer.Deserialize(new byte[] { 0 });
            Assert.AreEqual(0, typeZero);
            Assert.IsNull(dataZero);

            var (typeHigh, dataHigh) = BinarySerializer.Deserialize(new byte[] { 200, 1, 2, 3 });
            Assert.AreEqual(200, typeHigh);
            Assert.IsNull(dataHigh);
        }

        [Test]
        public void Deserialize_RoomPoseWithWrongVersion_ThrowsInvalidDataException()
        {
            var clientPose = BinarySerializer.SerializeClientTransform(MakeFullPose());
            var roomPose = BuildRoomPoseFromClientPose(clientPose, 1, 0.0);
            roomPose[1] = BinarySerializer.PROTOCOL_VERSION - 1;

            Assert.Throws<InvalidDataException>(() => BinarySerializer.Deserialize(roomPose));
        }

        [Test]
        public void Deserialize_TruncatedRoomPose_ThrowsEndOfStreamException()
        {
            var clientPose = BinarySerializer.SerializeClientTransform(MakeFullPose());
            var roomPose = BuildRoomPoseFromClientPose(clientPose, 1, 0.0);
            var truncated = new byte[roomPose.Length / 2];
            Array.Copy(roomPose, truncated, truncated.Length);

            Assert.Throws<EndOfStreamException>(() => BinarySerializer.Deserialize(truncated));
        }

        [Test]
        public void Deserialize_VirtualEntriesWithFlagUnset_WarnsAndConsumesBytes()
        {
            var input = MakeFullPose();
            input.flags = PoseFlags.HeadValid | PoseFlags.VirtualsValid;
            input.virtuals = new List<TransformData> { new TransformData(Vector3.one, Quaternion.identity) };

            var clientPose = BinarySerializer.SerializeClientTransform(input);
            var roomPose = BuildRoomPoseFromClientPose(clientPose, 1, 0.0);

            // Flip VirtualsValid off in the relayed flags byte while the payload
            // still contains one virtual entry.
            var roomIdLength = roomPose[2];
            // type(1)+ver(1)+len(1)+roomId+time(8)+count(2)+clientNo(2)+poseTime(8)+seq(2) => flags byte
            int flagsOffset = 3 + roomIdLength + 8 + 2 + 2 + 8 + 2;
            Assert.AreEqual((byte)input.flags, roomPose[flagsOffset], "flags byte offset sanity check");
            roomPose[flagsOffset] = (byte)PoseFlags.HeadValid;

            LogAssert.Expect(LogType.Warning,
                "[BinarySerializer] Virtual count 1 but VirtualsValid flag unset - malformed payload");
            var (messageType, data) = BinarySerializer.Deserialize(roomPose);
            Assert.AreEqual(BinarySerializer.MSG_ROOM_POSE, messageType);
            Assert.IsNull(((RoomTransformData)data).clients[0].virtuals);
        }

        #endregion

        #region === Pose signature ===

        [Test]
        public void ComputePoseSignature_SameData_IsStable()
        {
            Assert.AreEqual(
                BinarySerializer.ComputePoseSignature(MakeFullPose()),
                BinarySerializer.ComputePoseSignature(MakeFullPose()));
        }

        [Test]
        public void ComputePoseSignature_ChangedHeadPosition_Differs()
        {
            var a = MakeFullPose();
            var b = MakeFullPose();
            b.head.position += new Vector3(0.05f, 0f, 0f); // several quantization steps

            Assert.AreNotEqual(
                BinarySerializer.ComputePoseSignature(a),
                BinarySerializer.ComputePoseSignature(b));
        }

        [Test]
        public void ComputePoseSignature_IgnoresPoseSeqAndDeviceId()
        {
            var a = MakeFullPose();
            var b = MakeFullPose();
            b.poseSeq = 9999;
            b.deviceId = "another-device";

            Assert.AreEqual(
                BinarySerializer.ComputePoseSignature(a),
                BinarySerializer.ComputePoseSignature(b));
        }

        [Test]
        public void ComputePoseSignature_SubQuantumChange_IsStable()
        {
            var a = MakeFullPose();
            var b = MakeFullPose();
            b.head.position += new Vector3(0.0001f, 0f, 0f); // below quantization step

            Assert.AreEqual(
                BinarySerializer.ComputePoseSignature(a),
                BinarySerializer.ComputePoseSignature(b));
        }

        #endregion

        #region === RPC messages ===

        [Test]
        public void RpcMessage_RoundTrip_RestoresAllFields()
        {
            var msg = new RPCMessage
            {
                senderClientNo = 12,
                deviceId = "sender-device",
                targetClientNos = new[] { 3, 65535 },
                functionName = "DoSomething",
                argumentsJson = "[\"日本語\",42,true]",
            };

            var bytes = BinarySerializer.SerializeRPCMessage(msg);
            var (messageType, data) = BinarySerializer.Deserialize(bytes);

            Assert.AreEqual(BinarySerializer.MSG_RPC, messageType);
            var result = (RPCMessage)data;
            Assert.AreEqual(msg.senderClientNo, result.senderClientNo);
            Assert.AreEqual(msg.deviceId, result.deviceId);
            CollectionAssert.AreEqual(msg.targetClientNos, result.targetClientNos);
            Assert.AreEqual(msg.functionName, result.functionName);
            Assert.AreEqual(msg.argumentsJson, result.argumentsJson);
        }

        [Test]
        public void RpcMessage_EmptyTargetsAndArgs_RoundTripsAsBroadcast()
        {
            var msg = new RPCMessage
            {
                senderClientNo = 1,
                deviceId = "d",
                targetClientNos = Array.Empty<int>(),
                functionName = "f",
                argumentsJson = null,
            };

            var (_, data) = BinarySerializer.Deserialize(BinarySerializer.SerializeRPCMessage(msg));
            var result = (RPCMessage)data;
            Assert.AreEqual(0, result.targetClientNos.Length);
            Assert.AreEqual(string.Empty, result.argumentsJson);
        }

        [Test]
        public void RpcMessage_InvalidFields_Throw()
        {
            var tooLongName = new RPCMessage
            {
                deviceId = "d",
                targetClientNos = Array.Empty<int>(),
                functionName = new string('x', 300),
                argumentsJson = "",
            };
            Assert.Throws<ArgumentException>(() => BinarySerializer.SerializeRPCMessage(tooLongName));

            var outOfRangeTarget = new RPCMessage
            {
                deviceId = "d",
                targetClientNos = new[] { 65536 },
                functionName = "f",
                argumentsJson = "",
            };
            Assert.Throws<ArgumentException>(() => BinarySerializer.SerializeRPCMessage(outOfRangeTarget));
        }

        #endregion

        #region === Client-to-server control message layout ===

        [Test]
        public void GlobalVarSet_WritesExpectedLayout()
        {
            var bytes = BinarySerializer.SerializeGlobalVarSet(new Dictionary<string, object>
            {
                ["senderClientNo"] = 5,
                ["deviceId"] = "dev",
                ["variableName"] = "score",
                ["variableValue"] = "100",
            });

            using var reader = new BinaryReader(new MemoryStream(bytes));
            Assert.AreEqual(BinarySerializer.MSG_GLOBAL_VAR_SET, reader.ReadByte());
            Assert.AreEqual(5, reader.ReadUInt16());
            Assert.AreEqual("dev", System.Text.Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadByte())));
            Assert.AreEqual("score", System.Text.Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadByte())));
            Assert.AreEqual("100", System.Text.Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16())));
            Assert.AreEqual(bytes.Length, reader.BaseStream.Position);
        }

        [Test]
        public void GlobalVarSet_TruncatesNameAndValueLimits()
        {
            var bytes = BinarySerializer.SerializeGlobalVarSet(new Dictionary<string, object>
            {
                ["senderClientNo"] = 0,
                ["deviceId"] = "",
                ["variableName"] = new string('n', 100),
                ["variableValue"] = new string('v', 2000),
            });

            using var reader = new BinaryReader(new MemoryStream(bytes));
            reader.ReadByte();
            reader.ReadUInt16();
            reader.ReadBytes(reader.ReadByte());
            Assert.AreEqual(64, reader.ReadByte());   // name capped at 64
            reader.ReadBytes(64);
            Assert.AreEqual(1024, reader.ReadUInt16()); // value capped at 1024
        }

        [Test]
        public void ClientVarSet_WritesExpectedLayout()
        {
            var bytes = BinarySerializer.SerializeClientVarSet(new Dictionary<string, object>
            {
                ["senderClientNo"] = 5,
                ["deviceId"] = "dev",
                ["targetClientNo"] = 8,
                ["variableName"] = "hp",
                ["variableValue"] = "50",
            });

            using var reader = new BinaryReader(new MemoryStream(bytes));
            Assert.AreEqual(BinarySerializer.MSG_CLIENT_VAR_SET, reader.ReadByte());
            Assert.AreEqual(5, reader.ReadUInt16());
            Assert.AreEqual("dev", System.Text.Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadByte())));
            Assert.AreEqual(8, reader.ReadUInt16());
            Assert.AreEqual("hp", System.Text.Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadByte())));
            Assert.AreEqual("50", System.Text.Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16())));
            Assert.AreEqual(bytes.Length, reader.BaseStream.Position);
        }

        [Test]
        public void ClientVarClear_WritesExpectedLayout()
        {
            var bytes = BinarySerializer.SerializeClientVarClear(new Dictionary<string, object>
            {
                ["senderClientNo"] = 3,
                ["deviceId"] = "dev",
            });

            using var reader = new BinaryReader(new MemoryStream(bytes));
            Assert.AreEqual(BinarySerializer.MSG_CLIENT_VAR_CLEAR, reader.ReadByte());
            Assert.AreEqual(3, reader.ReadUInt16());
            Assert.AreEqual("dev", System.Text.Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadByte())));
            Assert.AreEqual(bytes.Length, reader.BaseStream.Position);
        }

        [Test]
        public void ObjectPose_WritesExpectedLayoutAndQuantization()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            var position = new Vector3(1.23f, -4.56f, 7.89f);
            BinarySerializer.SerializeObjectPoseInto(writer, "dev", 0xDEADBEEF, 77, position, Quaternion.identity);

            using var reader = new BinaryReader(new MemoryStream(ms.ToArray()));
            Assert.AreEqual(BinarySerializer.MSG_OBJECT_POSE, reader.ReadByte());
            Assert.AreEqual(BinarySerializer.PROTOCOL_VERSION, reader.ReadByte());
            Assert.AreEqual("dev", System.Text.Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadByte())));
            Assert.AreEqual(0xDEADBEEF, reader.ReadUInt32());
            Assert.AreEqual(77, reader.ReadUInt16());
            // 3x int24 position (little-endian, 0.01 m per step)
            Assert.AreEqual(Mathf.RoundToInt(position.x / AbsPosScale), ReadInt24(reader));
            Assert.AreEqual(Mathf.RoundToInt(position.y / AbsPosScale), ReadInt24(reader));
            Assert.AreEqual(Mathf.RoundToInt(position.z / AbsPosScale), ReadInt24(reader));
            reader.ReadUInt32(); // packed quaternion
            Assert.AreEqual(reader.BaseStream.Length, reader.BaseStream.Position);
        }

        [Test]
        public void ObjectOwnershipRequest_WritesExpectedLayout()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            BinarySerializer.SerializeObjectOwnershipRequestInto(writer, "dev", operationType: 1, objectId: 42);

            using var reader = new BinaryReader(new MemoryStream(ms.ToArray()));
            Assert.AreEqual(BinarySerializer.MSG_OBJECT_OWNERSHIP_REQUEST, reader.ReadByte());
            Assert.AreEqual(BinarySerializer.PROTOCOL_VERSION, reader.ReadByte());
            Assert.AreEqual("dev", System.Text.Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadByte())));
            Assert.AreEqual(1, reader.ReadByte());
            Assert.AreEqual(42, reader.ReadUInt32());
            Assert.AreEqual(reader.BaseStream.Length, reader.BaseStream.Position);
        }

        private static int ReadInt24(BinaryReader reader)
        {
            int b0 = reader.ReadByte();
            int b1 = reader.ReadByte();
            int b2 = reader.ReadByte();
            int value = b0 | (b1 << 8) | (b2 << 16);
            if ((value & 0x800000) != 0)
            {
                value |= unchecked((int)0xFF000000);
            }
            return value;
        }

        #endregion

        #region === Server-to-client message deserialization ===

        [Test]
        public void Deserialize_GlobalVarSync_ParsesVariables()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(BinarySerializer.MSG_GLOBAL_VAR_SYNC);
            writer.Write((ushort)1);
            WriteBytePrefixed(writer, "score");
            WriteUShortPrefixed(writer, "100");
            writer.Write((ushort)7); // lastWriterClientNo

            var (messageType, data) = BinarySerializer.Deserialize(ms.ToArray());
            Assert.AreEqual(BinarySerializer.MSG_GLOBAL_VAR_SYNC, messageType);
            var variables = (object[])((Dictionary<string, object>)data)["variables"];
            var variable = (Dictionary<string, object>)variables[0];
            Assert.AreEqual("score", variable["name"]);
            Assert.AreEqual("100", variable["value"]);
            Assert.AreEqual((ushort)7, variable["lastWriterClientNo"]);
        }

        [Test]
        public void Deserialize_ClientVarSync_ParsesPerClientVariables()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(BinarySerializer.MSG_CLIENT_VAR_SYNC);
            writer.Write((ushort)1);  // one client
            writer.Write((ushort)4);  // clientNo
            writer.Write((ushort)1);  // one variable
            WriteBytePrefixed(writer, "hp");
            WriteUShortPrefixed(writer, "50");
            writer.Write((ushort)4);

            var (messageType, data) = BinarySerializer.Deserialize(ms.ToArray());
            Assert.AreEqual(BinarySerializer.MSG_CLIENT_VAR_SYNC, messageType);
            var clientVariables = (Dictionary<string, object>)((Dictionary<string, object>)data)["clientVariables"];
            var variables = (object[])clientVariables["4"];
            var variable = (Dictionary<string, object>)variables[0];
            Assert.AreEqual("hp", variable["name"]);
            Assert.AreEqual("50", variable["value"]);
        }

        [Test]
        public void Deserialize_DeviceIdMapping_ParsesVersionAndMappings()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(BinarySerializer.MSG_DEVICE_ID_MAPPING);
            writer.Write((byte)0);
            writer.Write((byte)17);
            writer.Write((byte)1);
            writer.Write((ushort)2);
            writer.Write((ushort)1);
            writer.Write((byte)0x00); // not stealth
            WriteBytePrefixed(writer, "device-a");
            writer.Write((ushort)2);
            writer.Write((byte)0x01); // stealth
            WriteBytePrefixed(writer, "device-b");

            var (messageType, data) = BinarySerializer.Deserialize(ms.ToArray());
            Assert.AreEqual(BinarySerializer.MSG_DEVICE_ID_MAPPING, messageType);
            var mapping = (DeviceIdMappingData)data;
            Assert.AreEqual(0, mapping.serverVersionMajor);
            Assert.AreEqual(17, mapping.serverVersionMinor);
            Assert.AreEqual(1, mapping.serverVersionPatch);
            Assert.AreEqual(2, mapping.mappings.Count);
            Assert.AreEqual("device-a", mapping.mappings[0].deviceId);
            Assert.IsFalse(mapping.mappings[0].isStealthMode);
            Assert.AreEqual("device-b", mapping.mappings[1].deviceId);
            Assert.IsTrue(mapping.mappings[1].isStealthMode);
        }

        [Test]
        public void Deserialize_RoomObjects_ParsesObjectStates()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(BinarySerializer.MSG_ROOM_OBJECTS);
            writer.Write(BinarySerializer.PROTOCOL_VERSION);
            writer.Write(99.5);       // broadcastTime
            writer.Write((ushort)1);  // one object
            writer.Write((uint)42);   // objectId
            writer.Write((ushort)3);  // ownerClientNo
            writer.Write((ushort)7);  // poseSeq
            writer.Write(98.5);       // poseTime
            WriteInt24(writer, 123);  // x = 1.23 m
            WriteInt24(writer, -456);
            WriteInt24(writer, 789);
            writer.Write(CompressedIdentityQuaternion());

            var (messageType, data) = BinarySerializer.Deserialize(ms.ToArray());
            Assert.AreEqual(BinarySerializer.MSG_ROOM_OBJECTS, messageType);
            var room = (RoomObjectsData)data;
            Assert.AreEqual(99.5, room.broadcastTime, 1e-9);
            var obj = room.objects[0];
            Assert.AreEqual(42u, obj.objectId);
            Assert.AreEqual(3, obj.ownerClientNo);
            Assert.AreEqual(7, obj.poseSeq);
            Assert.AreEqual(98.5, obj.poseTime, 1e-9);
            AssertPositionNear(new Vector3(1.23f, -4.56f, 7.89f), obj.position, HeadPosTolerance, "object position");
            AssertRotationNear(Quaternion.identity, obj.rotation, "object rotation");
        }

        [Test]
        public void Deserialize_OwnershipChangedAndRejected_ParseFields()
        {
            using var msChanged = new MemoryStream();
            using var writerChanged = new BinaryWriter(msChanged);
            writerChanged.Write(BinarySerializer.MSG_OBJECT_OWNERSHIP_CHANGED);
            writerChanged.Write((uint)42);
            writerChanged.Write((ushort)5);
            writerChanged.Write((ushort)2);

            var (_, changedData) = BinarySerializer.Deserialize(msChanged.ToArray());
            var changed = (OwnershipChangedData)changedData;
            Assert.AreEqual(42u, changed.objectId);
            Assert.AreEqual(5, changed.newOwnerClientNo);
            Assert.AreEqual(2, changed.previousOwnerClientNo);

            using var msRejected = new MemoryStream();
            using var writerRejected = new BinaryWriter(msRejected);
            writerRejected.Write(BinarySerializer.MSG_OBJECT_OWNERSHIP_REJECTED);
            writerRejected.Write((uint)42);
            writerRejected.Write((ushort)5);
            writerRejected.Write((byte)1);

            var (_, rejectedData) = BinarySerializer.Deserialize(msRejected.ToArray());
            var rejected = (OwnershipRejectedData)rejectedData;
            Assert.AreEqual(42u, rejected.objectId);
            Assert.AreEqual(5, rejected.currentOwnerClientNo);
            Assert.AreEqual(1, rejected.reasonCode);
        }

        private static void WriteBytePrefixed(BinaryWriter writer, string value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }

        private static void WriteUShortPrefixed(BinaryWriter writer, string value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static void WriteInt24(BinaryWriter writer, int value)
        {
            var unsignedValue = (uint)(value & 0xFFFFFF);
            writer.Write((byte)(unsignedValue & 0xFF));
            writer.Write((byte)((unsignedValue >> 8) & 0xFF));
            writer.Write((byte)((unsignedValue >> 16) & 0xFF));
        }

        /// <summary>
        /// Smallest-three packed identity quaternion: w is largest (index 3),
        /// the other three components encode 0 at the midpoint (511.5 -> 512).
        /// Computed by serializing an identity head rotation instead of hardcoding.
        /// </summary>
        private static uint CompressedIdentityQuaternion()
        {
            var data = new ClientTransformData
            {
                deviceId = "",
                flags = PoseFlags.HeadValid,
                head = new TransformData(Vector3.zero, Quaternion.identity),
            };
            var bytes = BinarySerializer.SerializeClientTransform(data);
            // header(3) + seq(2) + flags(1) + encoding(1) + head pos int24*3 (9) => packed quat
            return BitConverter.ToUInt32(bytes, 3 + 2 + 1 + 1 + 9);
        }

        #endregion
    }
}
