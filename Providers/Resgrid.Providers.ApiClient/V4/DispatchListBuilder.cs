using System;
using System.Collections.Generic;
using System.Linq;

namespace Resgrid.Providers.ApiClient.V4
{
	public enum DispatchCodeType
	{
		Department = 1,
		Group = 2
	}

	public sealed class DispatchCode
	{
		public string Code { get; set; }
		public DispatchCodeType Type { get; set; }
	}

	public static class DispatchListBuilder
	{
		public static string Build(IEnumerable<DispatchCode> dispatchCodes, string departmentDispatchPrefix = "G")
		{
			if (dispatchCodes == null)
				return null;

			var tokens = dispatchCodes
				.Where(x => x != null && !String.IsNullOrWhiteSpace(x.Code))
				.Select(x => Format(x.Code, x.Type, departmentDispatchPrefix))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();

			if (tokens.Length == 0)
				return null;

			return String.Join("|", tokens);
		}

		public static string Format(string code, DispatchCodeType type, string departmentDispatchPrefix = "G")
		{
			if (String.IsNullOrWhiteSpace(code))
				throw new ArgumentException("A dispatch code is required.", nameof(code));

			var normalizedCode = code.Trim();
			var prefix = type == DispatchCodeType.Department
				? NormalizePrefix(departmentDispatchPrefix)
				: "G";

			return $"{prefix}:{normalizedCode}";
		}

		private static string NormalizePrefix(string prefix)
		{
			if (String.IsNullOrWhiteSpace(prefix))
				return "G";

			return prefix.Trim().TrimEnd(':').ToUpperInvariant();
		}
	}
}
