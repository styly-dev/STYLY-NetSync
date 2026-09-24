// NetworkUtilsTests.cs - EditMode tests for network utility functions.
// These assert on result shape only: actual values depend on the host's NICs.
using System.Net;
using NUnit.Framework;
using Styly.NetSync.Utils;

namespace Styly.NetSync.Tests
{
    public class NetworkUtilsTests
    {
        [Test]
        public void GetLocalIpAddress_ReturnsNullOrParseableIpv4()
        {
            var ip = NetworkUtils.GetLocalIpAddress();
            if (ip == null)
            {
                Assert.Pass("No non-virtual NIC available on this host");
            }
            Assert.IsTrue(IPAddress.TryParse(ip, out var parsed), $"'{ip}' must be a valid IP address");
            Assert.AreEqual(System.Net.Sockets.AddressFamily.InterNetwork, parsed.AddressFamily);
            Assert.IsFalse(ip.StartsWith("127."), "loopback must be filtered out");
            Assert.IsFalse(ip.StartsWith("169.254."), "APIPA must be filtered out");
        }

        [Test]
        public void GetAllLocalIpAddresses_EntriesAreFilteredIpv4()
        {
            var addresses = NetworkUtils.GetAllLocalIpAddresses();
            Assert.IsNotNull(addresses);
            foreach (var ip in addresses)
            {
                Assert.IsTrue(IPAddress.TryParse(ip, out _), $"'{ip}' must be a valid IP address");
                Assert.IsFalse(ip.StartsWith("127."));
                Assert.IsFalse(ip.StartsWith("169.254."));
            }
        }

        [TestCase("localhost")]
        [TestCase("127.0.0.1")]
        [TestCase("::1")]
        public void ResolveSourceAddress_LocalhostDestination_ReturnsNull(string destination)
        {
            Assert.IsNull(NetworkUtils.ResolveSourceAddress(destination, 5555));
        }

        [Test]
        public void GetBroadcastAddress_UnknownIp_FallsBackToLimitedBroadcast()
        {
            Assert.AreEqual("255.255.255.255", NetworkUtils.GetBroadcastAddress("203.0.113.1"));
            Assert.AreEqual("255.255.255.255", NetworkUtils.GetBroadcastAddress("not-an-ip"));
        }

        [Test]
        public void GetBroadcastAddress_LocalIp_ReturnsParseableAddress()
        {
            var ip = NetworkUtils.GetLocalIpAddress();
            if (ip == null)
            {
                Assert.Pass("No non-virtual NIC available on this host");
            }
            var broadcast = NetworkUtils.GetBroadcastAddress(ip);
            Assert.IsTrue(IPAddress.TryParse(broadcast, out _), $"'{broadcast}' must be a valid IP address");
        }
    }
}
