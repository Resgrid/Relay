using Resgrid.Relay.Engine.Configuration;
using Resgrid.Providers.ApiClient.V4;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Resgrid.Relay.Engine.Smtp
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
			var result = CreateResult();
			if (addresses == null)
				return result;

			foreach (var address in addresses)
				AddRecipient(result, address, mayChooseDepartment: true);

			return result;
		}

		/// <summary>
		/// Parses the envelope (RCPT TO) recipients and then the To/Cc header addresses. The envelope is what the relay accepted
		/// the message for, so it alone decides the department: a header address never sets it, and one with a hosted-mode
		/// subdomain is kept only when that subdomain is the envelope's department (otherwise it is another department's
		/// dispatch, recorded in <see cref="DispatchParseResult.OtherDepartmentAddresses"/>). Without envelope dispatch codes
		/// this is <see cref="ParseRecipients(IEnumerable{string})"/> over the headers.
		/// </summary>
		public DispatchParseResult ParseRecipients(IEnumerable<string> envelopeAddresses, IEnumerable<string> headerAddresses)
		{
			var envelope = envelopeAddresses?.Where(x => !String.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();
			var headers = headerAddresses?.Where(x => !String.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();

			var result = CreateResult();
			foreach (var address in envelope)
				AddRecipient(result, address, mayChooseDepartment: true);

			if (result.DispatchCodes.Count == 0)
				return ParseRecipients(envelope.Concat(headers).Distinct(StringComparer.OrdinalIgnoreCase));

			foreach (var address in headers.Where(x => !envelope.Contains(x, StringComparer.OrdinalIgnoreCase)))
				AddRecipient(result, address, mayChooseDepartment: false);

			return result;
		}

		private void AddRecipient(DispatchParseResult result, string address, bool mayChooseDepartment)
		{
			if (!TryParse(address, out var dispatchCode, out var departmentId))
				return;

			// Reject entries that are duplicates on (departmentId, Type, Code)
			// rather than just Code alone — same code with a different type
			// or from a different department is a distinct dispatch target.
			if (result.DispatchCodes.Any(x =>
				String.Equals(x.Code, dispatchCode.Code, StringComparison.OrdinalIgnoreCase) &&
				x.Type == dispatchCode.Type &&
				String.Equals(departmentId, result.DepartmentId, StringComparison.OrdinalIgnoreCase)))
				return;

			// In hosted mode, an entry for a different department than the one already resolved for this message is not
			// part of this dispatch. It is recorded rather than dropped, so the caller can dispatch it on its own.
			if (!String.IsNullOrWhiteSpace(departmentId) &&
				((!String.IsNullOrWhiteSpace(result.DepartmentId) && !String.Equals(departmentId, result.DepartmentId, StringComparison.OrdinalIgnoreCase)) ||
				 (String.IsNullOrWhiteSpace(result.DepartmentId) && !mayChooseDepartment)))
			{
				result.AddOtherDepartmentAddress(address);
				return;
			}

			result.DispatchCodes.Add(dispatchCode);
			result.SetAddress(dispatchCode, address);

			if (!String.IsNullOrWhiteSpace(departmentId) && String.IsNullOrWhiteSpace(result.DepartmentId))
				result.DepartmentId = departmentId;
		}

		/// <summary>
		/// A static DefaultDepartmentId overrides domain-based detection. It is the dispatch's department from the start, before
		/// any address is parsed: a hosted-mode address is then kept only when its subdomain is that department, whether it is on
		/// the envelope or only in the headers, and an address of any other department is recorded as another department's.
		/// </summary>
		private DispatchParseResult CreateResult()
		{
			var result = new DispatchParseResult();
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
			// Example: station5@dept123.dispatch.resgrid.com
			//   → code = "station5", departmentId = "dept123"
			// Only the first separator splits the department off; the rest is the base domain as configured. (Splitting on every
			// separator and rejoining with it only worked for "."; with "-" a base domain such as dispatch.resgrid.com never
			// contains the separator, so nothing ever matched.)
			if (_options.HostedMode)
			{
				var separator = String.IsNullOrEmpty(_options.DepartmentDomainSeparator) ? "." : _options.DepartmentDomainSeparator;
				var separatorIndex = domain.IndexOf(separator, StringComparison.Ordinal);
				if (separatorIndex > 0 && separatorIndex + separator.Length < domain.Length)
				{
					var extractedDepartmentId = domain.Substring(0, separatorIndex);
					var baseDomain = domain.Substring(separatorIndex + separator.Length);

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

		private readonly Dictionary<DispatchCode, string> _addresses = new Dictionary<DispatchCode, string>();
		private readonly List<string> _otherDepartmentAddresses = new List<string>();

		/// <summary>
		/// Dispatch addresses left out because they belong to a different department than this dispatch: a hosted-mode subdomain
		/// naming another department, or a department code Resgrid resolved to another department. Not dispatched here.
		/// </summary>
		public IReadOnlyList<string> OtherDepartmentAddresses => _otherDepartmentAddresses;

		/// <summary>The recipient address a dispatch code was parsed from, or null.</summary>
		public string AddressOf(DispatchCode code) =>
			code != null && _addresses.TryGetValue(code, out var address) ? address : null;

		internal void SetAddress(DispatchCode code, string address)
		{
			if (code != null && !String.IsNullOrWhiteSpace(address))
				_addresses[code] = address.Trim();
		}

		internal void AddOtherDepartmentAddress(string address)
		{
			if (!String.IsNullOrWhiteSpace(address) && !_otherDepartmentAddresses.Contains(address.Trim(), StringComparer.OrdinalIgnoreCase))
				_otherDepartmentAddresses.Add(address.Trim());
		}

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
