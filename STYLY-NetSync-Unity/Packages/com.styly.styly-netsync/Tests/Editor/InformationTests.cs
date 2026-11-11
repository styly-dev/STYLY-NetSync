// InformationTests.cs - EditMode tests for package version parsing and compatibility rules.
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Styly.NetSync.Tests
{
    public class InformationTests
    {
        [TestCase("1.2.3", 1, 2, 3)]
        [TestCase("0.7.5-beta", 0, 7, 5)]
        [TestCase("1.2.3+build42", 1, 2, 3)]
        [TestCase("1.2.3-rc1+meta", 1, 2, 3)]
        [TestCase("1", 1, 0, 0)]
        [TestCase("1.2", 1, 2, 0)]
        [TestCase("300.400.500", 255, 255, 255)] // clamped to byte range
        public void ParseVersion_ValidInput_ReturnsComponents(string input, int major, int minor, int patch)
        {
            Assert.AreEqual((major, minor, patch), Information.ParseVersion(input));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("unknown")]
        [TestCase("garbage")]
        [TestCase("a.b.c")]
        public void ParseVersion_InvalidInput_ReturnsZero(string input)
        {
            Assert.AreEqual((0, 0, 0), Information.ParseVersion(input));
        }

        [Test]
        public void GetVersion_ReturnsSemverFromResources()
        {
            var version = Information.GetVersion();
            Assert.IsTrue(Regex.IsMatch(version, @"^\d+\.\d+\.\d+"),
                $"Expected semver from the version resource, got '{version}'");
        }

        [Test]
        public void IsVersionCompatible_MatchingMajorMinor_IsTrue()
        {
            var (major, minor, _) = Information.ParseVersion(Information.GetVersion());
            Assert.IsTrue(Information.IsVersionCompatible(major, minor, 99),
                "Patch mismatch must not break compatibility");
        }

        [Test]
        public void IsVersionCompatible_MinorMismatch_IsFalse()
        {
            var (major, minor, _) = Information.ParseVersion(Information.GetVersion());
            // A 0.0.x client version would skip the check entirely; the resource
            // in this repo always carries a real version.
            Assume.That(major != 0 || minor != 0);
            Assert.IsFalse(Information.IsVersionCompatible(major, minor + 1, 0));
        }

        [Test]
        public void IsVersionCompatible_UnknownServerVersion_SkipsCheck()
        {
            Assert.IsTrue(Information.IsVersionCompatible(0, 0, 0));
        }
    }
}
