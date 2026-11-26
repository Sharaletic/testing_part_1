using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace async_tests.Tests;

public class PrimesAmountTests
{

    [Test]
    public void CountPrimesAmount_ValidBorder_ReturnsAllPrimesAndClearsCache()
    {
        var primeService = new TestPrimeService(new[] { 2, 3, 5, 7, 11, 13, 17, 19 });
        var sut = new PrimesAmount(20);

        var result = sut.CountPrimesAmount(primeService);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EquivalentTo(new[] { 2, 3, 5, 7, 11, 13, 17, 19 }));
            Assert.That(primeService.ClearCacheCalls, Is.EqualTo(1));
        });
    }

    private sealed class TestPrimeService : PrimeService
    {
        private readonly HashSet<int> primes;
        private int clearCacheCalls;

        public TestPrimeService(IEnumerable<int> primes)
        {
            this.primes = primes.ToHashSet();
        }

        public int ClearCacheCalls => Volatile.Read(ref clearCacheCalls);

        public override bool CheckIsPrime(int p) => primes.Contains(p);

        public override void ClearCache() => Interlocked.Increment(ref clearCacheCalls);
    }
}
