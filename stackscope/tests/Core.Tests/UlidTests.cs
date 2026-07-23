using StackScope.Core.Transactions;
using Xunit;

namespace StackScope.Core.Tests;

public class UlidTests
{
    [Fact]
    public void Length_Is_26_And_Uses_CrockfordAlphabet()
    {
        for (int i = 0; i < 200; i++)
        {
            var id = Ulid.NewUlid();
            Assert.Equal(26, id.Length);
            foreach (char c in id)
                Assert.Contains(c, "0123456789ABCDEFGHJKMNPQRSTVWXYZ");
        }
    }

    [Fact]
    public void MonotonicWithinMillisecond()
    {
        // The timestamp component sorts lexicographically by creation time.
        // Two consecutive calls should have IDs whose timestamp prefix
        // (first 10 chars) is monotonically non-decreasing.
        for (int i = 0; i < 50; i++)
        {
            var a = Ulid.NewUlid();
            var b = Ulid.NewUlid();
            Assert.True(string.Compare(a[..10], b[..10], StringComparison.Ordinal) <= 0);
        }
    }
}
