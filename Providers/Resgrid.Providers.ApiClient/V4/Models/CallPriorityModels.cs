using System.Collections.Generic;

namespace Resgrid.Providers.ApiClient.V4.Models
{
	/// <summary>Response from GET /api/v4/CallPriorities/GetAllCallPriorites.</summary>
	public sealed class CallPrioritiesResult
	{
		public List<CallPriorityResultData> Data { get; set; } = new List<CallPriorityResultData>();
		public string Status { get; set; }
	}

	public sealed class CallPriorityResultData
	{
		/// <summary>The value a call's Priority carries (0-3 for the defaults, the DepartmentCallPriorityId for a custom one).</summary>
		public int Id { get; set; }
		public string Name { get; set; }
		public string Color { get; set; }
		public bool IsDeleted { get; set; }
		public bool IsDefault { get; set; }

		/// <summary>The department's alert tone for this priority.</summary>
		public int Tone { get; set; }
	}
}
