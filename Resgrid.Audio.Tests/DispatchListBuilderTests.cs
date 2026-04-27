using FluentAssertions;
using NUnit.Framework;
using Resgrid.Providers.ApiClient.V4;

namespace Resgrid.Audio.Tests
{
	[TestFixture]
	public class DispatchListBuilderTests
	{
		[Test]
		public void Build_Should_Format_Group_And_Department_Dispatch_Codes()
		{
			var result = DispatchListBuilder.Build(new[]
			{
				new DispatchCode { Code = "GRP001", Type = DispatchCodeType.Group },
				new DispatchCode { Code = "DEPT01", Type = DispatchCodeType.Department }
			}, "G");

			result.Should().Be("G:GRP001|G:DEPT01");
		}

		[Test]
		public void Build_Should_Deduplicate_Dispatch_Codes_Case_Insensitively()
		{
			var result = DispatchListBuilder.Build(new[]
			{
				new DispatchCode { Code = "grp001", Type = DispatchCodeType.Group },
				new DispatchCode { Code = "GRP001", Type = DispatchCodeType.Group }
			}, "G");

			result.Should().Be("G:grp001");
		}
	}
}
