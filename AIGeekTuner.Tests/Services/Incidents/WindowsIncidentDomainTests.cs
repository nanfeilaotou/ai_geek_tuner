using AIGeekTuner.Models.Incidents;
using Xunit;

namespace AIGeekTuner.Tests.Services.Incidents
{
    /// <summary>Gate G（Domain）：查询边界硬约束。</summary>
    public sealed class WindowsIncidentDomainTests
    {
        private static DateTimeOffset T(int hours) =>
            new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero).AddHours(hours);

        [Fact]
        public void StartAfterEnd_IsRejected()
        {
            var query = new IncidentQuery(StartUtc: T(5), EndUtc: T(1), MaxResults: 100);
            Assert.Throws<ArgumentException>(query.Validate);
        }

        [Fact]
        public void StartEqualsEnd_IsRejected()
        {
            var query = new IncidentQuery(StartUtc: T(1), EndUtc: T(1), MaxResults: 100);
            Assert.Throws<ArgumentException>(query.Validate);
        }

        [Fact]
        public void WindowBeyond24Hours_IsRejected()
        {
            var query = new IncidentQuery(StartUtc: T(0), EndUtc: T(25), MaxResults: 100);
            Assert.Throws<ArgumentException>(query.Validate);
        }

        [Fact]
        public void WindowExactly24Hours_IsAccepted()
        {
            var query = new IncidentQuery(StartUtc: T(0), EndUtc: T(24), MaxResults: 100);
            query.Validate(); // 不抛
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(501)]
        public void MaxResultsOutOfRange_IsRejected(int maxResults)
        {
            var query = new IncidentQuery(StartUtc: T(0), EndUtc: T(1), MaxResults: maxResults);
            Assert.Throws<ArgumentException>(query.Validate);
        }
    }
}
