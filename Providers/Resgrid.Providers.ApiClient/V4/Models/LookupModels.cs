using System.Text.Json.Serialization;

namespace Resgrid.Providers.ApiClient.V4.Models
{
	/// <summary>
	/// Generic API response wrapper from the Resgrid v4 API.
	/// </summary>
	public sealed class LookupResponse<T>
	{
		[JsonPropertyName("Data")]
		public T Data { get; set; }

		[JsonPropertyName("Status")]
		public string Status { get; set; }
	}

	/// <summary>
	/// Result of looking up a group by its dispatch email code
	/// (GET /api/v4/Groups/GetGroupByDispatchCode).
	/// </summary>
	public sealed class GroupLookupResult
	{
		/// <summary>
		/// The numeric group ID used in DispatchList (G:{GroupId}).
		/// </summary>
		[JsonPropertyName("GroupId")]
		public string GroupId { get; set; }

		[JsonPropertyName("Name")]
		public string Name { get; set; }

		[JsonPropertyName("DepartmentId")]
		public string DepartmentId { get; set; }

		[JsonPropertyName("Type")]
		public int Type { get; set; }
	}

	/// <summary>
	/// Result of looking up a unit by its name
	/// (GET /api/v4/Units/GetUnitByName).
	/// </summary>
	public sealed class UnitLookupResult
	{
		/// <summary>
		/// The numeric unit ID used in DispatchList (U:{UnitId}).
		/// </summary>
		[JsonPropertyName("UnitId")]
		public string UnitId { get; set; }

		[JsonPropertyName("Name")]
		public string Name { get; set; }

		[JsonPropertyName("Type")]
		public string Type { get; set; }

		[JsonPropertyName("DepartmentId")]
		public string DepartmentId { get; set; }
	}

	/// <summary>
	/// Result of looking up a role by its name
	/// (GET /api/v4/Roles/GetRoleByName).
	/// </summary>
	public sealed class RoleLookupResult
	{
		/// <summary>
		/// The numeric role ID used in DispatchList (R:{RoleId}).
		/// </summary>
		[JsonPropertyName("RoleId")]
		public string RoleId { get; set; }

		[JsonPropertyName("Name")]
		public string Name { get; set; }

		[JsonPropertyName("DepartmentId")]
		public string DepartmentId { get; set; }
	}
}
