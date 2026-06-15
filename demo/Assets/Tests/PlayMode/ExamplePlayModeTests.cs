using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Demo.Tests
{
    public class ExamplePlayModeTests
    {
        [Test]
        public void GameObject_CanBeCreated()
        {
            var go = new GameObject("TestObject");
            Assert.IsNotNull(go);
            Assert.AreEqual("TestObject", go.name);
            Object.Destroy(go);
        }

        [Test]
        public void Vector3_Addition_Works()
        {
            var a = new Vector3(1, 2, 3);
            var b = new Vector3(4, 5, 6);
            Assert.AreEqual(new Vector3(5, 7, 9), a + b);
        }

        [UnityTest]
        public System.Collections.IEnumerator Coroutine_Test_With_One_Frame()
        {
            var go = new GameObject("CoroutineTest");
            yield return null;
            Assert.IsTrue(go.activeInHierarchy);
            Object.Destroy(go);
        }
    }
}
