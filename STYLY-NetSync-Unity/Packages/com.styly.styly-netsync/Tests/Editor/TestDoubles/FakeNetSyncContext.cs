// FakeNetSyncContext.cs - Test double for INetSyncContext.
// Lets control-lane manager tests drive ClientNo / RoomId / IsReady / IsOfflineMode
// without a live NetSyncManager MonoBehaviour.
namespace Styly.NetSync.Tests
{
    internal sealed class FakeNetSyncContext : INetSyncContext
    {
        public int ClientNo { get; set; } = 1;
        public string RoomId { get; set; } = "test_room";
        public bool IsReady { get; set; } = true;
        public bool IsOfflineMode { get; set; }
    }
}
