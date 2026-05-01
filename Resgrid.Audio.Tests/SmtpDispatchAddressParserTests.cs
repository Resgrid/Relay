using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Relay.Console.Configuration;
using Resgrid.Audio.Relay.Console.Smtp;
using Resgrid.Providers.ApiClient.V4;
using System;

namespace Resgrid.Audio.Tests
{
	[TestFixture]
	public class SmtpDispatchAddressParserTests
	{
		[Test]
		public void TryParse_Should_Map_Department_Address_To_Department_Dispatch_Code()
		{
			var parser = new SmtpDispatchAddressParser(new SmtpRelayOptions());

			var result = parser.TryParse("abc123@dispatch.resgrid.com", out var dispatchCode);

			result.Should().BeTrue();
			dispatchCode.Code.Should().Be("abc123");
			dispatchCode.Type.Should().Be(DispatchCodeType.Department);
		}

		[Test]
		public void TryParse_Should_Map_Group_Address_To_Group_Dispatch_Code()
		{
			var parser = new SmtpDispatchAddressParser(new SmtpRelayOptions());

			var result = parser.TryParse("station5@groups.resgrid.com", out var dispatchCode);

			result.Should().BeTrue();
			dispatchCode.Code.Should().Be("station5");
			dispatchCode.Type.Should().Be(DispatchCodeType.Group);
		}

		[Test]
		public void TryParse_Should_Map_GroupMessage_Address_To_GroupMessage_Code()
		{
			var parser = new SmtpDispatchAddressParser(new SmtpRelayOptions());

			var result = parser.TryParse("team1@gm.resgrid.com", out var dispatchCode);

			result.Should().BeTrue();
			dispatchCode.Code.Should().Be("team1");
			dispatchCode.Type.Should().Be(DispatchCodeType.GroupMessage);
		}

		[Test]
		public void TryParse_Should_Map_List_Address_To_DistributionList_Code()
		{
			var parser = new SmtpDispatchAddressParser(new SmtpRelayOptions());

			var result = parser.TryParse("inbound@lists.resgrid.com", out var dispatchCode);

			result.Should().BeTrue();
			dispatchCode.Code.Should().Be("inbound");
			dispatchCode.Type.Should().Be(DispatchCodeType.DistributionList);
		}

		[Test]
		public void TryParse_Should_Reject_Unknown_Domain()
		{
			var parser = new SmtpDispatchAddressParser(new SmtpRelayOptions());

			var result = parser.TryParse("abc123@example.com", out _);

			result.Should().BeFalse();
		}

		[Test]
		public void TryParse_Should_Honor_Custom_Domains()
		{
			var options = new SmtpRelayOptions
			{
				DepartmentAddressDomains = new[] { "pager.company.local" },
				GroupAddressDomains = Array.Empty<string>(),
				GroupMessageAddressDomains = Array.Empty<string>(),
				ListAddressDomains = Array.Empty<string>()
			};
			var parser = new SmtpDispatchAddressParser(options);

			var result = parser.TryParse("dispatch@pager.company.local", out var dispatchCode);

			result.Should().BeTrue();
			dispatchCode.Code.Should().Be("dispatch");
			dispatchCode.Type.Should().Be(DispatchCodeType.Department);
		}

		[Test]
		public void TryParse_HostedMode_Should_Extract_DepartmentId_From_Domain()
		{
			var options = new SmtpRelayOptions
			{
				HostedMode = true,
				DepartmentAddressDomains = new[] { "dispatch.resgrid.com" },
				GroupAddressDomains = new[] { "groups.resgrid.com" },
				DepartmentDomainSeparator = "."
			};
			var parser = new SmtpDispatchAddressParser(options);

			var result = parser.TryParse("station5@dept123.dispatch.resgrid.com", out var dispatchCode, out var departmentId);

			result.Should().BeTrue();
			dispatchCode.Code.Should().Be("station5");
			dispatchCode.Type.Should().Be(DispatchCodeType.Department);
			departmentId.Should().Be("dept123");
		}

		[Test]
		public void ParseRecipients_Should_Ignore_Unknown_Domains_And_Deduplicate_Valid_Ones()
		{
			var parser = new SmtpDispatchAddressParser(new SmtpRelayOptions());

			var result = parser.ParseRecipients(new[]
			{
				"abc123@dispatch.resgrid.com",
				"abc123@dispatch.resgrid.com",
				"station7@groups.resgrid.com",
				"invalid@example.com"
			});

			result.DispatchCodes.Should().HaveCount(2);
			result.DispatchCodes.Should().Contain(x => x.Type == DispatchCodeType.Department);
			result.DispatchCodes.Should().Contain(x => x.Type == DispatchCodeType.Group);
		}

		[Test]
		public void ParseRecipients_Should_Keep_Same_Code_When_Types_Differ()
		{
			// station5 as a Group dispatch code AND as a GroupMessage code
			// are distinct targets — both should be kept.
			var options = new SmtpRelayOptions
			{
				GroupAddressDomains = new[] { "groups.resgrid.com" },
				GroupMessageAddressDomains = new[] { "gm.resgrid.com" },
				DepartmentAddressDomains = Array.Empty<string>(),
				ListAddressDomains = Array.Empty<string>()
			};
			var parser = new SmtpDispatchAddressParser(options);

			var result = parser.ParseRecipients(new[]
			{
				"station5@groups.resgrid.com",
				"station5@gm.resgrid.com"
			});

			result.DispatchCodes.Should().HaveCount(2);
			result.DispatchCodes.Should().Contain(x => x.Type == DispatchCodeType.Group);
			result.DispatchCodes.Should().Contain(x => x.Type == DispatchCodeType.GroupMessage);
		}

		[Test]
		public void ParseRecipients_Should_Reject_Cross_Department_Entries_In_HostedMode()
		{
			// A single inbound email must target only one department.
			// Entries belonging to a different department are silently dropped.
			var options = new SmtpRelayOptions
			{
				HostedMode = true,
				DepartmentAddressDomains = new[] { "dispatch.resgrid.com" },
				DepartmentDomainSeparator = "."
			};
			var parser = new SmtpDispatchAddressParser(options);

			var result = parser.ParseRecipients(new[]
			{
				"station5@dept123.dispatch.resgrid.com",
				"engine2@dept456.dispatch.resgrid.com"
			});

			result.DepartmentId.Should().Be("dept123");
			result.DispatchCodes.Should().HaveCount(1);
			result.DispatchCodes[0].Code.Should().Be("station5");
		}
	}
}
