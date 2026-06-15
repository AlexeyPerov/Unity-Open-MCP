using NUnit.Framework;

namespace Demo.Tests
{
    public class ExampleEditModeTests
    {
        [Test]
        public void Addition_WorksCorrectly()
        {
            Assert.AreEqual(4, 2 + 2);
        }

        [Test]
        public void String_Concatenation_Works()
        {
            Assert.AreEqual("Hello World", "Hello " + "World");
        }

        [Test]
        public void List_Contains_AddedItem()
        {
            var list = new System.Collections.Generic.List<int> { 1, 2, 3 };
            Assert.Contains(2, list);
        }

        [Test]
        public void Example_FailingTest()
        {
            Assert.AreEqual(5, 2 + 2, "This test intentionally fails for demo purposes.");
        }
    }
}
