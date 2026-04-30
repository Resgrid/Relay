using FluentAssertions;
using NUnit.Framework;
using Resgrid.Providers.ApiClient.V4;

namespace Resgrid.Audio.Tests
{
	[TestFixture]
	public class DispatchListBuilderTests
	{
		[Test]
		public void Build_Should_Exclude_Department_Codes_Because_They_Dispatch_To_Entire_Department()
		{
			// Department-type codes dispatch to ALL department users — they should
			// NOT appear in the DispatchList (which targets specific groups/units/roles).
			var result = DispatchListBuilder.Build(new[]
			{
				new DispatchCode { Code = "GRP001", Type = DispatchCodeType.Group },
				new DispatchCode { Code = "DEPT01", Type = DispatchCodeType.Department }
			}, "G");

			result.Should().Be("G:GRP001");
		}

		[Test]
		public void Build_Should_Format_Group_And_GroupMessage_Dispatch_Codes()
		{
			var result = DispatchListBuilder.Build(new[]
			{
				new DispatchCode { Code = "GRP001", Type = DispatchCodeType.Group },
				new DispatchCode { Code = "MSG001", Type = DispatchCodeType.GroupMessage }
			}, "G");

			result.Should().Be("G:GRP001|G:MSG001");
		}

		[Test]
		public void Build_Should_Exclude_DistributionList_Codes()
		{
			// Distribution list codes are forwarded, not dispatched — they should
			// NOT appear in the DispatchList sent to SaveCall.
			var result = DispatchListBuilder.Build(new[]
			{
				new DispatchCode { Code = "GRP001", Type = DispatchCodeType.Group },
				new DispatchCode { Code = "LIST01", Type = DispatchCodeType.DistributionList }
			}, "G");

			result.Should().Be("G:GRP001");
		}

		[Test]
		public void Build_Should_Prefer_ResolvedId_Over_Code()
		{
			var result = DispatchListBuilder.Build(new[]
			{
				new DispatchCode { Code = "station5", Type = DispatchCodeType.Group, ResolvedId = "42" }
			}, "G");

			result.Should().Be("G:42");
		}

		[Test]
		public void Build_Should_Return_Null_When_No_Call_Dispatchable_Codes()
		{
			var result = DispatchListBuilder.Build(new[]
			{
				new DispatchCode { Code = "DEPT01", Type = DispatchCodeType.Department },
				new DispatchCode { Code = "LIST01", Type = DispatchCodeType.DistributionList }
			}, "G");

			result.Should().BeNull();
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
