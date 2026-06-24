using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Resgrid.Providers.ApiClient.V4.Models
{
	/// <summary>
	/// Response wrapper for GET /api/v4/Calls/GetActiveCalls. Used by the dispatch
	/// tone-out mode to discover newly created calls that should be announced into a
	/// department's PTT channel. Reuses <see cref="CallResultData"/> for each call.
	/// </summary>
	public sealed class ActiveCallsResult
	{
		[JsonPropertyName("Data")]
		public List<CallResultData> Data { get; set; } = new List<CallResultData>();

		[JsonPropertyName("Status")]
		public string Status { get; set; }
	}
}
