using Resgrid.Audio.Relay.Console.Configuration;
using Resgrid.Providers.ApiClient.V4;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Resgrid.Audio.Relay.Console.Smtp
{
	/// <summary>
	/// Parses SMTP recipient addresses into dispatch codes, determining the
	/// dispatch type from the recipient domain — matching the logic used by
	/// the Postmark EmailController in the Resgrid API.
	/// 
	/// Domain → DispatchCodeType mapping:
	///   DepartmentAddressDomains  → Department (Type 1 — department-wide call)
	///   GroupAddressDomains       → Group (Type 3 — group-scoped call)
	///   GroupMessageAddressDomains → GroupMessage (Type 4 — group message)
	///   ListAddressDomains        → DistributionList (Type 2 — list forwarding)
	/// </summary>
	public sealed class SmtpDispatchAddressParser
	{
		private readonly SmtpRelayOptions _options;

		public SmtpDispatchAddressParser(SmtpRelayOptions options)
		{
			_options = options ?? throw new ArgumentNullException(nameof(options));
		}

		/// <summary>
		/// Parses a list of recipient addresses into dispatch codes.
		/// In hosted mode, also extracts the department identifier from
		/// the recipient domain.
		/// </summary>
		public DispatchParseResult ParseRecipients(IEnumerable<string> addresses)
		{
			var result = new DispatchParseResult();
			if (addresses == null)
				return result;

			foreach (var address in addresses)
			{
				if (TryParse(address, out var dispatchCode, out var departmentId))
				{
					// Reject entries that are duplicates on (departmentId, Type, Code)
					// rather than just Code alone — same code with a different type
					// or from a different department is a distinct dispatch target.
					if (result.DispatchCodes.Any(x =>
						String.Equals(x.Code, dispatchCode.Code, StringComparison.OrdinalIgnoreCase) &&
						x.Type == dispatchCode.Type &&
						String.Equals(departmentId, result.DepartmentId, StringComparison.OrdinalIgnoreCase)))
						continue;

					// In hosted mode, reject entries that target a different department
					// than what has already been resolved for this message.
					if (!String.IsNullOrWhiteSpace(departmentId) &&
						!String.IsNullOrWhiteSpace(result.DepartmentId) &&
						!String.Equals(departmentId, result.DepartmentId, StringComparison.OrdinalIgnoreCase))
						continue;

					result.DispatchCodes.Add(dispatchCode);

					if (!String.IsNullOrWhiteSpace(departmentId) && String.IsNullOrWhiteSpace(result.DepartmentId))
						result.DepartmentId = departmentId;
				}
			}

			// A static DefaultDepartmentId overrides domain-based detection.
			if (!String.IsNullOrWhiteSpace(_options.DefaultDepartmentId))
				result.DepartmentId = _options.DefaultDepartmentId;

			return result;
		}

		public bool TryParse(string address, out DispatchCode dispatchCode)
		{
			return TryParse(address, out dispatchCode, out _);
		}

		public bool TryParse(string address, out DispatchCode dispatchCode, out string departmentId)
		{
			dispatchCode = null;
			departmentId = null;

			if (String.IsNullOrWhiteSpace(address))
				return false;

			var parts = address.Trim().Split('@');
			if (parts.Length != 2 || String.IsNullOrWhiteSpace(parts[0]) || String.IsNullOrWhiteSpace(parts[1]))
				return false;

			var code = parts[0].Trim();
			var domain = parts[1].Trim();

			// Exact domain match (single-department or non-hosted).
			if (TryMatchDomain(domain, _options.DepartmentAddressDomains, DispatchCodeType.Department, code, ref dispatchCode))
				return true;

			if (TryMatchDomain(domain, _options.GroupAddressDomains, DispatchCodeType.Group, code, ref dispatchCode))
				return true;

			if (TryMatchDomain(domain, _options.GroupMessageAddressDomains, DispatchCodeType.GroupMessage, code, ref dispatchCode))
				return true;

			if (TryMatchDomain(domain, _options.ListAddressDomains, DispatchCodeType.DistributionList, code, ref dispatchCode))
				return true;

			// Hosted mode: the domain may have a department prefix.
			// Example: station5.dept123.dispatch.resgrid.com
			//   → code = "station5", departmentId = "dept123"
			if (_options.HostedMode)
			{
				var domainParts = domain.Split(new[] { _options.DepartmentDomainSeparator }, StringSplitOptions.RemoveEmptyEntries);
				if (domainParts.Length >= 3)
				{
					// The first segment is the department ID, the rest is the base domain.
					var extractedDepartmentId = domainParts[0];
					var baseDomain = String.Join(_options.DepartmentDomainSeparator, domainParts.Skip(1));

					if (TryMatchDomain(baseDomain, _options.DepartmentAddressDomains, DispatchCodeType.Department, code, ref dispatchCode))
					{
						departmentId = extractedDepartmentId;
						return true;
					}

					if (TryMatchDomain(baseDomain, _options.GroupAddressDomains, DispatchCodeType.Group, code, ref dispatchCode))
					{
						departmentId = extractedDepartmentId;
						return true;
					}

					if (TryMatchDomain(baseDomain, _options.GroupMessageAddressDomains, DispatchCodeType.GroupMessage, code, ref dispatchCode))
					{
						departmentId = extractedDepartmentId;
						return true;
					}

					if (TryMatchDomain(baseDomain, _options.ListAddressDomains, DispatchCodeType.DistributionList, code, ref dispatchCode))
					{
						departmentId = extractedDepartmentId;
						return true;
					}
				}
			}

			return false;
		}

		private static bool TryMatchDomain(string domain, string[] configuredDomains, DispatchCodeType type, string code, ref DispatchCode dispatchCode)
		{
			if (configuredDomains == null || configuredDomains.Length == 0)
				return false;

			if (configuredDomains.Any(x => String.Equals(x, domain, StringComparison.OrdinalIgnoreCase)))
			{
				dispatchCode = new DispatchCode
				{
					Code = code,
					Type = type
				};
				return true;
			}

			return false;
		}
	}

	/// <summary>
	/// The result of parsing SMTP recipient addresses into dispatch targets.
	/// </summary>
	public sealed class DispatchParseResult
	{
		/// <summary>
		/// The dispatch codes extracted from recipient addresses.
		/// </summary>
		public List<DispatchCode> DispatchCodes { get; } = new List<DispatchCode>();

		/// <summary>
		/// The department identifier extracted from the recipient domain
		/// in hosted mode, or the <see cref="SmtpRelayOptions.DefaultDepartmentId"/>
		/// when configured. Null when not in hosted mode.
		/// </summary>
		public string DepartmentId { get; set; }

		/// <summary>
		/// Returns true if at least one dispatchable target was parsed.
		/// GroupMessage and DistributionList codes are included here
		/// (they are valid targets), but group messages and list
		/// forwarding are handled differently than calls.
		/// </summary>
		public bool HasTargets => DispatchCodes.Count > 0;

		/// <summary>
		/// True when ALL dispatch codes are call-dispatchable (Department or Group).
		/// </summary>
		public bool HasCallTargets => DispatchCodes.Any(x => x.Type == DispatchCodeType.Department || x.Type == DispatchCodeType.Group);

		/// <summary>
		/// True when any code is a group message target.
		/// </summary>
		public bool HasGroupMessageTargets => DispatchCodes.Any(x => x.Type == DispatchCodeType.GroupMessage);

		/// <summary>
		/// True when any code is a distribution list target.
		/// </summary>
		public bool HasDistributionListTargets => DispatchCodes.Any(x => x.Type == DispatchCodeType.DistributionList);
	}
}
