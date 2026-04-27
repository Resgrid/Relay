using Resgrid.Audio.Relay.Console.Configuration;
using Resgrid.Providers.ApiClient.V4;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Resgrid.Audio.Relay.Console.Smtp
{
	public sealed class SmtpDispatchAddressParser
	{
		private readonly SmtpRelayOptions _options;

		public SmtpDispatchAddressParser(SmtpRelayOptions options)
		{
			_options = options ?? throw new ArgumentNullException(nameof(options));
		}

		public List<DispatchCode> ParseRecipients(IEnumerable<string> addresses)
		{
			var dispatchCodes = new List<DispatchCode>();
			if (addresses == null)
				return dispatchCodes;

			foreach (var address in addresses)
			{
				if (TryParse(address, out var dispatchCode) &&
					dispatchCodes.All(x => !String.Equals(x.Code, dispatchCode.Code, StringComparison.OrdinalIgnoreCase)))
				{
					dispatchCodes.Add(dispatchCode);
				}
			}

			return dispatchCodes;
		}

		public bool TryParse(string address, out DispatchCode dispatchCode)
		{
			dispatchCode = null;
			if (String.IsNullOrWhiteSpace(address))
				return false;

			var parts = address.Trim().Split('@');
			if (parts.Length != 2 || String.IsNullOrWhiteSpace(parts[0]) || String.IsNullOrWhiteSpace(parts[1]))
				return false;

			var code = parts[0].Trim();
			var domain = parts[1].Trim();
			if (_options.DepartmentAddressDomains.Any(x => String.Equals(x, domain, StringComparison.OrdinalIgnoreCase)))
			{
				dispatchCode = new DispatchCode
				{
					Code = code,
					Type = DispatchCodeType.Department
				};
				return true;
			}

			if (_options.GroupAddressDomains.Any(x => String.Equals(x, domain, StringComparison.OrdinalIgnoreCase)))
			{
				dispatchCode = new DispatchCode
				{
					Code = code,
					Type = DispatchCodeType.Group
				};
				return true;
			}

			return false;
		}
	}
}
