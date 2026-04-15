// EntityRegistryTests.cs
// Verifies registration, duplicate behavior, and TryGet lookup.

using NUnit.Framework;
using Styly.NetSync;
using Styly.NetSync.Internal;
using UnityEditor;
using UnityEngine;

namespace Styly.NetSync.Tests.EditorTests
{
    [TestFixture]
    public class EntityRegistryTests
    {
        private GameObject _go;
        private NetSyncObject _obj;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("NetSyncTestObj");
            _obj = _go.AddComponent<NetSyncObject>();
            // OnValidate fires via AddComponent so _guid is auto-assigned,
            // but make sure here too.
            SerializedObject so = new SerializedObject(_obj);
            SerializedProperty guidProp = so.FindProperty("_guid");
            if (string.IsNullOrEmpty(guidProp.stringValue))
            {
                guidProp.stringValue = System.Guid.NewGuid().ToString("D");
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
            {
                Object.DestroyImmediate(_go);
            }
        }

        [Test]
        public void RegisterAndLookup()
        {
            EntityRegistry.Instance.Register(_obj);
            bool found = EntityRegistry.Instance.TryGet(_obj.EntityId, out EntityBinding binding);
            Assert.IsTrue(found);
            Assert.AreSame(_obj, binding.Component);
            EntityRegistry.Instance.Unregister(_obj);
        }

        [Test]
        public void UnregisterRemovesBinding()
        {
            EntityRegistry.Instance.Register(_obj);
            EntityRegistry.Instance.Unregister(_obj);
            Assert.IsFalse(EntityRegistry.Instance.TryGet(_obj.EntityId, out _));
        }

        [Test]
        public void IdempotentSameReferenceRegister()
        {
            EntityRegistry.Instance.Register(_obj);
            int before = EntityRegistry.Instance.Count;
            EntityRegistry.Instance.Register(_obj);
            int after = EntityRegistry.Instance.Count;
            Assert.AreEqual(before, after);
            EntityRegistry.Instance.Unregister(_obj);
        }
    }
}
