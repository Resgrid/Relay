using FluentAssertions;
using NUnit.Framework;
using Resgrid.Audio.Relay.Console.Configuration;
using Resgrid.Audio.Relay.Console.Smtp;
using Resgrid.Providers.ApiClient.V4;

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

			result.Should().HaveCount(2);
			result[0].Type.Should().Be(DispatchCodeType.Department);
			result[1].Type.Should().Be(DispatchCodeType.Group);
		}
	}
}
