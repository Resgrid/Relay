using System;
using System.Collections.Generic;
using System.Linq;

namespace Resgrid.Providers.ApiClient.V4
{
	public enum DispatchCodeType
	{
		/// <summary>
		/// Department-wide dispatch email (Type 1 in the Postmark pipeline).
		/// The local-part identifies the department (InternalDispatchEmail setting).
		/// This does not map to a single group — the call should dispatch to
		/// all department users. When processed by DispatchListBuilder this
		/// type produces no DispatchList token.
		/// </summary>
		Department = 1,

		/// <summary>
		/// Group dispatch email (Type 3 in the Postmark pipeline).
		/// The local-part is the dispatch email code stored in
		/// DepartmentGroups.DispatchEmail. It resolves to a single numeric
		/// group ID via the lookup API.
		/// </summary>
		Group = 2,

		/// <summary>
		/// Group message email (Type 4 in the Postmark pipeline).
		/// The local-part is the message email code stored in
		/// DepartmentGroups.MessageEmail. Results in a group message,
		/// not a call.
		/// </summary>
		GroupMessage = 3,

		/// <summary>
		/// Distribution list email (Type 2 in the Postmark pipeline).
		/// The local-part is the list address stored in
		/// DistributionLists.EmailAddress. Results in list forwarding.
		/// </summary>
		DistributionList = 4
	}

	public sealed class DispatchCode
	{
		/// <summary>
		/// The dispatch code (name) extracted from the email local-part.
		/// Example: "station5" from station5@dispatch.resgrid.com
		/// </summary>
		public string Code { get; set; }

		/// <summary>
		/// The type of dispatch this code represents.
		/// </summary>
		public DispatchCodeType Type { get; set; }

		/// <summary>
		/// (Optional) The resolved numeric entity ID from the lookup API.
		/// When set, DispatchListBuilder uses this value in the DispatchList
		/// string (e.g. G:42) instead of the raw name (e.g. G:STATION5).
		/// When null, DispatchListBuilder falls back to <see cref="Code"/>.
		/// </summary>
		public string ResolvedId { get; set; }

		/// <summary>
		/// True when this dispatch code has been resolved to a numeric ID
		/// and is ready to be included in the DispatchList sent to SaveCall.
		/// </summary>
		public bool IsResolved => !String.IsNullOrWhiteSpace(ResolvedId);

		/// <summary>
		/// Returns the value that should appear in a DispatchList token.
		/// Prefers the resolved ID, falls back to the code name.
		/// </summary>
		public string DispatchToken => IsResolved ? ResolvedId : Code;
	}

	public static class DispatchListBuilder
	{
		/// <summary>
		/// Builds a pipe-delimited DispatchList string suitable for the
		/// Resgrid v4 SaveCall API.
		/// 
		/// Prefix conventions:
		///   G:{id} — Group dispatch (numeric group ID)
		///   U:{id} — Unit dispatch (numeric unit ID)
		///   R:{id} — Role dispatch (numeric role ID)
		/// 
		/// Department-type codes are excluded — they dispatch to the
		/// entire department and should not appear in the DispatchList.
		/// 
		/// When a dispatch code has a <see cref="DispatchCode.ResolvedId"/>,
		/// that numeric ID is used. Otherwise the original code string is
		/// used as a best-effort fallback.
		/// </summary>
		public static string Build(IEnumerable<DispatchCode> dispatchCodes, string departmentDispatchPrefix = "G")
		{
			if (dispatchCodes == null)
				return null;

			var tokens = dispatchCodes
				.Where(x => x != null && !String.IsNullOrWhiteSpace(x.Code) && x.Type != DispatchCodeType.Department && x.Type != DispatchCodeType.DistributionList && x.Type != DispatchCodeType.GroupMessage)
				.Select(x => Format(x, departmentDispatchPrefix))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();

			if (tokens.Length == 0)
				return null;

			return String.Join("|", tokens);
		}

		/// <summary>
		/// Formats a dispatch code into a single DispatchList token.
		/// </summary>
		public static string Format(DispatchCode dispatchCode, string departmentDispatchPrefix = "G")
		{
			if (dispatchCode == null)
				throw new ArgumentNullException(nameof(dispatchCode));
			if (String.IsNullOrWhiteSpace(dispatchCode.Code) && String.IsNullOrWhiteSpace(dispatchCode.ResolvedId))
				throw new ArgumentException("A dispatch code or resolved ID is required.", nameof(dispatchCode));

			var token = dispatchCode.DispatchToken;
			var prefix = dispatchCode.Type switch
			{
				DispatchCodeType.Group => "G",
				DispatchCodeType.GroupMessage => "G",
				_ => NormalizePrefix(departmentDispatchPrefix)
			};

			return $"{prefix}:{token}";
		}

		private static string NormalizePrefix(string prefix)
		{
			if (String.IsNullOrWhiteSpace(prefix))
				return "G";

			return prefix.Trim().TrimEnd(':').ToUpperInvariant();
		}
	}
}
