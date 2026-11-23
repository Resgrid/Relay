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
	/// Result of looking up a department by its dispatch email code, the department's InternalDispatchEmail setting
	/// (GET /api/v4/Departments/GetDepartmentByDispatchCode). A token caller only ever resolves its own department (Resgrid
	/// answers 401 for another one); the system API key resolves any department.
	/// </summary>
	public sealed class DepartmentLookupResult
	{
		[JsonPropertyName("DepartmentId")]
		public string DepartmentId { get; set; }

		[JsonPropertyName("Name")]
		public string Name { get; set; }

		[JsonPropertyName("Code")]
		public string Code { get; set; }
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

		/// <summary>
		/// Not returned by Resgrid's GroupResultData: the group's department is only known from the lookup's departmentId
		/// argument (which Resgrid validates in system-key mode).
		/// </summary>
		[JsonPropertyName("DepartmentId")]
		public string DepartmentId { get; set; }

		/// <summary>Station or organizational group type id.</summary>
		[JsonPropertyName("TypeId")]
		public string TypeId { get; set; }
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
