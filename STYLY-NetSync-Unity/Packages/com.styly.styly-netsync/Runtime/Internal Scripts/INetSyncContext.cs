// INetSyncContext.cs - Minimal context abstraction over NetSyncManager.
// Lets control-lane managers (RPC, Network Variables) read the small slice of
// manager state they depend on without binding to the concrete MonoBehaviour,
// which in turn makes them unit-testable with a lightweight fake.
namespace Styly.NetSync
{
    internal interface INetSyncContext
    {
        int ClientNo { get; }
        string RoomId { get; }
        bool IsReady { get; }
        bool IsOfflineMode { get; }
    }
}
